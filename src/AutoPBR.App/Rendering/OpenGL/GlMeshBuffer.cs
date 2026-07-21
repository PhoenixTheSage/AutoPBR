using System.Runtime.InteropServices;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed class GlMeshBuffer : IDisposable
{
    private const BufferTargetARB ParameterBufferTarget = (BufferTargetARB)0x80EE;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate void MultiDrawElementsIndirectCountProc(
        uint mode,
        uint type,
        void* indirect,
        nint drawCountOffset,
        int maxDrawCount,
        int stride);

    private readonly GL _gl;
    private readonly MultiDrawElementsIndirectCountProc? _multiDrawElementsIndirectCount;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private int _indexCount;
    private int _vertexByteCapacity;
    private int _indexByteCapacity;
    private DrawElementsType _indexElementType = DrawElementsType.UnsignedShort;
    private ushort[]? _indexScratchUshort;
    private bool _disposed;
    private bool _vaoBound;

    public GlMeshBuffer(GL gl)
    {
        _gl = gl;
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();
        if (_gl.Context.TryGetProcAddress("glMultiDrawElementsIndirectCount", out var proc) ||
            _gl.Context.TryGetProcAddress("glMultiDrawElementsIndirectCountARB", out proc))
        {
            _multiDrawElementsIndirectCount =
                Marshal.GetDelegateForFunctionPointer<MultiDrawElementsIndirectCountProc>(proc);
        }
    }

    public bool SupportsIndirectCount => _multiDrawElementsIndirectCount is not null;

    public void Upload(float[] interleavedVertices, uint[] indices, int floatsPerVertex = 12)
    {
        _gl.BindVertexArray(_vao);
        _vaoBound = true;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var vertexBytes = interleavedVertices.Length * sizeof(float);
        if (vertexBytes <= _vertexByteCapacity)
        {
            _gl.BufferSubData<float>(GLEnum.ArrayBuffer, 0, interleavedVertices.AsSpan());
        }
        else
        {
            _gl.BufferData<float>(GLEnum.ArrayBuffer, interleavedVertices.AsSpan(), GLEnum.StaticDraw);
            _vertexByteCapacity = vertexBytes;
        }

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        var maxIndex = 0u;
        foreach (var i in indices)
        {
            maxIndex = Math.Max(maxIndex, i);
        }

        if (maxIndex <= ushort.MaxValue)
        {
            EnsureUshortScratch(indices.Length);
            for (var j = 0; j < indices.Length; j++)
            {
                _indexScratchUshort![j] = (ushort)indices[j];
            }

            var indexBytes = indices.Length * sizeof(ushort);
            if (_indexElementType == DrawElementsType.UnsignedShort && indexBytes <= _indexByteCapacity)
            {
                _gl.BufferSubData<ushort>(GLEnum.ElementArrayBuffer, 0, _indexScratchUshort.AsSpan(0, indices.Length));
            }
            else
            {
                _gl.BufferData<ushort>(GLEnum.ElementArrayBuffer, _indexScratchUshort.AsSpan(0, indices.Length), GLEnum.StaticDraw);
                _indexByteCapacity = indexBytes;
            }

            _indexElementType = DrawElementsType.UnsignedShort;
        }
        else
        {
            var indexBytes = indices.Length * sizeof(uint);
            if (_indexElementType == DrawElementsType.UnsignedInt && indexBytes <= _indexByteCapacity)
            {
                _gl.BufferSubData<uint>(GLEnum.ElementArrayBuffer, 0, indices.AsSpan());
            }
            else
            {
                _gl.BufferData<uint>(GLEnum.ElementArrayBuffer, indices.AsSpan(), GLEnum.StaticDraw);
                _indexByteCapacity = indexBytes;
            }

            _indexElementType = DrawElementsType.UnsignedInt;
        }

        ConfigureVertexAttribs(floatsPerVertex);
        UnbindVertexArray();
        _indexCount = indices.Length;
    }

    private void EnsureUshortScratch(int requiredLength)
    {
        if (_indexScratchUshort is null || _indexScratchUshort.Length < requiredLength)
        {
            _indexScratchUshort = new ushort[Math.Max(requiredLength, 256)];
        }
    }

    private void ConfigureVertexAttribs(int floatsPerVertex)
    {
        var stride = (uint)(floatsPerVertex * sizeof(float));
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
            if (floatsPerVertex >= 13)
            {
                _gl.EnableVertexAttribArray(4);
                // Bit-cast element index stored as float; decode with floatBitsToInt in the shader (ANGLE-safe).
                _gl.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));
            }
            else
            {
                _gl.DisableVertexAttribArray(4);
            }
        }
    }

    public void BindVertexArray()
    {
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

    /// <summary>Clears local bind tracking without touching GL (after another VAO took over).</summary>
    public void ClearBoundTracking() => _vaoBound = false;

    public void Draw(bool patches = false, bool keepBound = false)
    {
        BindVertexArray();
        if (patches)
        {
            _gl.PatchParameter(PatchParameterName.Vertices, 3);
        }

        unsafe
        {
            _gl.DrawElements(patches ? PrimitiveType.Patches : PrimitiveType.Triangles, (uint)_indexCount, _indexElementType, (void*)0);
        }

        if (!keepBound)
        {
            UnbindVertexArray();
        }
    }

    /// <summary>Draw a subrange of the index buffer (indices are measured in elements, not bytes).</summary>
    public void DrawRange(int firstIndex, int indexCount, bool patches = false, bool keepBound = false)
    {
        if (indexCount <= 0)
        {
            return;
        }

        if (!keepBound)
        {
            BindVertexArray();
        }
        else if (!_vaoBound)
        {
            BindVertexArray();
        }

        if (patches)
        {
            _gl.PatchParameter(PatchParameterName.Vertices, 3);
        }

        unsafe
        {
            var byteOffset = _indexElementType == DrawElementsType.UnsignedShort
                ? (void*)(firstIndex * sizeof(ushort))
                : (void*)(firstIndex * sizeof(uint));
            _gl.DrawElements(patches ? PrimitiveType.Patches : PrimitiveType.Triangles, (uint)indexCount, _indexElementType, byteOffset);
        }

        if (!keepBound)
        {
            UnbindVertexArray();
        }
    }

    public void DrawIndirect(
        GlIndirectDrawCommandBuffer commands,
        int commandIndex,
        bool patches = false,
        bool keepBound = false)
    {
        if (!commands.IsValid || commandIndex < 0 || commandIndex >= commands.CommandCount)
        {
            return;
        }

        if (!keepBound)
        {
            BindVertexArray();
        }
        else if (!_vaoBound)
        {
            BindVertexArray();
        }

        if (patches)
        {
            _gl.PatchParameter(PatchParameterName.Vertices, 3);
        }

        commands.Bind();
        unsafe
        {
            var byteOffset = commandIndex * GlIndirectDrawCommandBuffer.CommandByteSize;
            _gl.DrawElementsIndirect(
                patches ? PrimitiveType.Patches : PrimitiveType.Triangles,
                _indexElementType,
                (void*)byteOffset);
        }

        commands.Unbind();

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
        bool keepBound = false)
    {
        if (!commands.IsValid ||
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

        if (!keepBound)
        {
            BindVertexArray();
        }
        else if (!_vaoBound)
        {
            BindVertexArray();
        }

        if (patches)
        {
            _gl.PatchParameter(PatchParameterName.Vertices, 3);
        }

        commands.Bind();
        unsafe
        {
            var byteOffset = firstCommand * GlIndirectDrawCommandBuffer.CommandByteSize;
            _gl.MultiDrawElementsIndirect(
                patches ? PrimitiveType.Patches : PrimitiveType.Triangles,
                _indexElementType,
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
        bool patches = false,
        bool keepBound = false)
    {
        if (_multiDrawElementsIndirectCount is null ||
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
        _gl.BindBuffer(ParameterBufferTarget, countBuffer);
        _multiDrawElementsIndirectCount(
            (uint)(patches ? PrimitiveType.Patches : PrimitiveType.Triangles),
            (uint)_indexElementType,
            null,
            0,
            maxDrawCount,
            GlIndirectDrawCommandBuffer.CommandByteSize);
        _gl.BindBuffer(ParameterBufferTarget, 0);
        commands.Unbind();

        if (!keepBound)
        {
            UnbindVertexArray();
        }

        return true;
    }

    public int IndexCount => _indexCount;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _gl.DeleteBuffer(_vbo);
            _gl.DeleteBuffer(_ebo);
            _gl.DeleteVertexArray(_vao);
        }
        catch (Exception)
        {
            // Context may already be destroyed (e.g. Avalonia OpenGlDeinit after WGL sidecar teardown).
        }
    }
}
