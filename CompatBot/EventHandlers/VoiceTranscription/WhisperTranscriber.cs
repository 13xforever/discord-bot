using System.Diagnostics;
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

    // whisper defaults, spelled out because they're what keeps hallucinated text from running away
    // on silence and music; the no speech threshold is nudged up a bit to drop background noise sooner
    private const float NoSpeechThreshold = 0.7f;
    private const float EntropyThreshold = 2.4f;
    private const float LogProbThreshold = -1.0f;
    private const float TemperatureIncrement = 0.2f;

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
                {
                    await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                        ParseModelVariant(Config.WhisperModelVariant),
                        cancellationToken: cancellationToken
                    ).ConfigureAwait(false);
                    await using var file = File.Create(partialPath);
                    await modelStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
                File.Move(partialPath, ModelPath);
            }
            Config.Log.Info($"Whisper {DescribeModel()} is ready, thresholds: noSpeech={NoSpeechThreshold:F2} entropy={EntropyThreshold:F2} logProb={LogProbThreshold:F2} temperatureInc={TemperatureIncrement:F2}");
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
    public static async ValueTask<string?> DetectLanguageAsync(string tag, short[] samples16k, CancellationToken cancellationToken)
    {
        if (samples16k.Length < MinSamplesForLanguageDetection)
        {
            if (samples16k.Length > 0)
                Config.Log.Trace($"{tag}: language detection needs at least {MinSamplesForLanguageDetection / (double)PcmResampler.TargetSampleRate:F1}s of speech, this chunk is {AudioLength(samples16k.Length)}");
            return null;
        }

        if (!ModelAvailable())
            return null;

        var samples = ToFloatSamples(samples16k);
        var wait = AwaitLockAsync(tag, cancellationToken).ConfigureAwait(false);
        try
        {
            var detect = Stopwatch.StartNew();
            var (language, probability) = GetProcessor().DetectLanguageWithProbability(samples);
            detect.Stop();
            if (string.IsNullOrEmpty(language))
            {
                Config.Log.Debug($"{tag}: whisper detected no language at all in {detect.ElapsedMilliseconds}ms");
                return null;
            }
            Config.Log.Debug($"{tag}: whisper detected language '{language}' with p={probability:F2} from {samples16k.Length / (double)PcmResampler.TargetSampleRate:F1}s of audio in {detect.ElapsedMilliseconds}ms");
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
    public static async ValueTask<IReadOnlyList<TranscribedSegment>> TranscribeAsync(string tag, short[] samples16k, string? language, CancellationToken cancellationToken)
    {
        var segments = new List<TranscribedSegment>();
        var audioLength = AudioLength(samples16k.Length);
        if (samples16k.Length < MinSamplesForTranscription)
        {
            Config.Log.Trace($"{tag}: {audioLength} of audio is too short to bother whisper, skipped");
            return segments;
        }

        if (!ModelAvailable())
            return segments;

        var samples = ToFloatSamples(samples16k);
        await AwaitLockAsync(tag, cancellationToken).ConfigureAwait(false);
        try
        {
            var activeProcessor = GetProcessor();
            SetLanguage(activeProcessor, language);
            var run = Stopwatch.StartNew();
            await foreach (var segment in activeProcessor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                var text = segment.Text.Trim();
                if (string.IsNullOrEmpty(text))
                    continue;

                segments.Add(new(text, segment.Start, segment.End));
                Config.Log.Trace($"{tag}: segment {segments.Count} [{segment.Start.TotalSeconds:F2}-{segment.End.TotalSeconds:F2}]s, p={segment.Probability:F2} max={segment.MaxProbability:F2} min={segment.MinProbability:F2} noSpeech={segment.NoSpeechProbability:F2} lang={segment.Language}: \"{text}\"");
            }
            run.Stop();

            if (segments.Count == 0)
                Config.Log.Debug($"{tag}: whisper made nothing out of {audioLength} of audio (language {(string.IsNullOrEmpty(language) ? "auto" : language)}), " +
                                 $"noSpeechThreshold={NoSpeechThreshold:F2} entropyThreshold={EntropyThreshold:F2} logProbThreshold={LogProbThreshold:F2} in {run.ElapsedMilliseconds}ms");
            else
                Config.Log.Debug($"{tag}: whisper produced {segments.Count} segment(s) from {audioLength} of audio (language {(string.IsNullOrEmpty(language) ? "auto" : language)}) in {run.ElapsedMilliseconds}ms, rtf={(run.Elapsed.TotalSeconds / (samples16k.Length / (double)PcmResampler.TargetSampleRate)):F2}");
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

    private static async Task AwaitLockAsync(string tag, CancellationToken cancellationToken)
    {
        var wait = Stopwatch.StartNew();
        await JobLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        wait.Stop();
        if (wait.ElapsedMilliseconds > 0)
            Config.Log.Trace($"{tag}: waited {wait.ElapsedMilliseconds}ms for the whisper lane");
    }

    private static string AudioLength(int samples)
        => samples < PcmResampler.TargetSampleRate
            ? $"{samples * 1000.0 / PcmResampler.TargetSampleRate:F0}ms"
            : $"{samples / (double)PcmResampler.TargetSampleRate:F1}s";

    private static bool ModelAvailable()
    {
        if (File.Exists(ModelPath))
            return true;
        if (Interlocked.Exchange(ref logFailed, 1) is 0)
            Config.Log.Error($"Voice transcription unavailable: whisper model '{Config.WhisperModelVariant}' is missing from {ModelCachePath}");
        return false;
    }

    /// <summary>
    /// Model file state, so a session's first line already says whether transcription can work at all.
    /// </summary>
    public static string DescribeModel()
    {
        try
        {
            var file = new FileInfo(ModelPath);
            return $"'{Config.WhisperModelVariant}' at {ModelPath}, {file.Length / 1024.0 / 1024.0:F1} MB";
        }
        catch (Exception e)
        {
            return $"'{Config.WhisperModelVariant}' expected at {ModelPath} but unavailable ({e.Message})";
        }
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
            .WithNoSpeechThreshold(NoSpeechThreshold)
            .WithEntropyThreshold(EntropyThreshold)
            .WithLogProbThreshold(LogProbThreshold)
            .WithTemperatureInc(TemperatureIncrement)
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
