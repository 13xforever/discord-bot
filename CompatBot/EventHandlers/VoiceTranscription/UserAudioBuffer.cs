using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace CompatBot.EventHandlers.VoiceTranscription;

/// <summary>
/// Accumulates 16 kHz mono PCM samples for a single voice channel user in memory.
/// Only audio received while the user is actually speaking gets buffered, since the
/// receiver's pipe also carries silence frames that we don't want to transcribe.
/// </summary>
internal sealed class UserAudioBuffer
{
    private readonly PipeReader reader;
    private readonly Func<bool> isSpeakingProvider;
    private readonly PcmResampler resampler = new();
    private readonly object gate = new();

    private short[] buffer = [];
    private DateTime? chunkStartUtc;
    private long voicedSampleCount;
    private long receivedSampleCount;

    public UserAudioBuffer(PipeReader reader, Func<bool> isSpeakingProvider)
    {
        this.reader = reader;
        this.isSpeakingProvider = isSpeakingProvider;
    }

    /// <summary>
    /// Total number of 16 kHz samples buffered while the user was speaking.
    /// </summary>
    public long VoicedSampleCount
    {
        get { lock (gate) return voicedSampleCount; }
    }

    /// <summary>
    /// Total number of 48 kHz samples received from the pipe, voiced or not.
    /// </summary>
    public long ReceivedSampleCount => Interlocked.Read(ref receivedSampleCount);

    public Task RunAsync(CancellationToken cancellationToken) => ReadLoopAsync(cancellationToken);

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var segment = result.Buffer;
                if (segment.Length > 0)
                    foreach (var seg in segment)
                        AppendChunk(MemoryMarshal.Cast<byte, short>(seg.Span));
                if (result.IsCompleted)
                    break;
                reader.AdvanceTo(segment.End);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown path
        }
    }

    private void AppendChunk(ReadOnlySpan<short> samples48k)
    {
        Interlocked.Add(ref receivedSampleCount, samples48k.Length);
        if (!isSpeakingProvider())
            return;

        var samples16k = resampler.Resample(samples48k);
        lock (gate)
        {
            voicedSampleCount += samples16k.Length;
            if (samples16k.Length == 0)
                return;

            var grown = new short[buffer.Length + samples16k.Length];
            buffer.CopyTo(grown);
            samples16k.CopyTo(grown.AsSpan(buffer.Length));
            buffer = grown;
            chunkStartUtc ??= DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Takes the buffered audio if it has been pending for at least <paramref name="minAge"/>.
    /// </summary>
    public bool TryTakePending(TimeSpan minAge, out short[] samples, out DateTime chunkStartUtc)
    {
        lock (gate)
        {
            if (this.chunkStartUtc is null
                || DateTime.UtcNow - this.chunkStartUtc.Value < minAge)
            {
                samples = [];
                chunkStartUtc = default;
                return false;
            }

            chunkStartUtc = this.chunkStartUtc.Value;
            samples = buffer;
            buffer = [];
            this.chunkStartUtc = null;
            return true;
        }
    }

    /// <summary>
    /// Takes all buffered audio regardless of age (used when the session ends).
    /// </summary>
    public (short[] Samples, DateTime? ChunkStartUtc) TakeAll()
    {
        lock (gate)
        {
            var samples = buffer;
            buffer = [];
            var start = chunkStartUtc;
            chunkStartUtc = null;
            return (samples, start);
        }
    }
}
