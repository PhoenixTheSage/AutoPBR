using System.Runtime.CompilerServices;

using AutoPBR.PreviewGpuAssets;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudDensityAssetGeneratorTests
{
    private static readonly Lazy<PreviewCloudDensityAssetPayloads> Generated =
        new(PreviewCloudDensityAssetGenerator.GenerateAll);

    [Fact]
    public void Cq2V2Generation_MatchesPinnedHashesAndStrictLengths()
    {
        var generated = Generated.Value;
        AssertAsset(
            PreviewCloudDensityAssetKind.Shape,
            PreviewCloudDensityAssetContract.Shape,
            generated.ShapeRgba,
            PreviewCloudDensityAssetGenerator.ExpectedShapeSha256);
        AssertAsset(
            PreviewCloudDensityAssetKind.Detail,
            PreviewCloudDensityAssetContract.Detail,
            generated.DetailRgba,
            PreviewCloudDensityAssetGenerator.ExpectedDetailSha256);
        AssertAsset(
            PreviewCloudDensityAssetKind.Weather,
            PreviewCloudDensityAssetContract.Weather,
            generated.WeatherRgba,
            PreviewCloudDensityAssetGenerator.ExpectedWeatherSha256);
    }

    [Fact]
    public void Cq2V2BundledAssets_MatchGeneratorAndPinnedHashes()
    {
        var generated = Generated.Value;
        var assetDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AutoPBR.App",
            "Assets",
            "Preview");

        AssertBundledAsset(
            assetDirectory,
            PreviewCloudDensityAssetKind.Shape,
            PreviewCloudDensityAssetContract.Shape,
            generated.ShapeRgba,
            PreviewCloudDensityAssetGenerator.ExpectedShapeSha256);
        AssertBundledAsset(
            assetDirectory,
            PreviewCloudDensityAssetKind.Detail,
            PreviewCloudDensityAssetContract.Detail,
            generated.DetailRgba,
            PreviewCloudDensityAssetGenerator.ExpectedDetailSha256);
        AssertBundledAsset(
            assetDirectory,
            PreviewCloudDensityAssetKind.Weather,
            PreviewCloudDensityAssetContract.Weather,
            generated.WeatherRgba,
            PreviewCloudDensityAssetGenerator.ExpectedWeatherSha256);
    }

    [Fact]
    public void Cq2BuildTarget_DeclaresTheCompleteCloudAssetSet()
    {
        var project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AutoPBR.App",
            "AutoPBR.App.csproj"));
        string[] expectedOutputs =
        [
            "cloud_noise_shape_128.bin",
            "cloud_noise_detail_32.bin",
            "cloud_coverage_256.bin",
            PreviewCloudSpatiotemporalBlueNoiseGenerator.AssetFileName,
            PreviewCloudDensityAssetContract.Shape.FileName,
            PreviewCloudDensityAssetContract.Detail.FileName,
            PreviewCloudDensityAssetContract.Weather.FileName,
        ];

        Assert.All(
            expectedOutputs,
            fileName => Assert.Contains(
                $"<_PreviewCloudAssetOutput Include=\"$(ProjectDir)Assets\\Preview\\{fileName}\" />",
                project,
                StringComparison.Ordinal));
        Assert.Contains(
            "Inputs=\"@(_PreviewCloudAssetGeneratorInput)\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "Outputs=\"@(_PreviewCloudAssetOutput)\"",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Condition=\"!Exists('$(ProjectDir)Assets\\Preview\\cloud_noise_shape_128.bin')",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Cq2V2Generation_IsByteIdenticalAcrossParallelRuns()
    {
        var first = Generated.Value;
        var second = PreviewCloudDensityAssetGenerator.GenerateAll();

        Assert.Equal(first.ShapeRgba, second.ShapeRgba);
        Assert.Equal(first.DetailRgba, second.DetailRgba);
        Assert.Equal(first.WeatherRgba, second.WeatherRgba);
    }

    [Fact]
    public void Cq2V2Volumes_HaveExactPeriodicEdgesOnEveryAxis()
    {
        AssertVolumeEdges(
            Generated.Value.ShapeRgba,
            PreviewCloudDensityAssetContract.Shape.Width);
        AssertVolumeEdges(
            Generated.Value.DetailRgba,
            PreviewCloudDensityAssetContract.Detail.Width);
    }

    [Fact]
    public void Cq2V2Weather_HasExactPeriodicEdgesOnBothAxes()
    {
        var descriptor = PreviewCloudDensityAssetContract.Weather;
        var rgba = Generated.Value.WeatherRgba;

        for (var y = 0; y < descriptor.Height; y++)
        {
            AssertTexelEqual(
                rgba,
                PixelOffset(0, y, descriptor.Width),
                PixelOffset(descriptor.Width - 1, y, descriptor.Width));
        }

        for (var x = 0; x < descriptor.Width; x++)
        {
            AssertTexelEqual(
                rgba,
                PixelOffset(x, 0, descriptor.Width),
                PixelOffset(x, descriptor.Height - 1, descriptor.Width));
        }
    }

    [Fact]
    public void Cq2V2Channels_HaveUsefulDistributionsAndAreNotDuplicates()
    {
        AssertChannelDistributions(Generated.Value.ShapeRgba, "shape");
        AssertChannelDistributions(Generated.Value.DetailRgba, "detail");
        AssertChannelDistributions(Generated.Value.WeatherRgba, "weather");

        AssertNoHighlyCorrelatedChannels(Generated.Value.ShapeRgba, "shape");
        AssertNoHighlyCorrelatedChannels(Generated.Value.DetailRgba, "detail");
        AssertNoHighlyCorrelatedChannels(Generated.Value.WeatherRgba, "weather");
    }

    [Fact]
    public void Cq2AtomicOutput_FailedCommitPreservesPriorValidAsset()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "AutoPBR-CQ2-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var destination = Path.Combine(
            testRoot,
            PreviewCloudDensityAssetContract.Detail.FileName);
        byte[] prior = [1, 3, 5, 7];
        byte[] replacement = [2, 4, 6, 8];

        try
        {
            File.WriteAllBytes(destination, prior);
            Assert.Throws<IOException>(() =>
                PreviewCloudAssetFileWriter.WriteAtomically(
                    destination,
                    replacement,
                    static (temporaryPath, destinationPath) =>
                    {
                        Assert.True(File.Exists(temporaryPath));
                        Assert.True(File.Exists(destinationPath));
                        throw new IOException("intentional CQ2 commit failure");
                    }));

            Assert.Equal(prior, File.ReadAllBytes(destination));
            Assert.Empty(Directory.GetFiles(
                testRoot,
                "*.tmp-*",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void AssertAsset(
        PreviewCloudDensityAssetKind kind,
        PreviewCloudDensityAssetDescriptor descriptor,
        byte[] rgba,
        string expectedHash)
    {
        Assert.Equal(descriptor.ByteLength, rgba.Length);
        Assert.True(
            PreviewCloudDensityAssetContract.ValidatePayload(
                descriptor,
                rgba,
                out var validationReason),
            validationReason);
        Assert.Equal(expectedHash, PreviewCloudDensityAssetGenerator.ComputeSha256Hex(rgba));
        Assert.True(PreviewCloudDensityAssetGenerator.HasExpectedHash(kind, rgba));
    }

    private static void AssertBundledAsset(
        string assetDirectory,
        PreviewCloudDensityAssetKind kind,
        PreviewCloudDensityAssetDescriptor descriptor,
        byte[] generated,
        string expectedHash)
    {
        var bundled = File.ReadAllBytes(Path.Combine(assetDirectory, descriptor.FileName));
        Assert.Equal(generated, bundled);
        AssertAsset(kind, descriptor, bundled, expectedHash);
    }

    private static void AssertVolumeEdges(byte[] rgba, int size)
    {
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                AssertTexelEqual(
                    rgba,
                    VoxelOffset(0, y, z, size),
                    VoxelOffset(size - 1, y, z, size));
            }
        }

        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                AssertTexelEqual(
                    rgba,
                    VoxelOffset(x, 0, z, size),
                    VoxelOffset(x, size - 1, z, size));
            }
        }

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                AssertTexelEqual(
                    rgba,
                    VoxelOffset(x, y, 0, size),
                    VoxelOffset(x, y, size - 1, size));
            }
        }
    }

    private static void AssertChannelDistributions(byte[] rgba, string label)
    {
        for (var channel = 0; channel < 4; channel++)
        {
            var minimum = byte.MaxValue;
            var maximum = byte.MinValue;
            var distinct = new bool[256];
            for (var offset = channel; offset < rgba.Length; offset += 4)
            {
                var value = rgba[offset];
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                distinct[value] = true;
            }

            var distinctCount = distinct.Count(present => present);
            Assert.True(
                maximum - minimum >= 96,
                $"{label} channel {channel} range was only {minimum}..{maximum}.");
            Assert.True(
                distinctCount >= 96,
                $"{label} channel {channel} used only {distinctCount} byte values.");
        }
    }

    private static void AssertNoHighlyCorrelatedChannels(byte[] rgba, string label)
    {
        const int sampleStridePixels = 17;
        for (var first = 0; first < 4; first++)
        {
            for (var second = first + 1; second < 4; second++)
            {
                long count = 0;
                double sumFirst = 0;
                double sumSecond = 0;
                double sumFirstSquared = 0;
                double sumSecondSquared = 0;
                double sumProduct = 0;
                for (var offset = 0;
                     offset < rgba.Length;
                     offset += 4 * sampleStridePixels)
                {
                    var a = rgba[offset + first];
                    var b = rgba[offset + second];
                    count++;
                    sumFirst += a;
                    sumSecond += b;
                    sumFirstSquared += a * a;
                    sumSecondSquared += b * b;
                    sumProduct += a * b;
                }

                var numerator = count * sumProduct - sumFirst * sumSecond;
                var denominator = Math.Sqrt(
                    Math.Max(count * sumFirstSquared - sumFirst * sumFirst, 0) *
                    Math.Max(count * sumSecondSquared - sumSecond * sumSecond, 0));
                var correlation = denominator > 1e-9 ? numerator / denominator : 1.0;
                Assert.True(
                    Math.Abs(correlation) < 0.97,
                    $"{label} channels {first}/{second} correlation was {correlation:0.000}.");
            }
        }
    }

    private static void AssertTexelEqual(byte[] rgba, int firstOffset, int secondOffset)
    {
        Assert.True(
            rgba.AsSpan(firstOffset, 4).SequenceEqual(rgba.AsSpan(secondOffset, 4)),
            $"Periodic texels differ at byte offsets {firstOffset} and {secondOffset}.");
    }

    private static int PixelOffset(int x, int y, int width) =>
        (y * width + x) * 4;

    private static int VoxelOffset(int x, int y, int z, int size) =>
        ((z * size + y) * size + x) * 4;

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));
}
