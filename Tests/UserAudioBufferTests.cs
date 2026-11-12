using System;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CompatBot.EventHandlers.VoiceTranscription;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class UserAudioBufferTests
{
    // 480 stereo frames is 960 interleaved samples, which decimates down to 160 mono samples
    private const int FramesPerBurst = 480;
    private const int DecimatedPerBurst = FramesPerBurst / PcmResampler.DecimationFactor;
    private const string Tag = "voice[test]";

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for condition");
            await Task.Delay(25);
        }
    }

    private static byte[] ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static short[] StereoBurst(short value)
    {
        var samples = new short[FramesPerBurst * PcmResampler.Channels];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = value;
        return samples;
    }

    /// <summary>
    /// Asymmetric noise: a sample that gets paired with the wrong half of its frame changes the
    /// filtered output, which is what makes the segment boundary test below meaningful.
    /// </summary>
    private static short[] VaryingStereoBurst()
    {
        var samples = new short[FramesPerBurst * PcmResampler.Channels];
        var random = new Random(20261111);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)random.Next(-20000, 20000);
        return samples;
    }

    [Test]
    public async Task BuffersVoicedAudio()
    {
        var pipe = new Pipe();
        var buffer = new UserAudioBuffer(pipe.Reader, () => true, Tag);
        var runTask = buffer.RunAsync(CancellationToken.None);

        await pipe.Writer.WriteAsync(ToBytes(StereoBurst(100))).ConfigureAwait(false);
        await WaitForAsync(() => buffer.ReceivedFrameCount >= FramesPerBurst).ConfigureAwait(false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.ReceivedFrameCount, Is.EqualTo(FramesPerBurst));
            Assert.That(buffer.BufferedSampleCount, Is.EqualTo(DecimatedPerBurst));
            Assert.That(buffer.TryTakePending(TimeSpan.Zero, out var samples, out var startUtc), Is.True);
            Assert.That(samples, Has.Length.EqualTo(DecimatedPerBurst));
            // the filter has to fill up before it reaches steady state, so only the tail is checked
            Assert.That(samples.Skip(DecimatedPerBurst / 2).All(s => Math.Abs(s - 100) <= 2), Is.True);
            Assert.That(Math.Abs((startUtc - DateTime.UtcNow).TotalSeconds), Is.LessThan(5));
        }

        Assert.That(buffer.TryTakePending(TimeSpan.Zero, out _, out _), Is.False);

        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        await runTask.ConfigureAwait(false);
    }

    [Test]
    public async Task IgnoresSilentAudio()
    {
        var pipe = new Pipe();
        var buffer = new UserAudioBuffer(pipe.Reader, () => false, Tag);
        var runTask = buffer.RunAsync(CancellationToken.None);

        await pipe.Writer.WriteAsync(ToBytes(StereoBurst(100))).ConfigureAwait(false);
        await WaitForAsync(() => buffer.ReceivedFrameCount >= FramesPerBurst).ConfigureAwait(false);

        using (Assert.EnterMultipleScope())
        {
            // the frames are still counted, they're what proves the connection is alive
            Assert.That(buffer.ReceivedFrameCount, Is.EqualTo(FramesPerBurst));
            Assert.That(buffer.BufferedSampleCount, Is.Zero);
            Assert.That(buffer.TryTakePending(TimeSpan.Zero, out _, out _), Is.False);
        }

        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        await runTask.ConfigureAwait(false);
    }

    [Test]
    public async Task PendingAudioYoungerThanTheMinimumAgeIsLeftAlone()
    {
        var pipe = new Pipe();
        var buffer = new UserAudioBuffer(pipe.Reader, () => true, Tag);
        var runTask = buffer.RunAsync(CancellationToken.None);

        await pipe.Writer.WriteAsync(ToBytes(StereoBurst(100))).ConfigureAwait(false);
        await WaitForAsync(() => buffer.ReceivedFrameCount >= FramesPerBurst).ConfigureAwait(false);

        Assert.That(buffer.TryTakePending(TimeSpan.FromMinutes(5), out _, out _), Is.False);
        Assert.That(buffer.BufferedSampleCount, Is.EqualTo(DecimatedPerBurst));

        var (samples, _) = buffer.TakeAll();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples, Has.Length.EqualTo(DecimatedPerBurst));
            Assert.That(buffer.BufferedSampleCount, Is.Zero);
        }

        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        await runTask.ConfigureAwait(false);
    }

    [Test]
    public async Task MisalignedPipeSegmentsMakeNoDifference()
    {
        var payload = ToBytes(VaryingStereoBurst());

        var aligned = await ReadAllAsync(new Pipe(), payload, [payload.Length]);
        var misaligned = await ReadAllAsync(new Pipe(), payload, [3, 1, 5, 2, 7, 4]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(misaligned.Frames, Is.EqualTo(FramesPerBurst));
            Assert.That(misaligned.Samples, Has.Length.EqualTo(aligned.Samples.Length));
            Assert.That(misaligned.Samples, Is.EqualTo(aligned.Samples));
        }
    }

    [Test]
    public async Task DrainingOnTheLastTakeRecoversWhatTheFilterHolds()
    {
        var plain = await ReadAllAsync(new Pipe(), ToBytes(StereoBurst(100)), [int.MaxValue], drain: false);
        var drained = await ReadAllAsync(new Pipe(), ToBytes(StereoBurst(100)), [int.MaxValue], drain: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plain.Samples.Length, Is.EqualTo(DecimatedPerBurst));
            Assert.That(drained.Samples.Length, Is.GreaterThan(plain.Samples.Length),
                "the samples the filter still holds must come out when the stream is dropped for good");
            Assert.That(drained.Samples[..plain.Samples.Length], Is.EqualTo(plain.Samples));
        }
    }

    private static async Task<(long Frames, short[] Samples)> ReadAllAsync(Pipe pipe, byte[] payload, int[] chunkSizes, bool drain = false)
    {
        var buffer = new UserAudioBuffer(pipe.Reader, () => true, Tag);
        var runTask = buffer.RunAsync(CancellationToken.None);

        var offset = 0;
        var chunkIndex = 0;
        while (offset < payload.Length)
        {
            var length = Math.Min(chunkSizes[chunkIndex++ % chunkSizes.Length], payload.Length - offset);
            await pipe.Writer.WriteAsync(payload.AsMemory(offset, length)).ConfigureAwait(false);
            offset += length;
        }

        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        await runTask.ConfigureAwait(false);
        var (samples, _) = buffer.TakeAll(drain);
        return (buffer.ReceivedFrameCount, samples);
    }

    [Test]
    public async Task EverythingStillInFlightIsDrainedWhenTheWriterCompletes()
    {
        var pipe = new Pipe();
        var buffer = new UserAudioBuffer(pipe.Reader, () => true, Tag);
        var runTask = buffer.RunAsync(CancellationToken.None);

        for (var i = 0; i < 5; i++)
            await pipe.Writer.WriteAsync(ToBytes(StereoBurst(100))).ConfigureAwait(false);
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        await runTask.ConfigureAwait(false);

        Assert.That(buffer.ReceivedFrameCount, Is.EqualTo(FramesPerBurst * 5));
    }

    [Test]
    public async Task CancellingReleasesThePipeForTheWritingSide()
    {
        var pipe = new Pipe();
        var buffer = new UserAudioBuffer(pipe.Reader, () => true, Tag);
        using var cts = new CancellationTokenSource();
        var runTask = buffer.RunAsync(cts.Token);

        await pipe.Writer.WriteAsync(ToBytes(StereoBurst(100))).ConfigureAwait(false);
        await WaitForAsync(() => buffer.ReceivedFrameCount >= FramesPerBurst).ConfigureAwait(false);

        cts.Cancel();
        await runTask.ConfigureAwait(false);

        // an abandoned pipe would keep the sender buffering audio forever, so the writing side has
        // to be told the pipe is over: either a completed write result or a throw both count
        bool released;
        try
        {
            var writeResult = await pipe.Writer.WriteAsync(ToBytes(StereoBurst(100))).ConfigureAwait(false);
            released = writeResult.IsCompleted;
        }
        catch (Exception)
        {
            released = true;
        }
        Assert.That(released, Is.True, "the read loop must complete the pipe it reads from");
    }
}
