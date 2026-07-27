using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudTemporalJitterTests
{
    [Fact]
    public void Sequence_IsStableBoundedAndRepeats()
    {
        var firstPeriod = Enumerable.Range(0, PreviewCloudTemporalJitter.Period)
            .Select(PreviewCloudTemporalJitter.Sample)
            .ToArray();

        Assert.Equal(firstPeriod.Length, firstPeriod.Distinct().Count());
        Assert.All(firstPeriod, value => Assert.InRange(value, 0f, 1f));
        Assert.Equal(firstPeriod[0], PreviewCloudTemporalJitter.Sample(PreviewCloudTemporalJitter.Period));
    }

    [Fact]
    public void SamplingFrame_AdvancesWrapsAndFreezesOnlyForTemporalDisable()
    {
        Assert.Equal(18, PreviewCloudTemporalJitter.AdvanceFrame(17, temporalSamplingDisabled: false, 64));
        Assert.Equal(0, PreviewCloudTemporalJitter.AdvanceFrame(63, temporalSamplingDisabled: false, 64));
        Assert.Equal(17, PreviewCloudTemporalJitter.AdvanceFrame(17, temporalSamplingDisabled: true, 64));
    }
}
