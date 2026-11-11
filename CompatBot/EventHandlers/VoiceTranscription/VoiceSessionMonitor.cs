using System.Collections.Concurrent;
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

    public VoiceSessionMonitor() => instance = this;

    public DiscordClient? Client { get; set; }

    public static IReadOnlyCollection<VoiceSession> ActiveSessions => instance?.sessions.Values.ToArray() ?? [];

    /// <summary>
    /// Voice state changes only schedule a deferred check: connecting to a voice channel takes a
    /// while and the handler queue must not be stalled by it, see the readme in this folder.
    /// Deferring also coalesces the flapping of someone moving between channels.
    /// </summary>
    public Task OnVoiceStateUpdated(DiscordClient client, VoiceStateUpdatedEventArgs e)
    {
        Client = client;
        if (!Config.VoiceTranscriptionEnabled || e.GuildId is null || e.GuildId.Value != Config.BotGuildId)
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

        Config.Log.Debug($"Reconciling voice transcription sessions in {e.Guild.Name}…");
        foreach (var channelId in sessions.Keys.ToArray())
            ScheduleCheck(client, channelId);

        var threshold = Config.VoiceTranscriptionJoinThreshold;
        foreach (var channel in e.Guild.Channels.Values)
        {
            if (channel.Type is not DiscordChannelType.Voice and not DiscordChannelType.Stage)
                continue;
            if (VoiceChannelPolicy.ShouldJoin(CountHumansInChannel(e.Guild, channel.Id), threshold))
                ScheduleCheck(client, channel.Id);
        }
        return Task.CompletedTask;
    }

    public async Task OnClientStopped(DiscordClient client, ClientStoppedEventArgs _)
    {
        foreach (var channelId in sessions.Keys.ToArray())
            await EndSessionAsync(channelId, "the bot disconnected").ConfigureAwait(false);
    }

    private void ScheduleCheck(DiscordClient client, ulong channelId)
    {
        if (!scheduledChecks.TryAdd(channelId, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Config.VoiceTranscriptionDecisionDelaySeconds)).ConfigureAwait(false);
                await CheckChannelAsync(client, channelId).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Config.Log.Warn(e, $"Failed to decide what to do about voice channel {channelId}");
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
            return;

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
            await EndSessionAsync(channelId, "the voice channel is gone").ConfigureAwait(false);
            return;
        }

        if (channel.Type is not DiscordChannelType.Voice and not DiscordChannelType.Stage)
            return;

        var humans = CountHumansInChannel(guild, channelId);
        if (VoiceChannelPolicy.ShouldJoin(humans, Config.VoiceTranscriptionJoinThreshold))
            await JoinChannelAsync(client, guild, channel, humans).ConfigureAwait(false);
        else if (sessions.ContainsKey(channelId))
            await EndSessionAsync(channelId, $"{humans} human(s) in the channel, below the threshold of {Config.VoiceTranscriptionJoinThreshold}").ConfigureAwait(false);
    }

    private static int CountHumansInChannel(DiscordGuild guild, ulong channelId)
        => VoiceChannelPolicy.CountHumans(
            guild.VoiceStates.Values.Select(state => (state.UserId, state.ChannelId)),
            userId => guild.Members.TryGetValue(userId, out var member) && member.IsBot,
            channelId);

    private async Task JoinChannelAsync(DiscordClient client, DiscordGuild guild, DiscordChannel channel, int humans)
    {
        if (sessions.ContainsKey(channel.Id) || !busyChannels.TryAdd(channel.Id, 0))
            return;

        try
        {
            if (sessions.Count >= Config.VoiceTranscriptionMaxSessions)
            {
                Config.Log.Debug($"Not joining #{channel.Name} for transcription: {sessions.Count} channel(s) are already being transcribed, {Config.VoiceTranscriptionMaxSessions} at most");
                return;
            }

            var connection = await channel.ConnectAsync(typeof(DefaultAudioReceiver), new VoiceConnectionOptions()).ConfigureAwait(false);
            if (connection.Receiver is not DefaultAudioReceiver receiver)
                throw new InvalidOperationException($"Unexpected voice receiver type: {connection.Receiver?.GetType().Name ?? "null"}");

            // the library reconnects on its own, so a dropped connection is only fatal when the
            // reason says so or when no audio comes through afterwards
            connection.SetDisconnectHandler((reason, _) => OnVoiceDisconnectedAsync(channel.Id, channel.Name, reason), this);

            sessions[channel.Id] = new VoiceSession(client, guild, channel, connection, receiver);
            Config.Log.Info($"Joined #{channel.Name} for voice transcription ({humans} human(s) in the channel)");
        }
        catch (Exception e)
        {
            Config.Log.Error(e, $"Failed to join voice channel #{channel.Name} for transcription");
        }
        finally
        {
            busyChannels.TryRemove(channel.Id, out _);
        }
    }

    private async Task EndSessionAsync(ulong channelId, string reason)
    {
        if (!sessions.TryGetValue(channelId, out var existing) || !busyChannels.TryAdd(channelId, 0))
            return;

        try
        {
            if (!sessions.TryRemove(channelId, out var session))
                return;
            Config.Log.Info($"Leaving voice channel #{existing.ChannelName}: {reason}");
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
            return;

        Config.Log.Warn($"Voice connection in #{channelName} dropped ({reason})");

        if (!IsFatal(reason))
        {
            var frames = session.AudioFrameSnapshot();
            await Task.Delay(TimeSpan.FromSeconds(Config.VoiceTranscriptionReconnectGraceSeconds)).ConfigureAwait(false);
            if (sessions.TryGetValue(channelId, out var current) && ReferenceEquals(current, session) && current.AudioFrameSnapshot() != frames)
            {
                Config.Log.Info($"Voice connection in #{channelName} recovered, the transcript continues");
                return;
            }
        }

        await EndSessionAsync(channelId, $"voice connection dropped ({reason})").ConfigureAwait(false);
        // people may still be in there, and the next session starts a fresh transcript
        if (ShouldRetry(reason) && Client is { } client)
            ScheduleCheck(client, channelId);
    }

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
