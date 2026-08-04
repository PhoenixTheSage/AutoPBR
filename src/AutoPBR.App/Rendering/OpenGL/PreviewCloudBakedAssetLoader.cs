using Avalonia.Platform;

using AutoPBR.PreviewGpuAssets;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Loads pre-baked cloud noise, coverage, and sampling blobs from Assets/Preview.</summary>
internal static class PreviewCloudBakedAssetLoader
{
    private const string AssetRoot = "avares://AutoPBR.App/Assets/Preview/";
    private const string V1ShapeFileName = "cloud_noise_shape_128.bin";
    private const string V1DetailFileName = "cloud_noise_detail_32.bin";
    private const string V1WeatherFileName = "cloud_coverage_256.bin";
    private static readonly StandardAssetLoader BundledAssetLoader =
        new StandardAssetLoader(typeof(PreviewCloudBakedAssetLoader).Assembly);
    private static readonly object BundledAssetLoaderGate = new();

    public static bool TryLoadDensityAssetSet(
        bool allowV2,
        out PreviewCloudDensityAssetSet assetSet,
        out string reason) =>
        TryLoadDensityAssetSet(allowV2, TryLoadRaw, out assetSet, out reason);

    internal static bool TryLoadDensityAssetSet(
        bool allowV2,
        PreviewCloudRawAssetReader assetReader,
        out PreviewCloudDensityAssetSet assetSet,
        out string reason)
    {
        var v2Reason = "profile-disabled";
        if (allowV2 && TryLoadV2DensityAssetSet(
                assetReader,
                out assetSet,
                out v2Reason))
        {
            reason = $"v2-bundled/{PreviewCloudDensityAssetContract.GenerationAbi}";
            return true;
        }

        if (TryLoadV1DensityAssetSet(assetReader, out assetSet, out var v1Reason))
        {
            reason = $"v1-bundled (v2-{v2Reason})";
            return true;
        }

        assetSet = default;
        reason = $"v2-{v2Reason};v1-{v1Reason}";
        return false;
    }

    public static bool TryLoadShapeNoise(out byte[] rgba)
    {
        rgba = Array.Empty<byte>();
        if (!TryLoadRaw(V1ShapeFileName, out var data))
        {
            return false;
        }

        var expected = PreviewCloudNoiseTextureGenerator.Size *
                       PreviewCloudNoiseTextureGenerator.Size *
                       PreviewCloudNoiseTextureGenerator.Size * 4;
        if (data.Length != expected)
        {
            return false;
        }

        rgba = data;
        return true;
    }

    public static bool TryLoadDetailNoise(out byte[] rgba)
    {
        rgba = Array.Empty<byte>();
        if (!TryLoadRaw(V1DetailFileName, out var data))
        {
            return false;
        }

        var expected = PreviewCloudNoiseTextureGenerator.DetailSize *
                       PreviewCloudNoiseTextureGenerator.DetailSize *
                       PreviewCloudNoiseTextureGenerator.DetailSize * 4;
        if (data.Length != expected)
        {
            return false;
        }

        rgba = data;
        return true;
    }

    public static bool TryLoadCoverageMap(out byte[] rgba)
    {
        rgba = Array.Empty<byte>();
        if (!TryLoadRaw(V1WeatherFileName, out var data))
        {
            return false;
        }

        var expected = PreviewCloudCoverageMapGenerator.Size * PreviewCloudCoverageMapGenerator.Size * 4;
        if (data.Length != expected)
        {
            return false;
        }

        rgba = data;
        return true;
    }

    internal static bool ValidateDensityAssetV2(
        PreviewCloudDensityAssetDescriptor descriptor,
        ReadOnlySpan<byte> data,
        out string reason)
    {
        if (!PreviewCloudDensityAssetContract.ValidatePayload(
                descriptor,
                data,
                out reason))
        {
            return false;
        }

        if (!PreviewCloudDensityAssetGenerator.HasExpectedHash(
                descriptor.Kind,
                data))
        {
            reason = "sha256-mismatch";
            return false;
        }

        reason = "valid";
        return true;
    }

    public static bool TryLoadSpatiotemporalBlueNoise(out byte[] r8, out string reason)
    {
        r8 = Array.Empty<byte>();
        if (!TryLoadRaw(
                PreviewCloudSpatiotemporalBlueNoiseGenerator.AssetFileName,
                out var data,
                out reason))
        {
            return false;
        }

        if (!ValidateSpatiotemporalBlueNoise(data, out reason))
        {
            return false;
        }

        r8 = data;
        reason = $"asset-v{PreviewCloudSpatiotemporalBlueNoiseGenerator.AssetVersion}";
        return true;
    }

    internal static bool ValidateSpatiotemporalBlueNoise(
        ReadOnlySpan<byte> data,
        out string reason)
    {
        if (data.Length != PreviewCloudSpatiotemporalBlueNoiseGenerator.ByteLength)
        {
            reason =
                $"length-{data.Length}-expected-{PreviewCloudSpatiotemporalBlueNoiseGenerator.ByteLength}";
            return false;
        }

        if (!PreviewCloudSpatiotemporalBlueNoiseGenerator.HasExpectedHash(data))
        {
            reason = "sha256-mismatch";
            return false;
        }

        reason = "valid";
        return true;
    }

    private static bool TryLoadV2DensityAssetSet(
        PreviewCloudRawAssetReader assetReader,
        out PreviewCloudDensityAssetSet assetSet,
        out string reason)
    {
        assetSet = default;
        var loaded = new Dictionary<PreviewCloudDensityAssetKind, byte[]>();
        foreach (var descriptor in PreviewCloudDensityAssetContract.Assets)
        {
            if (!assetReader(descriptor.FileName, out var data, out var loadReason))
            {
                reason = $"{descriptor.Kind.ToString().ToLowerInvariant()}-{loadReason}";
                return false;
            }

            if (!ValidateDensityAssetV2(descriptor, data, out var validationReason))
            {
                reason =
                    $"{descriptor.Kind.ToString().ToLowerInvariant()}-{validationReason}";
                return false;
            }

            loaded.Add(descriptor.Kind, data);
        }

        assetSet = new PreviewCloudDensityAssetSet(
            AssetVersion: PreviewCloudDensityAssetContract.AssetVersion,
            ProfileName: "cq2-v2",
            ShapeSize: PreviewCloudDensityAssetContract.Shape.Width,
            ShapeRgba: loaded[PreviewCloudDensityAssetKind.Shape],
            DetailSize: PreviewCloudDensityAssetContract.Detail.Width,
            DetailRgba: loaded[PreviewCloudDensityAssetKind.Detail],
            WeatherWidth: PreviewCloudDensityAssetContract.Weather.Width,
            WeatherHeight: PreviewCloudDensityAssetContract.Weather.Height,
            WeatherRgba: loaded[PreviewCloudDensityAssetKind.Weather]);
        reason = "valid";
        return true;
    }

    private static bool TryLoadV1DensityAssetSet(
        PreviewCloudRawAssetReader assetReader,
        out PreviewCloudDensityAssetSet assetSet,
        out string reason)
    {
        assetSet = default;
        if (!TryLoadV1Asset(
                assetReader,
                V1ShapeFileName,
                checked(PreviewCloudNoiseTextureGenerator.Size *
                        PreviewCloudNoiseTextureGenerator.Size *
                        PreviewCloudNoiseTextureGenerator.Size * 4),
                out var shape,
                out var shapeReason))
        {
            reason = $"shape-{shapeReason}";
            return false;
        }

        if (!TryLoadV1Asset(
                assetReader,
                V1DetailFileName,
                checked(PreviewCloudNoiseTextureGenerator.DetailSize *
                        PreviewCloudNoiseTextureGenerator.DetailSize *
                        PreviewCloudNoiseTextureGenerator.DetailSize * 4),
                out var detail,
                out var detailReason))
        {
            reason = $"detail-{detailReason}";
            return false;
        }

        if (!TryLoadV1Asset(
                assetReader,
                V1WeatherFileName,
                checked(PreviewCloudCoverageMapGenerator.Size *
                        PreviewCloudCoverageMapGenerator.Size * 4),
                out var weather,
                out var weatherReason))
        {
            reason = $"weather-{weatherReason}";
            return false;
        }

        assetSet = new PreviewCloudDensityAssetSet(
            AssetVersion: 1,
            ProfileName: "legacy-v1",
            ShapeSize: PreviewCloudNoiseTextureGenerator.Size,
            ShapeRgba: shape,
            DetailSize: PreviewCloudNoiseTextureGenerator.DetailSize,
            DetailRgba: detail,
            WeatherWidth: PreviewCloudCoverageMapGenerator.Size,
            WeatherHeight: PreviewCloudCoverageMapGenerator.Size,
            WeatherRgba: weather);
        reason = "valid";
        return true;
    }

    private static bool TryLoadV1Asset(
        PreviewCloudRawAssetReader assetReader,
        string fileName,
        int expectedLength,
        out byte[] data,
        out string reason)
    {
        if (!assetReader(fileName, out data, out reason))
        {
            return false;
        }

        if (data.Length != expectedLength)
        {
            reason = $"length-{data.Length}-expected-{expectedLength}";
            data = Array.Empty<byte>();
            return false;
        }

        reason = "valid";
        return true;
    }

    private static bool TryLoadRaw(string fileName, out byte[] data)
    {
        return TryLoadRaw(fileName, out data, out _);
    }

    private static bool TryLoadRaw(string fileName, out byte[] data, out string reason)
    {
        data = Array.Empty<byte>();
        var uri = new Uri(AssetRoot + fileName);
        try
        {
            lock (BundledAssetLoaderGate)
            {
                // StandardAssetLoader can be reached concurrently by CQ1/CQ2/CQ4 startup
                // and headless tests. Keep its Exists/Open pair atomic so one valid bundled
                // asset cannot transiently report missing while another stream is opening.
                if (!BundledAssetLoader.Exists(uri))
                {
                    reason = "missing";
                    return false;
                }

                using var stream = BundledAssetLoader.Open(uri);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                data = ms.ToArray();
            }

            if (data.Length == 0)
            {
                reason = "empty";
                return false;
            }

            reason = "loaded";
            return true;
        }
        catch (Exception exception)
        {
            reason = "read-" + exception.GetType().Name;
            return false;
        }
    }

    internal static bool TryLoadBundledRaw(
        string fileName,
        out byte[] data,
        out string reason) =>
        TryLoadRaw(fileName, out data, out reason);
}

internal delegate bool PreviewCloudRawAssetReader(
    string fileName,
    out byte[] data,
    out string reason);

internal readonly record struct PreviewCloudDensityAssetSet(
    int AssetVersion,
    string ProfileName,
    int ShapeSize,
    byte[] ShapeRgba,
    int DetailSize,
    byte[] DetailRgba,
    int WeatherWidth,
    int WeatherHeight,
    byte[] WeatherRgba)
{
    public long BaseLevelByteLength =>
        ShapeRgba.LongLength + DetailRgba.LongLength + WeatherRgba.LongLength;
}
