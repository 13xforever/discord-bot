using System.Globalization;
using System.IO;
using System.Text;

namespace CompatBot.EventHandlers.VoiceTranscription;

internal static class TranscriptFormatter
{
    public sealed record Line(DateTime UtcStart, string Speaker, string Text);

    private const int MaxFilenameNamePartLength = 100;

    public static string FormatDuration(TimeSpan duration)
        => $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    public static long ToUnixTimeSeconds(DateTime utc)
        => new DateTimeOffset(utc.Kind is DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc).ToUnixTimeSeconds();

    /// <summary>
    /// The plain text line that accompanies the transcript attachment in the log channel.
    /// Timestamps are rendered by Discord in the reader's own timezone.
    /// </summary>
    public static string BuildSummary(string channelName, DateTime startedUtc, DateTime endedUtc)
        => $"Voice transcription for #{channelName}: <t:{ToUnixTimeSeconds(startedUtc)}:F> → <t:{ToUnixTimeSeconds(endedUtc)}:F> ({FormatDuration(endedUtc - startedUtc)})";

    /// <summary>
    /// Deterministic .txt attachment name for a session. Channel names are user settable, so
    /// anything that isn't filename safe gets replaced.
    /// </summary>
    public static string BuildAttachmentFilename(string channelName, DateTime startedUtc)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new StringBuilder(channelName.Length);
        foreach (var c in channelName)
            safeName.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);

        var name = safeName.ToString().Trim('_');
        if (name.Length > MaxFilenameNamePartLength)
            name = name[..MaxFilenameNamePartLength];
        if (name.Length == 0)
            name = "voice";
        return $"{name}_{startedUtc:yyyyMMdd-HHmmss}.txt";
    }

    public static string Format(
        string channelName,
        string? guildName,
        DateTime startedUtc,
        DateTime? endedUtc,
        IReadOnlyList<string> participants,
        IEnumerable<Line> lines,
        bool inProgress,
        bool truncated = false)
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

        var rendered = 0;
        foreach (var line in lines.OrderBy(l => l.UtcStart))
        {
            sb.Append('[').Append(line.UtcStart.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ")
              .Append(line.Speaker).Append(": ").AppendLine(line.Text);
            rendered++;
        }

        if (truncated)
            sb.AppendLine($"… transcript truncated, only the first {rendered} lines were kept …");

        return sb.ToString().TrimEnd();
    }
}
