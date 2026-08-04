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

    [Fact]
    public void ResolveShadowCasterInclusionPadding_stays_small_and_grows_with_low_sun()
    {
        var highSun = Vector3.Normalize(new Vector3(0.1f, -0.95f, 0.1f));
        var lowSun = Vector3.Normalize(new Vector3(0.7f, -0.15f, 0.2f));
        var highPad = OpenGlPreviewBackend.ResolveShadowCasterInclusionPadding(highSun, 8f, 48f);
        var lowPad = OpenGlPreviewBackend.ResolveShadowCasterInclusionPadding(lowSun, 8f, 48f);
        Assert.True(lowPad >= highPad);
        Assert.True(highPad >= 0f);
        Assert.True(lowPad <= 6f);
    }

    [Fact]
    public void BuildDirectionalViewProj_camera_plus_distant_stage_clamp_keeps_camera_inside_when_camera_centered()
    {
        // Regression: unioning stage-at-origin into the far AABB then clamping maxHalfExtent
        // recentered the ortho off a distant fly camera and wiped nearby shadows.
        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        const float farHalf = 128f;
        var camera = new Vector3(400f, 10f, 0f);

        PreviewShadowFrustum.SeedTerrainShadowBounds(
            focusXz: camera with { Y = 0f },
            groundFloorY: -1f,
            groundCeilingY: 8f,
            xzHalfExtent: farHalf,
            out var camMin,
            out var camMax);
        PreviewShadowFrustum.SeedTerrainShadowBounds(
            focusXz: Vector3.Zero,
            groundFloorY: -1f,
            groundCeilingY: 8f,
            xzHalfExtent: PreviewShadowFrustum.TerrainShadowMinXzHalfExtent,
            out var stageMin,
            out var stageMax);

        var unionMin = camMin;
        var unionMax = camMax;
        PreviewShadowFrustum.EncapsulateAabb(ref unionMin, ref unionMax, stageMin, stageMax);
        var brokenVp = PreviewShadowFrustum.BuildDirectionalViewProj(
            lightDir,
            unionMin,
            unionMax,
            Matrix4x4.Identity,
            maxHalfExtent: farHalf);
        Assert.False(
            PointProjectsInsideFrustum(brokenVp, camera with { Y = 0f }),
            "precondition: stage+camera union + clamp must leave distant camera outside");

        var fixedVp = PreviewShadowFrustum.BuildDirectionalViewProj(
            lightDir,
            camMin,
            camMax,
            Matrix4x4.Identity,
            maxHalfExtent: farHalf);
        Assert.True(PointProjectsInsideFrustum(fixedVp, camera with { Y = 0f }));
        Assert.True(PointProjectsInsideFrustum(fixedVp, camera + new Vector3(8f, 0f, 0f)));
        Assert.True(PointProjectsInsideFrustum(fixedVp, camera + new Vector3(-8f, 0f, 0f)));
    }

    [Fact]
    public void BuildDirectionalViewProj_near_cascade_ignores_distant_subject_inflation()
    {
        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        const float nearHalf = 8f;
        var camera = new Vector3(400f, 10f, 0f);

        PreviewShadowFrustum.SeedTerrainShadowBounds(
            focusXz: camera with { Y = 0f },
            groundFloorY: -1f,
            groundCeilingY: 8f,
            xzHalfExtent: nearHalf,
            out var nearMin,
            out var nearMax);
        var inflatedMin = nearMin;
        var inflatedMax = nearMax;
        PreviewShadowFrustum.EncapsulateAabb(
            ref inflatedMin,
            ref inflatedMax,
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(0.5f, 2f, 0.5f));

        var brokenVp = PreviewShadowFrustum.BuildDirectionalViewProj(
            lightDir,
            inflatedMin,
            inflatedMax,
            Matrix4x4.Identity,
            maxHalfExtent: nearHalf);
        Assert.False(
            PointProjectsInsideFrustum(brokenVp, camera with { Y = 0f }),
            "precondition: distant subject inflation + clamp must leave camera outside near ortho");

        var fixedVp = PreviewShadowFrustum.BuildDirectionalViewProj(
            lightDir,
            nearMin,
            nearMax,
            Matrix4x4.Identity,
            maxHalfExtent: nearHalf);
        Assert.True(PointProjectsInsideFrustum(fixedVp, camera with { Y = 0f }));
    }

    [Fact]
    public void ShadowPass_GroundMeshFarCascade_UsesCameraCenteredFit()
    {
        var shadow = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassShadow.cs"));
        Assert.Contains("BuildCameraCenteredCascadeAabb(", shadow, StringComparison.Ordinal);
        Assert.Contains("TryEncapsulateNearbySubjectIntoCascadeAabb", shadow, StringComparison.Ordinal);
        // Far terrain fit must not reintroduce stage-origin encapsulation before maxHalfExtent clamp.
        var groundFarFit = shadow.IndexOf(
            "// Far must stay camera-centered like near/mid",
            StringComparison.Ordinal);
        Assert.True(groundFarFit >= 0);
        var stageSeedAfter = shadow.IndexOf(
            "focusXz: Vector3.Zero",
            groundFarFit,
            StringComparison.Ordinal);
        var nextMethod = shadow.IndexOf(
            "ResolveShadowCasterInclusionPadding",
            groundFarFit,
            StringComparison.Ordinal);
        Assert.True(stageSeedAfter < 0 || stageSeedAfter > nextMethod,
            "far cascade VP fit must not seed stage-at-origin before inclusion-padding helper");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AutoPBR.sln")) ||
                File.Exists(Path.Combine(dir.FullName, "AutoPBR.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}