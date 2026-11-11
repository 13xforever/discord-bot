using System.IO;
using Whisper.net;
using Whisper.net.Ggml;

namespace CompatBot.EventHandlers.VoiceTranscription;

internal readonly record struct TranscribedSegment(string Text, TimeSpan Start, TimeSpan End);

/// <summary>
/// A wrapper around whisper.net that keeps the model loaded and serializes transcription jobs.
/// One <see cref="WhisperProcessor"/> is reused for every job: whisper.net processors are stateful
/// and meant to be run repeatedly, so building a new one per utterance would re-init the ggml
/// context over and over for nothing. All access is serialized through <see cref="JobLock"/>.
/// </summary>
internal static class WhisperTranscriber
{
    private const int MinSamplesForTranscription = 1600; // less than 0.1s of audio is not worth transcribing
    private const int MinSamplesForLanguageDetection = PcmResampler.TargetSampleRate; // detection needs about a full second of speech to be reliable
    private const string AutoDetectLanguage = "auto";

    private static readonly SemaphoreSlim JobLock = new(1, 1);
    private static readonly Lock ProcessorGate = new();
    private static WhisperFactory? factory;
    private static WhisperProcessor? processor;
    private static string? currentLanguage;
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

    /// <summary>
    /// Detects the spoken language once so the rest of the session can be transcribed with a fixed
    /// language instead of re-detecting it on every chunk, which tends to produce gibberish or
    /// translations for short utterances.
    /// </summary>
    public static async ValueTask<string?> DetectLanguageAsync(short[] samples16k, CancellationToken cancellationToken)
    {
        if (samples16k.Length < MinSamplesForLanguageDetection || !ModelAvailable())
            return null;

        var samples = ToFloatSamples(samples16k);
        await JobLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (language, probability) = GetProcessor().DetectLanguageWithProbability(samples);
            if (string.IsNullOrEmpty(language))
                return null;
            Config.Log.Debug($"Detected voice language '{language}' (p={probability:F2})");
            return language;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            LogOnce(e);
            return null;
        }
        finally
        {
            JobLock.Release();
        }
    }

    /// <summary>
    /// Transcribes a chunk of 16 kHz mono PCM. <paramref name="language"/> pins the spoken language;
    /// passing null keeps whisper guessing every run.
    /// </summary>
    public static async ValueTask<IReadOnlyList<TranscribedSegment>> TranscribeAsync(short[] samples16k, string? language, CancellationToken cancellationToken)
    {
        var segments = new List<TranscribedSegment>();
        if (samples16k.Length < MinSamplesForTranscription || !ModelAvailable())
            return segments;

        var samples = ToFloatSamples(samples16k);
        await JobLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activeProcessor = GetProcessor();
            SetLanguage(activeProcessor, language);
            await foreach (var segment in activeProcessor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
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
            LogOnce(e);
        }
        finally
        {
            JobLock.Release();
        }

        return segments;
    }

    private static bool ModelAvailable()
    {
        if (File.Exists(ModelPath))
            return true;
        if (Interlocked.Exchange(ref logFailed, 1) is 0)
            Config.Log.Error($"Voice transcription unavailable: whisper model '{Config.WhisperModelVariant}' is missing from {ModelCachePath}");
        return false;
    }

    private static WhisperProcessor GetProcessor()
    {
        lock (ProcessorGate)
            return processor ??= CreateProcessor();
    }

    private static WhisperProcessor CreateProcessor()
    {
        factory ??= WhisperFactory.FromPath(ModelPath);
        currentLanguage = AutoDetectLanguage;
        return factory
            .CreateBuilder()
            .WithLanguage(AutoDetectLanguage)
            // whisper defaults, spelled out because they're what keeps hallucinated text from
            // running away on silence and music; the no speech threshold is nudged up a bit to
            // drop background noise sooner
            .WithNoSpeechThreshold(0.7f)
            .WithEntropyThreshold(2.4f)
            .WithLogProbThreshold(-1.0f)
            .WithTemperatureInc(0.2f)
            .Build();
    }

    private static void SetLanguage(WhisperProcessor activeProcessor, string? language)
    {
        var target = string.IsNullOrEmpty(language) ? AutoDetectLanguage : language;
        if (string.Equals(currentLanguage, target, StringComparison.OrdinalIgnoreCase))
            return;
        activeProcessor.ChangeLanguage(target);
        currentLanguage = target;
    }

    private static float[] ToFloatSamples(short[] samples16k)
    {
        var samples = new float[samples16k.Length];
        for (var i = 0; i < samples16k.Length; i++)
            samples[i] = samples16k[i] / 32768f;
        return samples;
    }

    private static void LogOnce(Exception e)
    {
        if (Interlocked.Exchange(ref logFailed, 1) is 0)
            Config.Log.Error(e, "Voice transcription unavailable: whisper failed to process audio");
    }

    private static GgmlType ParseModelVariant(string variant)
        => Enum.TryParse<GgmlType>(variant.Replace("-", ""), ignoreCase: true, out var type)
            ? type
            : GgmlType.Base;
}
