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
}
