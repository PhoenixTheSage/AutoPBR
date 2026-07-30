using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>Shared CPU reference math for flat continuous-world cloud layers.</summary>
public static class PreviewCloudLayerGeometry
{
    public const float DefaultMaxTraceDistance = 4_096f;
    public const float DefaultDistanceFadeFraction = 0.20f;
    public const float DefaultMarchSpanFloor = 256f;

    public static float Altitude(Vector3 worldPosition, float groundWorldY) =>
        worldPosition.Y - groundWorldY;

    /// <summary>
    /// Near-field step sizing span so long near-horizontal rays do not inherit the complete
    /// altitude-plane exit for their finest sample lattice.
    /// </summary>
    public static float MarchSpanLimit(float volumeSize, float volumeHeight) =>
        Math.Max(Math.Max(volumeSize * 4f, volumeHeight * 8f), DefaultMarchSpanFloor);

    /// <summary>
    /// CPU reference for the shader's primary march step length. Short intervals divide their
    /// complete span; long intervals use the bounded near span regardless of camera region.
    /// </summary>
    public static float MarchStepLength(
        float tEnter,
        float tExit,
        int steps,
        float volumeSize,
        float volumeHeight)
    {
        var safeSteps = Math.Max(steps, 1);
        var interval = Math.Max(tExit - tEnter, 0f);
        var sizedSpan = Math.Min(interval, MarchSpanLimit(volumeSize, volumeHeight));
        return Math.Max(sizedSpan / safeSteps, 0.01f);
    }

    /// <summary>
    /// CPU reference for the shader's opaque-scene ordering test. Positive infinity denotes
    /// a cleared depth sample. The bias keeps equal-depth reconstruction from flickering.
    /// </summary>
    public static float SceneOcclusionVisibility(float cloudDistance, float sceneDistance)
    {
        if (!float.IsFinite(cloudDistance) || cloudDistance < 0f)
        {
            return 0f;
        }

        if (float.IsPositiveInfinity(sceneDistance))
        {
            return 1f;
        }

        if (!float.IsFinite(sceneDistance) || sceneDistance <= 1e-3f)
        {
            return 0f;
        }

        var bias = Math.Max(0.04f, sceneDistance * 0.002f);
        return cloudDistance < sceneDistance - bias ? 1f : 0f;
    }

    /// <summary>
    /// Returns the first forward interval through a horizontal altitude slab. A negative Y
    /// component denotes no forward intersection. Horizontal rays remain valid only while
    /// their origin is already inside the slab, and every interval is distance bounded.
    /// </summary>
    public static Vector2 Intersect(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float groundWorldY,
        float lowerAltitude,
        float upperAltitude,
        float maxTraceDistance = DefaultMaxTraceDistance)
    {
        if (rayDirection.LengthSquared() < 1e-12f ||
            upperAltitude <= lowerAltitude ||
            !float.IsFinite(maxTraceDistance) ||
            maxTraceDistance <= 0f)
        {
            return new Vector2(0f, -1f);
        }

        rayDirection = Vector3.Normalize(rayDirection);
        var lowerWorldY = groundWorldY + lowerAltitude;
        var upperWorldY = groundWorldY + upperAltitude;
        if (MathF.Abs(rayDirection.Y) <= 1e-6f)
        {
            return rayOrigin.Y >= lowerWorldY && rayOrigin.Y <= upperWorldY
                ? new Vector2(0f, maxTraceDistance)
                : new Vector2(0f, -1f);
        }

        var first = (lowerWorldY - rayOrigin.Y) / rayDirection.Y;
        var second = (upperWorldY - rayOrigin.Y) / rayDirection.Y;
        var enter = Math.Max(Math.Min(first, second), 0f);
        var exit = Math.Min(Math.Max(first, second), maxTraceDistance);
        return exit > enter
            ? new Vector2(enter, exit)
            : new Vector2(0f, -1f);
    }

    /// <summary>
    /// Smoothly removes slabs whose entry approaches the finite trace boundary. This replaces
    /// the former planet-tangent mask without bending the layer or creating a hard distance rim.
    /// </summary>
    public static float DistanceVisibility(
        float entryDistance,
        float maxTraceDistance = DefaultMaxTraceDistance,
        float fadeFraction = DefaultDistanceFadeFraction)
    {
        if (!float.IsFinite(entryDistance) ||
            !float.IsFinite(maxTraceDistance) ||
            maxTraceDistance <= 0f)
        {
            return 0f;
        }

        var safeFade = Math.Clamp(fadeFraction, 0.01f, 0.95f);
        var fadeStart = maxTraceDistance * (1f - safeFade);
        return 1f - Smoothstep(fadeStart, maxTraceDistance, Math.Max(entryDistance, 0f));
    }

    private static float Smoothstep(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / Math.Max(edge1 - edge0, 1e-6f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
