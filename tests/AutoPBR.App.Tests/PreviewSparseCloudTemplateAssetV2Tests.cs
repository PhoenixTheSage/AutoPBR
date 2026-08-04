using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.PreviewGpuAssets;

namespace AutoPBR.App.Tests;

/// <summary>
/// CA2.4 coverage for the parallel asymmetric v2 sparse-cloud envelope ABI. These tests never
/// touch <see cref="PreviewSparseCloudTemplateAssetContract"/> (frozen v1) bytes or hashes; they
/// only prove that v2 is internally consistent, visibly asymmetric, and falls back cleanly.
/// </summary>
public sealed class PreviewSparseCloudTemplateAssetV2Tests
{
    private static readonly Lazy<IReadOnlyList<PreviewSparseCloudTemplateAssetPayload>>
        GeneratedV2 = new(PreviewSparseCloudTemplateAssetGenerator.GenerateAllV2);

    [Fact]
    public void Ca24Contract_FreezesTwelveRg8TemplatesDistinctFromV1()
    {
        Assert.Equal(2, PreviewSparseCloudTemplateAssetContractV2.AssetVersion);
        Assert.Equal(
            "cq4-envelope-v2",
            PreviewSparseCloudTemplateAssetContractV2.GenerationAbi);
        Assert.Equal(
            (32, 24, 32, 2, 49_152, 589_824L),
            (
                PreviewSparseCloudTemplateAssetContractV2.Width,
                PreviewSparseCloudTemplateAssetContractV2.Height,
                PreviewSparseCloudTemplateAssetContractV2.Depth,
                PreviewSparseCloudTemplateAssetContractV2.ChannelCount,
                PreviewSparseCloudTemplateAssetContractV2.ByteLength,
                PreviewSparseCloudTemplateAssetContractV2.TotalByteLength));
        Assert.Equal(12, PreviewSparseCloudTemplateAssetContractV2.Assets.Count);
        Assert.Equal(
            12,
            PreviewSparseCloudTemplateAssetContractV2.Assets
                .Select(asset => (asset.Family, asset.Variant))
                .Distinct()
                .Count());
        Assert.All(
            Enum.GetValues<PreviewSparseCloudTemplateFamily>(),
            family => Assert.Equal(
                [0, 1, 2],
                PreviewSparseCloudTemplateAssetContractV2.Assets
                    .Where(asset => asset.Family == family)
                    .Select(asset => asset.Variant)
                    .ToArray()));
        Assert.All(
            PreviewSparseCloudTemplateAssetContractV2.Assets,
            asset =>
            {
                Assert.Equal(2, asset.Version);
                Assert.EndsWith("_rg8_v2.bin", asset.FileName, StringComparison.Ordinal);
                Assert.Equal(64, asset.ExpectedSha256.Length);
            });

        var v2Seeds = PreviewSparseCloudTemplateAssetContractV2.Assets
            .Select(asset => asset.Seed)
            .ToArray();
        Assert.Equal(12, v2Seeds.Distinct().Count());
        var v1Seeds = PreviewSparseCloudTemplateAssetContract.Assets
            .Select(asset => asset.Seed)
            .ToHashSet();
        Assert.Empty(v1Seeds.Intersect(v2Seeds));
        var v1FileNames = PreviewSparseCloudTemplateAssetContract.Assets
            .Select(asset => asset.FileName)
            .ToHashSet(StringComparer.Ordinal);
        var v2FileNames = PreviewSparseCloudTemplateAssetContractV2.Assets
            .Select(asset => asset.FileName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(v1FileNames.Intersect(v2FileNames));
    }

    [Fact]
    public void Ca24Generation_MatchesPinnedHashesAndBundledAssets()
    {
        var assetDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AutoPBR.App",
            "Assets",
            "Preview");
        foreach (var payload in GeneratedV2.Value)
        {
            Assert.Equal(payload.Descriptor.ByteLength, payload.Rg.Length);
            Assert.True(
                PreviewSparseCloudTemplateAssetContractV2.ValidatePayload(
                    payload.Descriptor,
                    payload.Rg,
                    out var reason),
                reason);
            Assert.Equal(
                payload.Descriptor.ExpectedSha256,
                PreviewSparseCloudTemplateAssetGenerator.ComputeSha256Hex(payload.Rg));
            Assert.True(
                PreviewSparseCloudTemplateAssetGenerator.HasExpectedHash(
                    payload.Descriptor,
                    payload.Rg));
            Assert.Equal(
                payload.Rg,
                File.ReadAllBytes(Path.Combine(
                    assetDirectory,
                    payload.Descriptor.FileName)));
        }
    }

    [Fact]
    public void Ca24Generation_IsByteIdenticalAcrossRuns()
    {
        var second = PreviewSparseCloudTemplateAssetGenerator.GenerateAllV2();
        Assert.Equal(GeneratedV2.Value.Count, second.Count);
        for (var index = 0; index < second.Count; index++)
        {
            Assert.Same(
                GeneratedV2.Value[index].Descriptor,
                second[index].Descriptor);
            Assert.Equal(GeneratedV2.Value[index].Rg, second[index].Rg);
        }
    }

    [Fact]
    public void Ca24Templates_AreConnectedAndCumulusBasesAreFlat()
    {
        foreach (var payload in GeneratedV2.Value)
        {
            var occupied = Enumerable.Range(
                    0,
                    payload.Rg.Length / 2)
                .Where(index => payload.Rg[index * 2] > 0)
                .ToArray();
            Assert.InRange(occupied.Length, 128, payload.Rg.Length / 3);
            Assert.Equal(occupied.Length, ConnectedVoxelCount(payload.Rg, occupied[0]));

            if (payload.Descriptor.Family ==
                PreviewSparseCloudTemplateFamily.Stratus)
            {
                continue;
            }

            var minimumY = occupied.Min(index =>
                index / PreviewSparseCloudTemplateAssetContractV2.Width %
                PreviewSparseCloudTemplateAssetContractV2.Height);
            Assert.Equal(
                PreviewSparseCloudTemplateAssetContractV2.CumulusBaseLayer,
                minimumY);
            Assert.True(
                occupied.Count(index =>
                    index / PreviewSparseCloudTemplateAssetContractV2.Width %
                    PreviewSparseCloudTemplateAssetContractV2.Height ==
                    PreviewSparseCloudTemplateAssetContractV2.CumulusBaseLayer) >= 24);
        }
    }

    [Fact]
    public void Ca24Templates_AreVisiblyAsymmetricAboutTheVolumeCenter()
    {
        const double CenterX = (PreviewSparseCloudTemplateAssetContractV2.Width - 1) / 2.0;
        const double CenterZ = (PreviewSparseCloudTemplateAssetContractV2.Depth - 1) / 2.0;
        const double MinimumCenterOfMassOffsetVoxels = 1.0;

        Assert.All(
            GeneratedV2.Value,
            payload =>
            {
                var occupied = Enumerable.Range(0, payload.Rg.Length / 2)
                    .Where(index => payload.Rg[index * 2] > 0)
                    .ToArray();
                var comX = occupied.Average(index => Decode(index).X);
                var comZ = occupied.Average(index => Decode(index).Z);
                var offset = Math.Sqrt(
                    (comX - CenterX) * (comX - CenterX) +
                    (comZ - CenterZ) * (comZ - CenterZ));
                Assert.True(
                    offset >= MinimumCenterOfMassOffsetVoxels,
                    $"{payload.Descriptor.FileName} center-of-mass offset {offset:F3} " +
                    $"voxels did not clear the CA2.4 asymmetry floor.");
            });
    }

    [Fact]
    public void Ca24Templates_ExposeAMissingRingSectorInTheOuterSkirt()
    {
        const double CenterX = (PreviewSparseCloudTemplateAssetContractV2.Width - 1) / 2.0;
        const double CenterZ = (PreviewSparseCloudTemplateAssetContractV2.Depth - 1) / 2.0;
        const double OuterRadiusVoxels = 6.0;

        Assert.All(
            GeneratedV2.Value,
            payload =>
            {
                var octantCounts = new int[8];
                for (var index = 0; index < payload.Rg.Length / 2; index++)
                {
                    if (payload.Rg[index * 2] == 0)
                    {
                        continue;
                    }

                    var (x, _, z) = Decode(index);
                    var dx = x - CenterX;
                    var dz = z - CenterZ;
                    if (dx * dx + dz * dz < OuterRadiusVoxels * OuterRadiusVoxels)
                    {
                        continue;
                    }

                    var angle = Math.Atan2(dz, dx) * (180.0 / Math.PI);
                    if (angle < 0)
                    {
                        angle += 360.0;
                    }

                    octantCounts[(int)(angle / 45.0) % 8]++;
                }

                Assert.True(
                    octantCounts.Any(count => count == 0) ||
                    octantCounts.Min() * 6 < octantCounts.Max(),
                    $"{payload.Descriptor.FileName} outer skirt octant counts " +
                    $"[{string.Join(',', octantCounts)}] did not show a missing/thin sector.");
            });
    }

    [Fact]
    public void Ca24Distance_IsZeroWhenOccupiedAndConservativeForFixedSamples()
    {
        foreach (var payload in GeneratedV2.Value)
        {
            var occupied = Enumerable.Range(0, payload.Rg.Length / 2)
                .Where(index => payload.Rg[index * 2] > 0)
                .ToArray();
            Assert.All(
                occupied,
                index => Assert.Equal(0, payload.Rg[index * 2 + 1]));

            var emptySamples = Enumerable.Range(0, payload.Rg.Length / 2)
                .Where(index => payload.Rg[index * 2] == 0)
                .Where(index => index % 173 == payload.Descriptor.Variant * 17)
                .Take(48);
            foreach (var sample in emptySamples)
            {
                var (sx, sy, sz) = Decode(sample);
                var minimumSquared = int.MaxValue;
                foreach (var target in occupied)
                {
                    var (tx, ty, tz) = Decode(target);
                    var dx = sx - tx;
                    var dy = sy - ty;
                    var dz = sz - tz;
                    minimumSquared = Math.Min(
                        minimumSquared,
                        dx * dx + dy * dy + dz * dz);
                }

                var stored = payload.Rg[sample * 2 + 1];
                Assert.InRange(stored, (byte)1,
                    (byte)PreviewSparseCloudTemplateAssetContractV2.MaximumEncodedDistance);
                Assert.True(
                    stored * stored <= minimumSquared,
                    $"{payload.Descriptor.FileName} distance {stored} exceeded " +
                    $"reference sqrt({minimumSquared}) at {sample}.");
            }
        }
    }

    [Fact]
    public void Ca24Loader_PrefersCompleteV2SetOverBundledV1()
    {
        var files = BuildCombinedFileTable();
        Assert.True(
            PreviewSparseCloudTemplateAssetLoader.TryLoad(
                ReaderFor(files),
                out var loaded,
                out var reason),
            reason);
        Assert.Equal(2, loaded.AssetVersion);
        Assert.Equal("cq4-envelope-v2", loaded.GenerationAbi);
        Assert.Equal(12, loaded.Templates.Count);
        Assert.All(
            loaded.Templates,
            template => Assert.Equal(2, template.Descriptor.Version));
    }

    [Fact]
    public void Ca24Loader_FallsBackTransactionallyToCompleteV1WhenV2IsCorrupt()
    {
        var files = BuildCombinedFileTable();
        var corruptDescriptor = PreviewSparseCloudTemplateAssetContractV2.Assets[3];
        var corrupt = files[corruptDescriptor.FileName].ToArray();
        corrupt[corrupt.Length / 2] ^= 0x40;
        files[corruptDescriptor.FileName] = corrupt;

        Assert.True(
            PreviewSparseCloudTemplateAssetLoader.TryLoad(
                ReaderFor(files),
                out var loaded,
                out var reason),
            reason);
        Assert.Equal(1, loaded.AssetVersion);
        Assert.Equal("cq4-envelope-v1", loaded.GenerationAbi);
        Assert.Equal(12, loaded.Templates.Count);
        Assert.All(
            loaded.Templates,
            template => Assert.Equal(1, template.Descriptor.Version));
        Assert.Contains("v2-", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Ca24Loader_NeverMixesVersionsAndFailsWhenBothSetsAreInvalid()
    {
        var files = BuildCombinedFileTable();
        var corruptV2 = PreviewSparseCloudTemplateAssetContractV2.Assets[1];
        var corruptV2Bytes = files[corruptV2.FileName].ToArray();
        corruptV2Bytes[0] ^= 0xFF;
        files[corruptV2.FileName] = corruptV2Bytes;

        var corruptV1 = PreviewSparseCloudTemplateAssetContract.Assets[7];
        var corruptV1Bytes = files[corruptV1.FileName].ToArray();
        corruptV1Bytes[0] ^= 0xFF;
        files[corruptV1.FileName] = corruptV1Bytes;

        Assert.False(
            PreviewSparseCloudTemplateAssetLoader.TryLoad(
                ReaderFor(files),
                out var loaded,
                out var reason));
        Assert.Equal(default, loaded);
        Assert.Contains("v2-", reason, StringComparison.Ordinal);
        Assert.Contains("v1-", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Ca24BundledLoader_LoadsCompletePinnedV2LibraryHeadlessly()
    {
        Assert.True(
            PreviewSparseCloudTemplateAssetLoader.TryLoad(
                out var loaded,
                out var reason),
            reason);
        Assert.Equal(2, loaded.AssetVersion);
        Assert.Equal("cq4-envelope-v2", loaded.GenerationAbi);
        Assert.Equal(12, loaded.Templates.Count);
        Assert.Equal(
            PreviewSparseCloudTemplateAssetContractV2.TotalByteLength,
            loaded.ByteLength);
    }

    [Fact]
    public void Ca24BuildTarget_DeclaresEveryV2TemplateOutput()
    {
        var project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AutoPBR.App",
            "AutoPBR.App.csproj"));
        Assert.All(
            PreviewSparseCloudTemplateAssetContractV2.Assets,
            descriptor => Assert.Contains(
                $"<_PreviewCloudAssetOutput Include=\"$(ProjectDir)Assets\\Preview\\" +
                $"{descriptor.FileName}\" />",
                project,
                StringComparison.Ordinal));
    }

    private static Dictionary<string, byte[]> BuildCombinedFileTable()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var payload in GeneratedV2.Value)
        {
            files[payload.Descriptor.FileName] = payload.Rg;
        }

        foreach (var payload in PreviewSparseCloudTemplateAssetGenerator.GenerateAll())
        {
            files[payload.Descriptor.FileName] = payload.Rg;
        }

        return files;
    }

    private static PreviewCloudRawAssetReader ReaderFor(
        Dictionary<string, byte[]> files) =>
        (string fileName, out byte[] data, out string readReason) =>
        {
            if (files.TryGetValue(fileName, out data!))
            {
                readReason = "loaded";
                return true;
            }

            data = [];
            readReason = "missing";
            return false;
        };

    private static int ConnectedVoxelCount(byte[] rg, int start)
    {
        var visited = new bool[rg.Length / 2];
        var queue = new Queue<int>();
        visited[start] = true;
        queue.Enqueue(start);
        var count = 0;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            count++;
            var (x, y, z) = Decode(current);
            Visit(x - 1, y, z);
            Visit(x + 1, y, z);
            Visit(x, y - 1, z);
            Visit(x, y + 1, z);
            Visit(x, y, z - 1);
            Visit(x, y, z + 1);
        }

        return count;

        void Visit(int x, int y, int z)
        {
            if (x < 0 || x >= PreviewSparseCloudTemplateAssetContractV2.Width ||
                y < 0 || y >= PreviewSparseCloudTemplateAssetContractV2.Height ||
                z < 0 || z >= PreviewSparseCloudTemplateAssetContractV2.Depth)
            {
                return;
            }

            var index =
                (z * PreviewSparseCloudTemplateAssetContractV2.Height + y) *
                PreviewSparseCloudTemplateAssetContractV2.Width + x;
            if (visited[index] || rg[index * 2] == 0)
            {
                return;
            }

            visited[index] = true;
            queue.Enqueue(index);
        }
    }

    private static (int X, int Y, int Z) Decode(int index)
    {
        var x = index % PreviewSparseCloudTemplateAssetContractV2.Width;
        index /= PreviewSparseCloudTemplateAssetContractV2.Width;
        var y = index % PreviewSparseCloudTemplateAssetContractV2.Height;
        var z = index / PreviewSparseCloudTemplateAssetContractV2.Height;
        return (x, y, z);
    }

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AutoPBR.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new DirectoryNotFoundException("Repository root not found.");
    }
}
