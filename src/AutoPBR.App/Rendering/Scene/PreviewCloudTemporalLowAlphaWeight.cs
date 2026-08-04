namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CA3.1 low-alpha temporal weight policy. Thin/disappearing wisps must not inherit
/// opaque history for as long as dense cores; under camera/wind stability the floor
/// rises so standing-still grain on soft edges can accumulate history. During pans,
/// agreeing soft edges keep more history so motion does not flash raw low-res borders.
/// </summary>
public static class PreviewCloudTemporalLowAlphaWeight
{
    public const float ThinStartAlpha = 0.28f;
    public const float ThinEndAlpha = 0.55f;
    public const float DisagreementStart = 0.06f;
    public const float DisagreementEnd = 0.28f;
    /// <summary>Floor while the camera is moving (anti-ghosting, still history-friendly).</summary>
    public const float MinimumWeight = 0.58f;
    /// <summary>Floor while standing still with high history confidence (anti-grain).</summary>
    public const float StaticMinimumWeight = 0.86f;
    /// <summary>How much stability can suppress thinness-only reactivity while idle.</summary>
    public const float StabilityThinnessRelief = 0.72f;
    /// <summary>
    /// While moving, agreeing thin edges only apply this fraction of thinness reactivity
    /// so pans do not dump history into raw 2/3-res borders.
    /// </summary>
    public const float MotionAgreeingReactiveScale = 0.40f;
    /// <summary>Camera delta that counts as standing still for idle snap.</summary>
    public const float IdleCameraDelta = 0.02f;
    /// <summary>Minimum history confidence before idle denoise snaps to 1.</summary>
    public const float IdleConfidence = 0.875f;
    /// <summary>Camera delta that exits a latched idle denoise state.</summary>
    public const float IdleExitCameraDelta = 0.07f;
    /// <summary>Max stability drop per frame when leaving idle (softens motion borders).</summary>
    public const float StabilityExitStep = 0.16f;

    public static float Evaluate(float currentAlpha, float historyAlpha, float stability = 0f)
    {
        stability = Math.Clamp(stability, 0f, 1f);
        var minAlpha = Math.Min(Math.Clamp(currentAlpha, 0f, 1f), Math.Clamp(historyAlpha, 0f, 1f));
        var thinness = 1f - Smoothstep(ThinStartAlpha, ThinEndAlpha, minAlpha);
        var disagreement = Smoothstep(
            DisagreementStart,
            DisagreementEnd,
            Math.Abs(currentAlpha - historyAlpha));
        // Idle: thinness alone is relieved; only disagreement cuts hard (anti-grain).
        // Motion: agreeing soft edges keep most history (anti-border); disagreement
        // still forces an update so evaporating wisps do not ghost.
        var idleReactiveScale = Math.Max(1f - stability * StabilityThinnessRelief, disagreement);
        var motionReactiveScale = Lerp(MotionAgreeingReactiveScale, 1f, disagreement);
        var reactiveScale = Lerp(motionReactiveScale, idleReactiveScale, stability);
        var reactive = thinness * reactiveScale;
        var minWeight = Lerp(MinimumWeight, StaticMinimumWeight, stability);
        return Lerp(1f, minWeight, Math.Clamp(reactive, 0f, 1f));
    }

    /// <summary>
    /// 0 = full motion CA3.1 cut; 1 = static denoise-friendly floor.
    /// Near-idle views snap to exactly 1 so wind micro-deltas cannot chatter
    /// history weights across the whole cloud layer.
    /// </summary>
    public static float EvaluateStability(
        float cameraDeltaWorld,
        float windDeltaLength,
        float historyConfidence)
    {
        var cameraDelta = Math.Max(0f, cameraDeltaWorld);
        var confidence = Math.Clamp(historyConfidence, 0f, 1f);
        if (cameraDelta <= IdleCameraDelta && confidence >= IdleConfidence)
        {
            return 1f;
        }

        var cameraStability = 1f - Smoothstep(0.015f, 0.12f, cameraDelta);
        var windStability = 1f - Smoothstep(0.08f, 0.55f, Math.Max(0f, windDeltaLength));
        return Math.Clamp(cameraStability * Lerp(1f, windStability, 0.35f) * confidence, 0f, 1f);
    }

    /// <summary>
    /// Hysteresis around idle denoise so frames near the enter/exit thresholds do
    /// not toggle CA3.1 / CQ1.8 stability every tick.
    /// </summary>
    public static bool UpdateIdleLatch(
        bool currentlyLatched,
        float cameraDeltaWorld,
        float historyConfidence)
    {
        var cameraDelta = Math.Max(0f, cameraDeltaWorld);
        var confidence = Math.Clamp(historyConfidence, 0f, 1f);
        if (currentlyLatched)
        {
            return cameraDelta <= IdleExitCameraDelta && confidence >= 0.5f;
        }

        return cameraDelta <= IdleCameraDelta && confidence >= IdleConfidence;
    }

    /// <summary>
    /// Ease stability toward the frame target. Entering idle snaps up; leaving idle
    /// decays so repair/history do not hard-cut into bordering on the first pan frame.
    /// </summary>
    public static float EaseStability(float previous, float target)
    {
        previous = Math.Clamp(previous, 0f, 1f);
        target = Math.Clamp(target, 0f, 1f);
        if (target >= previous)
        {
            return target;
        }

        return Math.Max(target, previous - StabilityExitStep);
    }

    public static string FormatDiagnostic() =>
        $"ca3.1-low-alpha({ThinStartAlpha:0.##}..{ThinEndAlpha:0.##}->{MinimumWeight:0.##}..{StaticMinimumWeight:0.##}@stability)";

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0)
        {
            return x < edge0 ? 0f : 1f;
        }

        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
