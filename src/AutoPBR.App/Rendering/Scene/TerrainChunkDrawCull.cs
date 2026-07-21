using System.Numerics;

using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Draw-only terrain chunk selection (frustum + nearest fallback). Streaming owns residency.
/// </summary>
public static class TerrainChunkDrawCull
{
    public readonly struct Candidate
    {
        public required Vector3 BoundsCenter { get; init; }
        public required float BoundsRadius { get; init; }
        public required TerrainChunkLodKind Lod { get; init; }
        public required bool NearPom { get; init; }
        public required int SourceIndex { get; init; }
    }

    /// <summary>
    /// Fills <paramref name="selected"/> with source indices to draw: frustum hits, or nearest
    /// <paramref name="fallbackCount"/> by XZ if none pass (never blank the pad under the eye).
    /// Sort order: Full+POM, Full, Lod.
    /// </summary>
    public static void Select(
        IReadOnlyList<Candidate> candidates,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        int fallbackCount,
        bool fullOnly,
        List<int> selected)
    {
        selected.Clear();
        Span<Vector4> planes = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];
        PreviewFrustumPlanes.Extract(viewProjection, planes);

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (fullOnly && c.Lod != TerrainChunkLodKind.Full)
            {
                continue;
            }

            if (PreviewFrustumPlanes.SphereIntersects(planes, c.BoundsCenter, c.BoundsRadius))
            {
                selected.Add(i);
            }
        }

        if (selected.Count == 0)
        {
            CollectNearest(candidates, cameraPosition, fallbackCount, fullOnly, selected);
        }

        selected.Sort((a, b) => DrawGroup(candidates[a]).CompareTo(DrawGroup(candidates[b])));
    }

    private static void CollectNearest(
        IReadOnlyList<Candidate> candidates,
        Vector3 cameraPosition,
        int fallbackCount,
        bool fullOnly,
        List<int> selected)
    {
        fallbackCount = Math.Max(0, fallbackCount);
        var scored = new List<(float DistSq, int Index)>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (fullOnly && c.Lod != TerrainChunkLodKind.Full)
            {
                continue;
            }

            var dx = c.BoundsCenter.X - cameraPosition.X;
            var dz = c.BoundsCenter.Z - cameraPosition.Z;
            scored.Add((dx * dx + dz * dz, i));
        }

        scored.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));
        var n = Math.Min(fallbackCount, scored.Count);
        for (var i = 0; i < n; i++)
        {
            selected.Add(scored[i].Index);
        }
    }

    private static int DrawGroup(in Candidate c)
    {
        if (c.Lod == TerrainChunkLodKind.Full && c.NearPom)
        {
            return 0;
        }

        if (c.Lod == TerrainChunkLodKind.Full)
        {
            return 1;
        }

        return 2;
    }
}
