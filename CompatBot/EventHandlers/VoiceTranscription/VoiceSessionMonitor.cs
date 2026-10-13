using System.Collections.Concurrent;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Voice;
using DSharpPlus.Voice.Receivers;

namespace CompatBot.EventHandlers.VoiceTranscription;

internal sealed class VoiceSessionMonitor
{
    private static volatile VoiceSessionMonitor? instance;
    private readonly ConcurrentDictionary<ulong, VoiceSession> sessions = new();
    private readonly ConcurrentDictionary<ulong, byte> joiningChannels = new();

    public VoiceSessionMonitor() => instance = this;

    public static IReadOnlyCollection<VoiceSession> ActiveSessions => instance?.sessions.Values.ToArray() ?? [];

    public async Task OnVoiceStateUpdated(DiscordClient client, VoiceStateUpdatedEventArgs e)
    {
        if (!Config.VoiceTranscriptionEnabled || e.GuildId is null)
            return;

        var guild = await e.GetGuildAsync(false).ConfigureAwait(false);
        if (guild is null || guild.Id != Config.BotGuildId)
            return;

        var affectedChannels = new List<ulong>();
        if (e.Before?.ChannelId is { } beforeChannelId)
            affectedChannels.Add(beforeChannelId);
        if (e.After?.ChannelId is { } afterChannelId && afterChannelId != e.Before?.ChannelId)
            affectedChannels.Add(afterChannelId);

        foreach (var channelId in affectedChannels.Distinct())
            await CheckChannelAsync(client, guild, channelId).ConfigureAwait(false);
    }

    private async Task CheckChannelAsync(DiscordClient client, DiscordGuild guild, ulong channelId)
    {
        var channel = await client.GetChannelAsync(channelId).ConfigureAwait(false);
        if (channel is null)
            return;

        var humans = CountHumansInChannel(guild, channelId);
        if (humans >= Config.VoiceTranscriptionJoinThreshold)
            await JoinChannelAsync(client, guild, channel).ConfigureAwait(false);
        else if (sessions.TryRemove(channelId, out var session))
            _ = Task.Run(() => session.EndAsync());
    }

    private static int CountHumansInChannel(DiscordGuild guild, ulong channelId)
    {
        var count = 0;
        foreach (var state in guild.VoiceStates.Values)
        {
            if (state.ChannelId != channelId)
                continue;
            var isBot = guild.Members.TryGetValue(state.UserId, out var member) && member.IsBot;
            if (!isBot)
                count++;
        }
        return count;
    }

    private async Task JoinChannelAsync(DiscordClient client, DiscordGuild guild, DiscordChannel channel)
    {
        if (sessions.ContainsKey(channel.Id) || !joiningChannels.TryAdd(channel.Id, 0))
            return;

        try
        {
            var connection = await channel.ConnectAsync(typeof(DefaultAudioReceiver), new VoiceConnectionOptions()).ConfigureAwait(false);
            if (connection.Receiver is not DefaultAudioReceiver receiver)
                throw new InvalidOperationException($"Unexpected voice receiver type: {connection.Receiver?.GetType().Name ?? "null"}");

            sessions[channel.Id] = new VoiceSession(client, guild, channel, connection, receiver);
            Config.Log.Info($"Joined #{channel.Name} for voice transcription");
        }
        catch (Exception e)
        {
            Config.Log.Error(e, $"Failed to join voice channel #{channel.Name} for transcription");
        }
        finally
        {
            joiningChannels.TryRemove(channel.Id, out _);
        }
    }
}
