using System;
using System.IO;
using CompatBot.EventHandlers.VoiceTranscription;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class TranscriptFormatterTests
{
    private static readonly DateTime Started = new(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
    private const long StartedUnix = 1791885600;

    [Test]
    public void FormatsCompletedReport()
    {
        var ended = Started.Add(TimeSpan.FromMinutes(5));
        var lines = new[]
        {
            new TranscriptFormatter.Line(Started.AddSeconds(60), "bob", "second line"),
            new TranscriptFormatter.Line(Started.AddSeconds(10), "alice", "first line"),
        };
        var report = TranscriptFormatter.Format("general", "Test Guild", Started, ended, ["alice", "bob"], lines, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Does.Contain("Channel: #general (Test Guild)"));
            Assert.That(report, Does.Contain("Started: 2026-10-13 10:00:00 UTC"));
            Assert.That(report, Does.Contain("Ended: 2026-10-13 10:05:00 UTC"));
            Assert.That(report, Does.Contain("Duration: 00:05:00"));
            Assert.That(report, Does.Contain("Participants: alice, bob"));
            // lines are sorted by start time regardless of input order
            Assert.That(
                report.IndexOf("first line", StringComparison.Ordinal),
                Is.LessThan(report.IndexOf("second line", StringComparison.Ordinal))
            );
        }
    }

    [Test]
    public void InProgressReportOmitsEndAndDuration()
    {
        var report = TranscriptFormatter.Format("general", null, Started, null, [], [], true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Does.Contain("Channel: #general"));
            Assert.That(report, Does.Contain("Ended: in progress"));
            Assert.That(report, Does.Not.Contain("Duration:"));
        }
    }

    [Test]
    public void TruncatedReportSaysSo()
    {
        var lines = new[] { new TranscriptFormatter.Line(Started, "alice", "hello") };
        var report = TranscriptFormatter.Format("general", null, Started, Started.AddSeconds(1), ["alice"], lines, false, true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Does.Contain("hello"));
            Assert.That(report, Does.Contain("transcript truncated, only the first 1 lines were kept"));
        }
    }

    [Test]
    public void FormatDuration()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TranscriptFormatter.FormatDuration(TimeSpan.FromSeconds(3725)), Is.EqualTo("01:02:05"));
            Assert.That(TranscriptFormatter.FormatDuration(TimeSpan.Zero), Is.EqualTo("00:00:00"));
        }
    }

    [Test]
    public void UnixTimestampsAreReadAsUtc()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TranscriptFormatter.ToUnixTimeSeconds(Started), Is.EqualTo(StartedUnix));
            Assert.That(TranscriptFormatter.ToUnixTimeSeconds(DateTime.SpecifyKind(Started, DateTimeKind.Unspecified)), Is.EqualTo(StartedUnix));
        }
    }

    [Test]
    public void SummaryUsesDiscordTimestampMentions()
    {
        var summary = TranscriptFormatter.BuildSummary("VC 1", Started, Started.Add(TimeSpan.FromSeconds(3725)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary, Does.StartWith("Voice transcription for #VC 1: "));
            Assert.That(summary, Does.Contain($"<t:{StartedUnix}:F>"));
            Assert.That(summary, Does.Contain($"<t:{StartedUnix + 3725}:F>"));
            Assert.That(summary, Does.Contain("(01:02:05)"));
            Assert.That(summary, Has.Length.LessThan(200));
        }
    }

    [Test]
    public void AttachmentFilenamesAreMadeSafe()
    {
        var name = TranscriptFormatter.BuildAttachmentFilename("a/b:c d*e?fg<h>i|jk\"lm", Started);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(name, Does.StartWith("a_b_c_d_e_fg_h_i_jk_lm_20261013-100000.txt"));
            Assert.That(name.IndexOfAny(Path.GetInvalidFileNameChars()), Is.EqualTo(-1));
            Assert.That(name, Does.Not.Contain(" "));
        }
    }

    [Test]
    public void AttachmentFilenamesHandleAwkwardChannelNames()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TranscriptFormatter.BuildAttachmentFilename("   ", Started), Does.StartWith("voice_"));
            Assert.That(TranscriptFormatter.BuildAttachmentFilename(new string('x', 500), Started).Length, Is.LessThan(130));
        }
    }
}
