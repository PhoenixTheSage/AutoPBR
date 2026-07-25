using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlTerrainOccluderAtlasTests
{
    [Fact]
    public void BakeStuckTimeout_ExceedsRebuildDebounce()
    {
        // Startup/reload must be able to retry a latched bake after debounce would otherwise
        // suppress rebuilds once an atlas is valid.
        Assert.True(
            GlTerrainOccluderAtlas.BakeStuckTimeoutMs > GlTerrainOccluderAtlas.RebuildDebounceMs);
    }
}
