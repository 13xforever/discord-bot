using System;
using System.Collections.Generic;
using System.Linq;
using CompatBot.EventHandlers.VoiceTranscription;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class PcmResamplerTests
{
    // the filter history starts empty, so the first outputs ramp up and are skipped in these tests
    private const int WarmupOutputs = 8;

    private static short[] Interleave(ReadOnlySpan<short> left, ReadOnlySpan<short> right)
    {
        var result = new short[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            result[i * 2] = left[i];
            result[i * 2 + 1] = right[i];
        }
        return result;
    }

    private static short[] InterleaveMono(ReadOnlySpan<short> frames)
        => Interleave(frames, frames);

    [Test]
    public void ConstantSignalStaysConstant()
    {
        var resampler = new PcmResampler();
        var input = InterleaveMono(Enumerable.Repeat((short)1000, 480).ToArray());
        var output = resampler.Resample(input);

        Assert.That(output, Has.Length.EqualTo(160));
        Assert.That(output.Skip(WarmupOutputs).All(s => s == 1000), Is.True);
    }

    [Test]
    public void ChannelsAreDownmixedNotInterleavedIntoTheSignal()
    {
        var resampler = new PcmResampler();
        // left and right out of phase by 180° must cancel out; a resampler that ignored the
        // channel pairing would happily produce a big signal out of this
        var input = Interleave(
            Enumerable.Repeat((short)1000, 480).ToArray(),
            Enumerable.Repeat((short)(-1000), 480).ToArray());
        var output = resampler.Resample(input);

        Assert.That(output, Is.All.EqualTo((short)0));
    }

    [Test]
    public void LowFrequenciesSurviveTheFilter()
    {
        const int frames = 4800; // 100 ms at 48 kHz
        var mono = new short[frames];
        for (var i = 0; i < frames; i++)
            mono[i] = (short)(Math.Sin(2 * Math.PI * 1000 * i / PcmResampler.SourceSampleRate) * 10000);

        var output = new PcmResampler().Resample(InterleaveMono(mono));
        var inputRms = Math.Sqrt(mono.Select(s => (double)s * s).Average());
        var outputRms = Math.Sqrt(output.Skip(WarmupOutputs).Select(s => (double)s * s).Average());

        Assert.That(outputRms / inputRms, Is.EqualTo(1).Within(0.05));
    }

    [Test]
    public void HighFrequenciesAreAttenuatedInsteadOfAliasingDown()
    {
        // a 24 kHz square wave is right at the source Nyquist frequency: a plain 3:1 average
        // would let a third of it through as a low frequency artifact
        var mono = new short[480];
        for (var i = 0; i < mono.Length; i++)
            mono[i] = i % 2 == 0 ? (short)1000 : (short)(-1000);

        var output = new PcmResampler().Resample(InterleaveMono(mono));
        var peak = output.Skip(WarmupOutputs).Max(s => Math.Abs((int)s));

        Assert.That(peak, Is.LessThan(200));
    }

    [Test]
    public void SplittingTheStreamAnywhereMakesNoDifference()
    {
        var source = new short[3000];
        var random = new Random(12345);
        for (var i = 0; i < source.Length; i++)
            source[i] = (short)random.Next(-10000, 10000);

        var whole = new PcmResampler().Resample(source);

        // 3000 interleaved samples is 1500 frames, so these split points cut through frames
        foreach (var chunk in new[] { 1, 7, 2, 999, 1998, 1 })
        {
            var resampler = new PcmResampler();
            var result = new List<short>();
            var offset = 0;
            while (offset < source.Length)
            {
                var length = Math.Min(chunk, source.Length - offset);
                result.AddRange(resampler.Resample(source.AsSpan(offset, length)));
                offset += length;
            }
            Assert.That(result, Is.EqualTo(whole), $"split at {chunk} samples");
        }
    }

    [Test]
    public void PartialFramesAreCarriedBetweenChunks()
    {
        var resampler = new PcmResampler();
        Assert.That(resampler.Resample([1000]), Is.Empty);
        Assert.That(resampler.ProcessedFrameCount, Is.Zero, "a dangling left channel is not a frame yet");

        resampler.Resample([2000, 500]);
        Assert.That(resampler.ProcessedFrameCount, Is.EqualTo(1), "the dangling sample completes the first frame");
    }

    [Test]
    public void EmptyInputProducesNoOutput()
    {
        var resampler = new PcmResampler();
        Assert.That(resampler.Resample([]), Is.Empty);
        Assert.That(resampler.ProcessedFrameCount, Is.Zero);
    }

    [Test]
    public void ProcessedFrameCountTracksCompleteFramesOnly()
    {
        var resampler = new PcmResampler();
        resampler.Resample([1, 2, 3, 4, 5]);
        Assert.That(resampler.ProcessedFrameCount, Is.EqualTo(2)); // the dangling fifth sample waits

        resampler.Resample([6]);
        Assert.That(resampler.ProcessedFrameCount, Is.EqualTo(3));
    }
}
