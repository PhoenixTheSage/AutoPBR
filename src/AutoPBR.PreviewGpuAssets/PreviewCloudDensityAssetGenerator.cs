using System.Security.Cryptography;

namespace AutoPBR.PreviewGpuAssets;

/// <summary>
/// Deterministic CQ2 v2 cloud-density asset generator. All field evaluation uses fixed-point
/// arithmetic and writes independent texels, so output is invariant to locale, scheduling,
/// processor floating-point mode, and parallel execution order.
/// </summary>
public static class PreviewCloudDensityAssetGenerator
{
    public const string ExpectedShapeSha256 =
        "13966e74ccf9b03bcac896ab0f1869eb0cca3c01813ecfd83566e0571531f906";
    public const string ExpectedDetailSha256 =
        "71782f1b10c30b38c1fa7c80da18c01fc73ba12153b1063a494dd9304c786083";
    public const string ExpectedWeatherSha256 =
        "c58a1549ed26a8da72c519e430b20cc5166b9d0680642cc62ea112ad4583556c";

    private const int CoordinateBits = 20;
    private const int CoordinateOne = 1 << CoordinateBits;
    private const int ValueOne = ushort.MaxValue;

    public static byte[] GenerateShapeRgba8() =>
        BakeVolume(PreviewCloudDensityAssetContract.Shape, BakeShapeVoxel);

    public static byte[] GenerateDetailRgba8() =>
        BakeVolume(PreviewCloudDensityAssetContract.Detail, BakeDetailVoxel);

    public static byte[] GenerateWeatherRgba8()
    {
        var descriptor = PreviewCloudDensityAssetContract.Weather;
        var rgba = new byte[descriptor.ByteLength];
        Parallel.For(0, descriptor.Height, y =>
        {
            var v = NormalizedCoordinate(y, descriptor.Height);
            for (var x = 0; x < descriptor.Width; x++)
            {
                var u = NormalizedCoordinate(x, descriptor.Width);
                var (r, g, b, a) = BakeWeatherPixel(u, v);
                var offset = (y * descriptor.Width + x) * 4;
                rgba[offset] = r;
                rgba[offset + 1] = g;
                rgba[offset + 2] = b;
                rgba[offset + 3] = a;
            }
        });
        return rgba;
    }

    public static PreviewCloudDensityAssetPayloads GenerateAll() =>
        new(
            GenerateShapeRgba8(),
            GenerateDetailRgba8(),
            GenerateWeatherRgba8());

    public static string ComputeSha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static bool HasExpectedHash(
        PreviewCloudDensityAssetKind kind,
        ReadOnlySpan<byte> data)
    {
        if (!PreviewCloudDensityAssetContract.TryGet(kind, out var descriptor) ||
            !PreviewCloudDensityAssetContract.ValidatePayload(descriptor, data, out _))
        {
            return false;
        }

        var expected = kind switch
        {
            PreviewCloudDensityAssetKind.Shape => ExpectedShapeSha256,
            PreviewCloudDensityAssetKind.Detail => ExpectedDetailSha256,
            PreviewCloudDensityAssetKind.Weather => ExpectedWeatherSha256,
            _ => string.Empty,
        };
        return string.Equals(ComputeSha256Hex(data), expected, StringComparison.Ordinal);
    }

    private static byte[] BakeVolume(
        PreviewCloudDensityAssetDescriptor descriptor,
        Func<int, int, int, (byte R, byte G, byte B, byte A)> voxel)
    {
        if (descriptor.Depth <= 1 ||
            descriptor.Width != descriptor.Height ||
            descriptor.Width != descriptor.Depth)
        {
            throw new ArgumentException("CQ2 volume generation requires a cubic 3D descriptor.", nameof(descriptor));
        }

        var rgba = new byte[descriptor.ByteLength];
        Parallel.For(0, descriptor.Depth, z =>
        {
            var w = NormalizedCoordinate(z, descriptor.Depth);
            for (var y = 0; y < descriptor.Height; y++)
            {
                var v = NormalizedCoordinate(y, descriptor.Height);
                for (var x = 0; x < descriptor.Width; x++)
                {
                    var u = NormalizedCoordinate(x, descriptor.Width);
                    var (r, g, b, a) = voxel(u, v, w);
                    var offset =
                        ((z * descriptor.Height + y) * descriptor.Width + x) * 4;
                    rgba[offset] = r;
                    rgba[offset + 1] = g;
                    rgba[offset + 2] = b;
                    rgba[offset + 3] = a;
                }
            }
        });
        return rgba;
    }

    private static (byte, byte, byte, byte) BakeShapeVoxel(int u, int v, int w)
    {
        var channels = PreviewCloudDensityAssetContract.Shape.Channels;

        var coherent = Fbm3(u, v, w, basePeriod: 3, octaves: 5, channels[0].Seed);
        var bodyBillow = Cellular3(u, v, w, cells: 5, channels[0].Seed ^ 0x2b67);
        var coherentBody = Smoothstep(
            ValueOne * 18 / 100,
            ValueOne * 86 / 100,
            Weighted(coherent, 76, bodyBillow, 24));

        var broadCell = Cellular3(u, v, w, cells: 4, channels[1].Seed);
        var broadNoise = Fbm3(u, v, w, basePeriod: 3, octaves: 3, channels[1].Seed ^ 0x41a7);
        var broad = Smoothstep(
            ValueOne * 8 / 100,
            ValueOne * 94 / 100,
            Weighted(broadCell, 78, broadNoise, 22));

        var mediumCell = Cellular3(u, v, w, cells: 8, channels[2].Seed);
        var mediumNoise = Fbm3(u, v, w, basePeriod: 7, octaves: 2, channels[2].Seed ^ 0x63d1);
        var medium = Smoothstep(
            ValueOne * 7 / 100,
            ValueOne * 95 / 100,
            Weighted(mediumCell, 82, mediumNoise, 18));

        var fineCell = Cellular3(u, v, w, cells: 16, channels[3].Seed);
        var fineNoise = Fbm3(u, v, w, basePeriod: 13, octaves: 2, channels[3].Seed ^ 0x7f4a);
        var fine = Smoothstep(
            ValueOne * 6 / 100,
            ValueOne * 96 / 100,
            Weighted(fineCell, 86, fineNoise, 14));

        return (ToByte(coherentBody), ToByte(broad), ToByte(medium), ToByte(fine));
    }

    private static (byte, byte, byte, byte) BakeDetailVoxel(int u, int v, int w)
    {
        var channels = PreviewCloudDensityAssetContract.Detail.Channels;

        var broadCell = Cellular3(u, v, w, cells: 4, channels[0].Seed);
        var broadNoise = Fbm3(u, v, w, basePeriod: 3, octaves: 3, channels[0].Seed ^ 0x39b5);
        var broad = Smoothstep(
            ValueOne * 7 / 100,
            ValueOne * 95 / 100,
            Weighted(broadCell, 72, broadNoise, 28));

        var fineCell = Cellular3(u, v, w, cells: 9, channels[1].Seed);
        var fineNoise = Fbm3(u, v, w, basePeriod: 8, octaves: 3, channels[1].Seed ^ 0x54c3);
        var fine = Smoothstep(
            ValueOne * 6 / 100,
            ValueOne * 96 / 100,
            Weighted(fineCell, 76, fineNoise, 24));

        // Two decorrelated periodic potentials provide a signed curl-like scalar. Integer
        // coordinate transforms preserve toroidal continuity while stretching the B field
        // into wind-sheared fibers rather than another isotropic cellular octave.
        var curl0 = Fbm3(
            2 * u + v,
            -u + 2 * v + w,
            u - w,
            basePeriod: 3,
            octaves: 4,
            channels[3].Seed);
        var curl1 = Fbm3(
            u - 2 * v,
            u + v - w,
            2 * w + v,
            basePeriod: 4,
            octaves: 3,
            channels[3].Seed ^ 0x6d2f);
        var curlSigned = curl0 - curl1;
        var warp = (int)((long)curlSigned * CoordinateOne / (ValueOne * 12L));

        var wispyNoise = Fbm3(
            2 * u + v + warp,
            -u + 3 * v + w - warp,
            u + v - 2 * w + warp / 2,
            basePeriod: 3,
            octaves: 4,
            channels[2].Seed);
        var wispyFine = Fbm3(
            3 * u - v,
            u + 5 * v + w,
            v - 3 * w,
            basePeriod: 4,
            octaves: 3,
            channels[2].Seed ^ 0x275d);
        var wispyRidge = ValueOne - Math.Abs(wispyNoise * 2 - ValueOne);
        var wispy = Smoothstep(
            ValueOne * 12 / 100,
            ValueOne * 92 / 100,
            Weighted(wispyRidge, 74, wispyFine, 26));

        var curl = Math.Clamp(ValueOne / 2 + curlSigned / 2, 0, ValueOne);
        return (ToByte(broad), ToByte(fine), ToByte(wispy), ToByte(curl));
    }

    private static (byte, byte, byte, byte) BakeWeatherPixel(int u, int v)
    {
        var channels = PreviewCloudDensityAssetContract.Weather.Channels;

        var warpUField = Fbm2(u, v, basePeriod: 2, octaves: 3, channels[0].Seed ^ 0x1d43);
        var warpVField = Fbm2(
            2 * u + v,
            -u + 2 * v,
            basePeriod: 2,
            octaves: 3,
            channels[0].Seed ^ 0x3479);
        var warpU = (int)((long)(warpUField - ValueOne / 2) * CoordinateOne / (ValueOne * 9L));
        var warpV = (int)((long)(warpVField - ValueOne / 2) * CoordinateOne / (ValueOne * 9L));
        var wu = u + warpU;
        var wv = v + warpV;

        var coverageLarge = Fbm2(wu, wv, basePeriod: 2, octaves: 5, channels[0].Seed);
        var coverageSystems = Fbm2(
            2 * wu + wv,
            -wu + 2 * wv,
            basePeriod: 3,
            octaves: 4,
            channels[0].Seed ^ 0x58a1);
        var coverage = Smoothstep(
            ValueOne * 32 / 100,
            ValueOne * 74 / 100,
            Weighted(coverageLarge, 68, coverageSystems, 32));

        var typeLarge = Fbm2(wu, wv, basePeriod: 2, octaves: 4, channels[1].Seed);
        var typeCells = Cellular2(
            2 * wu - wv,
            wu + 2 * wv,
            cells: 5,
            channels[1].Seed ^ 0x21e7);
        var cloudType = Smoothstep(
            ValueOne * 24 / 100,
            ValueOne * 78 / 100,
            Weighted(typeLarge, 72, typeCells, 28));

        var stormField = Fbm2(
            wu - 2 * wv,
            2 * wu + wv,
            basePeriod: 3,
            octaves: 4,
            channels[2].Seed);
        var precipitation = Smoothstep(
            ValueOne * 26 / 100,
            ValueOne * 82 / 100,
            Weighted(stormField, 62, coverage, 38));

        var convectionCells = Cellular2(
            3 * wu + wv,
            -wu + 3 * wv,
            cells: 6,
            channels[3].Seed);
        var convectionNoise = Fbm2(
            wu + 2 * wv,
            -2 * wu + wv,
            basePeriod: 4,
            octaves: 3,
            channels[3].Seed ^ 0x4bc9);
        var convectionPotential = Weighted(convectionCells, 55, convectionNoise, 45);
        var convection = Smoothstep(
            ValueOne * 30 / 100,
            ValueOne * 88 / 100,
            Weighted(convectionPotential, 70, cloudType, 30));

        return (
            ToByte(coverage),
            ToByte(cloudType),
            ToByte(precipitation),
            ToByte(convection));
    }

    private static int Fbm3(
        int u,
        int v,
        int w,
        int basePeriod,
        int octaves,
        int seed)
    {
        long sum = 0;
        var weight = 256;
        var totalWeight = 0;
        for (var octave = 0; octave < octaves; octave++)
        {
            sum += (long)PeriodicValueNoise3(
                u,
                v,
                w,
                basePeriod << octave,
                seed + octave * 104729) * weight;
            totalWeight += weight;
            weight >>= 1;
        }

        return (int)(sum / totalWeight);
    }

    private static int Fbm2(
        int u,
        int v,
        int basePeriod,
        int octaves,
        int seed)
    {
        long sum = 0;
        var weight = 256;
        var totalWeight = 0;
        for (var octave = 0; octave < octaves; octave++)
        {
            sum += (long)PeriodicValueNoise2(
                u,
                v,
                basePeriod << octave,
                seed + octave * 104729) * weight;
            totalWeight += weight;
            weight >>= 1;
        }

        return (int)(sum / totalWeight);
    }

    private static int PeriodicValueNoise3(int u, int v, int w, int period, int seed)
    {
        ResolveLattice(u, period, out var x0, out var tx);
        ResolveLattice(v, period, out var y0, out var ty);
        ResolveLattice(w, period, out var z0, out var tz);
        var sx = SmoothCoordinate(tx);
        var sy = SmoothCoordinate(ty);
        var sz = SmoothCoordinate(tz);

        int Corner(int dx, int dy, int dz) =>
            HashValue(
                Wrap(x0 + dx, period),
                Wrap(y0 + dy, period),
                Wrap(z0 + dz, period),
                seed);

        var x00 = Lerp(Corner(0, 0, 0), Corner(1, 0, 0), sx);
        var x10 = Lerp(Corner(0, 1, 0), Corner(1, 1, 0), sx);
        var x01 = Lerp(Corner(0, 0, 1), Corner(1, 0, 1), sx);
        var x11 = Lerp(Corner(0, 1, 1), Corner(1, 1, 1), sx);
        return Lerp(Lerp(x00, x10, sy), Lerp(x01, x11, sy), sz);
    }

    private static int PeriodicValueNoise2(int u, int v, int period, int seed)
    {
        ResolveLattice(u, period, out var x0, out var tx);
        ResolveLattice(v, period, out var y0, out var ty);
        var sx = SmoothCoordinate(tx);
        var sy = SmoothCoordinate(ty);

        int Corner(int dx, int dy) =>
            HashValue(
                Wrap(x0 + dx, period),
                Wrap(y0 + dy, period),
                0,
                seed);

        return Lerp(
            Lerp(Corner(0, 0), Corner(1, 0), sx),
            Lerp(Corner(0, 1), Corner(1, 1), sx),
            sy);
    }

    private static int Cellular3(int u, int v, int w, int cells, int seed)
    {
        ResolveLattice(u, cells, out var x0, out var tx);
        ResolveLattice(v, cells, out var y0, out var ty);
        ResolveLattice(w, cells, out var z0, out var tz);
        var minimumSquared = long.MaxValue;

        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var wrappedX = Wrap(x0 + dx, cells);
                    var wrappedY = Wrap(y0 + dy, cells);
                    var wrappedZ = Wrap(z0 + dz, cells);
                    var deltaX =
                        ((long)dx << CoordinateBits) +
                        HashCoordinate(wrappedX, wrappedY, wrappedZ, seed) -
                        tx;
                    var deltaY =
                        ((long)dy << CoordinateBits) +
                        HashCoordinate(wrappedX, wrappedY, wrappedZ, seed ^ 0x4f1b) -
                        ty;
                    var deltaZ =
                        ((long)dz << CoordinateBits) +
                        HashCoordinate(wrappedX, wrappedY, wrappedZ, seed ^ 0x7a2d) -
                        tz;
                    var squared =
                        deltaX * deltaX +
                        deltaY * deltaY +
                        deltaZ * deltaZ;
                    minimumSquared = Math.Min(minimumSquared, squared);
                }
            }
        }

        return CellularDistanceToValue(minimumSquared, dimensions: 3);
    }

    private static int Cellular2(int u, int v, int cells, int seed)
    {
        ResolveLattice(u, cells, out var x0, out var tx);
        ResolveLattice(v, cells, out var y0, out var ty);
        var minimumSquared = long.MaxValue;

        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var wrappedX = Wrap(x0 + dx, cells);
                var wrappedY = Wrap(y0 + dy, cells);
                var deltaX =
                    ((long)dx << CoordinateBits) +
                    HashCoordinate(wrappedX, wrappedY, 0, seed) -
                    tx;
                var deltaY =
                    ((long)dy << CoordinateBits) +
                    HashCoordinate(wrappedX, wrappedY, 0, seed ^ 0x4f1b) -
                    ty;
                var squared = deltaX * deltaX + deltaY * deltaY;
                minimumSquared = Math.Min(minimumSquared, squared);
            }
        }

        return CellularDistanceToValue(minimumSquared, dimensions: 2);
    }

    private static int CellularDistanceToValue(long squaredDistance, int dimensions)
    {
        var coordinateSquared = (long)CoordinateOne * CoordinateOne;
        var normalization = dimensions == 3
            ? coordinateSquared * 3 / 2
            : coordinateSquared * 5 / 4;
        var distance = Math.Clamp(
            squaredDistance * ValueOne / Math.Max(normalization, 1L),
            0,
            ValueOne);
        return Smoothstep(0, ValueOne, ValueOne - (int)distance);
    }

    private static void ResolveLattice(
        int normalizedCoordinate,
        int period,
        out int cell,
        out int fraction)
    {
        var scaled = (long)normalizedCoordinate * period;
        cell = FloorDivide(scaled, CoordinateOne);
        fraction = (int)(scaled - (long)cell * CoordinateOne);
    }

    private static int NormalizedCoordinate(int index, int size) =>
        size <= 1
            ? 0
            : (int)((long)index * CoordinateOne / (size - 1));

    private static int SmoothCoordinate(int value)
    {
        var squared = (long)value * value / CoordinateOne;
        return (int)(squared * (3L * CoordinateOne - 2L * value) / CoordinateOne);
    }

    private static int Smoothstep(int edge0, int edge1, int value)
    {
        if (edge1 <= edge0)
        {
            return value >= edge1 ? ValueOne : 0;
        }

        var t = Math.Clamp(
            (long)(value - edge0) * CoordinateOne / (edge1 - edge0),
            0,
            CoordinateOne);
        return (int)((long)SmoothCoordinate((int)t) * ValueOne / CoordinateOne);
    }

    private static int Weighted(int first, int firstWeight, int second, int secondWeight)
    {
        var totalWeight = firstWeight + secondWeight;
        return (int)(((long)first * firstWeight + (long)second * secondWeight) / totalWeight);
    }

    private static int Lerp(int first, int second, int t) =>
        first + (int)((long)(second - first) * t / CoordinateOne);

    private static int FloorDivide(long value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return (int)(remainder < 0 ? quotient - 1 : quotient);
    }

    private static int Wrap(int value, int period)
    {
        var wrapped = value % period;
        return wrapped < 0 ? wrapped + period : wrapped;
    }

    private static int HashCoordinate(int x, int y, int z, int seed) =>
        (int)(Hash(x, y, z, seed) & (CoordinateOne - 1));

    private static int HashValue(int x, int y, int z, int seed) =>
        (int)(Hash(x, y, z, seed) & ValueOne);

    private static uint Hash(int x, int y, int z, int seed)
    {
        var hash = unchecked((uint)seed * 0x9E3779B9u);
        hash ^= unchecked((uint)x * 0x85EBCA6Bu);
        hash ^= unchecked((uint)y * 0xC2B2AE35u);
        hash ^= unchecked((uint)z * 0x27D4EB2Fu);
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;
        return hash;
    }

    private static byte ToByte(int value) =>
        (byte)((Math.Clamp(value, 0, ValueOne) + 128) / 257);
}

public sealed record PreviewCloudDensityAssetPayloads(
    byte[] ShapeRgba,
    byte[] DetailRgba,
    byte[] WeatherRgba);
