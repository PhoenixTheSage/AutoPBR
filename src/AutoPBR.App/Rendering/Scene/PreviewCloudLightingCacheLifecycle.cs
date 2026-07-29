using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

[Flags]
public enum PreviewCloudLightCascadeSelection
{
    None = 0,
    Near = 1,
    Far = 2,
    Both = Near | Far,
}

public enum PreviewCloudLightInvalidationReason
{
    None,
    InitialGeneration,
    MaterialSettingsChanged,
    LargeCameraMovement,
    MaterialSunDirectionChanged,
    LightBasisChanged,
}

public readonly record struct PreviewCloudLightUpdateRequest(
    PreviewCloudLightingCacheProfile Profile,
    int FrameIndex,
    bool NearGenerated,
    bool FarGenerated,
    int NearLastUpdateFrame,
    int FarLastUpdateFrame,
    bool MaterialSettingsChanged,
    bool LargeCameraMovement,
    bool MaterialSunDirectionChanged,
    bool LightBasisChanged);

public readonly record struct PreviewCloudLightUpdateDecision(
    PreviewCloudLightCascadeSelection Cascades,
    bool InvalidateBeforeGeneration,
    PreviewCloudLightInvalidationReason InvalidationReason,
    int NearAge,
    int FarAge)
{
    public bool UpdatesNear =>
        (Cascades & PreviewCloudLightCascadeSelection.Near) != 0;

    public bool UpdatesFar =>
        (Cascades & PreviewCloudLightCascadeSelection.Far) != 0;
}

/// <summary>
/// Pure CQ3.6 cadence and invalidation policy. Keeping this decision outside the GL backend makes
/// the one/two/four-frame schedule and the four-frame maximum reuse age deterministic and testable.
/// </summary>
public static class PreviewCloudLightUpdateScheduler
{
    public const int MaximumReuseFrames = 4;
    public const float MaterialSunDirectionThresholdDegrees = 0.5f;

    public static PreviewCloudLightUpdateDecision Evaluate(
        in PreviewCloudLightUpdateRequest request)
    {
        var nearAge = Age(
            request.FrameIndex,
            request.NearLastUpdateFrame,
            request.NearGenerated);
        var farAge = Age(
            request.FrameIndex,
            request.FarLastUpdateFrame,
            request.FarGenerated);
        var invalidationReason = ResolveInvalidationReason(request);
        if (invalidationReason != PreviewCloudLightInvalidationReason.None)
        {
            return new PreviewCloudLightUpdateDecision(
                PreviewCloudLightCascadeSelection.Both,
                InvalidateBeforeGeneration: true,
                invalidationReason,
                nearAge,
                farAge);
        }

        var cascades = PreviewCloudLightCascadeSelection.None;
        if (!request.NearGenerated ||
            request.Profile.Near.IsUpdateDue(request.FrameIndex) ||
            nearAge >= MaximumReuseFrames)
        {
            cascades |= PreviewCloudLightCascadeSelection.Near;
        }

        if (!request.FarGenerated ||
            request.Profile.Far.IsUpdateDue(request.FrameIndex) ||
            farAge >= MaximumReuseFrames)
        {
            cascades |= PreviewCloudLightCascadeSelection.Far;
        }

        // A cadence collision must not submit both 3D cache generations in one frame.
        // Refresh far first because it has the longer interval; near becomes due again
        // sooner and is refreshed on a following frame.
        if (cascades == PreviewCloudLightCascadeSelection.Both)
        {
            cascades = !request.NearGenerated
                ? PreviewCloudLightCascadeSelection.Near
                : PreviewCloudLightCascadeSelection.Far;
        }

        return new PreviewCloudLightUpdateDecision(
            cascades,
            InvalidateBeforeGeneration: false,
            PreviewCloudLightInvalidationReason.None,
            nearAge,
            farAge);
    }

    public static bool IsMaterialSunDirectionChange(
        Vector3 previous,
        Vector3 current)
    {
        if (!IsFinite(previous) ||
            !IsFinite(current) ||
            previous.LengthSquared() <= 1e-10f ||
            current.LengthSquared() <= 1e-10f)
        {
            return true;
        }

        var dot = Math.Clamp(
            Vector3.Dot(Vector3.Normalize(previous), Vector3.Normalize(current)),
            -1f,
            1f);
        var angleDegrees = MathF.Acos(dot) * (180f / MathF.PI);
        return angleDegrees > MaterialSunDirectionThresholdDegrees;
    }

    private static PreviewCloudLightInvalidationReason ResolveInvalidationReason(
        in PreviewCloudLightUpdateRequest request)
    {
        if (!request.NearGenerated && !request.FarGenerated)
        {
            return PreviewCloudLightInvalidationReason.InitialGeneration;
        }

        if (request.MaterialSettingsChanged)
        {
            return PreviewCloudLightInvalidationReason.MaterialSettingsChanged;
        }

        if (request.LargeCameraMovement)
        {
            return PreviewCloudLightInvalidationReason.LargeCameraMovement;
        }

        if (request.MaterialSunDirectionChanged)
        {
            return PreviewCloudLightInvalidationReason.MaterialSunDirectionChanged;
        }

        return request.LightBasisChanged
            ? PreviewCloudLightInvalidationReason.LightBasisChanged
            : PreviewCloudLightInvalidationReason.None;
    }

    private static int Age(int frameIndex, int lastUpdateFrame, bool generated)
    {
        if (!generated || lastUpdateFrame < 0)
        {
            return int.MaxValue;
        }

        return Math.Max(0, frameIndex - lastUpdateFrame);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public readonly record struct PreviewCloudLightScrollPlan(
    int TexelDeltaX,
    int TexelDeltaY,
    int SliceDelta,
    float ReusableFraction,
    bool CanReusePlaneColumns)
{
    public static PreviewCloudLightScrollPlan Create(
        in PreviewCloudLightCascadeTransform previous,
        in PreviewCloudLightCascadeTransform current)
    {
        if (!previous.Profile.Equals(current.Profile) ||
            previous.Basis.ReferenceAxis != current.Basis.ReferenceAxis ||
            Vector3.Dot(previous.Basis.Right, current.Basis.Right) < 0.9999f ||
            Vector3.Dot(previous.Basis.Up, current.Basis.Up) < 0.9999f ||
            Vector3.Dot(previous.Basis.Forward, current.Basis.Forward) < 0.9999f)
        {
            return default;
        }

        var texel = previous.PlaneTexelWorldSize;
        var slice = previous.DepthSliceWorldSize;
        var deltaX = QuantizedDelta(
            current.PlaneCenterX - previous.PlaneCenterX,
            texel);
        var deltaY = QuantizedDelta(
            current.PlaneCenterY - previous.PlaneCenterY,
            texel);
        var deltaZ = QuantizedDelta(
            current.LightDepthMin - previous.LightDepthMin,
            slice);
        var xOverlap = Math.Max(0, previous.Profile.Width - Math.Abs(deltaX));
        var yOverlap = Math.Max(0, previous.Profile.Height - Math.Abs(deltaY));
        var reusable = (float)(xOverlap * yOverlap) /
            Math.Max(1, previous.Profile.Width * previous.Profile.Height);
        var canReuse =
            deltaZ == 0 &&
            Math.Abs(deltaX) < previous.Profile.Width &&
            Math.Abs(deltaY) < previous.Profile.Height;
        return new PreviewCloudLightScrollPlan(
            deltaX,
            deltaY,
            deltaZ,
            canReuse ? reusable : 0f,
            canReuse);
    }

    public string FormatDiagnostic() =>
        FormattableString.Invariant(
            $"scroll={TexelDeltaX},{TexelDeltaY},{SliceDelta};planeReuse={ReusableFraction:F2};mode={(CanReusePlaneColumns ? "snapped-reuse" : "full-refresh")}");

    private static int QuantizedDelta(float value, float quantum)
    {
        if (!float.IsFinite(value) || !float.IsFinite(quantum) || quantum <= 0f)
        {
            return 0;
        }

        return (int)MathF.Round(value / quantum);
    }
}

public static class PreviewCloudLightWindReprojection
{
    public static Vector3 WrappedDelta(
        Vector3 current,
        Vector3 generated,
        float period)
    {
        static float ShortestDelta(float value, float range)
        {
            var half = range * 0.5f;
            if (value > half)
            {
                return value - range;
            }

            if (value < -half)
            {
                return value + range;
            }

            return value;
        }

        if (!float.IsFinite(period) || period <= 0f)
        {
            return current - generated;
        }

        var delta = current - generated;
        return new Vector3(
            ShortestDelta(delta.X, period),
            ShortestDelta(delta.Y, period),
            ShortestDelta(delta.Z, period));
    }

    public static PreviewCloudLightCascadeTransform Apply(
        in PreviewCloudLightCascadeTransform generatedTransform,
        Vector3 worldOffset)
    {
        var lightOffset = generatedTransform.Basis.WorldToLight(worldOffset);
        return generatedTransform with
        {
            PlaneCenterX = generatedTransform.PlaneCenterX - lightOffset.X,
            PlaneCenterY = generatedTransform.PlaneCenterY - lightOffset.Y,
            LightDepthMin = generatedTransform.LightDepthMin - lightOffset.Z,
        };
    }
}
