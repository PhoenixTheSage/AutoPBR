using System.Numerics;
using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class PreviewShadowFrustumTests
{
    [Fact]
    public void BuildDirectionalViewProj_small_subject_stays_near_legacy_extent()
    {
        var min = new Vector3(-0.5f, 0f, -0.5f);
        var max = new Vector3(0.5f, 1.8f, 0.5f);
        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        var vp = PreviewShadowFrustum.BuildDirectionalViewProj(lightDir, min, max, Matrix4x4.Identity);
        Assert.True(AllCornersProjectInsideFrustum(vp, min, max, Matrix4x4.Identity));
        Assert.True(EstimateOrthoHalfExtent(vp) is >= 0.75f and <= 2.5f);
    }

    [Fact]
    public void BuildDirectionalViewProj_large_subject_expands_beyond_block_default_extent()
    {
        var min = new Vector3(-8f, -1f, -10f);
        var max = new Vector3(8f, 6f, 12f);
        PreviewShadowFrustum.ExpandBoundsForGroundReceiver(ref min, ref max, -0.56f);

        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        var vp = PreviewShadowFrustum.BuildDirectionalViewProj(
            lightDir,
            min,
            max,
            Matrix4x4.Identity,
            maxHalfExtent: 36f);

        Assert.True(AllCornersProjectInsideFrustum(vp, min, max, Matrix4x4.Identity));
    }

    [Fact]
    public void ExpandBoundsForGroundReceiver_includes_terrain_ceiling_and_min_xz()
    {
        var min = new Vector3(-0.5f, 0f, -0.5f);
        var max = new Vector3(0.5f, 1.8f, 0.5f);
        const float groundY = -0.56f;
        const float ceilingY = groundY + 6f;
        PreviewShadowFrustum.ExpandBoundsForGroundReceiver(
            ref min,
            ref max,
            groundY,
            ceilingY,
            PreviewShadowFrustum.TerrainShadowMinXzHalfExtent);

        Assert.True(min.Y <= groundY);
        Assert.True(max.Y >= ceilingY);
        Assert.True(max.X - min.X >= PreviewShadowFrustum.TerrainShadowMinXzHalfExtent * 2f - 1e-3f);
        Assert.True(max.Z - min.Z >= PreviewShadowFrustum.TerrainShadowMinXzHalfExtent * 2f - 1e-3f);
    }

    [Fact]
    public void BuildDirectionalViewProj_terrain_relief_corners_stay_inside_fitted_frustum()
    {
        PreviewShadowFrustum.SeedTerrainShadowBounds(
            focusXz: Vector3.Zero,
            groundFloorY: -0.56f - 3f,
            groundCeilingY: -0.56f + 6f,
            xzHalfExtent: PreviewShadowFrustum.TerrainShadowMinXzHalfExtent,
            out var min,
            out var max);

        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        var vp = PreviewShadowFrustum.BuildDirectionalViewProj(
            lightDir,
            min,
            max,
            Matrix4x4.Identity,
            maxHalfExtent: PreviewShadowFrustum.TerrainShadowFarMaxHalfExtent);

        Assert.True(AllCornersProjectInsideFrustum(vp, min, max, Matrix4x4.Identity));
        // Hill top and valley floor samples that previously fell outside the subject-fitted ortho.
        Assert.True(PointProjectsInsideFrustum(vp, new Vector3(18f, -0.56f + 6f, 18f)));
        Assert.True(PointProjectsInsideFrustum(vp, new Vector3(0f, -0.56f, 0f)));
        Assert.True(PointProjectsInsideFrustum(vp, new Vector3(40f, -0.56f, 40f)));
    }

    [Fact]
    public void ShadowDistanceCap_isAppliedBeforeFarFrustumFit()
    {
        // PassShadow uses min(ShadowDistance, streamed LOD ring) as the far ortho half-extent.
        const float shadowDistance = 64f;
        var ring = PreviewShadowFrustum.TerrainShadowFarMaxHalfExtent;
        var farHalf = Math.Min(shadowDistance, ring);
        Assert.Equal(64f, farHalf);
        Assert.True(farHalf < ring);

        PreviewShadowFrustum.SeedTerrainShadowBounds(
            focusXz: Vector3.Zero,
            groundFloorY: -1f,
            groundCeilingY: 8f,
            xzHalfExtent: farHalf,
            out var cappedMin,
            out var cappedMax);
        PreviewShadowFrustum.SeedTerrainShadowBounds(
            focusXz: Vector3.Zero,
            groundFloorY: -1f,
            groundCeilingY: 8f,
            xzHalfExtent: ring,
            out var uncappedMin,
            out var uncappedMax);

        Assert.True(cappedMax.X - cappedMin.X < uncappedMax.X - uncappedMin.X);
        Assert.True(cappedMax.X - cappedMin.X <= shadowDistance * 2f + 1e-3f);
    }

    [Fact]
    public void BuildDirectionalViewProj_legacy_fixed_extent_would_clip_large_subject()
    {
        var min = new Vector3(-8f, -1f, -10f);
        var max = new Vector3(8f, 6f, 12f);
        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        var legacyVp = BuildLegacyFixedHalfExtent(lightDir, 1.5f);
        Assert.False(AllCornersProjectInsideFrustum(legacyVp, min, max, Matrix4x4.Identity));
    }

    private static Matrix4x4 BuildLegacyFixedHalfExtent(Vector3 worldLightDir, float orthoHalfExtent)
    {
        const float shadowBoom = 4.0f;
        const float shadowNear = shadowBoom - 2.5f;
        const float shadowFar = shadowBoom + 2.5f;
        var shadowTargetPos = Vector3.Zero;
        var shadowEye = shadowTargetPos - worldLightDir * shadowBoom;
        var shadowUp = PreviewLightMath.PickShadowViewUp(worldLightDir);
        var shadowView = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(shadowEye, shadowTargetPos, shadowUp);
        var shadowProj = PreviewGlMatrices.CreateOrthographicOpenGlRowStorage(
            -orthoHalfExtent, orthoHalfExtent,
            -orthoHalfExtent, orthoHalfExtent,
            shadowNear, shadowFar);
        return shadowProj * shadowView;
    }

    private static bool AllCornersProjectInsideFrustum(
        Matrix4x4 lightViewProjRowStorage,
        Vector3 boundsMin,
        Vector3 boundsMax,
        Matrix4x4 worldFromModel)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        WriteCorners(boundsMin, boundsMax, corners);
        foreach (var corner in corners)
        {
            var world = Vector3.Transform(corner, worldFromModel);
            if (!PointProjectsInsideFrustum(lightViewProjRowStorage, world))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PointProjectsInsideFrustum(Matrix4x4 lightViewProjRowStorage, Vector3 world)
    {
        var columnVp = Matrix4x4.Transpose(lightViewProjRowStorage);
        var clip = Vector4.Transform(new Vector4(world, 1f), columnVp);
        if (MathF.Abs(clip.W) < 1e-5f)
        {
            return false;
        }

        var invW = 1f / clip.W;
        var ndc = new Vector3(clip.X * invW, clip.Y * invW, clip.Z * invW);
        return ndc.X is >= -1.02f and <= 1.02f &&
               ndc.Y is >= -1.02f and <= 1.02f &&
               ndc.Z is >= -1.02f and <= 1.02f;
    }

    private static float EstimateOrthoHalfExtent(Matrix4x4 lightViewProjRowStorage)
    {
        var columnVp = Matrix4x4.Transpose(lightViewProjRowStorage);
        var origin = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), columnVp);
        var xAxis = Vector4.Transform(new Vector4(1f, 0f, 0f, 1f), columnVp);
        if (MathF.Abs(origin.W) < 1e-5f)
        {
            return 0f;
        }

        var o = new Vector2(origin.X / origin.W, origin.Y / origin.W);
        var a = new Vector2(xAxis.X / xAxis.W, xAxis.Y / xAxis.W);
        return Vector2.Distance(o, a);
    }

    private static void WriteCorners(Vector3 min, Vector3 max, Span<Vector3> corners)
    {
        corners[0] = new Vector3(min.X, min.Y, min.Z);
        corners[1] = new Vector3(max.X, min.Y, min.Z);
        corners[2] = new Vector3(min.X, max.Y, min.Z);
        corners[3] = new Vector3(max.X, max.Y, min.Z);
        corners[4] = new Vector3(min.X, min.Y, max.Z);
        corners[5] = new Vector3(max.X, min.Y, max.Z);
        corners[6] = new Vector3(min.X, max.Y, max.Z);
        corners[7] = new Vector3(max.X, max.Y, max.Z);
    }
}
