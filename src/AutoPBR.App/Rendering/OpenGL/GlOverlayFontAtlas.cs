using System.Globalization;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AutoPBR.App.Rendering.OpenGL;

internal readonly record struct GlOverlayGlyphInfo(
    float U0,
    float V0,
    float U1,
    float V1,
    float AdvancePx);

/// <summary>
/// CPU-side monospace glyph atlas for native WGL HUD text (ImGui-style).
/// Baked on the UI thread via Avalonia; GL only uploads and samples it.
/// </summary>
internal sealed class GlOverlayFontAtlas
{
    public const char FirstChar = ' ';
    public const char LastChar = '~';
    public const int GlyphCount = LastChar - FirstChar + 1;

    /// <summary>FPS / CPU panel font size in DIPs (matches prior Avalonia HUD).</summary>
    public const double DefaultFontSizeLogical = 12.0;

    /// <summary>Debug panel font size in DIPs.</summary>
    public const double DebugFontSizeLogical = 11.0;

    private readonly GlOverlayGlyphInfo[] _glyphs;

    private GlOverlayFontAtlas(
        int width,
        int height,
        int cellWidth,
        int cellHeight,
        float lineHeight,
        double fontSizeLogical,
        double renderScale,
        byte[] bgra,
        GlOverlayGlyphInfo[] glyphs,
        float whiteU0,
        float whiteV0,
        float whiteU1,
        float whiteV1)
    {
        Width = width;
        Height = height;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        LineHeight = lineHeight;
        FontSizeLogical = fontSizeLogical;
        RenderScale = renderScale;
        BgraPremultiplied = bgra;
        _glyphs = glyphs;
        WhiteU0 = whiteU0;
        WhiteV0 = whiteV0;
        WhiteU1 = whiteU1;
        WhiteV1 = whiteV1;
    }

    public int Width { get; }
    public int Height { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    public float LineHeight { get; }
    public double FontSizeLogical { get; }
    public double RenderScale { get; }

    /// <summary>Top-row-first premultiplied BGRA8 (Avalonia CopyPixels layout).</summary>
    public byte[] BgraPremultiplied { get; }

    public float WhiteU0 { get; }
    public float WhiteV0 { get; }
    public float WhiteU1 { get; }
    public float WhiteV1 { get; }

    public bool TryGetGlyph(char c, out GlOverlayGlyphInfo glyph)
    {
        if (c < FirstChar || c > LastChar)
        {
            glyph = default;
            return false;
        }

        glyph = _glyphs[c - FirstChar];
        return true;
    }

    public GlOverlayGlyphInfo GetGlyphOrReplacement(char c)
    {
        if (TryGetGlyph(c, out var g))
        {
            return g;
        }

        // Missing / non-ASCII → '?' if available, else space.
        if (TryGetGlyph('?', out g))
        {
            return g;
        }

        return _glyphs[0];
    }

    /// <summary>
    /// Bake Consolas into an RGBA atlas. Must run on the Avalonia UI thread.
    /// </summary>
    public static GlOverlayFontAtlas BakeConsolas(double fontSizeLogical, double renderScale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fontSizeLogical, 0.0);
        if (renderScale <= 0.0)
        {
            renderScale = 1.0;
        }

        var typeface = new Typeface(new FontFamily("Consolas"));
        var measure = new FormattedText(
            "M",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSizeLogical,
            Brushes.White);
        var cellW = Math.Max(1, (int)Math.Ceiling(Math.Max(measure.Width, measure.LineHeight * 0.55) * renderScale) + 2);
        var cellH = Math.Max(1, (int)Math.Ceiling(measure.Height * renderScale) + 2);
        var lineHeight = (float)(cellH);

        // Glyphs + one solid white cell for panel fills.
        const int cols = 16;
        var rows = (GlyphCount + 1 + cols - 1) / cols;
        var atlasW = cols * cellW;
        var atlasH = rows * cellH;
        var bgra = new byte[atlasW * atlasH * 4];
        var glyphs = new GlOverlayGlyphInfo[GlyphCount];

        var dpi = 96.0 * renderScale;
        for (var i = 0; i < GlyphCount; i++)
        {
            var ch = (char)(FirstChar + i);
            var col = i % cols;
            var row = i / cols;
            var px = col * cellW;
            var py = row * cellH;
            BlitControlToAtlas(
                CreateGlyphVisual(ch.ToString(), fontSizeLogical),
                dpi,
                cellW,
                cellH,
                bgra,
                atlasW,
                px,
                py);

            var u0 = px / (float)atlasW;
            var v0 = py / (float)atlasH;
            var u1 = (px + cellW) / (float)atlasW;
            var v1 = (py + cellH) / (float)atlasH;
            glyphs[i] = new GlOverlayGlyphInfo(u0, v0, u1, v1, cellW);
        }

        var whiteIndex = GlyphCount;
        var whiteCol = whiteIndex % cols;
        var whiteRow = whiteIndex / cols;
        var whitePx = whiteCol * cellW;
        var whitePy = whiteRow * cellH;
        FillWhiteCell(bgra, atlasW, whitePx, whitePy, cellW, cellH);

        return new GlOverlayFontAtlas(
            atlasW,
            atlasH,
            cellW,
            cellH,
            lineHeight,
            fontSizeLogical,
            renderScale,
            bgra,
            glyphs,
            whitePx / (float)atlasW,
            whitePy / (float)atlasH,
            (whitePx + cellW) / (float)atlasW,
            (whitePy + cellH) / (float)atlasH);
    }

    /// <summary>Filled-cell atlas for unit/smoke tests (no Avalonia text stack required).</summary>
    public static GlOverlayFontAtlas CreateProcedural(int cellWidth = 8, int cellHeight = 12)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cellWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cellHeight, 0);

        const int cols = 16;
        var rows = (GlyphCount + 1 + cols - 1) / cols;
        var atlasW = cols * cellWidth;
        var atlasH = rows * cellHeight;
        var bgra = new byte[atlasW * atlasH * 4];
        var glyphs = new GlOverlayGlyphInfo[GlyphCount];

        for (var i = 0; i < GlyphCount; i++)
        {
            var col = i % cols;
            var row = i / cols;
            var px = col * cellWidth;
            var py = row * cellHeight;
            if (i > 0)
            {
                // Inset filled rect so glyphs are distinguishable from the white cell.
                FillRectBgra(bgra, atlasW, px + 1, py + 1, cellWidth - 2, cellHeight - 2, 255, 255, 255, 255);
            }

            glyphs[i] = new GlOverlayGlyphInfo(
                px / (float)atlasW,
                py / (float)atlasH,
                (px + cellWidth) / (float)atlasW,
                (py + cellHeight) / (float)atlasH,
                cellWidth);
        }

        var whiteIndex = GlyphCount;
        var whiteCol = whiteIndex % cols;
        var whiteRow = whiteIndex / cols;
        var whitePx = whiteCol * cellWidth;
        var whitePy = whiteRow * cellHeight;
        FillWhiteCell(bgra, atlasW, whitePx, whitePy, cellWidth, cellHeight);

        return new GlOverlayFontAtlas(
            atlasW,
            atlasH,
            cellWidth,
            cellHeight,
            cellHeight,
            DefaultFontSizeLogical,
            1.0,
            bgra,
            glyphs,
            whitePx / (float)atlasW,
            whitePy / (float)atlasH,
            (whitePx + cellWidth) / (float)atlasW,
            (whitePy + cellHeight) / (float)atlasH);
    }

    private static TextBlock CreateGlyphVisual(string text, double fontSizeLogical) =>
        new()
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = fontSizeLogical,
            Foreground = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };

    private static void BlitControlToAtlas(
        Control visual,
        double dpi,
        int cellW,
        int cellH,
        byte[] atlas,
        int atlasW,
        int destX,
        int destY)
    {
        visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        visual.Arrange(new Rect(visual.DesiredSize));
        using var bitmap = new RenderTargetBitmap(new PixelSize(cellW, cellH), new Vector(dpi, dpi));
        bitmap.Render(visual);
        var pixels = new byte[cellW * cellH * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, cellW, cellH), handle.AddrOfPinnedObject(), pixels.Length, cellW * 4);
        }
        finally
        {
            handle.Free();
        }

        for (var y = 0; y < cellH; y++)
        {
            var srcRow = y * cellW * 4;
            var dstOff = ((destY + y) * atlasW + destX) * 4;
            Buffer.BlockCopy(pixels, srcRow, atlas, dstOff, cellW * 4);
        }
    }

    private static void FillWhiteCell(byte[] bgra, int atlasW, int x, int y, int w, int h) =>
        FillRectBgra(bgra, atlasW, x, y, w, h, 255, 255, 255, 255);

    private static void FillRectBgra(byte[] bgra, int atlasW, int x, int y, int w, int h, byte r, byte g, byte b, byte a)
    {
        for (var row = 0; row < h; row++)
        {
            var yy = y + row;
            for (var col = 0; col < w; col++)
            {
                var i = ((yy * atlasW) + (x + col)) * 4;
                bgra[i] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
                bgra[i + 3] = a;
            }
        }
    }
}
