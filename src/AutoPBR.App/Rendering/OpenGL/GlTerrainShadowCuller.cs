using System.Numerics;
using System.Runtime.InteropServices;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Desktop compute cull of terrain chunks into near/mid/far MultiDrawIndirect command lists.
/// Compacted commands + atomic counters feed <c>MultiDrawIndirectCount</c> with no CPU readback.
/// </summary>
internal sealed class GlTerrainShadowCuller : IDisposable
{
    private const uint ChunkRecordsBinding = 0;
    private const uint SourceCommandsBinding = 1;
    private const uint NearCommandsBinding = 2;
    private const uint MidCommandsBinding = 3;
    private const uint FarCommandsBinding = 4;
    private const uint CountersBinding = 5;
    private const uint ShaderStorageBarrierBit = 0x00002000;
    private const uint CommandBarrierBit = 0x00000040;
    private const uint BufferUpdateBarrierBit = 0x00000200;
    private const int LocalSizeX = 64;
    private const int FloatsPerRecord = 8;

    private readonly GL _gl;
    private readonly GlIndirectDrawCommandBuffer _nearCommands;
    private readonly GlIndirectDrawCommandBuffer _midCommands;
    private readonly GlIndirectDrawCommandBuffer _farCommands;
    private uint _chunkRecords;
    private uint _counters;
    private int _chunkCapacity;
    private int _commandCapacity;
    private float[] _recordScratch = [];
    private bool _disposed;

    public GlTerrainShadowCuller(GL gl)
    {
        _gl = gl;
        _nearCommands = new GlIndirectDrawCommandBuffer(gl);
        _midCommands = new GlIndirectDrawCommandBuffer(gl);
        _farCommands = new GlIndirectDrawCommandBuffer(gl);
    }

    public GlIndirectDrawCommandBuffer NearCommands => _nearCommands;
    public GlIndirectDrawCommandBuffer MidCommands => _midCommands;
    public GlIndirectDrawCommandBuffer FarCommands => _farCommands;
    public uint CounterBufferHandle => _counters;
    public int MaxDrawCount { get; private set; }

    public bool Dispatch(
        GlShaderProgram program,
        GlIndirectDrawCommandBuffer sourceCommands,
        ReadOnlySpan<TerrainShadowCullRecord> chunks,
        ReadOnlySpan<Vector4> nearPlanes,
        ReadOnlySpan<Vector4> midPlanes,
        ReadOnlySpan<Vector4> farPlanes,
        Vector3 cameraPosition,
        float nearMaxDist,
        float midMaxDist,
        float farMaxDist,
        float inclusionPad,
        bool cascadesActive)
    {
        MaxDrawCount = 0;
        if (_disposed ||
            program is not { IsValid: true } ||
            !sourceCommands.IsValid ||
            sourceCommands.Handle == 0 ||
            chunks.Length == 0 ||
            sourceCommands.CommandCount < chunks.Length ||
            nearPlanes.Length < 6 ||
            midPlanes.Length < 6 ||
            farPlanes.Length < 6)
        {
            return false;
        }

        var count = chunks.Length;
        if (!EnsureCapacity(count))
        {
            return false;
        }

        UploadChunkRecords(chunks);
        ResetCounters();

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, ChunkRecordsBinding, _chunkRecords);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, SourceCommandsBinding, sourceCommands.Handle);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, NearCommandsBinding, _nearCommands.Handle);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, MidCommandsBinding, _midCommands.Handle);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, FarCommandsBinding, _farCommands.Handle);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, CountersBinding, _counters);

        program.Use();
        SetUint(program, "uChunkCount", (uint)count);
        SetUint(program, "uOutputCapacity", (uint)_commandCapacity);
        SetInt(program, "uCascadesActive", cascadesActive ? 1 : 0);
        SetVec3(program, "uCameraPos", cameraPosition);
        SetFloat(program, "uNearMaxDist", nearMaxDist);
        SetFloat(program, "uMidMaxDist", midMaxDist);
        SetFloat(program, "uFarMaxDist", farMaxDist);
        SetFloat(program, "uInclusionPad", MathF.Max(0f, inclusionPad));
        SetPlanes(program, "uNearPlanes", nearPlanes);
        SetPlanes(program, "uMidPlanes", midPlanes);
        SetPlanes(program, "uFarPlanes", farPlanes);

        _gl.DispatchCompute((uint)((count + LocalSizeX - 1) / LocalSizeX), 1, 1);
        _gl.MemoryBarrier(ShaderStorageBarrierBit | CommandBarrierBit | BufferUpdateBarrierBit);

        MaxDrawCount = count;
        _nearCommands.SetCommandCount(count);
        _midCommands.SetCommandCount(count);
        _farCommands.SetCommandCount(count);

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, ChunkRecordsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, SourceCommandsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, NearCommandsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, MidCommandsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, FarCommandsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, CountersBinding, 0);
        return true;
    }

    private void UploadChunkRecords(ReadOnlySpan<TerrainShadowCullRecord> chunks)
    {
        var floatCount = chunks.Length * FloatsPerRecord;
        if (_recordScratch.Length < floatCount)
        {
            _recordScratch = new float[floatCount];
        }

        for (var i = 0; i < chunks.Length; i++)
        {
            var c = chunks[i];
            var o = i * FloatsPerRecord;
            _recordScratch[o] = c.Center.X;
            _recordScratch[o + 1] = c.Center.Y;
            _recordScratch[o + 2] = c.Center.Z;
            _recordScratch[o + 3] = c.Radius;
            _recordScratch[o + 4] = c.IsFullLod ? 0f : 1f;
            _recordScratch[o + 5] = c.CandidateIndex;
            _recordScratch[o + 6] = 0f;
            _recordScratch[o + 7] = 0f;
        }

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _chunkRecords);
        _gl.BufferSubData<float>(BufferTargetARB.ShaderStorageBuffer, 0, _recordScratch.AsSpan(0, floatCount));
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private void ResetCounters()
    {
        Span<uint> zeros = stackalloc uint[3];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counters);
        _gl.BufferSubData<uint>(BufferTargetARB.ShaderStorageBuffer, 0, zeros);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private bool EnsureCapacity(int chunkCount)
    {
        if (chunkCount <= 0)
        {
            return false;
        }

        if (_chunkCapacity < chunkCount)
        {
            RecreateBuffer(ref _chunkRecords, (nuint)(chunkCount * FloatsPerRecord * sizeof(float)));
            _chunkCapacity = chunkCount;
        }

        if (_commandCapacity < chunkCount)
        {
            if (!_nearCommands.EnsureCommandCapacity(chunkCount) ||
                !_midCommands.EnsureCommandCapacity(chunkCount) ||
                !_farCommands.EnsureCommandCapacity(chunkCount))
            {
                return false;
            }

            _commandCapacity = chunkCount;
        }

        if (_counters == 0)
        {
            RecreateBuffer(ref _counters, 3 * sizeof(uint));
        }

        return _chunkRecords != 0 &&
               _nearCommands.Handle != 0 &&
               _midCommands.Handle != 0 &&
               _farCommands.Handle != 0 &&
               _counters != 0;
    }

    private void RecreateBuffer(ref uint handle, nuint byteSize)
    {
        if (handle != 0)
        {
            _gl.DeleteBuffer(handle);
            handle = 0;
        }

        handle = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, handle);
        unsafe
        {
            _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, byteSize, null, BufferUsageARB.DynamicDraw);
        }

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private void SetUint(GlShaderProgram program, string name, uint value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl.Uniform1(loc, value);
        }
    }

    private void SetInt(GlShaderProgram program, string name, int value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl.Uniform1(loc, value);
        }
    }

    private void SetFloat(GlShaderProgram program, string name, float value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl.Uniform1(loc, value);
        }
    }

    private void SetVec3(GlShaderProgram program, string name, Vector3 value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl.Uniform3(loc, value.X, value.Y, value.Z);
        }
    }

    private void SetPlanes(GlShaderProgram program, string name, ReadOnlySpan<Vector4> planes)
    {
        for (var i = 0; i < 6; i++)
        {
            var loc = program.GetUniformLocation($"{name}[{i}]");
            if (loc < 0)
            {
                continue;
            }

            var p = planes[i];
            _gl.Uniform4(loc, p.X, p.Y, p.Z, p.W);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nearCommands.Dispose();
        _midCommands.Dispose();
        _farCommands.Dispose();
        Delete(ref _chunkRecords);
        Delete(ref _counters);
        MaxDrawCount = 0;
    }

    private void Delete(ref uint handle)
    {
        if (handle == 0)
        {
            return;
        }

        _gl.DeleteBuffer(handle);
        handle = 0;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct TerrainShadowCullRecord
{
    public readonly Vector3 Center;
    public readonly float Radius;
    public readonly bool IsFullLod;
    public readonly float CandidateIndex;

    public TerrainShadowCullRecord(Vector3 center, float radius, bool isFullLod, int candidateIndex)
    {
        Center = center;
        Radius = MathF.Max(0f, radius);
        IsFullLod = isFullLod;
        CandidateIndex = candidateIndex;
    }
}
