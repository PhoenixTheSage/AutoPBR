using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Emits tree / cactus geometry into terrain material buckets, preferring
/// origin-centered meshes baked from each block's model JSON (Explore path),
/// with cuboid fallback when a template is missing.
/// </summary>
public static class PreviewTerrainTreeMeshEmitter
{
    public static int EmitPlacements(
        IReadOnlyList<PreviewTerrainTreePlacer.Placement> placements,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        ref int maxRelativeHeight,
        PreviewTerrainVegetationModelTemplates? modelTemplates = null)
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

        var emittedBlocks = 0;
        foreach (var placement in placements)
        {
            emittedBlocks += EmitOne(
                placement,
                surfaceWorldY,
                metersPerTile,
                buckets,
                ref maxRelativeHeight,
                templates);
        }

        return emittedBlocks;
    }

    private static int EmitOne(
        in PreviewTerrainTreePlacer.Placement placement,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets,
        ref int maxRelativeHeight,
        PreviewTerrainVegetationModelTemplates templates)
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
                EmitConical(placement, shape, surfaceWorldY, metersPerTile, buckets, ref maxRelativeHeight, templates),
            PreviewTerrainTreeShapeKind.FlatCanopy =>
                EmitFlatCanopy(placement, shape, surfaceWorldY, metersPerTile, buckets, ref maxRelativeHeight, templates),
            _ => EmitRoundCanopy(placement, shape, surfaceWorldY, metersPerTile, buckets, ref maxRelativeHeight, templates),
        };
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
        PreviewTerrainVegetationModelTemplates templates)
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

        if (placement.Species == PreviewTerrainTreeSpecies.Cactus)
        {
            EmitCactusBlock(bx, by, bz, sideSlot, ySlot, surfaceWorldY, metersPerTile, buckets);
            return;
        }

        EmitBlock(bx, by, bz, sideSlot, ySlot, surfaceWorldY, metersPerTile, buckets);
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
        PreviewTerrainVegetationModelTemplates templates)
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
                        templates);
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
        PreviewTerrainVegetationModelTemplates templates)
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
                        templates);
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
        PreviewTerrainVegetationModelTemplates templates)
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
                        templates);
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
        PreviewTerrainVegetationModelTemplates templates) =>
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
            templates);

    /// <summary>
    /// Emits a 1×1×1 block. Side faces use <paramref name="sideSlot"/>; Y faces use
    /// <paramref name="ySlot"/> (log top / cactus top / leaves).
    /// </summary>
    public static void EmitBlock(
        int bx,
        int by,
        int bz,
        int sideSlot,
        int ySlot,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets)
    {
        EmitFace(axis: 1, positive: true, bx, by, bz, ySlot, surfaceWorldY, metersPerTile, buckets);
        EmitFace(axis: 1, positive: false, bx, by, bz, ySlot, surfaceWorldY, metersPerTile, buckets);
        EmitFace(axis: 0, positive: true, bx, by, bz, sideSlot, surfaceWorldY, metersPerTile, buckets);
        EmitFace(axis: 0, positive: false, bx, by, bz, sideSlot, surfaceWorldY, metersPerTile, buckets);
        EmitFace(axis: 2, positive: true, bx, by, bz, sideSlot, surfaceWorldY, metersPerTile, buckets);
        EmitFace(axis: 2, positive: false, bx, by, bz, sideSlot, surfaceWorldY, metersPerTile, buckets);
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
