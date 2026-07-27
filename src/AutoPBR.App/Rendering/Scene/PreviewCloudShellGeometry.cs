using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

public enum PreviewCloudCameraRegion
{
    Below,
    Inside,
    Above,
}

/// <summary>Shared CPU reference math for the curved preview cloud layer.</summary>
public static class PreviewCloudShellGeometry
{
    /// <summary>
    /// Artistic planet radius for the small preview stage. The large radius keeps curvature
    /// below two world units across the nearest 500 units, while the default cloud deck still
    /// rolls below its geometric horizon at roughly 1,610 units.
    /// </summary>
    public const float PlanetRadius = 72_000f;

    public static Vector3 PlanetCenter(float groundWorldY, float planetRadius = PlanetRadius) =>
        new(0f, groundWorldY - Math.Max(planetRadius, 1f), 0f);

    /// <summary>Classifies a camera against the same radial altitude interval used by the cloud shader.</summary>
    public static PreviewCloudCameraRegion ClassifyCamera(
        Vector3 camera,
        Vector3 center,
        float layerBaseAltitude,
        float layerTopAltitude,
        float planetRadius = PlanetRadius)
    {
        var altitude = (camera - center).Length() - Math.Max(planetRadius, 1f);
        if (altitude < layerBaseAltitude)
        {
            return PreviewCloudCameraRegion.Below;
        }

        return altitude <= layerTopAltitude
            ? PreviewCloudCameraRegion.Inside
            : PreviewCloudCameraRegion.Above;
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
    /// Returns the first visible interval through a spherical shell. A negative Y component
    /// denotes no forward intersection. The implementation intentionally matches cloud_shell.glsl.
    /// </summary>
    public static Vector2 Intersect(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 center,
        float innerRadius,
        float outerRadius)
    {
        if (rayDirection.LengthSquared() < 1e-12f || outerRadius <= innerRadius || innerRadius <= 0f)
        {
            return new Vector2(0f, -1f);
        }

        rayDirection = Vector3.Normalize(rayDirection);
        var oc = rayOrigin - center;
        if (!TryIntersectSphere(oc, rayDirection, outerRadius, out var outerNear, out var outerFar) || outerFar <= 0f)
        {
            return new Vector2(0f, -1f);
        }

        var radius = oc.Length();
        float enter;
        float exit;
        if (radius < innerRadius)
        {
            if (!TryIntersectSphere(oc, rayDirection, innerRadius, out _, out var innerFar) || innerFar <= 0f)
            {
                return new Vector2(0f, -1f);
            }

            enter = innerFar;
            exit = outerFar;
        }
        else
        {
            enter = Math.Max(outerNear, 0f);
            exit = outerFar;
            if (TryIntersectSphere(oc, rayDirection, innerRadius, out var innerNear, out _) && innerNear > enter)
            {
                exit = Math.Min(exit, innerNear);
            }
        }

        return exit > enter ? new Vector2(enter, exit) : new Vector2(0f, -1f);
    }

    /// <summary>
    /// Returns the nearest intersection with the solid planet, or positive infinity when
    /// the ray remains above its horizon. This mirrors vcsPlanetOcclusionDistance.
    /// </summary>
    public static float PlanetOcclusionDistance(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 center,
        float planetRadius = PlanetRadius)
    {
        if (rayDirection.LengthSquared() < 1e-12f || planetRadius <= 0f)
        {
            return float.PositiveInfinity;
        }

        rayDirection = Vector3.Normalize(rayDirection);
        var oc = rayOrigin - center;
        var cameraRadius = oc.Length();
        if (cameraRadius < planetRadius - 1e-3f)
        {
            return 0f;
        }

        if (!TryIntersectSphere(oc, rayDirection, planetRadius, out var near, out _))
        {
            return float.PositiveInfinity;
        }

        if (cameraRadius <= planetRadius + 1e-3f && Vector3.Dot(oc, rayDirection) < 0f)
        {
            return 0f;
        }

        return near > 1e-3f ? near : float.PositiveInfinity;
    }

    /// <summary>
    /// CPU reference for the narrow shader-side geometric-horizon feather. The fade is
    /// biased behind the tangent so a cloud crossing the visible horizon is not cut at 50%.
    /// </summary>
    public static float PlanetHorizonVisibility(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 center,
        float feather,
        float planetRadius = PlanetRadius)
    {
        if (rayDirection.LengthSquared() < 1e-12f || planetRadius <= 0f)
        {
            return 0f;
        }

        rayDirection = Vector3.Normalize(rayDirection);
        var oc = rayOrigin - center;
        var cameraRadius = oc.Length();
        if (cameraRadius <= planetRadius - 1e-3f)
        {
            return 0f;
        }

        var localUp = oc / Math.Max(cameraRadius, 1e-4f);
        var radiusRatio = Math.Clamp(planetRadius / Math.Max(cameraRadius, planetRadius), 0f, 1f);
        var horizonMu = -MathF.Sqrt(Math.Max(1f - radiusRatio * radiusRatio, 0f));
        var viewMu = Vector3.Dot(rayDirection, localUp);
        var width = Math.Max(feather, 1e-5f);
        return Smoothstep(horizonMu - width * 2f, horizonMu + width * 0.25f, viewMu);
    }

    private static float Smoothstep(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / Math.Max(edge1 - edge0, 1e-6f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static bool TryIntersectSphere(
        Vector3 originFromCenter,
        Vector3 rayDirection,
        float radius,
        out float near,
        out float far)
    {
        var b = Vector3.Dot(originFromCenter, rayDirection);
        var c = Vector3.Dot(originFromCenter, originFromCenter) - radius * radius;
        var discriminant = b * b - c;
        if (discriminant < 0f)
        {
            near = far = 0f;
            return false;
        }

        var root = MathF.Sqrt(discriminant);
        near = -b - root;
        far = -b + root;
        return true;
    }
}
