namespace AutoPBR.PreviewGpuAssets;

/// <summary>
/// Immutable CQ2 density-asset ABI shared by the offline generator, packaged-asset loader,
/// renderer diagnostics, and tests. Changing any filename, dimension, channel meaning, or
/// seed requires a new asset version.
/// </summary>
public static class PreviewCloudDensityAssetContract
{
    public const int AssetVersion = 2;
    public const int ChannelCount = 4;
    public const int BytesPerChannel = 1;
    public const string GenerationAbi = "cq2-density-v2";

    public static PreviewCloudDensityAssetDescriptor Shape { get; } = new(
        PreviewCloudDensityAssetKind.Shape,
        "cloud_noise_shape_128_v2.bin",
        width: 128,
        height: 128,
        depth: 128,
        [
            new(0, "coherent-body", "Coherent Perlin-Worley vapor body", 7301),
            new(1, "broad-billow", "Broad cellular billow envelope", 7311),
            new(2, "medium-breakup", "Medium cellular lobe breakup", 7321),
            new(3, "fine-erosion", "Fine shape erosion envelope", 7331),
        ]);

    public static PreviewCloudDensityAssetDescriptor Detail { get; } = new(
        PreviewCloudDensityAssetKind.Detail,
        "cloud_noise_detail_64_v2.bin",
        width: 64,
        height: 64,
        depth: 64,
        [
            new(0, "broad-billow", "Broad billowy erosion", 9101),
            new(1, "fine-billow", "Fine billowy erosion", 9113),
            new(2, "wispy-erosion", "Wispy or sheared erosion", 9127),
            new(3, "curl-distortion", "Curl or domain-distortion scalar", 9137),
        ]);

    public static PreviewCloudDensityAssetDescriptor Weather { get; } = new(
        PreviewCloudDensityAssetKind.Weather,
        "cloud_weather_1024_v2.bin",
        width: 1024,
        height: 1024,
        depth: 1,
        [
            new(0, "coverage", "Coverage or humidity", 12011),
            new(1, "cloud-type", "Shallow-to-convective cloud type", 12037),
            new(2, "density-potential", "Precipitation or density potential", 12049),
            new(3, "convection", "Convection or updraft", 12071),
        ]);

    public static IReadOnlyList<PreviewCloudDensityAssetDescriptor> Assets { get; } =
        Array.AsReadOnly([Shape, Detail, Weather]);

    public static long BaseLevelByteLength { get; } = Assets.Sum(asset => asset.ByteLength);

    public static long MipChainByteLength { get; } = Assets.Sum(asset => asset.MipChainByteLength);

    public static bool TryGet(
        PreviewCloudDensityAssetKind kind,
        out PreviewCloudDensityAssetDescriptor descriptor)
    {
        descriptor = kind switch
        {
            PreviewCloudDensityAssetKind.Shape => Shape,
            PreviewCloudDensityAssetKind.Detail => Detail,
            PreviewCloudDensityAssetKind.Weather => Weather,
            _ => null!,
        };
        return descriptor is not null;
    }

    public static bool ValidatePayload(
        PreviewCloudDensityAssetDescriptor descriptor,
        ReadOnlySpan<byte> payload,
        out string reason)
    {
        if (!Assets.Contains(descriptor))
        {
            reason = "descriptor-not-cq2-v2";
            return false;
        }

        if (payload.Length != descriptor.ByteLength)
        {
            reason = $"length-{payload.Length}-expected-{descriptor.ByteLength}";
            return false;
        }

        reason = "valid";
        return true;
    }
}

public enum PreviewCloudDensityAssetKind
{
    Shape = 0,
    Detail = 1,
    Weather = 2,
}

public sealed class PreviewCloudDensityAssetDescriptor
{
    public PreviewCloudDensityAssetDescriptor(
        PreviewCloudDensityAssetKind kind,
        string fileName,
        int width,
        int height,
        int depth,
        IReadOnlyList<PreviewCloudDensityChannelContract> channels)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("An asset filename is required.", nameof(fileName));
        }

        if (width <= 0 || height <= 0 || depth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Cloud density asset dimensions must be positive.");
        }

        if (channels.Count != PreviewCloudDensityAssetContract.ChannelCount ||
            channels.Select(channel => channel.Index).Distinct().Count() !=
            PreviewCloudDensityAssetContract.ChannelCount ||
            channels.Any(channel =>
                channel.Index < 0 ||
                channel.Index >= PreviewCloudDensityAssetContract.ChannelCount))
        {
            throw new ArgumentException(
                "RGBA8 cloud density assets require exactly one contract for channels 0 through 3.",
                nameof(channels));
        }

        Kind = kind;
        FileName = fileName;
        Width = width;
        Height = height;
        Depth = depth;
        Channels = Array.AsReadOnly(channels.OrderBy(channel => channel.Index).ToArray());
        ByteLength = checked(width * height * depth *
                             PreviewCloudDensityAssetContract.ChannelCount *
                             PreviewCloudDensityAssetContract.BytesPerChannel);
        MipChainByteLength = CalculateMipChainByteLength(width, height, depth);
    }

    public PreviewCloudDensityAssetKind Kind { get; }

    public string FileName { get; }

    public int Width { get; }

    public int Height { get; }

    public int Depth { get; }

    public int ByteLength { get; }

    public long MipChainByteLength { get; }

    public IReadOnlyList<PreviewCloudDensityChannelContract> Channels { get; }

    public string DimensionLabel =>
        Depth > 1 ? $"{Width}x{Height}x{Depth}" : $"{Width}x{Height}";

    private static long CalculateMipChainByteLength(int width, int height, int depth)
    {
        var total = 0L;
        while (true)
        {
            total = checked(total +
                            (long)width * height * depth *
                            PreviewCloudDensityAssetContract.ChannelCount *
                            PreviewCloudDensityAssetContract.BytesPerChannel);
            if (width == 1 && height == 1 && depth == 1)
            {
                return total;
            }

            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            depth = Math.Max(1, depth / 2);
        }
    }
}

public readonly record struct PreviewCloudDensityChannelContract(
    int Index,
    string Symbol,
    string Meaning,
    int Seed);
