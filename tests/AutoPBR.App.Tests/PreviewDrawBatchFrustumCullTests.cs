using System.Numerics;

using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public class PreviewDrawBatchFrustumCullTests
{
    private static (Vector3 Eye, Vector4[] Planes) CreateLookingDownNegZFrustum()
    {
        var eye = new Vector3(0f, 0f, 5f);
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(eye, Vector3.Zero, Vector3.UnitY);
        var projection = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            MathF.PI / 3f,
            1f,
            0.1f,
            100f);
        var vp = projection * view;
        var planes = new Vector4[PreviewFrustumPlanes.PlaneCount];
        PreviewFrustumPlanes.Extract(vp, planes);
        return (eye, planes);
    }

    [Fact]
    public void IsBatchVisible_False_WhenSphereClearlyOutsideFrustum()
    {
        var (eye, planes) = CreateLookingDownNegZFrustum();
        var batch = new PreviewDrawBatch(0, 3, 0)
        {
            BoundsCenter = new Vector3(100f, 0f, 0f),
            BoundsRadius = 1f,
        };

        Assert.False(PreviewDrawBatchFrustumCull.IsBatchVisible(
            batch, planes, eye, Matrix4x4.Identity));
    }

    [Fact]
    public void IsBatchVisible_True_WhenSphereStraddlesFrustumPlane()
    {
        var (eye, planes) = CreateLookingDownNegZFrustum();
        // Near the right edge of the frustum at z≈0 (10 units ahead of eye at z=5 → look toward 0).
        var forward = Vector3.Normalize(-Vector3.UnitZ);
        var dist = 10f;
        var halfW = MathF.Tan(MathF.PI / 6f) * dist;
        var batch = new PreviewDrawBatch(0, 3, 0)
        {
            BoundsCenter = eye + forward * dist + Vector3.UnitX * halfW,
            BoundsRadius = 2f,
        };

        Assert.True(PreviewDrawBatchFrustumCull.IsBatchVisible(
            batch, planes, eye, Matrix4x4.Identity));
    }

    [Fact]
    public void IsBatchVisible_True_WhenBoundsUnknown()
    {
        var (eye, planes) = CreateLookingDownNegZFrustum();
        var batch = new PreviewDrawBatch(0, 3, 0)
        {
            BoundsCenter = new Vector3(1000f, 0f, 0f),
            BoundsRadius = -1f,
        };

        Assert.False(batch.HasBounds);
        Assert.True(PreviewDrawBatchFrustumCull.IsBatchVisible(
            batch, planes, eye, Matrix4x4.Identity));
    }

    [Fact]
    public void IsBatchVisible_ModelTranslationMovesBatchIntoFrustum()
    {
        var (eye, planes) = CreateLookingDownNegZFrustum();
        var batch = new PreviewDrawBatch(0, 3, 0)
        {
            BoundsCenter = new Vector3(100f, 0f, 0f),
            BoundsRadius = 0.5f,
        };

        Assert.False(PreviewDrawBatchFrustumCull.IsBatchVisible(
            batch, planes, eye, Matrix4x4.Identity));

        // Compactor uses row-coefficient multiply: translation lives in M14/M24/M34.
        var model = Matrix4x4.Identity;
        model.M14 = -100f;
        Assert.True(PreviewDrawBatchFrustumCull.IsBatchVisible(
            batch, planes, eye, model));
    }

    [Fact]
    public void IsBatchVisible_False_WhenBeyondLodMaxDistance()
    {
        var (eye, planes) = CreateLookingDownNegZFrustum();
        var batch = new PreviewDrawBatch(0, 3, 0)
        {
            BoundsCenter = Vector3.Zero,
            BoundsRadius = 0.5f,
            LodMaxDistance = 1f,
        };

        Assert.False(PreviewDrawBatchFrustumCull.IsBatchVisible(
            batch, planes, eye, Matrix4x4.Identity));
    }

    [Fact]
    public void IsSubjectFullyCulled_True_WhenAllBatchesOutside()
    {
        var (eye, planes) = CreateLookingDownNegZFrustum();
        PreviewDrawBatch[] batches =
        [
            new(0, 3, 0) { BoundsCenter = new Vector3(80f, 0f, 0f), BoundsRadius = 1f },
            new(3, 3, 0) { BoundsCenter = new Vector3(-80f, 0f, 0f), BoundsRadius = 1f },
        ];

        Assert.True(PreviewDrawBatchFrustumCull.IsSubjectFullyCulled(
            batches, planes, eye, Matrix4x4.Identity));
    }

    [Fact]
    public void IsSubjectFullyCulled_False_WhenAnyBatchHasUnknownBounds()
    {
        var (eye, planes) = CreateLookingDownNegZFrustum();
        PreviewDrawBatch[] batches =
        [
            new(0, 3, 0) { BoundsCenter = new Vector3(80f, 0f, 0f), BoundsRadius = 1f },
            new(3, 3, 0) { BoundsRadius = -1f },
        ];

        Assert.False(PreviewDrawBatchFrustumCull.IsSubjectFullyCulled(
            batches, planes, eye, Matrix4x4.Identity));
    }

    [Fact]
    public void TransformBatchSphere_ScalesRadiusByModelAxisLength()
    {
        var batch = new PreviewDrawBatch(0, 3, 0)
        {
            BoundsCenter = Vector3.Zero,
            BoundsRadius = 2f,
        };
        var model = Matrix4x4.Identity;
        model.M11 = 3f;
        model.M22 = 3f;
        model.M33 = 3f;

        PreviewDrawBatchFrustumCull.TransformBatchSphere(
            batch, model, boundsPadding: 1f, out var center, out var radius);

        Assert.Equal(Vector3.Zero, center);
        Assert.Equal(9f, radius, 5);
    }
}
