using System.Threading;
using System.Threading.Tasks;

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
    private float[] _cpuScratch = [];
    private bool _disposed;

    private int _bakeInFlight;
    private BakePayload? _readyPayload;
    private readonly object _payloadGate = new();
    private long _lastRebuildRequestUnixMs;

    public uint TextureHandle => _texture;
    public int Width => _width;
    public int Height => _height;
    public int OriginX => _originX;
    public int OriginZ => _originZ;
    public bool IsValid => _texture != 0 && _width > 0 && _height > 0 && _filledVersion >= 0;
    public float GroundPlaneWorldY => PreviewStageConstants.GroundPlaneWorldY;

    /// <summary>Minimum time between starting atlas rebuilds while flying.</summary>
    public const int RebuildDebounceMs = 400;

    /// <summary>
    /// If a bake latch stays set without producing a payload (thread-pool delay / abandoned worker),
    /// allow a retry so DDA is not stuck off after startup.
    /// </summary>
    public const int BakeStuckTimeoutMs = 3000;

    /// <summary>True while a CPU bake worker holds the single-flight latch.</summary>
    public bool IsBakeInFlight => Volatile.Read(ref _bakeInFlight) != 0;

    /// <summary>
    /// Request a rebuild when the camera nears the atlas edge, or view distance / world-gen changes.
    /// Never blocks on biome sampling; call <see cref="PumpUpload"/> each frame on the GL thread.
    /// </summary>
    public bool EnsureFilled(
        TerrainChunkKey cameraChunk,
        int chunkViewDistance,
        in PreviewTerrainWorldGenSettings worldGen,
        int worldGenRevision)
    {
        if (_disposed)
        {
            return false;
        }

        chunkViewDistance = Math.Clamp(
            chunkViewDistance,
            PreviewStageConstants.TerrainMinChunkViewDistance,
            PreviewStageConstants.TerrainMaxChunkViewDistance);
        var radiusChunks = chunkViewDistance + PreviewStageConstants.TerrainLodRingChunks;
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
        // Flying across the interior must not trigger a full 464² rebuild every chunk.
        var edgeMarginChunks = Math.Max(2, PreviewStageConstants.TerrainLodRingChunks);
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

        BakePayload? payload;
        lock (_payloadGate)
        {
            payload = _readyPayload;
            _readyPayload = null;
        }

        if (payload is null)
        {
            return;
        }

        if (!EnsureTexture(payload.SizeColumns, payload.SizeColumns))
        {
            Interlocked.Exchange(ref _bakeInFlight, 0);
            return;
        }

        if (_cpuScratch.Length < payload.Data.Length)
        {
            _cpuScratch = new float[payload.Data.Length];
        }

        Array.Copy(payload.Data, _cpuScratch, payload.Data.Length);
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        unsafe
        {
            fixed (float* ptr = _cpuScratch)
            {
                gl.TexSubImage2D(
                    TextureTarget.Texture2D,
                    0,
                    0,
                    0,
                    (uint)payload.SizeColumns,
                    (uint)payload.SizeColumns,
                    PixelFormat.RG,
                    PixelType.Float,
                    ptr);
            }
        }

        gl.BindTexture(TextureTarget.Texture2D, 0);
        _originX = payload.OriginX;
        _originZ = payload.OriginZ;
        _settingsVersion = payload.SettingsVersion;
        _filledVersion = payload.Version;
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
            // Worker published a payload but PumpUpload has not run yet, or a prior bake stalled.
            // Unstick after timeout so startup/reload cannot leave DDA permanently unavailable.
            if (now - _lastRebuildRequestUnixMs >= BakeStuckTimeoutMs)
            {
                Interlocked.Exchange(ref _bakeInFlight, 0);
                if (Interlocked.CompareExchange(ref _bakeInFlight, 1, 0) != 0)
                {
                    return;
                }
            }
            else
            {
                return;
            }
        }

        _lastRebuildRequestUnixMs = now;
        var genCopy = worldGen;
        var ox = originX;
        var oz = originZ;
        var size = sizeColumns;
        var ver = version;
        var settingsVer = settingsVersion;

        _ = Task.Run(() =>
        {
            try
            {
                if (_disposed)
                {
                    Interlocked.Exchange(ref _bakeInFlight, 0);
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
                    Interlocked.Exchange(ref _bakeInFlight, 0);
                    return;
                }

                lock (_payloadGate)
                {
                    _readyPayload = new BakePayload(ox, oz, size, ver, settingsVer, data);
                }

                // Latch stays set until PumpUpload (or Dispose) so a second bake cannot race the payload.
            }
            catch
            {
                Interlocked.Exchange(ref _bakeInFlight, 0);
            }
        });
    }

    private bool EnsureTexture(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_texture != 0 && _width == width && _height == height)
        {
            return true;
        }

        DestroyTexture();
        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _texture);
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
        _width = width;
        _height = height;
        _filledVersion = -1;
        _settingsVersion = -1;
        return _texture != 0;
    }

    private void DestroyTexture()
    {
        if (_texture != 0)
        {
            gl.DeleteTexture(_texture);
            _texture = 0;
        }

        _width = 0;
        _height = 0;
        _filledVersion = -1;
        _settingsVersion = -1;
    }

    private sealed record BakePayload(
        int OriginX,
        int OriginZ,
        int SizeColumns,
        int Version,
        int SettingsVersion,
        float[] Data);
}
