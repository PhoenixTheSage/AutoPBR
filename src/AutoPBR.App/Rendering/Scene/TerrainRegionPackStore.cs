using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Version 3 append-only terrain mesh cache. Records are partitioned by complete bake
/// fingerprint, LOD, and a fixed square region so lookups require no directory scan.
/// </summary>
public sealed class TerrainRegionPackStore : ITerrainMeshCache
{
    public const int FormatVersion = 3;
    public const int DefaultRegionSize = 32;
    public const long DefaultMaxBytes = 4L * 1024 * 1024 * 1024;

    private const int RecordHeaderSize = 68;
    private const int PayloadHeaderSize = 36;
    private const int IndexHeaderSize = 64;
    private const int IndexEntrySize = 28;
    private const int FingerprintHashSize = 32;
    private const int MaximumElementCount = 50_000_000;
    private const int MaximumBatchCount = 4096;
    private const int MaximumPayloadBytes = 1024 * 1024 * 1024;

    private static readonly uint[] SCrcTable = BuildCrcTable();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SPackWriters =
        new(StringComparer.OrdinalIgnoreCase);

    private static ReadOnlySpan<byte> RecordMagic => "APTRPK3\0"u8;

    private static ReadOnlySpan<byte> IndexMagic => "APTRIX3\0"u8;

    private readonly string _rootDirectory;
    private readonly long _maxBytes;
    private readonly int _regionSize;
    private readonly ConcurrentDictionary<string, PackState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _catalogGate = new();
    private readonly Dictionary<string, PackMetadata> _catalog =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ReaderWriterLockSlim _lifecycle = new();

    private bool _catalogLoaded;
    private long _accessSequence;
    private long _hits;
    private long _misses;
    private long _stores;
    private long _storeFailures;
    private long _recoveries;
    private long _evictions;
    private int _disposed;

    public TerrainRegionPackStore(
        string? rootDirectory = null,
        long maxBytes = DefaultMaxBytes,
        int regionSize = DefaultRegionSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(regionSize);

        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "Terrain region packs use little-endian bulk array encoding.");
        }

        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutoPBR",
            "terrain-lod-cache-v3");
        _maxBytes = maxBytes;
        _regionSize = regionSize;
    }

    public string RootDirectory => _rootDirectory;

    public long MaxBytes => _maxBytes;

    public int RegionSize => _regionSize;

    public TerrainMeshCacheStats Stats => GetStats();

    public bool Contains(in TerrainLodCacheKey key)
    {
        ThrowIfDisposed();
        try
        {
            PackState state = GetState(key);
            IndexSnapshot snapshot = GetIndexForRead(state);
            if (!snapshot.Entries.ContainsKey(key.Residency))
            {
                snapshot = RefreshIndexAfterMiss(state, snapshot);
            }

            bool found = snapshot.Entries.ContainsKey(key.Residency);
            if (found)
            {
                TouchPack(state.PackPath);
            }

            return found;
        }
        catch
        {
            return false;
        }
    }

    public bool TryLoad(
        in TerrainLodCacheKey key,
        [NotNullWhen(true)] out PreviewTerrainChunkMesh? mesh)
    {
        ThrowIfDisposed();
        mesh = null;
        try
        {
            PackState state = GetState(key);
            IndexSnapshot snapshot = GetIndexForRead(state);
            if (!snapshot.Entries.TryGetValue(key.Residency, out RecordLocation location))
            {
                snapshot = RefreshIndexAfterMiss(state, snapshot);
                if (!snapshot.Entries.TryGetValue(key.Residency, out location))
                {
                    Interlocked.Increment(ref _misses);
                    return false;
                }
            }

            if (!TryReadRecord(state, key, location, out mesh))
            {
                Interlocked.Increment(ref _misses);
                RecoverState(state);
                mesh = null;
                return false;
            }

            Interlocked.Increment(ref _hits);
            TouchPack(state.PackPath);
            return true;
        }
        catch
        {
            Interlocked.Increment(ref _misses);
            mesh = null;
            return false;
        }
    }

    public bool TryGet(
        in TerrainLodCacheKey key,
        [NotNullWhen(true)] out PreviewTerrainChunkMesh? mesh) =>
        TryLoad(key, out mesh);

    public async ValueTask<PreviewTerrainChunkMesh?> LoadAsync(
        TerrainLodCacheKey key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            PackState state = GetState(key);
            IndexSnapshot snapshot = GetIndexForRead(state);
            if (!snapshot.Entries.TryGetValue(key.Residency, out RecordLocation location))
            {
                snapshot = RefreshIndexAfterMiss(state, snapshot);
                if (!snapshot.Entries.TryGetValue(key.Residency, out location))
                {
                    Interlocked.Increment(ref _misses);
                    return null;
                }
            }

            PreviewTerrainChunkMesh? mesh = await ReadRecordAsync(
                state,
                key,
                location,
                cancellationToken).ConfigureAwait(false);
            if (mesh is null)
            {
                Interlocked.Increment(ref _misses);
                RecoverState(state);
                return null;
            }

            Interlocked.Increment(ref _hits);
            TouchPack(state.PackPath);
            return mesh;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    public void TryStore(in TerrainLodCacheKey key, PreviewTerrainChunkMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ThrowIfDisposed();
        if (mesh.Key != key.Residency)
        {
            return;
        }

        PackState state = GetState(key);
        state.Writer.Wait();
        try
        {
            StoreUnderWriter(state, key, mesh);
        }
        catch
        {
            Interlocked.Increment(ref _storeFailures);
        }
        finally
        {
            state.Writer.Release();
        }
    }

    public void Store(in TerrainLodCacheKey key, PreviewTerrainChunkMesh mesh) =>
        TryStore(key, mesh);

    public async ValueTask StoreAsync(
        TerrainLodCacheKey key,
        PreviewTerrainChunkMesh mesh,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ThrowIfDisposed();
        if (mesh.Key != key.Residency)
        {
            return;
        }

        PackState state = GetState(key);
        await state.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoreUnderWriter(state, key, mesh);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Interlocked.Increment(ref _storeFailures);
        }
        finally
        {
            state.Writer.Release();
        }
    }

    public void Clear()
    {
        ThrowIfDisposed();
        _lifecycle.EnterWriteLock();
        try
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }

            _states.Clear();
            lock (_catalogGate)
            {
                _catalog.Clear();
                _catalogLoaded = true;
            }
        }
        catch
        {
            // A cache clear is best-effort; open external handles may temporarily block deletion.
        }
        finally
        {
            _lifecycle.ExitWriteLock();
        }
    }

    public TerrainMeshCacheStats GetStats()
    {
        ThrowIfDisposed();
        EnsureCatalog();
        long bytes;
        int packs;
        lock (_catalogGate)
        {
            bytes = _catalog.Values.Sum(static value => value.Bytes);
            packs = _catalog.Count;
        }

        int indexedEntries = 0;
        foreach (PackState state in _states.Values)
        {
            indexedEntries += Volatile.Read(ref state.Snapshot)?.Entries.Count ?? 0;
        }

        return new TerrainMeshCacheStats(
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
            Interlocked.Read(ref _stores),
            Interlocked.Read(ref _storeFailures),
            Interlocked.Read(ref _recoveries),
            Interlocked.Read(ref _evictions),
            bytes,
            packs,
            indexedEntries);
    }

    public string ResolvePackPath(in TerrainLodCacheKey key) => GetState(key).PackPath;

    public string ResolveIndexPath(in TerrainLodCacheKey key) => GetState(key).IndexPath;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifecycle.Dispose();
    }

    private void StoreUnderWriter(
        PackState state,
        in TerrainLodCacheKey key,
        PreviewTerrainChunkMesh mesh)
    {
        IndexSnapshot current = RefreshIndexUnderWriter(state, EnsureIndex(state));
        int payloadLength = GetPayloadLength(mesh);
        byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            Span<byte> payload = payloadBuffer.AsSpan(0, payloadLength);
            SerializePayload(mesh, payload);
            uint checksum = ComputeCrc32(payload);

            Directory.CreateDirectory(state.DirectoryPath);
            long recordOffset;
            using (FileStream stream = new(
                state.PackPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan))
            {
                recordOffset = stream.Length;
                stream.Position = recordOffset;
                Span<byte> header = stackalloc byte[RecordHeaderSize];
                WriteRecordHeader(header, state.FingerprintHash, key.Residency, payloadLength, checksum);
                stream.Write(header);
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            var entries = new Dictionary<TerrainResidencyKey, RecordLocation>(current.Entries)
            {
                [key.Residency] = new RecordLocation(
                    recordOffset,
                    payloadLength,
                    checksum),
            };
            long packLength = checked(recordOffset + RecordHeaderSize + payloadLength);
            var updated = new IndexSnapshot(packLength, entries);
            WriteIndexAtomically(state, updated);
            Volatile.Write(ref state.Snapshot, updated);
            Interlocked.Increment(ref _stores);
            UpdatePackMetadataAndEvict(state.PackPath, state.IndexPath);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payloadBuffer);
        }
    }

    private PackState GetState(in TerrainLodCacheKey key)
    {
        byte[] fingerprintHash = HashFingerprint(key.Fingerprint);
        string hashHex = Convert.ToHexString(fingerprintHash);
        int regionX = TerrainResidencyKey.FloorDiv(key.Residency.X, _regionSize);
        int regionZ = TerrainResidencyKey.FloorDiv(key.Residency.Z, _regionSize);
        string directory = Path.Combine(_rootDirectory, hashHex, $"lod-{key.Residency.LodLevel}");
        string packPath = Path.Combine(directory, $"region-{regionX}-{regionZ}.pack");
        return _states.GetOrAdd(
            packPath,
            static (path, value) => new PackState(
                value.Directory,
                path,
                path + ".idx",
                value.Hash,
                SPackWriters.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1))),
            (Directory: directory, Hash: fingerprintHash));
    }

    private IndexSnapshot EnsureIndex(PackState state)
    {
        IndexSnapshot? existing = Volatile.Read(ref state.Snapshot);
        if (existing is not null)
        {
            return existing;
        }

        lock (state.IndexGate)
        {
            existing = Volatile.Read(ref state.Snapshot);
            if (existing is not null)
            {
                return existing;
            }

            long packLength = SafeFileLength(state.PackPath);
            if (TryReadIndex(state, packLength, out IndexSnapshot? fromDisk))
            {
                Volatile.Write(ref state.Snapshot, fromDisk);
                return fromDisk;
            }

            bool hadData = packLength != 0 || File.Exists(state.IndexPath);
            IndexSnapshot rebuilt = ScanAndRecover(state, packLength);
            if (hadData)
            {
                Interlocked.Increment(ref _recoveries);
                WriteIndexAtomically(state, rebuilt);
            }

            Volatile.Write(ref state.Snapshot, rebuilt);
            return rebuilt;
        }
    }

    private IndexSnapshot GetIndexForRead(PackState state)
    {
        IndexSnapshot? snapshot = Volatile.Read(ref state.Snapshot);
        if (snapshot is not null)
        {
            return snapshot;
        }

        state.Writer.Wait();
        try
        {
            return EnsureIndex(state);
        }
        finally
        {
            state.Writer.Release();
        }
    }

    private IndexSnapshot RefreshIndexAfterMiss(
        PackState state,
        IndexSnapshot snapshot)
    {
        if (snapshot.PackLength == SafeFileLength(state.PackPath))
        {
            return snapshot;
        }

        state.Writer.Wait();
        try
        {
            return RefreshIndexUnderWriter(state, snapshot);
        }
        finally
        {
            state.Writer.Release();
        }
    }

    private IndexSnapshot RefreshIndexUnderWriter(
        PackState state,
        IndexSnapshot snapshot)
    {
        long actualLength = SafeFileLength(state.PackPath);
        IndexSnapshot? latest = Volatile.Read(ref state.Snapshot);
        if (latest is not null && latest.PackLength == actualLength)
        {
            return latest;
        }

        if (snapshot.PackLength == actualLength)
        {
            return snapshot;
        }

        lock (state.IndexGate)
        {
            Volatile.Write(ref state.Snapshot, null);
            return EnsureIndex(state);
        }
    }

    private void RecoverState(PackState state)
    {
        state.Writer.Wait();
        try
        {
            lock (state.IndexGate)
            {
                long packLength = SafeFileLength(state.PackPath);
                IndexSnapshot rebuilt = ScanAndRecover(state, packLength);
                WriteIndexAtomically(state, rebuilt);
                Volatile.Write(ref state.Snapshot, rebuilt);
                Interlocked.Increment(ref _recoveries);
                UpdatePackMetadata(state.PackPath, state.IndexPath);
            }
        }
        catch
        {
            // Recovery is best-effort and the original operation remains a cache miss.
        }
        finally
        {
            state.Writer.Release();
        }
    }

    private static IndexSnapshot ScanAndRecover(PackState state, long originalLength)
    {
        if (originalLength <= 0 || !File.Exists(state.PackPath))
        {
            return new IndexSnapshot(0, new Dictionary<TerrainResidencyKey, RecordLocation>());
        }

        var entries = new Dictionary<TerrainResidencyKey, RecordLocation>();
        long offset = 0;
        byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
                state.PackPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);
            Span<byte> header = stackalloc byte[RecordHeaderSize];
            while (offset + RecordHeaderSize <= originalLength &&
                   ReadExactly(handle, header, offset) &&
                   TryParseRecordHeader(
                       header,
                       state.FingerprintHash,
                       out TerrainResidencyKey residency,
                       out int payloadLength,
                       out uint checksum) &&
                   payloadLength <= originalLength - offset - RecordHeaderSize)
            {
                if (payloadBuffer.Length < payloadLength)
                {
                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                    payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                }

                Span<byte> payload = payloadBuffer.AsSpan(0, payloadLength);
                if (!ReadExactly(handle, payload, offset + RecordHeaderSize) ||
                    ComputeCrc32(payload) != checksum ||
                    !ValidatePayload(payload, residency))
                {
                    break;
                }

                entries[residency] = new RecordLocation(offset, payloadLength, checksum);
                offset += RecordHeaderSize + payloadLength;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payloadBuffer);
        }

        if (offset != originalLength)
        {
            using FileStream repair = new(
                state.PackPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete);
            repair.SetLength(offset);
            repair.Flush(flushToDisk: true);
        }

        return new IndexSnapshot(offset, entries);
    }

    private static bool TryReadIndex(
        PackState state,
        long actualPackLength,
        [NotNullWhen(true)] out IndexSnapshot? snapshot)
    {
        snapshot = null;
        if (!File.Exists(state.IndexPath))
        {
            if (actualPackLength != 0)
            {
                return false;
            }

            snapshot = new IndexSnapshot(
                0,
                new Dictionary<TerrainResidencyKey, RecordLocation>());
            return true;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(state.IndexPath);
            ReadOnlySpan<byte> data = bytes;
            if (data.Length < IndexHeaderSize ||
                !data[..8].SequenceEqual(IndexMagic) ||
                BinaryPrimitives.ReadInt32LittleEndian(data[8..12]) != FormatVersion ||
                BinaryPrimitives.ReadInt32LittleEndian(data[12..16]) != IndexHeaderSize ||
                !data.Slice(16, FingerprintHashSize).SequenceEqual(state.FingerprintHash))
            {
                return false;
            }

            long indexedPackLength = BinaryPrimitives.ReadInt64LittleEndian(data[48..56]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(data[56..60]);
            int bodyLength = checked(count * IndexEntrySize);
            if (count < 0 ||
                data.Length != IndexHeaderSize + bodyLength ||
                indexedPackLength != actualPackLength ||
                BinaryPrimitives.ReadUInt32LittleEndian(data[60..64]) !=
                ComputeCrc32(data[IndexHeaderSize..]))
            {
                return false;
            }

            var entries = new Dictionary<TerrainResidencyKey, RecordLocation>(count);
            int position = IndexHeaderSize;
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> entry = data.Slice(position, IndexEntrySize);
                int x = BinaryPrimitives.ReadInt32LittleEndian(entry[0..4]);
                int z = BinaryPrimitives.ReadInt32LittleEndian(entry[4..8]);
                byte lod = entry[8];
                long offset = BinaryPrimitives.ReadInt64LittleEndian(entry[12..20]);
                int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(entry[20..24]);
                uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..28]);
                if (lod > TerrainResidencyKey.MaxLodLevel ||
                    offset < 0 ||
                    payloadLength < PayloadHeaderSize ||
                    payloadLength > MaximumPayloadBytes ||
                    offset + RecordHeaderSize + payloadLength > indexedPackLength)
                {
                    return false;
                }

                entries[new TerrainResidencyKey(x, z, lod)] =
                    new RecordLocation(offset, payloadLength, checksum);
                position += IndexEntrySize;
            }

            snapshot = new IndexSnapshot(indexedPackLength, entries);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteIndexAtomically(PackState state, IndexSnapshot snapshot)
    {
        if (!Directory.Exists(state.DirectoryPath))
        {
            return;
        }

        KeyValuePair<TerrainResidencyKey, RecordLocation>[] ordered = snapshot.Entries
            .OrderBy(static pair => pair.Key.X)
            .ThenBy(static pair => pair.Key.Z)
            .ThenBy(static pair => pair.Key.LodLevel)
            .ToArray();
        int length = checked(IndexHeaderSize + ordered.Length * IndexEntrySize);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        string temporaryPath = state.IndexPath + ".tmp";
        try
        {
            Span<byte> data = buffer.AsSpan(0, length);
            data.Clear();
            IndexMagic.CopyTo(data);
            BinaryPrimitives.WriteInt32LittleEndian(data[8..12], FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(data[12..16], IndexHeaderSize);
            state.FingerprintHash.CopyTo(data[16..48]);
            BinaryPrimitives.WriteInt64LittleEndian(data[48..56], snapshot.PackLength);
            BinaryPrimitives.WriteInt32LittleEndian(data[56..60], ordered.Length);

            int position = IndexHeaderSize;
            foreach ((TerrainResidencyKey key, RecordLocation location) in ordered)
            {
                Span<byte> entry = data.Slice(position, IndexEntrySize);
                BinaryPrimitives.WriteInt32LittleEndian(entry[0..4], key.X);
                BinaryPrimitives.WriteInt32LittleEndian(entry[4..8], key.Z);
                entry[8] = key.LodLevel;
                BinaryPrimitives.WriteInt64LittleEndian(entry[12..20], location.RecordOffset);
                BinaryPrimitives.WriteInt32LittleEndian(entry[20..24], location.PayloadLength);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[24..28], location.Checksum);
                position += IndexEntrySize;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                data[60..64],
                ComputeCrc32(data[IndexHeaderSize..]));
            using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, state.IndexPath, overwrite: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            TryDeleteFile(temporaryPath);
        }
    }

    private static bool TryReadRecord(
        PackState state,
        in TerrainLodCacheKey key,
        RecordLocation location,
        [NotNullWhen(true)] out PreviewTerrainChunkMesh? mesh)
    {
        mesh = null;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(location.PayloadLength);
        try
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
                state.PackPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);
            Span<byte> header = stackalloc byte[RecordHeaderSize];
            Span<byte> payload = buffer.AsSpan(0, location.PayloadLength);
            if (!ReadExactly(handle, header, location.RecordOffset) ||
                !TryParseRecordHeader(
                    header,
                    state.FingerprintHash,
                    out TerrainResidencyKey residency,
                    out int payloadLength,
                    out uint checksum) ||
                residency != key.Residency ||
                payloadLength != location.PayloadLength ||
                checksum != location.Checksum ||
                !ReadExactly(handle, payload, location.RecordOffset + RecordHeaderSize) ||
                ComputeCrc32(payload) != checksum)
            {
                return false;
            }

            return TryDeserializePayload(payload, residency, out mesh);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask<PreviewTerrainChunkMesh?> ReadRecordAsync(
        PackState state,
        TerrainLodCacheKey key,
        RecordLocation location,
        CancellationToken cancellationToken)
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(RecordHeaderSize);
        byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(location.PayloadLength);
        try
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
                state.PackPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            Memory<byte> header = headerBuffer.AsMemory(0, RecordHeaderSize);
            Memory<byte> payload = payloadBuffer.AsMemory(0, location.PayloadLength);
            if (!await ReadExactlyAsync(
                    handle,
                    header,
                    location.RecordOffset,
                    cancellationToken).ConfigureAwait(false) ||
                !TryParseRecordHeader(
                    header.Span,
                    state.FingerprintHash,
                    out TerrainResidencyKey residency,
                    out int payloadLength,
                    out uint checksum) ||
                residency != key.Residency ||
                payloadLength != location.PayloadLength ||
                checksum != location.Checksum ||
                !await ReadExactlyAsync(
                    handle,
                    payload,
                    location.RecordOffset + RecordHeaderSize,
                    cancellationToken).ConfigureAwait(false) ||
                ComputeCrc32(payload.Span) != checksum)
            {
                return null;
            }

            return TryDeserializePayload(payload.Span, residency, out PreviewTerrainChunkMesh? mesh)
                ? mesh
                : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
            ArrayPool<byte>.Shared.Return(payloadBuffer);
        }
    }

    private static int GetPayloadLength(PreviewTerrainChunkMesh mesh)
    {
        int batchBytes = checked(mesh.DrawBatches.Length * 3 * sizeof(int));
        int vertexBytes = checked(mesh.InterleavedVertices.Length * sizeof(float));
        int indexBytes = checked(mesh.Indices.Length * sizeof(uint));
        int length = checked(PayloadHeaderSize + batchBytes + vertexBytes + indexBytes);
        if (mesh.DrawBatches.Length > MaximumBatchCount ||
            mesh.InterleavedVertices.Length > MaximumElementCount ||
            mesh.InterleavedVertices.Length % 12 != 0 ||
            mesh.Indices.Length > MaximumElementCount ||
            length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Terrain mesh exceeds the v3 record limits.");
        }

        return length;
    }

    private static void SerializePayload(PreviewTerrainChunkMesh mesh, Span<byte> payload)
    {
        BinaryPrimitives.WriteSingleLittleEndian(payload[0..4], mesh.BoundsCenter.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload[4..8], mesh.BoundsCenter.Y);
        BinaryPrimitives.WriteSingleLittleEndian(payload[8..12], mesh.BoundsCenter.Z);
        BinaryPrimitives.WriteSingleLittleEndian(payload[12..16], mesh.BoundsRadius);
        BinaryPrimitives.WriteInt32LittleEndian(payload[16..20], mesh.MinRelativeHeight);
        BinaryPrimitives.WriteInt32LittleEndian(payload[20..24], mesh.MaxRelativeHeight);
        BinaryPrimitives.WriteInt32LittleEndian(payload[24..28], mesh.DrawBatches.Length);
        BinaryPrimitives.WriteInt32LittleEndian(payload[28..32], mesh.InterleavedVertices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(payload[32..36], mesh.Indices.Length);

        int position = PayloadHeaderSize;
        foreach (PreviewDrawBatch batch in mesh.DrawBatches)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(position, 4), batch.FirstIndex);
            BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(position + 4, 4), batch.IndexCount);
            BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(position + 8, 4), batch.MaterialIndex);
            position += 12;
        }

        ReadOnlySpan<byte> vertices = MemoryMarshal.AsBytes(mesh.InterleavedVertices.AsSpan());
        vertices.CopyTo(payload[position..]);
        position += vertices.Length;
        ReadOnlySpan<byte> indices = MemoryMarshal.AsBytes(mesh.Indices.AsSpan());
        indices.CopyTo(payload[position..]);
    }

    private static bool ValidatePayload(ReadOnlySpan<byte> payload, TerrainResidencyKey residency) =>
        TryGetPayloadLayout(
            payload,
            residency,
            out _,
            out _,
            out _,
            out _);

    private static bool TryDeserializePayload(
        ReadOnlySpan<byte> payload,
        TerrainResidencyKey residency,
        [NotNullWhen(true)] out PreviewTerrainChunkMesh? mesh)
    {
        mesh = null;
        if (!TryGetPayloadLayout(
                payload,
                residency,
                out int batchCount,
                out int vertexCount,
                out int indexCount,
                out int expectedLength))
        {
            return false;
        }

        var batches = new PreviewDrawBatch[batchCount];
        int position = PayloadHeaderSize;
        for (int i = 0; i < batchCount; i++)
        {
            batches[i] = new PreviewDrawBatch(
                BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(position, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(position + 4, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(position + 8, 4)));
            position += 12;
        }

        var vertices = new float[vertexCount];
        int vertexBytes = checked(vertexCount * sizeof(float));
        payload.Slice(position, vertexBytes).CopyTo(MemoryMarshal.AsBytes(vertices.AsSpan()));
        position += vertexBytes;
        var indices = new uint[indexCount];
        payload.Slice(position, expectedLength - position)
            .CopyTo(MemoryMarshal.AsBytes(indices.AsSpan()));

        mesh = new PreviewTerrainChunkMesh
        {
            Key = residency,
            Lod = residency.Kind,
            InterleavedVertices = vertices,
            Indices = indices,
            DrawBatches = batches,
            BoundsCenter = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(payload[0..4]),
                BinaryPrimitives.ReadSingleLittleEndian(payload[4..8]),
                BinaryPrimitives.ReadSingleLittleEndian(payload[8..12])),
            BoundsRadius = BinaryPrimitives.ReadSingleLittleEndian(payload[12..16]),
            MinRelativeHeight = BinaryPrimitives.ReadInt32LittleEndian(payload[16..20]),
            MaxRelativeHeight = BinaryPrimitives.ReadInt32LittleEndian(payload[20..24]),
        };
        return true;
    }

    private static bool TryGetPayloadLayout(
        ReadOnlySpan<byte> payload,
        TerrainResidencyKey residency,
        out int batchCount,
        out int vertexCount,
        out int indexCount,
        out int expectedLength)
    {
        batchCount = 0;
        vertexCount = 0;
        indexCount = 0;
        expectedLength = 0;
        if (payload.Length < PayloadHeaderSize ||
            residency.LodLevel > TerrainResidencyKey.MaxLodLevel)
        {
            return false;
        }

        batchCount = BinaryPrimitives.ReadInt32LittleEndian(payload[24..28]);
        vertexCount = BinaryPrimitives.ReadInt32LittleEndian(payload[28..32]);
        indexCount = BinaryPrimitives.ReadInt32LittleEndian(payload[32..36]);
        if (batchCount < 0 ||
            batchCount > MaximumBatchCount ||
            vertexCount < 0 ||
            vertexCount > MaximumElementCount ||
            vertexCount % 12 != 0 ||
            indexCount < 0 ||
            indexCount > MaximumElementCount)
        {
            return false;
        }

        try
        {
            expectedLength = checked(
                PayloadHeaderSize +
                batchCount * 12 +
                vertexCount * sizeof(float) +
                indexCount * sizeof(uint));
            return expectedLength == payload.Length && expectedLength <= MaximumPayloadBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void WriteRecordHeader(
        Span<byte> header,
        ReadOnlySpan<byte> fingerprintHash,
        TerrainResidencyKey residency,
        int payloadLength,
        uint checksum)
    {
        header.Clear();
        RecordMagic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], RecordHeaderSize);
        fingerprintHash.CopyTo(header[16..48]);
        BinaryPrimitives.WriteInt32LittleEndian(header[48..52], residency.X);
        BinaryPrimitives.WriteInt32LittleEndian(header[52..56], residency.Z);
        header[56] = residency.LodLevel;
        BinaryPrimitives.WriteInt32LittleEndian(header[60..64], payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[64..68], checksum);
    }

    private static bool TryParseRecordHeader(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> expectedFingerprintHash,
        out TerrainResidencyKey residency,
        out int payloadLength,
        out uint checksum)
    {
        residency = default;
        payloadLength = 0;
        checksum = 0;
        if (header.Length != RecordHeaderSize ||
            !header[..8].SequenceEqual(RecordMagic) ||
            BinaryPrimitives.ReadInt32LittleEndian(header[8..12]) != FormatVersion ||
            BinaryPrimitives.ReadInt32LittleEndian(header[12..16]) != RecordHeaderSize ||
            !header.Slice(16, FingerprintHashSize).SequenceEqual(expectedFingerprintHash))
        {
            return false;
        }

        byte lod = header[56];
        payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[60..64]);
        checksum = BinaryPrimitives.ReadUInt32LittleEndian(header[64..68]);
        if (lod > TerrainResidencyKey.MaxLodLevel ||
            payloadLength < PayloadHeaderSize ||
            payloadLength > MaximumPayloadBytes)
        {
            return false;
        }

        residency = new TerrainResidencyKey(
            BinaryPrimitives.ReadInt32LittleEndian(header[48..52]),
            BinaryPrimitives.ReadInt32LittleEndian(header[52..56]),
            lod);
        return true;
    }

    private static byte[] HashFingerprint(in TerrainLodCacheFingerprint fingerprint) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ContentIdentity));

    private void EnsureCatalog()
    {
        lock (_catalogGate)
        {
            if (_catalogLoaded)
            {
                return;
            }

            _catalog.Clear();
            if (Directory.Exists(_rootDirectory))
            {
                foreach (string packPath in Directory.EnumerateFiles(
                             _rootDirectory,
                             "*.pack",
                             SearchOption.AllDirectories))
                {
                    long bytes = SafeFileLength(packPath) + SafeFileLength(packPath + ".idx");
                    _catalog[packPath] = new PackMetadata(
                        bytes,
                        Interlocked.Increment(ref _accessSequence));
                }
            }

            _catalogLoaded = true;
        }
    }

    private void TouchPack(string packPath)
    {
        EnsureCatalog();
        lock (_catalogGate)
        {
            if (_catalog.TryGetValue(packPath, out PackMetadata metadata))
            {
                _catalog[packPath] = metadata with
                {
                    AccessSequence = Interlocked.Increment(ref _accessSequence),
                };
            }
        }
    }

    private void UpdatePackMetadata(string packPath, string indexPath)
    {
        EnsureCatalog();
        lock (_catalogGate)
        {
            _catalog[packPath] = new PackMetadata(
                SafeFileLength(packPath) + SafeFileLength(indexPath),
                Interlocked.Increment(ref _accessSequence));
        }
    }

    private void UpdatePackMetadataAndEvict(string currentPackPath, string currentIndexPath)
    {
        EnsureCatalog();
        lock (_catalogGate)
        {
            _catalog[currentPackPath] = new PackMetadata(
                SafeFileLength(currentPackPath) + SafeFileLength(currentIndexPath),
                Interlocked.Increment(ref _accessSequence));
            long totalBytes = _catalog.Values.Sum(static value => value.Bytes);
            while (totalBytes > _maxBytes && _catalog.Count > 0)
            {
                KeyValuePair<string, PackMetadata> victim = _catalog
                    .OrderBy(static pair => pair.Value.AccessSequence)
                    .First();
                TryDeleteFile(victim.Key);
                TryDeleteFile(victim.Key + ".idx");
                TryDeleteFile(victim.Key + ".idx.tmp");
                totalBytes -= victim.Value.Bytes;
                _catalog.Remove(victim.Key);
                _states.TryRemove(victim.Key, out _);
                Interlocked.Increment(ref _evictions);
            }
        }
    }

    private static long SafeFileLength(string path)
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cache cleanup is best-effort.
        }
    }

    private static bool ReadExactly(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        Span<byte> destination,
        long fileOffset)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int count = RandomAccess.Read(handle, destination[read..], fileOffset + read);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private static async ValueTask<bool> ReadExactlyAsync(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        Memory<byte> destination,
        long fileOffset,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int count = await RandomAccess.ReadAsync(
                handle,
                destination[read..],
                fileOffset + read,
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
        {
            crc = SCrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? 0xedb88320U ^ (value >> 1)
                    : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class PackState(
        string directoryPath,
        string packPath,
        string indexPath,
        byte[] fingerprintHash,
        SemaphoreSlim writer)
    {
        public string DirectoryPath { get; } = directoryPath;

        public string PackPath { get; } = packPath;

        public string IndexPath { get; } = indexPath;

        public byte[] FingerprintHash { get; } = fingerprintHash;

        public SemaphoreSlim Writer { get; } = writer;

        public object IndexGate { get; } = new();

        public IndexSnapshot? Snapshot;
    }

    private sealed record IndexSnapshot(
        long PackLength,
        Dictionary<TerrainResidencyKey, RecordLocation> Entries);

    private readonly record struct RecordLocation(
        long RecordOffset,
        int PayloadLength,
        uint Checksum);

    private readonly record struct PackMetadata(long Bytes, long AccessSequence);
}
