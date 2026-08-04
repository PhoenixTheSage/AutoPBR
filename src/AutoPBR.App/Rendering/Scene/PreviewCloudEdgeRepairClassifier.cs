namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CPU reference for the CQ1.8 four-tap edge-classification contract used by the
/// full-resolution cloud repair shader.
/// </summary>
internal static class PreviewCloudEdgeRepairClassifier
{
    // CQ1.8 used 0.08 for pre-CA1 smooth shells. CA1/CA2 boundary breakup creates
    // legitimate high-frequency opacity variation across the 2/3-res tap footprint;
    // treating that as a reconstruction failure replaces temporal history with a
    // noisy eight-step retrace and makes Cinematic look worse than High.
    public const float AlphaRangeThreshold = 0.24f;
    public const float StrongAlphaRangeThreshold = 0.36f;
    public const float SilhouetteAlphaCeiling = 0.18f;
    public const float MinimumDistanceRangeThreshold = 0.75f;
    public const float RelativeDistanceRangeThreshold = 0.01f;
    public const float MinimumValidWeight = 0.75f;
    public const float KindRangeThreshold = 0.24f;
    public const int RepairStepCount = 8;
    /// <summary>
    /// Stability at/above this value fully kills the post-temporal STBN retrace.
    /// </summary>
    public const float IdleFreezeThreshold = 0.85f;
    /// <summary>
    /// Below this stability the retrace is fully armed. Between this and
    /// <see cref="IdleFreezeThreshold"/> the blend ramps so pans do not flash
    /// blocky repair borders the instant idle ends.
    /// </summary>
    public const float RetraceRampStart = 0.20f;
    /// <summary>
    /// Freeze repair STBN until clearly moving so ramped blends do not sparkle.
    /// </summary>
    public const float JitterFreezeThreshold = 0.35f;

    /// <summary>
    /// CA3.0/CA3.2 first-use diagnostic token for thin-wisp-aware repair classification.
    /// </summary>
    public static string FormatDiagnostic() =>
        $"ca3.2-repair(alphaThr={AlphaRangeThreshold:0.##}," +
        $"sil={SilhouetteAlphaCeiling:0.##}," +
        $"jump={StrongAlphaRangeThreshold:0.##}," +
        $"steps={RepairStepCount}," +
        $"idleFreeze>={IdleFreezeThreshold:0.##}," +
        $"retraceRamp={RetraceRampStart:0.##}..{IdleFreezeThreshold:0.##})";

    /// <summary>
    /// Multiplier on repairConfidence. Idle keeps the temporally resolved source;
    /// clear motion restores CQ1.8; the band between ramps to avoid hard borders.
    /// </summary>
    public static float EvaluateRetraceBlend(float temporalStability)
    {
        var stability = Math.Clamp(temporalStability, 0f, 1f);
        if (stability >= IdleFreezeThreshold)
        {
            return 0f;
        }

        if (stability <= RetraceRampStart)
        {
            return 1f;
        }

        // 1 at ramp start, 0 at freeze threshold.
        var t = (stability - RetraceRampStart) / (IdleFreezeThreshold - RetraceRampStart);
        var s = t * t * (3f - 2f * t);
        return 1f - s;
    }

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

        var alphaRange = alphaMax - alphaMin;
        // Pure material variation inside occupied cloud must not retrace. Alpha repair
        // remains for cloud/sky silhouettes (a near-clear tap) or extreme opacity jumps.
        var alphaEdge = alphaRange > AlphaRangeThreshold &&
            (alphaMin <= SilhouetteAlphaCeiling ||
             alphaRange > StrongAlphaRangeThreshold);
        var distanceEdge = validCount > 1 &&
            distanceMax - distanceMin >
            Math.Max(MinimumDistanceRangeThreshold, distanceMin * RelativeDistanceRangeThreshold);
        var validityEdge = validCount > 0 && validCount < taps.Length;
        var kindEdge = validCount > 1 && kindMax - kindMin > KindRangeThreshold;
        var normalizedValidWeight = validWeight / taps.Length;
        // An entirely empty source footprint contains no recoverable boundary location.
        // Treating it as "low weight" retraces every clear shell pixel and turns the bounded
        // edge pass into a second full-screen cloud march.
        var lowValidWeight = validCount > 0 &&
                             normalizedValidWeight < MinimumValidWeight;
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
