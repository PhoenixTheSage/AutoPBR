using System.Numerics;

using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class TerrainRegionPackStoreTests
{
    [Fact]
    public async Task Roundtrip_same_input_is_byte_deterministic()
    {
        string firstRoot = NewRoot();
        string secondRoot = NewRoot();
        try
        {
            TerrainLodCacheKey key = CreateKey(4, -3, lod: 2, seed: 41);
            PreviewTerrainChunkMesh mesh = CreateMesh(key.Residency, 7);
            using (var first = new TerrainRegionPackStore(firstRoot))
            using (var second = new TerrainRegionPackStore(secondRoot))
            {
                await first.StoreAsync(key, mesh);
                second.TryStore(key, mesh);

                PreviewTerrainChunkMesh? loaded = await first.LoadAsync(key);
                Assert.NotNull(loaded);
                AssertMeshEqual(mesh, loaded);
                Assert.True(first.TryLoad(key, out PreviewTerrainChunkMesh? syncLoaded));
                AssertMeshEqual(mesh, syncLoaded);

                Assert.Equal(
                    await File.ReadAllBytesAsync(first.ResolvePackPath(key)),
                    await File.ReadAllBytesAsync(second.ResolvePackPath(key)));
                Assert.Equal(
                    await File.ReadAllBytesAsync(first.ResolveIndexPath(key)),
                    await File.ReadAllBytesAsync(second.ResolveIndexPath(key)));
            }
        }
        finally
        {
            DeleteRoot(firstRoot);
            DeleteRoot(secondRoot);
        }
    }

    [Fact]
    public void Fingerprints_are_isolated_in_separate_pack_trees()
    {
        string root = NewRoot();
        try
        {
            TerrainLodCacheKey firstKey = CreateKey(1, 2, lod: 1, seed: 10);
            TerrainLodCacheKey secondKey = CreateKey(1, 2, lod: 1, seed: 11);
            using var store = new TerrainRegionPackStore(root);
            store.TryStore(firstKey, CreateMesh(firstKey.Residency, 1));

            Assert.True(store.Contains(firstKey));
            Assert.False(store.Contains(secondKey));
            Assert.NotEqual(store.ResolvePackPath(firstKey), store.ResolvePackPath(secondKey));

            store.TryStore(secondKey, CreateMesh(secondKey.Residency, 2));
            Assert.True(store.TryLoad(firstKey, out PreviewTerrainChunkMesh? first));
            Assert.True(store.TryLoad(secondKey, out PreviewTerrainChunkMesh? second));
            Assert.NotEqual(first.InterleavedVertices[0], second.InterleavedVertices[0]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Truncated_or_corrupt_tail_and_index_preserve_prior_records()
    {
        string root = NewRoot();
        try
        {
            TerrainLodCacheKey firstKey = CreateKey(3, 4, lod: 2, seed: 77);
            TerrainLodCacheKey secondKey = CreateKey(4, 4, lod: 2, seed: 77);
            string packPath;
            string indexPath;
            long firstRecordLength;
            using (var store = new TerrainRegionPackStore(root))
            {
                store.TryStore(firstKey, CreateMesh(firstKey.Residency, 1));
                packPath = store.ResolvePackPath(firstKey);
                indexPath = store.ResolveIndexPath(firstKey);
                firstRecordLength = new FileInfo(packPath).Length;
                store.TryStore(secondKey, CreateMesh(secondKey.Residency, 2));
            }

            byte[] damagedIndex = File.ReadAllBytes(indexPath);
            damagedIndex[^1] ^= 0x5a;
            File.WriteAllBytes(indexPath, damagedIndex);
            using (var recoveredIndex = new TerrainRegionPackStore(root))
            {
                Assert.True(recoveredIndex.TryLoad(firstKey, out _));
                Assert.True(recoveredIndex.TryLoad(secondKey, out _));
                Assert.True(recoveredIndex.GetStats().Recoveries > 0);
            }

            long fullLength = new FileInfo(packPath).Length;
            using (FileStream file = new(packPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                file.SetLength(fullLength - 9);
            }

            using (var recoveredTruncation = new TerrainRegionPackStore(root))
            {
                Assert.True(recoveredTruncation.TryLoad(firstKey, out _));
                Assert.False(recoveredTruncation.TryLoad(secondKey, out _));
                Assert.Equal(firstRecordLength, new FileInfo(packPath).Length);
                recoveredTruncation.TryStore(secondKey, CreateMesh(secondKey.Residency, 3));
            }

            byte[] bytes = File.ReadAllBytes(packPath);
            bytes[^1] ^= 0xff;
            File.WriteAllBytes(packPath, bytes);
            using var recoveredCorruption = new TerrainRegionPackStore(root);
            Assert.False(recoveredCorruption.TryLoad(secondKey, out _));
            Assert.True(recoveredCorruption.TryLoad(firstKey, out _));
            Assert.Equal(firstRecordLength, new FileInfo(packPath).Length);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Concurrent_readers_and_writer_share_one_pack()
    {
        string root = NewRoot();
        try
        {
            using var store = new TerrainRegionPackStore(root, regionSize: 128);
            TerrainLodCacheKey[] keys = Enumerable.Range(0, 48)
                .Select(i => CreateKey(i, 0, lod: 3, seed: 99))
                .ToArray();

            // ReSharper disable AccessToDisposedClosure
            Task writer = Task.Run(async () =>
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    await store.StoreAsync(keys[i], CreateMesh(keys[i].Residency, i));
                }
            });
            Task[] readers = Enumerable.Range(0, 6)
                .Select(reader => Task.Run(async () =>
                {
                    int iteration = reader;
                    while (!writer.IsCompleted)
                    {
                        _ = await store.LoadAsync(keys[iteration % keys.Length]);
                        iteration += 7;
                    }
                }))
                .ToArray();
            // ReSharper restore AccessToDisposedClosure

            await writer;
            await Task.WhenAll(readers);
            foreach (TerrainLodCacheKey key in keys)
            {
                Assert.True(store.TryLoad(key, out PreviewTerrainChunkMesh? mesh));
                Assert.Equal(key.Residency, mesh.Key);
            }

            Assert.Equal(48, store.GetStats().IndexedEntries);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Byte_limit_evicts_oldest_pack_without_per_hit_file_touches()
    {
        string root = NewRoot();
        try
        {
            TerrainLodCacheKey firstKey = CreateKey(0, 0, lod: 1, seed: 5);
            TerrainLodCacheKey secondKey = CreateKey(1, 0, lod: 1, seed: 5);
            using var store = new TerrainRegionPackStore(root, maxBytes: 300, regionSize: 1);
            store.TryStore(firstKey, CreateMesh(firstKey.Residency, 1));
            DateTime packWriteTime = File.GetLastWriteTimeUtc(store.ResolvePackPath(firstKey));
            DateTime indexWriteTime = File.GetLastWriteTimeUtc(store.ResolveIndexPath(firstKey));
            Assert.True(store.TryLoad(firstKey, out _));
            Assert.Equal(packWriteTime, File.GetLastWriteTimeUtc(store.ResolvePackPath(firstKey)));
            Assert.Equal(indexWriteTime, File.GetLastWriteTimeUtc(store.ResolveIndexPath(firstKey)));

            store.TryStore(secondKey, CreateMesh(secondKey.Residency, 2));

            Assert.False(File.Exists(store.ResolvePackPath(firstKey)));
            Assert.True(store.TryLoad(secondKey, out _));
            TerrainMeshCacheStats stats = store.GetStats();
            Assert.True(stats.PackBytes <= store.MaxBytes);
            Assert.Equal(1, stats.Evictions);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static TerrainLodCacheKey CreateKey(int x, int z, byte lod, int seed)
    {
        var fingerprint = new TerrainLodCacheFingerprint(
            seed,
            4.5f,
            1.25f,
            0.4f,
            0.7f,
            PreviewTerrainGrassMode.BuiltInSingleTop,
            BetterGrassEnabled: true,
            EmitOverlay: false,
            HasStone: true,
            HasSand: true,
            HasGravel: false,
            VegetationIdentity: "test-vegetation",
            BakeRevision: TerrainLodCacheFingerprint.CurrentBakeRevision,
            SmartLeavesEnabled: true);
        return new TerrainLodCacheKey(TerrainResidencyKey.Section(x, z, lod), fingerprint);
    }

    private static PreviewTerrainChunkMesh CreateMesh(TerrainResidencyKey key, int marker)
    {
        float first = marker + 0.25f;
        return new PreviewTerrainChunkMesh
        {
            Key = key,
            Lod = key.Kind,
            InterleavedVertices =
            [
                first, 2, 3, 0, 1, 0, 0, 0, 1, 1, 1, 1,
            ],
            Indices = [0, 0, 0],
            DrawBatches = [new PreviewDrawBatch(0, 3, marker % 3)],
            BoundsCenter = new Vector3(marker, marker + 1, marker + 2),
            BoundsRadius = marker + 4.5f,
            MinRelativeHeight = -marker,
            MaxRelativeHeight = marker + 10,
        };
    }

    private static void AssertMeshEqual(
        PreviewTerrainChunkMesh expected,
        PreviewTerrainChunkMesh actual)
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.Lod, actual.Lod);
        Assert.Equal(expected.InterleavedVertices, actual.InterleavedVertices);
        Assert.Equal(expected.Indices, actual.Indices);
        Assert.Equal(expected.DrawBatches, actual.DrawBatches);
        Assert.Equal(expected.BoundsCenter, actual.BoundsCenter);
        Assert.Equal(expected.BoundsRadius, actual.BoundsRadius);
        Assert.Equal(expected.MinRelativeHeight, actual.MinRelativeHeight);
        Assert.Equal(expected.MaxRelativeHeight, actual.MaxRelativeHeight);
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "autopbr-region-pack-" + Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
