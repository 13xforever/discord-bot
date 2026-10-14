using System.IO;
using Whisper.net;
using Whisper.net.Ggml;

namespace CompatBot.EventHandlers.VoiceTranscription;

internal readonly record struct TranscribedSegment(string Text, TimeSpan Start, TimeSpan End);

/// <summary>
/// A wrapper around whisper.net that keeps the model loaded and serializes transcription jobs.
/// </summary>
internal static class WhisperTranscriber
{
    private const int MinSamplesForTranscription = 1600; // less than 0.1s of audio is not worth transcribing

    private static readonly SemaphoreSlim JobLock = new(1, 1);
    private static readonly Lock FactoryGate = new();
    private static WhisperFactory? factory;
    private static int logFailed;

    private static readonly string ModelCachePath = Path.Combine(Config.BotAppDataFolder, "voice-models");
    private static readonly string ModelPath = Path.Combine(ModelCachePath, $"ggml-{Config.WhisperModelVariant}.bin");

    public static async Task PreloadModelAsync(CancellationToken cancellationToken)
    {
        if (!Config.VoiceTranscriptionEnabled)
            return;
        
        try
        {
            if (!Directory.Exists(ModelCachePath))
                Directory.CreateDirectory(ModelCachePath);

            if (!File.Exists(ModelPath))
            {
                var partialPath = ModelPath + ".part";
                if (File.Exists(partialPath))
                    File.Delete(partialPath);

                Config.Log.Info($"Downloading whisper model ({Config.WhisperModelVariant})…");
                await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    ParseModelVariant(Config.WhisperModelVariant),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);
                await using var file = File.Create(partialPath);
                await modelStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                File.Move(partialPath, ModelPath);
            }
            Config.Log.Info($"Whisper model '{Config.WhisperModelVariant}' available");
        }
        catch (Exception e)
        {
            Interlocked.Exchange(ref logFailed, 1);
            Config.Log.Error(e, "Failed to preload whisper model; voice transcription will be unavailable");
        }
    }

    public static async ValueTask<IReadOnlyList<TranscribedSegment>> TranscribeAsync(short[] samples16k, CancellationToken cancellationToken)
    {
        var segments = new List<TranscribedSegment>();
        if (samples16k.Length < MinSamplesForTranscription)
            return segments;

        var samples = new float[samples16k.Length];
        for (var i = 0; i < samples16k.Length; i++)
            samples[i] = samples16k[i] / 32768f;

        await JobLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processor = GetFactory().CreateBuilder().WithLanguage("auto").Build();
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                var text = segment.Text.Trim();
                if (!string.IsNullOrEmpty(text))
                    segments.Add(new(text, segment.Start, segment.End));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref logFailed, 1) is 0)
                Config.Log.Error(e, "Voice transcription unavailable: whisper failed to process audio");
        }
        finally
        {
            JobLock.Release();
        }

        return segments;
    }

    private static WhisperFactory GetFactory()
    {
        lock (FactoryGate)
        {
            factory ??= WhisperFactory.FromPath(ModelPath);
            return factory;
        }
    }

    private static GgmlType ParseModelVariant(string variant)
        => Enum.TryParse<GgmlType>(variant.Replace("-", ""), ignoreCase: true, out var type)
            ? type
            : GgmlType.Base;
}
