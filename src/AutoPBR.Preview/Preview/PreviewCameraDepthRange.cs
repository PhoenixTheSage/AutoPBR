using System.Numerics;

namespace AutoPBR.Preview;

/// <summary>Derives near/far planes for orbit preview from subject + optional stage environment.</summary>
public static class PreviewCameraDepthRange
{
    public const float DefaultNear = 0.1f;
    public const float DefaultFar = 100f;

    /// <summary>
    /// Far-plane cap when the orbit scene includes a large stage (voxel terrain / wide grid).
    /// <see cref="DefaultFar"/> is too tight for ±48 terrain once the camera leaves the pad.
    /// </summary>
    public const float LargeEnvironmentFar = 250f;

    /// <summary>
    /// Orbit camera depth range using subject bounds plus optional floor grid/ground extent.
    /// <paramref name="eye"/> must already be composed for the current frame.
    /// When <paramref name="environmentCeilingY"/> is finite it replaces the thin floor slab max Y.
    /// </summary>
    public static (float NearPlane, float FarPlane) ForOrbitPreview(
        Vector3 subjectMin,
        Vector3 subjectMax,
        float orbitDistance,
        Vector3 eye,
        float environmentHalfExtent = 0f,
        float environmentFloorY = -0.56f,
        float environmentCeilingY = float.NaN)
    {
        var sceneMin = subjectMin;
        var sceneMax = subjectMax;
        if (environmentHalfExtent > 0f)
        {
            var ceiling = float.IsFinite(environmentCeilingY)
                ? environmentCeilingY
                : environmentFloorY + 0.05f;
            var envMin = new Vector3(-environmentHalfExtent, environmentFloorY, -environmentHalfExtent);
            var envMax = new Vector3(environmentHalfExtent, ceiling, environmentHalfExtent);
            sceneMin = Vector3.Min(sceneMin, envMin);
            sceneMax = Vector3.Max(sceneMax, envMax);
        }

        var minDist = MinDistanceEyeToAabb(eye, sceneMin, sceneMax);
        var maxDist = MaxDistanceEyeToAabb(eye, sceneMin, sceneMax);

        // Near plane tracks eye-to-geometry distance so fly-to-orbit handoff does not pop when
        // boom-arm orbit distance is much larger than the current proximity to scene bounds.
        // For a large stage AABB, minDist grows as soon as the eye leaves the box — do not let
        // near scale with that (it carved a black slab through the foreground terrain).
        const float nearFloor = 0.01f;
        var nearCeiling = environmentHalfExtent > 0f
            ? Math.Clamp(environmentHalfExtent * 0.02f, 0.25f, 1.0f)
            : Math.Max(minDist * 0.92f, nearFloor);
        var near = Math.Clamp(minDist * 0.35f, nearFloor, nearCeiling);

        var far = Math.Max(maxDist + 2.5f, near * 8f);
        // Streaming terrain rings can exceed LargeEnvironmentFar; size the cap from the stage extent.
        var farCap = environmentHalfExtent > 20f
            ? Math.Max(LargeEnvironmentFar, environmentHalfExtent * 2.5f + 48f)
            : DefaultFar;
        far = Math.Min(far, farCap);

        if (far / near > 5000f)
        {
            if (environmentHalfExtent > 20f)
            {
                // Inside a huge stage AABB, minDist≈0 → near≈nearFloor. Crushing far to near*5000
                // left a ~50m hard horizon when flying close over the pad. Keep far and lift near
                // slightly for Z precision instead (still small enough not to clip the turf).
                near = Math.Clamp(far / 5000f, nearFloor, 0.2f);
            }
            else
            {
                far = near * 5000f;
            }
        }

        return (near, far);
    }

    /// <summary>Legacy subject-only range (does not include grid); prefer <see cref="ForOrbitPreview"/>.</summary>
    public static (float NearPlane, float FarPlane) ForSubjectBounds(
        Vector3 boundsMin,
        Vector3 boundsMax,
        float orbitDistance,
        float marginScale = 1.35f)
    {
        var extent = boundsMax - boundsMin;
        var radius = extent.Length() * 0.5f;
        if (!float.IsFinite(radius) || radius < 1e-4f)
        {
            radius = 0.75f;
        }

        var orbit = Math.Max(orbitDistance, radius + 0.25f);
        var margin = Math.Max(0.35f, radius * marginScale);
        var near = Math.Clamp(orbit * 0.04f, 0.05f, orbit * 0.45f);
        var far = orbit + radius * 2.5f + margin;
        if (far / near > 5000f)
        {
            far = near * 5000f;
        }

        return (near, far);
    }

    private static float MaxDistanceEyeToAabb(Vector3 eye, Vector3 min, Vector3 max)
    {
        var maxDist = 0f;
        for (var ix = 0; ix < 2; ix++)
        {
            var x = ix == 0 ? min.X : max.X;
            for (var iy = 0; iy < 2; iy++)
            {
                var y = iy == 0 ? min.Y : max.Y;
                for (var iz = 0; iz < 2; iz++)
                {
                    var z = iz == 0 ? min.Z : max.Z;
                    maxDist = Math.Max(maxDist, Vector3.Distance(eye, new Vector3(x, y, z)));
                }
            }
        }

        return maxDist;
    }

    private static float MinDistanceEyeToAabb(Vector3 eye, Vector3 min, Vector3 max)
    {
        var closest = new Vector3(
            Math.Clamp(eye.X, min.X, max.X),
            Math.Clamp(eye.Y, min.Y, max.Y),
            Math.Clamp(eye.Z, min.Z, max.Z));
        return Vector3.Distance(eye, closest);
    }
}
