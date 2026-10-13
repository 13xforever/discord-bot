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
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Test]
    public async Task BuffersVoicedAudio()
    {
        var pipe = new Pipe();
        var buffer = new UserAudioBuffer(pipe.Reader, () => true);
        var runTask = buffer.RunAsync(CancellationToken.None);
        await pipe.Writer.WriteAsync(ToBytes(Enumerable.Repeat((short)100, 480).ToArray())).ConfigureAwait(false);
        await WaitForAsync(() => buffer.VoicedSampleCount == 160).ConfigureAwait(false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.TryTakePending(TimeSpan.Zero, out var samples, out var startUtc), Is.True);
            Assert.That(samples, Has.Length.EqualTo(160));
            Assert.That(samples.All(s => s == 100), Is.True);
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
        var buffer = new UserAudioBuffer(pipe.Reader, () => false);
        var runTask = buffer.RunAsync(CancellationToken.None);

        await pipe.Writer.WriteAsync(ToBytes(Enumerable.Repeat((short)100, 480).ToArray())).ConfigureAwait(false);
        await WaitForAsync(() => buffer.ReceivedSampleCount == 480).ConfigureAwait(false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.VoicedSampleCount, Is.EqualTo(0));
            Assert.That(buffer.TryTakePending(TimeSpan.Zero, out _, out _), Is.False);
        }

        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
        await runTask.ConfigureAwait(false);
    }
}
