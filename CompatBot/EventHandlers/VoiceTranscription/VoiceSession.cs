using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Voice;
using DSharpPlus.Voice.Metrics;
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
    private readonly ulong selfId;
    private readonly string tag;
    private readonly ConcurrentDictionary<ulong, UserAudioBuffer> users = new();
    private readonly ConcurrentDictionary<ulong, UserAudioReceiver> boundReceivers = new();
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
    private int jobSeq;
    private int ended;
    private int truncated;
    private int segmentsSeen;
    private int finalSamples;
    private int jobsOutstanding;
    private int loggedSelfSkip;
    private int boundParticipants;
    private long sweepIterations;
    private long lastSweepFrames;
    private DateTime lastSweepUtc;
    private string? language;

    public VoiceSession(DiscordClient client, DiscordGuild guild, DiscordChannel channel, VoiceConnection connection, DefaultAudioReceiver receiver)
    {
        this.client = client;
        this.guild = guild;
        this.channel = channel;
        this.connection = connection;
        this.receiver = receiver;
        selfId = client.CurrentUser?.Id ?? 0;
        tag = $"voice[{channel.Name}@{startedUtc:HHmmss}]";
        lastSweepUtc = startedUtc;

        receiver.UserJoined += OnUserJoinedAsync;
        receiver.UserLeft += OnUserLeftAsync;
        receiver.UserStartedSpeaking += OnUserStartedSpeakingAsync;
        receiver.UserStoppedSpeaking += OnUserStoppedSpeakingAsync;

        Config.Log.Debug($"{tag}: session settings, guild={guild.Id} channel={channel.Id}: joins at {Config.VoiceTranscriptionJoinThreshold} human(s) or more, " +
                         $"flushes from {Config.VoiceTranscriptionMinFlushSeconds}s of speech, sweeps every {Config.VoiceTranscriptionSweepIntervalSeconds}s taking anything older than {Config.VoiceTranscriptionSweepThresholdSeconds}s, " +
                         $"{Config.VoiceTranscriptionMaxQueuedJobs} job(s) in the queue at most and {Config.VoiceTranscriptionMaxPendingSeconds}s of audio buffered per participant, " +
                         $"{Config.VoiceTranscriptionMaxLines} transcript line(s) at most, watches {Config.VoiceTranscriptionResumeWatchSeconds}s for a dropped connection to resume before rejoining with backoff up to {Config.VoiceTranscriptionReconnectGraceSeconds}s, report goes to channel {Config.BotLogId}");
        Config.Log.Debug($"{tag}: whisper model {WhisperTranscriber.DescribeModel()}, voice channels are {PcmResampler.Channels} channel(s) at {PcmResampler.SourceSampleRate} Hz");
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
            var poll = 0;
            while (!captureCts.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                poll++;
                foreach (var userId in receiver.Receivers.Keys)
                    AddUser(userId);
                if (receiver.Receivers.Count > 0)
                {
                    Config.Log.Debug($"{tag}: found {receiver.Receivers.Count} receiver(s) on poll {poll}, listening to {users.Count} participant(s)");
                    return;
                }
                Config.Log.Trace($"{tag}: seed poll {poll}, the receiver has nobody in it yet");
                await Task.Delay(250, captureCts.Token).ConfigureAwait(false);
            }

            if (!captureCts.IsCancellationRequested)
                Config.Log.Debug($"{tag}: no receivers appeared within 10 seconds, the sweep will keep looking");
        }
        catch (OperationCanceledException)
        {
            Config.Log.Debug($"{tag}: session ended while waiting for the first participants");
        }
    }

    private bool AddUser(ulong userId)
    {
        if (users.ContainsKey(userId))
            return false;

        // the receiver lists us among its participants; broadcasting to ourselves and running
        // whisper over our own output is pointless, and it pollutes the participant list
        if (userId == selfId)
        {
            if (Interlocked.Exchange(ref loggedSelfSkip, 1) is 0)
                Config.Log.Debug($"{tag}: skipping our own account ({userId}), we're the one doing the listening");
            return false;
        }

        if (!receiver.Receivers.TryGetValue(userId, out var userReceiver))
        {
            Config.Log.Trace($"{tag}/u{userId}: an event came in but the receiver has no entry for this user yet, waiting for the sweep");
            return false;
        }

        // names are kept for the lifetime of the session, they end up in the participant list
        var resolvedName = guild.Members.TryGetValue(userId, out var member)
            ? (member.Nickname ?? member.Username)
            : userId.ToString();
        var cachedMember = member is not null;
        if (!userNames.TryGetValue(userId, out _))
            userNames[userId] = resolvedName;

        var userTag = $"{tag}/u{userId}";
        var buffer = new UserAudioBuffer(userReceiver.Reader, () => userReceiver.IsSpeaking, userTag);
        if (!users.TryAdd(userId, buffer))
            return false;

        boundReceivers[userId] = userReceiver;
        Config.Log.Debug($"{tag}/u{userId}: listening, stream={userReceiver.GetHashCode():X}, name=\"{resolvedName}\" (member {(cachedMember ? "cached" : "not in the member cache")}), speaking={userReceiver.IsSpeaking}");
        _ = Task.Run(() => buffer.RunAsync(captureCts.Token));

        // the first participant is the earliest moment the library's own counters say anything useful:
        // from here on, anything the voice link received but never handed over is a hole in our pipeline
        if (Interlocked.Increment(ref boundParticipants) is 1)
            ReportVoiceLink("when the first participant started being listened to");
        return true;
    }

    /// <summary>
    /// Catches up with whatever the receiver knows about right now, and restarts any buffer whose
    /// <see cref="UserAudioReceiver"/> got swapped underneath us: the library replaces those when it
    /// rebuilds a stream, and a buffer bound to the old instance would read a dead pipe forever.
    /// </summary>
    private void RefreshReceivers()
    {
        var subscribed = 0;
        foreach (var userId in receiver.Receivers.Keys)
        {
            if (!users.TryGetValue(userId, out var buffer))
            {
                if (AddUser(userId))
                    subscribed++;
                continue;
            }

            if (!receiver.Receivers.TryGetValue(userId, out var current))
                continue;

            var bound = boundReceivers.TryGetValue(userId, out var previous) ? previous : null;
            if (ReferenceEquals(bound, current))
                continue;

            Config.Log.Info($"{tag}/u{userId}: its voice receiver was replaced ({bound?.GetHashCode():X} → {current.GetHashCode():X}), restarting the buffer");
            var (samples, chunkStartUtc) = buffer.TakeAll(drainResampler: true);
            StartFlush(userId, samples, chunkStartUtc, "receiver-replaced");
            users.TryRemove(userId, out _);
            boundReceivers.TryRemove(userId, out _);
            departedFrameCount[userId] = buffer.ReceivedFrameCount;
            AddUser(userId);
        }

        if (subscribed > 0)
            Config.Log.Trace($"{tag}: picked up {subscribed} new participant(s), listening to {users.Count} of {receiver.Receivers.Count} receiver(s)");
    }

    private Task OnUserJoinedAsync(ulong userId)
    {
        Config.Log.Trace($"{tag}/u{userId}: joined the stream");
        AddUser(userId);
        return Task.CompletedTask;
    }

    private Task OnUserLeftAsync(ulong userId)
    {
        Config.Log.Trace($"{tag}/u{userId}: left the stream");
        if (!users.TryRemove(userId, out var buffer))
            return Task.CompletedTask;

        boundReceivers.TryRemove(userId, out _);
        departedFrameCount[userId] = buffer.ReceivedFrameCount;
        // this is the last we'll ever hear from that participant's stream, so flush the filter too
        var (samples, chunkStartUtc) = buffer.TakeAll(drainResampler: true);
        StartFlush(userId, samples, chunkStartUtc, "user-left");
        return Task.CompletedTask;
    }

    private Task OnUserStartedSpeakingAsync(ulong userId)
    {
        Config.Log.Trace($"{tag}/u{userId}: started speaking");
        AddUser(userId);
        return Task.CompletedTask;
    }

    private Task OnUserStoppedSpeakingAsync(ulong userId)
    {
        Config.Log.Trace($"{tag}/u{userId}: stopped speaking");
        if (!users.TryGetValue(userId, out var buffer))
        {
            Config.Log.Trace($"{tag}/u{userId}: we're not listening to it, nothing to flush");
            return Task.CompletedTask;
        }

        if (QueueIsFull(buffer, userId))
            return Task.CompletedTask;

        if (buffer.TryTakePending(TimeSpan.FromSeconds(Config.VoiceTranscriptionMinFlushSeconds), out var samples, out var chunkStartUtc))
            StartFlush(userId, samples, chunkStartUtc, "speech-end");
        return Task.CompletedTask;
    }

    private async Task SweepLoopAsync()
    {
        var interval = TimeSpan.FromSeconds(Config.VoiceTranscriptionSweepIntervalSeconds);
        var threshold = TimeSpan.FromSeconds(Config.VoiceTranscriptionSweepThresholdSeconds);

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

            // one unexpected error must not cost the rest of the session its flushing and re-seeding
            try
            {
                RefreshReceivers();

                foreach (var (userId, buffer) in users)
                {
                    if (QueueIsFull(buffer, userId))
                        continue;

                    // audio that piled up past the pending cap is handed over whatever its age is
                    var minAge = OverPendingCap(buffer) ? TimeSpan.Zero : threshold;
                    if (buffer.TryTakePending(minAge, out var samples, out var chunkStartUtc))
                        StartFlush(userId, samples, chunkStartUtc, "sweep");
                }

                ReportSweepStats();
            }
            catch (Exception e)
            {
                Config.Log.Warn(e, $"{tag}: the sweep hit an unexpected error, carrying on with the next one");
            }
        }
    }

    private void ReportSweepStats()
    {
        var frames = AudioFrameSnapshot();
        var lastInterval = frames - lastSweepFrames;
        var elapsed = (DateTime.UtcNow - lastSweepUtc).TotalSeconds;
        var buffered = 0;
        foreach (var buffer in users.Values)
            buffered += buffer.BufferedSampleCount;

        int lineCount;
        lock (linesGate)
            lineCount = lines.Count;

        var iteration = Interlocked.Increment(ref sweepIterations);
        Config.Log.Debug($"{tag}: sweep #{iteration}, {receiver.Receivers.Count} receiver(s) and {users.Count} participant(s), " +
                         $"{lastInterval} frame(s) in the last {elapsed:F0}s ({lastInterval / elapsed:F0}/s, expect about {PcmResampler.SourceSampleRate}/s per speaking participant), " +
                         $"{frames} frame(s) in total, {buffered} sample(s) buffered, {Volatile.Read(ref pendingJobs)}/{Config.VoiceTranscriptionMaxQueuedJobs} job(s) running, " +
                         $"{lineCount}/{Config.VoiceTranscriptionMaxLines} line(s) so far");
        ReportVoiceLink($"sweep #{iteration}");
        lastSweepFrames = frames;
        lastSweepUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// What the voice link itself saw, straight from the library's counters. These count whole RTP
    /// packets (about 50 per second per talker), unlike our own frame counters, so one of each is what
    /// tells a dead media path apart from a pipeline that simply never got handed anything.
    /// </summary>
    private VoiceMetricsCollection? ReportVoiceLink(string what)
    {
        VoiceMetricsCollection metrics;
        try
        {
            metrics = connection.GetVoiceMetrics();
        }
        catch (Exception e)
        {
            Config.Log.Debug(e, $"{tag}: couldn't read the voice link metrics {what}");
            return null;
        }

        Config.Log.Debug($"{tag}: voice link {what}: {metrics.AudioFramesReceived} audio packet(s) of {metrics.AudioBytesReceived} byte(s) received, " +
                         $"{metrics.AudioFramesFailedDecryption} failed decryption, {metrics.AudioFramesDropped} dropped, {metrics.EmptyAudioFramesReceived} empty, " +
                         $"{metrics.AudioFramesSent} sent, {metrics.GatewayPayloadsReceived} gateway payload(s) in / {metrics.GatewayPayloadsSent} out, " +
                         $"{metrics.ControlPacketsReceived}/{metrics.ControlPacketsSent} control and {metrics.KeepalivesReceived}/{metrics.KeepalivesSent} keepalive packet(s) in/out, " +
                         $"audio socket latency {connection.AudioConnectionLatency.TotalMilliseconds:F0}ms, " +
                         $"connection up {metrics.CurrentUserConnectionTime.TotalSeconds:F0}s of {metrics.TotalSessionTime.TotalSeconds:F0}s");
        return metrics;
    }

    /// <summary>
    /// Turns the two counters that matter into a sentence, so a session that produced nothing says where
    /// it stopped instead of making anyone subtract one log line from another.
    /// </summary>
    private void ExplainEmptyPipeline(VoiceMetricsCollection link)
    {
        var ours = AudioFrameSnapshot();
        if (link.AudioFramesReceived is 0)
        {
            Config.Log.Warn($"{tag}: no audio packet came over the voice link at all while {link.KeepalivesReceived} keepalive(s) and " +
                            $"{link.GatewayPayloadsReceived} gateway payload(s) did, so nothing reached the media path");
            return;
        }

        if (ours is 0)
        {
            Config.Log.Warn($"{tag}: the voice link took {link.AudioFramesReceived} audio packet(s) of {link.AudioBytesReceived} byte(s) but our pipes got no audio: " +
                            $"{link.AudioFramesFailedDecryption} packet(s) failed decryption and {link.AudioFramesDropped} were dropped before the receiver saw them, " +
                            $"so the remaining {link.AudioFramesReceived - link.AudioFramesFailedDecryption - link.AudioFramesDropped - link.EmptyAudioFramesReceived} were lost between the receiver and our buffers");
            return;
        }

        Config.Log.Trace($"{tag}: the voice link took {link.AudioFramesReceived} audio packet(s) and our pipes received {ours} frame(s) out of it");
    }

    private static bool OverPendingCap(UserAudioBuffer buffer)
        => buffer.BufferedSampleCount >= Config.VoiceTranscriptionMaxPendingSeconds * PcmResampler.TargetSampleRate;

    /// <summary>
    /// Whisper processes one chunk at a time, so a session that keeps handing audio over while the
    /// queue is full only grows memory. Audio past the pending cap gets handed over anyway,
    /// otherwise a user talking into a saturated queue would be buffered forever.
    /// </summary>
    private bool QueueIsFull(UserAudioBuffer buffer, ulong userId)
    {
        var queued = Volatile.Read(ref pendingJobs);
        if (queued < Config.VoiceTranscriptionMaxQueuedJobs)
            return false;

        var buffered = buffer.BufferedSampleCount;
        if (!OverPendingCap(buffer))
        {
            Config.Log.Debug($"{tag}/u{userId}: holding {buffered} sample(s) back, {queued}/{Config.VoiceTranscriptionMaxQueuedJobs} job(s) already running");
            return true;
        }

        Config.Log.Debug($"{tag}/u{userId}: forcing a flush, {buffered} sample(s) buffered is past the {Config.VoiceTranscriptionMaxPendingSeconds}s cap and {queued}/{Config.VoiceTranscriptionMaxQueuedJobs} job(s) are running");
        return false;
    }

    private static string AudioLength(int samples)
        => samples < PcmResampler.TargetSampleRate
            ? $"{samples * 1000.0 / PcmResampler.TargetSampleRate:F0}ms"
            : $"{samples / (double)PcmResampler.TargetSampleRate:F1}s";

    private void StartFlush(ulong userId, short[] samples, DateTime? chunkStartUtc, string trigger)
    {
        if (samples.Length == 0)
            return;

        var job = Interlocked.Increment(ref jobSeq);
        Config.Log.Debug($"{tag}/u{userId}: job #{job} from {trigger}, {samples.Length} sample(s) " +
                         $"({AudioLength(samples.Length)}, started {chunkStartUtc:yyyy-MM-dd HH:mm:ss}Z), {Volatile.Read(ref pendingJobs)} job(s) already queued");

        Interlocked.Increment(ref pendingJobs);
        var task = Task.Run(async () =>
        {
            try
            {
                await TranscribeAndAppendAsync(job, userId, samples, chunkStartUtc).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Config.Log.Debug($"{tag}/u{userId}: job #{job} was cancelled");
            }
            catch (Exception e)
            {
                Config.Log.Warn(e, $"{tag}/u{userId}: job #{job} from {trigger} failed");
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
    private async Task TranscribeAndAppendAsync(int job, ulong userId, short[] samples, DateTime? chunkStartUtc)
    {
        if (Volatile.Read(ref language) is null)
        {
            var detected = await WhisperTranscriber.DetectLanguageAsync(tag, samples, jobCts.Token).ConfigureAwait(false);
            if (detected is not null)
                Volatile.Write(ref language, detected);
        }

        var usedLanguage = Volatile.Read(ref language);
        var segments = await WhisperTranscriber.TranscribeAsync(tag, samples, usedLanguage, jobCts.Token).ConfigureAwait(false);
        Interlocked.Add(ref segmentsSeen, segments.Count);
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
                        Config.Log.Warn($"{tag}: the transcript hit the {maxLines} lines limit, the rest is dropped");
                    break;
                }

                var lineUtc = chunkStartUtc is null ? DateTime.UtcNow : chunkStartUtc.Value.Add(segment.Start);
                lines.Add(new TranscriptFormatter.Line(lineUtc, name, segment.Text));
                Config.Log.Trace($"{tag}/u{userId}: line {lines.Count}/{maxLines} at {lineUtc:HH:mm:ss.fff}Z, speaker \"{name}\": \"{segment.Text}\"");
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

        // the library's counters belong to the connection, so read them before it's gone
        var finalLink = ReportVoiceLink("at the end of the session");

        Config.Log.Debug($"{tag}: closing the voice connection");
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Config.Log.Warn(e, $"{tag}: failed to dispose the voice connection");
        }

        // let the jobs that are already running finish, they produce the tail of the transcript
        await DrainPendingJobsAsync().ConfigureAwait(false);

        foreach (var (userId, buffer) in users)
        {
            var (samples, chunkStartUtc) = buffer.TakeAll(drainResampler: true);
            if (samples.Length == 0)
                continue;

            finalSamples += samples.Length;
            Config.Log.Debug($"{tag}/u{userId}: final job for {samples.Length} sample(s) ({AudioLength(samples.Length)})");
            try
            {
                await TranscribeAndAppendAsync(Interlocked.Increment(ref jobSeq), userId, samples, chunkStartUtc).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Config.Log.Debug($"{tag}/u{userId}: the final job was skipped, the bot is going down");
                break;
            }
            catch (Exception e)
            {
                Config.Log.Warn(e, $"{tag}/u{userId}: the final job failed");
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

        var endedUtc = DateTime.UtcNow;
        Config.Log.Debug($"{tag}: session over after {TranscriptFormatter.FormatDuration(endedUtc - startedUtc)}, " +
                         $"{userNames.Count} participant(s), {AudioFrameSnapshot()} frame(s) received, {finalSamples} sample(s) in the final jobs, " +
                         $"{jobSeq} job(s) run and {jobsOutstanding} still outstanding, {segmentsSeen} segment(s) from whisper, " +
                         $"{lineCount}/{Config.VoiceTranscriptionMaxLines} line(s) kept{(Volatile.Read(ref truncated) is 1 ? ", truncated" : "")}");
        if (finalLink is not null)
            ExplainEmptyPipeline(finalLink);

        if (lineCount == 0)
        {
            Config.Log.Debug($"{tag}: nothing was transcribed, no report to post");
            return;
        }

        try
        {
            var report = BuildTranscript(endedUtc);
            var filename = GetAttachmentFilename();
            var summary = TranscriptFormatter.BuildSummary(channel.Name, startedUtc, endedUtc);
            Config.Log.Trace($"{tag}: report is \"{filename}\", {Encoding.UTF8.GetByteCount(report)} byte(s), content \"{summary}\"");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(report));
            var message = new DiscordMessageBuilder()
                .AddFile(filename, stream)
                .WithContent(summary)
                // channel names are user settable and the summary is plain message content
                .WithAllowedMentions(Mentions.None);

            var logLookup = Stopwatch.StartNew();
            var logChannel = await client.GetChannelAsync(Config.BotLogId).ConfigureAwait(false);
            logLookup.Stop();
            var send = Stopwatch.StartNew();
            var posted = await message.SendAsync(logChannel).ConfigureAwait(false);
            send.Stop();
            Config.Log.Info($"{tag}: posted the transcript for #{channel.Name}, {lineCount} line(s) in {TranscriptFormatter.FormatDuration(endedUtc - startedUtc)}, message {posted.Id} " +
                            $"(channel lookup {logLookup.ElapsedMilliseconds}ms, upload {send.ElapsedMilliseconds}ms)");
        }
        catch (Exception e)
        {
            Config.Log.Error(e, $"{tag}: failed to post the transcription report");
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

        Config.Log.Debug($"{tag}: waiting up to {JobDrainTimeout.TotalSeconds:F0}s for {pending.Length} job(s) still running");
        var all = Task.WhenAll(pending);
        if (await Task.WhenAny(all, Task.Delay(JobDrainTimeout)).ConfigureAwait(false) == all)
            return;

        jobsOutstanding = pending.Length;
        Config.Log.Warn($"{tag}: {pending.Length} job(s) did not finish within {JobDrainTimeout.TotalSeconds:F0}s, reporting what we have");
    }
}
