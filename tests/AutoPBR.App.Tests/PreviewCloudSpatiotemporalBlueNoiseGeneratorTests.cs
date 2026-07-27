using System.Runtime.CompilerServices;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;

using AutoPBR.PreviewGpuAssets;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudSpatiotemporalBlueNoiseGeneratorTests
{
    private static readonly Lazy<byte[]> Generated =
        new(PreviewCloudSpatiotemporalBlueNoiseGenerator.GenerateR8);

    [Fact]
    public void GenerateR8_IsDeterministicUniformAndHighFrequency()
    {
        var data = Generated.Value;

        Assert.Equal(128, PreviewCloudSpatiotemporalBlueNoiseGenerator.Width);
        Assert.Equal(128, PreviewCloudSpatiotemporalBlueNoiseGenerator.Height);
        Assert.Equal(64, PreviewCloudSpatiotemporalBlueNoiseGenerator.FrameCount);
        Assert.Equal(PreviewCloudSpatiotemporalBlueNoiseGenerator.ByteLength, data.Length);
        Assert.Equal(
            PreviewCloudSpatiotemporalBlueNoiseGenerator.ExpectedSha256,
            PreviewCloudSpatiotemporalBlueNoiseGenerator.ComputeSha256Hex(data));
        Assert.True(PreviewCloudSpatiotemporalBlueNoiseGenerator.HasExpectedHash(data));

        AssertUniformSliceHistogram(data, frame: 0);
        AssertUniformSliceHistogram(data, frame: 63);
        Assert.True(MeanWrappedAbsoluteDifference(data, temporal: false) > 90.0);
        Assert.True(MeanWrappedAbsoluteDifference(data, temporal: true) > 90.0);
    }

    [Fact]
    public void CheckedInAsset_MatchesGeneratorAndStrictLoaderContract()
    {
        var root = FindRepositoryRoot();
        var assetPath = Path.Combine(
            root,
            "src",
            "AutoPBR.App",
            "Assets",
            "Preview",
            PreviewCloudSpatiotemporalBlueNoiseGenerator.AssetFileName);
        var checkedIn = File.ReadAllBytes(assetPath);

        Assert.Equal(Generated.Value, checkedIn);
        Assert.True(
            PreviewCloudBakedAssetLoader.ValidateSpatiotemporalBlueNoise(
                checkedIn,
                out var validReason),
            validReason);

        Assert.False(
            PreviewCloudBakedAssetLoader.ValidateSpatiotemporalBlueNoise(
                checkedIn.AsSpan(1),
                out var lengthReason));
        Assert.StartsWith("length-", lengthReason, StringComparison.Ordinal);

        var corrupt = checkedIn.ToArray();
        corrupt[corrupt.Length / 2] ^= 0x80;
        Assert.False(
            PreviewCloudBakedAssetLoader.ValidateSpatiotemporalBlueNoise(
                corrupt,
                out var hashReason));
        Assert.Equal("sha256-mismatch", hashReason);
    }

    [Fact]
    public void RuntimePolicy_UsesAssetOnlyForDesktopHighAndCinematic()
    {
        Assert.False(OpenGlPreviewBackend.CanUseCloudStbn(
            useOpenGlEs: false,
            PreviewVolumetricQuality.Low,
            assetAvailable: true));
        Assert.False(OpenGlPreviewBackend.CanUseCloudStbn(
            useOpenGlEs: false,
            PreviewVolumetricQuality.Medium,
            assetAvailable: true));
        Assert.True(OpenGlPreviewBackend.CanUseCloudStbn(
            useOpenGlEs: false,
            PreviewVolumetricQuality.High,
            assetAvailable: true));
        Assert.True(OpenGlPreviewBackend.CanUseCloudStbn(
            useOpenGlEs: false,
            PreviewVolumetricQuality.Cinematic,
            assetAvailable: true));
        Assert.False(OpenGlPreviewBackend.CanUseCloudStbn(
            useOpenGlEs: true,
            PreviewVolumetricQuality.Cinematic,
            assetAvailable: true));
        Assert.False(OpenGlPreviewBackend.CanUseCloudStbn(
            useOpenGlEs: false,
            PreviewVolumetricQuality.Cinematic,
            assetAvailable: false));
    }

    private static void AssertUniformSliceHistogram(byte[] data, int frame)
    {
        var histogram = new int[256];
        var sliceOffset = frame *
                          PreviewCloudSpatiotemporalBlueNoiseGenerator.Width *
                          PreviewCloudSpatiotemporalBlueNoiseGenerator.Height;
        for (var i = 0;
             i < PreviewCloudSpatiotemporalBlueNoiseGenerator.Width *
                   PreviewCloudSpatiotemporalBlueNoiseGenerator.Height;
             i++)
        {
            histogram[data[sliceOffset + i]]++;
        }

        Assert.All(histogram, count => Assert.Equal(64, count));
    }

    private static double MeanWrappedAbsoluteDifference(byte[] data, bool temporal)
    {
        var width = PreviewCloudSpatiotemporalBlueNoiseGenerator.Width;
        var height = PreviewCloudSpatiotemporalBlueNoiseGenerator.Height;
        var frames = PreviewCloudSpatiotemporalBlueNoiseGenerator.FrameCount;
        long sum = 0;
        long count = 0;

        for (var z = 0; z < frames; z++)
        {
            var slice = z * width * height;
            var nextSlice = ((z + 1) % frames) * width * height;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = slice + y * width + x;
                    var neighbor = temporal
                        ? nextSlice + y * width + x
                        : slice + y * width + ((x + 1) % width);
                    sum += Math.Abs(data[index] - data[neighbor]);
                    count++;
                }
            }
        }

        return sum / (double)count;
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));
}
