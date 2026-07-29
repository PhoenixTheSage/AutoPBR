using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlOverlayTextLayoutTests
{
    [Fact]
    public void Build_EmitsPanelAndGlyphQuadsForMultiLineText()
    {
        var atlas = GlOverlayFontAtlas.CreateProcedural(cellWidth: 8, cellHeight: 12);
        var verts = GlOverlayTextLayout.Build(
            atlas,
            debugText: null,
            fpsText: "60 FPS\nGPU 1.0 ms",
            cpuText: "CPU 2.0 ms",
            viewportWidth: 800,
            viewportHeight: 600,
            marginPixels: 8,
            out var vertexCount);

        Assert.True(vertexCount > 0);
        Assert.Equal(0, vertexCount % GlOverlayTextLayout.VerticesPerQuad);
        Assert.Equal(vertexCount * GlOverlayTextLayout.FloatsPerVertex, verts.Length);

        // 1 panel + strlen without newlines for fps ("60 FPS" = 6, "GPU 1.0 ms" = 10) + cpu panel + chars
        var fpsGlyphs = 6 + 10;
        var cpuGlyphs = "CPU 2.0 ms".Length;
        var expectedQuads = 1 + fpsGlyphs + 1 + cpuGlyphs;
        Assert.Equal(expectedQuads * GlOverlayTextLayout.VerticesPerQuad, vertexCount);
    }

    [Fact]
    public void Measure_CountsLinesAndPadding()
    {
        var atlas = GlOverlayFontAtlas.CreateProcedural(cellWidth: 8, cellHeight: 12);
        var size = GlOverlayTextLayout.Measure(atlas, "ab\ncd", padX: 6, padY: 3);
        Assert.Equal(2 * 8 + 12, size.Width);
        Assert.Equal(2 * 12 + 6, size.Height);
    }

    [Fact]
    public void ProceduralAtlas_ExposesAsciiGlyphsAndWhiteCell()
    {
        var atlas = GlOverlayFontAtlas.CreateProcedural();
        Assert.True(atlas.TryGetGlyph('A', out var a));
        Assert.True(a.AdvancePx > 0);
        Assert.True(atlas.WhiteU1 > atlas.WhiteU0);
        Assert.Equal(GlOverlayFontAtlas.GlyphCount, '~' - ' ' + 1);
    }
}
