using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Emits tree / cactus geometry into terrain material buckets, preferring
/// origin-centered meshes baked from each block's model JSON (Explore path),
/// with cuboid fallback when a template is missing.
/// Full + LOD1 use Full voxel occupancy. LOD≥2 keeps the same placement roots but may
/// stamp crossed-plane impostors so distant rings stay VRAM-tractable.
/// When <c>smartLeaves</c> is on (OptiFine-style), FullVoxel leaf cubes omit faces buried
/// against adjacent leaf voxels across all placements in the bake.
/// </summary>
public static class PreviewTerrainTreeMeshEmitter
{
    public static int EmitPlacements(
        IReadOnlyList<PreviewTerrainTreePlacer.Placement> placements,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        ref int maxRelativeHeight,
        PreviewTerrainVegetationModelTemplates? modelTemplates = null,
        PreviewTerrainVegetationEmitMode emitMode = PreviewTerrainVegetationEmitMode.FullVoxel,
        bool smartLeaves = true)
    {
        if (placements.Count == 0 || buckets.Length == 0)
        {
            return 0;
        }

        if (metersPerTile <= 1e-6f)
        {
            metersPerTile = PreviewStageConstants.MetersPerGrassTile;
        }

        var templates = modelTemplates is { HasAny: true }
            ? modelTemplates
            : PreviewTerrainVegetationModelTemplates.Empty;

        HashSet<(int X, int Y, int Z)>? leafVoxels = null;
        if (emitMode == PreviewTerrainVegetationEmitMode.FullVoxel && smartLeaves)
        {
            leafVoxels = new HashSet<(int X, int Y, int Z)>();
            for (var i = 0; i < placements.Count; i++)
            {
                CollectLeafVoxels(placements[i], leafVoxels);
            }
        }

        var emittedBlocks = 0;
        foreach (var placement in placements)
        {
            emittedBlocks += emitMode == PreviewTerrainVegetationEmitMode.Impostor
                ? EmitImpostor(
                    placement,
                    surfaceWorldY,
                    metersPerTile,
                    buckets,
                    ref maxRelativeHeight)
                : EmitOne(
                    placement,
                    surfaceWorldY,
                    metersPerTile,
                    buckets,
                    ref maxRelativeHeight,
                    templates,
                    leafVoxels);
        }

        return emittedBlocks;
    }

    /// <summary>
    /// Crossed vertical planes at the placement root (trunk + canopy). Same XZ as Full voxel
    /// trees; far cheaper than stamping every canopy block.
    /// </summary>
    private static int EmitImpostor(
        in PreviewTerrainTreePlacer.Placement placement,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        ref int maxRelativeHeight)
    {
        var shape = PreviewTerrainTreeSpeciesRules.GetShape(placement.Species);
        var logSlot = placement.Materials.LogSlot;
        var leafSlot = placement.Materials.LeavesOrTopSlot;
        var cx = placement.RootX + 0.5f;
        var cz = placement.RootZ + 0.5f;
        var yBase = surfaceWorldY + placement.SurfaceHeight;
        var yTrunkTop = yBase + Math.Max(1, placement.TrunkHeight);

        if (placement.Species == PreviewTerrainTreeSpecies.Cactus ||
            shape.Kind == PreviewTerrainTreeShapeKind.Column)
        {
            EmitVerticalCross(
                cx, cz, yBase, yTrunkTop, halfWidth: 0.35f, logSlot, metersPerTile, buckets);
            maxRelativeHeight = Math.Max(maxRelativeHeight, placement.SurfaceHeight + placement.TrunkHeight);
            return 1;
        }

        var canopyR = Math.Max(1, shape.CanopyRadius);
        var half = canopyR + 0.5f;
        EmitVerticalCross(
            cx, cz, yBase, yTrunkTop, halfWidth: 0.2f, logSlot, metersPerTile, buckets);
        var canopyY0 = yTrunkTop - half * 0.35f;
        var canopyY1 = yTrunkTop + half;
        EmitVerticalCross(
            cx, cz, canopyY0, canopyY1, half, leafSlot, metersPerTile, buckets);
        maxRelativeHeight = Math.Max(
            maxRelativeHeight,
            placement.SurfaceHeight + placement.TrunkHeight + canopyR + 1);
        return 1;
    }

    private static void EmitVerticalCross(
        float cx,
        float cz,
        float y0,
        float y1,
        float halfWidth,
        int slot,
        float metersPerTile,
        List<float>[] buckets)
    {
        if (y1 <= y0 + 1e-3f || halfWidth <= 1e-3f)
        {
            return;
        }

        // X-facing plane (extends in Z)
        EmitWorldQuad(
            new Vector3(cx, y0, cz - halfWidth),
            new Vector3(cx, y0, cz + halfWidth),
            new Vector3(cx, y1, cz + halfWidth),
            new Vector3(cx, y1, cz - halfWidth),
            Vector3.UnitX,
            slot,
            metersPerTile,
            buckets);
        EmitWorldQuad(
            new Vector3(cx, y0, cz + halfWidth),
            new Vector3(cx, y0, cz - halfWidth),
            new Vector3(cx, y1, cz - halfWidth),
            new Vector3(cx, y1, cz + halfWidth),
            -Vector3.UnitX,
            slot,
            metersPerTile,
            buckets);

        // Z-facing plane (extends in X)
        EmitWorldQuad(
            new Vector3(cx - halfWidth, y0, cz),
            new Vector3(cx + halfWidth, y0, cz),
            new Vector3(cx + halfWidth, y1, cz),
            new Vector3(cx - halfWidth, y1, cz),
            Vector3.UnitZ,
            slot,
            metersPerTile,
            buckets);
        EmitWorldQuad(
            new Vector3(cx + halfWidth, y0, cz),
            new Vector3(cx - halfWidth, y0, cz),
            new Vector3(cx - halfWidth, y1, cz),
            new Vector3(cx + halfWidth, y1, cz),
            -Vector3.UnitZ,
            slot,
            metersPerTile,
            buckets);
    }

    private static void EmitWorldQuad(
        Vector3 c0,
        Vector3 c1,
        Vector3 c2,
        Vector3 c3,
        Vector3 normal,
        int slot,
        float metersPerTile,
        List<float>[] buckets)
    {
        if ((uint)slot >= (uint)buckets.Length)
        {
            return;
        }

        Span<Vector3> corners = stackalloc Vector3[4];
        Span<Vector2> uvs = stackalloc Vector2[4];
        corners[0] = c0;
        corners[1] = c1;
        corners[2] = c2;
        corners[3] = c3;
        for (var i = 0; i < 4; i++)
        {
            // Vertical impostors: U along horizontal span, V along height.
            uvs[i] = MathF.Abs(normal.X) > 0.5f
                ? new Vector2(corners[i].Z / metersPerTile, corners[i].Y / metersPerTile)
                : new Vector2(corners[i].X / metersPerTile, corners[i].Y / metersPerTile);
        }

        var tangent = MathF.Abs(normal.X) > 0.5f
            ? new Vector3(0, 0, -MathF.Sign(normal.X == 0 ? 1 : normal.X))
            : Vector3.UnitX;
        AddSolidFace(normal, tangent, 1f, corners, uvs, buckets[slot]);
    }

    private static int EmitOne(
        in PreviewTerrainTreePlacer.Placement placement,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        ref int maxRelativeHeight,
        PreviewTerrainVegetationModelTemplates templates,
        HashSet<(int X, int Y, int Z)>? leafVoxels)
    {
        var shape = PreviewTerrainTreeSpeciesRules.GetShape(placement.Species);
        if (placement.Species == PreviewTerrainTreeSpecies.Cactus ||
            shape.Kind == PreviewTerrainTreeShapeKind.Column)
        {
            return EmitCactus(
                placement, surfaceWorldY, metersPerTile, buckets, ref maxRelativeHeight, templates);
        }

        return shape.Kind switch
        {
            PreviewTerrainTreeShapeKind.Conical =>
                EmitConical(
                    placement, shape, surfaceWorldY, metersPerTile, buckets, ref maxRelativeHeight,
                    templates, leafVoxels),
            PreviewTerrainTreeShapeKind.FlatCanopy =>
                EmitFlatCanopy(
                    placement, shape, surfaceWorldY, metersPerTile, buckets, ref maxRelativeHeight,
                    templates, leafVoxels),
            _ => EmitRoundCanopy(
                placement, shape, surfaceWorldY, metersPerTile, buckets, ref maxRelativeHeight,
                templates, leafVoxels),
        };
    }

    private static void CollectLeafVoxels(
        in PreviewTerrainTreePlacer.Placement placement,
        HashSet<(int X, int Y, int Z)> leafVoxels)
    {
        var shape = PreviewTerrainTreeSpeciesRules.GetShape(placement.Species);
        if (placement.Species == PreviewTerrainTreeSpecies.Cactus ||
            shape.Kind == PreviewTerrainTreeShapeKind.Column)
        {
            return;
        }

        switch (shape.Kind)
        {
            case PreviewTerrainTreeShapeKind.Conical:
                CollectConicalLeaves(placement, shape, leafVoxels);
                break;
            case PreviewTerrainTreeShapeKind.FlatCanopy:
                CollectFlatCanopyLeaves(placement, shape, leafVoxels);
                break;
            default:
                CollectRoundCanopyLeaves(placement, shape, leafVoxels);
                break;
        }
    }

    private static void CollectRoundCanopyLeaves(
        in PreviewTerrainTreePlacer.Placement placement,
        PreviewTerrainTreeSpeciesRules.ShapeProfile shape,
        HashSet<(int X, int Y, int Z)> leafVoxels)
    {
        var trunkTopY = placement.SurfaceHeight + placement.TrunkHeight;
        var canopyY = trunkTopY + 1;
        var radius = shape.CanopyRadius;
        var r2 = radius * radius + 1;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var by = canopyY + dy;
                    if (dx == 0 && dz == 0 && by <= trunkTopY)
                    {
                        continue;
                    }

                    var dist2 = dx * dx + dy * dy + dz * dz;
                    if (dist2 > r2)
                    {
                        continue;
                    }

                    if (dist2 >= r2 - 1 && ((placement.VariantSalt ^ (dx * 31 + dz * 17 + dy)) & 7) == 0)
                    {
                        continue;
                    }

                    leafVoxels.Add((placement.RootX + dx, by, placement.RootZ + dz));
                }
            }
        }
    }

    private static void CollectConicalLeaves(
        in PreviewTerrainTreePlacer.Placement placement,
        PreviewTerrainTreeSpeciesRules.ShapeProfile shape,
        HashSet<(int X, int Y, int Z)> leafVoxels)
    {
        var trunkTopY = placement.SurfaceHeight + placement.TrunkHeight;
        var baseY = placement.SurfaceHeight + Math.Max(2, placement.TrunkHeight / 3);
        var tipY = trunkTopY + 2;
        for (var by = baseY; by <= tipY; by++)
        {
            var t = tipY == baseY ? 1f : (tipY - by) / (float)(tipY - baseY);
            var radius = Math.Max(0, (int)Math.Round(shape.CanopyRadius * t));
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0 && by <= trunkTopY)
                    {
                        continue;
                    }

                    if (Math.Abs(dx) + Math.Abs(dz) > radius + 1)
                    {
                        continue;
                    }

                    leafVoxels.Add((placement.RootX + dx, by, placement.RootZ + dz));
                }
            }
        }
    }

    private static void CollectFlatCanopyLeaves(
        in PreviewTerrainTreePlacer.Placement placement,
        PreviewTerrainTreeSpeciesRules.ShapeProfile shape,
        HashSet<(int X, int Y, int Z)> leafVoxels)
    {
        var branchReach = 1 + (placement.VariantSalt & 1);
        var dir = (placement.VariantSalt >> 2) & 3;
        var bx = placement.RootX + (dir switch { 0 => branchReach, 1 => -branchReach, _ => 0 });
        var bz = placement.RootZ + (dir switch { 2 => branchReach, 3 => -branchReach, _ => 0 });
        var canopyY = placement.SurfaceHeight + placement.TrunkHeight + 1;
        var radius = shape.CanopyRadius;
        for (var dy = 0; dy <= 1; dy++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) == radius && Math.Abs(dz) == radius)
                    {
                        continue;
                    }

                    leafVoxels.Add((bx + dx, canopyY + dy, bz + dz));
                }
            }
        }
    }

    private static bool TryStampBlock(
        PreviewTerrainVegetationModelTemplates templates,
        PreviewTerrainTreeSpecies species,
        bool leaves,
        int bx,
        int by,
        int bz,
        float surfaceWorldY,
        List<float>[] buckets)
    {
        if (!templates.TryGet(species, out var speciesTemplates))
        {
            return false;
        }

        var template = leaves ? speciesTemplates.Leaves : speciesTemplates.LogOrCactus;
        if (template is not { HasGeometry: true })
        {
            return false;
        }

        PreviewTerrainBlockModelTemplates.Stamp(template, bx, by, bz, surfaceWorldY, buckets);
        return true;
    }

    private static void EmitVegetationBlock(
        in PreviewTerrainTreePlacer.Placement placement,
        int bx,
        int by,
        int bz,
        bool leaves,
        int sideSlot,
        int ySlot,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        PreviewTerrainVegetationModelTemplates templates,
        HashSet<(int X, int Y, int Z)>? leafVoxels = null)
    {
        // Smart-leaves needs per-face neighbor tests; model-JSON stamps emit fixed quads.
        if (leafVoxels is null || !leaves)
        {
            if (TryStampBlock(
                    templates,
                    placement.Species,
                    leaves,
                    bx,
                    by,
                    bz,
                    surfaceWorldY,
                    buckets))
            {
                return;
            }
        }

        if (placement.Species == PreviewTerrainTreeSpecies.Cactus)
        {
            EmitCactusBlock(bx, by, bz, sideSlot, ySlot, surfaceWorldY, metersPerTile, buckets);
            return;
        }

        EmitBlock(
            bx,
            by,
            bz,
            sideSlot,
            ySlot,
            surfaceWorldY,
            metersPerTile,
            buckets,
            leaves ? leafVoxels : null);
    }

    private static int EmitCactus(
        in PreviewTerrainTreePlacer.Placement placement,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        ref int maxRelativeHeight,
        PreviewTerrainVegetationModelTemplates templates)
    {
        var count = 0;
        var side = placement.Materials.LogSlot;
        var top = placement.Materials.LeavesOrTopSlot;
        for (var i = 1; i <= placement.TrunkHeight; i++)
        {
            var by = placement.SurfaceHeight + i;
            EmitVegetationBlock(
                placement,
                placement.RootX,
                by,
                placement.RootZ,
                leaves: false,
                side,
                top,
                surfaceWorldY,
                metersPerTile,
                buckets,
                templates);
            maxRelativeHeight = Math.Max(maxRelativeHeight, by);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Vanilla cactus geometry: full Y faces, N/S/E/W inset by 1/16 so side textures with alpha
    /// notches match Explore <c>cactus.json</c> corner correction.
    /// </summary>
    public static void EmitCactusBlock(
        int bx,
        int by,
        int bz,
        int sideSlot,
        int topSlot,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets)
    {
        const float inset = 1f / 16f;
        var y0 = surfaceWorldY + by - 1f;
        var y1 = surfaceWorldY + by;
        var x0 = bx;
        var x1 = bx + 1f;
        var z0 = bz;
        var z1 = bz + 1f;

        // Up / down — full footprint (vanilla core element).
        EmitAxisAlignedFace(
            axis: 1,
            positive: true,
            x0, x1, y1, y1, z0, z1,
            topSlot,
            metersPerTile,
            buckets);
        EmitAxisAlignedFace(
            axis: 1,
            positive: false,
            x0, x1, y0, y0, z0, z1,
            topSlot,
            metersPerTile,
            buckets);

        // North / south — inset on Z (vanilla NS element From.z=1, To.z=15).
        EmitAxisAlignedFace(
            axis: 2,
            positive: false,
            x0, x1, y0, y1, z0 + inset, z0 + inset,
            sideSlot,
            metersPerTile,
            buckets);
        EmitAxisAlignedFace(
            axis: 2,
            positive: true,
            x0, x1, y0, y1, z1 - inset, z1 - inset,
            sideSlot,
            metersPerTile,
            buckets);

        // West / east — inset on X (vanilla EW element From.x=1, To.x=15).
        EmitAxisAlignedFace(
            axis: 0,
            positive: false,
            x0 + inset, x0 + inset, y0, y1, z0, z1,
            sideSlot,
            metersPerTile,
            buckets);
        EmitAxisAlignedFace(
            axis: 0,
            positive: true,
            x1 - inset, x1 - inset, y0, y1, z0, z1,
            sideSlot,
            metersPerTile,
            buckets);
    }

    private static void EmitAxisAlignedFace(
        int axis,
        bool positive,
        float x0,
        float x1,
        float y0,
        float y1,
        float z0,
        float z1,
        int slot,
        float metersPerTile,
        List<float>[] buckets)
    {
        if ((uint)slot >= (uint)buckets.Length)
        {
            return;
        }

        Span<Vector3> corners = stackalloc Vector3[4];
        Span<Vector2> uvs = stackalloc Vector2[4];
        Vector3 n;
        Vector3 tFallback;
        float wSign = 1f;

        if (axis == 1)
        {
            var y = positive ? y1 : y0;
            if (positive)
            {
                n = Vector3.UnitY;
                tFallback = Vector3.UnitX;
                corners[0] = new(x0, y, z0);
                corners[1] = new(x1, y, z0);
                corners[2] = new(x1, y, z1);
                corners[3] = new(x0, y, z1);
            }
            else
            {
                n = -Vector3.UnitY;
                tFallback = Vector3.UnitX;
                wSign = -1f;
                corners[0] = new(x0, y, z1);
                corners[1] = new(x1, y, z1);
                corners[2] = new(x1, y, z0);
                corners[3] = new(x0, y, z0);
            }

            for (var i = 0; i < 4; i++)
            {
                uvs[i] = new(corners[i].X / metersPerTile, corners[i].Z / metersPerTile);
            }
        }
        else if (axis == 0)
        {
            var x = positive ? x1 : x0;
            if (positive)
            {
                n = Vector3.UnitX;
                tFallback = new Vector3(0, 0, -1);
                corners[0] = new(x, y0, z1);
                corners[1] = new(x, y0, z0);
                corners[2] = new(x, y1, z0);
                corners[3] = new(x, y1, z1);
            }
            else
            {
                n = -Vector3.UnitX;
                tFallback = new Vector3(0, 0, 1);
                wSign = -1f;
                corners[0] = new(x, y0, z0);
                corners[1] = new(x, y0, z1);
                corners[2] = new(x, y1, z1);
                corners[3] = new(x, y1, z0);
            }

            for (var i = 0; i < 4; i++)
            {
                uvs[i] = new(corners[i].Z / metersPerTile, corners[i].Y / metersPerTile);
            }
        }
        else
        {
            var z = positive ? z1 : z0;
            if (positive)
            {
                n = Vector3.UnitZ;
                tFallback = Vector3.UnitX;
                corners[0] = new(x0, y0, z);
                corners[1] = new(x1, y0, z);
                corners[2] = new(x1, y1, z);
                corners[3] = new(x0, y1, z);
            }
            else
            {
                n = -Vector3.UnitZ;
                tFallback = -Vector3.UnitX;
                wSign = -1f;
                corners[0] = new(x1, y0, z);
                corners[1] = new(x0, y0, z);
                corners[2] = new(x0, y1, z);
                corners[3] = new(x1, y1, z);
            }

            for (var i = 0; i < 4; i++)
            {
                uvs[i] = new(corners[i].X / metersPerTile, corners[i].Y / metersPerTile);
            }
        }

        AddSolidFace(n, tFallback, wSign, corners, uvs, buckets[slot]);
    }

    private static int EmitRoundCanopy(
        in PreviewTerrainTreePlacer.Placement placement,
        PreviewTerrainTreeSpeciesRules.ShapeProfile shape,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        ref int maxRelativeHeight,
        PreviewTerrainVegetationModelTemplates templates,
        HashSet<(int X, int Y, int Z)>? leafVoxels)
    {
        var count = EmitTrunkAndOptionalBranches(
            placement, shape, surfaceWorldY, metersPerTile, buckets, ref maxRelativeHeight, templates);
        var trunkTopY = placement.SurfaceHeight + placement.TrunkHeight;
        var canopyY = trunkTopY + 1;
        var radius = shape.CanopyRadius;
        var r2 = radius * radius + 1;
        var leafSlot = placement.Materials.LeavesOrTopSlot;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var by = canopyY + dy;
                    if (dx == 0 && dz == 0 && by <= trunkTopY)
                    {
                        continue;
                    }

                    var dist2 = dx * dx + dy * dy + dz * dz;
                    if (dist2 > r2)
                    {
                        continue;
                    }

                    if (dist2 >= r2 - 1 && ((placement.VariantSalt ^ (dx * 31 + dz * 17 + dy)) & 7) == 0)
                    {
                        continue;
                    }

                    EmitLeaf(
                        placement,
                        placement.RootX + dx,
                        by,
                        placement.RootZ + dz,
                        leafSlot,
                        surfaceWorldY,
                        metersPerTile,
                        buckets,
                        templates,
                        leafVoxels);
                    maxRelativeHeight = Math.Max(maxRelativeHeight, by);
                    count++;
                }
            }
        }

        return count;
    }

    private static int EmitConical(
        in PreviewTerrainTreePlacer.Placement placement,
        PreviewTerrainTreeSpeciesRules.ShapeProfile shape,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        ref int maxRelativeHeight,
        PreviewTerrainVegetationModelTemplates templates,
        HashSet<(int X, int Y, int Z)>? leafVoxels)
    {
        var count = EmitTrunkAndOptionalBranches(
            placement, shape, surfaceWorldY, metersPerTile, buckets, ref maxRelativeHeight, templates);
        var trunkTopY = placement.SurfaceHeight + placement.TrunkHeight;
        var baseY = placement.SurfaceHeight + Math.Max(2, placement.TrunkHeight / 3);
        var tipY = trunkTopY + 2;
        var leafSlot = placement.Materials.LeavesOrTopSlot;
        for (var by = baseY; by <= tipY; by++)
        {
            var t = tipY == baseY ? 1f : (tipY - by) / (float)(tipY - baseY);
            var radius = Math.Max(0, (int)Math.Round(shape.CanopyRadius * t));
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0 && by <= trunkTopY)
                    {
                        continue;
                    }

                    if (Math.Abs(dx) + Math.Abs(dz) > radius + 1)
                    {
                        continue;
                    }

                    EmitLeaf(
                        placement,
                        placement.RootX + dx,
                        by,
                        placement.RootZ + dz,
                        leafSlot,
                        surfaceWorldY,
                        metersPerTile,
                        buckets,
                        templates,
                        leafVoxels);
                    maxRelativeHeight = Math.Max(maxRelativeHeight, by);
                    count++;
                }
            }
        }

        return count;
    }

    private static int EmitFlatCanopy(
        in PreviewTerrainTreePlacer.Placement placement,
        PreviewTerrainTreeSpeciesRules.ShapeProfile shape,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        ref int maxRelativeHeight,
        PreviewTerrainVegetationModelTemplates templates,
        HashSet<(int X, int Y, int Z)>? leafVoxels)
    {
        var count = EmitTrunkAndOptionalBranches(
            placement, shape, surfaceWorldY, metersPerTile, buckets, ref maxRelativeHeight, templates);
        var branchReach = 1 + (placement.VariantSalt & 1);
        var dir = (placement.VariantSalt >> 2) & 3;
        var bx = placement.RootX + (dir switch { 0 => branchReach, 1 => -branchReach, _ => 0 });
        var bz = placement.RootZ + (dir switch { 2 => branchReach, 3 => -branchReach, _ => 0 });
        var canopyY = placement.SurfaceHeight + placement.TrunkHeight + 1;
        var sideSlot = ResolveLogSlot(placement, yFace: false);
        var topSlot = ResolveLogSlot(placement, yFace: true);
        EmitVegetationBlock(
            placement,
            bx,
            canopyY - 1,
            bz,
            leaves: false,
            sideSlot,
            topSlot,
            surfaceWorldY,
            metersPerTile,
            buckets,
            templates);
        count++;

        var radius = shape.CanopyRadius;
        var leafSlot = placement.Materials.LeavesOrTopSlot;
        for (var dy = 0; dy <= 1; dy++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) == radius && Math.Abs(dz) == radius)
                    {
                        continue;
                    }

                    var by = canopyY + dy;
                    EmitLeaf(
                        placement,
                        bx + dx,
                        by,
                        bz + dz,
                        leafSlot,
                        surfaceWorldY,
                        metersPerTile,
                        buckets,
                        templates,
                        leafVoxels);
                    maxRelativeHeight = Math.Max(maxRelativeHeight, by);
                    count++;
                }
            }
        }

        return count;
    }

    private static int EmitTrunkAndOptionalBranches(
        in PreviewTerrainTreePlacer.Placement placement,
        PreviewTerrainTreeSpeciesRules.ShapeProfile shape,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        ref int maxRelativeHeight,
        PreviewTerrainVegetationModelTemplates templates)
    {
        var count = 0;
        var sideSlot = ResolveLogSlot(placement, yFace: false);
        var topSlot = ResolveLogSlot(placement, yFace: true);
        for (var i = 1; i <= placement.TrunkHeight; i++)
        {
            var by = placement.SurfaceHeight + i;
            EmitVegetationBlock(
                placement,
                placement.RootX,
                by,
                placement.RootZ,
                leaves: false,
                sideSlot,
                topSlot,
                surfaceWorldY,
                metersPerTile,
                buckets,
                templates);
            maxRelativeHeight = Math.Max(maxRelativeHeight, by);
            count++;
        }

        if (!shape.HasBranches || placement.TrunkHeight < 3)
        {
            return count;
        }

        var branchY = placement.SurfaceHeight + placement.TrunkHeight - 1;
        var dir = placement.VariantSalt & 3;
        var ox = dir switch { 0 => 1, 1 => -1, _ => 0 };
        var oz = dir switch { 2 => 1, 3 => -1, _ => 0 };
        if (ox != 0 || oz != 0)
        {
            EmitVegetationBlock(
                placement,
                placement.RootX + ox,
                branchY,
                placement.RootZ + oz,
                leaves: false,
                sideSlot,
                topSlot,
                surfaceWorldY,
                metersPerTile,
                buckets,
                templates);
            count++;
        }

        return count;
    }

    private static int ResolveLogSlot(in PreviewTerrainTreePlacer.Placement placement, bool yFace)
    {
        if (yFace && placement.Materials is { LogTopSlot: not null })
        {
            return placement.Materials.LogTopSlot.Value;
        }

        return placement.Materials.LogSlot;
    }

    private static void EmitLeaf(
        in PreviewTerrainTreePlacer.Placement placement,
        int bx,
        int by,
        int bz,
        int slot,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        PreviewTerrainVegetationModelTemplates templates,
        HashSet<(int X, int Y, int Z)>? leafVoxels = null) =>
        EmitVegetationBlock(
            placement,
            bx,
            by,
            bz,
            leaves: true,
            slot,
            slot,
            surfaceWorldY,
            metersPerTile,
            buckets,
            templates,
            leafVoxels);

    /// <summary>
    /// Emits a 1×1×1 block. Side faces use <paramref name="sideSlot"/>; Y faces use
    /// <paramref name="ySlot"/> (log top / cactus top / leaves).
    /// When <paramref name="cullAgainstLeaves"/> is set (OptiFine smart leaves), faces whose
    /// neighbor voxel is also a leaf are omitted.
    /// </summary>
    public static void EmitBlock(
        int bx,
        int by,
        int bz,
        int sideSlot,
        int ySlot,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        HashSet<(int X, int Y, int Z)>? cullAgainstLeaves = null)
    {
        TryEmitFace(axis: 1, positive: true, bx, by, bz, ySlot, surfaceWorldY, metersPerTile, buckets, cullAgainstLeaves);
        TryEmitFace(axis: 1, positive: false, bx, by, bz, ySlot, surfaceWorldY, metersPerTile, buckets, cullAgainstLeaves);
        TryEmitFace(axis: 0, positive: true, bx, by, bz, sideSlot, surfaceWorldY, metersPerTile, buckets, cullAgainstLeaves);
        TryEmitFace(axis: 0, positive: false, bx, by, bz, sideSlot, surfaceWorldY, metersPerTile, buckets, cullAgainstLeaves);
        TryEmitFace(axis: 2, positive: true, bx, by, bz, sideSlot, surfaceWorldY, metersPerTile, buckets, cullAgainstLeaves);
        TryEmitFace(axis: 2, positive: false, bx, by, bz, sideSlot, surfaceWorldY, metersPerTile, buckets, cullAgainstLeaves);
    }

    private static void TryEmitFace(
        int axis,
        bool positive,
        int bx,
        int by,
        int bz,
        int slot,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        HashSet<(int X, int Y, int Z)>? cullAgainstLeaves)
    {
        if (cullAgainstLeaves is not null)
        {
            var nx = bx + (axis == 0 ? (positive ? 1 : -1) : 0);
            var ny = by + (axis == 1 ? (positive ? 1 : -1) : 0);
            var nz = bz + (axis == 2 ? (positive ? 1 : -1) : 0);
            if (cullAgainstLeaves.Contains((nx, ny, nz)))
            {
                return;
            }
        }

        EmitFace(axis, positive, bx, by, bz, slot, surfaceWorldY, metersPerTile, buckets);
    }

    private static void EmitFace(
        int axis,
        bool positive,
        int bx,
        int by,
        int bz,
        int slot,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets)
    {
        if ((uint)slot >= (uint)buckets.Length)
        {
            return;
        }

        Span<Vector3> corners = stackalloc Vector3[4];
        Span<Vector2> uvs = stackalloc Vector2[4];
        Vector3 n;
        Vector3 tFallback;
        float wSign = 1f;
        var y0 = surfaceWorldY + by - 1f;
        var y1 = surfaceWorldY + by;
        var x0 = bx;
        var x1 = bx + 1f;
        var z0 = bz;
        var z1 = bz + 1f;

        if (axis == 1)
        {
            var y = positive ? y1 : y0;
            if (positive)
            {
                n = Vector3.UnitY;
                tFallback = Vector3.UnitX;
                corners[0] = new(x0, y, z0);
                corners[1] = new(x1, y, z0);
                corners[2] = new(x1, y, z1);
                corners[3] = new(x0, y, z1);
            }
            else
            {
                n = -Vector3.UnitY;
                tFallback = Vector3.UnitX;
                wSign = -1f;
                corners[0] = new(x0, y, z1);
                corners[1] = new(x1, y, z1);
                corners[2] = new(x1, y, z0);
                corners[3] = new(x0, y, z0);
            }

            for (var i = 0; i < 4; i++)
            {
                uvs[i] = new(corners[i].X / metersPerTile, corners[i].Z / metersPerTile);
            }
        }
        else if (axis == 0)
        {
            var x = positive ? x1 : x0;
            if (positive)
            {
                n = Vector3.UnitX;
                tFallback = new Vector3(0, 0, -1);
                corners[0] = new(x, y0, z1);
                corners[1] = new(x, y0, z0);
                corners[2] = new(x, y1, z0);
                corners[3] = new(x, y1, z1);
            }
            else
            {
                n = -Vector3.UnitX;
                tFallback = new Vector3(0, 0, 1);
                wSign = -1f;
                corners[0] = new(x, y0, z0);
                corners[1] = new(x, y0, z1);
                corners[2] = new(x, y1, z1);
                corners[3] = new(x, y1, z0);
            }

            for (var i = 0; i < 4; i++)
            {
                uvs[i] = new(corners[i].Z / metersPerTile, corners[i].Y / metersPerTile);
            }
        }
        else
        {
            var z = positive ? z1 : z0;
            if (positive)
            {
                n = Vector3.UnitZ;
                tFallback = Vector3.UnitX;
                corners[0] = new(x0, y0, z);
                corners[1] = new(x1, y0, z);
                corners[2] = new(x1, y1, z);
                corners[3] = new(x0, y1, z);
            }
            else
            {
                n = -Vector3.UnitZ;
                tFallback = -Vector3.UnitX;
                wSign = -1f;
                corners[0] = new(x1, y0, z);
                corners[1] = new(x0, y0, z);
                corners[2] = new(x0, y1, z);
                corners[3] = new(x1, y1, z);
            }

            for (var i = 0; i < 4; i++)
            {
                uvs[i] = new(corners[i].X / metersPerTile, corners[i].Y / metersPerTile);
            }
        }

        AddSolidFace(n, tFallback, wSign, corners, uvs, buckets[slot]);
    }

    private static void AddSolidFace(
        Vector3 normal,
        Vector3 fallbackTangent,
        float fallbackWSign,
        ReadOnlySpan<Vector3> cornersIn,
        ReadOnlySpan<Vector2> uvsIn,
        List<float> verts)
    {
        Span<Vector3> corners = stackalloc Vector3[4];
        Span<Vector2> uvs = stackalloc Vector2[4];
        cornersIn.CopyTo(corners);
        uvsIn.CopyTo(uvs);

        var geo = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
        if (Vector3.Dot(geo, normal) < 0f)
        {
            (corners[1], corners[3]) = (corners[3], corners[1]);
            (uvs[1], uvs[3]) = (uvs[3], uvs[1]);
        }

        PreviewTangentBasis.Derive(corners, uvs, normal, fallbackTangent, fallbackWSign, out var tangent, out var wSign);
        for (var i = 0; i < 4; i++)
        {
            var p = corners[i];
            var uv = uvs[i];
            verts.Add(p.X);
            verts.Add(p.Y);
            verts.Add(p.Z);
            verts.Add(normal.X);
            verts.Add(normal.Y);
            verts.Add(normal.Z);
            verts.Add(uv.X);
            verts.Add(uv.Y);
            verts.Add(tangent.X);
            verts.Add(tangent.Y);
            verts.Add(tangent.Z);
            verts.Add(wSign);
        }
    }
}
