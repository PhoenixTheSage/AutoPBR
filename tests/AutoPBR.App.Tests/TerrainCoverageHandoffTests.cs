using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class TerrainCoverageHandoffTests
{
    [Fact]
    public void Replacement_protection_extends_to_the_visible_render_window()
    {
        var camera = new TerrainChunkKey(0, 0);
        const int hardRadius = 8;
        var keepRadius = TerrainChunkStreamer.ResolveLodPinKeepRadiusChunks(hardRadius);

        Assert.Equal(
            keepRadius,
            TerrainChunkStreamer.ResolveReplacementProtectionRadiusChunks(
                TerrainResidencyKey.Full(hardRadius + 1, 0),
                camera,
                hardRadius,
                renderRadiusChunks: hardRadius));
        Assert.Equal(
            keepRadius,
            TerrainChunkStreamer.ResolveReplacementProtectionRadiusChunks(
                TerrainResidencyKey.FromChunk(
                    new TerrainChunkKey(hardRadius, 0),
                    lodLevel: 2),
                camera,
                hardRadius,
                renderRadiusChunks: hardRadius));

        Assert.Equal(
            -1,
            TerrainChunkStreamer.ResolveReplacementProtectionRadiusChunks(
                TerrainResidencyKey.Full(keepRadius + 1, 0),
                camera,
                hardRadius,
                renderRadiusChunks: hardRadius));
        Assert.Equal(
            -1,
            TerrainChunkStreamer.ResolveReplacementProtectionRadiusChunks(
                TerrainResidencyKey.FromChunk(
                    new TerrainChunkKey(keepRadius + 16, 0),
                    lodLevel: 2),
                camera,
                hardRadius,
                renderRadiusChunks: hardRadius));

        var renderRadius = keepRadius + 32;
        Assert.Equal(
            renderRadius,
            TerrainChunkStreamer.ResolveReplacementProtectionRadiusChunks(
                TerrainResidencyKey.Full(renderRadius, 0),
                camera,
                hardRadius,
                renderRadius));
    }

    [Fact]
    public void OutgoingFull_waits_for_real_lod_parent_coverage()
    {
        var camera = new TerrainChunkKey(0, 0);
        var full = TerrainResidencyKey.Full(3, 0);
        var residents = new HashSet<TerrainResidencyKey> { full };

        Assert.False(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                full,
                camera,
                keepRadiusChunks: 8,
                residents));

        residents.Add(TerrainResidencyKey.FromChunk(new TerrainChunkKey(3, 0), lodLevel: 1));
        Assert.True(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                full,
                camera,
                keepRadiusChunks: 8,
                residents));
    }

    [Fact]
    public void Replacement_coverage_requires_every_required_cell()
    {
        var camera = new TerrainChunkKey(0, 0);
        var leaving = TerrainResidencyKey.Section(0, 0, lodLevel: 2);
        var residents = new HashSet<TerrainResidencyKey> { leaving };
        var children = TerrainTargetCutBuilder.ChildrenOf(leaving);
        residents.UnionWith(children.Take(3));

        Assert.False(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                leaving,
                camera,
                keepRadiusChunks: 8,
                residents));

        residents.Add(children[3]);
        Assert.True(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                leaving,
                camera,
                keepRadiusChunks: 8,
                residents));
    }

    [Fact]
    public void Replacement_coverage_accepts_a_coarser_ancestor_without_cell_scanning()
    {
        var camera = new TerrainChunkKey(-3, -3);
        var leaving = TerrainResidencyKey.FromChunk(camera, lodLevel: 3);
        var ancestor = TerrainTargetCutBuilder.ParentOf(leaving);
        var residents = new HashSet<TerrainResidencyKey> { leaving, ancestor };

        Assert.True(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                leaving,
                camera,
                keepRadiusChunks: 64,
                residents));
    }

    [Fact]
    public void Replacement_coverage_descends_only_until_resident_leaves()
    {
        var camera = new TerrainChunkKey(0, 0);
        var leaving = TerrainResidencyKey.Section(0, 0, lodLevel: 3);
        var children = TerrainTargetCutBuilder.ChildrenOf(leaving);
        var residents = new HashSet<TerrainResidencyKey> { leaving };
        residents.UnionWith(children.Take(3));
        var grandchildren = TerrainTargetCutBuilder.ChildrenOf(children[3]);
        residents.UnionWith(grandchildren);

        Assert.True(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                leaving,
                camera,
                keepRadiusChunks: 16,
                residents));

        residents.Remove(grandchildren[2]);
        Assert.False(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                leaving,
                camera,
                keepRadiusChunks: 16,
                residents));
    }

    [Fact]
    public void Hierarchical_replacement_proof_matches_cell_reference()
    {
        var random = new Random(0x51A7);
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var level = (byte)random.Next(0, 6);
            var leaving = TerrainResidencyKey.FromChunk(
                new TerrainChunkKey(random.Next(-32, 33), random.Next(-32, 33)),
                level);
            var camera = new TerrainChunkKey(random.Next(-24, 25), random.Next(-24, 25));
            var keepRadius = random.Next(0, 33);
            var residents = new HashSet<TerrainResidencyKey> { leaving };
            for (var i = 0; i < 80; i++)
            {
                residents.Add(TerrainResidencyKey.FromChunk(
                    new TerrainChunkKey(random.Next(-48, 49), random.Next(-48, 49)),
                    (byte)random.Next(0, TerrainResidencyKey.MaxLodLevel + 1)));
            }

            var expected = CellReference(leaving, camera, keepRadius, residents);
            var actual = TerrainChunkStreamer.HasFootprintReplacementCoverage(
                leaving,
                camera,
                keepRadius,
                residents);
            Assert.Equal(expected, actual);
        }
    }

    private static bool CellReference(
        TerrainResidencyKey leaving,
        TerrainChunkKey camera,
        int keepRadius,
        IReadOnlySet<TerrainResidencyKey> residents)
    {
        for (var z = leaving.OriginChunkZ;
             z < leaving.OriginChunkZ + leaving.ChunksPerSide;
             z++)
        {
            for (var x = leaving.OriginChunkX;
                 x < leaving.OriginChunkX + leaving.ChunksPerSide;
                 x++)
            {
                if (Math.Max(Math.Abs(x - camera.X), Math.Abs(z - camera.Z)) > keepRadius)
                {
                    continue;
                }

                var chunk = new TerrainChunkKey(x, z);
                var covered = false;
                for (byte level = 0; level <= TerrainResidencyKey.MaxLodLevel; level++)
                {
                    var candidate = TerrainResidencyKey.FromChunk(chunk, level);
                    if (candidate != leaving && residents.Contains(candidate))
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
