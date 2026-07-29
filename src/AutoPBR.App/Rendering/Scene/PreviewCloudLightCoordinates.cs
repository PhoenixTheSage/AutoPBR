using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

public enum PreviewCloudLightReferenceAxis
{
    WorldUp,
    WorldRight,
}

/// <summary>
/// CQ3 light-space convention. Forward points from the sun toward the world; right/up span each
/// cloud-light cascade plane.
/// </summary>
public readonly record struct PreviewCloudLightBasis(
    Vector3 Right,
    Vector3 Up,
    Vector3 Forward,
    PreviewCloudLightReferenceAxis ReferenceAxis)
{
    public Vector3 WorldToLight(Vector3 world) =>
        new(
            Vector3.Dot(world, Right),
            Vector3.Dot(world, Up),
            Vector3.Dot(world, Forward));

    public Vector3 LightToWorld(Vector3 light) =>
        Right * light.X + Up * light.Y + Forward * light.Z;
}

public static class PreviewCloudLightBasisBuilder
{
    public const float EnterWorldRightThreshold = 0.94f;
    public const float ExitWorldRightThreshold = 0.88f;
    private const float InitialWorldRightThreshold = 0.92f;

    public static PreviewCloudLightBasis Build(
        Vector3 sunToWorldDirection,
        PreviewCloudLightBasis? previous = null)
    {
        if (!IsFinite(sunToWorldDirection) ||
            sunToWorldDirection.LengthSquared() <= 1e-10f)
        {
            throw new ArgumentException(
                "Cloud-light forward direction must be finite and non-zero.",
                nameof(sunToWorldDirection));
        }

        var forward = Vector3.Normalize(sunToWorldDirection);
        var verticalAlignment = MathF.Abs(Vector3.Dot(forward, Vector3.UnitY));
        var referenceAxis = previous?.ReferenceAxis switch
        {
            PreviewCloudLightReferenceAxis.WorldUp
                when verticalAlignment > EnterWorldRightThreshold =>
                PreviewCloudLightReferenceAxis.WorldRight,
            PreviewCloudLightReferenceAxis.WorldRight
                when verticalAlignment < ExitWorldRightThreshold =>
                PreviewCloudLightReferenceAxis.WorldUp,
            { } retained => retained,
            _ when verticalAlignment > InitialWorldRightThreshold =>
                PreviewCloudLightReferenceAxis.WorldRight,
            _ => PreviewCloudLightReferenceAxis.WorldUp,
        };

        var reference = referenceAxis == PreviewCloudLightReferenceAxis.WorldUp
            ? Vector3.UnitY
            : Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(reference, forward));
        var up = Vector3.Normalize(Vector3.Cross(forward, right));

        // The alternate reference axis must not introduce a 180-degree basis flip.
        if (previous is { } prior &&
            Vector3.Dot(right, prior.Right) + Vector3.Dot(up, prior.Up) < 0f)
        {
            right = -right;
            up = -up;
        }

        return new PreviewCloudLightBasis(right, up, forward, referenceAxis);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

/// <summary>
/// Snapped world-to-cache transform for one CQ3 cascade. PlaneCenterX/Y and LightDepthMin are in
/// the light basis, not world axes.
/// </summary>
public readonly record struct PreviewCloudLightCascadeTransform(
    PreviewCloudLightBasis Basis,
    PreviewCloudLightCascadeProfile Profile,
    float PlaneCenterX,
    float PlaneCenterY,
    float LightDepthMin,
    float LightDepthSpan)
{
    public float PlaneTexelWorldSize => Profile.WorldSpan / Profile.Width;
    public float DepthSliceWorldSize => LightDepthSpan / Profile.Depth;

    public static PreviewCloudLightCascadeTransform Create(
        in PreviewCloudLightBasis basis,
        in PreviewCloudLightCascadeProfile profile,
        Vector3 cameraGroundProjection,
        float lightDepthMin,
        float lightDepthMax)
    {
        if (!profile.IsEnabled)
        {
            throw new ArgumentException("Cloud-light cascade profile is disabled.", nameof(profile));
        }

        if (!float.IsFinite(lightDepthMin) ||
            !float.IsFinite(lightDepthMax) ||
            lightDepthMax <= lightDepthMin)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lightDepthMax),
                "Light-depth interval must be finite and increasing.");
        }

        var anchor = basis.WorldToLight(cameraGroundProjection);
        var planeTexel = profile.WorldSpan / profile.Width;
        var depthSpan = lightDepthMax - lightDepthMin;
        var depthSlice = depthSpan / profile.Depth;
        return new PreviewCloudLightCascadeTransform(
            basis,
            profile,
            SnapDown(anchor.X, planeTexel),
            SnapDown(anchor.Y, planeTexel),
            SnapDown(lightDepthMin, depthSlice),
            depthSpan);
    }

    public Vector3 WorldToUnit(Vector3 world)
    {
        var light = Basis.WorldToLight(world);
        return new Vector3(
            (light.X - PlaneCenterX) / Profile.WorldSpan + 0.5f,
            (light.Y - PlaneCenterY) / Profile.WorldSpan + 0.5f,
            (light.Z - LightDepthMin) / LightDepthSpan);
    }

    public Vector3 UnitToWorld(Vector3 unit)
    {
        var light = new Vector3(
            PlaneCenterX + (unit.X - 0.5f) * Profile.WorldSpan,
            PlaneCenterY + (unit.Y - 0.5f) * Profile.WorldSpan,
            LightDepthMin + unit.Z * LightDepthSpan);
        return Basis.LightToWorld(light);
    }

    public bool Contains(Vector3 world)
    {
        var unit = WorldToUnit(world);
        return unit.X is >= 0f and <= 1f &&
               unit.Y is >= 0f and <= 1f &&
               unit.Z is >= 0f and <= 1f;
    }

    internal static float SnapDown(float value, float quantum) =>
        MathF.Floor(value / quantum) * quantum;
}
