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

    /// <summary>
    /// Bake resolution multiplier over display scale. Glyphs are rasterized at this factor and
    /// drawn at 1× display size so LINEAR filtering yields clean HUD text.
    /// </summary>
    public const int BakeOversample = 2;

    private static readonly FontFamily OverlayFontFamily =
        new("Cascadia Mono, Consolas, Courier New, monospace");

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

    /// <summary>Display-space glyph advance / quad width in viewport pixels.</summary>
    public int CellWidth { get; }

    /// <summary>Display-space glyph quad height in viewport pixels.</summary>
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
    /// Bake monospace HUD glyphs into an RGBA atlas. Must run on the Avalonia UI thread.
    /// Rasterizes in pixel space at 96 DPI (avoids RenderTargetBitmap DIP/DPI footguns) at
    /// <see cref="BakeOversample"/>× display resolution for clean downsampled text.
    /// </summary>
    public static GlOverlayFontAtlas BakeConsolas(double fontSizeLogical, double renderScale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fontSizeLogical, 0.0);
        if (renderScale <= 0.0)
        {
            renderScale = 1.0;
        }

        var bakeScale = renderScale * BakeOversample;
        var fontPx = fontSizeLogical * bakeScale;
        var typeface = new Typeface(OverlayFontFamily);
        var measure = new FormattedText(
            "M",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontPx,
            Brushes.White);

        // Pixel-space cell (1 DIP = 1 px at 96 DPI). Small inset keeps LINEAR samples inside ink.
        var glyphW = Math.Max(1, (int)Math.Ceiling(Math.Max(measure.Width, measure.LineHeight * 0.55)));
        var glyphH = Math.Max(1, (int)Math.Ceiling(Math.Max(measure.Height, measure.LineHeight)));
        const int inset = 1;
        var cellW = glyphW + inset * 2;
        var cellH = glyphH + inset * 2;
        // Empty gutter between packed cells so LINEAR filtering does not bleed neighbors.
        const int gutter = 2;
        var strideW = cellW + gutter;
        var strideH = cellH + gutter;

        var displayAdvance = Math.Max(1, (int)Math.Round(glyphW / (double)BakeOversample));
        var displayLineH = Math.Max(1, (int)Math.Round(glyphH / (double)BakeOversample));

        const int cols = 16;
        var rows = (GlyphCount + 1 + cols - 1) / cols;
        var atlasW = cols * strideW;
        var atlasH = rows * strideH;
        var bgra = new byte[atlasW * atlasH * 4];
        var glyphs = new GlOverlayGlyphInfo[GlyphCount];

        for (var i = 0; i < GlyphCount; i++)
        {
            var ch = (char)(FirstChar + i);
            var col = i % cols;
            var row = i / cols;
            var px = col * strideW;
            var py = row * strideH;
            BlitGlyphToAtlas(
                ch,
                fontPx,
                cellW,
                cellH,
                inset,
                bgra,
                atlasW,
                px,
                py);

            // Half-texel inset keeps LINEAR taps inside this glyph's cell.
            var u0 = (px + 0.5f) / atlasW;
            var v0 = (py + 0.5f) / atlasH;
            var u1 = (px + cellW - 0.5f) / atlasW;
            var v1 = (py + cellH - 0.5f) / atlasH;
            glyphs[i] = new GlOverlayGlyphInfo(u0, v0, u1, v1, displayAdvance);
        }

        var whiteIndex = GlyphCount;
        var whiteCol = whiteIndex % cols;
        var whiteRow = whiteIndex / cols;
        var whitePx = whiteCol * strideW;
        var whitePy = whiteRow * strideH;
        FillWhiteCell(bgra, atlasW, whitePx, whitePy, cellW, cellH);

        // Sample the solid interior of the white cell (avoid gutter / edge).
        var whiteU0 = (whitePx + inset + 0.5f) / atlasW;
        var whiteV0 = (whitePy + inset + 0.5f) / atlasH;
        var whiteU1 = (whitePx + cellW - inset - 0.5f) / atlasW;
        var whiteV1 = (whitePy + cellH - inset - 0.5f) / atlasH;

        return new GlOverlayFontAtlas(
            atlasW,
            atlasH,
            displayAdvance,
            displayLineH,
            displayLineH,
            fontSizeLogical,
            renderScale,
            bgra,
            glyphs,
            whiteU0,
            whiteV0,
            whiteU1,
            whiteV1);
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

    private static void BlitGlyphToAtlas(
        char ch,
        double fontPx,
        int cellW,
        int cellH,
        int inset,
        byte[] atlas,
        int atlasW,
        int destX,
        int destY)
    {
        var visual = new TextBlock
        {
            Text = ch.ToString(),
            FontFamily = OverlayFontFamily,
            FontSize = fontPx,
            Foreground = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        RenderOptions.SetTextRenderingMode(visual, TextRenderingMode.Antialias);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Antialias);

        visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = visual.DesiredSize;
        // Pixel-space RTB: DPI 96 so FontSize maps 1:1 to texels (no DIP rescale ambiguity).
        visual.Arrange(new Rect(inset, inset, Math.Max(desired.Width, 1), Math.Max(desired.Height, 1)));
        using var bitmap = new RenderTargetBitmap(new PixelSize(cellW, cellH), new Vector(96.0, 96.0));
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
