using AutoPBR.App.Rendering.OpenGL;

using AutoPBR.PreviewGpuAssets;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudNoiseTextureGeneratorTests
{
    [Fact]
    public void Cq2V2Contract_FreezesDimensionsFilenamesChannelsSeedsAndMemory()
    {
        Assert.Equal(2, PreviewCloudDensityAssetContract.AssetVersion);
        Assert.Equal("cq2-density-v2", PreviewCloudDensityAssetContract.GenerationAbi);
        Assert.Equal(3, PreviewCloudDensityAssetContract.Assets.Count);

        AssertDescriptor(
            PreviewCloudDensityAssetContract.Shape,
            PreviewCloudDensityAssetKind.Shape,
            "cloud_noise_shape_128_v2.bin",
            128,
            128,
            128,
            ["coherent-body", "broad-billow", "medium-breakup", "fine-erosion"],
            [7301, 7311, 7321, 7331]);
        AssertDescriptor(
            PreviewCloudDensityAssetContract.Detail,
            PreviewCloudDensityAssetKind.Detail,
            "cloud_noise_detail_64_v2.bin",
            64,
            64,
            64,
            ["broad-billow", "fine-billow", "wispy-erosion", "curl-distortion"],
            [9101, 9113, 9127, 9137]);
        AssertDescriptor(
            PreviewCloudDensityAssetContract.Weather,
            PreviewCloudDensityAssetKind.Weather,
            "cloud_weather_1024_v2.bin",
            1024,
            1024,
            1,
            ["coverage", "cloud-type", "density-potential", "convection"],
            [12011, 12037, 12049, 12071]);

        Assert.Equal(13_631_488L, PreviewCloudDensityAssetContract.BaseLevelByteLength);
        Assert.Equal(16_377_756L, PreviewCloudDensityAssetContract.MipChainByteLength);
    }

    [Fact]
    public void Cq2V2Contract_StrictlyRejectsWrongPayloadLength()
    {
        var descriptor = PreviewCloudDensityAssetContract.Detail;
        var exact = PreviewCloudDensityAssetGenerator.GenerateDetailRgba8();

        Assert.True(
            PreviewCloudBakedAssetLoader.ValidateDensityAssetV2(
                descriptor,
                exact,
                out var validReason),
            validReason);
        Assert.Equal("valid", validReason);

        Assert.False(
            PreviewCloudBakedAssetLoader.ValidateDensityAssetV2(
                descriptor,
                exact.AsSpan(1),
                out var invalidReason));
        Assert.Equal(
            $"length-{descriptor.ByteLength - 1}-expected-{descriptor.ByteLength}",
            invalidReason);

        var corrupt = exact.ToArray();
        corrupt[corrupt.Length / 2] ^= 0x80;
        Assert.False(
            PreviewCloudBakedAssetLoader.ValidateDensityAssetV2(
                descriptor,
                corrupt,
                out var hashReason));
        Assert.Equal("sha256-mismatch", hashReason);
    }

    [Fact]
    public void Cq2RolloutPolicy_LoadsOneCoherentV1SetWhileV2ProfileIsDisabled()
    {
        Assert.True(
            PreviewCloudBakedAssetLoader.TryLoadDensityAssetSet(
                allowV2: false,
                out var assets,
                out var reason),
            reason);

        Assert.Equal(1, assets.AssetVersion);
        Assert.Equal("legacy-v1", assets.ProfileName);
        Assert.Equal(PreviewCloudNoiseTextureGenerator.Size, assets.ShapeSize);
        Assert.Equal(PreviewCloudNoiseTextureGenerator.DetailSize, assets.DetailSize);
        Assert.Equal(PreviewCloudCoverageMapGenerator.Size, assets.WeatherWidth);
        Assert.Equal(PreviewCloudCoverageMapGenerator.Size, assets.WeatherHeight);
        Assert.Contains("v2-profile-disabled", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Cq2BundledProfile_LoadsOneCompleteV2SetWhenExplicitlyAllowed()
    {
        Assert.True(
            PreviewCloudBakedAssetLoader.TryLoadDensityAssetSet(
                allowV2: true,
                out var assets,
                out var reason),
            reason);

        Assert.Equal(PreviewCloudDensityAssetContract.AssetVersion, assets.AssetVersion);
        Assert.Equal("cq2-v2", assets.ProfileName);
        Assert.Equal(
            PreviewCloudDensityAssetContract.Shape.ByteLength,
            assets.ShapeRgba.Length);
        Assert.Equal(
            PreviewCloudDensityAssetContract.Detail.ByteLength,
            assets.DetailRgba.Length);
        Assert.Equal(
            PreviewCloudDensityAssetContract.Weather.ByteLength,
            assets.WeatherRgba.Length);
        Assert.True(PreviewCloudDensityAssetGenerator.HasExpectedHash(
            PreviewCloudDensityAssetKind.Shape,
            assets.ShapeRgba));
        Assert.True(PreviewCloudDensityAssetGenerator.HasExpectedHash(
            PreviewCloudDensityAssetKind.Detail,
            assets.DetailRgba));
        Assert.True(PreviewCloudDensityAssetGenerator.HasExpectedHash(
            PreviewCloudDensityAssetKind.Weather,
            assets.WeatherRgba));
        Assert.Equal(
            $"v2-bundled/{PreviewCloudDensityAssetContract.GenerationAbi}",
            reason);
    }

    [Fact]
    public void Cq2StrictProfileSelection_CorruptV2FallsBackToOneCompleteV1Set()
    {
        var v2 = PreviewCloudDensityAssetGenerator.GenerateAll();
        var corruptDetail = v2.DetailRgba.ToArray();
        corruptDetail[corruptDetail.Length / 2] ^= 0x40;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PreviewCloudDensityAssetContract.Shape.FileName] = v2.ShapeRgba,
            [PreviewCloudDensityAssetContract.Detail.FileName] = corruptDetail,
            [PreviewCloudDensityAssetContract.Weather.FileName] = v2.WeatherRgba,
            ["cloud_noise_shape_128.bin"] = PreviewCloudNoiseTextureGenerator.GenerateRgba8(),
            ["cloud_noise_detail_32.bin"] = PreviewCloudNoiseTextureGenerator.GenerateDetailRgba8(),
            ["cloud_coverage_256.bin"] = PreviewCloudCoverageMapGenerator.GenerateRgba8(),
        };

        Assert.True(
            PreviewCloudBakedAssetLoader.TryLoadDensityAssetSet(
                allowV2: true,
                ReadAsset,
                out var selected,
                out var reason),
            reason);
        Assert.Equal(1, selected.AssetVersion);
        Assert.Equal("legacy-v1", selected.ProfileName);
        Assert.Contains(
            "v2-detail-sha256-mismatch",
            reason,
            StringComparison.Ordinal);
        Assert.Equal(
            PreviewCloudNoiseTextureGenerator.Size *
            PreviewCloudNoiseTextureGenerator.Size *
            PreviewCloudNoiseTextureGenerator.Size * 4L +
            PreviewCloudNoiseTextureGenerator.DetailSize *
            PreviewCloudNoiseTextureGenerator.DetailSize *
            PreviewCloudNoiseTextureGenerator.DetailSize * 4L +
            PreviewCloudCoverageMapGenerator.Size *
            PreviewCloudCoverageMapGenerator.Size * 4L,
            selected.BaseLevelByteLength);

        bool ReadAsset(string fileName, out byte[] data, out string readReason)
        {
            if (files.TryGetValue(fileName, out data!))
            {
                readReason = "loaded";
                return true;
            }

            data = Array.Empty<byte>();
            readReason = "missing";
            return false;
        }
    }

    [Fact]
    public void GenerateRgba8_ProducesExpectedShapeVolume()
    {
        var rgba = PreviewCloudNoiseTextureGenerator.GenerateRgba8();
        Assert.Equal(PreviewCloudNoiseTextureGenerator.Size * PreviewCloudNoiseTextureGenerator.Size *
                     PreviewCloudNoiseTextureGenerator.Size * 4, rgba.Length);
        AssertChannelHasVariance(rgba, channel: 0); // Perlin-Worley base
        AssertChannelHasVariance(rgba, channel: 1); // Worley octave
    }

    [Fact]
    public void GenerateDetailRgba8_ProducesExpectedDetailVolume()
    {
        var rgba = PreviewCloudNoiseTextureGenerator.GenerateDetailRgba8();
        Assert.Equal(PreviewCloudNoiseTextureGenerator.DetailSize * PreviewCloudNoiseTextureGenerator.DetailSize *
                     PreviewCloudNoiseTextureGenerator.DetailSize * 4, rgba.Length);
        AssertChannelHasVariance(rgba, channel: 0);
    }

    [Fact]
    public void GenerateRgba8_HonorsCancellationBeforeAllocatingVolume()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => PreviewCloudNoiseTextureGenerator.GenerateRgba8(cancellation.Token));
    }

    [Fact]
    public void CoverageMapGenerator_ProducesWeatherChannels()
    {
        var map = PreviewCloudCoverageMapGenerator.GenerateRgba8();
        Assert.Equal(PreviewCloudCoverageMapGenerator.Size * PreviewCloudCoverageMapGenerator.Size * 4, map.Length);
        AssertChannelHasVariance(map, channel: 0); // coverage
        AssertChannelHasVariance(map, channel: 1); // cloud type
    }

    private static void AssertChannelHasVariance(byte[] rgba, int channel)
    {
        var min = byte.MaxValue;
        var max = byte.MinValue;
        for (var i = channel; i < rgba.Length; i += 4)
        {
            min = Math.Min(min, rgba[i]);
            max = Math.Max(max, rgba[i]);
        }

        Assert.True(max - min > 40, $"channel {channel} expected variance, got min={min} max={max}");
    }

    private static void AssertDescriptor(
        PreviewCloudDensityAssetDescriptor descriptor,
        PreviewCloudDensityAssetKind expectedKind,
        string expectedFileName,
        int expectedWidth,
        int expectedHeight,
        int expectedDepth,
        string[] expectedSymbols,
        int[] expectedSeeds)
    {
        Assert.Equal(expectedKind, descriptor.Kind);
        Assert.Equal(expectedFileName, descriptor.FileName);
        Assert.Equal(expectedWidth, descriptor.Width);
        Assert.Equal(expectedHeight, descriptor.Height);
        Assert.Equal(expectedDepth, descriptor.Depth);
        Assert.Equal(expectedSymbols, descriptor.Channels.Select(channel => channel.Symbol));
        Assert.Equal(expectedSeeds, descriptor.Channels.Select(channel => channel.Seed));
        Assert.All(descriptor.Channels, channel => Assert.False(string.IsNullOrWhiteSpace(channel.Meaning)));
        Assert.Equal(
            expectedWidth * expectedHeight * expectedDepth * 4,
            descriptor.ByteLength);
    }
}
