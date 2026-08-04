using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.PreviewGpuAssets;

namespace AutoPBR.App.Tests;

public sealed class PreviewSparseCloudTemplateAssetTests
{
    private static readonly Lazy<IReadOnlyList<PreviewSparseCloudTemplateAssetPayload>>
        Generated = new(PreviewSparseCloudTemplateAssetGenerator.GenerateAll);

    [Fact]
    public void Cq41Contract_FreezesTwelveRg8Templates()
    {
        Assert.Equal(1, PreviewSparseCloudTemplateAssetContract.AssetVersion);
        Assert.Equal(
            "cq4-envelope-v1",
            PreviewSparseCloudTemplateAssetContract.GenerationAbi);
        Assert.Equal(
            (32, 24, 32, 2, 49_152, 589_824L),
            (
                PreviewSparseCloudTemplateAssetContract.Width,
                PreviewSparseCloudTemplateAssetContract.Height,
                PreviewSparseCloudTemplateAssetContract.Depth,
                PreviewSparseCloudTemplateAssetContract.ChannelCount,
                PreviewSparseCloudTemplateAssetContract.ByteLength,
                PreviewSparseCloudTemplateAssetContract.TotalByteLength));
        Assert.Equal(12, PreviewSparseCloudTemplateAssetContract.Assets.Count);
        Assert.Equal(
            12,
            PreviewSparseCloudTemplateAssetContract.Assets
                .Select(asset => (asset.Family, asset.Variant))
                .Distinct()
                .Count());
        Assert.Equal(
            12,
            PreviewSparseCloudTemplateAssetContract.Assets
                .Select(asset => asset.Seed)
                .Distinct()
                .Count());
        Assert.All(
            Enum.GetValues<PreviewSparseCloudTemplateFamily>(),
            family => Assert.Equal(
                [0, 1, 2],
                PreviewSparseCloudTemplateAssetContract.Assets
                    .Where(asset => asset.Family == family)
                    .Select(asset => asset.Variant)
                    .ToArray()));
        Assert.All(
            PreviewSparseCloudTemplateAssetContract.Assets,
            asset =>
            {
                Assert.EndsWith("_rg8_v1.bin", asset.FileName, StringComparison.Ordinal);
                Assert.Equal(64, asset.ExpectedSha256.Length);
            });
    }

    [Fact]
    public void Cq41Generation_MatchesPinnedHashesAndBundledAssets()
    {
        var assetDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AutoPBR.App",
            "Assets",
            "Preview");
        foreach (var payload in Generated.Value)
        {
            Assert.Equal(payload.Descriptor.ByteLength, payload.Rg.Length);
            Assert.True(
                PreviewSparseCloudTemplateAssetContract.ValidatePayload(
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
    public void Cq41Generation_IsByteIdenticalAcrossRuns()
    {
        var second = PreviewSparseCloudTemplateAssetGenerator.GenerateAll();
        Assert.Equal(Generated.Value.Count, second.Count);
        for (var index = 0; index < second.Count; index++)
        {
            Assert.Same(
                Generated.Value[index].Descriptor,
                second[index].Descriptor);
            Assert.Equal(Generated.Value[index].Rg, second[index].Rg);
        }
    }

    [Fact]
    public void Cq41Templates_AreConnectedAndCumulusBasesAreFlat()
    {
        foreach (var payload in Generated.Value)
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
                index / PreviewSparseCloudTemplateAssetContract.Width %
                PreviewSparseCloudTemplateAssetContract.Height);
            Assert.Equal(
                PreviewSparseCloudTemplateAssetContract.CumulusBaseLayer,
                minimumY);
            Assert.True(
                occupied.Count(index =>
                    index / PreviewSparseCloudTemplateAssetContract.Width %
                    PreviewSparseCloudTemplateAssetContract.Height ==
                    PreviewSparseCloudTemplateAssetContract.CumulusBaseLayer) >= 24);
        }
    }

    [Fact]
    public void Cq41Families_PreserveIntendedVerticalAndHorizontalForms()
    {
        var bounds = Generated.Value.ToDictionary(
            payload => (payload.Descriptor.Family, payload.Descriptor.Variant),
            payload => OccupiedBounds(payload.Rg));
        Assert.All(
            Enumerable.Range(0, 3),
            variant =>
            {
                var humilis = bounds[
                    (PreviewSparseCloudTemplateFamily.CumulusHumilis, variant)];
                var mediocris = bounds[
                    (PreviewSparseCloudTemplateFamily.CumulusMediocris, variant)];
                var congestus = bounds[
                    (PreviewSparseCloudTemplateFamily.CumulusCongestus, variant)];
                var stratus = bounds[
                    (PreviewSparseCloudTemplateFamily.Stratus, variant)];

                Assert.True(humilis.MaxY < mediocris.MaxY);
                Assert.True(mediocris.MaxY < congestus.MaxY);
                Assert.InRange(humilis.MaxY - humilis.MinY, 6, 8);
                Assert.InRange(congestus.MaxY - congestus.MinY, 19, 21);
                Assert.InRange(stratus.MaxY - stratus.MinY, 4, 5);
                Assert.True(stratus.MaxX - stratus.MinX >= 28);
                Assert.True(stratus.MaxZ - stratus.MinZ >= 28);
            });
    }

    [Fact]
    public void Cq41Distance_IsZeroWhenOccupiedAndConservativeForFixedSamples()
    {
        foreach (var payload in Generated.Value)
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
                Decode(sample, out var sx, out var sy, out var sz);
                var minimumSquared = int.MaxValue;
                foreach (var target in occupied)
                {
                    Decode(target, out var tx, out var ty, out var tz);
                    var dx = sx - tx;
                    var dy = sy - ty;
                    var dz = sz - tz;
                    minimumSquared = Math.Min(
                        minimumSquared,
                        dx * dx + dy * dy + dz * dz);
                }

                var stored = payload.Rg[sample * 2 + 1];
                Assert.InRange(stored, (byte)1,
                    (byte)PreviewSparseCloudTemplateAssetContract.MaximumEncodedDistance);
                Assert.True(
                    stored * stored <= minimumSquared,
                    $"{payload.Descriptor.Symbol} distance {stored} exceeded " +
                    $"reference sqrt({minimumSquared}) at {sample}.");
            }
        }
    }

    [Fact]
    public void Cq41Loader_IsAllOrNothingAndRejectsCorruption()
    {
        var files = Generated.Value.ToDictionary(
            payload => payload.Descriptor.FileName,
            payload => payload.Rg,
            StringComparer.Ordinal);
        Assert.True(
            PreviewSparseCloudTemplateAssetLoader.TryLoad(
                ReadAsset,
                out var loaded,
                out var reason),
            reason);
        Assert.Equal(12, loaded.Templates.Count);
        Assert.Equal(
            PreviewSparseCloudTemplateAssetContract.TotalByteLength,
            loaded.ByteLength);

        var corruptDescriptor =
            PreviewSparseCloudTemplateAssetContract.Assets[5];
        var corrupt = files[corruptDescriptor.FileName].ToArray();
        corrupt[corrupt.Length / 2] ^= 0x40;
        files[corruptDescriptor.FileName] = corrupt;
        Assert.False(
            PreviewSparseCloudTemplateAssetLoader.TryLoad(
                ReadAsset,
                out var rejected,
                out var corruptReason));
        Assert.Equal(default, rejected);
        Assert.Contains(
            $"{corruptDescriptor.Symbol}-sha256-mismatch",
            corruptReason,
            StringComparison.Ordinal);

        bool ReadAsset(string fileName, out byte[] data, out string readReason)
        {
            if (files.TryGetValue(fileName, out data!))
            {
                readReason = "loaded";
                return true;
            }

            data = [];
            readReason = "missing";
            return false;
        }
    }

    [Fact]
    public void Cq41BundledLoader_LoadsCompletePinnedV1LibraryHeadlesslyWhenV2IsUnavailable()
    {
        // CA2.4 made v2 the preferred bundled set. This isolates v1 by hiding v2 filenames from
        // the reader, proving the frozen v1 library still loads completely and correctly on its
        // own; the v2-preferred bundled path is covered by PreviewSparseCloudTemplateAssetV2Tests.
        var v2FileNames = PreviewSparseCloudTemplateAssetContractV2.Assets
            .Select(descriptor => descriptor.FileName)
            .ToHashSet(StringComparer.Ordinal);
        bool ReadIgnoringV2(string fileName, out byte[] data, out string reason)
        {
            if (v2FileNames.Contains(fileName))
            {
                data = [];
                reason = "missing";
                return false;
            }

            return PreviewCloudBakedAssetLoader.TryLoadBundledRaw(fileName, out data, out reason);
        }

        Assert.True(
            PreviewSparseCloudTemplateAssetLoader.TryLoad(
                ReadIgnoringV2,
                out var loaded,
                out var reason),
            reason);
        Assert.Equal(
            PreviewSparseCloudTemplateAssetContract.AssetVersion,
            loaded.AssetVersion);
        Assert.Equal(
            PreviewSparseCloudTemplateAssetContract.GenerationAbi,
            loaded.GenerationAbi);
        Assert.Equal(12, loaded.Templates.Count);
        Assert.Equal(
            PreviewSparseCloudTemplateAssetContract.TotalByteLength,
            loaded.ByteLength);
    }

    [Fact]
    public void Cq41BuildTarget_DeclaresEveryTemplateOutput()
    {
        var project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AutoPBR.App",
            "AutoPBR.App.csproj"));
        Assert.All(
            PreviewSparseCloudTemplateAssetContract.Assets,
            descriptor => Assert.Contains(
                $"<_PreviewCloudAssetOutput Include=\"$(ProjectDir)Assets\\Preview\\" +
                $"{descriptor.FileName}\" />",
                project,
                StringComparison.Ordinal));
    }

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
            Decode(current, out var x, out var y, out var z);
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
            if (x < 0 || x >= PreviewSparseCloudTemplateAssetContract.Width ||
                y < 0 || y >= PreviewSparseCloudTemplateAssetContract.Height ||
                z < 0 || z >= PreviewSparseCloudTemplateAssetContract.Depth)
            {
                return;
            }

            var index =
                (z * PreviewSparseCloudTemplateAssetContract.Height + y) *
                PreviewSparseCloudTemplateAssetContract.Width + x;
            if (visited[index] || rg[index * 2] == 0)
            {
                return;
            }

            visited[index] = true;
            queue.Enqueue(index);
        }
    }

    private static (int MinX, int MaxX, int MinY, int MaxY, int MinZ, int MaxZ)
        OccupiedBounds(byte[] rg)
    {
        var minX = PreviewSparseCloudTemplateAssetContract.Width;
        var minY = PreviewSparseCloudTemplateAssetContract.Height;
        var minZ = PreviewSparseCloudTemplateAssetContract.Depth;
        var maxX = -1;
        var maxY = -1;
        var maxZ = -1;
        for (var index = 0; index < rg.Length / 2; index++)
        {
            if (rg[index * 2] == 0)
            {
                continue;
            }

            Decode(index, out var x, out var y, out var z);
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            minZ = Math.Min(minZ, z);
            maxZ = Math.Max(maxZ, z);
        }

        return (minX, maxX, minY, maxY, minZ, maxZ);
    }

    private static void Decode(int index, out int x, out int y, out int z)
    {
        x = index % PreviewSparseCloudTemplateAssetContract.Width;
        index /= PreviewSparseCloudTemplateAssetContract.Width;
        y = index % PreviewSparseCloudTemplateAssetContract.Height;
        z = index / PreviewSparseCloudTemplateAssetContract.Height;
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
