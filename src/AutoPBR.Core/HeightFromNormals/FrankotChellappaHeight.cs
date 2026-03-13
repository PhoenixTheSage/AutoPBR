// Optional height-from-normal map using the Frankot-Chellappa algorithm (Fourier domain).
// This approach is used in the DeepBump ecosystem (NormalHeight add-on by HugoTini).
// See LICENSE-DeepBump.txt and README for attribution.

using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.Core.HeightFromNormals;

/// <summary>
/// Reconstructs a height map from a tangent-space normal map using the Frankot-Chellappa
/// algorithm (Fourier-domain integration). Based on the method used in the DeepBump / NormalHeight
/// project (HugoTini). Improves height consistency with the normal map.
/// </summary>
public static class FrankotChellappaHeight
{
    private const float Epsilon = 1e-6f;

    /// <summary>
    /// Builds a height map from the given normal map. Expects tangent-space normals with
    /// R = normal X, G = normal Y, B = AO/unused; Z is reconstructed as sqrt(1 - x² - y²).
    /// Returns (Width, Height, Data) with height in [0,255]; caller can wrap in HeightMap.
    /// </summary>
    public static (int Width, int Height, byte[] Data) FromNormalMap(Image<Rgba32> normalMap)
    {
        var width = normalMap.Width;
        var height = normalMap.Height;
        var n = width * height;

        // Extract gradients p = dz/dx, q = dz/dy from normal: n = (-p,-q,1)/sqrt(1+p²+q²)
        // So p = -nx/nz, q = -ny/nz with nz = sqrt(1 - nx² - ny²).
        var p = new double[n];
        var q = new double[n];

        normalMap.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var px = row[x];
                    var nx = (px.R / 255.0) * 2.0 - 1.0;
                    var ny = (px.G / 255.0) * 2.0 - 1.0;
                    var nzSq = 1.0 - nx * nx - ny * ny;
                    var nz = nzSq > Epsilon ? Math.Sqrt(nzSq) : Epsilon;
                    var idx = y * width + x;
                    p[idx] = -nx / nz;
                    q[idx] = -ny / nz;
                }
            }
        });

        // Frankot-Chellappa in Fourier domain: Z(u,v) = (-i * (ωu*P + ωv*Q)) / (ωu² + ωv²)
        // with ωu = 2π*u/width, ωv = 2π*v/height. At (0,0) set Z=0.
        var fftP = ToComplex32_2D(p);
        var fftQ = ToComplex32_2D(q);
        Fft2D(fftP, width, height, forward: true);
        Fft2D(fftQ, width, height, forward: true);

        var twoPiW = (float)(2.0 * Math.PI / width);
        var twoPiH = (float)(2.0 * Math.PI / height);
        var zFft = new Complex32[n];

        for (var v = 0; v < height; v++)
        {
            var omegaV = twoPiH * (v <= height / 2 ? v : v - height);
            for (var u = 0; u < width; u++)
            {
                var omegaU = twoPiW * (u <= width / 2 ? u : u - width);
                var idx = v * width + u;
                var denom = omegaU * omegaU + omegaV * omegaV;
                if (denom < 1e-20f)
                {
                    zFft[idx] = Complex32.Zero;
                    continue;
                }

                var sum = (omegaU * fftP[idx] + omegaV * fftQ[idx]);
                var num = new Complex32(0, -1) * sum;
                zFft[idx] = num / denom;
            }
        }

        Fft2D(zFft, width, height, forward: false);

        // Take real part, normalize to 0–255 (scale by 1/n from IFFT is already in MathNet)
        var scale = 1.0 / (width * height);
        var minH = double.MaxValue;
        var maxH = double.MinValue;
        var real = new double[n];
        for (var i = 0; i < n; i++)
        {
            var h = zFft[i].Real * scale;
            real[i] = h;
            if (h < minH) minH = h;
            if (h > maxH) maxH = h;
        }

        var range = maxH - minH;
        if (range < 1e-10) range = 1;
        var outData = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var t = (real[i] - minH) / range;
            outData[i] = (byte)Math.Clamp((int)Math.Round(t * 255.0), 0, 255);
        }

        return (width, height, outData);
    }

    private static Complex32[] ToComplex32_2D(double[] data)
    {
        var c = new Complex32[data.Length];
        for (var i = 0; i < data.Length; i++)
            c[i] = new Complex32((float)data[i], 0f);
        return c;
    }

    private static void Fft2D(Complex32[] data, int width, int height, bool forward)
    {
        var rowBuf = new Complex32[width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                rowBuf[x] = data[y * width + x];
            if (forward)
                Fourier.Forward(rowBuf);
            else
                Fourier.Inverse(rowBuf);
            for (var x = 0; x < width; x++)
                data[y * width + x] = rowBuf[x];
        }

        var colBuf = new Complex32[height];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
                colBuf[y] = data[y * width + x];
            if (forward)
                Fourier.Forward(colBuf);
            else
                Fourier.Inverse(colBuf);
            for (var y = 0; y < height; y++)
                data[y * width + x] = colBuf[y];
        }
    }
}
