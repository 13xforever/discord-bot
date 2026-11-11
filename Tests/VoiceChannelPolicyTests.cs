using System;
using System.Collections.Generic;
using CompatBot.EventHandlers.VoiceTranscription;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class VoiceChannelPolicyTests
{
    private const ulong Channel = 100;
    private const ulong OtherChannel = 200;

    private static readonly IReadOnlySet<ulong> EmptyBotIds = new HashSet<ulong>();
    private static readonly Func<ulong, bool> NobodyIsBot = _ => false;

    [Test]
    public void CountsHumansInTheRequestedChannelOnly()
    {
        var states = new List<(ulong UserId, ulong? ChannelId)>
        {
            (1, Channel),
            (2, Channel),
            (3, OtherChannel),
            (4, null), // disconnected
        };

        Assert.That(VoiceChannelPolicy.CountHumans(states, NobodyIsBot, Channel), Is.EqualTo(2));
    }

    [Test]
    public void SkipsBots()
    {
        var states = new List<(ulong UserId, ulong? ChannelId)>
        {
            (1, Channel),
            (2, Channel),
            (3, Channel),
        };
        var bots = new HashSet<ulong> { 2 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(VoiceChannelPolicy.CountHumans(states, bots.Contains, Channel), Is.EqualTo(2));
            // the bot itself always has a member record, so an unknown user counts as a human
            Assert.That(VoiceChannelPolicy.CountHumans(states, NobodyIsBot, Channel), Is.EqualTo(3));
        }
    }

    [Test]
    public void JoinsAtTheThresholdAndAbove()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(VoiceChannelPolicy.ShouldJoin(0, 2), Is.False);
            Assert.That(VoiceChannelPolicy.ShouldJoin(1, 2), Is.False);
            Assert.That(VoiceChannelPolicy.ShouldJoin(2, 2), Is.True);
            Assert.That(VoiceChannelPolicy.ShouldJoin(3, 2), Is.True);
            Assert.That(VoiceChannelPolicy.ShouldJoin(1, 1), Is.True);
        }
    }

    [Test]
    public void NonPositiveThresholdNeverJoins()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(VoiceChannelPolicy.ShouldJoin(10, 0), Is.False);
            Assert.That(VoiceChannelPolicy.ShouldJoin(10, -1), Is.False);
        }
    }
}
