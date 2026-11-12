using System;
using System.Collections.Generic;
using System.Linq;
using CompatBot.EventHandlers.VoiceTranscription;
using NUnit.Framework;
using SpeexResamplerSharp;

namespace Tests;

[TestFixture]
public class PcmResamplerTests
{
    // pinned so these tests don't follow the config default; the ramp-up length depends on the filter
    private const ResamplerQuality Quality = ResamplerQuality.Voice;
    private const int InputFrames = 4800;

    private static short[] Interleave(ReadOnlySpan<short> left, ReadOnlySpan<short> right)
    {
        var result = new short[left.Length * PcmResampler.Channels];
        for (var i = 0; i < left.Length; i++)
        {
            result[i * PcmResampler.Channels] = left[i];
            result[i * PcmResampler.Channels + 1] = right[i];
        }
        return result;
    }

    private static short[] InterleaveMono(ReadOnlySpan<short> frames)
        => Interleave(frames, frames);

    /// <summary>
    /// Runs the whole stream through, giving the resampler the capacity it asked for.
    /// </summary>
    private static List<short> ResampleAll(PcmResampler resampler, ReadOnlySpan<short> interleaved)
    {
        var result = new List<short>();
        var destination = new short[resampler.MaxOutputFrames(interleaved.Length)];
        var written = resampler.Resample(interleaved, destination, out var framesProcessed);
        Assert.That(framesProcessed, Is.EqualTo(interleaved.Length / PcmResampler.Channels), "everything must be consumed");
        result.AddRange(destination.AsSpan(0, written));
        return result;
    }

    private static List<short> ResampleChunked(PcmResampler resampler, ReadOnlySpan<short> interleaved, int chunkSamples)
    {
        var result = new List<short>();
        var destination = new short[resampler.MaxOutputFrames(chunkSamples)];
        for (var offset = 0; offset < interleaved.Length; offset += chunkSamples)
        {
            var length = Math.Min(chunkSamples, interleaved.Length - offset);
            var written = resampler.Resample(interleaved.Slice(offset, length), destination, out _);
            result.AddRange(destination.AsSpan(0, written));
        }
        return result;
    }

    [Test]
    public void ConstantSignalStaysConstant()
    {
        var resampler = new PcmResampler(quality: Quality);
        var steady = ResampleAll(resampler, InterleaveMono(Enumerable.Repeat((short)1000, InputFrames).ToArray()))
            .Skip(resampler.OutputLatency * 2);

        Assert.That(steady, Is.Not.Empty);
        Assert.That(steady.All(s => Math.Abs(s - 1000) <= 2), Is.True);
    }

    [Test]
    public void ChannelsAreDownmixedNotInterleavedIntoTheSignal()
    {
        var resampler = new PcmResampler(quality: Quality);
        // left and right out of phase by 180° must cancel out; anything that ignored the channel
        // pairing would happily make a big signal out of this
        var output = ResampleAll(resampler, Interleave(
            Enumerable.Repeat((short)1000, InputFrames).ToArray(),
            Enumerable.Repeat((short)(-1000), InputFrames).ToArray()));

        Assert.That(output, Is.All.EqualTo((short)0));
    }

    [Test]
    public void LowFrequenciesSurviveTheFilter()
    {
        var mono = new short[InputFrames];
        for (var i = 0; i < mono.Length; i++)
            mono[i] = (short)(Math.Sin(2 * Math.PI * 1000 * i / PcmResampler.SourceSampleRate) * 10000);

        var resampler = new PcmResampler(quality: Quality);
        var output = ResampleAll(resampler, InterleaveMono(mono)).Skip(resampler.OutputLatency * 2);
        var inputRms = Math.Sqrt(mono.Select(s => (double)s * s).Average());
        var outputRms = Math.Sqrt(output.Select(s => (double)s * s).Average());

        Assert.That(outputRms / inputRms, Is.EqualTo(1).Within(0.05));
    }

    [Test]
    public void HighFrequenciesAreStoppedRatherThanAliasedDown()
    {
        // a 24 kHz square wave sits right at the source Nyquist frequency: without a real low-pass
        // filter a third of it comes back as a low frequency artifact after 3:1 decimation
        var mono = new short[InputFrames];
        for (var i = 0; i < mono.Length; i++)
            mono[i] = i % 2 == 0 ? (short)1000 : (short)(-1000);

        var resampler = new PcmResampler(quality: Quality);
        var peak = ResampleAll(resampler, InterleaveMono(mono))
            .Skip(resampler.OutputLatency * 4)
            .Max(s => Math.Abs((int)s));

        Assert.That(peak, Is.LessThan(30));
    }

    [Test]
    public void SplittingTheStreamAnywhereMakesNoDifference()
    {
        var source = new short[3000];
        var random = new Random(12345);
        for (var i = 0; i < source.Length; i++)
            source[i] = (short)random.Next(-10000, 10000);

        var whole = ResampleAll(new PcmResampler(quality: Quality), source);
        Assert.That(whole, Is.Not.Empty);

        // 3000 interleaved samples is 1500 frames, so these chunks cut through frames
        foreach (var chunk in new[] { 1, 7, 2, 999, 1998, 2999 })
            Assert.That(ResampleChunked(new PcmResampler(quality: Quality), source, chunk), Is.EqualTo(whole), $"chunked at {chunk} samples");
    }

    [Test]
    public void PartialFramesWaitForTheRestOfThePair()
    {
        var resampler = new PcmResampler(quality: Quality);
        var destination = new short[resampler.MaxOutputFrames(4)];

        Assert.That(resampler.Resample([1000], destination, out var framesProcessed), Is.Zero);
        Assert.That(framesProcessed, Is.Zero, "a dangling left channel is not a frame yet");

        resampler.Resample([2000, 500, 500], destination, out framesProcessed);
        Assert.That(framesProcessed, Is.EqualTo(2), "the dangling sample completes the first frame");
    }

    [Test]
    public void DrainingIsWhatTheFilterWasHolding()
    {
        var source = new short[2000];
        var random = new Random(999);
        for (var i = 0; i < source.Length; i++)
            source[i] = (short)random.Next(-12000, 12000);

        // running the tail of the stream out of the filter has to be the same as feeding the same
        // amount of real silence through it
        var drainedResampler = new PcmResampler(quality: Quality);
        var buffered = ResampleAll(drainedResampler, source);
        var drained = new short[drainedResampler.MaxDrainFrames];
        var drainedCount = drainedResampler.Drain(drained);

        var silenceFrames = 2 * drainedResampler.InputLatency;
        var padded = new short[source.Length + silenceFrames * PcmResampler.Channels];
        Array.Copy(source, padded, source.Length);
        var reference = ResampleAll(new PcmResampler(quality: Quality), padded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drainedCount, Is.GreaterThan(0), "the filter must give something back");
            Assert.That(drainedCount, Is.EqualTo(reference.Count - buffered.Count), "draining must produce exactly what a padded stream produces");
            Assert.That(buffered.Concat(drained[..drainedCount]), Is.EqualTo(reference));
        }
    }

    [Test]
    public void EmptyInputMakesNoFrames()
    {
        var resampler = new PcmResampler(quality: Quality);
        Assert.That(resampler.Resample([], new short[resampler.MaxOutputFrames(0)], out var framesProcessed), Is.Zero);
        Assert.That(framesProcessed, Is.Zero);
    }

    [Test]
    public void LatencyMatchesTheFilterLength()
    {
        var resampler = new PcmResampler(quality: Quality);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resampler.InputLatency, Is.GreaterThan(0));
            Assert.That(resampler.OutputLatency, Is.GreaterThan(0));
            Assert.That(resampler.OutputLatency, Is.LessThanOrEqualTo(resampler.InputLatency));
            Assert.That(resampler.MaxDrainFrames, Is.GreaterThan(resampler.OutputLatency));
            Assert.That(resampler.Quality, Is.EqualTo(Quality));
        }
    }
}
