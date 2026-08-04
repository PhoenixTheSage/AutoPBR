using System.Numerics;

using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class PreviewSunScreenProjectionTests
{
    private const float Epsilon = 0.02f;

    [Fact]
    public void TryProjectWorldDirectionToUv_LookingAtSun_MapsNearCenter()
    {
        var eye = new Vector3(0f, 2f, 6f);
        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        var towardSun = Vector3.Normalize(-lightDir);
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(eye, eye + towardSun * 10f, Vector3.UnitY);
        var proj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            45f * (MathF.PI / 180f), 16f / 9f, 0.1f, 500f);

        Assert.True(PreviewSunScreenProjection.TryProjectWorldDirectionToUv(
            towardSun, view, proj, out var uv));
        Assert.Equal(0.5f, uv.X, Epsilon);
        Assert.Equal(0.5f, uv.Y, Epsilon);
    }

    [Fact]
    public void TryProjectWorldDirectionToUv_HighSunLookingAtGround_ProjectsAboveViewport()
    {
        var eye = new Vector3(0f, 2f, 6f);
        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        var towardSun = Vector3.Normalize(-lightDir);
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(eye, Vector3.Zero, Vector3.UnitY);
        var proj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            45f * (MathF.PI / 180f), 16f / 9f, 0.1f, 500f);

        Assert.True(PreviewSunScreenProjection.TryProjectWorldDirectionToUv(
            towardSun, view, proj, out var uv));
        // Must not fold onto the visible frame — that aimed shafts at the wrong place.
        Assert.True(uv.Y > 1.2f, $"expected sun UV above viewport, got {uv}");
    }

    [Fact]
    public void TryCompute_DefaultLightPose_YieldsValidConeTowardSun()
    {
        var eye = new Vector3(0f, 2f, 6f);
        var lookTarget = Vector3.Zero;
        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(eye, lookTarget, Vector3.UnitY);
        var proj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            45f * (MathF.PI / 180f), 16f / 9f, 0.1f, 500f);

        Assert.True(PreviewSunScreenProjection.TryCompute(
            eye, lightDir, view, proj, 16f / 9f, 1f, 1f,
            out var sunUv, out var discRadiusUv, out var coneRadiusUv, out var cosDiscEdge));

        // High sun can project above the viewport; UV may be outside [0,1] while still in front.
        Assert.True(float.IsFinite(sunUv.X) && float.IsFinite(sunUv.Y));
        Assert.True(sunUv.Y > 1f, $"high sun should project above the frame, got {sunUv}");
        Assert.True(discRadiusUv > 0.005f);
        Assert.True(coneRadiusUv >= discRadiusUv);
        Assert.True(cosDiscEdge > 0.85f);
        Assert.True(cosDiscEdge < 1f);
    }

    [Fact]
    public void TryCompute_FixedMatrices_ProjectsFiniteUvAndDisc()
    {
        var eye = new Vector3(1.2f, 3.4f, 8.5f);
        var lookTarget = new Vector3(0f, 1f, 0f);
        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(eye, lookTarget, Vector3.UnitY);
        var aspect = 1.777f;
        var proj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            50f * (MathF.PI / 180f), aspect, 0.05f, 400f);

        Assert.True(PreviewSunScreenProjection.TryCompute(
            eye, lightDir, view, proj, aspect, 1f, 1f,
            out var sunUv, out var discRadiusUv, out var coneRadiusUv, out _));

        Assert.True(float.IsFinite(sunUv.X) && float.IsFinite(sunUv.Y));
        // Previous golden of (0.5,0.5) was the behind-camera fallback, not a real projection.
        Assert.False(MathF.Abs(sunUv.X - 0.5f) < 0.01f && MathF.Abs(sunUv.Y - 0.5f) < 0.01f);
        Assert.True(discRadiusUv >= 0.008f);
        Assert.True(coneRadiusUv >= discRadiusUv);
    }

    [Fact]
    public void TryCompute_BehindCamera_ReturnsFalse()
    {
        var eye = new Vector3(0f, 2f, 6f);
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(eye, Vector3.Zero, Vector3.UnitY);
        var proj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            45f * (MathF.PI / 180f), 1f, 0.1f, 500f);
        // Light traveling toward -Z (same as camera look) ⇒ sun sits behind the camera at +Z.
        var lightDir = Vector3.Normalize(new Vector3(0f, 0f, -1f));

        Assert.False(PreviewSunScreenProjection.TryCompute(
            eye, lightDir, view, proj, 1f, 1f, 1f,
            out _, out _, out _, out _));
    }

    [Fact]
    public void Compute_ConeScale_ScalesShaftRadiusMonotonically()
    {
        var eye = new Vector3(0f, 2f, 6f);
        var lookTarget = Vector3.Zero;
        var lightDir = PreviewLightMath.LightDirectionFromYawPitch(-35.0, -55.0);
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(eye, lookTarget, Vector3.UnitY);
        var aspect = 1f;
        var proj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            45f * (MathF.PI / 180f), aspect, 0.1f, 500f);

        PreviewSunScreenProjection.Compute(eye, lightDir, view, proj, aspect, 0.5f, 1f,
            out _, out _, out var narrow, out _);
        PreviewSunScreenProjection.Compute(eye, lightDir, view, proj, aspect, 1.5f, 1f,
            out _, out _, out var wide, out _);

        Assert.True(wide > narrow);
    }

    [Fact]
    public void ComputeMoon_AngularSize_SmallerThanSun()
    {
        var pose = PreviewVolumetricRegressionFixtures.All.First(p => p.Id == "midnight-0h");
        var (view, proj) = pose.BuildMatrices();

        PreviewSunScreenProjection.Compute(pose.Eye, pose.LightDir, view, proj, pose.Aspect, pose.ConeScale, 1f,
            out _, out _, out _, out var sunCosDiscEdge);
        PreviewSunScreenProjection.ComputeMoon(pose.Eye, pose.LightDir, view, proj, pose.Aspect,
            out _, out _, out var moonCosDiscEdge);

        Assert.True(PreviewSunScreenProjection.MoonRadius < PreviewSunScreenProjection.SunRadius);
        Assert.True(moonCosDiscEdge > sunCosDiscEdge);
    }

    [Fact]
    public void WorldToViewportUv_CenterOfFrustum_MapsNearCenter()
    {
        var eye = new Vector3(0f, 0f, 5f);
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(eye, Vector3.Zero, Vector3.UnitY);
        var proj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            60f * (MathF.PI / 180f), 1f, 0.1f, 100f);
        var viewProj = proj * view;

        var uv = PreviewSunScreenProjection.WorldToViewportUv(Vector3.Zero, viewProj);

        Assert.Equal(0.5f, uv.X, Epsilon);
        Assert.Equal(0.5f, uv.Y, Epsilon);
    }
}
