using AutoPBR.Preview;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed class GlIndirectDrawCommandBuffer : IDisposable
{
    public const int CommandDwords = 5;
    public const int CommandByteSize = CommandDwords * sizeof(uint);

    private readonly GL _gl;
    private uint _buffer;
    private int _byteCapacity;
    private uint[] _scratch = [];
    private bool _disposed;

    public GlIndirectDrawCommandBuffer(GL gl) => _gl = gl;

    public bool IsValid => !_disposed && _buffer != 0 && CommandCount > 0;

    public uint Handle => _buffer;

    public int CommandCount { get; private set; }

    public bool Upload(IReadOnlyList<PreviewDrawBatch> batches)
    {
        if (_disposed || batches.Count <= 0)
        {
            CommandCount = 0;
            return false;
        }

        var dwordCount = checked(batches.Count * CommandDwords);
        if (_scratch.Length < dwordCount)
        {
            _scratch = new uint[Math.Max(dwordCount, 64)];
        }

        var dst = _scratch.AsSpan(0, dwordCount);
        dst.Clear();
        for (var i = 0; i < batches.Count; i++)
        {
            WriteCommandDwords(dst.Slice(i * CommandDwords, CommandDwords), batches[i], (uint)i);
        }

        _buffer = _buffer == 0 ? _gl.GenBuffer() : _buffer;
        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _buffer);
        var byteCount = dwordCount * sizeof(uint);
        if (byteCount <= _byteCapacity)
        {
            _gl.BufferSubData<uint>(BufferTargetARB.DrawIndirectBuffer, 0, dst);
        }
        else
        {
            _gl.BufferData<uint>(BufferTargetARB.DrawIndirectBuffer, dst, BufferUsageARB.DynamicDraw);
            _byteCapacity = byteCount;
        }

        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        CommandCount = batches.Count;
        return true;
    }

    public bool EnsureCommandCapacity(int commandCount)
    {
        if (_disposed || commandCount <= 0)
        {
            CommandCount = 0;
            return false;
        }

        _buffer = _buffer == 0 ? _gl.GenBuffer() : _buffer;
        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _buffer);
        var byteCount = checked(commandCount * CommandByteSize);
        if (byteCount > _byteCapacity)
        {
            unsafe
            {
                _gl.BufferData(BufferTargetARB.DrawIndirectBuffer, (nuint)byteCount, null, BufferUsageARB.DynamicDraw);
            }

            _byteCapacity = byteCount;
        }

        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        CommandCount = commandCount;
        return true;
    }

    public void SetCommandCount(int commandCount)
    {
        if (_disposed || commandCount <= 0)
        {
            CommandCount = 0;
            return;
        }

        CommandCount = _byteCapacity <= 0
            ? 0
            : Math.Min(commandCount, _byteCapacity / CommandByteSize);
    }

    public void Bind()
    {
        if (!_disposed && _buffer != 0)
        {
            _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _buffer);
        }
    }

    public void Unbind() => _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);

    internal static void WriteCommandDwords(Span<uint> destination, PreviewDrawBatch batch, uint baseInstance)
    {
        if (destination.Length < CommandDwords)
        {
            throw new ArgumentException("Draw command destination must hold five uints.", nameof(destination));
        }

        var indexCount = Math.Max(0, batch.IndexCount);
        var firstIndex = Math.Max(0, batch.FirstIndex);
        destination[0] = (uint)indexCount;
        destination[1] = indexCount > 0 ? 1u : 0u;
        destination[2] = (uint)firstIndex;
        destination[3] = 0u;
        destination[4] = baseInstance;
    }

    public bool UploadCommands(ReadOnlySpan<uint> commandDwords, int commandCount)
    {
        if (_disposed || commandCount <= 0 || commandDwords.Length < commandCount * CommandDwords)
        {
            CommandCount = 0;
            return false;
        }

        var dwordCount = commandCount * CommandDwords;
        _buffer = _buffer == 0 ? _gl.GenBuffer() : _buffer;
        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, _buffer);
        var byteCount = dwordCount * sizeof(uint);
        var span = commandDwords[..dwordCount];
        if (byteCount <= _byteCapacity)
        {
            _gl.BufferSubData<uint>(BufferTargetARB.DrawIndirectBuffer, 0, span);
        }
        else
        {
            _gl.BufferData<uint>(BufferTargetARB.DrawIndirectBuffer, span, BufferUsageARB.DynamicDraw);
            _byteCapacity = byteCount;
        }

        _gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, 0);
        CommandCount = commandCount;
        return true;
    }

    public static void WriteCommandDwords(
        Span<uint> destination,
        uint indexCount,
        uint firstIndex,
        uint baseInstance)
    {
        if (destination.Length < CommandDwords)
        {
            throw new ArgumentException("Draw command destination must hold five uints.", nameof(destination));
        }

        destination[0] = indexCount;
        destination[1] = indexCount > 0 ? 1u : 0u;
        destination[2] = firstIndex;
        destination[3] = 0u;
        destination[4] = baseInstance;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_buffer != 0)
        {
            _gl.DeleteBuffer(_buffer);
            _buffer = 0;
        }

        CommandCount = 0;
    }
}
