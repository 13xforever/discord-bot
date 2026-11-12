using System.Collections.Concurrent;
using System.Diagnostics;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Exceptions;
using DSharpPlus.Voice;
using DSharpPlus.Voice.Receivers;

namespace CompatBot.EventHandlers.VoiceTranscription;

internal sealed class VoiceSessionMonitor
{
    private static volatile VoiceSessionMonitor? instance;
    private readonly ConcurrentDictionary<ulong, VoiceSession> sessions = new();
    private readonly ConcurrentDictionary<ulong, byte> scheduledChecks = new();
    private readonly ConcurrentDictionary<ulong, byte> busyChannels = new();
    private readonly ConcurrentDictionary<ulong, int> rejoinAttempts = new();

    public VoiceSessionMonitor() => instance = this;

    public DiscordClient? Client { get; set; }

    public static IReadOnlyCollection<VoiceSession> ActiveSessions => instance?.sessions.Values.ToArray() ?? [];

    private static string ChannelTag(string channelName) => $"voice[{channelName}]";

    /// <summary>
    /// Voice state changes only schedule a deferred check: connecting to a voice channel takes a
    /// while and the handler queue must not be stalled by it, see the readme in this folder.
    /// Deferring also coalesces the flapping of someone moving between channels.
    /// </summary>
    public Task OnVoiceStateUpdated(DiscordClient client, VoiceStateUpdatedEventArgs e)
    {
        Client = client;
        var inScope = Config.VoiceTranscriptionEnabled && e.GuildId is not null && e.GuildId.Value == Config.BotGuildId;
        var userId = e.Before?.UserId ?? e.After?.UserId;
        Config.Log.Trace($"voice: state update for user {userId}{(userId == client.CurrentUser?.Id ? " (that's us)" : "")}, guild {e.GuildId}, " +
                         $"channel {(e.Before?.ChannelId?.ToString() ?? "none")} → {(e.After?.ChannelId?.ToString() ?? "none")}, guild filter {(inScope ? "passed" : "rejected")}");
        if (!inScope)
            return Task.CompletedTask;

        if (e.Before?.ChannelId is { } beforeChannelId)
            ScheduleCheck(client, beforeChannelId);
        if (e.After?.ChannelId is { } afterChannelId && afterChannelId != e.Before?.ChannelId)
            ScheduleCheck(client, afterChannelId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Brings the sessions in sync after a (re)start: people that were already in a voice channel
    /// never trigger a voice state update for us to react to.
    /// </summary>
    public Task OnGuildAvailable(DiscordClient client, GuildAvailableEventArgs e)
    {
        Client = client;
        if (!Config.VoiceTranscriptionEnabled || e.Guild.Id != Config.BotGuildId)
            return Task.CompletedTask;

        var threshold = Config.VoiceTranscriptionJoinThreshold;
        var scanned = 0;
        var qualifying = 0;
        foreach (var channelId in sessions.Keys.ToArray())
            ScheduleCheck(client, channelId);

        foreach (var channel in e.Guild.Channels.Values)
        {
            if (channel.Type is not DiscordChannelType.Voice and not DiscordChannelType.Stage)
                continue;

            scanned++;
            if (!VoiceChannelPolicy.ShouldJoin(CountHumansInChannel(e.Guild, channel.Id), threshold))
                continue;

            qualifying++;
            ScheduleCheck(client, channel.Id);
        }

        Config.Log.Debug($"voice: reconciling {e.Guild.Name} after it became available, {scanned} voice channel(s) scanned, {qualifying} meet the threshold of {threshold}, {sessions.Count} session(s) active");
        return Task.CompletedTask;
    }

    public async Task OnClientStopped(DiscordClient client, ClientStoppedEventArgs _)
    {
        var pending = sessions.Keys.ToArray();
        if (pending.Length == 0)
            return;

        Config.Log.Info($"voice: the bot disconnected, ending {pending.Length} transcription session(s)");
        foreach (var channelId in pending)
            await EndSessionAsync(channelId, "the bot disconnected").ConfigureAwait(false);
    }

    private void ScheduleCheck(DiscordClient client, ulong channelId)
        => ScheduleCheck(client, channelId, TimeSpan.FromSeconds(Config.VoiceTranscriptionDecisionDelaySeconds));

    private void ScheduleCheck(DiscordClient client, ulong channelId, TimeSpan delay)
    {
        if (!scheduledChecks.TryAdd(channelId, 0))
        {
            Config.Log.Trace($"voice[{channelId}]: a check is already pending, skipping");
            return;
        }

        Config.Log.Trace($"voice[{channelId}]: checking the channel in {delay.TotalSeconds:F0}s");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                await CheckChannelAsync(client, channelId).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Config.Log.Warn(e, $"voice[{channelId}]: failed to decide what to do about this channel");
            }
            finally
            {
                scheduledChecks.TryRemove(channelId, out _);
            }
        });
    }

    private async Task CheckChannelAsync(DiscordClient client, ulong channelId)
    {
        var guild = await client.GetGuildAsync(Config.BotGuildId, false).ConfigureAwait(false);
        if (guild is null)
        {
            Config.Log.Debug($"voice[{channelId}]: the bot guild is gone, nothing to decide");
            return;
        }

        DiscordChannel? channel;
        try
        {
            channel = await client.GetChannelAsync(channelId).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            channel = null;
        }

        if (channel is null)
        {
            Config.Log.Debug($"voice[{channelId}]: the channel is gone, ending anything that was running in it");
            await EndSessionAsync(channelId, "the voice channel is gone").ConfigureAwait(false);
            return;
        }

        var tag = ChannelTag(channel.Name);
        if (channel.Type is not DiscordChannelType.Voice and not DiscordChannelType.Stage)
        {
            Config.Log.Trace($"{tag}: not a voice channel (type {channel.Type}), nothing to do");
            return;
        }

        var humans = CountHumansInChannel(guild, channelId);
        var states = guild.VoiceStates.Values.Count(state => state.ChannelId == channelId);
        var threshold = Config.VoiceTranscriptionJoinThreshold;
        if (VoiceChannelPolicy.ShouldJoin(humans, threshold))
        {
            Config.Log.Debug($"{tag}: {humans} human(s) of {states} voice state(s) in the channel, threshold is {threshold} → joining");
            await JoinChannelAsync(client, guild, channel, humans).ConfigureAwait(false);
            return;
        }

        if (!sessions.ContainsKey(channelId))
        {
            Config.Log.Trace($"{tag}: {humans} human(s) of {states} voice state(s), below the threshold of {threshold} and no session to end");
            return;
        }

        Config.Log.Debug($"{tag}: {humans} human(s) of {states} voice state(s), below the threshold of {threshold} → leaving");
        await EndSessionAsync(channelId, $"{humans} human(s) in the channel, below the threshold of {threshold}").ConfigureAwait(false);
    }

    private static int CountHumansInChannel(DiscordGuild guild, ulong channelId)
        => VoiceChannelPolicy.CountHumans(
            guild.VoiceStates.Values.Select(state => (state.UserId, state.ChannelId)),
            userId => guild.Members.TryGetValue(userId, out var member) && member.IsBot,
            channelId);

    private async Task JoinChannelAsync(DiscordClient client, DiscordGuild guild, DiscordChannel channel, int humans)
    {
        var tag = ChannelTag(channel.Name);
        if (sessions.ContainsKey(channel.Id))
        {
            Config.Log.Trace($"{tag}: already transcribing this channel");
            return;
        }

        if (!busyChannels.TryAdd(channel.Id, 0))
        {
            Config.Log.Debug($"{tag}: a join or a leave is still in flight for this channel, skipping");
            return;
        }

        try
        {
            if (sessions.Count >= Config.VoiceTranscriptionMaxSessions)
            {
                Config.Log.Debug($"{tag}: not joining, {sessions.Count} channel(s) are already being transcribed and the limit is {Config.VoiceTranscriptionMaxSessions}");
                return;
            }

            var connect = Stopwatch.StartNew();
            var connection = await channel.ConnectAsync(typeof(DefaultAudioReceiver), new VoiceConnectionOptions()).ConfigureAwait(false);
            connect.Stop();
            if (connection.Receiver is not DefaultAudioReceiver receiver)
                throw new InvalidOperationException($"Unexpected voice receiver type: {connection.Receiver?.GetType().Name ?? "null"}");

            // the library reconnects on its own, so a dropped connection is only fatal when the
            // reason says so or when no audio comes through afterwards
            connection.SetDisconnectHandler((reason, _) => OnVoiceDisconnectedAsync(channel.Id, channel.Name, reason), this);

            sessions[channel.Id] = new VoiceSession(client, guild, channel, connection, receiver);
            Config.Log.Info($"{tag}: joined for voice transcription, {humans} human(s) in the channel, connected in {connect.ElapsedMilliseconds}ms, " +
                            $"receiver={receiver.GetHashCode():X} in {receiver.DefaultReceiveMode} mode with {receiver.Receivers.Count} participant(s), bot is {client.CurrentUser?.Id}");
        }
        catch (Exception e)
        {
            Config.Log.Error(e, $"{tag}: failed to join for transcription");
        }
        finally
        {
            busyChannels.TryRemove(channel.Id, out _);
        }
    }

    private async Task EndSessionAsync(ulong channelId, string reason)
    {
        if (!sessions.TryGetValue(channelId, out var existing))
        {
            Config.Log.Trace($"voice[{channelId}]: no session to end");
            return;
        }

        var tag = ChannelTag(existing.ChannelName);
        if (!busyChannels.TryAdd(channelId, 0))
        {
            Config.Log.Debug($"{tag}: can't leave yet, a join for this channel is still in flight");
            return;
        }

        try
        {
            if (!sessions.TryRemove(channelId, out var session))
                return;

            // a session that heard audio means the pipeline works, so the next drop backs off from scratch
            if (session.AudioFrameSnapshot() > 0)
                rejoinAttempts.TryRemove(channelId, out _);

            Config.Log.Info($"{tag}: leaving, {reason}");
            await session.EndAsync().ConfigureAwait(false);
        }
        finally
        {
            busyChannels.TryRemove(channelId, out _);
        }
    }

    private async Task OnVoiceDisconnectedAsync(ulong channelId, string channelName, VoiceDisconnectReason reason)
    {
        // we dropped the connection ourselves while ending the session
        if (!sessions.TryGetValue(channelId, out var session))
        {
            Config.Log.Trace($"{ChannelTag(channelName)}: disconnect handler fired for {reason} but there's no session, we closed it ourselves");
            return;
        }

        var tag = ChannelTag(channelName);
        Config.Log.Warn($"{tag}: the voice connection dropped ({reason})");

        // the library resumes a dropped connection on its own, so the session is only lost when that
        // resume stays silent or when the reason says the connection is gone for good
        if (MayResumeOnItsOwn(reason) && await WaitForAudioToResumeAsync(session, channelId, tag).ConfigureAwait(false))
            return;

        await EndSessionAsync(channelId, $"voice connection dropped ({reason})").ConfigureAwait(false);

        // people may still be in there, and the next session starts a fresh transcript
        if (!ShouldRetry(reason) || Client is not { } client)
        {
            Config.Log.Debug($"{tag}: not rejoining after {reason}");
            return;
        }

        var attempt = rejoinAttempts.AddOrUpdate(channelId, 1, (_, previous) => previous + 1);
        var backoff = RejoinBackoff(attempt);
        Config.Log.Debug($"{tag}: rejoining after {reason}, attempt {attempt} in {backoff.TotalSeconds:F0}s");
        ScheduleCheck(client, channelId, backoff);
    }

    /// <summary>
    /// Watching for audio to come back only makes sense while the library may still resume the
    /// connection itself: a connection it tore down for being a library problem, or one Discord ended,
    /// is not coming back, and waiting for it just loses conversation.
    /// </summary>
    private static bool MayResumeOnItsOwn(VoiceDisconnectReason reason)
        => !IsFatal(reason) && reason is not VoiceDisconnectReason.LibraryIssue;

    private async Task<bool> WaitForAudioToResumeAsync(VoiceSession session, ulong channelId, string tag)
    {
        var watch = TimeSpan.FromSeconds(Config.VoiceTranscriptionResumeWatchSeconds);
        var before = session.AudioFrameSnapshot();
        Config.Log.Trace($"{tag}: watching {watch.TotalSeconds:F0}s for the library's own resume to bring audio back (at {before} frame(s))");

        for (var elapsed = 0.0; elapsed < watch.TotalSeconds; elapsed += 1)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            var after = session.AudioFrameSnapshot();
            if (after == before)
                continue;

            if (sessions.TryGetValue(channelId, out var current) && ReferenceEquals(current, session))
            {
                Config.Log.Info($"{tag}: the connection recovered, {after - before} frame(s) came through and the transcript continues");
                return true;
            }

            return false;
        }

        Config.Log.Debug($"{tag}: no audio came back within {watch.TotalSeconds:F0}s");
        return false;
    }

    /// <summary>
    /// Rejoining starts straight away and backs off geometrically up to the configured cap, so a first
    /// drop costs about a second of conversation while a channel that keeps failing can't turn into a
    /// join storm.
    /// </summary>
    private static TimeSpan RejoinBackoff(int attempt)
        => TimeSpan.FromSeconds(Math.Min(Config.VoiceTranscriptionReconnectGraceSeconds, 1 << Math.Min(attempt - 1, 16)));

    private static bool IsFatal(VoiceDisconnectReason reason)
        => reason is VoiceDisconnectReason.Kicked
            or VoiceDisconnectReason.CallTerminated
            or VoiceDisconnectReason.SessionInvalid
            or VoiceDisconnectReason.Ratelimited
            or VoiceDisconnectReason.VoiceEndpointUnavailable;

    /// <summary>
    /// Being kicked out of a call, ending the call or getting rate limited all mean that barging
    /// straight back in is not going to end well.
    /// </summary>
    private static bool ShouldRetry(VoiceDisconnectReason reason)
        => reason is not (VoiceDisconnectReason.Kicked or VoiceDisconnectReason.CallTerminated or VoiceDisconnectReason.Ratelimited);
}
