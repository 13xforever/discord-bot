using System;
using CompatBot.EventHandlers.VoiceTranscription;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class TranscriptFormatterTests
{
    [Test]
    public void FormatsCompletedReport()
    {
        var started = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        var ended = started.Add(TimeSpan.FromMinutes(5));
        var lines = new[]
        {
            new TranscriptFormatter.Line(started.AddSeconds(60), "bob", "second line"),
            new TranscriptFormatter.Line(started.AddSeconds(10), "alice", "first line"),
        };
        var report = TranscriptFormatter.Format("general", "Test Guild", started, ended, ["alice", "bob"], lines, false);
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
        var started = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        var report = TranscriptFormatter.Format("general", null, started, null, [], [], true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Does.Contain("Channel: #general"));
            Assert.That(report, Does.Contain("Ended: in progress"));
            Assert.That(report, Does.Not.Contain("Duration:"));
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
}
