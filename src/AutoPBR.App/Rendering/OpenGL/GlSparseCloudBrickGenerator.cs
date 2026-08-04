using System.Numerics;
using System.Runtime.InteropServices;

using AutoPBR.App.Rendering.Scene;
using AutoPBR.PreviewGpuAssets;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CQ4.4 bounded compute generator. Atlas writes become resident only after the dispatch fence
/// signals and every workgroup publishes its completion marker.
/// </summary>
internal sealed class GlSparseCloudBrickGenerator : IDisposable
{
    private const uint TextureFetchBarrierBit = 0x00000008;
    private const uint ShaderImageAccessBarrierBit = 0x00000020;
    private const uint ShaderStorageBarrierBit = 0x00002000;

    private readonly GL _gl;
    private readonly GlShaderProgram _program;
    private readonly Uniforms _uniforms;
    private readonly List<PreviewSparseCloudBrickGenerationRecord> _inFlight = [];
    private uint _templateTexture;
    private uint _requestBuffer;
    private uint _statusBuffer;
    private nint _generationFence;
    private int _generationId;
    private bool _disposed;
    private PreviewSparseCloudFaultInjectPoint _faultInject =
        PreviewSparseCloudFaultInjectPoint.None;

    private GlSparseCloudBrickGenerator(
        GL gl,
        GlShaderProgram program)
    {
        _gl = gl;
        _program = program;
        _uniforms = Uniforms.Resolve(program);
    }

    public bool HasPendingGeneration => _generationFence != 0;
    public int GenerationId => _generationId;
    public int InFlightCount => _inFlight.Count;
    public uint TemplateTextureHandle => _templateTexture;
    public uint RequestBufferHandle => _requestBuffer;
    public uint StatusBufferHandle => _statusBuffer;

    internal void SetFaultInjectForTests(
        PreviewSparseCloudFaultInjectPoint point) =>
        _faultInject = point;

    public static bool TryCreate(
        GL gl,
        GlShaderProgram program,
        PreviewSparseCloudTemplateAssetSet templates,
        out GlSparseCloudBrickGenerator? generator,
        out string diagnostic)
    {
        generator = null;
        if (!IsTemplateSetAcceptable(templates, out diagnostic))
        {
            return false;
        }

        if (!program.IsValid)
        {
            diagnostic = "program-invalid";
            return false;
        }

        var candidate = new GlSparseCloudBrickGenerator(gl, program);
        try
        {
            if (!candidate.TryAllocateAndUpload(templates, out diagnostic))
            {
                return false;
            }

            generator = candidate;
            candidate = null!;
            return true;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    /// <summary>
    /// CA2.4: accepts either the frozen v1 or the parallel asymmetric v2 template ABI, provided
    /// the loaded set's declared version, template count, and total byte length exactly match
    /// that version's contract. GL-independent so it can be exercised without a live context.
    /// </summary>
    internal static bool IsTemplateSetAcceptable(
        PreviewSparseCloudTemplateAssetSet templates,
        out string diagnostic)
    {
        IReadOnlyList<PreviewSparseCloudTemplateAssetDescriptor> expectedAssets;
        long expectedTotalByteLength;
        switch (templates.AssetVersion)
        {
            case PreviewSparseCloudTemplateAssetContractV2.AssetVersion:
                expectedAssets = PreviewSparseCloudTemplateAssetContractV2.Assets;
                expectedTotalByteLength = PreviewSparseCloudTemplateAssetContractV2.TotalByteLength;
                break;
            case PreviewSparseCloudTemplateAssetContract.AssetVersion:
                expectedAssets = PreviewSparseCloudTemplateAssetContract.Assets;
                expectedTotalByteLength = PreviewSparseCloudTemplateAssetContract.TotalByteLength;
                break;
            default:
                diagnostic = "template-set-invalid";
                return false;
        }

        if (templates.Templates.Count != expectedAssets.Count ||
            templates.ByteLength != expectedTotalByteLength)
        {
            diagnostic = "template-set-invalid";
            return false;
        }

        diagnostic = "valid";
        return true;
    }

    public bool TryDispatch(
        GlSparseCloudVolumeResources resources,
        PreviewSparseCloudBrickAllocator allocator,
        IReadOnlyList<PreviewSparseCloudLogicalBrickKey> entering,
        in PreviewSparseCloudBrickGenerationInputs inputs,
        out int dispatchedCount,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(entering);
        dispatchedCount = 0;
        if (_disposed ||
            !_program.IsValid ||
            !resources.IsAllocated ||
            _templateTexture == 0 ||
            _requestBuffer == 0 ||
            _statusBuffer == 0)
        {
            diagnostic = "generator-or-resources-unavailable";
            return false;
        }

        if (HasPendingGeneration)
        {
            diagnostic = "generation-pending";
            return false;
        }

        if (entering.Count >
            PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame)
        {
            diagnostic = $"entering-over-cap-{entering.Count}";
            return false;
        }

        _inFlight.Clear();
        for (var index = 0; index < entering.Count; index++)
        {
            var key = entering[index];
            var priority = 1f -
                index /
                (float)Math.Max(entering.Count, 1);
            if (!allocator.TryRequest(
                    key,
                    inputs.Frame,
                    priority,
                    out var residency))
            {
                continue;
            }

            if (residency.State != PreviewSparseCloudBrickState.Requested ||
                !allocator.MarkGenerating(key))
            {
                continue;
            }

            _inFlight.Add(
                PreviewSparseCloudBrickGenerationContract.CreateRecord(
                    key,
                    residency.PhysicalBrickIndex));
        }

        if (_inFlight.Count == 0)
        {
            diagnostic =
                entering.Count == 0
                    ? "no-entering-bricks"
                    : "no-allocatable-bricks";
            return true;
        }

        try
        {
            FlushErrors();
            UploadDispatchBuffers();
            _program.Use();
            SetInt(_uniforms.EnvelopeTemplates, 0);
            SetInt(_uniforms.WeatherMap, 1);
            SetInt(_uniforms.HasWeatherMap, inputs.WeatherTexture != 0 ? 1 : 0);
            SetInt(_uniforms.RequestCount, _inFlight.Count);
            SetFloat(_uniforms.CloudBaseWorldY, inputs.CloudBaseWorldY);
            SetFloat(_uniforms.CloudTopWorldY, inputs.CloudTopWorldY);
            SetFloat(_uniforms.Density, inputs.Density);
            SetFloat(_uniforms.CoverageScale, inputs.CoverageScale);
            SetFloat(_uniforms.VolumeSize, inputs.VolumeSize);
            SetVector3(_uniforms.WindOffset, inputs.WindOffset);
            SetInt(_uniforms.StyleBias, Math.Clamp(inputs.StyleBias, 0, 4));

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2DArray, _templateTexture);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, inputs.WeatherTexture);
            _gl.BindBufferBase(
                BufferTargetARB.ShaderStorageBuffer,
                0,
                _requestBuffer);
            _gl.BindBufferBase(
                BufferTargetARB.ShaderStorageBuffer,
                1,
                _statusBuffer);
            _gl.BindImageTexture(
                0,
                resources.AtlasTextureHandle,
                0,
                true,
                0,
                GLEnum.WriteOnly,
                GLEnum.RG8);

            _gl.DispatchCompute((uint)_inFlight.Count, 1, 1);
            if (_faultInject == PreviewSparseCloudFaultInjectPoint.Dispatch)
            {
                diagnostic = "injected-dispatch-failure";
                return false;
            }

            var dispatchError = _gl.GetError();
            if (dispatchError != GLEnum.NoError)
            {
                diagnostic = "dispatch-" + dispatchError;
                return false;
            }

            _gl.MemoryBarrier(
                ShaderImageAccessBarrierBit |
                TextureFetchBarrierBit |
                ShaderStorageBarrierBit);
            if (_faultInject == PreviewSparseCloudFaultInjectPoint.Barrier)
            {
                diagnostic = "injected-barrier-failure";
                return false;
            }

            var barrierError = _gl.GetError();
            if (barrierError != GLEnum.NoError)
            {
                diagnostic = "barrier-" + barrierError;
                return false;
            }

            if (_faultInject == PreviewSparseCloudFaultInjectPoint.Fence)
            {
                diagnostic = "injected-fence-failure";
                return false;
            }

            _generationFence = _gl.FenceSync(
                SyncCondition.SyncGpuCommandsComplete,
                SyncBehaviorFlags.None);
            if (_generationFence == 0)
            {
                diagnostic = "generation-fence-unavailable";
                return false;
            }

            _gl.Flush();
            dispatchedCount = _inFlight.Count;
            diagnostic =
                $"dispatched-{dispatchedCount};frame={inputs.Frame};" +
                "workgroup=5x5x5;brick=10x10x10;" +
                "distance=exact-local-chebyshev-cap-32";
            return true;
        }
        catch (Exception exception)
        {
            DeleteFence();
            diagnostic = $"{exception.GetType().Name}:{exception.Message}";
            FlushErrors();
            return false;
        }
        finally
        {
            _gl.BindImageTexture(
                0,
                0,
                0,
                true,
                0,
                GLEnum.WriteOnly,
                GLEnum.RG8);
            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, 0);
            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, 0);
        }
    }

    public bool TryCollectCompleted(
        out IReadOnlyList<PreviewSparseCloudBrickGenerationRecord> completed,
        out string diagnostic)
    {
        completed = Array.Empty<PreviewSparseCloudBrickGenerationRecord>();
        if (_disposed)
        {
            diagnostic = "generator-disposed";
            return false;
        }

        if (!HasPendingGeneration)
        {
            diagnostic = "no-generation-pending";
            return true;
        }

        if (_faultInject == PreviewSparseCloudFaultInjectPoint.Status)
        {
            DeleteFence();
            diagnostic = "injected-status-failure";
            return false;
        }

        var wait = _gl.ClientWaitSync(_generationFence, (uint)0, 0);
        var fenceState = ClassifyFenceResult(wait);
        if (fenceState == PreviewSparseCloudGenerationFenceState.Pending)
        {
            diagnostic = $"pending-{_inFlight.Count}";
            return true;
        }

        if (fenceState == PreviewSparseCloudGenerationFenceState.Failed)
        {
            diagnostic = "generation-wait-" + wait;
            DeleteFence();
            return false;
        }

        try
        {
            var status = new uint[_inFlight.Count];
            _gl.BindBuffer(
                BufferTargetARB.ShaderStorageBuffer,
                _statusBuffer);
            _gl.GetBufferSubData<uint>(
                BufferTargetARB.ShaderStorageBuffer,
                0,
                status);
            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
            var readError = _gl.GetError();
            if (readError != GLEnum.NoError)
            {
                diagnostic = "status-read-" + readError;
                DeleteFence();
                return false;
            }

            for (var index = 0; index < status.Length; index++)
            {
                if (status[index] !=
                    PreviewSparseCloudBrickGenerationContract.CompletionMagic)
                {
                    diagnostic =
                        $"brick-incomplete-{index}-0x{status[index]:X8}";
                    DeleteFence();
                    return false;
                }
            }

            DeleteFence();
            _generationId =
                _generationId == int.MaxValue ? 1 : _generationId + 1;
            completed = _inFlight.ToArray();
            diagnostic =
                $"completed-generation-{_generationId}/" +
                $"bricks-{completed.Count}";
            _inFlight.Clear();
            return true;
        }
        catch (Exception exception)
        {
            DeleteFence();
            diagnostic = $"{exception.GetType().Name}:{exception.Message}";
            FlushErrors();
            return false;
        }
    }

    internal static PreviewSparseCloudGenerationFenceState ClassifyFenceResult(
        GLEnum wait) =>
        wait is GLEnum.TimeoutExpired || (int)wait == 0x911B
            ? PreviewSparseCloudGenerationFenceState.Pending
            : wait is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied ||
              (int)wait is 0x911A or 0x911C
                ? PreviewSparseCloudGenerationFenceState.Complete
                : PreviewSparseCloudGenerationFenceState.Failed;

    public string FormatDiagnostic() =>
        $"generatorAllocated={_templateTexture != 0 && _requestBuffer != 0 && _statusBuffer != 0};" +
        $"generation={GenerationId};pending={HasPendingGeneration};" +
        $"inFlight={InFlightCount};templates=12x32-layers-RG8;" +
        $"queueBytes={PreviewSparseCloudVolumeContract.MemoryAccounting.GenerationQueueBytes};" +
        $"statusBytes={PreviewSparseCloudVolumeContract.MemoryAccounting.GenerationStatusBytes}";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            DeleteFence();
            if (_requestBuffer != 0)
            {
                _gl.DeleteBuffer(_requestBuffer);
                _requestBuffer = 0;
            }

            if (_statusBuffer != 0)
            {
                _gl.DeleteBuffer(_statusBuffer);
                _statusBuffer = 0;
            }

            if (_templateTexture != 0)
            {
                _gl.DeleteTexture(_templateTexture);
                _templateTexture = 0;
            }
        }
        catch
        {
            // The parent preview context owns objects after teardown.
        }

        _inFlight.Clear();
    }

    private unsafe bool TryAllocateAndUpload(
        PreviewSparseCloudTemplateAssetSet templates,
        out string diagnostic)
    {
        try
        {
            var packed = new byte[checked((int)templates.ByteLength)];
            var offset = 0;
            foreach (var template in templates.Templates)
            {
                template.Rg.CopyTo(packed, offset);
                offset += template.Rg.Length;
            }

            FlushErrors();
            _templateTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2DArray, _templateTexture);
            fixed (byte* pointer = packed)
            {
                _gl.TexImage3D(
                    TextureTarget.Texture2DArray,
                    0,
                    InternalFormat.RG8,
                    PreviewSparseCloudTemplateAssetContract.Width,
                    PreviewSparseCloudTemplateAssetContract.Height,
                    PreviewSparseCloudBrickGenerationContract
                        .TemplateTextureLayerCount,
                    0,
                    PixelFormat.RG,
                    PixelType.UnsignedByte,
                    pointer);
            }

            _gl.TexParameter(
                TextureTarget.Texture2DArray,
                TextureParameterName.TextureMinFilter,
                (int)GLEnum.Nearest);
            _gl.TexParameter(
                TextureTarget.Texture2DArray,
                TextureParameterName.TextureMagFilter,
                (int)GLEnum.Nearest);
            _gl.TexParameter(
                TextureTarget.Texture2DArray,
                TextureParameterName.TextureWrapS,
                (int)GLEnum.ClampToEdge);
            _gl.TexParameter(
                TextureTarget.Texture2DArray,
                TextureParameterName.TextureWrapT,
                (int)GLEnum.ClampToEdge);
            _gl.TexParameter(
                TextureTarget.Texture2DArray,
                TextureParameterName.TextureWrapR,
                (int)GLEnum.ClampToEdge);

            _requestBuffer = _gl.GenBuffer();
            _gl.BindBuffer(
                BufferTargetARB.ShaderStorageBuffer,
                _requestBuffer);
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (nuint)PreviewSparseCloudVolumeContract
                    .MemoryAccounting.GenerationQueueBytes,
                null,
                BufferUsageARB.DynamicDraw);
            _statusBuffer = _gl.GenBuffer();
            _gl.BindBuffer(
                BufferTargetARB.ShaderStorageBuffer,
                _statusBuffer);
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (nuint)PreviewSparseCloudVolumeContract
                    .MemoryAccounting.GenerationStatusBytes,
                null,
                BufferUsageARB.DynamicDraw);
            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);

            var error = _gl.GetError();
            if (error != GLEnum.NoError)
            {
                diagnostic = "allocation-or-upload-" + error;
                return false;
            }

            if (Marshal.SizeOf<PreviewSparseCloudBrickGenerationRecord>() !=
                PreviewSparseCloudVolumeContract.GenerationQueueRecordByteSize)
            {
                diagnostic = "generation-record-layout-mismatch";
                return false;
            }

            diagnostic =
                $"ready-templates-{templates.Templates.Count}/" +
                $"{templates.ByteLength}-bytes;queue=" +
                $"{PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame}x" +
                $"{PreviewSparseCloudVolumeContract.GenerationQueueRecordByteSize}";
            return true;
        }
        catch (Exception exception)
        {
            diagnostic = $"{exception.GetType().Name}:{exception.Message}";
            FlushErrors();
            return false;
        }
    }

    private void UploadDispatchBuffers()
    {
        var records = CollectionsMarshal.AsSpan(_inFlight);
        _gl.BindBuffer(
            BufferTargetARB.ShaderStorageBuffer,
            _requestBuffer);
        _gl.BufferSubData<PreviewSparseCloudBrickGenerationRecord>(
            BufferTargetARB.ShaderStorageBuffer,
            0,
            records);

        Span<uint> status = stackalloc uint[
            PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame];
        status.Clear();
        _gl.BindBuffer(
            BufferTargetARB.ShaderStorageBuffer,
            _statusBuffer);
        _gl.BufferSubData<uint>(
            BufferTargetARB.ShaderStorageBuffer,
            0,
            status[.._inFlight.Count]);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private void DeleteFence()
    {
        if (_generationFence == 0)
        {
            return;
        }

        _gl.DeleteSync(_generationFence);
        _generationFence = 0;
    }

    private void SetInt(int location, int value)
    {
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    private void SetFloat(int location, float value)
    {
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    private void SetVector3(int location, Vector3 value)
    {
        if (location >= 0)
        {
            _gl.Uniform3(location, value.X, value.Y, value.Z);
        }
    }

    private void FlushErrors()
    {
        for (var index = 0;
             index < 16 && _gl.GetError() != GLEnum.NoError;
             index++)
        {
        }
    }

    private readonly record struct Uniforms(
        int EnvelopeTemplates,
        int WeatherMap,
        int HasWeatherMap,
        int RequestCount,
        int CloudBaseWorldY,
        int CloudTopWorldY,
        int Density,
        int CoverageScale,
        int VolumeSize,
        int WindOffset,
        int StyleBias)
    {
        public static Uniforms Resolve(GlShaderProgram program) =>
            new(
                program.GetUniformLocation("uEnvelopeTemplates"),
                program.GetUniformLocation("uWeatherMap"),
                program.GetUniformLocation("uHasWeatherMap"),
                program.GetUniformLocation("uRequestCount"),
                program.GetUniformLocation("uCloudBaseWorldY"),
                program.GetUniformLocation("uCloudTopWorldY"),
                program.GetUniformLocation("uDensity"),
                program.GetUniformLocation("uCoverageScale"),
                program.GetUniformLocation("uVolumeSize"),
                program.GetUniformLocation("uWindOffset"),
                program.GetUniformLocation("uStyleBias"));
    }
}

internal readonly record struct PreviewSparseCloudBrickGenerationInputs(
    int Frame,
    float CloudBaseWorldY,
    float CloudTopWorldY,
    float Density,
    float CoverageScale,
    float VolumeSize,
    Vector3 WindOffset,
    uint WeatherTexture,
    int StyleBias = 0);

internal enum PreviewSparseCloudGenerationFenceState
{
    Pending = 0,
    Complete = 1,
    Failed = 2,
}
