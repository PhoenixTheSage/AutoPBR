namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Builds ImGui-style overlay vertex data (panel + glyph quads) from HUD strings.
/// Intended to run on the GL Overlay pass when published strings/scale are dirty.
/// </summary>
internal static class GlOverlayTextLayout
{
    /// <summary>x, y, u, v, r, g, b, a — 8 floats per vertex; 6 verts per quad.</summary>
    public const int FloatsPerVertex = 8;

    public const int VerticesPerQuad = 6;

    // Match prior Avalonia HUD chrome / text colors.
    private static readonly Rgba PanelColor = new(0f, 0f, 0f, 0x66 / 255f);
    private static readonly Rgba TextColor = new(1f, 1f, 1f, 0xF0 / 255f);
    private static readonly Rgba DebugTextColor = new(1f, 1f, 1f, 0xE8 / 255f);

    private readonly record struct Rgba(float R, float G, float B, float A);

    public static float[] Build(
        GlOverlayFontAtlas atlas,
        string? debugText,
        string? fpsText,
        string? cpuText,
        int viewportWidth,
        int viewportHeight,
        int marginPixels,
        out int vertexCount)
    {
        vertexCount = 0;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return Array.Empty<float>();
        }

        marginPixels = Math.Max(0, marginPixels);
        var padX = Math.Max(1, (int)Math.Round(6.0 * atlas.RenderScale));
        var padY = Math.Max(1, (int)Math.Round(3.0 * atlas.RenderScale));
        var gap = Math.Max(4, marginPixels / 2);

        // Worst-case capacity: three panels of long expanded HUD text.
        var capacityQuads = EstimateQuadCapacity(debugText) + EstimateQuadCapacity(fpsText) + EstimateQuadCapacity(cpuText);
        capacityQuads = Math.Max(capacityQuads, 8);
        var verts = new float[capacityQuads * VerticesPerQuad * FloatsPerVertex];
        var write = 0;

        if (!string.IsNullOrWhiteSpace(debugText))
        {
            AppendPanel(
                ref verts,
                ref write,
                atlas,
                debugText,
                marginPixels,
                marginPixels,
                padX,
                padY,
                viewportWidth,
                viewportHeight,
                PanelColor,
                DebugTextColor);
        }

        var rightStackY = marginPixels;
        if (!string.IsNullOrWhiteSpace(fpsText))
        {
            var size = Measure(atlas, fpsText, padX, padY);
            var x = Math.Max(marginPixels, viewportWidth - size.Width - marginPixels);
            AppendPanel(
                ref verts,
                ref write,
                atlas,
                fpsText,
                x,
                rightStackY,
                padX,
                padY,
                viewportWidth,
                viewportHeight,
                PanelColor,
                TextColor);
            rightStackY += size.Height + gap;
        }

        if (!string.IsNullOrWhiteSpace(cpuText))
        {
            var size = Measure(atlas, cpuText, padX, padY);
            var x = Math.Max(marginPixels, viewportWidth - size.Width - marginPixels);
            AppendPanel(
                ref verts,
                ref write,
                atlas,
                cpuText,
                x,
                rightStackY,
                padX,
                padY,
                viewportWidth,
                viewportHeight,
                PanelColor,
                TextColor);
        }

        vertexCount = write / FloatsPerVertex;
        if (write == verts.Length)
        {
            return verts;
        }

        var trimmed = new float[write];
        Array.Copy(verts, trimmed, write);
        return trimmed;
    }

    public static (int Width, int Height) Measure(
        GlOverlayFontAtlas atlas,
        string text,
        int padX,
        int padY)
    {
        var (lines, maxCols) = CountLines(text);
        var width = maxCols * atlas.CellWidth + padX * 2;
        var height = lines * atlas.CellHeight + padY * 2;
        return (Math.Max(1, width), Math.Max(1, height));
    }

    private static int EstimateQuadCapacity(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        // 1 panel + 1 quad per character (newlines excluded at emit time).
        return 1 + text.Length;
    }

    private static (int Lines, int MaxCols) CountLines(string text)
    {
        var lines = 1;
        var maxCols = 0;
        var cols = 0;
        foreach (var ch in text)
        {
            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                maxCols = Math.Max(maxCols, cols);
                cols = 0;
                lines++;
                continue;
            }

            cols++;
        }

        maxCols = Math.Max(maxCols, cols);
        return (lines, maxCols);
    }

    private static void AppendPanel(
        ref float[] verts,
        ref int write,
        GlOverlayFontAtlas atlas,
        string text,
        int x,
        int y,
        int padX,
        int padY,
        int viewportWidth,
        int viewportHeight,
        Rgba panelColor,
        Rgba textColor)
    {
        var size = Measure(atlas, text, padX, padY);
        AppendQuad(
            ref verts,
            ref write,
            x,
            y,
            size.Width,
            size.Height,
            atlas.WhiteU0,
            atlas.WhiteV0,
            atlas.WhiteU1,
            atlas.WhiteV1,
            panelColor,
            viewportWidth,
            viewportHeight);

        var penX = x + padX;
        var penY = y + padY;
        var lineX = penX;
        foreach (var ch in text)
        {
            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                penY += atlas.CellHeight;
                lineX = penX;
                continue;
            }

            var glyph = atlas.GetGlyphOrReplacement(ch);
            AppendQuad(
                ref verts,
                ref write,
                lineX,
                penY,
                atlas.CellWidth,
                atlas.CellHeight,
                glyph.U0,
                glyph.V0,
                glyph.U1,
                glyph.V1,
                textColor,
                viewportWidth,
                viewportHeight);
            lineX += (int)MathF.Round(glyph.AdvancePx);
        }
    }

    private static void AppendQuad(
        ref float[] verts,
        ref int write,
        int x,
        int y,
        int width,
        int height,
        float u0,
        float v0,
        float u1,
        float v1,
        Rgba color,
        int viewportWidth,
        int viewportHeight)
    {
        EnsureCapacity(ref verts, write, VerticesPerQuad * FloatsPerVertex);
        var x0 = PixelToNdcX(x, viewportWidth);
        var x1 = PixelToNdcX(x + width, viewportWidth);
        var y0 = PixelToNdcY(y, viewportHeight);
        var y1 = PixelToNdcY(y + height, viewportHeight);
        WriteVertex(verts, ref write, x0, y0, u0, v0, color);
        WriteVertex(verts, ref write, x1, y0, u1, v0, color);
        WriteVertex(verts, ref write, x1, y1, u1, v1, color);
        WriteVertex(verts, ref write, x0, y0, u0, v0, color);
        WriteVertex(verts, ref write, x1, y1, u1, v1, color);
        WriteVertex(verts, ref write, x0, y1, u0, v1, color);
    }

    private static void EnsureCapacity(ref float[] verts, int write, int needed)
    {
        if (write + needed <= verts.Length)
        {
            return;
        }

        var grown = new float[Math.Max(verts.Length * 2, write + needed)];
        Array.Copy(verts, grown, write);
        verts = grown;
    }

    private static void WriteVertex(float[] verts, ref int write, float x, float y, float u, float v, Rgba c)
    {
        verts[write++] = x;
        verts[write++] = y;
        verts[write++] = u;
        verts[write++] = v;
        verts[write++] = c.R;
        verts[write++] = c.G;
        verts[write++] = c.B;
        verts[write++] = c.A;
    }

    private static float PixelToNdcX(int x, int width) => (x / (float)Math.Max(1, width)) * 2f - 1f;

    private static float PixelToNdcY(int y, int height) => 1f - (y / (float)Math.Max(1, height)) * 2f;
}
