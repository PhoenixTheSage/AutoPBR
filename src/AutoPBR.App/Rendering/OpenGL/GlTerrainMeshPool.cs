using System.Runtime.InteropServices;

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

    private readonly GL _gl;
    private readonly uint _vao;
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

    public GlTerrainMeshPool(GL gl)
    {
        _gl = gl;
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();
        EnsureGpuCapacity(InitialVertexFloatCapacity, InitialIndexCapacity);
        ConfigureVertexAttribs();
        _gl.BindVertexArray(0);
    }

    public bool IsValid => !_disposed && _vao != 0;

    public DrawElementsType IndexElementType => DrawElementsType.UnsignedInt;

    public readonly struct Allocation
    {
        public required int VertexFloatOffset { get; init; }
        public required int VertexFloatCount { get; init; }
        public required int IndexOffset { get; init; }
        public required int IndexCount { get; init; }
        public required uint BaseVertex { get; init; }
        public bool IsEmpty => IndexCount <= 0 || VertexFloatCount <= 0;
    }

    public Allocation Upload(ReadOnlySpan<float> interleavedVertices, ReadOnlySpan<uint> indices)
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
            GrowToFit(vertexFloats, indices.Length);
            if (!TryAllocate(vertexFloats, indices.Length, out vertexFloatOffset, out indexOffset))
            {
                throw new InvalidOperationException("Terrain mesh pool failed to allocate after grow.");
            }
        }

        var baseVertex = (uint)(vertexFloatOffset / FloatsPerVertex);
        EnsureIndexRemapScratch(indices.Length);
        var remap = _indexRemapScratch!;
        for (var i = 0; i < indices.Length; i++)
        {
            remap[i] = indices[i] + baseVertex;
        }

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
            remap.AsSpan(0, indices.Length));
        UnbindVertexArray();

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
        bool updatePatchParameter = true)
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
            _gl.DrawElements(
                patches ? PrimitiveType.Patches : PrimitiveType.Triangles,
                (uint)indexCount,
                DrawElementsType.UnsignedInt,
                byteOffset);
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

        var gotVerts = TryTakeFree(_freeVertices, vertexFloats, out var vertOff);
        var gotIndices = TryTakeFree(_freeIndices, indexCount, out var indexOff);
        if (gotVerts && gotIndices)
        {
            vertexFloatOffset = vertOff;
            indexOffset = indexOff;
            return true;
        }

        if (gotVerts)
        {
            InsertFree(_freeVertices, vertOff, vertexFloats);
        }

        if (gotIndices)
        {
            InsertFree(_freeIndices, indexOff, indexCount);
        }

        if (_vertexFloatHighWater + vertexFloats <= _vertexFloatCapacity &&
            _indexHighWater + indexCount <= _indexCapacity)
        {
            vertexFloatOffset = _vertexFloatHighWater;
            indexOffset = _indexHighWater;
            _vertexFloatHighWater += vertexFloats;
            _indexHighWater += indexCount;
            return true;
        }

        return false;
    }

    private void GrowToFit(int vertexFloats, int indexCount)
    {
        var needVerts = Math.Max(_vertexFloatCapacity * 2, _vertexFloatHighWater + vertexFloats);
        var needIndices = Math.Max(_indexCapacity * 2, _indexHighWater + indexCount);
        EnsureGpuCapacity(needVerts, needIndices);
    }

    private void EnsureGpuCapacity(int vertexFloatCapacity, int indexCapacity)
    {
        var growVerts = vertexFloatCapacity > _vertexFloatCapacity;
        var growIndices = indexCapacity > _indexCapacity;
        if (!growVerts && !growIndices && _vertexFloatCapacity > 0)
        {
            return;
        }

        BindVertexArray();
        if (growVerts || _vertexFloatCapacity == 0)
        {
            GrowBuffer(
                BufferTargetARB.ArrayBuffer,
                ref _vbo,
                _vertexFloatCapacity * sizeof(float),
                vertexFloatCapacity * sizeof(float),
                copyBytes: _vertexFloatHighWater * sizeof(float));
            _vertexFloatCapacity = vertexFloatCapacity;
        }

        if (growIndices || _indexCapacity == 0)
        {
            GrowBuffer(
                BufferTargetARB.ElementArrayBuffer,
                ref _ebo,
                _indexCapacity * sizeof(uint),
                indexCapacity * sizeof(uint),
                copyBytes: _indexHighWater * sizeof(uint));
            _indexCapacity = indexCapacity;
        }

        // EBO binding is VAO state; rebind after possible EBO recreation.
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        ConfigureVertexAttribs();
        UnbindVertexArray();
    }

    private void GrowBuffer(
        BufferTargetARB target,
        ref uint buffer,
        int oldBytes,
        int newBytes,
        int copyBytes)
    {
        var newBuffer = _gl.GenBuffer();
        _gl.BindBuffer(target, newBuffer);
        unsafe
        {
            _gl.BufferData(target, (nuint)newBytes, null, BufferUsageARB.DynamicDraw);
        }

        if (buffer != 0 && copyBytes > 0 && oldBytes > 0)
        {
            _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, buffer);
            _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, newBuffer);
            _gl.CopyBufferSubData(
                GLEnum.CopyReadBuffer,
                GLEnum.CopyWriteBuffer,
                0,
                0,
                (nuint)Math.Min(copyBytes, Math.Min(oldBytes, newBytes)));
            _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
            _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
        }

        if (buffer != 0)
        {
            _gl.DeleteBuffer(buffer);
        }

        buffer = newBuffer;
        _gl.BindBuffer(target, buffer);
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
