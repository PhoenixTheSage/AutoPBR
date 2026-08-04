using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CQ4.2 transactional GPU ownership plus CQ4.3 fenced page-table publication. Allocation and
/// publication alone never make sparse sampling active; generated brick residency arrives later.
/// </summary>
internal sealed class GlSparseCloudVolumeResources : IDisposable
{
    private readonly GL _gl;
    private readonly uint[] _activePageTables =
        new uint[PreviewSparseCloudVolumeContract.ClipmapCount];
    private readonly uint[] _buildPageTables =
        new uint[PreviewSparseCloudVolumeContract.ClipmapCount];
    private readonly Int3[] _activeOrigins =
        new Int3[PreviewSparseCloudVolumeContract.ClipmapCount];
    private readonly Int3[] _pendingOrigins =
        new Int3[PreviewSparseCloudVolumeContract.ClipmapCount];
    private readonly HashSet<int> _pendingPublishedPhysicalIndices = [];
    private readonly HashSet<int> _publishedPhysicalIndices = [];
    private uint _atlasTexture;
    private nint _publicationFence;
    private int _pendingPlanRevision;
    private int _pendingAtlasGenerationId;
    private int _pendingResidentCount;
    private bool _disposed;
    private PreviewSparseCloudFaultInjectPoint _faultInject =
        PreviewSparseCloudFaultInjectPoint.None;

    private GlSparseCloudVolumeResources(GL gl)
    {
        _gl = gl;
    }

    public uint AtlasTextureHandle => _atlasTexture;
    public bool IsAllocated =>
        _atlasTexture != 0 &&
        _activePageTables.All(handle => handle != 0) &&
        _buildPageTables.All(handle => handle != 0);
    public bool IsSamplingReady { get; private set; }
    public int PublishedGenerationId { get; private set; }
    public int PublishedPlanRevision { get; private set; }
    public int PublishedAtlasGenerationId { get; private set; }
    public int PublishedResidentCount { get; private set; }
    public bool HasPendingPublication => _publicationFence != 0;
    public IReadOnlySet<int> PublishedPhysicalBrickIndices =>
        _publishedPhysicalIndices;
    public PreviewSparseCloudMemoryAccounting MemoryAccounting { get; } =
        PreviewSparseCloudVolumeContract.MemoryAccounting;

    internal void SetFaultInjectForTests(
        PreviewSparseCloudFaultInjectPoint point) =>
        _faultInject = point;

    public uint GetActivePageTableHandle(int clipmapLevel) =>
        GetPageTableHandle(_activePageTables, clipmapLevel);

    public uint GetBuildPageTableHandle(int clipmapLevel) =>
        GetPageTableHandle(_buildPageTables, clipmapLevel);

    public Int3 GetActiveOrigin(int clipmapLevel)
    {
        ValidateClipmapLevel(clipmapLevel);
        return _activeOrigins[clipmapLevel];
    }

    public void SetSamplingReadyCq46(bool ready)
    {
        IsSamplingReady = ready && IsAllocated && !_disposed;
    }

    public static bool TryCreate(
        GL gl,
        PreviewGlCapabilities? capabilities,
        out GlSparseCloudVolumeResources? resources,
        out string diagnostic) =>
        TryCreate(
            gl,
            capabilities,
            failAfterTextureAllocation: -1,
            out resources,
            out diagnostic);

    internal static bool TryCreate(
        GL gl,
        PreviewGlCapabilities? capabilities,
        int failAfterTextureAllocation,
        out GlSparseCloudVolumeResources? resources,
        out string diagnostic)
    {
        resources = null;
        if (capabilities?.CanUseSparseCloudVolumes != true)
        {
            diagnostic = "capability-unavailable";
            return false;
        }

        if (!PreviewSparseCloudVolumeContract.MemoryAccounting.IsWithinBudget)
        {
            diagnostic =
                "memory-budget-exceeded-" +
                PreviewSparseCloudVolumeContract.MemoryAccounting
                    .FormatDiagnostic();
            return false;
        }

        var candidate = new GlSparseCloudVolumeResources(gl);
        try
        {
            if (!candidate.TryAllocate(
                    failAfterTextureAllocation,
                    out diagnostic))
            {
                return false;
            }

            resources = candidate;
            candidate = null!;
            return true;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    public string FormatDiagnostic() =>
        $"allocated={IsAllocated};samplingReady={IsSamplingReady};" +
        $"generation={PublishedGenerationId};planRevision={PublishedPlanRevision};" +
        $"atlasGeneration={PublishedAtlasGenerationId};" +
        $"publishedResident={PublishedResidentCount};" +
        $"publicationPending={HasPendingPublication};" +
        $"atlas={PreviewSparseCloudVolumeContract.AtlasTexelSize}^3-RG8/" +
        $"{PreviewSparseCloudVolumeContract.AtlasByteLength};" +
        $"pageTables={PreviewSparseCloudVolumeContract.ClipmapCount}x2x" +
        $"{PreviewSparseCloudVolumeContract.PageTableWidth}x" +
        $"{PreviewSparseCloudVolumeContract.PageTableHeight}x" +
        $"{PreviewSparseCloudVolumeContract.PageTableDepth}-R16UI/" +
        $"{PreviewSparseCloudVolumeContract.MemoryAccounting.PageTableBytes};" +
        $"pool={PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount}+" +
        $"1-reserved;" +
        MemoryAccounting.FormatDiagnostic();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            DeletePublicationFence();
            if (_atlasTexture != 0)
            {
                _gl.DeleteTexture(_atlasTexture);
                _atlasTexture = 0;
            }

            DeleteTextures(_activePageTables);
            DeleteTextures(_buildPageTables);
        }
        catch
        {
            // Context teardown owns any native objects that can no longer be deleted.
        }
    }

    public unsafe bool TryStagePageTables(
        PreviewSparseCloudClipmapController controller,
        out string diagnostic)
    {
        return TryStagePageTables(
            controller,
            atlasGenerationId: 0,
            out diagnostic);
    }

    public unsafe bool TryStagePageTables(
        PreviewSparseCloudClipmapController controller,
        int atlasGenerationId,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (_disposed || !IsAllocated)
        {
            diagnostic = "resources-unavailable";
            return false;
        }

        if (HasPendingPublication)
        {
            diagnostic = "publication-pending";
            return false;
        }

        if (controller.TableRevision == PublishedPlanRevision)
        {
            diagnostic = "revision-not-newer";
            return false;
        }

        var priorActiveTexture = _gl.GetInteger(GetPName.ActiveTexture);
        var priorTexture3D = _gl.GetInteger(GetPName.TextureBinding3D);
        var priorUnpackAlignment = _gl.GetInteger(GetPName.UnpackAlignment);
        FlushErrors();
        try
        {
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
            for (var level = 0;
                 level < PreviewSparseCloudVolumeContract.ClipmapCount;
                 level++)
            {
                _gl.BindTexture(
                    TextureTarget.Texture3D,
                    _buildPageTables[level]);
                var table = controller.GetBuildTable(level);
                fixed (ushort* pointer = table)
                {
                    _gl.TexSubImage3D(
                        TextureTarget.Texture3D,
                        0,
                        0,
                        0,
                        0,
                        PreviewSparseCloudVolumeContract.PageTableWidth,
                        PreviewSparseCloudVolumeContract.PageTableHeight,
                        PreviewSparseCloudVolumeContract.PageTableDepth,
                        PixelFormat.RedInteger,
                        PixelType.UnsignedShort,
                        pointer);
                }

                _pendingOrigins[level] = controller.GetOrigin(level);
            }

            // Complete CPU texture updates must be visible before the publication fence.
            _gl.MemoryBarrier(0x00000100u | 0x00000008u);
            var uploadError = _gl.GetError();
            if (uploadError != GLEnum.NoError)
            {
                diagnostic = "table-upload-" + uploadError;
                FlushErrors();
                return false;
            }

            _publicationFence = _gl.FenceSync(
                SyncCondition.SyncGpuCommandsComplete,
                SyncBehaviorFlags.None);
            if (_publicationFence == 0)
            {
                diagnostic = "publication-fence-unavailable";
                return false;
            }

            _pendingPlanRevision = controller.TableRevision;
            _pendingAtlasGenerationId = Math.Max(0, atlasGenerationId);
            _pendingResidentCount = controller.ResidentCount;
            _pendingPublishedPhysicalIndices.Clear();
            foreach (var mapping in controller.EnumerateResidentMappings())
            {
                _pendingPublishedPhysicalIndices.Add(mapping.Value);
            }

            _gl.Flush();
            diagnostic =
                $"staged-revision-{_pendingPlanRevision};" +
                $"atlas-generation-{_pendingAtlasGenerationId};" +
                $"resident={_pendingResidentCount};" +
                $"requested={controller.RequestedCount}";
            return true;
        }
        catch (Exception exception)
        {
            DeletePublicationFence();
            diagnostic = $"{exception.GetType().Name}:{exception.Message}";
            FlushErrors();
            return false;
        }
        finally
        {
            _gl.BindTexture(
                TextureTarget.Texture3D,
                (uint)Math.Max(0, priorTexture3D));
            _gl.ActiveTexture((TextureUnit)priorActiveTexture);
            _gl.PixelStore(
                PixelStoreParameter.UnpackAlignment,
                priorUnpackAlignment);
        }
    }

    public bool TryPublishCompleted(
        out bool published,
        out string diagnostic)
    {
        published = false;
        if (_disposed || !IsAllocated)
        {
            diagnostic = "resources-unavailable";
            return false;
        }

        if (!HasPendingPublication)
        {
            diagnostic = "no-publication-pending";
            return true;
        }

        if (_faultInject == PreviewSparseCloudFaultInjectPoint.Publication)
        {
            DeletePublicationFence();
            diagnostic = "injected-publication-failure";
            return false;
        }

        var wait = _gl.ClientWaitSync(_publicationFence, (uint)0, 0);
        if (wait is GLEnum.TimeoutExpired || (int)wait == 0x911B)
        {
            diagnostic = $"pending-revision-{_pendingPlanRevision}";
            return true;
        }

        if (wait is not GLEnum.AlreadySignaled and
            not GLEnum.ConditionSatisfied &&
            (int)wait is not 0x911A and not 0x911C)
        {
            diagnostic = "publication-wait-" + wait;
            DeletePublicationFence();
            return false;
        }

        DeletePublicationFence();
        for (var level = 0;
             level < PreviewSparseCloudVolumeContract.ClipmapCount;
             level++)
        {
            (_activePageTables[level], _buildPageTables[level]) =
                (_buildPageTables[level], _activePageTables[level]);
            _activeOrigins[level] = _pendingOrigins[level];
        }

        PublishedPlanRevision = _pendingPlanRevision;
        PublishedAtlasGenerationId = _pendingAtlasGenerationId;
        PublishedResidentCount = _pendingResidentCount;
        _publishedPhysicalIndices.Clear();
        foreach (var physicalIndex in _pendingPublishedPhysicalIndices)
        {
            _publishedPhysicalIndices.Add(physicalIndex);
        }

        _pendingPublishedPhysicalIndices.Clear();
        PublishedGenerationId =
            PublishedGenerationId == int.MaxValue
                ? 1
                : PublishedGenerationId + 1;
        _pendingPlanRevision = 0;
        _pendingAtlasGenerationId = 0;
        _pendingResidentCount = 0;
        published = true;
        diagnostic =
            $"published-generation-{PublishedGenerationId}/" +
            $"revision-{PublishedPlanRevision}";
        return true;
    }

    private unsafe bool TryAllocate(
        int failAfterTextureAllocation,
        out string diagnostic)
    {
        var priorActiveTexture = _gl.GetInteger(GetPName.ActiveTexture);
        var priorTexture3D = _gl.GetInteger(GetPName.TextureBinding3D);
        var priorUnpackAlignment =
            _gl.GetInteger(GetPName.UnpackAlignment);
        FlushErrors();
        try
        {
            var maximum3DTextureSize =
                _gl.GetInteger(GetPName.Max3DTextureSize);
            if (maximum3DTextureSize <
                PreviewSparseCloudVolumeContract.AtlasTexelSize)
            {
                diagnostic =
                    $"max-3d-texture-{maximum3DTextureSize}-below-" +
                    $"{PreviewSparseCloudVolumeContract.AtlasTexelSize}";
                return false;
            }

            var allocationCount = 0;
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            _atlasTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture3D, _atlasTexture);
            var atlasClear = new byte[
                checked((int)PreviewSparseCloudVolumeContract.AtlasByteLength)];
            fixed (byte* atlasPointer = atlasClear)
            {
                _gl.TexImage3D(
                    TextureTarget.Texture3D,
                    0,
                    InternalFormat.RG8,
                    PreviewSparseCloudVolumeContract.AtlasTexelSize,
                    PreviewSparseCloudVolumeContract.AtlasTexelSize,
                    PreviewSparseCloudVolumeContract.AtlasTexelSize,
                    0,
                    PixelFormat.RG,
                    PixelType.UnsignedByte,
                    atlasPointer);
            }

            ConfigureAtlasTexture();
            ThrowIfInjectedFailure(
                ++allocationCount,
                failAfterTextureAllocation);

            var pageClear = new ushort[
                PreviewSparseCloudVolumeContract.PageTableEntryCount];
            for (var level = 0;
                 level < PreviewSparseCloudVolumeContract.ClipmapCount;
                 level++)
            {
                _activePageTables[level] =
                    AllocatePageTable(pageClear);
                ThrowIfInjectedFailure(
                    ++allocationCount,
                    failAfterTextureAllocation);
                _buildPageTables[level] =
                    AllocatePageTable(pageClear);
                ThrowIfInjectedFailure(
                    ++allocationCount,
                    failAfterTextureAllocation);
            }

            var allocationError = _gl.GetError();
            if (allocationError != GLEnum.NoError)
            {
                diagnostic = "allocation-" + allocationError;
                FlushErrors();
                return false;
            }

            diagnostic =
                "allocated-cq4.2/" + FormatDiagnostic();
            return true;
        }
        catch (Exception exception)
        {
            diagnostic =
                exception is PreviewSparseCloudInjectedAllocationException
                    ? exception.Message
                    : $"{exception.GetType().Name}:{exception.Message}";
            FlushErrors();
            return false;
        }
        finally
        {
            _gl.BindTexture(
                TextureTarget.Texture3D,
                (uint)Math.Max(0, priorTexture3D));
            _gl.ActiveTexture((TextureUnit)priorActiveTexture);
            _gl.PixelStore(
                PixelStoreParameter.UnpackAlignment,
                priorUnpackAlignment);
        }
    }

    private unsafe uint AllocatePageTable(ReadOnlySpan<ushort> clear)
    {
        var texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture3D, texture);
        fixed (ushort* pointer = clear)
        {
            _gl.TexImage3D(
                TextureTarget.Texture3D,
                0,
                InternalFormat.R16ui,
                PreviewSparseCloudVolumeContract.PageTableWidth,
                PreviewSparseCloudVolumeContract.PageTableHeight,
                PreviewSparseCloudVolumeContract.PageTableDepth,
                0,
                PixelFormat.RedInteger,
                PixelType.UnsignedShort,
                pointer);
        }

        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureMinFilter,
            (int)GLEnum.Nearest);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureMagFilter,
            (int)GLEnum.Nearest);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureWrapS,
            (int)GLEnum.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureWrapT,
            (int)GLEnum.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureWrapR,
            (int)GLEnum.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureBaseLevel,
            0);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureMaxLevel,
            0);
        return texture;
    }

    private void ConfigureAtlasTexture()
    {
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureMinFilter,
            (int)GLEnum.Linear);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureMagFilter,
            (int)GLEnum.Linear);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureWrapS,
            (int)GLEnum.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureWrapT,
            (int)GLEnum.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureWrapR,
            (int)GLEnum.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureBaseLevel,
            0);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureMaxLevel,
            0);
    }

    private static uint GetPageTableHandle(
        uint[] handles,
        int clipmapLevel)
    {
        if (clipmapLevel < 0 || clipmapLevel >= handles.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(clipmapLevel));
        }

        return handles[clipmapLevel];
    }

    private static void ValidateClipmapLevel(int clipmapLevel)
    {
        if (clipmapLevel < 0 ||
            clipmapLevel >= PreviewSparseCloudVolumeContract.ClipmapCount)
        {
            throw new ArgumentOutOfRangeException(nameof(clipmapLevel));
        }
    }

    private static void ThrowIfInjectedFailure(
        int allocationCount,
        int failAfterTextureAllocation)
    {
        if (failAfterTextureAllocation >= 0 &&
            allocationCount >= failAfterTextureAllocation)
        {
            throw new PreviewSparseCloudInjectedAllocationException(
                $"injected-failure-after-{allocationCount}-textures");
        }
    }

    private void DeleteTextures(uint[] textures)
    {
        for (var index = 0; index < textures.Length; index++)
        {
            if (textures[index] == 0)
            {
                continue;
            }

            _gl.DeleteTexture(textures[index]);
            textures[index] = 0;
        }
    }

    private void DeletePublicationFence()
    {
        if (_publicationFence == 0)
        {
            return;
        }

        _gl.DeleteSync(_publicationFence);
        _publicationFence = 0;
    }

    private void FlushErrors()
    {
        for (var index = 0;
             index < 16 && _gl.GetError() != GLEnum.NoError;
             index++)
        {
        }
    }

    private sealed class PreviewSparseCloudInjectedAllocationException(
        string message) : Exception(message);
}
