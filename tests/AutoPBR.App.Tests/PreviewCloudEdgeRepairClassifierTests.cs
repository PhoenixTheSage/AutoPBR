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
    public void InteriorMaterialVariation_DoesNotRepair()
    {
        // CA1/CA2 breakup routinely produces ~0.10-0.20 opacity swings across the
        // 2/3-res tap footprint inside occupied cloud. Retracing those pixels made
        // Cinematic noisier than High.
        var mild = PreviewCloudEdgeRepairClassifier.Classify(
        [
            Tap(0.52f, true, 120f, 0.5f, 1f),
            Tap(0.68f, true, 120.2f, 0.5f, 1f),
            Tap(0.55f, true, 120.1f, 0.5f, 1f),
            Tap(0.71f, true, 119.9f, 0.5f, 1f),
        ],
        shellIntersects: true);
        var aboveLegacyThreshold = PreviewCloudEdgeRepairClassifier.Classify(
        [
            Tap(0.48f, true, 140f, 0.5f, 1f),
            Tap(0.66f, true, 140.1f, 0.5f, 1f),
            Tap(0.50f, true, 140.0f, 0.5f, 1f),
            Tap(0.64f, true, 139.9f, 0.5f, 1f),
        ],
        shellIntersects: true);

        Assert.False(mild.ShouldRepair);
        Assert.False(mild.AlphaEdge);
        Assert.False(aboveLegacyThreshold.ShouldRepair);
        Assert.False(aboveLegacyThreshold.AlphaEdge);
        Assert.True(
            PreviewCloudEdgeRepairClassifier.AlphaRangeThreshold >
            PreviewCloudEdgeRepairClassifier.SilhouetteAlphaCeiling);
    }

    [Fact]
    public void StrongInteriorOpacityJump_StillRepairs()
    {
        var result = PreviewCloudEdgeRepairClassifier.Classify(
        [
            Tap(0.30f, true, 160f, 0.5f, 1f),
            Tap(0.78f, true, 160f, 0.5f, 1f),
            Tap(0.32f, true, 160f, 0.5f, 1f),
            Tap(0.80f, true, 160f, 0.5f, 1f),
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
    public void IdleStability_RampsRetraceBlend()
    {
        Assert.Equal(1f, PreviewCloudEdgeRepairClassifier.EvaluateRetraceBlend(0f));
        Assert.Equal(1f, PreviewCloudEdgeRepairClassifier.EvaluateRetraceBlend(0.20f));
        Assert.Equal(0f, PreviewCloudEdgeRepairClassifier.EvaluateRetraceBlend(0.85f));
        Assert.Equal(0f, PreviewCloudEdgeRepairClassifier.EvaluateRetraceBlend(1f));
        var mid = PreviewCloudEdgeRepairClassifier.EvaluateRetraceBlend(0.50f);
        Assert.InRange(mid, 0.15f, 0.85f);
    }

    [Fact]
    public void FormatDiagnostic_ReportsIdleFreezeGate()
    {
        Assert.Equal(
            "ca3.2-repair(alphaThr=0.24,sil=0.18,jump=0.36,steps=8,idleFreeze>=0.85,retraceRamp=0.2..0.85)",
            PreviewCloudEdgeRepairClassifier.FormatDiagnostic());
    }

    [Fact]
    public void Backend_UsesOptionalCinematicRepairWithCq17Fallback()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "AutoPBR.App", "Rendering", "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));
        var repair = File.ReadAllText(Path.Combine(
            root, "src", "AutoPBR.App", "Rendering", "Shaders",
            "genesis_clouds_repair.frag"));

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
        Assert.Contains("ru.RepairStability", source, StringComparison.Ordinal);
        Assert.Contains("UpdateIdleLatch", source, StringComparison.Ordinal);
        Assert.Contains("uniform float uRepairStability", repair, StringComparison.Ordinal);
        Assert.Contains("CLOUD_REPAIR_IDLE_FREEZE = 0.85", repair, StringComparison.Ordinal);
        Assert.Contains("CLOUD_REPAIR_RETRACE_RAMP_START = 0.20", repair, StringComparison.Ordinal);
        Assert.Contains("CLOUD_REPAIR_JITTER_FREEZE = 0.35", repair, StringComparison.Ordinal);
        Assert.Contains("repairConfidence *= retraceBlend", repair, StringComparison.Ordinal);
        Assert.Contains("EaseStability", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        foreach (var start in (string?[])
                 [Path.GetDirectoryName(sourceFilePath), AppContext.BaseDirectory, Directory.GetCurrentDirectory()])
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AutoPBR.sln")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate AutoPBR.sln from test context.");
    }

    private static PreviewCloudEdgeRepairClassifier.Tap Tap(
        float alpha,
        bool valid,
        float distance,
        float kind,
        float weight) =>
        new(alpha, valid, distance, kind, weight);
}
