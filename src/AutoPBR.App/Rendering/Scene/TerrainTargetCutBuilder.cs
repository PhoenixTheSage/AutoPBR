namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Builds a globally aligned quadtree leaf cut for the camera's required terrain domain.
/// The cut contains no parent/descendant overlap; temporary LOD overlap belongs in
/// <see cref="TerrainCoverageGraph"/>, not in the target.
/// </summary>
public sealed class TerrainTargetCutBuilder
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The instance facade allows demand planners to receive a target-cut builder.")]
    public IReadOnlySet<TerrainResidencyKey> Build(
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks,
        int lodRingChunks) =>
        BuildCameraTarget(cameraChunk, hardRadiusChunks, lodRingChunks);

    public static IReadOnlySet<TerrainResidencyKey> BuildCameraTarget(
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks,
        int lodRingChunks)
    {
        hardRadiusChunks = Math.Max(0, hardRadiusChunks);
        lodRingChunks = Math.Max(0, lodRingChunks);

        Span<TerrainChunkStreamer.LodBand> bands =
            stackalloc TerrainChunkStreamer.LodBand[TerrainResidencyKey.MaxLodLevel];
        var bandCount = TerrainChunkStreamer.ResolveLodBands(hardRadiusChunks, lodRingChunks, bands);
        var outerRadius = checked(hardRadiusChunks + lodRingChunks);
        var rootLevel = bandCount == 0 ? (byte)0 : bands[bandCount - 1].Level;
        var rootSide = TerrainResidencyKey.ChunksPerSideForLevel(rootLevel);
        var minSectionX = TerrainResidencyKey.FloorDiv(cameraChunk.X - outerRadius, rootSide);
        var maxSectionX = TerrainResidencyKey.FloorDiv(cameraChunk.X + outerRadius, rootSide);
        var minSectionZ = TerrainResidencyKey.FloorDiv(cameraChunk.Z - outerRadius, rootSide);
        var maxSectionZ = TerrainResidencyKey.FloorDiv(cameraChunk.Z + outerRadius, rootSide);
        var leaves = new HashSet<TerrainResidencyKey>();

        for (var z = minSectionZ; z <= maxSectionZ; z++)
        {
            for (var x = minSectionX; x <= maxSectionX; x++)
            {
                AddLeaves(
                    TerrainResidencyKey.Section(x, z, rootLevel),
                    cameraChunk,
                    outerRadius,
                    hardRadiusChunks,
                    bands[..bandCount],
                    leaves);
            }
        }

        return leaves;
    }

    /// <summary>Throws when keys are invalid, duplicated, or contain ancestor overlap.</summary>
    public static void ValidateStrictCut(IEnumerable<TerrainResidencyKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var cut = new HashSet<TerrainResidencyKey>();
        foreach (var key in keys)
        {
            if (key.LodLevel > TerrainResidencyKey.MaxLodLevel)
            {
                throw new ArgumentException(
                    $"Terrain key {key} exceeds maximum LOD {TerrainResidencyKey.MaxLodLevel}.",
                    nameof(keys));
            }

            if (!cut.Add(key))
            {
                throw new ArgumentException($"Terrain target contains duplicate key {key}.", nameof(keys));
            }
        }

        foreach (var leaf in cut)
        {
            var ancestor = leaf;
            while (ancestor.LodLevel < TerrainResidencyKey.MaxLodLevel)
            {
                ancestor = ParentOf(ancestor);
                if (cut.Contains(ancestor))
                {
                    throw new ArgumentException(
                        $"Terrain target overlaps descendant {leaf} with ancestor {ancestor}.",
                        nameof(keys));
                }
            }
        }
    }

    /// <summary>
    /// Strictly validates the cut and proves exactly-one target coverage for every required cell.
    /// Finer alignment leaves may extend beyond the required square.
    /// </summary>
    public static void ValidateCameraTarget(
        IEnumerable<TerrainResidencyKey> keys,
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks,
        int lodRingChunks)
    {
        ArgumentNullException.ThrowIfNull(keys);
        hardRadiusChunks = Math.Max(0, hardRadiusChunks);
        lodRingChunks = Math.Max(0, lodRingChunks);
        var outerRadius = checked(hardRadiusChunks + lodRingChunks);
        var cut = keys as IReadOnlySet<TerrainResidencyKey> ?? new HashSet<TerrainResidencyKey>(keys);
        ValidateStrictCut(cut);

        for (var z = cameraChunk.Z - outerRadius; z <= cameraChunk.Z + outerRadius; z++)
        {
            for (var x = cameraChunk.X - outerRadius; x <= cameraChunk.X + outerRadius; x++)
            {
                var count = CountCoveringLeaves(cut, x, z);
                if (count != 1)
                {
                    throw new ArgumentException(
                        $"Required terrain cell ({x}, {z}) has {count} target leaves; expected exactly one.",
                        nameof(keys));
                }
            }
        }
    }

    public static bool CoversCell(
        IReadOnlySet<TerrainResidencyKey> cut,
        int chunkX,
        int chunkZ) =>
        CountCoveringLeaves(cut, chunkX, chunkZ) != 0;

    public static int CountCoveringLeaves(
        IReadOnlySet<TerrainResidencyKey> cut,
        int chunkX,
        int chunkZ)
    {
        ArgumentNullException.ThrowIfNull(cut);
        var count = 0;
        var chunk = new TerrainChunkKey(chunkX, chunkZ);
        for (byte level = 0; level <= TerrainResidencyKey.MaxLodLevel; level++)
        {
            if (cut.Contains(TerrainResidencyKey.FromChunk(chunk, level)))
            {
                count++;
            }
        }

        return count;
    }

    public static TerrainResidencyKey ParentOf(TerrainResidencyKey child)
    {
        if (child.LodLevel >= TerrainResidencyKey.MaxLodLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(child), "The coarsest terrain key has no parent.");
        }

        return TerrainResidencyKey.Section(
            TerrainResidencyKey.FloorDiv(child.X, 2),
            TerrainResidencyKey.FloorDiv(child.Z, 2),
            (byte)(child.LodLevel + 1));
    }

    public static IReadOnlyList<TerrainResidencyKey> ChildrenOf(TerrainResidencyKey parent)
    {
        if (parent.LodLevel == 0 || parent.LodLevel > TerrainResidencyKey.MaxLodLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(parent), "Only valid LOD parents have children.");
        }

        var childLevel = (byte)(parent.LodLevel - 1);
        var x = checked(parent.X * 2);
        var z = checked(parent.Z * 2);
        return
        [
            CreateKey(x, z, childLevel),
            CreateKey(x + 1, z, childLevel),
            CreateKey(x, z + 1, childLevel),
            CreateKey(x + 1, z + 1, childLevel),
        ];
    }

    private static TerrainResidencyKey CreateKey(int x, int z, byte level) =>
        level == 0
            ? TerrainResidencyKey.Full(x, z)
            : TerrainResidencyKey.Section(x, z, level);

    private static void AddLeaves(
        TerrainResidencyKey node,
        TerrainChunkKey cameraChunk,
        int outerRadius,
        int hardRadius,
        ReadOnlySpan<TerrainChunkStreamer.LodBand> bands,
        HashSet<TerrainResidencyKey> leaves)
    {
        var closest = node.ChebyshevDistanceToChunk(cameraChunk);
        if (closest > outerRadius)
        {
            return;
        }

        var desiredLevel = ResolveDesiredLevel(closest, hardRadius, bands);
        if (node.LodLevel <= desiredLevel || node.LodLevel == 0)
        {
            leaves.Add(node);
            return;
        }

        foreach (var child in ChildrenOf(node))
        {
            AddLeaves(child, cameraChunk, outerRadius, hardRadius, bands, leaves);
        }
    }

    private static byte ResolveDesiredLevel(
        int distance,
        int hardRadius,
        ReadOnlySpan<TerrainChunkStreamer.LodBand> bands)
    {
        if (distance <= hardRadius)
        {
            return 0;
        }

        for (var i = 0; i < bands.Length; i++)
        {
            if (distance <= bands[i].DMax)
            {
                return bands[i].Level;
            }
        }

        return bands.IsEmpty ? (byte)0 : bands[^1].Level;
    }
}
