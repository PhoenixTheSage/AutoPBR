using AutoPBR.Core.Models;
using AutoPBR.Preview;
using AutoPBR.App.Rendering.Scene;
using System.Numerics;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed class EntityRebakeWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _workAvailable = new(false);
    private readonly Thread _thread;
    private readonly Action? _onCompleted;
    private EntityRebakeRequest? _pending;
    private EntityRebakeResult? _completedA;
    private EntityRebakeResult? _completedB;
    private EntityRebakeResult? _latestCompleted;
    private volatile bool _disposed;

    public EntityRebakeWorker(Action? onCompleted = null)
    {
        _onCompleted = onCompleted;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "EntityRebakeWorker"
        };
        _thread.Start();
    }

    public long Enqueue(EntityRebakeRequest request)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return request.Sequence;
            }

            _pending = request;
        }

        _workAvailable.Set();
        return request.Sequence;
    }

    public bool TryTakeCompleted(
        long afterSequence,
        EntityRebakeWorkKind workKind,
        string requestKey,
        out EntityRebakeResult result)
    {
        lock (_gate)
        {
            if (_latestCompleted is null ||
                _latestCompleted.Sequence <= afterSequence ||
                _latestCompleted.WorkKind != workKind ||
                !string.Equals(
                    _latestCompleted.RequestKey,
                    requestKey,
                    StringComparison.Ordinal))
            {
                result = default!;
                return false;
            }

            result = _latestCompleted;
            return true;
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _workAvailable.Wait();
            _workAvailable.Reset();

            EntityRebakeRequest? request;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                request = _pending;
                _pending = null;
            }

            if (request is null)
            {
                continue;
            }

            bool success;
            float[]? verts;
            uint[]? indices;
            PreviewDrawBatch[]? batches;
            int gpuBoneCount = 0;
            float gpuLift = 0f;
            var workerContext = CloneContext(request.RebakeContext);
            if (request.WorkKind == EntityRebakeWorkKind.GpuSkinPrepare)
            {
                success = EntityEmulatedPreviewRebaker.TryPrepareGpuSkinnedEmulatedMesh(
                    workerContext,
                    request.Materials,
                    PreviewStageConstants.GridWorldY,
                    EntityPreviewGrounding.DefaultClearance,
                    out verts,
                    out indices,
                    out batches,
                    out gpuBoneCount,
                    out gpuLift,
                    request.ApplyGeometryIrSetupAnimMotion);
            }
            else
            {
                success = EntityEmulatedPreviewRebaker.TryRebakeMesh(
                    workerContext,
                    request.Materials,
                    request.AnimationTimeSeconds,
                    out verts,
                    out indices,
                    out batches,
                    applyGeometryIrSetupAnimMotion:
                        request.ApplyGeometryIrSetupAnimMotion);
            }

            var completed = new EntityRebakeResult
            {
                Sequence = request.Sequence,
                WorkKind = request.WorkKind,
                RequestKey = request.RequestKey,
                Success = success,
                InterleavedVertices = verts,
                Indices = indices,
                DrawBatches = batches,
                GpuBoneCount = gpuBoneCount,
                GpuMeshSpaceLiftY = gpuLift,
                GpuBoneDispatchRoute = workerContext.GpuBoneDispatchRoute,
                MeshProvenance = workerContext.MeshProvenance,
                GpuBindPoseInverseLocalToParent =
                    workerContext.GpuBindPoseInverseLocalToParent,
                GpuBindPoseBonePalette =
                    workerContext.GpuBindPoseBonePalette,
                GpuBindPoseInterleavedVertices =
                    workerContext.GpuBindPoseInterleavedVertices,
                ElementPartIds = workerContext.ElementPartIds,
                LastGroundContactY = workerContext.LastGroundContactY,
                LastGroundLiftY = workerContext.LastGroundLiftY,
                LastBodyCentroidY = workerContext.LastBodyCentroidY,
                LastHeadCentroidY = workerContext.LastHeadCentroidY,
                LastLegCentroidY = workerContext.LastLegCentroidY,
            };

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_completedA is null)
                {
                    _completedA = completed;
                    _latestCompleted = _completedA;
                }
                else if (_completedB is null || ReferenceEquals(_latestCompleted, _completedB))
                {
                    _completedB = completed;
                    _latestCompleted = _completedB;
                }
                else
                {
                    _completedA = completed;
                    _latestCompleted = _completedA;
                }
            }

            try
            {
                _onCompleted?.Invoke();
            }
            catch
            {
                // Frame notification is best-effort; never terminate the worker.
            }
        }
    }

    private static EntityEmulatedPreviewRebakeContext CloneContext(
        EntityEmulatedPreviewRebakeContext source) =>
        new()
        {
            PackZipPath = source.PackZipPath,
            AssetArchivePath = source.AssetArchivePath,
            NativeRootDirectory = source.NativeRootDirectory,
            NativeProfileName = source.NativeProfileName,
            NativeParsedVersion = source.NativeParsedVersion,
            ModelDefaultNamespace = source.ModelDefaultNamespace,
            IdlePhase01 = source.IdlePhase01,
            PreviewPoseId = source.PreviewPoseId,
            PreviewSizeId = source.PreviewSizeId,
            PreviewContextTypeId = source.PreviewContextTypeId,
            OrderedTextureZipPaths = source.OrderedTextureZipPaths,
            GpuBoneDispatchRoute = source.GpuBoneDispatchRoute,
            MeshProvenance = source.MeshProvenance,
            GpuPreparedBoneCount = source.GpuPreparedBoneCount,
            GpuBindPoseInverseLocalToParent =
                source.GpuBindPoseInverseLocalToParent,
            GpuBindPoseBonePalette = source.GpuBindPoseBonePalette,
            GpuBindPoseInterleavedVertices =
                source.GpuBindPoseInterleavedVertices,
            ElementPartIds = source.ElementPartIds,
            LastGroundContactY = source.LastGroundContactY,
            LastGroundLiftY = source.LastGroundLiftY,
            LastBodyCentroidY = source.LastBodyCentroidY,
            LastHeadCentroidY = source.LastHeadCentroidY,
            LastLegCentroidY = source.LastLegCentroidY,
            PackConverterCpuMeshFingerprint =
                source.PackConverterCpuMeshFingerprint,
            GpuBoundCpuMeshFingerprint =
                source.GpuBoundCpuMeshFingerprint,
        };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = null;
        }

        _workAvailable.Set();
        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _workAvailable.Dispose();
    }
}

internal sealed class EntityRebakeRequest
{
    public required long Sequence { get; init; }
    public required EntityRebakeWorkKind WorkKind { get; init; }
    public required string RequestKey { get; init; }
    public required EntityEmulatedPreviewRebakeContext RebakeContext { get; init; }
    public required PreviewTextureMaps[] Materials { get; init; }
    public required float AnimationTimeSeconds { get; init; }
    public required bool ApplyGeometryIrSetupAnimMotion { get; init; }
}

internal sealed class EntityRebakeResult
{
    public long Sequence { get; init; }
    public EntityRebakeWorkKind WorkKind { get; init; }
    public string RequestKey { get; init; } = string.Empty;
    public bool Success { get; init; }
    public float[]? InterleavedVertices { get; init; }
    public uint[]? Indices { get; init; }
    public PreviewDrawBatch[]? DrawBatches { get; init; }
    public int GpuBoneCount { get; init; }
    public float GpuMeshSpaceLiftY { get; init; }
    public EntityGpuBoneDispatchRoute? GpuBoneDispatchRoute { get; init; }
    public PreviewMeshProvenance? MeshProvenance { get; init; }
    public Matrix4x4[]? GpuBindPoseInverseLocalToParent { get; init; }
    public Matrix4x4[]? GpuBindPoseBonePalette { get; init; }
    public float[]? GpuBindPoseInterleavedVertices { get; init; }
    public string[]? ElementPartIds { get; init; }
    public float LastGroundContactY { get; init; }
    public float LastGroundLiftY { get; init; }
    public float LastBodyCentroidY { get; init; }
    public float LastHeadCentroidY { get; init; }
    public float LastLegCentroidY { get; init; }
}

internal enum EntityRebakeWorkKind
{
    CpuRebake,
    GpuSkinPrepare,
}
