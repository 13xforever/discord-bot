using System.Linq;
using CompatBot.EventHandlers.VoiceTranscription;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class PcmResamplerTests
{
    [Test]
    public void ConstantSignalStaysConstant()
    {
        var resampler = new PcmResampler();
        var input = Enumerable.Repeat((short)1000, 480).ToArray();
        var output = resampler.Resample(input);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(output, Has.Length.EqualTo(160));
            Assert.That(output.All(s => s == 1000), Is.True);
        }
    }

    [Test]
    public void AveragesTriplets()
    {
        var resampler = new PcmResampler();
        var output = resampler.Resample([0, 3, 6, 9, 12, 15]);
        Assert.That(output, Is.EqualTo(new short[] { 3, 12 }));
    }

    [Test]
    public void CarriesRemainderBetweenCalls()
    {
        var resampler = new PcmResampler();
        Assert.That(resampler.Resample([1, 2]), Is.Empty);

        var output = resampler.Resample([3, 4, 5]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(output, Has.Length.EqualTo(1));
            Assert.That(output[0], Is.EqualTo((short)2)); // (1+2+3)/3
        }
    }

    [Test]
    public void EmptyInputProducesNoOutput()
    {
        var resampler = new PcmResampler();
        Assert.That(resampler.Resample([]), Is.Empty);
    }
}
