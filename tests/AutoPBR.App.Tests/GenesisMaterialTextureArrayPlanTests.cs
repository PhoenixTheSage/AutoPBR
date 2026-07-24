using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.Preview;

namespace AutoPBR.App.Tests;

public sealed class GenesisMaterialTextureArrayPlanTests
{
    [Fact]
    public void TryCreate_AcceptsSameDimensionMaterialSlots()
    {
        var slots = new[]
        {
            CreateMaterial(2, 2, seed: 1, normal: true, specular: true, heightMap: true),
            CreateMaterial(2, 2, seed: 2, normal: false, specular: true),
        };

        var ok = GenesisMaterialTextureArrayPlan.TryCreate(slots, maxTextureArrayLayers: 8, out var plan, out var reason);

        Assert.True(ok, reason);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Width);
        Assert.Equal(2, plan.Height);
        Assert.Equal(2, plan.Layers);
    }

    [Fact]
    public void TryCreate_ResamplesMixedSlotDimensionsToLargestLayer()
    {
        var slots = new[]
        {
            CreateMaterial(2, 2, seed: 1),
            CreateMaterial(4, 2, seed: 2),
        };

        var ok = GenesisMaterialTextureArrayPlan.TryCreate(slots, maxTextureArrayLayers: 8, out var plan, out var reason);

        Assert.True(ok, reason);
        Assert.NotNull(plan);
        Assert.Equal(4, plan.Width);
        Assert.Equal(2, plan.Height);
    }

    [Fact]
    public void TryCreate_RejectsLayerLimitOverflow()
    {
        var slots = new[]
        {
            CreateMaterial(1, 1, seed: 1),
            CreateMaterial(1, 1, seed: 2),
            CreateMaterial(1, 1, seed: 3),
        };

        var ok = GenesisMaterialTextureArrayPlan.TryCreate(slots, maxTextureArrayLayers: 2, out var plan, out var reason);

        Assert.False(ok);
        Assert.Null(plan);
        Assert.Contains("layer limit", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_RejectsShortOptionalMapPayload()
    {
        var slots = new[]
        {
            CreateMaterial(2, 2, seed: 1, normal: true),
            CreateMaterial(2, 2, seed: 2, normal: true, normalBytesOverride: 4),
        };

        var ok = GenesisMaterialTextureArrayPlan.TryCreate(slots, maxTextureArrayLayers: 8, out var plan, out var reason);

        Assert.False(ok);
        Assert.Null(plan);
        Assert.Contains("mixed map dimensions", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_AcceptsMissingOptionalMapsAsNeutralLayers()
    {
        var slots = new[]
        {
            CreateMaterial(2, 2, seed: 1, normal: true, specular: true, heightMap: true),
            CreateMaterial(2, 2, seed: 2),
        };

        var ok = GenesisMaterialTextureArrayPlan.TryCreate(slots, maxTextureArrayLayers: 8, out var plan, out var reason);

        Assert.True(ok, reason);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Layers);
    }

    [Theory]
    [InlineData(false, true, true, true, "capability gate is off")]
    [InlineData(true, false, true, true, "material draw records are unavailable")]
    [InlineData(true, true, false, true, "no block/entity model")]
    [InlineData(true, true, true, false, "no material slots")]
    public void Eligibility_RejectsFallbackCases(
        bool capabilityEnabled,
        bool materialDrawRecordsUploaded,
        bool hasBlockModel,
        bool hasSlots,
        string expectedReason)
    {
        var ok = GenesisMaterialTextureArrayEligibility.TryResolve(
            capabilityEnabled,
            materialDrawRecordsUploaded,
            hasBlockModel,
            hasSlots,
            out var reason);

        Assert.False(ok);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void Eligibility_AcceptsCompatibleBlockModel()
    {
        var ok = GenesisMaterialTextureArrayEligibility.TryResolve(
            capabilityEnabled: true,
            materialDrawRecordsUploaded: true,
            hasBlockModel: true,
            hasSlots: true,
            out var reason);

        Assert.True(ok, reason);
        Assert.Empty(reason);
    }

    [Fact]
    public void Eligibility_AcceptsWhenTessellationWouldHaveBeenActive()
    {
        // Tessellation no longer gates material texture arrays (desktop WGL composes both).
        var ok = GenesisMaterialTextureArrayEligibility.TryResolve(
            capabilityEnabled: true,
            materialDrawRecordsUploaded: true,
            hasBlockModel: true,
            hasSlots: true,
            out var reason);

        Assert.True(ok, reason);
    }

    [Fact]
    public void ContentEquals_TracksTexturePayloadChanges()
    {
        var first = new[] { CreateMaterial(1, 1, seed: 1) };
        var same = new[] { CreateMaterial(1, 1, seed: 1) };
        var changed = new[] { CreateMaterial(1, 1, seed: 2) };

        Assert.True(GenesisMaterialTextureArrayPlan.TryCreate(first, 8, out var firstPlan, out _));
        Assert.True(GenesisMaterialTextureArrayPlan.TryCreate(same, 8, out var samePlan, out _));
        Assert.True(GenesisMaterialTextureArrayPlan.TryCreate(changed, 8, out var changedPlan, out _));

        Assert.True(firstPlan!.ContentEquals(samePlan!));
        Assert.False(firstPlan.ContentEquals(changedPlan!));
    }

    [Fact]
    public void ResampleMaterialArrayLayer_NearestExpandsAndFlipsRows()
    {
        byte[] source =
        [
            1, 2, 3, 255,
            4, 5, 6, 255,
        ];
        var expanded = new byte[2 * 2 * 4];

        OpenGlPreviewBackend.ResampleMaterialArrayLayer(
            source,
            sourceWidth: 1,
            sourceHeight: 2,
            expanded,
            destinationWidth: 2,
            destinationHeight: 2,
            flipRows: true);

        Assert.Equal(
            [
                4, 5, 6, 255, 4, 5, 6, 255,
                1, 2, 3, 255, 1, 2, 3, 255,
            ],
            expanded);
    }

    [Fact]
    public void Fixture_CompatibleMultiMaterialSubjectUsesOneArrayLayerPerSlot()
    {
        var subject = CreateSubject(
            CreateMaps(2, 2, seed: 1, normal: true, specular: true, heightMap: true),
            CreateMaps(2, 2, seed: 2, normal: false, specular: true));
        var slots = MapSubjectMaterials(subject);

        AssertDrawBatchesReferenceMaterialSlots(subject);
        Assert.True(GenesisMaterialTextureArrayEligibility.TryResolve(
            capabilityEnabled: true,
            materialDrawRecordsUploaded: true,
            hasBlockModel: true,
            hasSlots: slots.Length > 0,
            out var reason), reason);

        var ok = GenesisMaterialTextureArrayPlan.TryCreate(slots, maxTextureArrayLayers: 8, out var plan, out reason);

        Assert.True(ok, reason);
        Assert.NotNull(plan);
        Assert.Equal(subject.Materials.Length, plan.Layers);
        Assert.All(subject.DrawBatches, batch => Assert.InRange(batch.MaterialIndex, 0, plan.Layers - 1));
    }

    [Fact]
    public void Fixture_MixedDimensionSubjectUsesResampledArrayLayers()
    {
        var subject = CreateSubject(
            CreateMaps(2, 2, seed: 1),
            CreateMaps(4, 2, seed: 2));
        var slots = MapSubjectMaterials(subject);

        AssertDrawBatchesReferenceMaterialSlots(subject);
        Assert.True(GenesisMaterialTextureArrayEligibility.TryResolve(
            capabilityEnabled: true,
            materialDrawRecordsUploaded: true,
            hasBlockModel: true,
            hasSlots: slots.Length > 0,
            out var reason), reason);

        var ok = GenesisMaterialTextureArrayPlan.TryCreate(slots, maxTextureArrayLayers: 8, out var plan, out reason);

        Assert.True(ok, reason);
        Assert.NotNull(plan);
        Assert.Equal(4, plan.Width);
        Assert.Equal(2, plan.Height);
    }

    [Fact]
    public void Fixture_EntityEmulatedTessellationGateAllowsTextureArraysWhenEffectiveTessellationIsOff()
    {
        var subject = CreateSubject(new[]
        {
            CreateMaps(2, 2, seed: 1, normal: true, specular: true, heightMap: true),
            CreateMaps(2, 2, seed: 2, normal: true)
        },
            entityEmulated: true);
        var slots = MapSubjectMaterials(subject);
        var effectiveTessellation = PreviewEntityEmulatedShaderGating.EffectiveTessellationDisplacement(
            enableTessellation: true,
            entityEmulated: string.Equals(subject.AnimationPreset, "entity_emulated", StringComparison.Ordinal));

        Assert.False(effectiveTessellation);
        Assert.True(GenesisMaterialTextureArrayEligibility.TryResolve(
            capabilityEnabled: true,
            materialDrawRecordsUploaded: true,
            hasBlockModel: true,
            hasSlots: slots.Length > 0,
            out var reason), reason);

        var ok = GenesisMaterialTextureArrayPlan.TryCreate(slots, maxTextureArrayLayers: 8, out var plan, out reason);

        Assert.True(ok, reason);
        Assert.NotNull(plan);
        Assert.Equal(subject.Materials.Length, plan.Layers);
    }

    private static PreviewMaterial CreateMaterial(
        int width,
        int height,
        byte seed,
        bool normal = false,
        bool specular = false,
        bool heightMap = false,
        int? normalBytesOverride = null)
    {
        var px = width * height * 4;
        var albedo = Fill(px, seed);
        return new PreviewMaterial
        {
            Width = width,
            Height = height,
            AlbedoRgba = albedo,
            NormalRgba = normal ? Fill(normalBytesOverride ?? px, (byte)(seed + 10)) : null,
            SpecularRgba = specular ? Fill(px, (byte)(seed + 20)) : null,
            HeightRgba = heightMap ? Fill(px, (byte)(seed + 30)) : null,
            GlUploadFlipRows = false,
        };
    }

    private static byte[] Fill(int bytes, byte seed)
    {
        var rgba = new byte[bytes];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = seed;
            rgba[i + 1] = (byte)(seed + 1);
            rgba[i + 2] = (byte)(seed + 2);
            rgba[i + 3] = 255;
        }

        return rgba;
    }

    private static PreviewModelSubject CreateSubject(params PreviewTextureMaps[] materials) =>
        CreateSubject(materials, entityEmulated: false);

    private static PreviewModelSubject CreateSubject(PreviewTextureMaps[] materials, bool entityEmulated)
    {
        const int stride = 12;
        var vertices = new float[materials.Length * 4 * stride];
        var indices = new uint[materials.Length * 6];
        var batches = new PreviewDrawBatch[materials.Length];

        for (var batch = 0; batch < materials.Length; batch++)
        {
            var vertexBase = batch * 4;
            var vertexOffset = vertexBase * stride;
            var x = batch * 1.25f;
            WriteVertex(vertices, vertexOffset + (0 * stride), x, 0f, 0f, u: 0f, v: 0f);
            WriteVertex(vertices, vertexOffset + (1 * stride), x + 1f, 0f, 0f, u: 1f, v: 0f);
            WriteVertex(vertices, vertexOffset + (2 * stride), x + 1f, 1f, 0f, u: 1f, v: 1f);
            WriteVertex(vertices, vertexOffset + (3 * stride), x, 1f, 0f, u: 0f, v: 1f);

            var indexOffset = batch * 6;
            indices[indexOffset + 0] = (uint)(vertexBase + 0);
            indices[indexOffset + 1] = (uint)(vertexBase + 1);
            indices[indexOffset + 2] = (uint)(vertexBase + 2);
            indices[indexOffset + 3] = (uint)(vertexBase + 2);
            indices[indexOffset + 4] = (uint)(vertexBase + 3);
            indices[indexOffset + 5] = (uint)(vertexBase + 0);
            batches[batch] = new PreviewDrawBatch(indexOffset, 6, batch);
        }

        return new PreviewModelSubject
        {
            InterleavedVertices = vertices,
            Indices = indices,
            DrawBatches = batches,
            Materials = materials,
            PrimaryMaterialIndex = 0,
            EnableRenderTimeAnimation = entityEmulated,
            AnimationPreset = entityEmulated ? "entity_emulated" : null,
        };
    }

    private static void WriteVertex(float[] vertices, int offset, float x, float y, float z, float u, float v)
    {
        vertices[offset + 0] = x;
        vertices[offset + 1] = y;
        vertices[offset + 2] = z;
        vertices[offset + 3] = 0f;
        vertices[offset + 4] = 0f;
        vertices[offset + 5] = 1f;
        vertices[offset + 6] = u;
        vertices[offset + 7] = v;
        vertices[offset + 8] = 1f;
        vertices[offset + 9] = 0f;
        vertices[offset + 10] = 0f;
        vertices[offset + 11] = 1f;
    }

    private static PreviewMaterial[] MapSubjectMaterials(PreviewModelSubject subject) =>
        subject.Materials.Select(m => PreviewMaterialMapper.FromCoreMaps(m)).ToArray();

    private static void AssertDrawBatchesReferenceMaterialSlots(PreviewModelSubject subject)
    {
        Assert.NotEmpty(subject.DrawBatches);
        Assert.All(subject.DrawBatches, batch =>
        {
            Assert.InRange(batch.FirstIndex, 0, subject.Indices.Length - 1);
            Assert.InRange(batch.IndexCount, 1, subject.Indices.Length - batch.FirstIndex);
            Assert.InRange(batch.MaterialIndex, 0, subject.Materials.Length - 1);
        });
    }

    private static PreviewTextureMaps CreateMaps(
        int width,
        int height,
        byte seed,
        bool normal = false,
        bool specular = false,
        bool heightMap = false)
    {
        var px = width * height * 4;
        return new PreviewTextureMaps
        {
            Width = width,
            Height = height,
            DiffuseRgba = Fill(px, seed),
            NormalRgba = normal ? Fill(px, (byte)(seed + 10)) : null,
            SpecularRgba = specular ? Fill(px, (byte)(seed + 20)) : null,
            HeightRgba = heightMap ? Fill(px, (byte)(seed + 30)) : null,
        };
    }
}
