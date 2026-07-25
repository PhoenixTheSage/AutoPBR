
namespace AutoPBR.Preview;

/// <summary>
/// Origin-centered block mesh baked from pack/install model JSON (or parity synthesis),
/// split into per-material-slot quads for terrain vegetation stamping.
/// </summary>
public sealed class PreviewTerrainBlockModelTemplate
{
    public required string SourceArchivePath { get; init; }

    public required string ProvenanceDetail { get; init; }

    /// <summary>Concatenated quads (4 verts × 12 floats) keyed by terrain material slot.</summary>
    public required IReadOnlyDictionary<int, float[]> VerticesBySlot { get; init; }

    public bool HasGeometry => VerticesBySlot.Count > 0;
}

/// <summary>Per-species log / leaves (or cactus) templates.</summary>
public sealed class PreviewTerrainSpeciesModelTemplates
{
    public required PreviewTerrainTreeSpecies Species { get; init; }

    public PreviewTerrainBlockModelTemplate? LogOrCactus { get; init; }

    public PreviewTerrainBlockModelTemplate? Leaves { get; init; }
}

/// <summary>Cached block-model templates for a vegetation kit identity.</summary>
public sealed class PreviewTerrainVegetationModelTemplates
{
    public required string Identity { get; init; }

    public required IReadOnlyDictionary<PreviewTerrainTreeSpecies, PreviewTerrainSpeciesModelTemplates> BySpecies
    {
        get;
        init;
    }

    public static PreviewTerrainVegetationModelTemplates Empty { get; } = new()
    {
        Identity = "veg-empty",
        BySpecies = new Dictionary<PreviewTerrainTreeSpecies, PreviewTerrainSpeciesModelTemplates>(),
    };

    public bool HasAny => BySpecies.Count > 0;

    public bool TryGet(PreviewTerrainTreeSpecies species, out PreviewTerrainSpeciesModelTemplates templates) =>
        BySpecies.TryGetValue(species, out templates!);
}

/// <summary>
/// Builds terrain vegetation block meshes from respective Minecraft model JSON
/// (pack → install composite), matching the Explore preview resolve/bake path.
/// </summary>
public static class PreviewTerrainBlockModelTemplates
{
    internal static PreviewTerrainVegetationModelTemplates TryBuild(
        IAssetSource source,
        IReadOnlyList<PreviewTerrainVegetationSpeciesKit> species,
        string identity)
    {
        if (species.Count == 0)
        {
            return PreviewTerrainVegetationModelTemplates.Empty;
        }

        var map = new Dictionary<PreviewTerrainTreeSpecies, PreviewTerrainSpeciesModelTemplates>();
        foreach (var kit in species)
        {
            if (kit.IsCactus)
            {
                var cactus = TryBakeTemplate(
                    source,
                    kit.LogArchivePath,
                    BuildCactusPathMaps(kit),
                    BuildCactusSizeMaps(kit));
                if (cactus is null)
                {
                    continue;
                }

                map[kit.Species] = new PreviewTerrainSpeciesModelTemplates
                {
                    Species = kit.Species,
                    LogOrCactus = cactus,
                };
                continue;
            }

            var pathMaps = BuildWoodPathMaps(kit);
            var sizeMaps = BuildWoodSizeMaps(kit);
            var log = TryBakeTemplate(source, kit.LogArchivePath, pathMaps, sizeMaps);
            var leaves = TryBakeTemplate(source, kit.LeavesOrTopArchivePath, pathMaps, sizeMaps);
            if (log is null && leaves is null)
            {
                continue;
            }

            map[kit.Species] = new PreviewTerrainSpeciesModelTemplates
            {
                Species = kit.Species,
                LogOrCactus = log,
                Leaves = leaves,
            };
        }

        return map.Count == 0
            ? PreviewTerrainVegetationModelTemplates.Empty
            : new PreviewTerrainVegetationModelTemplates
            {
                Identity = identity,
                BySpecies = map,
            };
    }

    /// <summary>
    /// Translates an origin-centered template into terrain material buckets at block
    /// (<paramref name="bx"/>, <paramref name="by"/>, <paramref name="bz"/>).
    /// </summary>
    public static void Stamp(
        PreviewTerrainBlockModelTemplate template,
        int bx,
        int by,
        int bz,
        float surfaceWorldY,
        List<float>[] buckets)
    {
        var ox = bx + 0.5f;
        var oy = surfaceWorldY + by - 0.5f;
        var oz = bz + 0.5f;
        foreach (var (slot, verts) in template.VerticesBySlot)
        {
            if ((uint)slot >= (uint)buckets.Length || verts.Length < PreviewMesh.FloatsPerVertex * 4)
            {
                continue;
            }

            var dest = buckets[slot];
            for (var i = 0; i + PreviewMesh.FloatsPerVertex <= verts.Length; i += PreviewMesh.FloatsPerVertex)
            {
                dest.Add(verts[i] + ox);
                dest.Add(verts[i + 1] + oy);
                dest.Add(verts[i + 2] + oz);
                for (var k = 3; k < PreviewMesh.FloatsPerVertex; k++)
                {
                    dest.Add(verts[i + k]);
                }
            }
        }
    }

    private static PreviewTerrainBlockModelTemplate? TryBakeTemplate(
        IAssetSource source,
        string primaryTextureArchivePath,
        Dictionary<string, int> pathToSlot,
        Dictionary<string, (int w, int h)> pathToSize)
    {
        if (!TryResolveMergedModel(source, primaryTextureArchivePath, out var model, out var ns, out var detail))
        {
            return null;
        }

        // Ensure case-insensitive lookups match baker zip paths.
        var slotMap = new Dictionary<string, int>(pathToSlot, StringComparer.OrdinalIgnoreCase);
        var sizeMap = new Dictionary<string, (int w, int h)>(pathToSize, StringComparer.OrdinalIgnoreCase);

        if (!MinecraftModelBaker.TryBake(model, ns, slotMap, sizeMap, out var vertices, out var indices, out var batches) ||
            vertices.Length == 0 ||
            indices.Length == 0 ||
            batches.Count == 0)
        {
            return null;
        }

        var bySlot = SplitBakeIntoSlotQuads(vertices, indices, batches);
        if (bySlot.Count == 0)
        {
            return null;
        }

        return new PreviewTerrainBlockModelTemplate
        {
            SourceArchivePath = primaryTextureArchivePath,
            ProvenanceDetail = detail,
            VerticesBySlot = bySlot,
        };
    }

    internal static bool TryResolveMergedModel(
        IAssetSource source,
        string textureArchivePath,
        out MergedJavaBlockModel model,
        out string defaultNamespace,
        out string provenanceDetail)
    {
        model = null!;
        defaultNamespace = "minecraft";
        provenanceDetail = string.Empty;

        if (JavaModelPathResolver.TryResolveModelJsonPathsFromTexture(
                source,
                textureArchivePath,
                out var modelJsonPaths,
                out var ns) &&
            MinecraftModelMerger.TryMergeMany(source, modelJsonPaths, out var merged))
        {
            model = merged;
            defaultNamespace = ns;
            provenanceDetail = modelJsonPaths[0];
            return true;
        }

        if (VanillaBlockPreviewRuntime.TryBuildSyntheticMesh(
                textureArchivePath,
                out var synthetic,
                out var provenance,
                out _,
                out var synNs))
        {
            model = synthetic;
            defaultNamespace = synNs;
            provenanceDetail = provenance.Detail ?? "parity-synthesis";
            return true;
        }

        return false;
    }

    private static Dictionary<int, float[]> SplitBakeIntoSlotQuads(
        float[] vertices,
        uint[] indices,
        List<PreviewDrawBatch> batches)
    {
        var bySlot = new Dictionary<int, List<float>>();
        foreach (var batch in batches)
        {
            if (!bySlot.TryGetValue(batch.MaterialIndex, out var list))
            {
                list = new List<float>(batch.IndexCount / 6 * PreviewMesh.FloatsPerVertex * 4);
                bySlot[batch.MaterialIndex] = list;
            }

            for (var i = 0; i + 5 < batch.IndexCount; i += 6)
            {
                var baseIndex = batch.FirstIndex + i;
                // Baker emits one face as four consecutive verts, then either:
                //   forward: 0,1,2, 0,2,3  (TryConcatMaterialBuckets order)
                //   reverse: 0,2,1, 0,3,2  (BlockOrItemBaseline.ReverseFaceWinding)
                // Reconstruct corners so terrain's forward triangulation keeps CCW outward faces.
                if (!TryResolveFaceCorners(
                        indices[baseIndex],
                        indices[baseIndex + 1],
                        indices[baseIndex + 2],
                        indices[baseIndex + 3],
                        indices[baseIndex + 4],
                        indices[baseIndex + 5],
                        out var c0,
                        out var c1,
                        out var c2,
                        out var c3))
                {
                    continue;
                }

                AppendVertex(list, vertices, c0);
                AppendVertex(list, vertices, c1);
                AppendVertex(list, vertices, c2);
                AppendVertex(list, vertices, c3);
            }
        }

        var result = new Dictionary<int, float[]>(bySlot.Count);
        foreach (var (slot, list) in bySlot)
        {
            if (list.Count >= PreviewMesh.FloatsPerVertex * 4)
            {
                result[slot] = [.. list];
            }
        }

        return result;
    }

    /// <summary>
    /// Maps a baker face's six indices to four corners ordered for
    /// <c>0,1,2 / 0,2,3</c> triangulation (same as terrain greedy mesh).
    /// </summary>
    internal static bool TryResolveFaceCorners(
        uint i0,
        uint i1,
        uint i2,
        uint i3,
        uint i4,
        uint i5,
        out uint c0,
        out uint c1,
        out uint c2,
        out uint c3)
    {
        c0 = i0;
        c1 = c2 = c3 = 0;
        if (i3 != i0)
        {
            return false;
        }

        // Forward winding: 0,1,2, 0,2,3
        if (i4 == i2)
        {
            c1 = i1;
            c2 = i2;
            c3 = i5;
            return c1 != c0 && c2 != c0 && c3 != c0 && c1 != c2 && c1 != c3 && c2 != c3;
        }

        // Reverse winding: 0,2,1, 0,3,2 → store [0,3,2,1] so forward tris match.
        if (i5 == i1)
        {
            c1 = i4;
            c2 = i1;
            c3 = i2;
            return c1 != c0 && c2 != c0 && c3 != c0 && c1 != c2 && c1 != c3 && c2 != c3;
        }

        return false;
    }

    private static void AppendVertex(List<float> dest, float[] vertices, uint index)
    {
        var o = (int)index * PreviewMesh.FloatsPerVertex;
        if (o < 0 || o + PreviewMesh.FloatsPerVertex > vertices.Length)
        {
            return;
        }

        for (var k = 0; k < PreviewMesh.FloatsPerVertex; k++)
        {
            dest.Add(vertices[o + k]);
        }
    }

    private static Dictionary<string, int> BuildWoodPathMaps(PreviewTerrainVegetationSpeciesKit kit)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [Normalize(kit.LogArchivePath)] = kit.LogSlot,
            [Normalize(kit.LeavesOrTopArchivePath)] = kit.LeavesOrTopSlot,
        };
        if (kit is { LogTopSlot: { } topSlot, LogTopArchivePath: { Length: > 0 } logTopPath } &&
            !string.IsNullOrWhiteSpace(logTopPath))
        {
            map[Normalize(logTopPath)] = topSlot;
        }
        else
        {
            // Cube-column end face often references *_log_top even when the kit only has side.
            var inferredTop = PreviewTerrainVegetationKitResolver.LogTopArchivePath(kit.TextureStem);
            map.TryAdd(Normalize(inferredTop), kit.LogSlot);
        }

        return map;
    }

    private static Dictionary<string, (int w, int h)> BuildWoodSizeMaps(PreviewTerrainVegetationSpeciesKit kit)
    {
        var map = new Dictionary<string, (int w, int h)>(StringComparer.OrdinalIgnoreCase)
        {
            [Normalize(kit.LogArchivePath)] = (kit.LogMaps.Width, kit.LogMaps.Height),
            [Normalize(kit.LeavesOrTopArchivePath)] =
                (kit.LeavesOrTopMaps.Width, kit.LeavesOrTopMaps.Height),
        };
        if (kit is { LogTopMaps: not null, LogTopArchivePath: not null and var logTopPath } &&
            !string.IsNullOrWhiteSpace(logTopPath))
        {
            map[Normalize(logTopPath)] = (kit.LogTopMaps.Width, kit.LogTopMaps.Height);
        }
        else
        {
            var inferredTop = PreviewTerrainVegetationKitResolver.LogTopArchivePath(kit.TextureStem);
            map.TryAdd(Normalize(inferredTop), (kit.LogMaps.Width, kit.LogMaps.Height));
        }

        return map;
    }

    private static Dictionary<string, int> BuildCactusPathMaps(PreviewTerrainVegetationSpeciesKit kit)
    {
        var side = Normalize(kit.LogArchivePath);
        var top = Normalize(kit.LeavesOrTopArchivePath);
        var bottom = Normalize(PreviewTerrainVegetationKitResolver.BlockTexturesPrefix + "cactus_bottom.png");
        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [side] = kit.LogSlot,
            [top] = kit.LeavesOrTopSlot,
            [bottom] = kit.LeavesOrTopSlot, // alias bottom → top slot
        };
    }

    private static Dictionary<string, (int w, int h)> BuildCactusSizeMaps(PreviewTerrainVegetationSpeciesKit kit)
    {
        var side = Normalize(kit.LogArchivePath);
        var top = Normalize(kit.LeavesOrTopArchivePath);
        var bottom = Normalize(PreviewTerrainVegetationKitResolver.BlockTexturesPrefix + "cactus_bottom.png");
        var sideSize = (kit.LogMaps.Width, kit.LogMaps.Height);
        var topSize = (kit.LeavesOrTopMaps.Width, kit.LeavesOrTopMaps.Height);
        return new Dictionary<string, (int w, int h)>(StringComparer.OrdinalIgnoreCase)
        {
            [side] = sideSize,
            [top] = topSize,
            [bottom] = topSize,
        };
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}

/// <summary>Shared float stride for terrain / model bake vertices.</summary>
file static class PreviewMesh
{
    public const int FloatsPerVertex = 12;
}
