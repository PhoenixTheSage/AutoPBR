using Avalonia.Platform;

using AutoPBR.PreviewGpuAssets;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Loads pre-baked cloud noise, coverage, and sampling blobs from Assets/Preview.</summary>
internal static class PreviewCloudBakedAssetLoader
{
    private const string AssetRoot = "avares://AutoPBR.App/Assets/Preview/";

    public static bool TryLoadShapeNoise(out byte[] rgba)
    {
        rgba = Array.Empty<byte>();
        if (!TryLoadRaw("cloud_noise_shape_128.bin", out var data))
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
        if (!TryLoadRaw("cloud_noise_detail_32.bin", out var data))
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
        if (!TryLoadRaw("cloud_coverage_256.bin", out var data))
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
            if (!AssetLoader.Exists(uri))
            {
                reason = "missing";
                return false;
            }

            using var stream = AssetLoader.Open(uri);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            data = ms.ToArray();
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
}
