using System.Numerics;

namespace AutoPBR.PreviewGpuAssets;

/// <summary>
/// Procedural cloud noise volumes, fully periodic so they tile seamlessly with wrapped UVWs.
/// Shape atlas (128³): R = Perlin-Worley base, G/B/A = Worley at rising frequencies (remap FBM weights).
/// Detail volume (32³): R/G/B = Worley octaves used to erode cloud edges.
/// </summary>
public static class PreviewCloudNoiseTextureGenerator
{
    public const int Size = 128;
    public const int DetailSize = 32;

    public static byte[] GenerateRgba8() => GenerateRgba8(CancellationToken.None);

    public static byte[] GenerateRgba8(CancellationToken cancellationToken) =>
        Bake(Size, BakeShapeVoxel, cancellationToken);

    public static byte[] GenerateDetailRgba8() =>
        GenerateDetailRgba8(CancellationToken.None);

    public static byte[] GenerateDetailRgba8(CancellationToken cancellationToken) =>
        Bake(DetailSize, BakeDetailVoxel, cancellationToken);

    private static byte[] Bake(
        int size,
        Func<Vector3, (byte R, byte G, byte B, byte A)> voxel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rgba = new byte[size * size * size * 4];
        Parallel.For(0, size, new ParallelOptions
        {
            CancellationToken = cancellationToken,
        }, z =>
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var uvw = new Vector3((x + 0.5f) / size, (y + 0.5f) / size, (z + 0.5f) / size);
                    var (r, g, b, a) = voxel(uvw);
                    var i = ((z * size + y) * size + x) * 4;
                    rgba[i] = r;
                    rgba[i + 1] = g;
                    rgba[i + 2] = b;
                    rgba[i + 3] = a;
                }
            }
        });

        return rgba;
    }

    private static (byte, byte, byte, byte) BakeShapeVoxel(Vector3 uvw)
    {
        var perlin = PeriodicFbm(uvw, basePeriod: 4, octaves: 6, seed: 7);
        var worleyBase = WorleyFbm(uvw, baseCells: 4);
        var perlinWorley = Remap01(perlin, worleyBase - 1f, 1f);

        var w1 = WorleyFbm(uvw, baseCells: 8);
        var w2 = WorleyFbm(uvw, baseCells: 16);
        var w3 = WorleyFbm(uvw, baseCells: 32);

        return (ToByte(perlinWorley), ToByte(w1), ToByte(w2), ToByte(w3));
    }

    private static (byte, byte, byte, byte) BakeDetailVoxel(Vector3 uvw)
    {
        var d0 = WorleyFbm(uvw, baseCells: 2);
        var d1 = WorleyFbm(uvw, baseCells: 4);
        var d2 = WorleyFbm(uvw, baseCells: 8);
        return (ToByte(d0), ToByte(d1), ToByte(d2), 255);
    }

    private static float WorleyFbm(Vector3 uvw, int baseCells) =>
        PeriodicWorley(uvw * baseCells, baseCells) * 0.625f +
        PeriodicWorley(uvw * (baseCells * 2), baseCells * 2) * 0.25f +
        PeriodicWorley(uvw * (baseCells * 4), baseCells * 4) * 0.125f;

    private static float Remap01(float x, float a, float b) =>
        Math.Clamp((x - a) / MathF.Max(b - a, 1e-5f), 0f, 1f);

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f);

    private static float PeriodicWorley(Vector3 p, int cells)
    {
        var cx = (int)MathF.Floor(p.X);
        var cy = (int)MathF.Floor(p.Y);
        var cz = (int)MathF.Floor(p.Z);
        var minDist = 1f;
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = cx + dx;
                    var ny = cy + dy;
                    var nz = cz + dz;
                    var feature = new Vector3(nx, ny, nz) + Hash33(Wrap(nx, cells), Wrap(ny, cells), Wrap(nz, cells), seed: 0);
                    var dist = Vector3.Distance(p, feature);
                    minDist = MathF.Min(minDist, dist);
                }
            }
        }

        return 1f - MathF.Min(minDist, 1f);
    }

    private static float PeriodicFbm(Vector3 uvw, int basePeriod, int octaves, int seed)
    {
        var sum = 0f;
        var amp = 0.5f;
        var norm = 0f;
        for (var i = 0; i < octaves; i++)
        {
            var period = basePeriod << i;
            sum += amp * PeriodicValueNoise(uvw * period, period, seed + i * 17);
            norm += amp;
            amp *= 0.5f;
        }

        return sum / Math.Max(norm, 1e-5f);
    }

    private static float PeriodicValueNoise(Vector3 p, int period, int seed)
    {
        var x0 = (int)MathF.Floor(p.X);
        var y0 = (int)MathF.Floor(p.Y);
        var z0 = (int)MathF.Floor(p.Z);
        var fx = Smooth(p.X - x0);
        var fy = Smooth(p.Y - y0);
        var fz = Smooth(p.Z - z0);

        float Corner(int dx, int dy, int dz) =>
            Hash01(Wrap(x0 + dx, period), Wrap(y0 + dy, period), Wrap(z0 + dz, period), seed);

        var nx00 = Lerp(Corner(0, 0, 0), Corner(1, 0, 0), fx);
        var nx10 = Lerp(Corner(0, 1, 0), Corner(1, 1, 0), fx);
        var nx01 = Lerp(Corner(0, 0, 1), Corner(1, 0, 1), fx);
        var nx11 = Lerp(Corner(0, 1, 1), Corner(1, 1, 1), fx);
        var nxy0 = Lerp(nx00, nx10, fy);
        var nxy1 = Lerp(nx01, nx11, fy);
        return Lerp(nxy0, nxy1, fz);
    }

    private static int Wrap(int v, int period)
    {
        var m = v % period;
        return m < 0 ? m + period : m;
    }

    private static Vector3 Hash33(int x, int y, int z, int seed) =>
        new(Hash01(x, y, z, seed), Hash01(x, y, z, seed + 19), Hash01(x, y, z, seed + 43));

    private static float Hash01(int x, int y, int z, int seed)
    {
        var h = unchecked((uint)(x * 374761393 + y * 668265263 + z * 1274126177 + seed * 974711));
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smooth(float t) => t * t * (3f - 2f * t);
}
