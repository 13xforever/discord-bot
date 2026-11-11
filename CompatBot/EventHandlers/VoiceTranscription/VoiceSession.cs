using System.Collections.Concurrent;
using System.IO;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Voice;
using DSharpPlus.Voice.Receivers;

namespace CompatBot.EventHandlers.VoiceTranscription;

internal sealed class VoiceSession
{
    /// <summary>
    /// How long the session waits for transcription jobs that are already running before posting
    /// the report. Audio that hasn't made it through whisper by then is still reported as is.
    /// </summary>
    private static readonly TimeSpan JobDrainTimeout = TimeSpan.FromSeconds(15);

    private readonly DiscordClient client;
    private readonly DiscordGuild guild;
    private readonly DiscordChannel channel;
    private readonly VoiceConnection connection;
    private readonly DefaultAudioReceiver receiver;
    private readonly ConcurrentDictionary<ulong, UserAudioBuffer> users = new();
    private readonly ConcurrentDictionary<ulong, string> userNames = new();
    private readonly ConcurrentDictionary<ulong, long> departedFrameCount = new();
    private readonly Lock linesGate = new();
    private readonly List<TranscriptFormatter.Line> lines = [];
    private readonly Lock flushGate = new();
    private readonly List<Task> flushTasks = [];
    private readonly CancellationTokenSource captureCts = new();
    private readonly CancellationTokenSource jobCts = CancellationTokenSource.CreateLinkedTokenSource(Config.Cts.Token);
    private readonly DateTime startedUtc = DateTime.UtcNow;
    private readonly Task seedTask;
    private readonly Task sweepTask;
    private int pendingJobs;
    private int ended;
    private int truncated;
    private string? language;

    public VoiceSession(DiscordClient client, DiscordGuild guild, DiscordChannel channel, VoiceConnection connection, DefaultAudioReceiver receiver)
    {
        this.client = client;
        this.guild = guild;
        this.channel = channel;
        this.connection = connection;
        this.receiver = receiver;

        receiver.UserJoined += OnUserJoinedAsync;
        receiver.UserLeft += OnUserLeftAsync;
        receiver.UserStartedSpeaking += OnUserStartedSpeakingAsync;
        receiver.UserStoppedSpeaking += OnUserStoppedSpeakingAsync;

        seedTask = Task.Run(SeedExistingUsersAsync);
        sweepTask = Task.Run(SweepLoopAsync);
    }

    public ulong ChannelId => channel.Id;

    public string ChannelName => channel.Name;

    /// <summary>
    /// Total 48 kHz audio frames delivered for everyone we're currently listening to, including the
    /// participants that already left. A value that stops growing means the connection went silent.
    /// </summary>
    public long AudioFrameSnapshot()
    {
        var total = 0L;
        foreach (var buffer in users.Values)
            total += buffer.ReceivedFrameCount;
        foreach (var count in departedFrameCount.Values)
            total += count;
        return total;
    }

    private async Task SeedExistingUsersAsync()
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!captureCts.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                foreach (var userId in receiver.Receivers.Keys)
                    AddUser(userId);
                if (receiver.Receivers.Count > 0)
                    break;
                await Task.Delay(250, captureCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // session ended during seeding
        }
    }

    private void AddUser(ulong userId)
    {
        if (users.ContainsKey(userId) || !receiver.Receivers.TryGetValue(userId, out var userReceiver))
            return;

        // names are kept for the lifetime of the session, they end up in the participant list
        if (!userNames.TryGetValue(userId, out _))
            userNames[userId] = guild.Members.TryGetValue(userId, out var member)
                ? (member.Nickname ?? member.Username)
                : userId.ToString();

        var buffer = new UserAudioBuffer(userReceiver.Reader, () => userReceiver.IsSpeaking);
        if (!users.TryAdd(userId, buffer))
            return;
        _ = Task.Run(() => buffer.RunAsync(captureCts.Token));
    }

    private Task OnUserJoinedAsync(ulong userId)
    {
        AddUser(userId);
        return Task.CompletedTask;
    }

    private Task OnUserLeftAsync(ulong userId)
    {
        if (!users.TryRemove(userId, out var buffer))
            return Task.CompletedTask;

        departedFrameCount[userId] = buffer.ReceivedFrameCount;
        var (samples, chunkStartUtc) = buffer.TakeAll();
        StartFlush(userId, samples, chunkStartUtc);
        return Task.CompletedTask;
    }

    private Task OnUserStartedSpeakingAsync(ulong userId)
    {
        AddUser(userId);
        return Task.CompletedTask;
    }

    private Task OnUserStoppedSpeakingAsync(ulong userId)
    {
        if (!users.TryGetValue(userId, out var buffer) || QueueIsFull(buffer))
            return Task.CompletedTask;

        if (buffer.TryTakePending(TimeSpan.FromSeconds(Config.VoiceTranscriptionMinFlushSeconds), out var samples, out var chunkStartUtc))
            StartFlush(userId, samples, chunkStartUtc);
        return Task.CompletedTask;
    }

    private async Task SweepLoopAsync()
    {
        var interval = TimeSpan.FromSeconds(Config.VoiceTranscriptionSweepIntervalSeconds);
        var threshold = TimeSpan.FromSeconds(Config.VoiceTranscriptionSweepThresholdSeconds);
        var lastStatsUtc = startedUtc;
        long lastStatsFrames = 0;

        while (!captureCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, captureCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // a reconnect can hand us fresh pipes and drop the events we'd normally rely on
            foreach (var userId in receiver.Receivers.Keys)
                AddUser(userId);

            foreach (var (userId, buffer) in users)
            {
                if (QueueIsFull(buffer))
                    continue;

                // audio that piled up past the pending cap is handed over whatever its age is
                var minAge = OverPendingCap(buffer) ? TimeSpan.Zero : threshold;
                if (buffer.TryTakePending(minAge, out var samples, out var chunkStartUtc))
                    StartFlush(userId, samples, chunkStartUtc);
            }

            if (Config.Log.IsDebugEnabled)
            {
                var elapsed = (DateTime.UtcNow - lastStatsUtc).TotalSeconds;
                var snapshot = AudioFrameSnapshot();
                var frames = Math.Max(0, snapshot - lastStatsFrames);
                lastStatsFrames = snapshot;
                lastStatsUtc = DateTime.UtcNow;
                if (frames > 0)
                    Config.Log.Debug(
                        $"#{channel.Name} voice audio: {frames / elapsed:F0} frames/s (a live connection pushes about {PcmResampler.SourceSampleRate} per participant), " +
                        $"{Volatile.Read(ref pendingJobs)}/{Config.VoiceTranscriptionMaxQueuedJobs} transcription job(s) active");
            }
        }
    }

    private static bool OverPendingCap(UserAudioBuffer buffer)
        => buffer.BufferedSampleCount >= Config.VoiceTranscriptionMaxPendingSeconds * PcmResampler.TargetSampleRate;

    /// <summary>
    /// Whisper processes one chunk at a time, so a session that keeps handing audio over while the
    /// queue is full only grows memory. Audio past the pending cap gets handed over anyway,
    /// otherwise a user talking into a saturated queue would be buffered forever.
    /// </summary>
    private bool QueueIsFull(UserAudioBuffer buffer)
        => Volatile.Read(ref pendingJobs) >= Config.VoiceTranscriptionMaxQueuedJobs && !OverPendingCap(buffer);

    private void StartFlush(ulong userId, short[] samples, DateTime? chunkStartUtc)
    {
        if (samples.Length == 0)
            return;

        Interlocked.Increment(ref pendingJobs);
        var task = Task.Run(async () =>
        {
            try
            {
                await TranscribeAndAppendAsync(userId, samples, chunkStartUtc).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Config.Log.Debug($"A voice transcription job for #{channel.Name} was cancelled");
            }
            catch (Exception e)
            {
                Config.Log.Warn(e, $"Failed to transcribe a voice chunk in #{channel.Name}");
            }
            finally
            {
                Interlocked.Decrement(ref pendingJobs);
            }
        });

        lock (flushGate)
            flushTasks.Add(task);
    }

    /// <summary>
    /// Runs whisper over one chunk and appends whatever it makes out of it. Cancellation and failures
    /// are left to the caller, and the queue counters to <see cref="StartFlush"/>.
    /// </summary>
    private async Task TranscribeAndAppendAsync(ulong userId, short[] samples, DateTime? chunkStartUtc)
    {
        if (Volatile.Read(ref language) is null)
        {
            var detected = await WhisperTranscriber.DetectLanguageAsync(samples, jobCts.Token).ConfigureAwait(false);
            if (detected is not null)
                Volatile.Write(ref language, detected);
        }

        var segments = await WhisperTranscriber.TranscribeAsync(samples, Volatile.Read(ref language), jobCts.Token).ConfigureAwait(false);
        if (segments.Count == 0)
            return;

        var name = userNames.TryGetValue(userId, out var userName) ? userName : userId.ToString();
        lock (linesGate)
        {
            var maxLines = Config.VoiceTranscriptionMaxLines;
            foreach (var segment in segments)
            {
                if (lines.Count >= maxLines)
                {
                    if (Interlocked.Exchange(ref truncated, 1) is 0)
                        Config.Log.Warn($"Voice transcript in #{channel.Name} hit the {maxLines} lines limit, the rest is dropped");
                    break;
                }

                lines.Add(new TranscriptFormatter.Line(
                    chunkStartUtc is null ? DateTime.UtcNow : chunkStartUtc.Value.Add(segment.Start),
                    name,
                    segment.Text));
            }
        }
    }

    public string BuildTranscript(DateTime? endedUtc)
    {
        lock (linesGate)
            return TranscriptFormatter.Format(
                channel.Name,
                guild.Name,
                startedUtc,
                endedUtc,
                userNames.Values.Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
                lines,
                endedUtc is null,
                Volatile.Read(ref truncated) is 1);
    }

    public string GetAttachmentFilename()
        => TranscriptFormatter.BuildAttachmentFilename(channel.Name, startedUtc);

    public async Task EndAsync()
    {
        if (Interlocked.Exchange(ref ended, 1) is 1)
            return;

        captureCts.Cancel();
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Config.Log.Warn(e, $"Failed to dispose the voice connection in #{channel.Name}");
        }

        // let the jobs that are already running finish, they produce the tail of the transcript
        await DrainPendingJobsAsync().ConfigureAwait(false);

        foreach (var (userId, buffer) in users)
        {
            var (samples, chunkStartUtc) = buffer.TakeAll();
            if (samples.Length == 0)
                continue;
            try
            {
                await TranscribeAndAppendAsync(userId, samples, chunkStartUtc).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Config.Log.Debug($"Skipping the last audio chunk of #{channel.Name}: the bot is going down");
                break;
            }
            catch (Exception e)
            {
                Config.Log.Warn(e, $"Failed to transcribe the last audio chunk in #{channel.Name}");
            }
        }
        users.Clear();

        try
        {
            await seedTask.ConfigureAwait(false);
            await sweepTask.ConfigureAwait(false);
        }
        catch
        {
            // shutdown races are expected
        }
        jobCts.Cancel();

        int lineCount;
        lock (linesGate)
            lineCount = lines.Count;
        if (lineCount == 0)
        {
            Config.Log.Debug($"Voice transcription in #{channel.Name} ended without any transcribed audio");
            return;
        }

        try
        {
            var endedUtc = DateTime.UtcNow;
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(BuildTranscript(endedUtc)));
            var message = new DiscordMessageBuilder()
                .AddFile(GetAttachmentFilename(), stream)
                .WithContent(TranscriptFormatter.BuildSummary(channel.Name, startedUtc, endedUtc))
                // channel names are user settable and the summary is plain message content
                .WithAllowedMentions(Mentions.None);
            var logChannel = await client.GetChannelAsync(Config.BotLogId).ConfigureAwait(false);
            await message.SendAsync(logChannel).ConfigureAwait(false);
            Config.Log.Info($"Posted the voice transcript for #{channel.Name}: {lineCount} line(s), {TranscriptFormatter.FormatDuration(endedUtc - startedUtc)}");
        }
        catch (Exception e)
        {
            Config.Log.Error(e, "Failed to post the voice transcription report");
        }
    }

    private async Task DrainPendingJobsAsync()
    {
        Task[] pending;
        lock (flushGate)
        {
            flushTasks.RemoveAll(task => task.IsCompleted);
            pending = [.. flushTasks];
        }
        if (pending.Length == 0)
            return;

        var all = Task.WhenAll(pending);
        if (await Task.WhenAny(all, Task.Delay(JobDrainTimeout)).ConfigureAwait(false) != all)
            Config.Log.Warn($"Voice transcription jobs in #{channel.Name} did not finish within {JobDrainTimeout.TotalSeconds:F0} seconds");
    }
}
