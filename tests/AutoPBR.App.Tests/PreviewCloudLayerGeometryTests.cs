using System.Numerics;

using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudLayerGeometryTests
{
    private const float GroundY = -3.2f;
    private const float BaseAltitude = 18f;
    private const float TopAltitude = 42f;
    private const float MaxDistance = 4_096f;

    [Fact]
    public void BelowLayer_HorizontalRay_DoesNotInventPlanetaryHorizonIntersection()
    {
        var segment = PreviewCloudLayerGeometry.Intersect(
            new Vector3(0f, GroundY, 0f),
            Vector3.UnitX,
            GroundY,
            BaseAltitude,
            TopAltitude,
            MaxDistance);

        Assert.True(segment.Y < 0f);
    }

    [Fact]
    public void BelowLayer_SlightlyUpwardRay_ReachesFlatDeck()
    {
        var direction = Vector3.Normalize(new Vector3(1f, 0.01f, 0f));
        var segment = PreviewCloudLayerGeometry.Intersect(
            new Vector3(0f, GroundY, 0f),
            direction,
            GroundY,
            BaseAltitude,
            TopAltitude,
            MaxDistance);

        Assert.InRange(segment.X, 1_799f, 1_801f);
        Assert.True(segment.Y > segment.X);
    }

    [Theory]
    [InlineData(0f, 1f, 0f, 12f)]
    [InlineData(1f, 0f, 0f, MaxDistance)]
    [InlineData(0f, -1f, 0f, 12f)]
    public void InsideLayer_AllViewDirectionsHaveBoundedForwardSegment(
        float x,
        float y,
        float z,
        float expectedExit)
    {
        var camera = new Vector3(0f, GroundY + 30f, 0f);
        var segment = PreviewCloudLayerGeometry.Intersect(
            camera,
            new Vector3(x, y, z),
            GroundY,
            BaseAltitude,
            TopAltitude,
            MaxDistance);

        Assert.Equal(0f, segment.X, 3);
        Assert.Equal(expectedExit, segment.Y, 3);
    }

    [Fact]
    public void AboveLayer_DownwardRayTraversesDeck()
    {
        var segment = PreviewCloudLayerGeometry.Intersect(
            new Vector3(0f, GroundY + 60f, 0f),
            -Vector3.UnitY,
            GroundY,
            BaseAltitude,
            TopAltitude,
            MaxDistance);

        Assert.InRange(segment.X, 17.9f, 18.1f);
        Assert.InRange(segment.Y - segment.X, 23.9f, 24.1f);
    }

    [Fact]
    public void AboveLayer_UpwardRayMissesDeck()
    {
        var segment = PreviewCloudLayerGeometry.Intersect(
            new Vector3(0f, GroundY + 60f, 0f),
            Vector3.UnitY,
            GroundY,
            BaseAltitude,
            TopAltitude,
            MaxDistance);

        Assert.True(segment.Y < 0f);
    }

    [Fact]
    public void Altitude_PreservesSubCentimeterVerticalMotionAtArbitraryWorldPosition()
    {
        var below = PreviewCloudLayerGeometry.Altitude(
            new Vector3(750_000f, GroundY + BaseAltitude - 0.001f, -920_000f),
            GroundY);
        var above = PreviewCloudLayerGeometry.Altitude(
            new Vector3(750_000f, GroundY + BaseAltitude + 0.001f, -920_000f),
            GroundY);

        Assert.InRange(below, BaseAltitude - 0.0015f, BaseAltitude - 0.0005f);
        Assert.InRange(above, BaseAltitude + 0.0005f, BaseAltitude + 0.0015f);
        Assert.InRange(above - below, 0.0018f, 0.0022f);
    }

    [Fact]
    public void VerticalRoot_TracksCentimeterBoundaryCrossing()
    {
        var segment = PreviewCloudLayerGeometry.Intersect(
            new Vector3(0f, GroundY + BaseAltitude - 0.01f, 0f),
            Vector3.UnitY,
            GroundY,
            BaseAltitude,
            TopAltitude,
            MaxDistance);

        Assert.InRange(segment.X, 0.009f, 0.011f);
        Assert.True(segment.Y > segment.X);
    }

    [Fact]
    public void LayerAltitude_DoesNotChangeWithHorizontalWorldDistance()
    {
        var origin = PreviewCloudLayerGeometry.Altitude(
            new Vector3(0f, GroundY + BaseAltitude, 0f),
            GroundY);
        var distant = PreviewCloudLayerGeometry.Altitude(
            new Vector3(1_000_000f, GroundY + BaseAltitude, -1_000_000f),
            GroundY);

        Assert.Equal(BaseAltitude, origin);
        Assert.Equal(origin, distant);
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
            PreviewCloudLayerGeometry.SceneOcclusionVisibility(cloudDistance, sceneDistance));
    }

    [Fact]
    public void DistanceVisibility_FeathersOnlyTheFinalTraceRange()
    {
        var near = PreviewCloudLayerGeometry.DistanceVisibility(2_000f, MaxDistance);
        var fadeStart = PreviewCloudLayerGeometry.DistanceVisibility(
            MaxDistance * 0.8f,
            MaxDistance);
        var middle = PreviewCloudLayerGeometry.DistanceVisibility(
            MaxDistance * 0.9f,
            MaxDistance);
        var end = PreviewCloudLayerGeometry.DistanceVisibility(MaxDistance, MaxDistance);

        Assert.Equal(1f, near, 3);
        Assert.Equal(1f, fadeStart, 3);
        Assert.InRange(middle, 0.49f, 0.51f);
        Assert.Equal(0f, end, 3);
    }

    [Fact]
    public void MarchSpanLimit_ClampsLongIntervalsWithoutCameraRegionSwitch()
    {
        const float volumeSize = 175f;
        const float volumeHeight = 96f;
        var limit = PreviewCloudLayerGeometry.MarchSpanLimit(volumeSize, volumeHeight);

        Assert.Equal(Math.Max(Math.Max(volumeSize * 4f, volumeHeight * 8f), 256f), limit);

        var insideLong = PreviewCloudLayerGeometry.MarchStepLength(
            tEnter: 0f,
            tExit: MaxDistance,
            steps: 32,
            volumeSize,
            volumeHeight);
        var outsideLong = PreviewCloudLayerGeometry.MarchStepLength(
            tEnter: 100f,
            tExit: 100f + MaxDistance,
            steps: 32,
            volumeSize,
            volumeHeight);
        var insideShort = PreviewCloudLayerGeometry.MarchStepLength(
            tEnter: 0f,
            tExit: 48f,
            steps: 32,
            volumeSize,
            volumeHeight);
        var outsideShort = PreviewCloudLayerGeometry.MarchStepLength(
            tEnter: 100f,
            tExit: 148f,
            steps: 32,
            volumeSize,
            volumeHeight);
        Assert.Equal(limit / 32f, insideLong, 3);
        Assert.Equal(insideLong, outsideLong, 3);
        Assert.Equal(48f / 32f, insideShort, 3);
        Assert.Equal(insideShort, outsideShort, 3);
        Assert.True(insideShort < insideLong);
    }

    [Fact]
    public void MarchSpanLimit_UsesFloorWhenVolumeIsTiny()
    {
        Assert.Equal(
            PreviewCloudLayerGeometry.DefaultMarchSpanFloor,
            PreviewCloudLayerGeometry.MarchSpanLimit(8f, 4f));
    }
}
