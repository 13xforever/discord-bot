using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace CompatBot.EventHandlers.VoiceTranscription;

/// <summary>
/// Accumulates 16 kHz mono PCM samples for a single voice channel participant in memory.
/// The participant's pipe carries their whole 48 kHz stereo stream, silence frames included, so
/// samples only get added to the buffer while that user is actually speaking. The stream is fed
/// through the resampler regardless of the speaking state to keep channel pairing and decimation
/// phase aligned across chunks.
/// </summary>
internal sealed class UserAudioBuffer
{
    private const int InitialBufferSize = 4096;

    private readonly PipeReader reader;
    private readonly Func<bool> isSpeakingProvider;
    private readonly PcmResampler resampler;
    private readonly Lock gate = new();
    private readonly string tag;

    private short[] buffer = new short[InitialBufferSize];
    private short[] discarded = [];
    private int length;
    private DateTime? chunkStartUtc;

    private long receivedFrameCount;
    private long chunkCount;
    private long carriedByteCount;
    private byte pendingByte;
    private bool hasPendingByte;

    public UserAudioBuffer(PipeReader reader, Func<bool> isSpeakingProvider, string tag, PcmResampler? resampler = null)
    {
        this.reader = reader;
        this.isSpeakingProvider = isSpeakingProvider;
        this.tag = tag;
        this.resampler = resampler ?? new PcmResampler(tag);
    }

    /// <summary>
    /// Total number of 48 kHz frames read from the pipe, voiced or not.
    /// A live connection keeps pushing frames at all times, spoken or silent, which is what tells a
    /// live connection apart from one that quietly died.
    /// </summary>
    public long ReceivedFrameCount => Interlocked.Read(ref receivedFrameCount);

    /// <summary>
    /// Number of 16 kHz samples waiting to be transcribed.
    /// </summary>
    public int BufferedSampleCount
    {
        get { lock (gate) return length; }
    }

    public Task RunAsync(CancellationToken cancellationToken) => ReadLoopAsync(cancellationToken);

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var segment = result.Buffer;
                foreach (var memory in segment)
                    ConsumeBytes(memory.Span);
                reader.AdvanceTo(segment.End);
                if (result.IsCompleted || result.IsCanceled)
                {
                    Config.Log.Debug($"{tag}: the audio pipe finished after {chunkCount} chunk(s) and {receivedFrameCount} frame(s)");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Config.Log.Debug($"{tag}: audio reading cancelled after {chunkCount} chunk(s) and {receivedFrameCount} frame(s)");
        }
        catch (Exception e)
        {
            Config.Log.Warn(e, $"{tag}: the audio stream broke unexpectedly after {chunkCount} chunk(s) and {receivedFrameCount} frame(s)");
        }
        finally
        {
            // release the pipe, otherwise the writing side keeps buffering audio forever
            try
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Config.Log.Debug(e, $"{tag}: failed to complete the audio pipe");
            }
        }
    }

    private void ConsumeBytes(ReadOnlySpan<byte> bytes)
    {
        // pipe segments are not sample aligned, so a sample can be split between two of them
        var start = 0;
        if (hasPendingByte)
        {
            if (bytes.IsEmpty)
                return;
            // the pending byte started that sample, so the new one completes it as the high half
            var sample = unchecked((short)(pendingByte | (bytes[0] << 8)));
            Interlocked.Increment(ref carriedByteCount);
            Span<short> single = [sample];
            ConsumeSamples(single);
            start = 1;
            hasPendingByte = false;
        }

        var aligned = bytes[start..];
        var sampleCount = aligned.Length / sizeof(short);
        if (sampleCount > 0)
            ConsumeSamples(MemoryMarshal.Cast<byte, short>(aligned[..(sampleCount * sizeof(short))]));

        if ((aligned.Length & 1) != 0)
        {
            pendingByte = aligned[^1];
            hasPendingByte = true;
        }
    }

    private void ConsumeSamples(ReadOnlySpan<short> interleavedSamples)
    {
        // the resampler is not thread safe, and TakeAll can reach it from the session thread to
        // drain it, so everything that touches it goes through the same lock
        lock (gate)
        {
            var maxOutput = resampler.MaxOutputFrames(interleavedSamples.Length);
            var speaking = isSpeakingProvider();
            Span<short> destination;
            if (speaking)
            {
                EnsureCapacity(length + maxOutput);
                destination = buffer.AsSpan(length, maxOutput);
            }
            else
            {
                // the samples still have to go through the filter to keep its history and phase in
                // step with the stream, they just don't get kept
                if (discarded.Length < maxOutput)
                    Array.Resize(ref discarded, maxOutput);
                destination = discarded.AsSpan(0, maxOutput);
            }

            var written = resampler.Resample(interleavedSamples, destination, out var frames);
            if (speaking && written > 0)
            {
                length += written;
                chunkStartUtc ??= DateTime.UtcNow;
            }

            // counted last, so anyone watching this counter can see the samples that come with it
            Interlocked.Add(ref receivedFrameCount, frames);

            chunkCount++;
            if (chunkCount == 1)
                Config.Log.Trace($"{tag}: first audio chunk, {interleavedSamples.Length * sizeof(short)} bytes, {frames} frame(s) at " +
                                 $"{PcmResampler.SourceSampleRate} Hz, {written} sample(s) at {PcmResampler.TargetSampleRate} Hz, speaking={speaking}");
            else
                Config.Log.Trace($"{tag}: chunk #{chunkCount}, {interleavedSamples.Length * sizeof(short)} bytes → {frames} frame(s) consumed, {written} sample(s) kept, " +
                                 $"speaking={speaking}, buffered={length}, misaligned segment(s) carried={Interlocked.Read(ref carriedByteCount)}");
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= buffer.Length)
            return;
        var newSize = buffer.Length;
        while (newSize < required)
            newSize *= 2;
        Array.Resize(ref buffer, newSize);
    }

    /// <summary>
    /// Takes the buffered audio if it has been pending for at least <paramref name="minAge"/>.
    /// </summary>
    public bool TryTakePending(TimeSpan minAge, out short[] samples, out DateTime chunkStartUtc)
    {
        lock (gate)
        {
            if (length == 0)
            {
                Config.Log.Trace($"{tag}: nothing to take, the buffer is empty");
                samples = [];
                chunkStartUtc = default;
                return false;
            }

            var age = DateTime.UtcNow - (this.chunkStartUtc ?? DateTime.UtcNow);
            if (this.chunkStartUtc is null || age < minAge)
            {
                Config.Log.Trace($"{tag}: holding {length} sample(s) back, pending for {age.TotalMilliseconds:F0}ms, needs {minAge.TotalMilliseconds:F0}ms");
                samples = [];
                chunkStartUtc = default;
                return false;
            }

            Config.Log.Debug($"{tag}: taking {length} sample(s) ({length / (double)PcmResampler.TargetSampleRate:F1}s), pending for {age.TotalMilliseconds:F0}ms");
            chunkStartUtc = this.chunkStartUtc.Value;
            samples = buffer.AsSpan(0, length).ToArray();
            length = 0;
            this.chunkStartUtc = null;
            return true;
        }
    }

    /// <summary>
    /// Takes all buffered audio regardless of age. <paramref name="drainResampler"/> also flushes
    /// whatever the filter still holds, which is what you want whenever this buffer is the last
    /// thing that participant's audio ever goes through.
    /// </summary>
    public (short[] Samples, DateTime? ChunkStartUtc) TakeAll(bool drainResampler = false)
    {
        lock (gate)
        {
            if (drainResampler)
            {
                if (Interlocked.Read(ref receivedFrameCount) == 0)
                    Config.Log.Trace($"{tag}: nothing to drain, no audio ever came through this pipe");
                else
                {
                    var maxDrain = resampler.MaxDrainFrames;
                    EnsureCapacity(length + maxDrain);
                    var drained = resampler.Drain(buffer.AsSpan(length, maxDrain));
                    length += drained;
                    if (drained > 0)
                        chunkStartUtc ??= DateTime.UtcNow;
                    Config.Log.Debug($"{tag}: drained {drained} sample(s) ({drained * 1000.0 / PcmResampler.TargetSampleRate:F0}ms) out of the resampler filter");
                }
            }

            var samples = buffer.AsSpan(0, length).ToArray();
            length = 0;
            var start = chunkStartUtc;
            chunkStartUtc = null;
            return (samples, start);
        }
    }
}
