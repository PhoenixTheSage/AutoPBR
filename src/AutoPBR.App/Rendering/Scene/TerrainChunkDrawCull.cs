using System.Collections.Concurrent;
using System.Numerics;

using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Draw-only terrain chunk selection (frustum + nearest fallback). Streaming owns residency.
/// </summary>
public static class TerrainChunkDrawCull
{
    /// <summary>Match shadow-cascade parallel gate; tiny candidate sets stay single-threaded.</summary>
    // A frustum sphere test is tiny; Parallel.For + ConcurrentBag costs more than the work for
    // the ~1.5k candidates seen in normal High-profile terrain sessions. Reserve parallel fanout
    // for genuinely large resident sets.
    public const int ParallelFilterMinCandidates = 2048;
    private static readonly ParallelOptions FilterParallelOptions = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(
            Environment.ProcessorCount / 2,
            1,
            4),
    };

    public readonly struct Candidate
    {
        public TerrainResidencyKey Key { get; init; }
        public required Vector3 BoundsCenter { get; init; }
        public required float BoundsRadius { get; init; }
        public required TerrainChunkLodKind Lod { get; init; }
        /// <summary>
        /// Near-camera POM eligibility for draw-group sort / parallax toggles.
        /// Derived at select/draw time from camera distance — not from residency collect.
        /// </summary>
        public required bool NearPom { get; init; }
        public required int SourceIndex { get; init; }
    }

    /// <summary>
    /// Sets <see cref="Candidate.NearPom"/> from camera XZ distance and LOD (parallax enable radius).
    /// </summary>
    public static void ApplyNearPomFlags(
        IList<Candidate> candidates,
        Vector3 cameraPosition,
        bool enableParallaxSetting)
    {
        if (!enableParallaxSetting || candidates.Count == 0)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.NearPom)
                {
                    candidates[i] = c with { NearPom = false };
                }
            }

            return;
        }

        var pomEnableRadius = PreviewStageConstants.TerrainNearPomRadius +
                              PreviewStageConstants.TerrainNearPomFadeWidth;
        var pomEnableRadiusSq = pomEnableRadius * pomEnableRadius;
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var dx = c.BoundsCenter.X - cameraPosition.X;
            var dz = c.BoundsCenter.Z - cameraPosition.Z;
            var nearPom = c.Lod == TerrainChunkLodKind.Full &&
                          dx * dx + dz * dz <= pomEnableRadiusSq;
            if (c.NearPom != nearPom)
            {
                candidates[i] = c with { NearPom = nearPom };
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="selected"/> with source indices to draw: frustum hits, or nearest
    /// <paramref name="fallbackCount"/> by XZ if none pass (never blank the pad under the eye).
    /// Sort order: Full+POM, Full, Lod (stable by candidate index within a group).
    /// </summary>
    public static void Select(
        IReadOnlyList<Candidate> candidates,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        int fallbackCount,
        bool fullOnly,
        List<int> selected,
        float maxCasterDistanceXz = 0f,
        bool allowParallel = true)
    {
        selected.Clear();
        Span<Vector4> planes = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];
        PreviewFrustumPlanes.Extract(viewProjection, planes);
        var maxDist = maxCasterDistanceXz > 0f ? maxCasterDistanceXz : float.PositiveInfinity;

        if (allowParallel &&
            candidates.Count >= ParallelFilterMinCandidates)
        {
            CollectFrustumHitsParallel(candidates, planes, cameraPosition, fullOnly, maxDist, selected);
        }
        else
        {
            CollectFrustumHitsSequential(candidates, planes, cameraPosition, fullOnly, maxDist, selected);
        }

        if (selected.Count == 0)
        {
            CollectNearest(candidates, cameraPosition, fallbackCount, fullOnly, selected, maxDist);
        }

        selected.Sort((a, b) =>
        {
            var groupCmp = DrawGroup(candidates[a]).CompareTo(DrawGroup(candidates[b]));
            return groupCmp != 0 ? groupCmp : a.CompareTo(b);
        });
    }

    /// <summary>
    /// Sort key for a terrain draw item: POM/LOD group, then cutout, then material, then chunk order.
    /// </summary>
    public static int CompareDrawItems(
        in Candidate a,
        int materialA,
        bool cutoutA,
        in Candidate b,
        int materialB,
        bool cutoutB,
        int sourceOrderA,
        int sourceOrderB)
    {
        var groupCmp = DrawGroup(a).CompareTo(DrawGroup(b));
        if (groupCmp != 0)
        {
            return groupCmp;
        }

        // Opaque before cutout within a POM/LOD group so POM stays on for longer stretches.
        var cutoutCmp = cutoutA.CompareTo(cutoutB);
        if (cutoutCmp != 0)
        {
            return cutoutCmp;
        }

        var matCmp = materialA.CompareTo(materialB);
        return matCmp != 0 ? matCmp : sourceOrderA.CompareTo(sourceOrderB);
    }

    private static void CollectFrustumHitsSequential(
        IReadOnlyList<Candidate> candidates,
        ReadOnlySpan<Vector4> planes,
        Vector3 cameraPosition,
        bool fullOnly,
        float maxDist,
        List<int> selected)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (PassesFrustumFilter(candidates[i], planes, cameraPosition, fullOnly, maxDist))
            {
                selected.Add(i);
            }
        }
    }

    private static void CollectFrustumHitsParallel(
        IReadOnlyList<Candidate> candidates,
        ReadOnlySpan<Vector4> planes,
        Vector3 cameraPosition,
        bool fullOnly,
        float maxDist,
        List<int> selected)
    {
        // Copy planes for worker threads (stackalloc Span cannot be captured).
        var plane0 = planes[0];
        var plane1 = planes[1];
        var plane2 = planes[2];
        var plane3 = planes[3];
        var plane4 = planes[4];
        var plane5 = planes[5];
        var bag = new ConcurrentBag<int>();
        Parallel.For(0, candidates.Count, FilterParallelOptions, i =>
        {
            Span<Vector4> localPlanes = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];
            localPlanes[0] = plane0;
            localPlanes[1] = plane1;
            localPlanes[2] = plane2;
            localPlanes[3] = plane3;
            localPlanes[4] = plane4;
            localPlanes[5] = plane5;
            if (PassesFrustumFilter(candidates[i], localPlanes, cameraPosition, fullOnly, maxDist))
            {
                bag.Add(i);
            }
        });

        selected.AddRange(bag);
    }

    private static bool PassesFrustumFilter(
        in Candidate c,
        ReadOnlySpan<Vector4> planes,
        Vector3 cameraPosition,
        bool fullOnly,
        float maxDist)
    {
        if (fullOnly && c.Lod != TerrainChunkLodKind.Full)
        {
            return false;
        }

        if (!WithinCasterDistance(c, cameraPosition, maxDist))
        {
            return false;
        }

        return PreviewFrustumPlanes.SphereIntersects(planes, c.BoundsCenter, c.BoundsRadius);
    }

    private static bool WithinCasterDistance(in Candidate c, Vector3 cameraPosition, float maxDist)
    {
        if (float.IsPositiveInfinity(maxDist))
        {
            return true;
        }

        var dx = c.BoundsCenter.X - cameraPosition.X;
        var dz = c.BoundsCenter.Z - cameraPosition.Z;
        var dist = MathF.Sqrt(dx * dx + dz * dz);
        return dist - c.BoundsRadius <= maxDist;
    }

    private static void CollectNearest(
        IReadOnlyList<Candidate> candidates,
        Vector3 cameraPosition,
        int fallbackCount,
        bool fullOnly,
        List<int> selected,
        float maxDist = float.PositiveInfinity)
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

            if (!WithinCasterDistance(c, cameraPosition, maxDist))
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
        // Coarser LOD first as solid underlay; Full draws last and may dither-out at its edge.
        if (c.Lod != TerrainChunkLodKind.Full)
        {
            return TerrainResidencyKey.MaxLodLevel - (int)c.Lod;
        }

        return c.NearPom
            ? TerrainResidencyKey.MaxLodLevel + 1
            : TerrainResidencyKey.MaxLodLevel + 2;
    }
}
