namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CPU reference for the CQ1.8 four-tap edge-classification contract used by the
/// full-resolution cloud repair shader.
/// </summary>
internal static class PreviewCloudEdgeRepairClassifier
{
    public const float AlphaRangeThreshold = 0.08f;
    public const float MinimumDistanceRangeThreshold = 0.75f;
    public const float RelativeDistanceRangeThreshold = 0.01f;
    public const float MinimumValidWeight = 0.75f;
    public const float KindRangeThreshold = 0.24f;
    public const int RepairStepCount = 8;

    internal readonly record struct Tap(
        float Alpha,
        bool MetadataValid,
        float RepresentativeDistance,
        float CloudKind,
        float ValidWeight);

    internal readonly record struct Result(
        bool ShouldRepair,
        bool AlphaEdge,
        bool DistanceEdge,
        bool ValidityEdge,
        bool KindEdge,
        bool LowValidWeight);

    public static Result Classify(ReadOnlySpan<Tap> taps, bool shellIntersects)
    {
        if (!shellIntersects || taps.IsEmpty)
        {
            return default;
        }

        var alphaMin = float.PositiveInfinity;
        var alphaMax = float.NegativeInfinity;
        var distanceMin = float.PositiveInfinity;
        var distanceMax = float.NegativeInfinity;
        var kindMin = float.PositiveInfinity;
        var kindMax = float.NegativeInfinity;
        var validCount = 0;
        var validWeight = 0f;

        foreach (var tap in taps)
        {
            alphaMin = Math.Min(alphaMin, Math.Max(tap.Alpha, 0f));
            alphaMax = Math.Max(alphaMax, Math.Max(tap.Alpha, 0f));
            if (!tap.MetadataValid)
            {
                continue;
            }

            validCount++;
            distanceMin = Math.Min(distanceMin, Math.Max(tap.RepresentativeDistance, 0f));
            distanceMax = Math.Max(distanceMax, Math.Max(tap.RepresentativeDistance, 0f));
            kindMin = Math.Min(kindMin, tap.CloudKind);
            kindMax = Math.Max(kindMax, tap.CloudKind);
            validWeight += Math.Clamp(tap.ValidWeight, 0f, 1f);
        }

        var alphaEdge = alphaMax - alphaMin > AlphaRangeThreshold;
        var distanceEdge = validCount > 1 &&
            distanceMax - distanceMin >
            Math.Max(MinimumDistanceRangeThreshold, distanceMin * RelativeDistanceRangeThreshold);
        var validityEdge = validCount > 0 && validCount < taps.Length;
        var kindEdge = validCount > 1 && kindMax - kindMin > KindRangeThreshold;
        var normalizedValidWeight = validWeight / taps.Length;
        var lowValidWeight = normalizedValidWeight < MinimumValidWeight;
        var shouldRepair =
            alphaEdge || distanceEdge || validityEdge || kindEdge || lowValidWeight;

        return new Result(
            shouldRepair,
            alphaEdge,
            distanceEdge,
            validityEdge,
            kindEdge,
            lowValidWeight);
    }
}
