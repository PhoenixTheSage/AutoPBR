using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Clockwise annular bake/upload ordering for Full through distant LOD.
/// Comparator: Full phase before LOD, then Chebyshev ring, then clockwise angle, then LodLevel.
/// </summary>
public static class TerrainStreamSchedule
{
    /// <summary>0 = Full keys, 1 = combined LOD sections.</summary>
    public static int Phase(TerrainResidencyKey key) => key.IsFull ? 0 : 1;

    /// <summary>Chebyshev ring from camera (closest AABB edge for multi-chunk sections).</summary>
    public static int RingIndex(TerrainResidencyKey key, TerrainChunkKey cameraChunk) =>
        key.ChebyshevDistanceToChunk(cameraChunk);

    /// <summary>
    /// Clockwise angle about the camera in [0, 65535], quantized for stable ordering.
    /// 0 = +Z, increases toward +X (atan2(x, z) remapped).
    /// </summary>
    public static ushort ClockAngle(TerrainResidencyKey key, TerrainChunkKey cameraChunk)
    {
        Vector2 center;
        if (key.IsFull)
        {
            center = new Vector2(
                key.X + 0.5f - cameraChunk.X,
                key.Z + 0.5f - cameraChunk.Z);
        }
        else
        {
            var world = key.CenterXZ();
            var camWorldX = cameraChunk.X * PreviewStageConstants.TerrainChunkSize +
                            PreviewStageConstants.TerrainChunkSize * 0.5f;
            var camWorldZ = cameraChunk.Z * PreviewStageConstants.TerrainChunkSize +
                            PreviewStageConstants.TerrainChunkSize * 0.5f;
            center = new Vector2(world.X - camWorldX, world.Y - camWorldZ);
        }

        if (center.X * center.X + center.Y * center.Y < 1e-8f)
        {
            return 0;
        }

        // atan2(x, z): 0 at +Z, π/2 at +X → clockwise when looking down -Y (Minecraft XZ).
        var radians = MathF.Atan2(center.X, center.Y);
        if (radians < 0f)
        {
            radians += MathF.Tau;
        }

        return (ushort)Math.Clamp((int)(radians * (65536f / MathF.Tau)), 0, 65535);
    }

    public readonly record struct Rank(
        int Phase,
        int Ring,
        ushort Angle,
        byte LodLevel,
        int KeyX,
        int KeyZ);

    public static Rank RankKey(TerrainResidencyKey key, TerrainChunkKey cameraChunk) =>
        new(
            Phase(key),
            RingIndex(key, cameraChunk),
            ClockAngle(key, cameraChunk),
            key.LodLevel,
            key.X,
            key.Z);

    /// <summary>Ascending schedule order (earlier = bake/upload first).</summary>
    public static int Compare(in Rank a, in Rank b)
    {
        var cmp = a.Phase.CompareTo(b.Phase);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.Ring.CompareTo(b.Ring);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.Angle.CompareTo(b.Angle);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.LodLevel.CompareTo(b.LodLevel);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.KeyX.CompareTo(b.KeyX);
        return cmp != 0 ? cmp : a.KeyZ.CompareTo(b.KeyZ);
    }

    public static int CompareKeys(
        TerrainResidencyKey a,
        TerrainResidencyKey b,
        TerrainChunkKey cameraChunk) =>
        Compare(RankKey(a, cameraChunk), RankKey(b, cameraChunk));
}
