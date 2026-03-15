// Generates multi-resolution .ico from a PNG (256, 48, 32, 16; 32bpp with alpha).
// Scale-to-cover, center crop so each frame is a proper square.

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AutoPBR.Tools.GenerateIcon;

/// <summary>
/// Generates a multi-resolution ICO file from a PNG with 32-bit color and transparency.
/// </summary>
public static class MultiResolutionIcoGenerator
{
    /// <summary>Output sizes: 256, 48, 32, 16 pixels.</summary>
    public static readonly int[] DefaultSizes = { 256, 48, 32, 16 };

    /// <summary>
    /// Generates an ICO file at <paramref name="icoPath"/> from the PNG at <paramref name="pngPath"/>.
    /// Uses scale-to-cover so each frame fills the square (center crop). Throws on I/O or image errors.
    /// </summary>
    public static void Generate(string pngPath, string icoPath, int[]? sizes = null)
    {
        sizes ??= DefaultSizes;
        pngPath = Path.GetFullPath(pngPath);
        icoPath = Path.GetFullPath(icoPath);

        if (!File.Exists(pngPath))
            throw new FileNotFoundException("PNG not found.", pngPath);

        using var source = Image.FromFile(pngPath);
        int srcW = source.Width;
        int srcH = source.Height;
        var dibs = new List<byte[]>();

        foreach (int size in sizes)
        {
            using var bmp = RenderFrame(source, srcW, srcH, size);
            dibs.Add(BitmapToDib32BppArgb(bmp));
        }

        IcoWriter.Write(icoPath, sizes, dibs);
    }

    internal static Bitmap RenderFrame(Image source, int srcW, int srcH, int size)
    {
        double scale = Math.Max((double)size / srcW, (double)size / srcH);
        int drawW = (int)Math.Round(srcW * scale);
        int drawH = (int)Math.Round(srcH * scale);
        int x = (size - drawW) / 2;
        int y = (size - drawH) / 2;

        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(source, x, y, drawW, drawH);
        }
        return bmp;
    }

    internal static byte[] BitmapToDib32BppArgb(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        int rowBytes = w * 4;
        int imageBytes = rowBytes * h;
        int totalSize = 40 + imageBytes;

        var dib = new byte[totalSize];
        using (var ms = new MemoryStream(dib))
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40);           // biSize
            bw.Write(w);
            bw.Write(h);
            bw.Write((short)1);     // biPlanes
            bw.Write((short)32);    // biBitCount
            bw.Write(0);            // biCompression (BI_RGB)
            bw.Write(imageBytes);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);

            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBuf = new byte[rowBytes];
                for (int y = h - 1; y >= 0; y--)
                {
                    IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(rowPtr, rowBuf, 0, rowBytes);
                    bw.Write(rowBuf);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        return dib;
    }
}
