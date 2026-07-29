using AutoPBR.Core.Models;
using AutoPBR.Preview;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private EntityRebakeWorker? _entityRebakeWorker;
    private long _entityRebakeLastConsumedSequence;

    private void EnsureEntityRebakeWorker()
    {
        _entityRebakeWorker ??= new EntityRebakeWorker(RequestPreviewFrame);
    }

    private void DisposeEntityRebakeWorker()
    {
        _entityRebakeWorker?.Dispose();
        _entityRebakeWorker = null;
        _entityRebakeLastConsumedSequence = 0;
    }

    private void EnqueueEntityRebakeRequest(
        ref GlRenderFrame frame,
        bool setupAnimMotion,
        string requestKey,
        EntityRebakeWorkKind workKind = EntityRebakeWorkKind.CpuRebake,
        float? animationTimeSeconds = null)
    {
        if (frame.BlockModel is null || frame.EntityRebakeCtx is null)
        {
            return;
        }

        EnsureEntityRebakeWorker();
        var sequence = Interlocked.Increment(ref _entityRebakeWorkerSequence);
        _entityRebakeWorker!.Enqueue(new EntityRebakeRequest
        {
            Sequence = sequence,
            WorkKind = workKind,
            RequestKey = requestKey,
            RebakeContext = frame.EntityRebakeCtx,
            Materials = frame.BlockModel.Materials,
            AnimationTimeSeconds =
                animationTimeSeconds ?? frame.EntityEmulatedAnimClock,
            ApplyGeometryIrSetupAnimMotion = setupAnimMotion
        });
    }

    private long _entityRebakeWorkerSequence;

    private bool TryConsumeEntityRebakeResult(
        ref GlRenderFrame frame,
        string rebakeKey)
    {
        if (_entityRebakeWorker is null ||
            !_entityRebakeWorker.TryTakeCompleted(
                _entityRebakeLastConsumedSequence,
                EntityRebakeWorkKind.CpuRebake,
                rebakeKey,
                out var result) ||
            !result.Success ||
            result.InterleavedVertices is null ||
            result.Indices is null ||
            result.DrawBatches is null)
        {
            return false;
        }

        _entityRebakeLastConsumedSequence = result.Sequence;
        ApplyEntityRebakeContextResult(
            frame.EntityRebakeCtx!,
            result);
        var rebaked = new PreviewModelSubject
        {
            InterleavedVertices = result.InterleavedVertices,
            Indices = result.Indices,
            DrawBatches = result.DrawBatches,
            Materials = frame.BlockModel!.Materials,
            PrimaryMaterialIndex = frame.BlockModel.PrimaryMaterialIndex,
            Sprite2DFoliageTarget = frame.BlockModel.Sprite2DFoliageTarget,
            EnableRenderTimeAnimation = frame.BlockModel.EnableRenderTimeAnimation,
            AnimationPreset = frame.BlockModel.AnimationPreset,
            EmulatedRebake = frame.BlockModel.EmulatedRebake,
            GpuEntityBoneSkinning = false,
            EntityGpuMeshSpaceLiftY = 0f,
            EntityPreviewAnchorOffset = frame.BlockModel.EntityPreviewAnchorOffset,
            EntityPreviewPlacementApplied = true,
            MeshProvenance = frame.EntityRebakeCtx?.MeshProvenance ?? frame.BlockModel.MeshProvenance
        };
        frame.BlockModel = rebaked;
        UploadPreviewMesh(rebaked.InterleavedVertices, rebaked.Indices);
        frame.UploadedLiveEntityAnim = true;
        _lastEmulatedEntityRebakeRenderTime = frame.RenderTime;
        lock (_sync)
        {
            _blockModelSubject = rebaked;
            if (frame.MeshDirty)
            {
                _meshDirty = false;
            }
        }

        _emulatedRebakeSubjectKey = rebakeKey;
        return true;
    }

    private bool TryTakeEntityPreparationResult(
        EntityRebakeWorkKind workKind,
        string requestKey,
        out EntityRebakeResult result)
    {
        if (_entityRebakeWorker is null ||
            !_entityRebakeWorker.TryTakeCompleted(
                _entityRebakeLastConsumedSequence,
                workKind,
                requestKey,
                out result))
        {
            result = default!;
            return false;
        }

        _entityRebakeLastConsumedSequence = result.Sequence;
        return true;
    }

    private static void ApplyEntityRebakeContextResult(
        EntityEmulatedPreviewRebakeContext context,
        EntityRebakeResult result)
    {
        context.GpuBoneDispatchRoute = result.GpuBoneDispatchRoute;
        context.MeshProvenance = result.MeshProvenance;
        context.ElementPartIds = result.ElementPartIds;
        context.LastGroundContactY = result.LastGroundContactY;
        context.LastGroundLiftY = result.LastGroundLiftY;
        context.LastBodyCentroidY = result.LastBodyCentroidY;
        context.LastHeadCentroidY = result.LastHeadCentroidY;
        context.LastLegCentroidY = result.LastLegCentroidY;

        if (result.WorkKind != EntityRebakeWorkKind.GpuSkinPrepare)
        {
            return;
        }

        context.GpuBindPoseInverseLocalToParent =
            result.GpuBindPoseInverseLocalToParent;
        context.GpuBindPoseBonePalette =
            result.GpuBindPoseBonePalette;
        context.GpuBindPoseInterleavedVertices =
            result.GpuBindPoseInterleavedVertices;
    }
}
