using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

/// <summary>
/// CPU oracle fixtures for the GLSL terrain height sample used by genesis_terrain_height_atlas.comp.
/// Live WGL smoke compares GPU readback against these SampleColumn + SolidBottomY values.
/// </summary>
public sealed class TerrainHeightAtlasParityTests
{
    public const float AbsoluteRelativeYEpsilon = 1e-3f;

    [Fact]
    public void SampleColumn_pad_is_zero_surface()
    {
        var gen = PreviewTerrainWorldGenSettings.Default;
        Assert.Equal(0, PreviewTerrainHeightfield.SampleColumn(0, 0, gen));
        Assert.Equal(0, PreviewTerrainHeightfield.SampleColumn(14, -14, gen));
        Assert.Equal(
            PreviewVoxelDdaMath.SolidBottomY(0),
            PreviewVoxelDdaMath.SolidBottomY(PreviewTerrainHeightfield.SampleColumn(0, 0, gen)));
    }

    [Fact]
    public void SampleColumn_fixed_grid_is_deterministic()
    {
        var gen = PreviewTerrainWorldGenSettings.Default with
        {
            Seed = 0x41504252,
            BiomeSize = 1.0f,
            Amplification = 1.0f,
            ErosionStrength = 1.0f,
            Continentalness = 1.0f,
        };

        int[] xs = [-40, -20, -8, 0, 16, 32, 48];
        int[] zs = [-40, -16, 0, 12, 24, 40];
        Span<int> fingerprints = stackalloc int[xs.Length * zs.Length];
        var i = 0;
        foreach (var z in zs)
        {
            foreach (var x in xs)
            {
                var surface = PreviewTerrainHeightfield.SampleColumn(x, z, gen);
                var bottom = PreviewVoxelDdaMath.SolidBottomY(surface);
                fingerprints[i++] = HashCode.Combine(x, z, surface, bottom);
            }
        }

        var a = fingerprints.ToArray();
        i = 0;
        foreach (var z in zs)
        {
            foreach (var x in xs)
            {
                var surface = PreviewTerrainHeightfield.SampleColumn(x, z, gen);
                var bottom = PreviewVoxelDdaMath.SolidBottomY(surface);
                Assert.Equal(a[i++], HashCode.Combine(x, z, surface, bottom));
            }
        }
    }

    [Fact]
    public void SolidBottomY_matches_mesh_baker_floor()
    {
        Assert.Equal(
            PreviewTerrainMeshBaker.SolidBottomY(0, PreviewStageConstants.TerrainFillDepth),
            PreviewVoxelDdaMath.SolidBottomY(0));
        Assert.Equal(
            PreviewTerrainMeshBaker.SolidBottomY(20, PreviewStageConstants.TerrainFillDepth),
            PreviewVoxelDdaMath.SolidBottomY(20));
        Assert.Equal(
            PreviewStageConstants.TerrainSolidFloorRelativeY,
            PreviewVoxelDdaMath.SolidBottomY(100));
    }

    /// <summary>
    /// Shared fixture points for live GL atlas readback. Values must stay stable across
    /// GLSL ports; bump only when intentionally changing worldgen.
    /// </summary>
    public static (int X, int Z, int Surface, int Bottom)[] BuildDefaultOracleColumns(
        PreviewTerrainWorldGenSettings gen,
        int originX,
        int originZ,
        int size)
    {
        var columns = new (int X, int Z, int Surface, int Bottom)[size * size];
        var i = 0;
        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                var wx = originX + x;
                var wz = originZ + z;
                var surface = PreviewTerrainHeightfield.SampleColumn(wx, wz, gen);
                var bottom = PreviewVoxelDdaMath.SolidBottomY(surface);
                columns[i++] = (wx, wz, surface, bottom);
            }
        }

        return columns;
    }

    [Fact]
    public void BuildDefaultOracleColumns_64x64_has_non_pad_relief()
    {
        var gen = PreviewTerrainWorldGenSettings.Default;
        var columns = BuildDefaultOracleColumns(gen, originX: -32, originZ: -32, size: 64);
        Assert.Equal(64 * 64, columns.Length);
        Assert.Contains(columns, c => c.Surface != 0);
        Assert.All(columns, c => Assert.True(c.Bottom <= c.Surface));
    }
}
