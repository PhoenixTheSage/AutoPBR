using System.Numerics;

using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CPU mirror of heightfield occupancy + Amanatides–Woo DDA used by genesis_indirect_compact.
/// Column solids match <see cref="PreviewTerrainMeshBaker.IsSolid(System.Func{int,int,int},int,int,int,int)"/>.
/// </summary>
internal static class PreviewVoxelDdaMath
{
    public const int DefaultMaxSteps = 384;

    public static int SolidBottomY(int columnHeight, int fillDepth = PreviewStageConstants.TerrainFillDepth) =>
        PreviewTerrainMeshBaker.SolidBottomY(columnHeight, fillDepth);

    public static bool IsSolidColumnLayer(int surfaceY, int bottomY, int layerY) =>
        layerY >= bottomY && layerY <= surfaceY;

    public static int WorldYToRelativeLayer(float worldY) =>
        (int)MathF.Floor(worldY - PreviewStageConstants.GroundPlaneWorldY);

    /// <summary>
    /// March a ray through unit world voxels. Returns true when a solid column cell is entered
    /// before reaching <paramref name="maxDistance"/> along the ray.
    /// </summary>
    public static bool RayHitsSolidBefore(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        Func<int, int, (int Surface, int Bottom)> columnAt,
        int maxSteps = DefaultMaxSteps)
    {
        var lenSq = direction.LengthSquared();
        if (lenSq < 1e-12f || maxDistance <= 1e-5f)
        {
            return false;
        }

        direction /= MathF.Sqrt(lenSq);
        maxDistance = MathF.Max(0f, maxDistance - 1e-3f);

        var x = (int)MathF.Floor(origin.X);
        var y = (int)MathF.Floor(origin.Y);
        var z = (int)MathF.Floor(origin.Z);

        var stepX = direction.X >= 0f ? 1 : -1;
        var stepY = direction.Y >= 0f ? 1 : -1;
        var stepZ = direction.Z >= 0f ? 1 : -1;

        var tDeltaX = direction.X != 0f ? MathF.Abs(1f / direction.X) : float.PositiveInfinity;
        var tDeltaY = direction.Y != 0f ? MathF.Abs(1f / direction.Y) : float.PositiveInfinity;
        var tDeltaZ = direction.Z != 0f ? MathF.Abs(1f / direction.Z) : float.PositiveInfinity;

        var tMaxX = direction.X != 0f
            ? ((direction.X >= 0f ? x + 1f : x) - origin.X) / direction.X
            : float.PositiveInfinity;
        var tMaxY = direction.Y != 0f
            ? ((direction.Y >= 0f ? y + 1f : y) - origin.Y) / direction.Y
            : float.PositiveInfinity;
        var tMaxZ = direction.Z != 0f
            ? ((direction.Z >= 0f ? z + 1f : z) - origin.Z) / direction.Z
            : float.PositiveInfinity;

        var t = 0f;
        var stepLimit = Math.Max(1, Math.Min(maxSteps, (int)MathF.Ceiling(maxDistance) + 2));
        for (var step = 0; step < stepLimit && t <= maxDistance; step++)
        {
            var relY = WorldYToRelativeLayer(y + 0.5f);
            var (surface, bottom) = columnAt(x, z);
            if (t > 1e-4f && IsSolidColumnLayer(surface, bottom, relY) && t < maxDistance)
            {
                return true;
            }

            if (tMaxX < tMaxY)
            {
                if (tMaxX < tMaxZ)
                {
                    t = tMaxX;
                    tMaxX += tDeltaX;
                    x += stepX;
                }
                else
                {
                    t = tMaxZ;
                    tMaxZ += tDeltaZ;
                    z += stepZ;
                }
            }
            else if (tMaxY < tMaxZ)
            {
                t = tMaxY;
                tMaxY += tDeltaY;
                y += stepY;
            }
            else
            {
                t = tMaxZ;
                tMaxZ += tDeltaZ;
                z += stepZ;
            }
        }

        return false;
    }

    /// <summary>
    /// Conservative sphere occlusion: near-point + axis offsets must all be blocked by terrain.
    /// </summary>
    public static bool IsSphereOccludedByHeightfield(
        Vector3 camera,
        Vector3 center,
        float radius,
        Func<int, int, (int Surface, int Bottom)> columnAt,
        int maxSteps = DefaultMaxSteps)
    {
        radius = MathF.Max(0f, radius * 0.85f);
        var toCenter = center - camera;
        var dist = toCenter.Length();
        if (dist <= radius + 1e-3f)
        {
            return false;
        }

        var dir = toCenter / dist;
        Span<Vector3> samples = stackalloc Vector3[5];
        samples[0] = center - dir * radius;
        var orthoA = Vector3.Normalize(MathF.Abs(dir.Y) < 0.9f
            ? Vector3.Cross(dir, Vector3.UnitY)
            : Vector3.Cross(dir, Vector3.UnitX));
        var orthoB = Vector3.Normalize(Vector3.Cross(dir, orthoA));
        samples[1] = samples[0] + orthoA * (radius * 0.65f);
        samples[2] = samples[0] - orthoA * (radius * 0.65f);
        samples[3] = samples[0] + orthoB * (radius * 0.65f);
        samples[4] = samples[0] - orthoB * (radius * 0.65f);

        foreach (var sample in samples)
        {
            var delta = sample - camera;
            var sampleDist = delta.Length();
            if (sampleDist < 1e-4f)
            {
                return false;
            }

            if (!RayHitsSolidBefore(camera, delta / sampleDist, sampleDist, columnAt, maxSteps))
            {
                return false;
            }
        }

        return true;
    }
}
