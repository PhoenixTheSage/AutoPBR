using System.Security.Cryptography;

namespace AutoPBR.PreviewGpuAssets;

/// <summary>
/// Deterministic, toroidal spatiotemporal blue-noise ranks used to place cloud-march samples.
/// The offline generator high-pass filters an integer hash lattice in XY and time, then rank
/// maps every frame so each R8 slice has an exact uniform distribution.
/// </summary>
public static class PreviewCloudSpatiotemporalBlueNoiseGenerator
{
    public const int Width = 128;
    public const int Height = 128;
    public const int FrameCount = 64;
    public const int AssetVersion = 1;
    public const string AssetFileName = "cloud_stbn_128x128x64_r8_v1.bin";
    public const int ByteLength = Width * Height * FrameCount;

    // Updated only when the deterministic generation ABI intentionally changes.
    public const string ExpectedSha256 = "38af39ee46763013169a3ab5bcdb1da67acf8e1ff8166074e93fec39da5d81f3";

    public static byte[] GenerateR8()
    {
        var data = new byte[ByteLength];
        var rankedSlice = new RankedSample[Width * Height];

        for (var z = 0; z < FrameCount; z++)
        {
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var index = y * Width + x;
                    rankedSlice[index] = new RankedSample(
                        BlueNoiseScore(x, y, z),
                        index);
                }
            }

            Array.Sort(rankedSlice, static (left, right) =>
            {
                var scoreOrder = left.Score.CompareTo(right.Score);
                return scoreOrder != 0 ? scoreOrder : left.SourceIndex.CompareTo(right.SourceIndex);
            });

            var sliceOffset = z * Width * Height;
            for (var rank = 0; rank < rankedSlice.Length; rank++)
            {
                // Width*Height is divisible by 256, producing exactly 64 of every R8 value.
                var value = (byte)(rank * 256 / rankedSlice.Length);
                data[sliceOffset + rankedSlice[rank].SourceIndex] = value;
            }
        }

        return data;
    }

    public static string ComputeSha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static bool HasExpectedHash(ReadOnlySpan<byte> data) =>
        data.Length == ByteLength &&
        string.Equals(ComputeSha256Hex(data), ExpectedSha256, StringComparison.Ordinal);

    private static long BlueNoiseScore(int x, int y, int z)
    {
        // Zero-DC high-pass kernel. Cardinal and diagonal XY rejection suppresses spatial
        // clumping; the wrapped Z neighbors suppress low-frequency temporal runs.
        var center = Hash16(x, y, z);
        var cardinal =
            Hash16(x - 1, y, z) +
            Hash16(x + 1, y, z) +
            Hash16(x, y - 1, z) +
            Hash16(x, y + 1, z);
        var diagonal =
            Hash16(x - 1, y - 1, z) +
            Hash16(x + 1, y - 1, z) +
            Hash16(x - 1, y + 1, z) +
            Hash16(x + 1, y + 1, z);
        var temporal = Hash16(x, y, z - 1) + Hash16(x, y, z + 1);
        return center * 16L - cardinal * 2L - diagonal - temporal * 2L;
    }

    private static int Hash16(int x, int y, int z)
    {
        var wrappedX = x & (Width - 1);
        var wrappedY = y & (Height - 1);
        var wrappedZ = z & (FrameCount - 1);
        var hash = unchecked(
            (uint)wrappedX * 0x9E3779B9u ^
            (uint)wrappedY * 0x85EBCA6Bu ^
            (uint)wrappedZ * 0xC2B2AE35u ^
            (uint)AssetVersion * 0x27D4EB2Fu);
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;
        return (int)(hash >> 16);
    }

    private readonly record struct RankedSample(long Score, int SourceIndex);
}
