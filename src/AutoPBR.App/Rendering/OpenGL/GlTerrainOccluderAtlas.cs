
using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// GPU RG32F atlas of heightfield column surface/bottom relative Y for voxel DDA occlusion.
/// Texel (u,v) covers a <see cref="CellMeters"/>×<see cref="CellMeters"/> world block whose
/// min corner is (OriginX + u×CellMeters, OriginZ + v×CellMeters). CellMeters=1 is the fine
/// near-field atlas; larger cells track the full LOD ring as a coarse companion.
/// Desktop GL may fill 1 m atlases via compute (genesis_terrain_height_atlas.comp);
/// coarse and GLES paths use a CPU worker + <see cref="PumpUpload"/>.
/// </summary>
internal sealed class GlTerrainOccluderAtlas(GL gl, int cellMeters = 1) : IDisposable
{
    private const int ComputeLocalSize = 8;
    internal const int CpuBakeMaxDegreeOfParallelism = 2;
    internal const int CoarseSamplesPerAxis = 8;
    private static readonly ParallelOptions CpuBakeParallelOptions = new()
    {
        MaxDegreeOfParallelism = CpuBakeMaxDegreeOfParallelism,
    };

    /// <summary>Maximum column writes issued by one compute pump (≈64×1024 at 8×8 groups).</summary>
    public const int ComputeBudgetColumnsPerFrame = 64 * 1024;

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

    private GlShaderProgram? _computeProgram;
    private bool _computeEnabled;
    private bool _computeSessionDisabled;
    private ComputeJob? _computeJob;

    /// <summary>World meters per atlas texel (≥1). Fine atlas uses 1; coarse uses 8+.</summary>
    public int CellMeters { get; } = Math.Max(1, cellMeters);

    public uint TextureHandle => _texture;
    public int Width => _width;
    public int Height => _height;
    public int OriginX => _originX;
    public int OriginZ => _originZ;
    public bool IsValid => EvaluateValidity(_texture, _width, _height, _hasResidentData);
    public float GroundPlaneWorldY => PreviewStageConstants.GroundPlaneWorldY;
    public string LastFailureDiagnostic => Volatile.Read(ref _lastFailureDiagnostic);
    public bool UsesComputeFill =>
        CellMeters <= 1 &&
        _computeEnabled && !_computeSessionDisabled && _computeProgram is { IsValid: true };

    /// <summary>Minimum time between starting atlas rebuilds while flying.</summary>
    public const int RebuildDebounceMs = 400;

    /// <summary>
    /// A bake beyond this threshold is slow, not abandoned. The single-flight worker owns its
    /// latch until it publishes or reports a failure; launching duplicate full-atlas bakes under
    /// startup CPU pressure prevents any generation from reaching upload.
    /// </summary>
    public const int BakeSlowDiagnosticMs = 3000;

    /// <summary>Maximum RG32F atlas bytes uploaded by one render frame (CPU path).</summary>
    public const int UploadBudgetBytesPerFrame = 4 * 1024 * 1024;

    /// <summary>True while a CPU bake or compute fill holds the single-flight latch.</summary>
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
    /// Bind a desktop compute program for atlas fills. Pass null / disabled to force the CPU path.
    /// </summary>
    public void ConfigureCompute(GlShaderProgram? program, bool enabled)
    {
        _computeProgram = program;
        _computeEnabled = enabled && program is { IsValid: true };
    }

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
        // Fine (1 m): Full + modest LOD margin only. Coarse (cellMeters≥8): track full LOD ring.
        var maxLodRing = CellMeters <= 1
            ? PreviewStageConstants.TerrainOccluderAtlasMaxLodRingChunks
            : PreviewStageConstants.TerrainMaxLodRingChunks;
        var atlasLodRing = Math.Clamp(
            lodRingChunks,
            PreviewStageConstants.TerrainMinLodRingChunks,
            maxLodRing);
        var radiusChunks = chunkViewDistance + atlasLodRing;
        var sizeWorldMeters = (radiusChunks * 2 + 1) * PreviewStageConstants.TerrainChunkSize;
        var sizeColumns = Math.Max(1, sizeWorldMeters / CellMeters);
        var settingsVersion = HashCode.Combine(
            sizeColumns,
            CellMeters,
            worldGenRevision,
            worldGen.Seed,
            worldGen.Amplification.GetHashCode(),
            worldGen.BiomeSize.GetHashCode(),
            worldGen.ErosionStrength.GetHashCode(),
            worldGen.Continentalness.GetHashCode());

        // Keep the resident atlas while the camera stays inside with a chunk-margin buffer.
        // Flying across the interior must not trigger a full rebuild every chunk.
        var edgeMarginChunks = Math.Max(2, CellMeters <= 1 ? atlasLodRing : Math.Min(atlasLodRing, 16));
        if (IsValid &&
            _width == sizeColumns &&
            _height == sizeColumns &&
            _settingsVersion == settingsVersion &&
            CameraInsideAtlasWithMargin(cameraChunk, edgeMarginChunks))
        {
            return true;
        }

        var rawOriginX = (cameraChunk.X - radiusChunks) * PreviewStageConstants.TerrainChunkSize;
        var rawOriginZ = (cameraChunk.Z - radiusChunks) * PreviewStageConstants.TerrainChunkSize;
        var originX = AlignDown(rawOriginX, CellMeters);
        var originZ = AlignDown(rawOriginZ, CellMeters);
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

    /// <summary>
    /// Advance CPU row uploads and/or compute tile dispatches on the GL thread.
    /// </summary>
    public void PumpUpload()
    {
        if (_disposed)
        {
            return;
        }

        if (_computeJob is not null)
        {
            PumpComputeJob();
            return;
        }

        PumpCpuUpload();
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
        AbortComputeJob(clearLatch: false);
        DestroyTexture();
        Interlocked.Exchange(ref _bakeInFlight, 0);
        _computeProgram = null;
    }

    private void PumpCpuUpload()
    {
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

        PublishResidentTexture(
            _pendingTexture,
            payload.SizeColumns,
            payload.OriginX,
            payload.OriginZ,
            payload.SettingsVersion,
            payload.Version);
        _pendingTexture = 0;
        _uploadPayload = null;
        _uploadNextRow = 0;
    }

    private void PumpComputeJob()
    {
        var job = _computeJob;
        if (job is null || _computeProgram is not { IsValid: true })
        {
            AbortComputeJob(clearLatch: true);
            return;
        }

        if (job.PendingTexture == 0)
        {
            var texture = CreateTexture(job.SizeColumns, job.SizeColumns);
            if (texture == 0)
            {
                RecordFailure("atlas compute texture allocation failed");
                AbortComputeJob(clearLatch: true);
                return;
            }

            job = job with { PendingTexture = texture };
            _computeJob = job;
        }

        while (gl.GetError() != GLEnum.NoError)
        {
        }

        var remainingRows = job.SizeColumns - job.NextRow;
        if (remainingRows <= 0)
        {
            FinishComputeJob(job);
            return;
        }

        var rowsThisFrame = Math.Min(
            remainingRows,
            Math.Max(1, ComputeBudgetColumnsPerFrame / Math.Max(1, job.SizeColumns)));
        var program = _computeProgram;
        program.Use();
        BindComputeUniforms(gl, program, job, job.NextRow, rowsThisFrame);

        gl.BindImageTexture(
            0,
            job.PendingTexture,
            0,
            false,
            0,
            BufferAccessARB.WriteOnly,
            InternalFormat.RG32f);

        var groupsX = (uint)((job.SizeColumns + ComputeLocalSize - 1) / ComputeLocalSize);
        var groupsY = (uint)((rowsThisFrame + ComputeLocalSize - 1) / ComputeLocalSize);
        gl.DispatchCompute(groupsX, groupsY, 1);
        gl.MemoryBarrier(
            MemoryBarrierMask.ShaderImageAccessBarrierBit |
            MemoryBarrierMask.TextureFetchBarrierBit);
        gl.BindImageTexture(0, 0, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.RG32f);
        gl.UseProgram(0);

        var dispatchError = gl.GetError();
        if (dispatchError != GLEnum.NoError)
        {
            RecordFailure($"atlas compute dispatch produced {dispatchError}");
            _computeSessionDisabled = true;
            AbortComputeJob(clearLatch: true);
            return;
        }

        job = job with { NextRow = job.NextRow + rowsThisFrame };
        _computeJob = job;
        if (job.NextRow >= job.SizeColumns)
        {
            FinishComputeJob(job);
        }
    }

    private void FinishComputeJob(ComputeJob job)
    {
        PublishResidentTexture(
            job.PendingTexture,
            job.SizeColumns,
            job.OriginX,
            job.OriginZ,
            job.SettingsVersion,
            job.Version);
        _computeJob = null;
    }

    private void AbortComputeJob(bool clearLatch)
    {
        if (_computeJob is { PendingTexture: not 0 } job &&
            job.PendingTexture != _texture)
        {
            gl.DeleteTexture(job.PendingTexture);
        }

        _computeJob = null;
        if (clearLatch)
        {
            Interlocked.Exchange(ref _bakeInFlight, 0);
        }
    }

    private void PublishResidentTexture(
        uint texture,
        int sizeColumns,
        int originX,
        int originZ,
        int settingsVersion,
        int version)
    {
        if (_texture != 0 && _texture != texture)
        {
            gl.DeleteTexture(_texture);
        }

        _texture = texture;
        _width = sizeColumns;
        _height = sizeColumns;
        _originX = originX;
        _originZ = originZ;
        _settingsVersion = settingsVersion;
        _filledVersion = version;
        _hasResidentData = true;
        Volatile.Write(ref _lastFailureDiagnostic, "none");
        Interlocked.Exchange(ref _bakeInFlight, 0);
    }

    private static void BindComputeUniforms(
        GL gl,
        GlShaderProgram program,
        ComputeJob job,
        int tileOriginY,
        int tileRows)
    {
        SetUniform2i(gl, program, "uOrigin", job.OriginX, job.OriginZ);
        SetUniform2i(gl, program, "uSize", job.SizeColumns, job.SizeColumns);
        SetUniform2i(gl, program, "uTileOrigin", 0, tileOriginY);
        SetUniform2i(gl, program, "uTileSize", job.SizeColumns, tileRows);
        SetUniform1i(gl, program, "uSeed", job.WorldGen.Seed);
        SetUniform1f(gl, program, "uBiomeSize", job.WorldGen.BiomeSize);
        SetUniform1f(gl, program, "uAmplification", job.WorldGen.Amplification);
        SetUniform1f(gl, program, "uErosionStrength", job.WorldGen.ErosionStrength);
        SetUniform1f(gl, program, "uContinentalness", job.WorldGen.Continentalness);
        SetUniform1i(gl, program, "uFlatPadHalfExtent", PreviewStageConstants.TerrainFlatPadHalfExtent);
        SetUniform1i(gl, program, "uTransitionBlocks", PreviewStageConstants.TerrainTransitionBlocks);
        SetUniform1i(gl, program, "uFillDepth", PreviewStageConstants.TerrainFillDepth);
    }

    private static void SetUniform1i(GL gl, GlShaderProgram program, string name, int value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            gl.Uniform1(loc, value);
        }
    }

    private static void SetUniform1f(GL gl, GlShaderProgram program, string name, float value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            gl.Uniform1(loc, value);
        }
    }

    private static void SetUniform2i(GL gl, GlShaderProgram program, string name, int x, int y)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            gl.Uniform2(loc, x, y);
        }
    }

    private bool CameraInsideAtlasWithMargin(TerrainChunkKey cameraChunk, int marginChunks)
    {
        var chunkSize = PreviewStageConstants.TerrainChunkSize;
        var worldWidth = _width * CellMeters;
        var worldHeight = _height * CellMeters;
        if (worldWidth < chunkSize || worldHeight < chunkSize)
        {
            return false;
        }

        var minCx = FloorDiv(_originX, chunkSize);
        var minCz = FloorDiv(_originZ, chunkSize);
        var chunksX = worldWidth / chunkSize;
        var chunksZ = worldHeight / chunkSize;
        var maxCx = minCx + chunksX - 1;
        var maxCz = minCz + chunksZ - 1;
        marginChunks = Math.Clamp(marginChunks, 0, Math.Max(0, Math.Min(chunksX, chunksZ) / 2 - 1));
        return cameraChunk.X >= minCx + marginChunks &&
               cameraChunk.X <= maxCx - marginChunks &&
               cameraChunk.Z >= minCz + marginChunks &&
               cameraChunk.Z <= maxCz - marginChunks;
    }

    private static int AlignDown(int value, int alignment)
    {
        alignment = Math.Max(1, alignment);
        if (value >= 0)
        {
            return value / alignment * alignment;
        }

        return -((-value + alignment - 1) / alignment) * alignment;
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
        var genCopy = PreviewTerrainWorldGenSettings.Resolve(worldGen);

        if (UsesComputeFill)
        {
            // Drop any stale CPU payload; compute owns this generation on the GL thread.
            lock (_payloadGate)
            {
                _readyPayload = null;
            }

            _uploadPayload = null;
            _uploadNextRow = 0;
            if (_pendingTexture != 0)
            {
                gl.DeleteTexture(_pendingTexture);
                _pendingTexture = 0;
            }

            _computeJob = new ComputeJob(
                OriginX: originX,
                OriginZ: originZ,
                SizeColumns: sizeColumns,
                Version: version,
                SettingsVersion: settingsVersion,
                WorldGen: genCopy,
                PendingTexture: 0,
                NextRow: 0,
                Generation: generation);
            return;
        }

        AbortComputeJob(clearLatch: false);
        StartCpuBake(sizeColumns, originX, originZ, version, settingsVersion, genCopy, generation);
    }

    private void StartCpuBake(
        int sizeColumns,
        int originX,
        int originZ,
        int version,
        int settingsVersion,
        PreviewTerrainWorldGenSettings worldGen,
        int generation)
    {
        var ox = originX;
        var oz = originZ;
        var size = sizeColumns;
        var ver = version;
        var settingsVer = settingsVersion;
        var genCopy = worldGen;

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
                var cell = CellMeters;
                Parallel.For(0, size, CpuBakeParallelOptions, z =>
                {
                    var row = z * size;
                    for (var x = 0; x < size; x++)
                    {
                        var cellOriginX = ox + x * cell;
                        var cellOriginZ = oz + z * cell;
                        var surface = SampleCellSurface(cellOriginX, cellOriginZ, cell, genCopy);
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

    internal static int SampleCellSurface(
        int cellOriginX,
        int cellOriginZ,
        int cell,
        in PreviewTerrainWorldGenSettings worldGen)
    {
        var surface = int.MinValue;
        if (cell <= CoarseSamplesPerAxis)
        {
            for (var dz = 0; dz < cell; dz++)
            {
                for (var dx = 0; dx < cell; dx++)
                {
                    surface = Math.Max(
                        surface,
                        PreviewTerrainHeightfield.SampleColumn(
                            cellOriginX + dx,
                            cellOriginZ + dz,
                            worldGen));
                }
            }
        }
        else
        {
            // Exhaustively scanning a 128x128 coarse texel costs 16,384 worldgen evaluations.
            // An 8x8 grid is sufficient for optional far-field occlusion. If it misses a local
            // peak the atlas merely culls less aggressively; rendered geometry remains correct.
            for (var sampleZ = 0; sampleZ < CoarseSamplesPerAxis; sampleZ++)
            {
                var dz = sampleZ * (cell - 1) / (CoarseSamplesPerAxis - 1);
                for (var sampleX = 0; sampleX < CoarseSamplesPerAxis; sampleX++)
                {
                    var dx = sampleX * (cell - 1) / (CoarseSamplesPerAxis - 1);
                    surface = Math.Max(
                        surface,
                        PreviewTerrainHeightfield.SampleColumn(
                            cellOriginX + dx,
                            cellOriginZ + dz,
                            worldGen));
                }
            }
        }

        return surface == int.MinValue ? 0 : surface;
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

    private sealed record ComputeJob(
        int OriginX,
        int OriginZ,
        int SizeColumns,
        int Version,
        int SettingsVersion,
        PreviewTerrainWorldGenSettings WorldGen,
        uint PendingTexture,
        int NextRow,
        int Generation);
}
