using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudLayerEnvelopeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Build_StacksDecksWithConfiguredGaps(int deckCount)
    {
        var stack = PreviewCloudLayerEnvelope.Build(
            groundWorldY: -0.56f,
            layerWorldY: 18f,
            volumeHeight: 24f,
            volumeSize: 48f,
            cirrusStrength: 0.45f,
            cumulusLayerCount: deckCount,
            interDeckGap: 12f,
            heightVariance: 6f,
            upperThicknessScale: 0.65f,
            cirrusGap: 120f,
            cirrusThickness: 2.5f);

        Assert.Equal(deckCount, stack.DeckCount);
        AssertClose(18.56f, stack.Deck0.BaseAltitude);
        AssertClose(42.56f, stack.Deck0.TopAltitude);

        var top = stack.Deck0.TopAltitude;
        if (deckCount >= 2)
        {
            AssertClose(top + 12f, stack.Deck1.BaseAltitude);
            AssertClose(stack.Deck1.BaseAltitude + 24f * 0.65f, stack.Deck1.TopAltitude);
            top = stack.Deck1.TopAltitude;
        }

        if (deckCount >= 3)
        {
            AssertClose(top + 12f, stack.Deck2.BaseAltitude);
            AssertClose(stack.Deck2.BaseAltitude + 24f * 0.65f, stack.Deck2.TopAltitude);
            top = stack.Deck2.TopAltitude;
        }

        Assert.InRange(
            stack.CumulusSupportBase,
            stack.Deck0.BaseAltitude - MathF.Max(stack.HeightVariance * 0.25f, 2f),
            stack.Deck0.BaseAltitude + 1e-3f);
        // Soft pad may lift support slightly above the top deck; keep it near the deck top.
        Assert.InRange(
            stack.CumulusSupportTop,
            top - 1e-3f,
            top + MathF.Max(stack.HeightVariance * 0.25f, 2f));
        AssertClose(top + 120f, stack.CirrusBaseAltitude);
        AssertClose(2.5f, stack.CirrusThickness);
        AssertClose(stack.CirrusBaseAltitude + 2.5f, stack.CirrusTopAltitude);
    }

    [Fact]
    public void Build_DisablesCirrusEnvelopeWhenStrengthIsZero()
    {
        var stack = PreviewCloudLayerEnvelope.Build(
            groundWorldY: -0.56f,
            layerWorldY: 18f,
            volumeHeight: 24f,
            volumeSize: 48f,
            cirrusStrength: 0f,
            cumulusLayerCount: 2);

        AssertClose(stack.Deck1.TopAltitude, stack.CirrusBaseAltitude);
        AssertClose(0f, stack.CirrusThickness);
        AssertClose(stack.CirrusBaseAltitude, stack.CirrusTopAltitude);
    }

    private static void AssertClose(float expected, float actual) =>
        Assert.InRange(actual, expected - 1e-3f, expected + 1e-3f);
}
