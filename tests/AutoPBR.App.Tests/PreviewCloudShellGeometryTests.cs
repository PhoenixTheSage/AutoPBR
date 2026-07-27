using System.Numerics;

using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudShellGeometryTests
{
    private const float GroundY = 0f;
    private const float BaseAltitude = 18f;
    private const float TopAltitude = 42f;

    [Fact]
    public void BelowLayer_HorizontalRay_ReachesGeometricHorizon()
    {
        var center = PreviewCloudShellGeometry.PlanetCenter(GroundY);
        var segment = PreviewCloudShellGeometry.Intersect(
            Vector3.Zero,
            Vector3.UnitX,
            center,
            PreviewCloudShellGeometry.PlanetRadius + BaseAltitude,
            PreviewCloudShellGeometry.PlanetRadius + TopAltitude);

        Assert.True(segment.Y > segment.X);
        Assert.InRange(segment.X, 1600f, 1620f);
    }

    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, -1f, 0f)]
    public void InsideLayer_AllViewDirectionsHaveAForwardSegment(float x, float y, float z)
    {
        var center = PreviewCloudShellGeometry.PlanetCenter(GroundY);
        var camera = new Vector3(0f, 30f, 0f);
        var segment = PreviewCloudShellGeometry.Intersect(
            camera,
            new Vector3(x, y, z),
            center,
            PreviewCloudShellGeometry.PlanetRadius + BaseAltitude,
            PreviewCloudShellGeometry.PlanetRadius + TopAltitude);

        Assert.Equal(0f, segment.X, 3);
        Assert.True(segment.Y > 0f);
    }

    [Fact]
    public void AboveLayer_DownwardRayTraversesDeck()
    {
        var center = PreviewCloudShellGeometry.PlanetCenter(GroundY);
        var segment = PreviewCloudShellGeometry.Intersect(
            new Vector3(0f, 60f, 0f),
            -Vector3.UnitY,
            center,
            PreviewCloudShellGeometry.PlanetRadius + BaseAltitude,
            PreviewCloudShellGeometry.PlanetRadius + TopAltitude);

        Assert.InRange(segment.X, 17.9f, 18.1f);
        Assert.InRange(segment.Y - segment.X, 23.9f, 24.1f);
    }

    [Fact]
    public void AboveLayer_UpwardRayMissesDeck()
    {
        var center = PreviewCloudShellGeometry.PlanetCenter(GroundY);
        var segment = PreviewCloudShellGeometry.Intersect(
            new Vector3(0f, 60f, 0f),
            Vector3.UnitY,
            center,
            PreviewCloudShellGeometry.PlanetRadius + BaseAltitude,
            PreviewCloudShellGeometry.PlanetRadius + TopAltitude);

        Assert.True(segment.Y < 0f);
    }

    [Theory]
    [InlineData(17.99f, PreviewCloudCameraRegion.Below)]
    [InlineData(18f, PreviewCloudCameraRegion.Inside)]
    [InlineData(30f, PreviewCloudCameraRegion.Inside)]
    [InlineData(42f, PreviewCloudCameraRegion.Inside)]
    [InlineData(42.01f, PreviewCloudCameraRegion.Above)]
    public void CameraRegion_MatchesCloudShellBoundaries(
        float altitude,
        PreviewCloudCameraRegion expected)
    {
        var center = PreviewCloudShellGeometry.PlanetCenter(GroundY);
        var camera = new Vector3(0f, altitude, 0f);

        var actual = PreviewCloudShellGeometry.ClassifyCamera(
            camera,
            center,
            BaseAltitude,
            TopAltitude);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(20f, 50f, 1f)]
    [InlineData(50f, 20f, 0f)]
    [InlineData(299f, 300f, 1f)]
    [InlineData(300f, 300f, 0f)]
    [InlineData(350f, 300f, 0f)]
    [InlineData(20f, float.PositiveInfinity, 1f)]
    public void SceneOcclusionVisibility_OrdersCloudAgainstOpaqueDepth(
        float cloudDistance,
        float sceneDistance,
        float expected)
    {
        Assert.Equal(
            expected,
            PreviewCloudShellGeometry.SceneOcclusionVisibility(cloudDistance, sceneDistance));
    }

    [Fact]
    public void GroundCamera_DownwardRay_IsImmediatelyOccludedByPlanet()
    {
        var center = PreviewCloudShellGeometry.PlanetCenter(GroundY);
        var distance = PreviewCloudShellGeometry.PlanetOcclusionDistance(
            Vector3.Zero,
            -Vector3.UnitY,
            center);

        Assert.Equal(0f, distance, 3);
    }

    [Fact]
    public void GroundCamera_UpwardRay_IsNotOccludedByPlanet()
    {
        var center = PreviewCloudShellGeometry.PlanetCenter(GroundY);
        var distance = PreviewCloudShellGeometry.PlanetOcclusionDistance(
            Vector3.Zero,
            Vector3.UnitY,
            center);

        Assert.True(float.IsPositiveInfinity(distance));
    }

    [Fact]
    public void ElevatedCamera_SlightlyDownwardRay_HitsPlanetBeforeFarSideCloudShell()
    {
        var center = PreviewCloudShellGeometry.PlanetCenter(GroundY);
        var camera = new Vector3(0f, 6f, 0f);
        var direction = Vector3.Normalize(new Vector3(1f, -0.08f, 0f));
        var planetDistance = PreviewCloudShellGeometry.PlanetOcclusionDistance(
            camera,
            direction,
            center);
        var cloudSegment = PreviewCloudShellGeometry.Intersect(
            camera,
            direction,
            center,
            PreviewCloudShellGeometry.PlanetRadius + BaseAltitude,
            PreviewCloudShellGeometry.PlanetRadius + TopAltitude);

        Assert.True(float.IsFinite(planetDistance));
        Assert.True(planetDistance < cloudSegment.X);
    }

    [Fact]
    public void HorizonVisibility_BiasesNarrowFadeBehindTheTangent()
    {
        const float feather = 0.0025f;
        var center = PreviewCloudShellGeometry.PlanetCenter(GroundY);
        var camera = new Vector3(0f, 6f, 0f);
        var cameraRadius = (camera - center).Length();
        var ratio = PreviewCloudShellGeometry.PlanetRadius / cameraRadius;
        var horizonMu = -MathF.Sqrt(1f - ratio * ratio);

        static Vector3 DirectionForMu(float mu) =>
            Vector3.Normalize(new Vector3(MathF.Sqrt(Math.Max(1f - mu * mu, 0f)), mu, 0f));

        var below = PreviewCloudShellGeometry.PlanetHorizonVisibility(
            camera, DirectionForMu(horizonMu - feather * 2f), center, feather);
        var slightlyBehind = PreviewCloudShellGeometry.PlanetHorizonVisibility(
            camera, DirectionForMu(horizonMu - feather * 0.5f), center, feather);
        var tangent = PreviewCloudShellGeometry.PlanetHorizonVisibility(
            camera, DirectionForMu(horizonMu), center, feather);
        var above = PreviewCloudShellGeometry.PlanetHorizonVisibility(
            camera, DirectionForMu(horizonMu + feather * 0.25f), center, feather);

        Assert.Equal(0f, below, 3);
        Assert.InRange(slightlyBehind, 0.73f, 0.75f);
        Assert.InRange(tangent, 0.96f, 0.97f);
        Assert.Equal(1f, above, 3);
    }

    [Fact]
    public void DefaultPlanetRadius_KeepsNearAndMidDistanceCurvatureSubtle()
    {
        const float horizontalDistance = 500f;
        var radius = PreviewCloudShellGeometry.PlanetRadius;
        var surfaceDrop = radius - MathF.Sqrt(radius * radius - horizontalDistance * horizontalDistance);

        Assert.InRange(surfaceDrop, 1.70f, 1.80f);
    }
}
