using SpeexResamplerSharp;

namespace CompatBot.EventHandlers.VoiceTranscription;

/// <summary>
/// Turns the 48 kHz stereo interleaved s16le PCM that the voice receiver pushes into its pipe into
/// the 16 kHz mono PCM that whisper expects: complete frames are pulled out of the interleaved
/// stream and averaged down to mono, then <see cref="SpeexResampler"/> (a managed port of the
/// SpeexDSP resampler) does the band-limited 3:1 decimation.
/// The library's channel count is a single number, so it can't downmix for us, and taking one
/// channel with a stride would throw away half of the stereo energy, so the averaging stays here.
/// Partial frames are carried between chunks so the channel pairing never drifts, and the library
/// keeps its own filter history, which makes the output independent of where chunks are cut.
/// Not thread safe, same as the library: one instance per participant stream.
/// </summary>
internal sealed class PcmResampler
{
    public const int Channels = 2;
    public const int SourceSampleRate = 48_000;
    public const int TargetSampleRate = 16_000;
    public const int DecimationFactor = SourceSampleRate / TargetSampleRate;
    private const int InitialScratchSize = 1024;

    private readonly SpeexResampler resampler;
    private short[] monoFrames = new short[InitialScratchSize];
    private int pendingChannels;
    private int pendingChannelSum;

    /// <summary>
    /// The filter quality this instance was built with.
    /// </summary>
    public ResamplerQuality Quality { get; }

    public PcmResampler()
        : this(GetQuality())
    {
    }

    public PcmResampler(ResamplerQuality quality)
    {
        Quality = quality;
        resampler = new SpeexResampler(1, (uint)SourceSampleRate, (uint)TargetSampleRate, quality);
    }

    /// <summary>
    /// Filter delay in 48 kHz frames.
    /// </summary>
    public int InputLatency => resampler.InputLatency;

    /// <summary>
    /// Filter delay in 16 kHz frames.
    /// </summary>
    public int OutputLatency => resampler.OutputLatency;

    /// <summary>
    /// Quality from <see cref="Config.VoiceTranscriptionResamplerQuality"/>. Read once per session,
    /// because swapping the filter under a stream that already has history in it would be wrong.
    /// Quality 0 is off limits: its 24 tap filter is a worse version of what it replaces.
    /// </summary>
    private static ResamplerQuality GetQuality()
    {
        var configured = Config.VoiceTranscriptionResamplerQuality;
        var used = Math.Clamp(configured, (int)ResamplerQuality.Level1, (int)ResamplerQuality.Maximum);
        if (used != configured)
            Config.Log.Debug($"Ignoring VoiceTranscriptionResamplerQuality {configured}, using {used} instead");
        return (ResamplerQuality)used;
    }

    /// <summary>
    /// The most samples <see cref="Resample"/> can write for that much interleaved input. Passing a
    /// destination of at least that size makes sure no input is left unconsumed.
    /// </summary>
    public int MaxOutputFrames(int interleavedSampleCount)
        => OutputFramesFor((pendingChannels + interleavedSampleCount) / Channels) + OutputLatency + 2;

    /// <summary>
    /// Exactly how many samples <see cref="Drain"/> produces: a full filter length of silence, which
    /// is how far the retained history reaches.
    /// </summary>
    public int MaxDrainFrames => OutputFramesFor(DrainFrames);

    /// <summary>
    /// Resamples interleaved 48 kHz samples into <paramref name="destination"/>, which must be at
    /// least <see cref="MaxOutputFrames"/> wide, and reports how many complete 48 kHz frames got
    /// consumed, including ones completed from a partial frame of a previous chunk.
    /// </summary>
    public int Resample(ReadOnlySpan<short> interleavedSamples, Span<short> destination, out int framesProcessed)
    {
        var frameCount = Downmix(interleavedSamples);
        var written = 0;
        var consumed = 0;

        for (var offset = 0; offset < frameCount;)
        {
            var progress = resampler.Process(0, monoFrames.AsSpan(offset, frameCount - offset), destination[written..]);
            if (progress.InputConsumed == 0)
                throw new InvalidOperationException($"Destination too small to resample {frameCount} frames at speex quality {(int)Quality}");

            offset += progress.InputConsumed;
            consumed += progress.InputConsumed;
            written += progress.OutputProduced;
        }

        framesProcessed = consumed;
        return written;
    }

    /// <summary>
    /// Pushes one full filter length worth of silence through so the samples the filter still holds
    /// come out. Used when a participant stream is dropped for good, otherwise the last couple of
    /// milliseconds of their speech never reach whisper. <paramref name="destination"/> should be
    /// <see cref="MaxDrainFrames"/> wide.
    /// </summary>
    public int Drain(Span<short> destination)
        => resampler.ProcessZeros(0, DrainFrames, destination).OutputProduced;

    private int DrainFrames => resampler.InputLatency * 2;

    private int OutputFramesFor(int inputFrames)
        => (int)((long)inputFrames * resampler.RatioDenominator / resampler.RatioNumerator);

    private int Downmix(ReadOnlySpan<short> interleavedSamples)
    {
        var frames = (pendingChannels + interleavedSamples.Length) / Channels;
        if (frames > monoFrames.Length)
            Array.Resize(ref monoFrames, Math.Max(frames, monoFrames.Length * 2));

        var frameCount = 0;
        foreach (var sample in interleavedSamples)
        {
            pendingChannelSum += sample;
            if (++pendingChannels < Channels)
                continue;

            pendingChannels = 0;
            monoFrames[frameCount++] = (short)(pendingChannelSum / Channels);
            pendingChannelSum = 0;
        }

        return frameCount;
    }
}
