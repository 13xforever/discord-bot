namespace CompatBot.EventHandlers.VoiceTranscription;

/// <summary>
/// Decision logic for which voice channels the bot should be transcribing, kept free of Discord
/// plumbing so it can be reasoned about (and tested) on its own.
/// </summary>
internal static class VoiceChannelPolicy
{
    /// <summary>
    /// Counts the non-bot humans that are connected to <paramref name="channelId"/>.
    /// A user we have no member record for counts as a human; the bot itself always has one.
    /// </summary>
    public static int CountHumans(
        IEnumerable<(ulong UserId, ulong? ChannelId)> voiceStates,
        Func<ulong, bool> isBot,
        ulong channelId)
    {
        var humans = 0;
        foreach (var (userId, stateChannelId) in voiceStates)
        {
            if (stateChannelId != channelId || isBot(userId))
                continue;
            humans++;
        }
        return humans;
    }

    /// <summary>
    /// A zero or negative threshold would mean joining channels nobody is in, so it's treated as
    /// the feature being switched off instead.
    /// </summary>
    public static bool ShouldJoin(int humans, int threshold)
        => threshold > 0 && humans >= threshold;
}
