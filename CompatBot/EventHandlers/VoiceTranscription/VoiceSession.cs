using System.Collections.Concurrent;
using System.IO;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Voice;
using DSharpPlus.Voice.Receivers;

namespace CompatBot.EventHandlers.VoiceTranscription;

internal sealed class VoiceSession
{
    private readonly DiscordClient client;
    private readonly DiscordGuild guild;
    private readonly DiscordChannel channel;
    private readonly VoiceConnection connection;
    private readonly DefaultAudioReceiver receiver;
    private readonly ConcurrentDictionary<ulong, UserAudioBuffer> users = new();
    private readonly ConcurrentDictionary<ulong, string> userNames = new();
    private readonly object linesGate = new();
    private readonly List<TranscriptFormatter.Line> lines = [];
    private readonly CancellationTokenSource cts = new();
    private readonly DateTime startedUtc = DateTime.UtcNow;
    private readonly Task seedTask;
    private readonly Task sweepTask;
    private int ended;

    public VoiceSession(DiscordClient client, DiscordGuild guild, DiscordChannel channel, VoiceConnection connection, DefaultAudioReceiver receiver)
    {
        this.client = client;
        this.guild = guild;
        this.channel = channel;
        this.connection = connection;
        this.receiver = receiver;

        receiver.UserJoined += OnUserJoinedAsync;
        receiver.UserLeft += OnUserLeftAsync;
        receiver.UserStoppedSpeaking += OnUserStoppedSpeakingAsync;

        seedTask = Task.Run(SeedExistingUsersAsync);
        sweepTask = Task.Run(SweepLoopAsync);
    }

    public ulong ChannelId => channel.Id;

    private async Task SeedExistingUsersAsync()
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                foreach (var userId in receiver.Receivers.Keys)
                    AddUser(userId);
                if (receiver.Receivers.Count > 0)
                    break;
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
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

        var name = guild.Members.TryGetValue(userId, out var member)
            ? (member.Nickname ?? member.Username)
            : userId.ToString();
        userNames[userId] = name;

        var buffer = new UserAudioBuffer(userReceiver.Reader, () => userReceiver.IsSpeaking);
        users[userId] = buffer;
        _ = Task.Run(() => buffer.RunAsync(cts.Token));
    }

    private Task OnUserJoinedAsync(ulong userId)
    {
        AddUser(userId);
        return Task.CompletedTask;
    }

    private Task OnUserLeftAsync(ulong userId)
    {
        userNames.TryRemove(userId, out _);
        if (users.TryRemove(userId, out var buffer))
        {
            var (samples, chunkStartUtc) = buffer.TakeAll();
            _ = Task.Run(() => TranscribeAndAppendAsync(userId, samples, chunkStartUtc));
        }
        return Task.CompletedTask;
    }

    private Task OnUserStoppedSpeakingAsync(ulong userId)
    {
        if (users.TryGetValue(userId, out var buffer))
        {
            _ = Task.Run(async () =>
            {
                if (buffer.TryTakePending(TimeSpan.FromSeconds(Config.VoiceTranscriptionMinFlushSeconds), out var samples, out var chunkStartUtc))
                    await TranscribeAndAppendAsync(userId, samples, chunkStartUtc).ConfigureAwait(false);
            });
        }
        return Task.CompletedTask;
    }

    private async Task SweepLoopAsync()
    {
        var interval = TimeSpan.FromSeconds(Config.VoiceTranscriptionSweepIntervalSeconds);
        var threshold = TimeSpan.FromSeconds(Config.VoiceTranscriptionSweepThresholdSeconds);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var (userId, buffer) in users)
            {
                if (buffer.TryTakePending(threshold, out var samples, out var chunkStartUtc))
                    _ = Task.Run(() => TranscribeAndAppendAsync(userId, samples, chunkStartUtc));
            }
        }
    }

    private async Task TranscribeAndAppendAsync(ulong userId, short[] samples, DateTime? chunkStartUtc)
    {
        if (samples.Length == 0)
            return;

        var segments = await WhisperTranscriber.TranscribeAsync(samples, Config.Cts.Token).ConfigureAwait(false);
        if (segments.Count == 0)
            return;

        var name = userNames.TryGetValue(userId, out var userName) ? userName : userId.ToString();
        lock (linesGate)
        {
            foreach (var segment in segments)
                lines.Add(new TranscriptFormatter.Line(
                    chunkStartUtc is null ? DateTime.UtcNow : chunkStartUtc.Value.Add(segment.Start),
                    name,
                    segment.Text));
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
                userNames.Values.Distinct().OrderBy(n => n).ToList(),
                lines,
                endedUtc is null);
    }

    public string GetAttachmentFilename()
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(channel.Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return $"{safeName}_{startedUtc:yyyyMMdd-HHmmss}.txt";
    }

    public async Task EndAsync()
    {
        if (Interlocked.Exchange(ref ended, 1) == 1)
            return;

        cts.Cancel();
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Config.Log.Warn(e, $"Failed to dispose voice connection in #{channel.Name}");
        }

        foreach (var (userId, buffer) in users)
        {
            var (samples, chunkStartUtc) = buffer.TakeAll();
            if (samples.Length > 0)
                await TranscribeAndAppendAsync(userId, samples, chunkStartUtc).ConfigureAwait(false);
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

        var endedUtc = DateTime.UtcNow;
        lock (linesGate)
        {
            if (lines.Count == 0)
            {
                Config.Log.Debug($"Voice transcription session in #{channel.Name} ended without any transcribed audio");
                return;
            }
        }

        try
        {
            var report = BuildTranscript(endedUtc);
            var summary = $"Voice transcription for #{channel.Name}: <t:{ToUnixTimeSeconds(startedUtc)}:F> → <t:{ToUnixTimeSeconds(endedUtc)}:F> ({TranscriptFormatter.FormatDuration(endedUtc - startedUtc)})";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(report));
            var message = new DiscordMessageBuilder().AddFile(GetAttachmentFilename(), stream).WithContent(summary);
            var logChannel = await client.GetChannelAsync(Config.BotLogId).ConfigureAwait(false);
            await message.SendAsync(logChannel).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Config.Log.Error(e, "Failed to post voice transcription report");
        }
    }

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static long ToUnixTimeSeconds(DateTime utc) => (long)(utc - UnixEpoch).TotalSeconds;
}
