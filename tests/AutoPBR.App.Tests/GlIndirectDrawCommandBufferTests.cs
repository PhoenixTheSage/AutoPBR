using System.Numerics;

using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.Preview;

namespace AutoPBR.App.Tests;

public sealed class GlIndirectDrawCommandBufferTests
{
    [Fact]
    public void WriteCommandDwords_PacksDrawElementsIndirectAbi()
    {
        Span<uint> command = stackalloc uint[GlIndirectDrawCommandBuffer.CommandDwords];
        var batch = new PreviewDrawBatch(6, 12, 2);

        GlIndirectDrawCommandBuffer.WriteCommandDwords(command, batch, baseInstance: 4);

        Assert.Equal([12u, 1u, 6u, 0u, 4u], command.ToArray());
    }

    [Fact]
    public void WriteCommandDwords_ClampsEmptyOrInvalidRangesToNoOpCommand()
    {
        Span<uint> command = stackalloc uint[GlIndirectDrawCommandBuffer.CommandDwords];
        var batch = new PreviewDrawBatch(-3, -9, 0);

        GlIndirectDrawCommandBuffer.WriteCommandDwords(command, batch, baseInstance: 7);

        Assert.Equal([0u, 0u, 0u, 0u, 7u], command.ToArray());
    }

    [Fact]
    public void DrawReductionSnapshot_ParsesBoundedCounterAbi()
    {
        var snapshot = GlGpuDrawReductionSnapshot.FromDwords([8u, 2u, 1u, 1u, 1u, 1u, 2u, 36u]);

        Assert.Equal(8u, snapshot.ExaminedCommands);
        Assert.Equal(8u, snapshot.AccountedCommands);
        Assert.True(snapshot.IsConsistent);
        Assert.Equal(2u, snapshot.OverflowCommands);
        Assert.Contains("maxIndices=36", snapshot.FormatDiagnostic(), StringComparison.Ordinal);
    }

    [Fact]
    public void CountMainPassMultiDrawGroup_GroupsOnlyCompatibleConsecutiveBatches()
    {
        PreviewDrawBatch[] batches =
        [
            new(0, 6, 0),
            new(6, 6, 0),
            new(12, 6, 1),
            new(18, 6, 1) { LayerPolicy = PreviewDrawLayerPolicy.ForKind(PreviewDepthLayerKind.CutoutOverlay) },
        ];

        var firstGroup = OpenGlPreviewBackend.CountMainPassMultiDrawGroup(
            batches,
            startIndex: 0,
            materialCount: 2,
            entityBlendDraw: false,
            enabled: true);
        var secondGroup = OpenGlPreviewBackend.CountMainPassMultiDrawGroup(
            batches,
            startIndex: 2,
            materialCount: 2,
            entityBlendDraw: false,
            enabled: true);

        Assert.Equal(2, firstGroup);
        Assert.Equal(1, secondGroup);
    }

    [Fact]
    public void CountMainPassMultiDrawGroup_ArraysAllowMaterialChangesWithinSameLayerState()
    {
        PreviewDrawBatch[] batches =
        [
            new(0, 6, 0),
            new(6, 6, 1),
            new(12, 6, 2),
        ];

        var group = OpenGlPreviewBackend.CountMainPassMultiDrawGroup(
            batches,
            startIndex: 0,
            materialCount: 3,
            entityBlendDraw: false,
            enabled: true,
            allowMaterialChanges: true);

        Assert.Equal(3, group);
    }

    [Fact]
    public void CountShadowPassMultiDrawGroup_SkipsAtShadowModeBoundary()
    {
        PreviewDrawBatch[] batches =
        [
            new(0, 6, 0),
            new(6, 6, 0) { LayerPolicy = PreviewDrawLayerPolicy.ForKind(PreviewDepthLayerKind.CosmeticOverlay) },
            new(12, 6, 0) { LayerPolicy = PreviewDrawLayerPolicy.ForKind(PreviewDepthLayerKind.TranslucentOverlay) },
            new(18, 6, 0),
        ];

        var group = OpenGlPreviewBackend.CountShadowPassMultiDrawGroup(
            batches,
            startIndex: 0,
            materialCount: 1,
            enabled: true);

        Assert.Equal(2, group);
    }

    [Fact]
    public void PreviewDrawBatchBounds_ComputesSphereForIndexedRange()
    {
        float[] vertices =
        [
            -1f, -1f, 0f,
             1f, -1f, 0f,
             1f,  1f, 0f,
            -1f,  1f, 0f,
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var batch = new PreviewDrawBatch(0, indices.Length, 0);

        var withBounds = PreviewDrawBatchBounds.WithComputedBounds([batch], vertices, indices, vertexStrideFloats: 3);

        Assert.Single(withBounds);
        Assert.True(withBounds[0].HasBounds);
        Assert.Equal(new System.Numerics.Vector3(0f, 0f, 0f), withBounds[0].BoundsCenter);
        Assert.InRange(withBounds[0].BoundsRadius, 1.4141f, 1.4143f);
    }

    [Fact]
    public void WriteCullRecord_PacksBoundsAndLodForGpu()
    {
        Span<float> record = stackalloc float[8];
        var batch = new PreviewDrawBatch(0, 3, 0)
        {
            BoundsCenter = new System.Numerics.Vector3(1f, 2f, 3f),
            BoundsRadius = 4f,
            LodMaxDistance = 12f,
        };

        GlGpuDrawCommandCompactor.WriteCullRecord(record, batch);

        Assert.Equal([1f, 2f, 3f, 4f, 12f, 0f, 0f, 0f], record.ToArray());
    }

    [Fact]
    public void WriteCullRecord_TransformsBoundsWithRenderModelMatrix()
    {
        Span<float> record = stackalloc float[8];
        var batch = new PreviewDrawBatch(0, 3, 0)
        {
            BoundsCenter = new Vector3(1f, 2f, 3f),
            BoundsRadius = 2f,
        };
        var model = new Matrix4x4(
            2f, 0f, 0f, 5f,
            0f, 3f, 0f, 6f,
            0f, 0f, 4f, 7f,
            0f, 0f, 0f, 1f);

        GlGpuDrawCommandCompactor.WriteCullRecord(record, batch, model);

        Assert.Equal([7f, 12f, 19f, 8f], record[..4].ToArray());
    }

    [Fact]
    public void WriteCullRecord_PadsTessellatedBoundsBeforeModelScale()
    {
        Span<float> record = stackalloc float[8];
        var batch = new PreviewDrawBatch(0, 3, 0)
        {
            BoundsRadius = 2f,
        };
        var model = Matrix4x4.CreateScale(3f);

        GlGpuDrawCommandCompactor.WriteCullRecord(record, batch, model, boundsPadding: 0.2f);

        Assert.Equal(6.6f, record[3], 5);
    }

    [Fact]
    public void GroupHasCullableBounds_RequiresValidRangeAndKnownBounds()
    {
        PreviewDrawBatch[] batches =
        [
            new(0, 3, 0),
            new(3, 3, 0) { BoundsRadius = 1f },
            new(6, 3, 0),
        ];

        Assert.True(OpenGlPreviewBackend.GroupHasCullableBounds(batches, 0, 2));
        Assert.False(OpenGlPreviewBackend.GroupHasCullableBounds(batches, 2, 1));
        Assert.False(OpenGlPreviewBackend.GroupHasCullableBounds(batches, -1, 2));
    }

    [Fact]
    public void ExtractFrustumPlanes_ClassifiesCenterAndOutsidePoint()
    {
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(
            new Vector3(0f, 0f, 5f),
            Vector3.Zero,
            Vector3.UnitY);
        var projection = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            MathF.PI / 3f,
            1f,
            0.1f,
            100f);
        var vp = projection * view;
        Span<Vector4> planes = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];

        PreviewFrustumPlanes.Extract(vp, planes);

        Assert.All(planes.ToArray(), plane => Assert.True(SignedDistance(plane, Vector3.Zero) >= 0f));
        Assert.Contains(planes.ToArray(), plane => SignedDistance(plane, new Vector3(100f, 0f, 0f)) < 0f);

        // Screen-edge point must stay inside (column clip = vp * world matches GL upload path).
        var forward = Vector3.Normalize(-Vector3.UnitZ);
        var right = Vector3.UnitX;
        var edge = new Vector3(0f, 0f, 5f) + forward * 10f + right * (MathF.Tan(MathF.PI / 6f) * 10f * 0.9f);
        Assert.True(PreviewFrustumPlanes.SphereIntersects(planes, edge, 0.01f));
    }

    [Fact]
    public void GpuSkinnedBounds_TransformsCachedBoneClustersConservatively()
    {
        var bone0 = BitConverter.Int32BitsToSingle(0);
        var bone1 = BitConverter.Int32BitsToSingle(1);
        float[] vertices =
        [
            0f, 8f, 8f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, bone0,
            16f, 8f, 8f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, bone1,
        ];
        uint[] indices = [0, 1];
        PreviewDrawBatch[] batches = [new(0, 2, 0)];
        var cached = PreviewGpuSkinnedBounds.TryBuild(batches, vertices, indices, vertexStrideFloats: 13);
        Matrix4x4[] bones =
        [
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(16f, 0f, 0f),
        ];

        Assert.NotNull(cached);
        Assert.True(cached!.UpdateDrawBatchBounds(batches, bones, bones.Length, meshSpaceLiftY: 0.25f));
        Assert.True(batches[0].HasBounds);
        Assert.Equal(new Vector3(0.5f, 0.25f, 0f), batches[0].BoundsCenter);
        Assert.Equal(1f, batches[0].BoundsRadius, 5);
    }

    [Fact]
    public void GpuSkinnedBounds_InvalidPaletteRestoresAlwaysVisibleFallback()
    {
        var bone0 = BitConverter.Int32BitsToSingle(0);
        float[] vertices =
        [
            0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, bone0,
        ];
        uint[] indices = [0];
        PreviewDrawBatch[] batches = [new(0, 1, 0) { BoundsRadius = 2f }];
        var cached = PreviewGpuSkinnedBounds.TryBuild(batches, vertices, indices, vertexStrideFloats: 13);

        Assert.NotNull(cached);
        Assert.False(cached!.UpdateDrawBatchBounds(batches, [], boneCount: 0, meshSpaceLiftY: 0f));
        Assert.False(batches[0].HasBounds);
    }

    private static float SignedDistance(Vector4 plane, Vector3 point) =>
        Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), point) + plane.W;
}
