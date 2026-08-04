using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Conservative world-space altitude envelope used by the CQ3 cloud-light cache.
/// Altitudes are measured vertically from the continuous world's ground datum.
/// </summary>
public readonly record struct PreviewCloudLightAltitudeBounds(
    float CumulusBaseAltitude,
    float CumulusTopAltitude,
    float CirrusBaseAltitude,
    float CirrusTopAltitude,
    float DetailPadding,
    float PrimaryCumulusBaseAltitude = 0f,
    float PrimaryCumulusTopAltitude = 0f)
{
    /// <summary>Support-band base used for light-depth coverage (all decks + variance).</summary>
    public float MinimumAltitude => CumulusBaseAltitude - DetailPadding;

    public float MaximumAltitude =>
        MathF.Max(CumulusTopAltitude, CirrusTopAltitude) + DetailPadding;

    /// <summary>Primary deck base for density sampling (deck stacking origin).</summary>
    public float DensityCumulusBaseAltitude =>
        PrimaryCumulusBaseAltitude > 0f ? PrimaryCumulusBaseAltitude : CumulusBaseAltitude;

    /// <summary>Primary deck top for density sampling (primary thickness only).</summary>
    public float DensityCumulusTopAltitude =>
        PrimaryCumulusTopAltitude > 0f ? PrimaryCumulusTopAltitude : CumulusTopAltitude;

    public static PreviewCloudLightAltitudeBounds Create(
        float groundWorldY,
        float layerWorldY,
        float volumeHeight,
        float volumeSize,
        float cirrusStrength,
        int cumulusLayerCount = PreviewCloudLayerEnvelope.DefaultCumulusLayerCount,
        float interDeckGap = PreviewCloudLayerEnvelope.DefaultInterDeckGap,
        float heightVariance = PreviewCloudLayerEnvelope.DefaultHeightVariance,
        float upperThicknessScale = PreviewCloudLayerEnvelope.DefaultUpperThicknessScale,
        float cirrusGap = PreviewCloudLayerEnvelope.DefaultCirrusGap,
        float cirrusThickness = PreviewCloudLayerEnvelope.DefaultCirrusThickness)
    {
        var stack = PreviewCloudLayerEnvelope.Build(
            groundWorldY,
            layerWorldY,
            volumeHeight,
            volumeSize,
            cirrusStrength,
            cumulusLayerCount,
            interDeckGap,
            heightVariance,
            upperThicknessScale,
            cirrusGap,
            cirrusThickness);
        return FromStack(stack);
    }

    public static PreviewCloudLightAltitudeBounds FromStack(
        in PreviewCloudLayerEnvelope.AltitudeStack stack) =>
        new(
            stack.CumulusSupportBase,
            stack.CumulusSupportTop,
            stack.CirrusBaseAltitude,
            stack.CirrusTopAltitude,
            stack.DetailPadding,
            stack.Deck0.BaseAltitude,
            stack.Deck0.TopAltitude);
}


/// <summary>
/// Conservative light-axis interval covering a cascade's world footprint and complete cloud
/// altitude envelope. Flat layers have no footprint-dependent curvature allowance.
/// </summary>
public readonly record struct PreviewCloudLightDepthInterval(float Minimum, float Maximum)
{
    public float Span => Maximum - Minimum;

    public static PreviewCloudLightDepthInterval Create(
        in PreviewCloudLightBasis basis,
        in PreviewCloudLightCascadeProfile profile,
        Vector3 cameraGroundProjection,
        in PreviewCloudLightAltitudeBounds altitudeBounds,
        float groundWorldY)
    {
        if (!profile.IsEnabled)
        {
            throw new ArgumentException("Cloud-light cascade profile is disabled.", nameof(profile));
        }

        // A light-plane square can project to a world-XZ radius up to halfSpan*sqrt(2).
        var horizontalHalfExtent =
            profile.WorldSpan * 0.5f * MathF.Sqrt(2f) + altitudeBounds.DetailPadding;
        var worldMinY = groundWorldY + altitudeBounds.MinimumAltitude;
        var worldMaxY = groundWorldY + altitudeBounds.MaximumAltitude;
        var lightForward = basis.Forward;

        var minDepth = float.PositiveInfinity;
        var maxDepth = float.NegativeInfinity;
        for (var xSign = -1; xSign <= 1; xSign += 2)
        {
            for (var zSign = -1; zSign <= 1; zSign += 2)
            {
                var x = cameraGroundProjection.X + horizontalHalfExtent * xSign;
                var z = cameraGroundProjection.Z + horizontalHalfExtent * zSign;
                AccumulateDepth(new Vector3(x, worldMinY, z));
                AccumulateDepth(new Vector3(x, worldMaxY, z));
            }
        }

        // One small guard prevents half-float filtering at the terminal slice from observing
        // an exactly clipped boundary after light-depth snapping.
        var rawSpan = maxDepth - minDepth;
        var guard = MathF.Max(
            MathF.Max(profile.TexelWorldSize, altitudeBounds.DetailPadding * 0.25f),
            rawSpan * 2f / profile.Depth);
        return new PreviewCloudLightDepthInterval(minDepth - guard, maxDepth + guard);

        void AccumulateDepth(Vector3 world)
        {
            var depth = Vector3.Dot(world, lightForward);
            minDepth = MathF.Min(minDepth, depth);
            maxDepth = MathF.Max(maxDepth, depth);
        }
    }
}

public readonly record struct PreviewCloudLightSampleWeights(
    float Near,
    float Far,
    float ShortMarch)
{
    public float Sum => Near + Far + ShortMarch;
}

/// <summary>CQ3 near/far selection with the documented outer-near overlap.</summary>
public static class PreviewCloudLightCascadeBlend
{
    public static PreviewCloudLightSampleWeights Select(
        in PreviewCloudLightCascadeTransform near,
        in PreviewCloudLightCascadeTransform far,
        Vector3 world,
        float nearOverlapFraction)
    {
        var nearUnit = near.WorldToUnit(world);
        var farUnit = far.WorldToUnit(world);
        var nearInside = IsInside(nearUnit);
        var farInside = IsInside(farUnit);

        if (!nearInside)
        {
            return farInside
                ? new PreviewCloudLightSampleWeights(0f, 1f, 0f)
                : new PreviewCloudLightSampleWeights(0f, 0f, 1f);
        }

        if (!farInside)
        {
            return new PreviewCloudLightSampleWeights(1f, 0f, 0f);
        }

        var overlap = Math.Clamp(nearOverlapFraction, 0.001f, 0.999f);
        var edge = MathF.Max(
            MathF.Abs(nearUnit.X - 0.5f),
            MathF.Abs(nearUnit.Y - 0.5f)) * 2f;
        var blend = SmoothStep(1f - overlap, 1f, edge);
        return new PreviewCloudLightSampleWeights(1f - blend, blend, 0f);
    }

    private static bool IsInside(Vector3 unit) =>
        unit.X is >= 0f and <= 1f &&
        unit.Y is >= 0f and <= 1f &&
        unit.Z is >= 0f and <= 1f;

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var x = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
