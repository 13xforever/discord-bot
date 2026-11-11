using System.IO;
using CompatBot.EventHandlers.VoiceTranscription;

namespace CompatBot.Commands;

[Command("voice")]
[Description("Voice channel tools")]
internal static partial class Voice
{
    // Discord takes at most 10 attachments per message
    private const int MaxAttachments = 10;

    [Command("transcription"), RequiresBotModRole]
    [Description("Show in-progress voice transcription for active channels")]
    public static async ValueTask Transcription(SlashCommandContext ctx)
    {
        var ephemeral = !ctx.Channel.IsPrivate;
        var sessions = VoiceSessionMonitor.ActiveSessions
            .OrderBy(session => session.ChannelName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sessions.Count == 0)
        {
            await ctx.RespondAsync($"{Config.Reactions.Failure} No active voice transcription sessions", ephemeral: ephemeral).ConfigureAwait(false);
            return;
        }

        var response = new DiscordInteractionResponseBuilder().AsEphemeral(ephemeral);
        var streams = new List<Stream>();
        try
        {
            foreach (var session in sessions.Take(MaxAttachments))
            {
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(session.BuildTranscript(null)));
                streams.Add(stream);
                response.AddFile(session.GetAttachmentFilename(), stream);
            }
            if (sessions.Count > streams.Count)
                response.WithContent($"Showing {streams.Count} of {sessions.Count} in-progress transcripts");

            await ctx.RespondAsync(response).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Config.Log.Warn(e, "Failed to send voice transcription attachments");
            try
            {
                await ctx.RespondAsync($"{Config.Reactions.Failure} Failed to send the transcripts", ephemeral: ephemeral).ConfigureAwait(false);
            }
            catch
            {
                // response may have already been sent
            }
        }
        finally
        {
            foreach (var stream in streams)
                stream.Dispose();
        }
    }
}
