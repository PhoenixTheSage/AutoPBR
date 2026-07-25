using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

using System.Numerics;

namespace AutoPBR.App.Tests;

public sealed partial class PreviewRenderingTests
{
    [Fact]
    public void BlockModelPreviewSceneUsesBlockModelKind()
    {
        var mesh = PreviewMeshFactory.CreateUnitCube("subject");
        var scene = BlockModelPreviewSceneFactory.Create(new PreviewRenderSettings(), mesh);
        Assert.Equal(PreviewSceneKind.BlockModel, scene.SceneKind);
        Assert.Same(mesh, scene.Meshes[0]);
    }

    [Fact]
    public void CubeMeshFactoryCreatesSixFacesWithExpectedIndices()
    {
        var mesh = PreviewMeshFactory.CreateUnitCube();
        Assert.Equal(24, mesh.VertexCount);
        Assert.Equal(24 * PreviewMesh.FloatsPerVertex, mesh.InterleavedVertices.Length);
        Assert.Equal(36, mesh.Indices.Length);
    }

    [Fact]
    public void ItemPlaneMeshFactoryCreatesQuadIndices()
    {
        var mesh = PreviewMeshFactory.CreateItemPlane();
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(6, mesh.Indices.Length);
    }

    [Fact]
    public void SpritePixelCuboids_builds_one_cube_per_opaque_texel()
    {
        var rgba = new byte[]
        {
            255, 0, 0, 255, 0, 0, 0, 0,
            0, 0, 0, 0, 255, 0, 0, 255
        };
        var mesh = PreviewMeshFactory.CreateSpritePixelCuboids(rgba, 2, 2, thickness: 0.10f, alphaCutoff: 0.5f);
        Assert.Equal("sprite_voxels", mesh.Name);
        Assert.Equal(2, mesh.OpaqueVoxelCount);
        Assert.Equal(48, mesh.VertexCount);
        Assert.Equal(72, mesh.Indices.Length);
    }

    [Fact]
    public void SpriteVoxelMesh_culls_internal_faces_and_merges_side_quads()
    {
        var rgba = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            rgba[i * 4 + 3] = 255;
        }

        var mesh = PreviewMeshFactory.CreateSpritePixelCuboids(rgba, 2, 2, thickness: 0.10f, alphaCutoff: 0.5f);
        Assert.Equal(4, mesh.OpaqueVoxelCount);
        Assert.True(mesh.VertexCount < 96, $"expected fewer than 96 verts after culling/merge, got {mesh.VertexCount}");
        Assert.True(mesh.Indices.Length < 144, $"expected fewer than 144 indices after culling/merge, got {mesh.Indices.Length}");
    }

    [Fact]
    public void SpriteVoxelMesh_merges_horizontal_side_run()
    {
        var rgba = new byte[8];
        rgba[3] = 255;
        rgba[7] = 255;

        var mesh = PreviewMeshFactory.CreateSpritePixelCuboids(rgba, 2, 1, thickness: 0.10f, alphaCutoff: 0.5f);
        Assert.Equal(2, mesh.OpaqueVoxelCount);
        Assert.True(mesh.VertexCount < 48, $"expected merged side faces, got {mesh.VertexCount} verts");
    }

    [Fact]
    public void SpriteVoxelMeshCache_returns_same_mesh_for_same_input()
    {
        var rgba = new byte[]
        {
            255, 0, 0, 255, 0, 0, 0, 0,
            0, 0, 0, 0, 255, 0, 0, 255
        };
        var a = SpriteVoxelMeshCache.GetOrBuild(rgba, 2, 2, thickness: 0.10f, alphaCutoff: 0.5f);
        var b = SpriteVoxelMeshCache.GetOrBuild(rgba, 2, 2, thickness: 0.10f, alphaCutoff: 0.5f);
        Assert.Same(a, b);
    }

    [Fact]
    public void PreviewMaterialContentKey_detects_identical_payloads()
    {
        var rgba = new byte[] { 1, 2, 3, 255 };
        var a = PreviewMaterialMapper.FromCoreMaps(new PreviewTextureMaps
        {
            Width = 1,
            Height = 1,
            DiffuseRgba = rgba
        });
        var b = PreviewMaterialMapper.FromCoreMaps(new PreviewTextureMaps
        {
            Width = 1,
            Height = 1,
            DiffuseRgba = (byte[])rgba.Clone()
        });
        Assert.True(PreviewMaterialContentKey.Equals(
            PreviewMaterialContentKey.Compute(a),
            PreviewMaterialContentKey.Compute(b)));
    }

    [Fact]
    public void ItemFlatSceneCreate_uses_lightweight_placeholder_mesh()
    {
        var rgba = new byte[] { 255, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 255 };
        var material = PreviewMaterialMapper.FromCoreMaps(new PreviewTextureMaps
        {
            Width = 2,
            Height = 2,
            DiffuseRgba = rgba
        });
        var settings = new PreviewRenderSettings
        {
            ItemFlatSpritePreview = true,
            SpriteThickness = 0.10f
        };
        var scene = ItemPreviewSceneFactory.Create(settings, material);
        Assert.Equal("item_plane_pending", scene.Meshes[0].Name);
        Assert.Equal(4, scene.Meshes[0].VertexCount);
    }

    [Fact]
    public void PreviewMaterialMapperCopiesMapsFromCore()
    {
        var diffuse = new byte[4];
        diffuse[0] = 10;
        diffuse[1] = 20;
        diffuse[2] = 30;
        diffuse[3] = 240;
        var maps = new PreviewTextureMaps
        {
            Width = 1,
            Height = 1,
            DiffuseRgba = diffuse,
            NormalRgba = [1, 2, 3, 4],
            SpecularRgba = [5, 6, 7, 8],
            HeightRgba = [9, 10, 11, 12],
            IsPlantForNoHeight = true
        };
        var mat = PreviewMaterialMapper.FromCoreMaps(maps);
        Assert.Equal(1, mat.Width);
        Assert.Equal(1, mat.Height);
        Assert.True(mat.IsPlantForNoHeight);
        Assert.Equal(10, mat.AlbedoRgba.Span[0]);
        Assert.True(mat.NormalRgba.HasValue);
        Assert.Equal(4, mat.NormalRgba.Value.Span[3]);
        Assert.True(mat.SpecularRgba.HasValue);
        Assert.Equal(8, mat.SpecularRgba.Value.Span[3]);
        Assert.True(mat.HeightRgba.HasValue);
        Assert.Equal(12, mat.HeightRgba.Value.Span[3]);
    }

    [Fact]
    public void PreviewMaterialMapperPreservesEntityBakeAtlasDimensions()
    {
        var maps = new PreviewTextureMaps
        {
            Width = 64,
            Height = 64,
            BakeAtlasWidth = 64,
            BakeAtlasHeight = 32,
            DiffuseRgba = new byte[64 * 64 * 4],
        };
        var mat = PreviewMaterialMapper.FromCoreMaps(maps);
        Assert.Equal(64, mat.BakeAtlasWidth);
        Assert.Equal(32, mat.BakeAtlasHeight);
    }

    [Fact]
    public void EntityEmulatedShaderGating_DisablesTessellationByDefault()
    {
        Assert.False(PreviewEntityEmulatedShaderGating.EffectiveTessellationDisplacement(true, entityEmulated: true));
        Assert.True(PreviewEntityEmulatedShaderGating.EffectiveTessellationDisplacement(true, entityEmulated: false));
        Assert.False(PreviewEntityEmulatedShaderGating.EffectiveTessellationDisplacement(false, entityEmulated: false));
    }

    [Fact]
    public void EmptySubjectMeshHasNoIndices()
    {
        var mesh = PreviewMeshFactory.CreateEmptySubjectPlaceholder();
        Assert.Equal(0, mesh.VertexCount);
        Assert.Empty(mesh.Indices);
        Assert.Empty(mesh.InterleavedVertices);
    }

    [Fact]
    public void PreviewGroundPlaneTilingUvSpansOneTilePerWorldUnit()
    {
        const float h = 14f;
        var mesh = PreviewMeshFactory.CreatePreviewGroundPlane(halfExtent: h, worldY: -1f, metersPerTile: 1f);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(6, mesh.Indices.Length);
        var v = mesh.InterleavedVertices;
        const int s = PreviewMesh.FloatsPerVertex;
        Assert.Equal(0f, v[6]);
        Assert.Equal(0f, v[7]);
        Assert.Equal(2f * h, v[s + 6]);
        Assert.Equal(0f, v[s + 7]);
        Assert.Equal(2f * h, v[2 * s + 6]);
        Assert.Equal(2f * h, v[2 * s + 7]);
        Assert.Equal(0f, v[3 * s + 6]);
        Assert.Equal(2f * h, v[3 * s + 7]);
    }

    [Fact]
    public void PreviewGroundPlaneTangentBasisMatchesUvAxes()
    {
        var mesh = PreviewMeshFactory.CreatePreviewGroundPlane(halfExtent: 1f, worldY: -1f, metersPerTile: 1f);
        var bitangent = ReadBitangent(mesh.InterleavedVertices, 0);
        AssertVectorNear(Vector3.UnitZ, bitangent);
    }

    [Fact]
    public void ItemPlaneTangentBasisMatchesUvAxes()
    {
        var mesh = PreviewMeshFactory.CreateItemPlane();
        var bitangent = ReadBitangent(mesh.InterleavedVertices, 0);
        AssertVectorNear(Vector3.UnitY, bitangent);
    }

    [Fact]
    public void GridLinesFactoryVertexBuffersAreSevenFloatsPerVertex()
    {
        var grid = PreviewGridLinesFactory.BuildGrid(1f, 0.5f, -0.5f, 1, 1, 1, 1, lineHalfWidth: 0.02f);
        Assert.Equal(0, grid.Length % PreviewGridLinesFactory.FloatsPerVertex);
        // Thick ribbons: 6 verts per segment × (5 z-lines + 5 x-lines) for halfExtent=1 step=0.5.
        Assert.True(grid.Length >= 10 * 6 * PreviewGridLinesFactory.FloatsPerVertex);
        var axes = PreviewGridLinesFactory.BuildAxes(1f, 1, 0, 0, 0, 1, 0, 0, 0, 1);
        Assert.Equal(0, axes.Length % PreviewGridLinesFactory.FloatsPerVertex);
        Assert.Equal(6 * PreviewGridLinesFactory.FloatsPerVertex, axes.Length);
    }

    private static Vector3 ReadBitangent(float[] verts, int vertexIndex)
    {
        const int s = PreviewMesh.FloatsPerVertex;
        var o = vertexIndex * s;
        var normal = new Vector3(verts[o + 3], verts[o + 4], verts[o + 5]);
        var tangent = new Vector3(verts[o + 8], verts[o + 9], verts[o + 10]);
        var wSign = verts[o + 11];
        return Vector3.Cross(normal, tangent) * wSign;
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual, float tolerance = 1e-5f)
    {
        Assert.InRange(MathF.Abs(actual.X - expected.X), 0f, tolerance);
        Assert.InRange(MathF.Abs(actual.Y - expected.Y), 0f, tolerance);
        Assert.InRange(MathF.Abs(actual.Z - expected.Z), 0f, tolerance);
    }
}
