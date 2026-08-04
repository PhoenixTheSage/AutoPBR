using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudTemporalLowAlphaWeightTests
{
    [Fact]
    public void DenseOpaqueSamples_KeepFullHistoryWeight()
    {
        Assert.Equal(1f, PreviewCloudTemporalLowAlphaWeight.Evaluate(0.80f, 0.82f));
        Assert.Equal(1f, PreviewCloudTemporalLowAlphaWeight.Evaluate(1f, 1f, stability: 1f));
    }

    [Fact]
    public void ThinAgreeingWisps_KeepSubstantialHistory_WhenMoving()
    {
        // Pans across stable soft edges must not dump into raw 2/3-res borders.
        var thin = PreviewCloudTemporalLowAlphaWeight.Evaluate(0.12f, 0.14f, stability: 0f);
        Assert.True(thin > 0.75f);
        Assert.True(thin < 0.95f);
    }

    [Fact]
    public void ThinAgreeingWisps_KeepHighHistoryWeight_WhenStatic()
    {
        var thin = PreviewCloudTemporalLowAlphaWeight.Evaluate(0.12f, 0.14f, stability: 1f);
        Assert.True(thin > 0.90f);
        Assert.True(thin > PreviewCloudTemporalLowAlphaWeight.Evaluate(0.12f, 0.14f, stability: 0f));
    }

    [Fact]
    public void ThinDisagreement_ApproachesMinimumWeight_WhenMoving()
    {
        var weight = PreviewCloudTemporalLowAlphaWeight.Evaluate(0.05f, 0.35f, stability: 0f);
        Assert.InRange(
            weight,
            PreviewCloudTemporalLowAlphaWeight.MinimumWeight - 1e-5f,
            PreviewCloudTemporalLowAlphaWeight.MinimumWeight + 0.08f);
    }

    [Fact]
    public void ThinDisagreement_UsesStaticFloor_WhenIdle()
    {
        var weight = PreviewCloudTemporalLowAlphaWeight.Evaluate(0.05f, 0.35f, stability: 1f);
        Assert.InRange(
            weight,
            PreviewCloudTemporalLowAlphaWeight.StaticMinimumWeight - 1e-5f,
            PreviewCloudTemporalLowAlphaWeight.StaticMinimumWeight + 0.08f);
    }

    [Fact]
    public void EvaluateStability_IdleCameraWithFullConfidence_SnapsToOne()
    {
        var stability = PreviewCloudTemporalLowAlphaWeight.EvaluateStability(
            cameraDeltaWorld: 0f,
            windDeltaLength: 0.15f,
            historyConfidence: 1f);
        Assert.Equal(1f, stability);
    }

    [Fact]
    public void EvaluateStability_MovingCamera_Drops()
    {
        var stability = PreviewCloudTemporalLowAlphaWeight.EvaluateStability(
            cameraDeltaWorld: 0.25f,
            windDeltaLength: 0f,
            historyConfidence: 1f);
        Assert.True(stability < 0.15f);
    }

    [Fact]
    public void IdleLatch_HasEnterExitHysteresis()
    {
        Assert.False(PreviewCloudTemporalLowAlphaWeight.UpdateIdleLatch(
            currentlyLatched: false,
            cameraDeltaWorld: 0.03f,
            historyConfidence: 1f));
        Assert.True(PreviewCloudTemporalLowAlphaWeight.UpdateIdleLatch(
            currentlyLatched: false,
            cameraDeltaWorld: 0.01f,
            historyConfidence: 1f));
        Assert.True(PreviewCloudTemporalLowAlphaWeight.UpdateIdleLatch(
            currentlyLatched: true,
            cameraDeltaWorld: 0.05f,
            historyConfidence: 1f));
        Assert.False(PreviewCloudTemporalLowAlphaWeight.UpdateIdleLatch(
            currentlyLatched: true,
            cameraDeltaWorld: 0.08f,
            historyConfidence: 1f));
    }

    [Fact]
    public void EaseStability_DecaysWhenLeavingIdle()
    {
        var eased = PreviewCloudTemporalLowAlphaWeight.EaseStability(1f, 0f);
        Assert.InRange(
            eased,
            1f - PreviewCloudTemporalLowAlphaWeight.StabilityExitStep - 1e-5f,
            1f - PreviewCloudTemporalLowAlphaWeight.StabilityExitStep + 1e-5f);
        Assert.Equal(1f, PreviewCloudTemporalLowAlphaWeight.EaseStability(0.5f, 1f));
    }

    [Fact]
    public void FormatDiagnostic_ReportsCa31PolicyToken()
    {
        Assert.Equal(
            "ca3.1-low-alpha(0.28..0.55->0.58..0.86@stability)",
            PreviewCloudTemporalLowAlphaWeight.FormatDiagnostic());
    }

    [Fact]
    public void BackendSource_TreatsMomentHistoryCopyFailureAsNonFatal()
    {
        var root = FindRepoRoot();
        var targetSource = File.ReadAllText(Path.Combine(
            root, "src", "AutoPBR.App", "Rendering", "OpenGL", "GlCloudTemporalRenderTarget.cs"));
        var backendSource = File.ReadAllText(Path.Combine(
            root, "src", "AutoPBR.App", "Rendering", "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));
        var uniformSource = File.ReadAllText(Path.Combine(
            root, "src", "AutoPBR.App", "Rendering", "OpenGL",
            "OpenGlPreviewBackend.PostUniformLocs.cs"));

        Assert.Contains("attachment < 2", targetSource, StringComparison.Ordinal);
        Assert.Contains(
            "historyConfidence=live-via-overlay",
            backendSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryGetCloudTemporalHistoryDebug",
            backendSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PreviewCloudTemporalLowAlphaWeight.EaseStability",
            backendSource,
            StringComparison.Ordinal);
        Assert.Contains("uTemporalStability", uniformSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_MatchesShaderConstants()
    {
        var temporal = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "AutoPBR.App", "Rendering", "Shaders",
            "genesis_clouds_temporal.frag"));

        Assert.Contains("uniform float uTemporalStability", temporal, StringComparison.Ordinal);
        Assert.Contains("smoothstep(0.28, 0.55, minAlpha)", temporal, StringComparison.Ordinal);
        Assert.Contains("smoothstep(0.06, 0.28, abs(current.a - history.a))", temporal,
            StringComparison.Ordinal);
        Assert.Contains("mix(0.40, 1.0, alphaDisagreement)", temporal, StringComparison.Ordinal);
        Assert.Contains("mix(0.58, 0.86, stability)", temporal, StringComparison.Ordinal);
        Assert.Contains("trMotionRejectionWeight(velocity, 0.040, 0.32)", temporal,
            StringComparison.Ordinal);
        Assert.Contains("mix(1.0, lowAlphaFloor, clamp(lowAlphaReactive, 0.0, 1.0))", temporal,
            StringComparison.Ordinal);
        Assert.Contains("lowAlphaWeight * confidenceWeight", temporal, StringComparison.Ordinal);
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
}
