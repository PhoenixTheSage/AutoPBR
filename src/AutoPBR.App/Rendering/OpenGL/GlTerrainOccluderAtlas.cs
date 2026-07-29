
using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// GPU RG32F atlas of heightfield column surface/bottom relative Y for voxel DDA occlusion.
/// Texel (u,v) covers world column (OriginX + u, OriginZ + v).
/// CPU fills run on a worker thread; GL uploads happen on the render thread via <see cref="PumpUpload"/>.
/// Recenters only when the camera approaches the atlas edge (hysteresis), not on every chunk step.
/// </summary>
internal sealed class GlTerrainOccluderAtlas(GL gl) : IDisposable
{
    private uint _texture;
    private int _width;
    private int _height;
    private int _originX;
    private int _originZ;
    private int _settingsVersion = -1;
    private int _filledVersion = -1;
    private bool _hasResidentData;
    private uint _pendingTexture;
    private BakePayload? _uploadPayload;
    private int _uploadNextRow;
    private bool _disposed;

    private int _bakeInFlight;
    private int _bakeGeneration;
    private BakePayload? _readyPayload;
    private readonly object _payloadGate = new();
    private long _lastRebuildRequestUnixMs;
    private long _bakeStartedUnixMs;
    private string _lastFailureDiagnostic = "none";

    public uint TextureHandle => _texture;
    public int Width => _width;
    public int Height => _height;
    public int OriginX => _originX;
    public int OriginZ => _originZ;
    public bool IsValid => EvaluateValidity(_texture, _width, _height, _hasResidentData);
    public float GroundPlaneWorldY => PreviewStageConstants.GroundPlaneWorldY;
    public string LastFailureDiagnostic => Volatile.Read(ref _lastFailureDiagnostic);

    /// <summary>Minimum time between starting atlas rebuilds while flying.</summary>
    public const int RebuildDebounceMs = 400;

    /// <summary>
    /// A bake beyond this threshold is slow, not abandoned. The single-flight worker owns its
    /// latch until it publishes or reports a failure; launching duplicate full-atlas bakes under
    /// startup CPU pressure prevents any generation from reaching upload.
    /// </summary>
    public const int BakeSlowDiagnosticMs = 3000;

    /// <summary>Maximum RG32F atlas bytes uploaded by one render frame.</summary>
    public const int UploadBudgetBytesPerFrame = 4 * 1024 * 1024;

    /// <summary>True while a CPU bake worker holds the single-flight latch.</summary>
    public bool IsBakeInFlight => Volatile.Read(ref _bakeInFlight) != 0;
    public long BakeElapsedMilliseconds =>
        IsBakeInFlight
            ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() -
                          Volatile.Read(ref _bakeStartedUnixMs))
            : 0;
    public bool IsBakeSlow => BakeElapsedMilliseconds >= BakeSlowDiagnosticMs;

    internal static bool EvaluateValidity(
        uint texture,
        int width,
        int height,
        bool hasResidentData) =>
        texture != 0 && width > 0 && height > 0 && hasResidentData;

    /// <summary>
    /// Request a rebuild when the camera nears the atlas edge, or view distance / world-gen changes.
    /// Never blocks on biome sampling; call <see cref="PumpUpload"/> each frame on the GL thread.
    /// </summary>
    public bool EnsureFilled(
        TerrainChunkKey cameraChunk,
        int chunkViewDistance,
        in PreviewTerrainWorldGenSettings worldGen,
        int worldGenRevision,
        int lodRingChunks = PreviewStageConstants.TerrainDefaultLodRingChunks)
    {
        if (_disposed)
        {
            return false;
        }

        chunkViewDistance = Math.Clamp(
            chunkViewDistance,
            PreviewStageConstants.TerrainMinChunkViewDistance,
            PreviewStageConstants.TerrainMaxChunkViewDistance);
        lodRingChunks = Math.Clamp(
            lodRingChunks,
            PreviewStageConstants.TerrainMinLodRingChunks,
            PreviewStageConstants.TerrainMaxLodRingChunks);
        var radiusChunks = chunkViewDistance + lodRingChunks;
        var sizeChunks = radiusChunks * 2 + 1;
        var sizeColumns = sizeChunks * PreviewStageConstants.TerrainChunkSize;
        var settingsVersion = HashCode.Combine(
            sizeColumns,
            worldGenRevision,
            worldGen.Seed,
            worldGen.Amplification.GetHashCode(),
            worldGen.BiomeSize.GetHashCode(),
            worldGen.ErosionStrength.GetHashCode(),
            worldGen.Continentalness.GetHashCode());

        // Keep the resident atlas while the camera stays inside with a chunk-margin buffer.
        // Flying across the interior must not trigger a full rebuild every chunk.
        var edgeMarginChunks = Math.Max(2, lodRingChunks);
        if (IsValid &&
            _width == sizeColumns &&
            _height == sizeColumns &&
            _settingsVersion == settingsVersion &&
            CameraInsideAtlasWithMargin(cameraChunk, edgeMarginChunks))
        {
            return true;
        }

        var originX = (cameraChunk.X - radiusChunks) * PreviewStageConstants.TerrainChunkSize;
        var originZ = (cameraChunk.Z - radiusChunks) * PreviewStageConstants.TerrainChunkSize;
        var version = HashCode.Combine(settingsVersion, cameraChunk.X, cameraChunk.Z);

        // Already recentered on this chunk (bake in flight or just uploaded).
        if (IsValid &&
            _originX == originX &&
            _originZ == originZ &&
            _width == sizeColumns &&
            _filledVersion == version)
        {
            return true;
        }

        TryStartBake(sizeColumns, originX, originZ, version, settingsVersion, worldGen);
        return IsValid;
    }

    /// <summary>Upload any completed CPU bake on the GL thread.</summary>
    public void PumpUpload()
    {
        if (_disposed)
        {
            return;
        }

        var payload = _uploadPayload;
        if (payload is null)
        {
            lock (_payloadGate)
            {
                payload = _readyPayload;
                _readyPayload = null;
            }

            if (payload is null)
            {
                return;
            }

            _pendingTexture = CreateTexture(
                payload.SizeColumns,
                payload.SizeColumns);
            if (_pendingTexture == 0)
            {
                RecordFailure("atlas texture allocation failed");
                Interlocked.Exchange(ref _bakeInFlight, 0);
                return;
            }

            _uploadPayload = payload;
            _uploadNextRow = 0;
        }

        while (gl.GetError() != GLEnum.NoError)
        {
        }

        var rowBytes = checked(payload.SizeColumns * 2 * sizeof(float));
        var remainingRows = payload.SizeColumns - _uploadNextRow;
        var rowsThisFrame = Math.Min(
            remainingRows,
            Math.Max(1, UploadBudgetBytesPerFrame / rowBytes));
        gl.BindTexture(TextureTarget.Texture2D, _pendingTexture);
        unsafe
        {
            fixed (float* ptr = payload.Data)
            {
                gl.TexSubImage2D(
                    TextureTarget.Texture2D,
                    0,
                    0,
                    _uploadNextRow,
                    (uint)payload.SizeColumns,
                    (uint)rowsThisFrame,
                    PixelFormat.RG,
                    PixelType.Float,
                    ptr + _uploadNextRow * payload.SizeColumns * 2);
            }
        }

        gl.BindTexture(TextureTarget.Texture2D, 0);
        var uploadError = gl.GetError();
        if (uploadError != GLEnum.NoError)
        {
            RecordFailure($"atlas RG32F upload produced {uploadError}");
            gl.DeleteTexture(_pendingTexture);
            _pendingTexture = 0;
            _uploadPayload = null;
            _uploadNextRow = 0;
            Interlocked.Exchange(ref _bakeInFlight, 0);
            return;
        }

        _uploadNextRow += rowsThisFrame;
        if (_uploadNextRow < payload.SizeColumns)
        {
            return;
        }

        if (_texture != 0)
        {
            gl.DeleteTexture(_texture);
        }

        _texture = _pendingTexture;
        _pendingTexture = 0;
        _uploadPayload = null;
        _uploadNextRow = 0;
        _width = payload.SizeColumns;
        _height = payload.SizeColumns;
        _originX = payload.OriginX;
        _originZ = payload.OriginZ;
        _settingsVersion = payload.SettingsVersion;
        _filledVersion = payload.Version;
        _hasResidentData = true;
        Volatile.Write(ref _lastFailureDiagnostic, "none");
        Interlocked.Exchange(ref _bakeInFlight, 0);
    }

    public void Bind(TextureUnit unit)
    {
        if (!IsValid)
        {
            return;
        }

        gl.ActiveTexture(unit);
        gl.BindTexture(TextureTarget.Texture2D, _texture);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_payloadGate)
        {
            _readyPayload = null;
        }

        _uploadPayload = null;
        _uploadNextRow = 0;
        DestroyTexture();
        Interlocked.Exchange(ref _bakeInFlight, 0);
    }

    private bool CameraInsideAtlasWithMargin(TerrainChunkKey cameraChunk, int marginChunks)
    {
        var chunkSize = PreviewStageConstants.TerrainChunkSize;
        if (_width < chunkSize || _height < chunkSize)
        {
            return false;
        }

        var minCx = FloorDiv(_originX, chunkSize);
        var minCz = FloorDiv(_originZ, chunkSize);
        var chunksX = _width / chunkSize;
        var chunksZ = _height / chunkSize;
        var maxCx = minCx + chunksX - 1;
        var maxCz = minCz + chunksZ - 1;
        marginChunks = Math.Clamp(marginChunks, 0, Math.Max(0, Math.Min(chunksX, chunksZ) / 2 - 1));
        return cameraChunk.X >= minCx + marginChunks &&
               cameraChunk.X <= maxCx - marginChunks &&
               cameraChunk.Z >= minCz + marginChunks &&
               cameraChunk.Z <= maxCz - marginChunks;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : (value - divisor + 1) / divisor;

    private void TryStartBake(
        int sizeColumns,
        int originX,
        int originZ,
        int version,
        int settingsVersion,
        in PreviewTerrainWorldGenSettings worldGen)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _lastRebuildRequestUnixMs < RebuildDebounceMs && IsValid)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _bakeInFlight, 1, 0) != 0)
        {
            // The worker reports exceptions and clears its own latch. Retrying solely because a
            // valid large bake is slow creates a generation storm where every result is stale.
            return;
        }

        _lastRebuildRequestUnixMs = now;
        Volatile.Write(ref _bakeStartedUnixMs, now);
        var generation = Interlocked.Increment(ref _bakeGeneration);
        var genCopy = worldGen;
        var ox = originX;
        var oz = originZ;
        var size = sizeColumns;
        var ver = version;
        var settingsVer = settingsVersion;

        _ = Task.Factory.StartNew(() =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_disposed)
                {
                    ClearBakeLatchIfCurrent(generation);
                    return;
                }

                var data = new float[checked(size * size * 2)];
                var fillDepth = PreviewStageConstants.TerrainFillDepth;
                Parallel.For(0, size, z =>
                {
                    var worldZ = oz + z;
                    var row = z * size;
                    for (var x = 0; x < size; x++)
                    {
                        var worldX = ox + x;
                        var surface = PreviewTerrainHeightfield.SampleColumn(worldX, worldZ, genCopy);
                        var bottom = PreviewVoxelDdaMath.SolidBottomY(surface, fillDepth);
                        var i = (row + x) * 2;
                        data[i] = surface;
                        data[i + 1] = bottom;
                    }
                });

                if (_disposed)
                {
                    ClearBakeLatchIfCurrent(generation);
                    return;
                }

                lock (_payloadGate)
                {
                    if (generation == Volatile.Read(ref _bakeGeneration))
                    {
                        _readyPayload = new BakePayload(ox, oz, size, ver, settingsVer, data);
                    }
                }

                // Latch stays set until PumpUpload (or Dispose) so a second bake cannot race the payload.
            }
            catch (Exception exception)
            {
                RecordFailure(
                    $"heightfield bake failed after {stopwatch.Elapsed.TotalMilliseconds:F0} ms: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                ClearBakeLatchIfCurrent(generation);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void ClearBakeLatchIfCurrent(int generation)
    {
        if (generation == Volatile.Read(ref _bakeGeneration))
        {
            Interlocked.Exchange(ref _bakeInFlight, 0);
        }
    }

    private void RecordFailure(string diagnostic)
    {
        Volatile.Write(ref _lastFailureDiagnostic, diagnostic);
    }

    private uint CreateTexture(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        unsafe
        {
            gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.RG32f,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.RG,
                PixelType.Float,
                (void*)0);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private void DestroyTexture()
    {
        if (_texture != 0)
        {
            gl.DeleteTexture(_texture);
            _texture = 0;
        }

        if (_pendingTexture != 0)
        {
            gl.DeleteTexture(_pendingTexture);
            _pendingTexture = 0;
        }

        _width = 0;
        _height = 0;
        _filledVersion = -1;
        _settingsVersion = -1;
        _hasResidentData = false;
    }

    private sealed record BakePayload(
        int OriginX,
        int OriginZ,
        int SizeColumns,
        int Version,
        int SettingsVersion,
        float[] Data);
}
