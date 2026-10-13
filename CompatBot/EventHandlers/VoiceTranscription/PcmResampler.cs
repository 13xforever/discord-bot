namespace CompatBot.EventHandlers.VoiceTranscription;

/// <summary>
/// Downsamples 48 kHz mono s16 PCM to 16 kHz using a simple box (averaging) filter.
/// The 3:1 ratio is exact, so the only loss is from plain averaging.
/// </summary>
internal sealed class PcmResampler
{
    private readonly short[] pending = new short[2];
    private int pendingCount;

    /// <summary>
    /// Resamples a chunk of 48 kHz samples to 16 kHz, carrying the remainder (0..2 samples) over between calls.
    /// </summary>
    public short[] Resample(ReadOnlySpan<short> input)
    {
        var combined = new short[pendingCount + input.Length];
        pending.AsSpan(0, pendingCount).CopyTo(combined);
        input.CopyTo(combined.AsSpan(pendingCount));

        var outputCount = combined.Length / 3;
        var output = new short[outputCount];
        for (var i = 0; i < outputCount; i++)
        {
            var j = i * 3;
            output[i] = (short)((combined[j] + combined[j + 1] + combined[j + 2]) / 3);
        }

        pendingCount = combined.Length % 3;
        if (pendingCount > 0)
            combined.AsSpan(combined.Length - pendingCount, pendingCount).CopyTo(pending);
        return output;
    }
}
