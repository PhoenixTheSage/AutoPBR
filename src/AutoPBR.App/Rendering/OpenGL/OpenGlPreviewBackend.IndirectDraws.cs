using System.Numerics;

using AutoPBR.Preview;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private const int MinGpuCullingGroupSize = 4;

    private bool TryUploadGenesisIndirectDrawCommands(PreviewModelSubject? model)
    {
        if (_glCapabilities?.CanUseIndirectDrawCommands != true ||
            _gl is null ||
            model?.DrawBatches is not { Length: > 0 } batches)
        {
            return false;
        }

        _genesisIndirectDrawCommands ??= new GlIndirectDrawCommandBuffer(_gl);
        if (!_genesisIndirectDrawCommands.Upload(batches))
        {
            return false;
        }

        if (!_loggedIndirectDrawCommandBuffer)
        {
            _loggedIndirectDrawCommandBuffer = true;
            EmitDiagnostic(
                $"[3D preview] Indirect draw command buffer ready: batches={batches.Length}; per-batch indirect draws preserve current state ordering.");
        }

        return true;
    }

    private bool CanUseGenesisMultiDrawGroups(bool useMaterialDrawRecords) =>
        useMaterialDrawRecords &&
        _activeGenesisProgramKey.DrawRecordBaseInstance &&
        _glCapabilities?.CanUseMultiDrawIndirectGroups == true;

    private void DrawPreviewBatchRange(
        PreviewDrawBatch batch,
        int batchIndex,
        bool patches,
        bool useIndirectDrawCommands,
        bool useMultiDrawGroups = false,
        int groupCount = 1)
    {
        if (useIndirectDrawCommands && _genesisIndirectDrawCommands is { IsValid: true })
        {
            if (useMultiDrawGroups && groupCount > 1)
            {
                if (!_loggedMultiDrawIndirectGroups)
                {
                    _loggedMultiDrawIndirectGroups = true;
                    EmitDiagnostic(
                        "[3D preview] Multi-draw indirect groups enabled: draw-record indices come from indirect baseInstance.");
                }

                _mesh!.MultiDrawIndirect(_genesisIndirectDrawCommands, batchIndex, groupCount, patches, keepBound: true);
                return;
            }

            _mesh!.DrawIndirect(_genesisIndirectDrawCommands, batchIndex, patches, keepBound: true);
            return;
        }

        _mesh!.DrawRange(batch.FirstIndex, batch.IndexCount, patches, keepBound: true);
    }

    private bool TryDrawGpuCulledBatchGroup(
        PreviewModelSubject model,
        int firstCommand,
        int commandCount,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        Matrix4x4 modelMatrix,
        GlShaderProgram drawProgram,
        string passLabel,
        bool patches = false,
        bool preserveOrder = false,
        float boundsPadding = 0f)
    {
        if (commandCount < MinGpuCullingGroupSize ||
            _gpuDrawCommandCompactionCompileDisabled ||
            _glCapabilities?.CanUseGpuCompactedDrawSubmission != true ||
            _mesh is not { SupportsIndirectCount: true } ||
            _genesisIndirectDrawCommands is not { IsValid: true } sourceCommands ||
            !GroupHasCullableBounds(model.DrawBatches, firstCommand, commandCount))
        {
            return false;
        }

        if (!TryEnsureGpuDrawCommandCompactor())
        {
            return false;
        }

        Span<Vector4> frustumPlanes = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];
        PreviewFrustumPlanes.Extract(viewProjection, frustumPlanes);
        if (!_gpuDrawCommandCompactor!.DispatchWithGpuCulling(
                _gpuDrawCommandCompactionProgram!,
                sourceCommands,
                model.DrawBatches,
                frustumPlanes,
                cameraPosition,
                modelMatrix,
                firstCommand,
                commandCount,
                preserveOrder: preserveOrder,
                boundsPadding: boundsPadding))
        {
            return false;
        }

        drawProgram.Use();
        var drawn = _mesh.MultiDrawIndirectCount(
            _gpuDrawCommandCompactor.OutputCommands,
            _gpuDrawCommandCompactor.CounterBufferHandle,
            commandCount,
            patches,
            keepBound: true);
        if (drawn && !_loggedGpuCompactedDrawSubmission)
        {
            _gpuCompactedSubmissionGroups++;
            _gpuCompactedSubmissionSourceCommands += commandCount;
            _loggedGpuCompactedDrawSubmission = true;
            EmitDiagnostic(
                $"[3D preview] GPU-compacted draw submission enabled: pass={passLabel}, " +
                $"sourceCommands={commandCount}, apiCalls=1, order={(preserveOrder ? "stable" : "parallel")}; " +
                "frustum/LOD culling feeds indirect-count draws without CPU readback.");
        }
        else if (drawn)
        {
            _gpuCompactedSubmissionGroups++;
            _gpuCompactedSubmissionSourceCommands += commandCount;
        }

        return drawn;
    }

    private bool TryEnsureGpuDrawCommandCompactor()
    {
        if (_gpuDrawCommandCompactionProgram is { IsValid: true } && _gpuDrawCommandCompactor is not null)
        {
            return true;
        }

        if (_gl is null || _shaderCtx is null)
        {
            return false;
        }

        _gpuDrawCommandCompactionProgram = CreatePreviewComputeProgram(
            "genesis_indirect_compact.comp",
            out var error,
            "genesis-indirect-compact");
        if (!_gpuDrawCommandCompactionProgram.IsValid)
        {
            _gpuDrawCommandCompactionProgram.Dispose();
            _gpuDrawCommandCompactionProgram = null;
            _gpuDrawCommandCompactionCompileDisabled = true;
            EmitDiagnostic(
                $"[3D preview] GPU-compacted draw submission unavailable; retaining grouped indirect fallback. {error}");
            return false;
        }

        _gpuDrawCommandCompactor = new GlGpuDrawCommandCompactor(_gl);
        return true;
    }

    internal static bool GroupHasCullableBounds(
        IReadOnlyList<PreviewDrawBatch> batches,
        int firstCommand,
        int commandCount)
    {
        if (firstCommand < 0 || commandCount <= 0 || firstCommand > batches.Count - commandCount)
        {
            return false;
        }

        for (var i = firstCommand; i < firstCommand + commandCount; i++)
        {
            if (batches[i].HasBounds)
            {
                return true;
            }
        }

        return false;
    }

    internal static int CountMainPassMultiDrawGroup(
        IReadOnlyList<PreviewDrawBatch> batches,
        int startIndex,
        int materialCount,
        bool entityBlendDraw,
        bool enabled,
        bool allowMaterialChanges = false)
    {
        if (!enabled ||
            startIndex < 0 ||
            startIndex >= batches.Count ||
            (uint)batches[startIndex].MaterialIndex >= (uint)materialCount)
        {
            return 1;
        }

        var first = batches[startIndex];
        var firstBlend = entityBlendDraw || first.LayerPolicy.Kind == PreviewDepthLayerKind.TranslucentOverlay;
        var count = 1;
        for (var i = startIndex + 1; i < batches.Count; i++)
        {
            var next = batches[i];
            if ((uint)next.MaterialIndex >= (uint)materialCount ||
                (!allowMaterialChanges && next.MaterialIndex != first.MaterialIndex) ||
                next.LayerPolicy != first.LayerPolicy ||
                (entityBlendDraw || next.LayerPolicy.Kind == PreviewDepthLayerKind.TranslucentOverlay) != firstBlend)
            {
                break;
            }

            count++;
        }

        return count;
    }

    internal static int CountShadowPassMultiDrawGroup(
        IReadOnlyList<PreviewDrawBatch> batches,
        int startIndex,
        int materialCount,
        bool enabled,
        bool allowMaterialChanges = false)
    {
        if (!enabled ||
            startIndex < 0 ||
            startIndex >= batches.Count ||
            (uint)batches[startIndex].MaterialIndex >= (uint)materialCount ||
            batches[startIndex].LayerPolicy.ShadowMode == PreviewDrawLayerShadowMode.Skip)
        {
            return 1;
        }

        var first = batches[startIndex];
        var count = 1;
        for (var i = startIndex + 1; i < batches.Count; i++)
        {
            var next = batches[i];
            if ((uint)next.MaterialIndex >= (uint)materialCount ||
                (!allowMaterialChanges && next.MaterialIndex != first.MaterialIndex) ||
                next.LayerPolicy.ShadowMode == PreviewDrawLayerShadowMode.Skip)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private void DisposeGenesisIndirectDrawCommands()
    {
        _gpuDrawCommandCompactor?.Dispose();
        _gpuDrawCommandCompactor = null;
        _gpuDrawCommandCompactionProgram?.Dispose();
        _gpuDrawCommandCompactionProgram = null;
        _genesisIndirectDrawCommands?.Dispose();
        _genesisIndirectDrawCommands = null;
        _loggedIndirectDrawCommandBuffer = false;
        _loggedMultiDrawIndirectGroups = false;
        _loggedGpuCompactedDrawSubmission = false;
        _gpuCompactedSubmissionGroups = 0;
        _gpuCompactedSubmissionSourceCommands = 0;
        _gpuDrawCommandCompactionCompileDisabled = false;
    }

    private void AbandonGenesisIndirectDrawCommands()
    {
        _gpuDrawCommandCompactor = null;
        _gpuDrawCommandCompactionProgram = null;
        _genesisIndirectDrawCommands = null;
        _loggedIndirectDrawCommandBuffer = false;
        _loggedMultiDrawIndirectGroups = false;
        _loggedGpuCompactedDrawSubmission = false;
        _gpuCompactedSubmissionGroups = 0;
        _gpuCompactedSubmissionSourceCommands = 0;
        _gpuDrawCommandCompactionCompileDisabled = false;
    }
}
