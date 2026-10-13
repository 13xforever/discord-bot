using System.Globalization;
using System.Text;

namespace CompatBot.EventHandlers.VoiceTranscription;

internal static class TranscriptFormatter
{
    public sealed record Line(DateTime UtcStart, string Speaker, string Text);

    public static string FormatDuration(TimeSpan duration)
        => $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    public static string Format(
        string channelName,
        string? guildName,
        DateTime startedUtc,
        DateTime? endedUtc,
        IReadOnlyList<string> participants,
        IEnumerable<Line> lines,
        bool inProgress)
    {
        var sb = new StringBuilder();
        sb.Append("Channel: #").Append(channelName);
        if (!string.IsNullOrEmpty(guildName))
            sb.Append(" (").Append(guildName).Append(')');
        sb.AppendLine();
        sb.Append("Started: ").Append(startedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC").AppendLine();
        if (inProgress || endedUtc is null)
            sb.AppendLine("Ended: in progress");
        else
            sb.Append("Ended: ").Append(endedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC").AppendLine();
        if (!inProgress && endedUtc is not null)
            sb.Append("Duration: ").Append(FormatDuration(endedUtc.Value - startedUtc)).AppendLine();
        sb.Append("Participants: ").Append(string.Join(", ", participants)).AppendLine();
        sb.AppendLine("All timestamps are UTC.");
        sb.AppendLine();
        foreach (var line in lines.OrderBy(l => l.UtcStart))
            sb.Append('[').Append(line.UtcStart.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ")
              .Append(line.Speaker).Append(": ").AppendLine(line.Text);
        return sb.ToString().TrimEnd();
    }
}
