using System.Numerics;
using System.Threading.Tasks;

using AutoPBR.Preview;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed class GlGpuDrawCommandCompactor : IDisposable
{
    private const uint SourceCommandsBinding = 0;
    private const uint VisibilityFlagsBinding = 1;
    private const uint OutputCommandsBinding = 2;
    private const uint OutputCounterBinding = 3;
    private const uint BatchCullRecordsBinding = 4;
    private const uint ShaderStorageBarrierBit = 0x00002000;
    private const uint CommandBarrierBit = 0x00000040;
    private const uint BufferUpdateBarrierBit = 0x00000200;
    private const int LocalSizeX = 64;
    private const int CullRecordFloats = 8;
    private const int ParallelCullRecordMinCommands = 64;
    private static readonly ParallelOptions CullRecordParallelOptions = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(
            Environment.ProcessorCount / 2,
            1,
            4),
    };

    private readonly GL _gl;
    private readonly GlIndirectDrawCommandBuffer _outputCommands;
    private uint _visibilityBuffer;
    private uint _counterBuffer;
    private uint _cullRecordBuffer;
    private int _visibilityCapacity;
    private int _cullRecordCapacity;
    private uint[] _visibilityScratch = [];
    private float[] _cullRecordScratch = [];
    private bool _disposed;

    public GlGpuDrawCommandCompactor(GL gl)
    {
        _gl = gl;
        _outputCommands = new GlIndirectDrawCommandBuffer(gl);
    }

    public GlIndirectDrawCommandBuffer OutputCommands => _outputCommands;

    public uint CounterBufferHandle => _counterBuffer;

    public int LastVisibleCount { get; private set; }

    public bool Dispatch(
        GlShaderProgram program,
        GlIndirectDrawCommandBuffer sourceCommands,
        ReadOnlySpan<uint> visibilityFlags,
        bool readBackCounter = false,
        bool collectDiagnostics = false,
        int outputCapacity = 0,
        bool preserveOrder = false)
    {
        if (_disposed ||
            program is not { IsValid: true } ||
            !sourceCommands.IsValid ||
            sourceCommands.Handle == 0 ||
            visibilityFlags.Length < sourceCommands.CommandCount)
        {
            LastVisibleCount = 0;
            return false;
        }

        var commandCount = sourceCommands.CommandCount;
        outputCapacity = ResolveOutputCapacity(commandCount, outputCapacity);
        if (!_outputCommands.EnsureCommandCapacity(outputCapacity))
        {
            LastVisibleCount = 0;
            return false;
        }

        UploadVisibilityFlags(visibilityFlags[..commandCount]);
        ResetCounter();

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, SourceCommandsBinding, sourceCommands.Handle);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, VisibilityFlagsBinding, _visibilityBuffer);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, OutputCommandsBinding, _outputCommands.Handle);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, OutputCounterBinding, _counterBuffer);

        program.Use();
        var commandCountLoc = program.GetUniformLocation("uCommandCount");
        if (commandCountLoc >= 0)
        {
            _gl.Uniform1(commandCountLoc, (uint)commandCount);
        }

        SetUseGpuCulling(program, false);
        SetFirstCommand(program, 0);
        SetOutputCapacity(program, outputCapacity);
        SetCollectDiagnostics(program, collectDiagnostics);
        SetPreserveOrder(program, preserveOrder);
        SetHiZUniforms(program, enabled: false, hiZ: null, viewProj: null, textureUnit: 0);
        SetVoxelOcclusionUniforms(program, enabled: false, atlas: null, textureUnit: 0);
        _gl.DispatchCompute(preserveOrder ? 1u : (uint)((commandCount + LocalSizeX - 1) / LocalSizeX), 1, 1);
        _gl.MemoryBarrier(ShaderStorageBarrierBit | CommandBarrierBit | BufferUpdateBarrierBit);

        if (readBackCounter)
        {
            LastVisibleCount = Math.Min(ReadCounter(), outputCapacity);
            _outputCommands.SetCommandCount(LastVisibleCount);
        }
        else
        {
            LastVisibleCount = outputCapacity;
        }

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, SourceCommandsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, VisibilityFlagsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, OutputCommandsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, OutputCounterBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, BatchCullRecordsBinding, 0);
        return true;
    }

    public bool DispatchWithGpuCulling(
        GlShaderProgram program,
        GlIndirectDrawCommandBuffer sourceCommands,
        IReadOnlyList<PreviewDrawBatch> batches,
        ReadOnlySpan<Vector4> frustumPlanes,
        Vector3 cameraPosition,
        bool readBackCounter = false,
        bool collectDiagnostics = false,
        int outputCapacity = 0,
        bool preserveOrder = false,
        float boundsPadding = 0f,
        bool enableHiZ = false,
        GlHierarchicalZPyramid? hiZ = null,
        Matrix4x4? viewProj = null,
        int hiZTextureUnit = 0,
        bool enableVoxelOcclusion = false,
        GlTerrainOccluderAtlas? voxelAtlas = null,
        int voxelTextureUnit = 0) =>
        DispatchWithGpuCulling(
            program,
            sourceCommands,
            batches,
            frustumPlanes,
            cameraPosition,
            Matrix4x4.Identity,
            0,
            sourceCommands.CommandCount,
            readBackCounter,
            collectDiagnostics,
            outputCapacity,
            preserveOrder,
            boundsPadding,
            enableHiZ,
            hiZ,
            viewProj,
            hiZTextureUnit,
            enableVoxelOcclusion,
            voxelAtlas,
            voxelTextureUnit);

    public bool DispatchWithGpuCulling(
        GlShaderProgram program,
        GlIndirectDrawCommandBuffer sourceCommands,
        IReadOnlyList<PreviewDrawBatch> batches,
        ReadOnlySpan<Vector4> frustumPlanes,
        Vector3 cameraPosition,
        Matrix4x4 modelMatrix,
        int firstCommand,
        int commandCount,
        bool readBackCounter = false,
        bool collectDiagnostics = false,
        int outputCapacity = 0,
        bool preserveOrder = false,
        float boundsPadding = 0f,
        bool enableHiZ = false,
        GlHierarchicalZPyramid? hiZ = null,
        Matrix4x4? viewProj = null,
        int hiZTextureUnit = 0,
        bool enableVoxelOcclusion = false,
        GlTerrainOccluderAtlas? voxelAtlas = null,
        int voxelTextureUnit = 0)
    {
        if (_disposed ||
            program is not { IsValid: true } ||
            !sourceCommands.IsValid ||
            sourceCommands.Handle == 0 ||
            batches.Count < sourceCommands.CommandCount ||
            firstCommand < 0 ||
            commandCount <= 0 ||
            firstCommand > sourceCommands.CommandCount - commandCount ||
            frustumPlanes.Length < 6)
        {
            LastVisibleCount = 0;
            return false;
        }

        var useVoxel = enableVoxelOcclusion && voxelAtlas is { IsValid: true };
        var useHiZ = !useVoxel && enableHiZ && hiZ is { IsValid: true } && viewProj is not null;
        outputCapacity = ResolveOutputCapacity(commandCount, outputCapacity);
        if (!_outputCommands.EnsureCommandCapacity(outputCapacity))
        {
            LastVisibleCount = 0;
            return false;
        }

        UploadCullRecords(batches, sourceCommands.CommandCount, modelMatrix, boundsPadding);
        UploadAllVisibleFlags(commandCount);
        ResetCounter();

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, SourceCommandsBinding, sourceCommands.Handle);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, VisibilityFlagsBinding, _visibilityBuffer);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, OutputCommandsBinding, _outputCommands.Handle);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, OutputCounterBinding, _counterBuffer);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, BatchCullRecordsBinding, _cullRecordBuffer);

        program.Use();
        var commandCountLoc = program.GetUniformLocation("uCommandCount");
        if (commandCountLoc >= 0)
        {
            _gl.Uniform1(commandCountLoc, (uint)commandCount);
        }

        SetUseGpuCulling(program, true);
        SetFirstCommand(program, firstCommand);
        SetOutputCapacity(program, outputCapacity);
        SetCollectDiagnostics(program, collectDiagnostics);
        SetPreserveOrder(program, preserveOrder);
        SetCameraPosition(program, cameraPosition);
        SetFrustumPlanes(program, frustumPlanes[..6]);
        SetHiZUniforms(program, useHiZ, hiZ, viewProj, hiZTextureUnit);
        SetVoxelOcclusionUniforms(program, useVoxel, voxelAtlas, voxelTextureUnit);

        _gl.DispatchCompute(preserveOrder ? 1u : (uint)((commandCount + LocalSizeX - 1) / LocalSizeX), 1, 1);
        _gl.MemoryBarrier(ShaderStorageBarrierBit | CommandBarrierBit | BufferUpdateBarrierBit);

        if (readBackCounter)
        {
            LastVisibleCount = Math.Min(ReadCounter(), outputCapacity);
            _outputCommands.SetCommandCount(LastVisibleCount);
        }
        else
        {
            LastVisibleCount = outputCapacity;
        }

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, SourceCommandsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, VisibilityFlagsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, OutputCommandsBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, OutputCounterBinding, 0);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, BatchCullRecordsBinding, 0);
        if (useHiZ)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + hiZTextureUnit);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        if (useVoxel)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + voxelTextureUnit);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        return true;
    }

    public GlGpuDrawReductionSnapshot ReadReductionDiagnostics()
    {
        if (_disposed || _counterBuffer == 0)
        {
            return default;
        }

        Span<uint> dwords = stackalloc uint[GlGpuDrawReductionSnapshot.DwordCount];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counterBuffer);
        _gl.GetBufferSubData<uint>(BufferTargetARB.ShaderStorageBuffer, sizeof(uint), dwords);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        return GlGpuDrawReductionSnapshot.FromDwords(dwords);
    }

    public uint[] ReadOutputCommandDwords(int commandCount)
    {
        if (_disposed || commandCount <= 0 || _outputCommands.Handle == 0)
        {
            return [];
        }

        var dwordCount = checked(commandCount * GlIndirectDrawCommandBuffer.CommandDwords);
        var result = new uint[dwordCount];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _outputCommands.Handle);
        _gl.GetBufferSubData<uint>(BufferTargetARB.ShaderStorageBuffer, 0, result.AsSpan());
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        return result;
    }

    private void UploadVisibilityFlags(ReadOnlySpan<uint> visibilityFlags)
    {
        _visibilityBuffer = _visibilityBuffer == 0 ? _gl.GenBuffer() : _visibilityBuffer;
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _visibilityBuffer);
        var byteCount = visibilityFlags.Length * sizeof(uint);
        if (byteCount <= _visibilityCapacity)
        {
            _gl.BufferSubData<uint>(BufferTargetARB.ShaderStorageBuffer, 0, visibilityFlags);
        }
        else
        {
            _gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, visibilityFlags, BufferUsageARB.DynamicDraw);
            _visibilityCapacity = byteCount;
        }

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private void UploadAllVisibleFlags(int commandCount)
    {
        if (_visibilityScratch.Length < commandCount)
        {
            _visibilityScratch = new uint[Math.Max(commandCount, 64)];
        }

        _visibilityScratch.AsSpan(0, commandCount).Fill(1u);
        UploadVisibilityFlags(_visibilityScratch.AsSpan(0, commandCount));
    }

    private void UploadCullRecords(
        IReadOnlyList<PreviewDrawBatch> batches,
        int commandCount,
        Matrix4x4 modelMatrix,
        float boundsPadding)
    {
        var floatCount = checked(commandCount * CullRecordFloats);
        if (_cullRecordScratch.Length < floatCount)
        {
            _cullRecordScratch = new float[Math.Max(floatCount, 128)];
        }

        var records = _cullRecordScratch;
        Array.Clear(records, 0, floatCount);
        if (commandCount >= ParallelCullRecordMinCommands)
        {
            Parallel.For(0, commandCount, CullRecordParallelOptions, i =>
            {
                WriteCullRecord(
                    records.AsSpan(i * CullRecordFloats, CullRecordFloats),
                    batches[i],
                    modelMatrix,
                    boundsPadding);
            });
        }
        else
        {
            for (var i = 0; i < commandCount; i++)
            {
                WriteCullRecord(
                    records.AsSpan(i * CullRecordFloats, CullRecordFloats),
                    batches[i],
                    modelMatrix,
                    boundsPadding);
            }
        }

        _cullRecordBuffer = _cullRecordBuffer == 0 ? _gl.GenBuffer() : _cullRecordBuffer;
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _cullRecordBuffer);
        var byteCount = floatCount * sizeof(float);
        var uploadSpan = records.AsSpan(0, floatCount);
        if (byteCount <= _cullRecordCapacity)
        {
            _gl.BufferSubData<float>(BufferTargetARB.ShaderStorageBuffer, 0, uploadSpan);
        }
        else
        {
            _gl.BufferData<float>(BufferTargetARB.ShaderStorageBuffer, uploadSpan, BufferUsageARB.DynamicDraw);
            _cullRecordCapacity = byteCount;
        }

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    internal static void WriteCullRecord(Span<float> destination, PreviewDrawBatch batch)
        => WriteCullRecord(destination, batch, Matrix4x4.Identity);

    internal static void WriteCullRecord(
        Span<float> destination,
        PreviewDrawBatch batch,
        Matrix4x4 modelMatrix,
        float boundsPadding = 0f)
    {
        if (destination.Length < CullRecordFloats)
        {
            throw new ArgumentException("Cull record destination must hold eight floats.", nameof(destination));
        }

        var localCenter = batch.BoundsCenter;
        var center = new Vector3(
            modelMatrix.M11 * localCenter.X + modelMatrix.M12 * localCenter.Y +
            modelMatrix.M13 * localCenter.Z + modelMatrix.M14,
            modelMatrix.M21 * localCenter.X + modelMatrix.M22 * localCenter.Y +
            modelMatrix.M23 * localCenter.Z + modelMatrix.M24,
            modelMatrix.M31 * localCenter.X + modelMatrix.M32 * localCenter.Y +
            modelMatrix.M33 * localCenter.Z + modelMatrix.M34);
        var modelScale = MathF.Max(
            new Vector3(modelMatrix.M11, modelMatrix.M12, modelMatrix.M13).Length(),
            MathF.Max(
                new Vector3(modelMatrix.M21, modelMatrix.M22, modelMatrix.M23).Length(),
                new Vector3(modelMatrix.M31, modelMatrix.M32, modelMatrix.M33).Length()));
        var radius = batch.HasBounds
            ? (MathF.Max(0f, batch.BoundsRadius) + MathF.Max(0f, boundsPadding)) * modelScale
            : -1f;
        destination[0] = center.X;
        destination[1] = center.Y;
        destination[2] = center.Z;
        destination[3] = radius;
        destination[4] = batch.LodMaxDistance > 0f && float.IsFinite(batch.LodMaxDistance)
            ? batch.LodMaxDistance
            : 0f;
    }

    private void SetUseGpuCulling(GlShaderProgram program, bool enabled)
    {
        var loc = program.GetUniformLocation("uUseGpuCulling");
        if (loc >= 0)
        {
            _gl.Uniform1(loc, enabled ? 1 : 0);
        }
    }

    private void SetFirstCommand(GlShaderProgram program, int firstCommand)
    {
        var loc = program.GetUniformLocation("uFirstCommand");
        if (loc >= 0)
        {
            _gl.Uniform1(loc, (uint)firstCommand);
        }
    }

    private void SetOutputCapacity(GlShaderProgram program, int outputCapacity)
    {
        var loc = program.GetUniformLocation("uOutputCapacity");
        if (loc >= 0)
        {
            _gl.Uniform1(loc, (uint)outputCapacity);
        }
    }

    private void SetCollectDiagnostics(GlShaderProgram program, bool enabled)
    {
        var loc = program.GetUniformLocation("uCollectDiagnostics");
        if (loc >= 0)
        {
            _gl.Uniform1(loc, enabled ? 1 : 0);
        }
    }

    private void SetPreserveOrder(GlShaderProgram program, bool enabled)
    {
        var loc = program.GetUniformLocation("uPreserveOrder");
        if (loc >= 0)
        {
            _gl.Uniform1(loc, enabled ? 1 : 0);
        }
    }

    private void SetCameraPosition(GlShaderProgram program, Vector3 cameraPosition)
    {
        var loc = program.GetUniformLocation("uCameraPos");
        if (loc >= 0)
        {
            _gl.Uniform3(loc, cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        }
    }

    private void SetFrustumPlanes(GlShaderProgram program, ReadOnlySpan<Vector4> frustumPlanes)
    {
        for (var i = 0; i < 6; i++)
        {
            var loc = program.GetUniformLocation($"uFrustumPlanes[{i}]");
            if (loc < 0)
            {
                continue;
            }

            var p = frustumPlanes[i];
            _gl.Uniform4(loc, p.X, p.Y, p.Z, p.W);
        }
    }

    private void SetHiZUniforms(
        GlShaderProgram program,
        bool enabled,
        GlHierarchicalZPyramid? hiZ,
        Matrix4x4? viewProj,
        int textureUnit)
    {
        var useLoc = program.GetUniformLocation("uUseHiZ");
        if (useLoc >= 0)
        {
            _gl.Uniform1(useLoc, enabled ? 1 : 0);
        }

        if (!enabled || hiZ is null || viewProj is null)
        {
            return;
        }

        hiZ.Bind(TextureUnit.Texture0 + textureUnit);
        var samplerLoc = program.GetUniformLocation("uHiZ");
        if (samplerLoc >= 0)
        {
            _gl.Uniform1(samplerLoc, textureUnit);
        }

        var sizeLoc = program.GetUniformLocation("uHiZSize");
        if (sizeLoc >= 0)
        {
            _gl.Uniform2(sizeLoc, hiZ.Width, hiZ.Height);
        }

        var maxLevelLoc = program.GetUniformLocation("uHiZMaxLevel");
        if (maxLevelLoc >= 0)
        {
            _gl.Uniform1(maxLevelLoc, hiZ.MaxLevel);
        }

        var epsilonLoc = program.GetUniformLocation("uDepthEpsilon");
        if (epsilonLoc >= 0)
        {
            _gl.Uniform1(epsilonLoc, PreviewHierarchicalZMath.DepthEpsilon);
        }

        var vpLoc = program.GetUniformLocation("uViewProj");
        if (vpLoc >= 0)
        {
            var mt = Matrix4x4.Transpose(viewProj.Value);
            _gl.UniformMatrix4(vpLoc, 1, false, in mt.M11);
        }
    }

    private void SetVoxelOcclusionUniforms(
        GlShaderProgram program,
        bool enabled,
        GlTerrainOccluderAtlas? atlas,
        int textureUnit)
    {
        var useLoc = program.GetUniformLocation("uUseVoxelOcclusion");
        if (useLoc >= 0)
        {
            _gl.Uniform1(useLoc, enabled ? 1 : 0);
        }

        if (!enabled || atlas is null)
        {
            return;
        }

        atlas.Bind(TextureUnit.Texture0 + textureUnit);
        var samplerLoc = program.GetUniformLocation("uVoxelHeightAtlas");
        if (samplerLoc >= 0)
        {
            _gl.Uniform1(samplerLoc, textureUnit);
        }

        var originLoc = program.GetUniformLocation("uVoxelAtlasOrigin");
        if (originLoc >= 0)
        {
            _gl.Uniform2(originLoc, atlas.OriginX, atlas.OriginZ);
        }

        var sizeLoc = program.GetUniformLocation("uVoxelAtlasSize");
        if (sizeLoc >= 0)
        {
            _gl.Uniform2(sizeLoc, atlas.Width, atlas.Height);
        }

        var groundLoc = program.GetUniformLocation("uVoxelGroundPlaneY");
        if (groundLoc >= 0)
        {
            _gl.Uniform1(groundLoc, atlas.GroundPlaneWorldY);
        }

        var stepsLoc = program.GetUniformLocation("uVoxelMaxSteps");
        if (stepsLoc >= 0)
        {
            _gl.Uniform1(stepsLoc, PreviewVoxelDdaMath.DefaultMaxSteps);
        }
    }

    private void ResetCounter()
    {
        Span<uint> zero = stackalloc uint[1 + GlGpuDrawReductionSnapshot.DwordCount];
        zero.Clear();
        _counterBuffer = _counterBuffer == 0 ? _gl.GenBuffer() : _counterBuffer;
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counterBuffer);
        _gl.BufferData<uint>(BufferTargetARB.ShaderStorageBuffer, zero, BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private static int ResolveOutputCapacity(int commandCount, int requestedCapacity) =>
        requestedCapacity <= 0
            ? commandCount
            : Math.Clamp(requestedCapacity, 1, commandCount);

    private int ReadCounter()
    {
        Span<uint> value = stackalloc uint[1];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counterBuffer);
        _gl.GetBufferSubData<uint>(BufferTargetARB.ShaderStorageBuffer, 0, value);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        return checked((int)value[0]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outputCommands.Dispose();
        if (_visibilityBuffer != 0)
        {
            _gl.DeleteBuffer(_visibilityBuffer);
            _visibilityBuffer = 0;
        }

        if (_counterBuffer != 0)
        {
            _gl.DeleteBuffer(_counterBuffer);
            _counterBuffer = 0;
        }

        if (_cullRecordBuffer != 0)
        {
            _gl.DeleteBuffer(_cullRecordBuffer);
            _cullRecordBuffer = 0;
        }
    }
}
