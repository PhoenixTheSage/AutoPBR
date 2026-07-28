using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudEdgeRepairClassifierTests
{
    [Fact]
    public void StableFourTapFootprint_DoesNotRepair()
    {
        var result = PreviewCloudEdgeRepairClassifier.Classify(
        [
            Tap(0.50f, true, 120f, 0.5f, 1f),
            Tap(0.54f, true, 120.4f, 0.5f, 1f),
            Tap(0.48f, true, 120.2f, 0.5f, 1f),
            Tap(0.52f, true, 120.1f, 0.5f, 1f),
        ],
        shellIntersects: true);

        Assert.False(result.ShouldRepair);
    }

    [Fact]
    public void OpacityDiscontinuity_Repairs()
    {
        var result = PreviewCloudEdgeRepairClassifier.Classify(
        [
            Tap(0.02f, true, 120f, 0.5f, 1f),
            Tap(0.40f, true, 120f, 0.5f, 1f),
            Tap(0.04f, true, 120f, 0.5f, 1f),
            Tap(0.36f, true, 120f, 0.5f, 1f),
        ],
        shellIntersects: true);

        Assert.True(result.ShouldRepair);
        Assert.True(result.AlphaEdge);
    }

    [Fact]
    public void RepresentativeDistanceDiscontinuity_UsesAbsoluteAndRelativeThreshold()
    {
        var result = PreviewCloudEdgeRepairClassifier.Classify(
        [
            Tap(0.5f, true, 100f, 0.5f, 1f),
            Tap(0.5f, true, 102f, 0.5f, 1f),
            Tap(0.5f, true, 100.2f, 0.5f, 1f),
            Tap(0.5f, true, 101.8f, 0.5f, 1f),
        ],
        shellIntersects: true);

        Assert.True(result.ShouldRepair);
        Assert.True(result.DistanceEdge);
    }

    [Fact]
    public void ValidityAndCloudKindDiscontinuities_Repair()
    {
        var validity = PreviewCloudEdgeRepairClassifier.Classify(
        [
            Tap(0.5f, true, 100f, 0.5f, 1f),
            Tap(0.5f, false, 0f, 0f, 0f),
            Tap(0.5f, true, 100f, 0.5f, 1f),
            Tap(0.5f, true, 100f, 0.5f, 1f),
        ],
        shellIntersects: true);
        var kind = PreviewCloudEdgeRepairClassifier.Classify(
        [
            Tap(0.5f, true, 100f, 0.5f, 1f),
            Tap(0.5f, true, 100f, 1.0f, 1f),
            Tap(0.5f, true, 100f, 0.5f, 1f),
            Tap(0.5f, true, 100f, 1.0f, 1f),
        ],
        shellIntersects: true);

        Assert.True(validity.ValidityEdge);
        Assert.True(kind.KindEdge);
    }

    [Fact]
    public void LowValidWeight_RepairsOnlyWhenDestinationShellIntersects()
    {
        PreviewCloudEdgeRepairClassifier.Tap[] taps =
        [
            Tap(0.5f, true, 100f, 0.5f, 0.4f),
            Tap(0.5f, true, 100f, 0.5f, 0.4f),
            Tap(0.5f, true, 100f, 0.5f, 0.4f),
            Tap(0.5f, true, 100f, 0.5f, 0.4f),
        ];

        var visible = PreviewCloudEdgeRepairClassifier.Classify(taps, shellIntersects: true);
        var occluded = PreviewCloudEdgeRepairClassifier.Classify(taps, shellIntersects: false);

        Assert.True(visible.LowValidWeight);
        Assert.True(visible.ShouldRepair);
        Assert.False(occluded.ShouldRepair);
    }

    [Fact]
    public void EmptySourceFootprint_DoesNotBecomeAFullScreenRepair()
    {
        var result = PreviewCloudEdgeRepairClassifier.Classify(
        [
            Tap(0f, false, 0f, 0f, 0f),
            Tap(0f, false, 0f, 0f, 0f),
            Tap(0f, false, 0f, 0f, 0f),
            Tap(0f, false, 0f, 0f, 0f),
        ],
        shellIntersects: true);

        Assert.False(result.LowValidWeight);
        Assert.False(result.ShouldRepair);
    }

    [Fact]
    public void Contract_UsesExactlyEightRepairSteps()
    {
        Assert.Equal(8, PreviewCloudEdgeRepairClassifier.RepairStepCount);
    }

    [Fact]
    public void Backend_UsesOptionalCinematicRepairWithCq17Fallback()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("TryApplyCloudEdgeRepair(", source, StringComparison.Ordinal);
        Assert.Contains("PreviewVolumetricQuality.Cinematic", source, StringComparison.Ordinal);
        Assert.Contains("GlCloudRenderFormatProfile.DesktopFloatingPoint", source,
            StringComparison.Ordinal);
        Assert.Contains("_cloudCompositeTarget = _cloudRepairTarget", source,
            StringComparison.Ordinal);
        Assert.Contains("DisableCloudEdgeRepair(", source, StringComparison.Ordinal);
        Assert.Contains("continuing with CQ1.7 reconstruction", source, StringComparison.Ordinal);
        Assert.Contains("edgeRepair={_cloudEdgeRepairDiagnostic}", source,
            StringComparison.Ordinal);
        Assert.Contains("source.Profile.UsesDirectMetadata", source, StringComparison.Ordinal);
    }

    private static PreviewCloudEdgeRepairClassifier.Tap Tap(
        float alpha,
        bool valid,
        float distance,
        float kind,
        float weight) =>
        new(alpha, valid, distance, kind, weight);
}
