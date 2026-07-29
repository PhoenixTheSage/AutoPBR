using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudLightCoordinatesTests
{
    [Fact]
    public void Basis_IsOrthonormalAndRoundTripsWorldCoordinates()
    {
        var basis = PreviewCloudLightBasisBuilder.Build(
            Vector3.Normalize(new Vector3(0.35f, -0.82f, 0.44f)));

        AssertClose(1f, basis.Right.Length());
        AssertClose(1f, basis.Up.Length());
        AssertClose(1f, basis.Forward.Length());
        AssertClose(0f, Vector3.Dot(basis.Right, basis.Up));
        AssertClose(0f, Vector3.Dot(basis.Right, basis.Forward));
        AssertClose(0f, Vector3.Dot(basis.Up, basis.Forward));

        var world = new Vector3(19.25f, 83.5f, -44.75f);
        AssertVectorClose(world, basis.LightToWorld(basis.WorldToLight(world)));
    }

    [Fact]
    public void ReferenceAxis_UsesHysteresisNearVerticalThreshold()
    {
        static Vector3 DirectionWithVerticalAlignment(float y) =>
            Vector3.Normalize(new Vector3(MathF.Sqrt(Math.Max(0f, 1f - y * y)), y, 0f));

        var worldUp = PreviewCloudLightBasisBuilder.Build(DirectionWithVerticalAlignment(0.90f));
        Assert.Equal(PreviewCloudLightReferenceAxis.WorldUp, worldUp.ReferenceAxis);

        var retainedUp = PreviewCloudLightBasisBuilder.Build(
            DirectionWithVerticalAlignment(0.93f),
            worldUp);
        Assert.Equal(PreviewCloudLightReferenceAxis.WorldUp, retainedUp.ReferenceAxis);

        var worldRight = PreviewCloudLightBasisBuilder.Build(
            DirectionWithVerticalAlignment(0.96f),
            retainedUp);
        Assert.Equal(PreviewCloudLightReferenceAxis.WorldRight, worldRight.ReferenceAxis);

        var retainedRight = PreviewCloudLightBasisBuilder.Build(
            DirectionWithVerticalAlignment(0.90f),
            worldRight);
        Assert.Equal(PreviewCloudLightReferenceAxis.WorldRight, retainedRight.ReferenceAxis);

        var returnedUp = PreviewCloudLightBasisBuilder.Build(
            DirectionWithVerticalAlignment(0.86f),
            retainedRight);
        Assert.Equal(PreviewCloudLightReferenceAxis.WorldUp, returnedUp.ReferenceAxis);
    }

    [Fact]
    public void SnappedOrigin_IsStableInsideOneTexelAndMovesByWholeTexels()
    {
        var profile = PreviewCloudLightingCacheProfiles
            .Resolve(PreviewVolumetricQuality.High)
            .Near;
        var basis = PreviewCloudLightBasisBuilder.Build(new Vector3(0f, 0f, -1f));
        var anchor = basis.Right * 10.5f + basis.Up * 9f;
        var first = PreviewCloudLightCascadeTransform.Create(
            basis,
            profile,
            cameraGroundProjection: anchor,
            lightDepthMin: -128f,
            lightDepthMax: 128f);
        var inside = PreviewCloudLightCascadeTransform.Create(
            basis,
            profile,
            cameraGroundProjection: anchor + basis.Right,
            lightDepthMin: -127f,
            lightDepthMax: 129f);
        var crossed = PreviewCloudLightCascadeTransform.Create(
            basis,
            profile,
            cameraGroundProjection:
                anchor + basis.Right * (first.PlaneTexelWorldSize * 1.1f),
            lightDepthMin: -128f,
            lightDepthMax: 128f);

        Assert.Equal(first.PlaneCenterX, inside.PlaneCenterX);
        Assert.Equal(first.PlaneCenterY, inside.PlaneCenterY);
        AssertClose(
            first.PlaneTexelWorldSize,
            MathF.Abs(crossed.PlaneCenterX - first.PlaneCenterX));
    }

    [Fact]
    public void CascadeTransform_RoundTripsAndMapsCenterToHalfUv()
    {
        var profile = PreviewCloudLightingCacheProfiles
            .Resolve(PreviewVolumetricQuality.Cinematic)
            .Far;
        var basis = PreviewCloudLightBasisBuilder.Build(
            Vector3.Normalize(new Vector3(0.4f, -0.6f, 0.7f)));
        var transform = PreviewCloudLightCascadeTransform.Create(
            basis,
            profile,
            cameraGroundProjection: Vector3.Zero,
            lightDepthMin: -512f,
            lightDepthMax: 512f);

        var centerUnit = new Vector3(0.5f, 0.5f, 0.5f);
        var centerWorld = transform.UnitToWorld(centerUnit);
        AssertVectorClose(centerUnit, transform.WorldToUnit(centerWorld));
        Assert.True(transform.Contains(centerWorld));
    }

    [Fact]
    public void AltitudeBounds_IncludeCumulusCirrusAndOneDetailPeriod()
    {
        var bounds = PreviewCloudLightAltitudeBounds.Create(
            groundWorldY: -3.2f,
            layerWorldY: 23.36f,
            volumeHeight: 60f,
            volumeSize: 178f,
            cirrusStrength: 0.13f);

        AssertClose(26.56f, bounds.CumulusBaseAltitude);
        AssertClose(86.56f, bounds.CumulusTopAltitude);
        AssertClose(176.56f, bounds.CirrusBaseAltitude);
        AssertClose(178.66f, bounds.CirrusTopAltitude);
        AssertClose(89f, bounds.DetailPadding);
        Assert.True(bounds.MinimumAltitude < bounds.CumulusBaseAltitude);
        Assert.True(bounds.MaximumAltitude > bounds.CirrusTopAltitude);
    }

    [Fact]
    public void DepthInterval_ContainsProjectedCloudEnvelopeAndCurvatureGuard()
    {
        var profile = PreviewCloudLightingCacheProfiles
            .Resolve(PreviewVolumetricQuality.Cinematic)
            .Far;
        var basis = PreviewCloudLightBasisBuilder.Build(
            Vector3.Normalize(new Vector3(0.35f, -0.7f, 0.62f)));
        var bounds = PreviewCloudLightAltitudeBounds.Create(
            groundWorldY: -3.2f,
            layerWorldY: 23.36f,
            volumeHeight: 60f,
            volumeSize: 178f,
            cirrusStrength: 0.13f);
        var interval = PreviewCloudLightDepthInterval.Create(
            basis,
            profile,
            new Vector3(20f, -3.2f, -40f),
            bounds,
            groundWorldY: -3.2f,
            planetRadius: PreviewStageConstants.CloudPlanetRadius);

        Assert.True(float.IsFinite(interval.Minimum));
        Assert.True(float.IsFinite(interval.Maximum));
        Assert.True(interval.Span > bounds.MaximumAltitude - bounds.MinimumAltitude);
    }

    [Fact]
    public void CascadeBlend_IsContinuousAndFallsBackOutsideFarCoverage()
    {
        var profile = PreviewCloudLightingCacheProfiles.Resolve(
            PreviewVolumetricQuality.Cinematic);
        var basis = PreviewCloudLightBasisBuilder.Build(new Vector3(0f, -1f, 0f));
        var near = PreviewCloudLightCascadeTransform.Create(
            basis,
            profile.Near,
            Vector3.Zero,
            -256f,
            256f);
        var far = PreviewCloudLightCascadeTransform.Create(
            basis,
            profile.Far,
            Vector3.Zero,
            -256f,
            256f);

        var center = near.UnitToWorld(new Vector3(0.5f, 0.5f, 0.5f));
        var overlapStart = near.UnitToWorld(new Vector3(0.90f, 0.5f, 0.5f));
        var overlapMiddle = near.UnitToWorld(new Vector3(0.95f, 0.5f, 0.5f));
        var edge = near.UnitToWorld(new Vector3(1f, 0.5f, 0.5f));
        var outsideFar = far.UnitToWorld(new Vector3(1.1f, 0.5f, 0.5f));

        var centerWeights = PreviewCloudLightCascadeBlend.Select(
            near, far, center, profile.NearOverlapFraction);
        var startWeights = PreviewCloudLightCascadeBlend.Select(
            near, far, overlapStart, profile.NearOverlapFraction);
        var middleWeights = PreviewCloudLightCascadeBlend.Select(
            near, far, overlapMiddle, profile.NearOverlapFraction);
        var edgeWeights = PreviewCloudLightCascadeBlend.Select(
            near, far, edge, profile.NearOverlapFraction);
        var fallbackWeights = PreviewCloudLightCascadeBlend.Select(
            near, far, outsideFar, profile.NearOverlapFraction);

        AssertClose(1f, centerWeights.Near);
        AssertClose(1f, startWeights.Near);
        Assert.InRange(middleWeights.Near, 0f, 1f);
        Assert.InRange(middleWeights.Far, 0f, 1f);
        AssertClose(1f, edgeWeights.Far);
        AssertClose(1f, fallbackWeights.ShortMarch);
        AssertClose(1f, centerWeights.Sum);
        AssertClose(1f, startWeights.Sum);
        AssertClose(1f, middleWeights.Sum);
        AssertClose(1f, edgeWeights.Sum);
        AssertClose(1f, fallbackWeights.Sum);
    }

    [Fact]
    public void Cq36WindReprojection_SamplesTheAdvectedWorldPosition()
    {
        var profile = PreviewCloudLightingCacheProfiles
            .Resolve(PreviewVolumetricQuality.High)
            .Near;
        var basis = PreviewCloudLightBasisBuilder.Build(
            Vector3.Normalize(new Vector3(0.25f, -0.9f, 0.35f)));
        var generated = PreviewCloudLightCascadeTransform.Create(
            basis,
            profile,
            Vector3.Zero,
            -256f,
            256f);
        var windDelta = new Vector3(17f, 0f, -9f);
        var sampling =
            PreviewCloudLightWindReprojection.Apply(generated, windDelta);
        var world = new Vector3(40f, 28f, -12f);

        AssertVectorClose(
            generated.WorldToUnit(world + windDelta),
            sampling.WorldToUnit(world));
    }

    [Fact]
    public void Cq36WrappedWindDelta_UsesTheShortestPeriodicOffset()
    {
        var delta = PreviewCloudLightWindReprojection.WrappedDelta(
            current: new Vector3(2f, 0f, 126f),
            generated: new Vector3(126f, 0f, 2f),
            period: 128f);

        AssertVectorClose(new Vector3(4f, 0f, -4f), delta);
    }

    [Fact]
    public void Cq36ScrollPlan_ReportsWholeTexelReuseAndDepthInvalidation()
    {
        var profile = PreviewCloudLightingCacheProfiles
            .Resolve(PreviewVolumetricQuality.High)
            .Near;
        var basis = PreviewCloudLightBasisBuilder.Build(
            new Vector3(0f, 0f, -1f));
        var first = PreviewCloudLightCascadeTransform.Create(
            basis,
            profile,
            Vector3.Zero,
            -256f,
            256f);
        var moved = first with
        {
            PlaneCenterX =
                first.PlaneCenterX + first.PlaneTexelWorldSize * 2f,
            PlaneCenterY =
                first.PlaneCenterY - first.PlaneTexelWorldSize,
        };
        var planeScroll = PreviewCloudLightScrollPlan.Create(first, moved);
        var depthScroll = PreviewCloudLightScrollPlan.Create(
            first,
            moved with
            {
                LightDepthMin =
                    first.LightDepthMin + first.DepthSliceWorldSize,
            });

        Assert.Equal((2, -1, 0),
            (planeScroll.TexelDeltaX,
                planeScroll.TexelDeltaY,
                planeScroll.SliceDelta));
        Assert.True(planeScroll.CanReusePlaneColumns);
        Assert.InRange(planeScroll.ReusableFraction, 0.97f, 1f);
        Assert.False(depthScroll.CanReusePlaneColumns);
        Assert.Equal(0f, depthScroll.ReusableFraction);
    }

    private static void AssertClose(float expected, float actual, float epsilon = 1e-4f) =>
        Assert.InRange(actual, expected - epsilon, expected + epsilon);

    private static void AssertVectorClose(Vector3 expected, Vector3 actual, float epsilon = 1e-4f)
    {
        AssertClose(expected.X, actual.X, epsilon);
        AssertClose(expected.Y, actual.Y, epsilon);
        AssertClose(expected.Z, actual.Z, epsilon);
    }
}
