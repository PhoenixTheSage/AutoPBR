using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlTerrainOccluderAtlasTests
{
    [Fact]
    public void SlowBakeDiagnostic_ExceedsRebuildDebounce()
    {
        // A slow bake remains single-flight; this threshold is diagnostic and must not be
        // confused with the recenter debounce.
        Assert.True(
            GlTerrainOccluderAtlas.BakeSlowDiagnosticMs > GlTerrainOccluderAtlas.RebuildDebounceMs);
    }

    [Fact]
    public void ResidentValidity_UsesExplicitResidencyInsteadOfSignedHashSentinel()
    {
        Assert.True(GlTerrainOccluderAtlas.EvaluateValidity(
            texture: 7,
            width: 784,
            height: 784,
            hasResidentData: true));
        Assert.False(GlTerrainOccluderAtlas.EvaluateValidity(
            texture: 7,
            width: 784,
            height: 784,
            hasResidentData: false));
    }
}
