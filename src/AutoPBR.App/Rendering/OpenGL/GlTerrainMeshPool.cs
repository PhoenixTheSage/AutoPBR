using System.Runtime.InteropServices;

using AutoPBR.App.Rendering.Scene;
using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Shared VAO/VBO/EBO for streamed terrain chunks. Each chunk owns a suballocated range;
/// draws bind once and issue ranges (or MultiDrawIndirect) without per-chunk VAO thrash.
/// Indices are always <see cref="DrawElementsType.UnsignedInt"/> and remapped by base vertex.
/// </summary>
internal sealed class GlTerrainMeshPool : IDisposable
{
    private const int FloatsPerVertex = 12;
    private const int InitialVertexFloatCapacity = 256 * 1024;
    private const int InitialIndexCapacity = 512 * 1024;
    private const int GrowthAlignmentElements = 256 * 1024;
    /// <summary>
    /// Soft VRAM ceiling for the shared terrain VAO pool. Starts at
    /// <see cref="PreviewStageConstants.TerrainMeshPoolBudgetDefaultBytes"/> and may be raised
    /// dynamically toward a hardware-derived ceiling (see ConfigureBudgetCeiling).
    /// </summary>
    internal static long DefaultMaxTotalBufferBytes =>
        PreviewStageConstants.TerrainMeshPoolBudgetDefaultBytes;

    private readonly GL _gl;
    private readonly bool _useBaseVertex;
    private readonly uint _vao;
    private long _maxTotalBufferBytes;
    private long _absoluteCeilingBytes =
        PreviewStageConstants.TerrainMeshPoolBudgetAbsoluteCeilingBytes;
    private uint _vbo;
    private uint _ebo;
    private readonly List<FreeBlock> _freeVertices = new(32);
    private readonly List<FreeBlock> _freeIndices = new(32);
    private int _vertexFloatCapacity;
    private int _indexCapacity;
    private int _vertexFloatHighWater;
    private int _indexHighWater;
    private bool _vaoBound;
    private bool _disposed;
    private uint[]? _indexRemapScratch;

    public GlTerrainMeshPool(
        GL gl,
        long maxTotalBufferBytes = 0,
        bool useBaseVertex = false)
    {
        _gl = gl;
        _useBaseVertex = useBaseVertex;
        if (maxTotalBufferBytes <= 0)
        {
            maxTotalBufferBytes = DefaultMaxTotalBufferBytes;
        }

        _maxTotalBufferBytes = Math.Max(
            maxTotalBufferBytes,
            (long)InitialVertexFloatCapacity * sizeof(float) +
            (long)InitialIndexCapacity * sizeof(uint));
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();
        if (!EnsureGpuCapacity(InitialVertexFloatCapacity, InitialIndexCapacity))
        {
            _gl.DeleteBuffer(_vbo);
            _gl.DeleteBuffer(_ebo);
            _gl.DeleteVertexArray(_vao);
            throw new InvalidOperationException("Unable to allocate the initial terrain mesh pool.");
        }

        ConfigureVertexAttribs();
        _gl.BindVertexArray(0);
    }

    public bool IsValid => !_disposed && _vao != 0;

    public DrawElementsType IndexElementType => DrawElementsType.UnsignedInt;

    internal long VertexCapacityBytes => (long)_vertexFloatCapacity * sizeof(float);
    internal long IndexCapacityBytes => (long)_indexCapacity * sizeof(uint);
    internal long VertexHighWaterBytes => (long)_vertexFloatHighWater * sizeof(float);
    internal long IndexHighWaterBytes => (long)_indexHighWater * sizeof(uint);
    internal long TotalCapacityBytes => VertexCapacityBytes + IndexCapacityBytes;
    internal long MaxTotalBufferBytes => _maxTotalBufferBytes;
    internal int GrowthCount { get; private set; }
    internal int AllocationFailureCount { get; private set; }
    internal GLEnum LastFailure { get; private set; } = GLEnum.NoError;
    internal string LastFailureReason { get; private set; } = "none";

    /// <summary>
    /// When false, EnsureGpuCapacity refuses live-buffer growth copies. Bootstrap may pre-grow;
    /// movement-time uploads must fit existing segments or defer (segmented arena model).
    /// </summary>
    internal bool AllowLiveBufferGrowth { get; set; } = true;

    /// <summary>
    /// Allocates the immutable backing capacity described by the segmented arena before
    /// movement-time streaming begins. The arena is only an admission model; without matching
    /// GL storage, disabling live growth leaves this pool at its tiny constructor bootstrap size.
    /// </summary>
    internal bool TryPreallocateFixedCapacity(int vertexCapacityBytes, int indexCapacityBytes)
    {
        if (_disposed || vertexCapacityBytes <= 0 || indexCapacityBytes <= 0)
        {
            return false;
        }

        var vertexFloatCapacity = checked(
            (vertexCapacityBytes + sizeof(float) - 1) / sizeof(float));
        var indexCapacity = checked(
            (indexCapacityBytes + sizeof(uint) - 1) / sizeof(uint));
        var allocated = EnsureGpuCapacity(
            Math.Max(_vertexFloatCapacity, vertexFloatCapacity),
            Math.Max(_indexCapacity, indexCapacity));
        if (allocated)
        {
            // From this point onward uploads suballocate fixed storage; they never copy the
            // live terrain buffers merely because the camera crossed a chunk boundary.
            AllowLiveBufferGrowth = false;
        }

        return allocated;
    }

    /// <summary>
    /// Raise or lower the soft growth ceiling. Does not shrink existing VBO/EBO allocations;
    /// only gates further EnsureGpuCapacity growth.
    /// </summary>
    internal void ConfigureBudgetCeiling(long maxTotalBufferBytes, long? absoluteCeilingBytes = null)
    {
        if (absoluteCeilingBytes is > 0)
        {
            _absoluteCeilingBytes = Math.Clamp(
                absoluteCeilingBytes.Value,
                PreviewStageConstants.TerrainMeshPoolBudgetFloorBytes,
                PreviewStageConstants.TerrainMeshPoolBudgetAbsoluteCeilingBytes);
        }

        var minBytes =
            (long)InitialVertexFloatCapacity * sizeof(float) +
            (long)InitialIndexCapacity * sizeof(uint);
        _maxTotalBufferBytes = Math.Clamp(
            maxTotalBufferBytes,
            Math.Max(minBytes, PreviewStageConstants.TerrainMeshPoolBudgetFloorBytes),
            _absoluteCeilingBytes);
    }

    /// <summary>
    /// Try to raise the ceiling toward <paramref name="targetBytes"/> after a budget-ceiling
    /// failure so uploads can proceed without immediately evicting visible residents.
    /// </summary>
    internal bool TryRaiseBudgetCeiling(long targetBytes)
    {
        targetBytes = Math.Clamp(
            targetBytes,
            PreviewStageConstants.TerrainMeshPoolBudgetFloorBytes,
            _absoluteCeilingBytes);
        if (targetBytes <= _maxTotalBufferBytes)
        {
            return false;
        }

        _maxTotalBufferBytes = targetBytes;
        LastFailureReason = "none";
        return true;
    }

    public readonly struct Allocation
    {
        public required int VertexFloatOffset { get; init; }
        public required int VertexFloatCount { get; init; }
        public required int IndexOffset { get; init; }
        public required int IndexCount { get; init; }
        public required uint BaseVertex { get; init; }
        public bool IsEmpty => IndexCount <= 0 || VertexFloatCount <= 0;
    }

    public Allocation Upload(ReadOnlySpan<float> interleavedVertices, ReadOnlySpan<uint> indices) =>
        Upload(interleavedVertices, indices, staging: null);

    /// <summary>
    /// Upload into free-list ranges. When <paramref name="staging"/> is provided and the payload
    /// fits one ring segment, CPU writes go through the staging buffer and GPU
    /// <c>CopyBufferSubData</c> fills the resident VBO/EBO (P10.1). Oversized payloads and
    /// GLES / unavailable staging keep direct <c>BufferSubData</c>.
    /// </summary>
    public Allocation Upload(
        ReadOnlySpan<float> interleavedVertices,
        ReadOnlySpan<uint> indices,
        GlTerrainUploadStagingRing? staging)
    {
        if (_disposed || interleavedVertices.IsEmpty || indices.IsEmpty)
        {
            return default;
        }

        if (interleavedVertices.Length % FloatsPerVertex != 0)
        {
            throw new ArgumentException(
                $"Terrain vertices must be a multiple of {FloatsPerVertex} floats.",
                nameof(interleavedVertices));
        }

        var vertexCount = interleavedVertices.Length / FloatsPerVertex;
        var vertexFloats = interleavedVertices.Length;
        if (!TryAllocate(vertexFloats, indices.Length, out var vertexFloatOffset, out var indexOffset))
        {
            if (!GrowToFit(vertexFloats, indices.Length))
            {
                AllocationFailureCount++;
                return default;
            }

            if (!TryAllocate(vertexFloats, indices.Length, out vertexFloatOffset, out indexOffset))
            {
                AllocationFailureCount++;
                return default;
            }
        }

        var baseVertex = (uint)(vertexFloatOffset / FloatsPerVertex);
        ReadOnlySpan<uint> uploadIndices;
        if (_useBaseVertex)
        {
            uploadIndices = indices;
        }
        else
        {
            EnsureIndexRemapScratch(indices.Length);
            var remap = _indexRemapScratch!;
            for (var i = 0; i < indices.Length; i++)
            {
                remap[i] = indices[i] + baseVertex;
            }

            uploadIndices = remap.AsSpan(0, indices.Length);
        }

        ClearGlErrors();
        var usedStaging = false;
        if (staging is { IsValid: true } &&
            staging.TryWrite(
                interleavedVertices,
                uploadIndices,
                out var stagingVertOffset,
                out var stagingIndexOffset))
        {
            usedStaging = true;
            BindVertexArray();
            _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, staging.Handle);
            _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, _vbo);
            _gl.CopyBufferSubData(
                GLEnum.CopyReadBuffer,
                GLEnum.CopyWriteBuffer,
                stagingVertOffset,
                vertexFloatOffset * sizeof(float),
                (nuint)(vertexFloats * sizeof(float)));
            _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, _ebo);
            _gl.CopyBufferSubData(
                GLEnum.CopyReadBuffer,
                GLEnum.CopyWriteBuffer,
                stagingIndexOffset,
                indexOffset * sizeof(uint),
                (nuint)(indices.Length * sizeof(uint)));
            _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
            _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
            UnbindVertexArray();
            // Fence once per frame via GlTerrainUploadStagingRing.EndFrame — not per chunk.
        }
        else
        {
            BindVertexArray();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            _gl.BufferSubData<float>(
                GLEnum.ArrayBuffer,
                vertexFloatOffset * sizeof(float),
                interleavedVertices);
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            _gl.BufferSubData<uint>(
                GLEnum.ElementArrayBuffer,
                indexOffset * sizeof(uint),
                uploadIndices);
            UnbindVertexArray();
        }

        var uploadError = _gl.GetError();
        if (uploadError != GLEnum.NoError)
        {
            LastFailure = uploadError;
            LastFailureReason = usedStaging ? "staging-copy" : "buffer-subdata";
            AllocationFailureCount++;
            InsertFree(_freeVertices, vertexFloatOffset, vertexFloats);
            InsertFree(_freeIndices, indexOffset, indices.Length);
            return default;
        }

        LastFailure = GLEnum.NoError;
        LastFailureReason = "none";

        return new Allocation
        {
            VertexFloatOffset = vertexFloatOffset,
            VertexFloatCount = vertexFloats,
            IndexOffset = indexOffset,
            IndexCount = indices.Length,
            BaseVertex = baseVertex,
        };
    }

    public void Free(in Allocation allocation)
    {
        if (_disposed || allocation.IsEmpty)
        {
            return;
        }

        InsertFree(_freeVertices, allocation.VertexFloatOffset, allocation.VertexFloatCount);
        InsertFree(_freeIndices, allocation.IndexOffset, allocation.IndexCount);
    }

    public void BindVertexArray()
    {
        if (_disposed)
        {
            return;
        }

        _gl.BindVertexArray(_vao);
        _vaoBound = true;
    }

    public void UnbindVertexArray()
    {
        if (!_vaoBound)
        {
            return;
        }

        _gl.BindVertexArray(0);
        _vaoBound = false;
    }

    public void DrawRange(
        int firstIndex,
        int indexCount,
        bool patches = false,
        bool keepBound = false,
        bool updatePatchParameter = true,
        uint baseInstance = 0,
        int baseVertex = 0)
    {
        if (_disposed || indexCount <= 0)
        {
            return;
        }

        if (!keepBound || !_vaoBound)
        {
            BindVertexArray();
        }

        if (patches && updatePatchParameter)
        {
            _gl.PatchParameter(PatchParameterName.Vertices, 3);
        }

        unsafe
        {
            var byteOffset = (void*)(firstIndex * sizeof(uint));
            var mode = patches ? PrimitiveType.Patches : PrimitiveType.Triangles;
            // DrawElements leaves gl_BaseInstance at 0; draw-record shaders need the material index.
            if (_useBaseVertex && baseInstance != 0)
            {
                _gl.DrawElementsInstancedBaseVertexBaseInstance(
                    mode,
                    (uint)indexCount,
                    DrawElementsType.UnsignedInt,
                    byteOffset,
                    1u,
                    baseVertex,
                    baseInstance);
            }
            else if (_useBaseVertex)
            {
                _gl.DrawElementsBaseVertex(
                    mode,
                    (uint)indexCount,
                    DrawElementsType.UnsignedInt,
                    byteOffset,
                    baseVertex);
            }
            else if (baseInstance != 0)
            {
                _gl.DrawElementsInstancedBaseInstance(
                    mode,
                    (uint)indexCount,
                    DrawElementsType.UnsignedInt,
                    byteOffset,
                    1u,
                    baseInstance);
            }
            else
            {
                _gl.DrawElements(
                    mode,
                    (uint)indexCount,
                    DrawElementsType.UnsignedInt,
                    byteOffset);
            }
        }

        if (!keepBound)
        {
            UnbindVertexArray();
        }
    }

    public void MultiDrawIndirect(
        GlIndirectDrawCommandBuffer commands,
        int firstCommand,
        int commandCount,
        bool patches = false,
        bool keepBound = false,
        bool updatePatchParameter = true)
    {
        if (_disposed ||
            !commands.IsValid ||
            commandCount <= 0 ||
            firstCommand < 0 ||
            firstCommand >= commands.CommandCount)
        {
            return;
        }

        commandCount = Math.Min(commandCount, commands.CommandCount - firstCommand);
        if (commandCount <= 0)
        {
            return;
        }

        if (!keepBound || !_vaoBound)
        {
            BindVertexArray();
        }

        if (patches && updatePatchParameter)
        {
            _gl.PatchParameter(PatchParameterName.Vertices, 3);
        }

        commands.Bind();
        unsafe
        {
            var byteOffset = firstCommand * GlIndirectDrawCommandBuffer.CommandByteSize;
            _gl.MultiDrawElementsIndirect(
                patches ? PrimitiveType.Patches : PrimitiveType.Triangles,
                DrawElementsType.UnsignedInt,
                (void*)byteOffset,
                (uint)commandCount,
                GlIndirectDrawCommandBuffer.CommandByteSize);
        }

        commands.Unbind();
        if (!keepBound)
        {
            UnbindVertexArray();
        }
    }

    public unsafe bool MultiDrawIndirectCount(
        GlIndirectDrawCommandBuffer commands,
        uint countBuffer,
        int maxDrawCount,
        MultiDrawElementsIndirectCountProc? proc,
        bool patches = false,
        bool keepBound = false,
        nint drawCountOffset = 0)
    {
        if (_disposed ||
            proc is null ||
            !commands.IsValid ||
            countBuffer == 0 ||
            maxDrawCount <= 0)
        {
            return false;
        }

        if (!keepBound || !_vaoBound)
        {
            BindVertexArray();
        }

        if (patches)
        {
            _gl.PatchParameter(PatchParameterName.Vertices, 3);
        }

        commands.Bind();
        _gl.BindBuffer((BufferTargetARB)0x80EE, countBuffer);
        proc(
            (uint)(patches ? PrimitiveType.Patches : PrimitiveType.Triangles),
            (uint)DrawElementsType.UnsignedInt,
            null,
            drawCountOffset,
            maxDrawCount,
            GlIndirectDrawCommandBuffer.CommandByteSize);
        _gl.BindBuffer((BufferTargetARB)0x80EE, 0);
        commands.Unbind();
        if (!keepBound)
        {
            UnbindVertexArray();
        }

        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public unsafe delegate void MultiDrawElementsIndirectCountProc(
        uint mode,
        uint type,
        void* indirect,
        nint drawCountOffset,
        int maxDrawCount,
        int stride);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_vao != 0)
        {
            _gl.DeleteVertexArray(_vao);
        }

        if (_vbo != 0)
        {
            _gl.DeleteBuffer(_vbo);
        }

        if (_ebo != 0)
        {
            _gl.DeleteBuffer(_ebo);
        }

        _vaoBound = false;
        _freeVertices.Clear();
        _freeIndices.Clear();
    }

    private bool TryAllocate(int vertexFloats, int indexCount, out int vertexFloatOffset, out int indexOffset)
    {
        vertexFloatOffset = -1;
        indexOffset = -1;

        var vertsFromFree = TryTakeFree(_freeVertices, vertexFloats, out var vertOff);
        var gotVerts = vertsFromFree;
        if (!gotVerts && _vertexFloatHighWater + vertexFloats <= _vertexFloatCapacity)
        {
            vertOff = _vertexFloatHighWater;
            _vertexFloatHighWater += vertexFloats;
            gotVerts = true;
        }

        var indicesFromFree = TryTakeFree(_freeIndices, indexCount, out var indexOff);
        var gotIndices = indicesFromFree;
        if (!gotIndices && _indexHighWater + indexCount <= _indexCapacity)
        {
            indexOff = _indexHighWater;
            _indexHighWater += indexCount;
            gotIndices = true;
        }

        if (gotVerts && gotIndices)
        {
            vertexFloatOffset = vertOff;
            indexOffset = indexOff;
            return true;
        }

        if (gotVerts)
        {
            if (vertsFromFree)
            {
                InsertFree(_freeVertices, vertOff, vertexFloats);
            }
            else
            {
                _vertexFloatHighWater -= vertexFloats;
            }
        }

        if (gotIndices)
        {
            if (indicesFromFree)
            {
                InsertFree(_freeIndices, indexOff, indexCount);
            }
            else
            {
                _indexHighWater -= indexCount;
            }
        }

        return false;
    }

    private bool GrowToFit(int vertexFloats, int indexCount)
    {
        var verticesFit = CanAllocate(
            _freeVertices,
            _vertexFloatHighWater,
            _vertexFloatCapacity,
            vertexFloats);
        var indicesFit = CanAllocate(
            _freeIndices,
            _indexHighWater,
            _indexCapacity,
            indexCount);
        var requiredVerts = verticesFit
            ? _vertexFloatCapacity
            : _vertexFloatHighWater + vertexFloats;
        var requiredIndices = indicesFit
            ? _indexCapacity
            : _indexHighWater + indexCount;
        var needVerts = verticesFit
            ? _vertexFloatCapacity
            : GrowCapacity(_vertexFloatCapacity, requiredVerts);
        var needIndices = indicesFit
            ? _indexCapacity
            : GrowCapacity(_indexCapacity, requiredIndices);
        ConstrainGrowthToBudget(
            ref needVerts,
            ref needIndices,
            requiredVerts,
            requiredIndices,
            growVerts: !verticesFit,
            growIndices: !indicesFit);
        return EnsureGpuCapacity(needVerts, needIndices);
    }

    private void ConstrainGrowthToBudget(
        ref int vertexCapacity,
        ref int indexCapacity,
        int requiredVertexCapacity,
        int requiredIndexCapacity,
        bool growVerts,
        bool growIndices)
    {
        var proposedBytes =
            (long)vertexCapacity * sizeof(float) +
            (long)indexCapacity * sizeof(uint);
        if (proposedBytes <= _maxTotalBufferBytes)
        {
            return;
        }

        var requiredBytes =
            (long)requiredVertexCapacity * sizeof(float) +
            (long)requiredIndexCapacity * sizeof(uint);
        if (requiredBytes > _maxTotalBufferBytes)
        {
            return;
        }

        if (growVerts && !growIndices)
        {
            var maxVertexCapacity =
                (int)Math.Min(
                    int.MaxValue,
                    (_maxTotalBufferBytes - (long)indexCapacity * sizeof(uint)) / sizeof(float));
            vertexCapacity = Math.Min(
                maxVertexCapacity,
                GrowCapacityConservatively(_vertexFloatCapacity, requiredVertexCapacity));
            return;
        }

        if (growIndices && !growVerts)
        {
            var maxIndexCapacity =
                (int)Math.Min(
                    int.MaxValue,
                    (_maxTotalBufferBytes - (long)vertexCapacity * sizeof(float)) / sizeof(uint));
            indexCapacity = Math.Min(
                maxIndexCapacity,
                GrowCapacityConservatively(_indexCapacity, requiredIndexCapacity));
            return;
        }

        // Simultaneous pressure at the hard ceiling is rare. Commit only the minimum
        // required stores so neither independently over-reserves the other's budget.
        vertexCapacity = requiredVertexCapacity;
        indexCapacity = requiredIndexCapacity;
    }

    private static bool CanAllocate(
        List<FreeBlock> blocks,
        int highWater,
        int capacity,
        int count)
    {
        if (highWater + count <= capacity)
        {
            return true;
        }

        foreach (var block in blocks)
        {
            if (block.Count >= count)
            {
                return true;
            }
        }

        return false;
    }

    private static int GrowCapacity(int current, int required)
    {
        var increment = Math.Max(current / 2, GrowthAlignmentElements);
        var target = Math.Max((long)required, (long)current + increment);
        target = ((target + GrowthAlignmentElements - 1) / GrowthAlignmentElements) *
                 GrowthAlignmentElements;
        return checked((int)target);
    }

    private static int GrowCapacityConservatively(int current, int required)
    {
        var increment = Math.Max(current / 4, GrowthAlignmentElements);
        var target = Math.Max((long)required, (long)current + increment);
        target = ((target + GrowthAlignmentElements - 1) / GrowthAlignmentElements) *
                 GrowthAlignmentElements;
        return checked((int)target);
    }

    private bool EnsureGpuCapacity(int vertexFloatCapacity, int indexCapacity)
    {
        var growVerts = vertexFloatCapacity > _vertexFloatCapacity;
        var growIndices = indexCapacity > _indexCapacity;
        if (!growVerts && !growIndices && _vertexFloatCapacity > 0)
        {
            return true;
        }

        var targetVertexCapacity = growVerts || _vertexFloatCapacity == 0
            ? vertexFloatCapacity
            : _vertexFloatCapacity;
        var targetIndexCapacity = growIndices || _indexCapacity == 0
            ? indexCapacity
            : _indexCapacity;
        var targetBytes =
            (long)targetVertexCapacity * sizeof(float) +
            (long)targetIndexCapacity * sizeof(uint);
        if (targetBytes > _maxTotalBufferBytes)
        {
            LastFailure = GLEnum.NoError;
            LastFailureReason = "budget-ceiling";
            return false;
        }

        // Initial allocation (_vertexFloatCapacity == 0) is always allowed. Subsequent live
        // growth copies are refused once the segmented arena model is active during movement.
        if (!AllowLiveBufferGrowth &&
            _vertexFloatCapacity > 0 &&
            (growVerts || growIndices))
        {
            LastFailure = GLEnum.NoError;
            LastFailureReason = "live-growth-disabled";
            AllocationFailureCount++;
            return false;
        }

        BindVertexArray();
        uint candidateVbo = 0;
        uint candidateEbo = 0;
        if (growVerts || _vertexFloatCapacity == 0)
        {
            if (!TryCreateReplacementBuffer(
                BufferTargetARB.ArrayBuffer,
                _vbo,
                _vertexFloatCapacity * sizeof(float),
                vertexFloatCapacity * sizeof(float),
                copyBytes: _vertexFloatHighWater * sizeof(float),
                out candidateVbo))
            {
                RestoreLiveBindings();
                UnbindVertexArray();
                return false;
            }
        }

        if (growIndices || _indexCapacity == 0)
        {
            if (!TryCreateReplacementBuffer(
                BufferTargetARB.ElementArrayBuffer,
                _ebo,
                _indexCapacity * sizeof(uint),
                indexCapacity * sizeof(uint),
                copyBytes: _indexHighWater * sizeof(uint),
                out candidateEbo))
            {
                if (candidateVbo != 0)
                {
                    _gl.DeleteBuffer(candidateVbo);
                }

                RestoreLiveBindings();
                UnbindVertexArray();
                return false;
            }
        }

        var oldVbo = _vbo;
        var oldEbo = _ebo;
        if (candidateVbo != 0)
        {
            _vbo = candidateVbo;
            _vertexFloatCapacity = vertexFloatCapacity;
        }

        if (candidateEbo != 0)
        {
            _ebo = candidateEbo;
            _indexCapacity = indexCapacity;
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        ConfigureVertexAttribs();
        if (candidateVbo != 0 && oldVbo != 0)
        {
            _gl.DeleteBuffer(oldVbo);
        }

        if (candidateEbo != 0 && oldEbo != 0)
        {
            _gl.DeleteBuffer(oldEbo);
        }

        UnbindVertexArray();
        GrowthCount++;
        LastFailure = GLEnum.NoError;
        LastFailureReason = "none";
        return true;
    }

    private bool TryCreateReplacementBuffer(
        BufferTargetARB target,
        uint oldBuffer,
        int oldBytes,
        int newBytes,
        int copyBytes,
        out uint newBuffer)
    {
        newBuffer = 0;
        ClearGlErrors();
        newBuffer = _gl.GenBuffer();
        if (newBuffer == 0)
        {
            LastFailure = _gl.GetError();
            if (LastFailure == GLEnum.NoError)
            {
                LastFailure = GLEnum.OutOfMemory;
            }

            LastFailureReason = "buffer-handle";
            return false;
        }

        _gl.BindBuffer(target, newBuffer);
        unsafe
        {
            _gl.BufferData(target, (nuint)newBytes, null, BufferUsageARB.DynamicDraw);
        }

        var allocationError = _gl.GetError();
        if (allocationError != GLEnum.NoError)
        {
            LastFailure = allocationError;
            LastFailureReason = "buffer-data";
            _gl.DeleteBuffer(newBuffer);
            newBuffer = 0;
            return false;
        }

        if (oldBuffer != 0 && copyBytes > 0 && oldBytes > 0)
        {
            _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, oldBuffer);
            _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, newBuffer);
            _gl.CopyBufferSubData(
                GLEnum.CopyReadBuffer,
                GLEnum.CopyWriteBuffer,
                0,
                0,
                (nuint)Math.Min(copyBytes, Math.Min(oldBytes, newBytes)));
            _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
            _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
            var copyError = _gl.GetError();
            if (copyError != GLEnum.NoError)
            {
                LastFailure = copyError;
                LastFailureReason = "buffer-copy";
                _gl.DeleteBuffer(newBuffer);
                newBuffer = 0;
                return false;
            }
        }

        return true;
    }

    private void RestoreLiveBindings()
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        if (_vbo != 0)
        {
            ConfigureVertexAttribs();
        }
    }

    private void ClearGlErrors()
    {
        for (var i = 0; i < 16 && _gl.GetError() != GLEnum.NoError; i++)
        {
        }
    }

    private void ConfigureVertexAttribs()
    {
        var stride = (uint)(FloatsPerVertex * sizeof(float));
        unsafe
        {
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
            _gl.EnableVertexAttribArray(3);
            _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
            _gl.DisableVertexAttribArray(4);
        }
    }

    private void EnsureIndexRemapScratch(int count)
    {
        if (_indexRemapScratch is null || _indexRemapScratch.Length < count)
        {
            _indexRemapScratch = new uint[Math.Max(count, 1024)];
        }
    }

    private static bool TryTakeFree(List<FreeBlock> blocks, int size, out int offset)
    {
        offset = -1;
        var best = -1;
        var bestSize = int.MaxValue;
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Count >= size && b.Count < bestSize)
            {
                best = i;
                bestSize = b.Count;
            }
        }

        if (best < 0)
        {
            return false;
        }

        var chosen = blocks[best];
        offset = chosen.Offset;
        if (chosen.Count == size)
        {
            blocks.RemoveAt(best);
        }
        else
        {
            blocks[best] = new FreeBlock(chosen.Offset + size, chosen.Count - size);
        }

        return true;
    }

    private static void InsertFree(List<FreeBlock> blocks, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }

        blocks.Add(new FreeBlock(offset, count));
        blocks.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        for (var i = 0; i < blocks.Count - 1;)
        {
            var cur = blocks[i];
            var next = blocks[i + 1];
            if (cur.Offset + cur.Count == next.Offset)
            {
                blocks[i] = new FreeBlock(cur.Offset, cur.Count + next.Count);
                blocks.RemoveAt(i + 1);
                continue;
            }

            i++;
        }
    }

    private readonly record struct FreeBlock(int Offset, int Count);
}
