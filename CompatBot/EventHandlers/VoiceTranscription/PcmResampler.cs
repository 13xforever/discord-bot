namespace CompatBot.EventHandlers.VoiceTranscription;

/// <summary>
/// Turns the 48 kHz stereo interleaved s16le PCM that the voice receiver pushes into its pipe into
/// the 16 kHz mono PCM that whisper expects: complete frames are pulled out of the interleaved
/// stream and downmixed, then run through a two stage moving average low-pass filter before every
/// third frame is kept. A plain box filter would alias everything between 8 and 24 kHz straight
/// into the band whisper listens at.
/// Partial frames, filter history and the decimation phase are carried between chunks, so the
/// channel pairing never drifts when a chunk ends in the middle of a frame.
/// </summary>
internal sealed class PcmResampler
{
    public const int Channels = 2;
    public const int SourceSampleRate = 48_000;
    public const int TargetSampleRate = 16_000;
    public const int DecimationFactor = SourceSampleRate / TargetSampleRate;
    private const int FilterWidth = 3;

    private readonly int[] firstStage = new int[FilterWidth];
    private readonly int[] secondStage = new int[FilterWidth];
    private int pendingChannels;
    private int pendingChannelSum;
    private int decimationPhase;

    /// <summary>
    /// Total number of complete 48 kHz frames that went through the filter so far.
    /// </summary>
    public long ProcessedFrameCount { get; private set; }

    /// <summary>
    /// Resamples a chunk of interleaved 48 kHz samples into 16 kHz mono samples.
    /// </summary>
    public short[] Resample(ReadOnlySpan<short> interleavedSamples)
    {
        var frames = (pendingChannels + interleavedSamples.Length) / Channels;
        var output = new short[frames / DecimationFactor + 1];
        var outputCount = 0;

        foreach (var sample in interleavedSamples)
        {
            pendingChannelSum += sample;
            if (++pendingChannels < Channels)
                continue;

            pendingChannels = 0;
            var mono = pendingChannelSum / Channels;
            pendingChannelSum = 0;
            ProcessedFrameCount++;

            firstStage[0] = firstStage[1];
            firstStage[1] = firstStage[2];
            firstStage[2] = mono;
            var filtered = (firstStage[0] + firstStage[1] + firstStage[2]) / FilterWidth;

            secondStage[0] = secondStage[1];
            secondStage[1] = secondStage[2];
            secondStage[2] = filtered;
            filtered = (secondStage[0] + secondStage[1] + secondStage[2]) / FilterWidth;

            if (++decimationPhase < DecimationFactor)
                continue;

            decimationPhase = 0;
            output[outputCount++] = (short)filtered;
        }

        return outputCount == output.Length ? output : output.AsSpan(0, outputCount).ToArray();
    }
}
