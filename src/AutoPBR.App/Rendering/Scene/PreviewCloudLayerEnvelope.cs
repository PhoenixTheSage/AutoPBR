namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Shared multi-deck cumulus + cirrus altitude stack used by the view march, repair pass,
/// and CQ3 cloud-light cache. Altitudes are measured vertically from the continuous world's
/// ground datum.
/// </summary>
public static class PreviewCloudLayerEnvelope
{
    public const int MaxCumulusDecks = 3;
    public const int DefaultCumulusLayerCount = 2;
    public const float DefaultInterDeckGap = 12f;
    public const float DefaultHeightVariance = 6f;
    public const float DefaultUpperThicknessScale = 0.65f;
    public const float DefaultUpperCoverageScale = 0.70f;
    public const float DefaultUpperDensityScale = 0.85f;
    public const float DefaultUpperWindSpeedScale = 1.35f;
    public const float DefaultCirrusGap = 120f;
    public const float DefaultCirrusThickness = 2.5f;
    public const float UpperDeckHeadingVeerDegrees = 8f;

    public readonly record struct DeckBand(float BaseAltitude, float TopAltitude)
    {
        public float Thickness => MathF.Max(TopAltitude - BaseAltitude, 0.01f);
    }

    public readonly record struct AltitudeStack(
        int DeckCount,
        DeckBand Deck0,
        DeckBand Deck1,
        DeckBand Deck2,
        float HeightVariance,
        float CumulusSupportBase,
        float CumulusSupportTop,
        float CirrusBaseAltitude,
        float CirrusThickness,
        float CirrusTopAltitude,
        float DetailPadding)
    {
        public float MinimumAltitude => CumulusSupportBase - DetailPadding;

        public float MaximumAltitude =>
            MathF.Max(CumulusSupportTop, CirrusTopAltitude) + DetailPadding;
    }

    public static int ClampDeckCount(int count) => Math.Clamp(count, 1, MaxCumulusDecks);

    public static AltitudeStack Build(
        float groundWorldY,
        float layerWorldY,
        float volumeHeight,
        float volumeSize,
        float cirrusStrength,
        int cumulusLayerCount = DefaultCumulusLayerCount,
        float interDeckGap = DefaultInterDeckGap,
        float heightVariance = DefaultHeightVariance,
        float upperThicknessScale = DefaultUpperThicknessScale,
        float cirrusGap = DefaultCirrusGap,
        float cirrusThickness = DefaultCirrusThickness)
    {
        var safeHeight = MathF.Max(volumeHeight, 0.01f);
        var variance = MathF.Max(heightVariance, 0f);
        var gap = MathF.Max(interDeckGap, 0f);
        var upperScale = MathF.Max(upperThicknessScale, 0.05f);
        var decks = ClampDeckCount(cumulusLayerCount);

        var deck0Base = MathF.Max(layerWorldY - groundWorldY, 0.01f);
        var deck0Top = deck0Base + safeHeight;
        var deck0 = new DeckBand(deck0Base, deck0Top);

        var deck1 = default(DeckBand);
        var deck2 = default(DeckBand);
        var topDeckTop = deck0Top;

        if (decks >= 2)
        {
            var deck1Base = topDeckTop + gap;
            var deck1Top = deck1Base + safeHeight * upperScale;
            deck1 = new DeckBand(deck1Base, deck1Top);
            topDeckTop = deck1Top;
        }

        if (decks >= 3)
        {
            var deck2Base = topDeckTop + gap;
            var deck2Top = deck2Base + safeHeight * upperScale;
            deck2 = new DeckBand(deck2Base, deck2Top);
            topDeckTop = deck2Top;
        }

        // Occupancy stays on the fixed deck band; height variance only remaps the
        // vertical profile. Keep a small soft pad for light-cache filtering, not the
        // full variance amplitude (that formerly inflated the march lattice).
        var softPad = MathF.Min(variance * 0.2f, safeHeight * 0.08f);
        var supportBase = MathF.Max(deck0Base - softPad, 0.001f);
        var supportTop = topDeckTop + softPad;

        var safeCirrusGap = MathF.Max(cirrusGap, 0f);
        var safeCirrusThickness = MathF.Max(cirrusThickness, 0.1f);
        var cirrusBase = topDeckTop + safeCirrusGap;
        var cirrusTop = cirrusBase + safeCirrusThickness;
        if (cirrusStrength <= 0f)
        {
            cirrusBase = topDeckTop;
            cirrusTop = topDeckTop;
            safeCirrusThickness = 0f;
        }

        // CQ2 detail repeats every half volume scale. Retain that full period at both
        // boundaries so array filtering and rotated edge detail cannot clip the envelope.
        var detailPadding = MathF.Max(volumeSize, 8f) * 0.5f;
        return new AltitudeStack(
            decks,
            deck0,
            deck1,
            deck2,
            variance,
            supportBase,
            supportTop,
            cirrusBase,
            safeCirrusThickness,
            cirrusTop,
            detailPadding);
    }
}
