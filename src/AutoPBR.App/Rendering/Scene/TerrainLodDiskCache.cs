using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using AutoPBR.Preview;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Legacy per-file LOD mesh cache (v2). Production streaming uses
/// <see cref="TerrainRegionPackStore"/> (`terrain-lod-cache-v3`). Retained for parity tests
/// until removal after acceptance gates.
/// </summary>
[Obsolete("Use TerrainRegionPackStore (terrain-lod-cache-v3).")]
public sealed class TerrainLodDiskCache
{
    public const int MeshFormatVersion = 2;

    private static readonly byte[] MagicBytes = "APLOD1\0"u8.ToArray();

    private readonly string _rootDir;
    private readonly object _gate = new();
    private long _successfulStoreCount;
    private long _storeFailureCount;
    private string? _lastStoreFailure;

    public TerrainLodDiskCache(string? rootDir = null)
    {
        _rootDir = rootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutoPBR",
            "terrain-lod-cache");
    }

    public string RootDirectory => _rootDir;

    public long SuccessfulStoreCount => Interlocked.Read(ref _successfulStoreCount);

    public long StoreFailureCount => Interlocked.Read(ref _storeFailureCount);

    public string LastStoreFailure => Volatile.Read(ref _lastStoreFailure) ?? "none";

    public bool Contains(in TerrainLodCacheKey key) => File.Exists(ResolvePath(key));

    public bool TryLoad(in TerrainLodCacheKey key, [NotNullWhen(true)] out PreviewTerrainChunkMesh? mesh)
    {
        mesh = null;
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!TryReadMesh(fs, key, out mesh))
            {
                return false;
            }

            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch
            {
                // Best-effort LRU touch.
            }

            return mesh is not null;
        }
        catch
        {
            mesh = null;
            return false;
        }
    }

    public void TryStore(in TerrainLodCacheKey key, PreviewTerrainChunkMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!mesh.Key.IsLod || mesh.Key != key.Residency)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_rootDir);
            var path = ResolvePath(key);
            var temp = path + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                WriteMesh(fs, key, mesh);
            }

            File.Copy(temp, path, overwrite: true);
            File.Delete(temp);
            Interlocked.Increment(ref _successfulStoreCount);
            EvictOverflow();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _storeFailureCount);
            Volatile.Write(ref _lastStoreFailure, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (Directory.Exists(_rootDir))
                {
                    Directory.Delete(_rootDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    public static void ClearAll()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AutoPBR",
                "terrain-lod-cache");
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    public string ResolvePath(in TerrainLodCacheKey key)
    {
        var name = BuildFileName(key);
        return Path.Combine(_rootDir, name);
    }

    internal static string BuildFileName(in TerrainLodCacheKey key)
    {
        var fp = key.Fingerprint;
        // Full fingerprint hash so seed-colliding worldgen / grass / veg settings never share a file.
        var contentHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fp.ContentIdentity)));
        var contentHash = contentHex.Length >= 16 ? contentHex[..16] : contentHex;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"lod{key.Residency.LodLevel}_x{key.Residency.X}_z{key.Residency.Z}_r{fp.BakeRevision}_f{MeshFormatVersion}_{contentHash}.bin");
    }

    private void EvictOverflow()
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(_rootDir))
                {
                    return;
                }

                var files = Directory.GetFiles(_rootDir, "*.bin");
                if (files.Length == 0)
                {
                    return;
                }

                Array.Sort(files, static (a, b) =>
                {
                    var ta = SafeLastAccess(a);
                    var tb = SafeLastAccess(b);
                    return ta.CompareTo(tb);
                });

                long total = 0;
                foreach (var f in files)
                {
                    total += SafeLength(f);
                }

                var i = 0;
                while ((files.Length - i > PreviewStageConstants.TerrainLodDiskCacheMaxEntries ||
                        total > PreviewStageConstants.TerrainLodDiskCacheMaxBytes) &&
                       i < files.Length)
                {
                    var victim = files[i++];
                    total -= SafeLength(victim);
                    try
                    {
                        File.Delete(victim);
                    }
                    catch
                    {
                        // Skip locked files.
                    }
                }
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    private static DateTime SafeLastAccess(string path)
    {
        try
        {
            return File.GetLastAccessTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    internal static void WriteMesh(Stream stream, in TerrainLodCacheKey key, PreviewTerrainChunkMesh mesh)
    {
        using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        bw.Write(MagicBytes);
        bw.Write(MeshFormatVersion);
        bw.Write(key.Residency.X);
        bw.Write(key.Residency.Z);
        bw.Write(key.Residency.LodLevel);

        var fp = key.Fingerprint;
        bw.Write(fp.Seed);
        bw.Write(fp.BiomeSize);
        bw.Write(fp.Amplification);
        bw.Write(fp.ErosionStrength);
        bw.Write(fp.Continentalness);
        bw.Write((int)fp.Mode);
        bw.Write(fp.BetterGrassEnabled);
        bw.Write(fp.EmitOverlay);
        bw.Write(fp.HasStone);
        bw.Write(fp.HasSand);
        bw.Write(fp.HasGravel);
        bw.Write(fp.VegetationIdentity ?? "");
        bw.Write(fp.BakeRevision);
        bw.Write(fp.SmartLeavesEnabled);

        bw.Write(mesh.BoundsCenter.X);
        bw.Write(mesh.BoundsCenter.Y);
        bw.Write(mesh.BoundsCenter.Z);
        bw.Write(mesh.BoundsRadius);
        bw.Write(mesh.MinRelativeHeight);
        bw.Write(mesh.MaxRelativeHeight);

        var batches = mesh.DrawBatches ?? [];
        bw.Write(batches.Length);
        foreach (var b in batches)
        {
            bw.Write(b.FirstIndex);
            bw.Write(b.IndexCount);
            bw.Write(b.MaterialIndex);
        }

        var verts = mesh.InterleavedVertices;
        bw.Write(verts.Length);
        foreach (var v in verts)
        {
            bw.Write(v);
        }

        var indices = mesh.Indices;
        bw.Write(indices.Length);
        foreach (var idx in indices)
        {
            bw.Write(idx);
        }

        bw.Flush();
    }

    internal static bool TryReadMesh(
        Stream stream,
        in TerrainLodCacheKey expectedKey,
        [NotNullWhen(true)] out PreviewTerrainChunkMesh? mesh)
    {
        mesh = null;
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var magic = br.ReadBytes(MagicBytes.Length);
        if (magic.Length != MagicBytes.Length || !magic.AsSpan().SequenceEqual(MagicBytes))
        {
            return false;
        }

        var format = br.ReadInt32();
        if (format != MeshFormatVersion)
        {
            return false;
        }

        var x = br.ReadInt32();
        var z = br.ReadInt32();
        var lod = br.ReadByte();
        var residency = new TerrainResidencyKey(x, z, lod);
        if (residency != expectedKey.Residency)
        {
            return false;
        }

        var seed = br.ReadInt32();
        var biomeSize = br.ReadSingle();
        var amplification = br.ReadSingle();
        var erosion = br.ReadSingle();
        var continentalness = br.ReadSingle();
        var mode = (PreviewTerrainGrassMode)br.ReadInt32();
        var betterGrass = br.ReadBoolean();
        var emitOverlay = br.ReadBoolean();
        var hasStone = br.ReadBoolean();
        var hasSand = br.ReadBoolean();
        var hasGravel = br.ReadBoolean();
        var vegId = br.ReadString();
        var bakeRevision = br.ReadInt32();
        var smartLeaves = br.ReadBoolean();
        var fingerprint = new TerrainLodCacheFingerprint(
            seed,
            biomeSize,
            amplification,
            erosion,
            continentalness,
            mode,
            betterGrass,
            emitOverlay,
            hasStone,
            hasSand,
            hasGravel,
            vegId,
            bakeRevision,
            smartLeaves);
        if (!FingerprintEquals(fingerprint, expectedKey.Fingerprint))
        {
            return false;
        }

        var cx = br.ReadSingle();
        var cy = br.ReadSingle();
        var cz = br.ReadSingle();
        var radius = br.ReadSingle();
        var minH = br.ReadInt32();
        var maxH = br.ReadInt32();

        var batchCount = br.ReadInt32();
        if (batchCount < 0 || batchCount > 4096)
        {
            return false;
        }

        var batches = new PreviewDrawBatch[batchCount];
        for (var i = 0; i < batchCount; i++)
        {
            batches[i] = new PreviewDrawBatch(br.ReadInt32(), br.ReadInt32(), br.ReadInt32());
        }

        var vertCount = br.ReadInt32();
        if (vertCount < 0 || vertCount > 50_000_000 || vertCount % 12 != 0)
        {
            return false;
        }

        var verts = new float[vertCount];
        for (var i = 0; i < vertCount; i++)
        {
            verts[i] = br.ReadSingle();
        }

        var indexCount = br.ReadInt32();
        if (indexCount < 0 || indexCount > 50_000_000)
        {
            return false;
        }

        var indices = new uint[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            indices[i] = br.ReadUInt32();
        }

        mesh = new PreviewTerrainChunkMesh
        {
            Key = residency,
            Lod = residency.Kind,
            InterleavedVertices = verts,
            Indices = indices,
            DrawBatches = batches,
            BoundsCenter = new Vector3(cx, cy, cz),
            BoundsRadius = radius,
            MinRelativeHeight = minH,
            MaxRelativeHeight = maxH,
        };
        return true;
    }

    private static bool FingerprintEquals(in TerrainLodCacheFingerprint a, in TerrainLodCacheFingerprint b) =>
        a.Seed == b.Seed &&
        a.BiomeSize.Equals(b.BiomeSize) &&
        a.Amplification.Equals(b.Amplification) &&
        a.ErosionStrength.Equals(b.ErosionStrength) &&
        a.Continentalness.Equals(b.Continentalness) &&
        a.Mode == b.Mode &&
        a.BetterGrassEnabled == b.BetterGrassEnabled &&
        a.EmitOverlay == b.EmitOverlay &&
        a.HasStone == b.HasStone &&
        a.HasSand == b.HasSand &&
        a.HasGravel == b.HasGravel &&
        string.Equals(a.VegetationIdentity, b.VegetationIdentity, StringComparison.Ordinal) &&
        a.BakeRevision == b.BakeRevision &&
        a.SmartLeavesEnabled == b.SmartLeavesEnabled;
}
