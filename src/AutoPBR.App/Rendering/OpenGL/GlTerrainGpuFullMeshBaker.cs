using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.Preview;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Stage-2 Full-chunk solid meshing on desktop GL compute (per-face quads).
/// Budgeted outside PassScene; GLES/compile faults demote to CPU
/// <see cref="PreviewTerrainMeshBaker.BakeFullChunkSolidsPerFace"/>.
/// </summary>
internal sealed class GlTerrainGpuFullMeshBaker : IDisposable
{
    public const int BoardSide = 18;
    public const int MaxQuadsDefault = 98304;
    public const int FloatsPerQuadPayload = 50;
    public const int JobsPerFrameBudget = 2;

    private const uint ShaderStorageBarrierBit = 0x00002000;

    private readonly GL _gl;
    private GlShaderProgram? _boardProgram;
    private GlShaderProgram? _emitProgram;
    private uint _boardSsbo;
    private uint _quadSsbo;
    private uint _counterSsbo;
    private int _maxQuads;
    private bool _disposed;
    private bool _healthy;

    public GlTerrainGpuFullMeshBaker(GL gl) => _gl = gl;

    public bool IsHealthy => _healthy && !_disposed;
    public string? LastError { get; private set; }

    public void ConfigurePrograms(GlShaderProgram? board, GlShaderProgram? emit, int maxQuads = MaxQuadsDefault)
    {
        if (_disposed)
        {
            return;
        }

        _boardProgram = board is { IsValid: true } ? board : null;
        _emitProgram = emit is { IsValid: true } ? emit : null;
        _maxQuads = Math.Clamp(maxQuads, 1024, MaxQuadsDefault);
        if (_boardProgram is null || _emitProgram is null)
        {
            _healthy = false;
            LastError = "board/emit program missing";
            DisposeBuffers();
            return;
        }

        EnsureBuffers();
        _healthy = _boardSsbo != 0 && _quadSsbo != 0 && _counterSsbo != 0;
        if (!_healthy)
        {
            LastError = "SSBO allocation failed";
        }
    }

    public void Demote(string reason)
    {
        LastError = reason;
        _healthy = false;
    }

    /// <summary>
    /// Production Full bake for the Stage-2 job bridge: CPU greedy solids (vegetation off).
    /// Per-face GPU emit remains available via <see cref="TryBakeGpuPerFace"/> for live parity;
    /// v1 per-face was too heavy (pool ceiling) and exposed the solid-floor underside plane.
    /// </summary>
    public PreviewTerrainChunkMesh? TryBake(
        in TerrainGpuFullJob job,
        int chunkSize = PreviewStageConstants.TerrainChunkSize,
        int fillDepth = PreviewStageConstants.TerrainFillDepth,
        float metersPerTile = PreviewStageConstants.MetersPerGrassTile,
        float surfaceWorldY = PreviewStageConstants.GroundPlaneWorldY,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks)
    {
        _ = chunkSize;
        _ = fillDepth;
        _ = metersPerTile;
        _ = surfaceWorldY;
        _ = flatPadHalfExtent;
        _ = transitionBlocks;
        if (!IsHealthy)
        {
            return null;
        }

        var grass = job.GrassSettings;
        return PreviewTerrainMeshBaker.BakeFullChunk(
            job.Key,
            grass,
            job.WorldGen,
            job.Vegetation);
    }

    /// <summary>
    /// Stage-2 compute path: board + per-face emit (parity / v1.1 greedy precursor).
    /// </summary>
    public PreviewTerrainChunkMesh? TryBakeGpuPerFace(
        in TerrainGpuFullJob job,
        int chunkSize = PreviewStageConstants.TerrainChunkSize,
        int fillDepth = PreviewStageConstants.TerrainFillDepth,
        float metersPerTile = PreviewStageConstants.MetersPerGrassTile,
        float surfaceWorldY = PreviewStageConstants.GroundPlaneWorldY,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks)
    {
        if (!IsHealthy || _boardProgram is null || _emitProgram is null)
        {
            return null;
        }

        var gen = PreviewTerrainWorldGenSettings.Resolve(job.WorldGen);
        var grass = job.GrassSettings with { VegetationIdentity = "" };
        var cx0 = job.Key.OriginX(chunkSize);
        var cz0 = job.Key.OriginZ(chunkSize);

        ResetCounters();

        _boardProgram.Use();
        BindBoardUniforms(gen, cx0 - 1, cz0 - 1, fillDepth, flatPadHalfExtent, transitionBlocks);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _boardSsbo);
        var boardGroups = (uint)((BoardSide + 7) / 8);
        _gl.DispatchCompute(boardGroups, boardGroups, 1);
        _gl.MemoryBarrier(ShaderStorageBarrierBit);

        _emitProgram.Use();
        BindEmitUniforms(gen, grass, cx0, cz0, chunkSize, fillDepth, metersPerTile, surfaceWorldY);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _boardSsbo);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, _quadSsbo);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, _counterSsbo);
        var emitGroups = (uint)((chunkSize + 7) / 8);
        _gl.DispatchCompute(emitGroups, emitGroups, 1);
        _gl.MemoryBarrier(ShaderStorageBarrierBit);

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counterSsbo);
        Span<uint> counters = stackalloc uint[4];
        _gl.GetBufferSubData<uint>(BufferTargetARB.ShaderStorageBuffer, 0, counters);
        var quadCount = counters[0];
        var overflow = counters[1];
        var minH = unchecked((int)counters[2]);
        var maxH = unchecked((int)counters[3]);

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, 0);
        _gl.UseProgram(0);

        if (overflow != 0 || quadCount > (uint)_maxQuads)
        {
            LastError = $"quad overflow ({quadCount}/{_maxQuads})";
            return null;
        }

        if (quadCount == 0 || minH == int.MaxValue)
        {
            return null;
        }

        var floatCount = checked((int)quadCount * FloatsPerQuadPayload);
        var payload = new float[floatCount];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _quadSsbo);
        _gl.GetBufferSubData<float>(BufferTargetARB.ShaderStorageBuffer, 0, payload);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);

        return PackMesh(job.Key, payload, (int)quadCount, minH, maxH, fillDepth, surfaceWorldY, cx0, cz0, chunkSize);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _healthy = false;
        DisposeBuffers();
        // Programs are owned by the backend.
        _boardProgram = null;
        _emitProgram = null;
    }

    private void EnsureBuffers()
    {
        DisposeBuffers();

        var boardBytes = (nuint)(BoardSide * BoardSide * 8 * sizeof(int));
        _boardSsbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _boardSsbo);
        unsafe
        {
            _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, boardBytes, null, BufferUsageARB.DynamicDraw);
        }

        var quadBytes = (nuint)checked(_maxQuads * FloatsPerQuadPayload * sizeof(float));
        _quadSsbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _quadSsbo);
        unsafe
        {
            _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, quadBytes, null, BufferUsageARB.DynamicDraw);
        }

        _counterSsbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counterSsbo);
        unsafe
        {
            _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(4 * sizeof(uint)), null, BufferUsageARB.DynamicDraw);
        }
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private void DisposeBuffers()
    {
        if (_boardSsbo != 0)
        {
            _gl.DeleteBuffer(_boardSsbo);
            _boardSsbo = 0;
        }

        if (_quadSsbo != 0)
        {
            _gl.DeleteBuffer(_quadSsbo);
            _quadSsbo = 0;
        }

        if (_counterSsbo != 0)
        {
            _gl.DeleteBuffer(_counterSsbo);
            _counterSsbo = 0;
        }
    }

    private void ResetCounters()
    {
        Span<uint> init =
        [
            0u,
            0u,
            unchecked((uint)int.MaxValue),
            unchecked((uint)int.MinValue)
        ];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counterSsbo);
        _gl.BufferSubData<uint>(BufferTargetARB.ShaderStorageBuffer, 0, init);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private void BindBoardUniforms(
        PreviewTerrainWorldGenSettings gen,
        int boardOriginX,
        int boardOriginZ,
        int fillDepth,
        int flatPadHalfExtent,
        int transitionBlocks)
    {
        SetUniform2i(_boardProgram!, "uBoardOrigin", boardOriginX, boardOriginZ);
        SetUniform1i(_boardProgram!, "uBoardSide", BoardSide);
        SetUniform1i(_boardProgram!, "uSeed", gen.Seed);
        SetUniform1f(_boardProgram!, "uBiomeSize", gen.BiomeSize);
        SetUniform1f(_boardProgram!, "uAmplification", gen.Amplification);
        SetUniform1f(_boardProgram!, "uErosionStrength", gen.ErosionStrength);
        SetUniform1f(_boardProgram!, "uContinentalness", gen.Continentalness);
        SetUniform1i(_boardProgram!, "uFlatPadHalfExtent", flatPadHalfExtent);
        SetUniform1i(_boardProgram!, "uTransitionBlocks", transitionBlocks);
        SetUniform1i(_boardProgram!, "uFillDepth", fillDepth);
    }

    private void BindEmitUniforms(
        PreviewTerrainWorldGenSettings gen,
        PreviewTerrainGrassBakeSettings grass,
        int cx0,
        int cz0,
        int chunkSize,
        int fillDepth,
        float metersPerTile,
        float surfaceWorldY)
    {
        _ = gen;
        SetUniform1i(_emitProgram!, "uBoardSide", BoardSide);
        SetUniform2i(_emitProgram!, "uChunkOrigin", cx0, cz0);
        SetUniform1i(_emitProgram!, "uChunkSize", chunkSize);
        SetUniform1i(_emitProgram!, "uFillDepth", fillDepth);
        SetUniform1f(_emitProgram!, "uSurfaceWorldY", surfaceWorldY);
        SetUniform1f(_emitProgram!, "uMetersPerTile", metersPerTile);
        SetUniform1i(_emitProgram!, "uGrassMode", (int)grass.Mode);
        SetUniform1i(_emitProgram!, "uBetterGrass", grass.BetterGrassEnabled ? 1 : 0);
        SetUniform1i(_emitProgram!, "uEmitOverlay", grass.EmitOverlay ? 1 : 0);
        SetUniform1i(_emitProgram!, "uCliffDelta", PreviewStageConstants.TerrainCliffDeltaBlocks);
        SetUniform1i(_emitProgram!, "uSolidFloorRelativeY", PreviewStageConstants.TerrainSolidFloorRelativeY);
        var maxLoc = _emitProgram!.GetUniformLocation("uMaxQuads");
        if (maxLoc >= 0)
        {
            _gl.Uniform1(maxLoc, (uint)_maxQuads);
        }
    }

    private static PreviewTerrainChunkMesh? PackMesh(
        TerrainChunkKey key,
        float[] payload,
        int quadCount,
        int minH,
        int maxH,
        int fillDepth,
        float surfaceWorldY,
        int cx0,
        int cz0,
        int chunkSize)
    {
        var buckets = PreviewTerrainMeshBaker.CreateMaterialBuckets(PreviewTerrainGrassSlots.MaxCount);
        for (var q = 0; q < quadCount; q++)
        {
            var baseIndex = q * FloatsPerQuadPayload;
            var material = (int)MathF.Round(payload[baseIndex]);
            if ((uint)material >= (uint)buckets.Length)
            {
                // Legacy intBitsToFloat payloads from older smokes.
                material = BitConverter.SingleToInt32Bits(payload[baseIndex]);
            }

            if ((uint)material >= (uint)buckets.Length)
            {
                continue;
            }

            var bucket = buckets[material];
            for (var i = 0; i < 48; i++)
            {
                bucket.Add(payload[baseIndex + 2 + i]);
            }
        }

        if (!PreviewTerrainMeshBaker.TryConcatMaterialBuckets(buckets, out var verts, out var indices, out var batches) ||
            indices.Length == 0)
        {
            return null;
        }

        var layerMin = PreviewTerrainMeshBaker.ResolveLayerMin(minH, fillDepth);
        var minY = surfaceWorldY + layerMin - 1;
        var maxY = surfaceWorldY + maxH;
        var cx1 = cx0 + chunkSize;
        var cz1 = cz0 + chunkSize;
        var boundsMin = new Vector3(cx0, minY, cz0);
        var boundsMax = new Vector3(cx1, maxY, cz1);
        var center = (boundsMin + boundsMax) * 0.5f;
        return new PreviewTerrainChunkMesh
        {
            Key = TerrainResidencyKey.Full(key),
            Lod = TerrainChunkLodKind.Full,
            InterleavedVertices = verts,
            Indices = indices,
            DrawBatches = batches,
            BoundsCenter = center,
            BoundsRadius = Vector3.Distance(center, boundsMax),
            MinRelativeHeight = layerMin,
            MaxRelativeHeight = maxH
        };
    }

    private void SetUniform1i(GlShaderProgram program, string name, int value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl.Uniform1(loc, value);
        }
    }

    private void SetUniform1f(GlShaderProgram program, string name, float value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl.Uniform1(loc, value);
        }
    }

    private void SetUniform2i(GlShaderProgram program, string name, int x, int y)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl.Uniform2(loc, x, y);
        }
    }
}
