using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed class GlPersistentMappedUploadBuffer : IDisposable
{
    private const uint MapWriteBit = 0x0002;
    private const uint MapPersistentBit = 0x0040;
    private const uint MapCoherentBit = 0x0080;
    private const uint DynamicStorageBit = 0x0100;
    private const uint ClientStorageBit = 0x0200;
    private const uint ClientMappedBufferBarrierBit = 0x00004000;
    private const int SegmentCount = 3;

    private readonly GL _gl;
    private readonly BufferTargetARB _target;
    private readonly uint _bindingPoint;
    private readonly bool _indexedBinding;
    private readonly int _payloadByteSize;
    private readonly int _segmentByteSize;
    private readonly int _bufferByteSize;
    private readonly bool _preferPersistent;
    private uint _buffer;
    private unsafe byte* _mapped;
    private bool _persistent;
    private bool _disposed;
    private int _nextSegment;
    private int _activeSegment;
    private nint _activeOffset;
    private readonly nint[] _segmentFences = new nint[SegmentCount];

    public GlPersistentMappedUploadBuffer(
        GL gl,
        BufferTargetARB target,
        uint bindingPoint,
        int byteSize,
        int alignmentBytes,
        bool preferPersistent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteSize);
        _gl = gl;
        _target = target;
        _bindingPoint = bindingPoint;
        _indexedBinding = true;
        _payloadByteSize = byteSize;
        _segmentByteSize = AlignUp(byteSize, Math.Max(1, alignmentBytes));
        _bufferByteSize = checked(_segmentByteSize * SegmentCount);
        _preferPersistent = preferPersistent;
        CreateBuffer();
    }

    public GlPersistentMappedUploadBuffer(
        GL gl,
        BufferTargetARB target,
        int byteSize,
        int alignmentBytes,
        bool preferPersistent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteSize);
        _gl = gl;
        _target = target;
        _bindingPoint = 0;
        _indexedBinding = false;
        _payloadByteSize = byteSize;
        _segmentByteSize = AlignUp(byteSize, Math.Max(1, alignmentBytes));
        _bufferByteSize = checked(_segmentByteSize * SegmentCount);
        _preferPersistent = preferPersistent;
        CreateBuffer();
    }

    public uint Handle => _buffer;

    public bool UsesPersistentMapping => _persistent;

    public nint ActiveOffset => _activeOffset;

    public void Upload(ReadOnlySpan<byte> bytes)
    {
        if (_disposed || _buffer == 0)
        {
            return;
        }

        if (bytes.Length > _payloadByteSize)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes.Length, "Upload exceeds persistent buffer capacity.");
        }

        _activeSegment = _nextSegment;
        WaitForSegment(_activeSegment);
        _activeOffset = (nint)(_activeSegment * _segmentByteSize);
        _nextSegment = (_nextSegment + 1) % SegmentCount;

        _gl.BindBuffer(_target, _buffer);
        if (_persistent)
        {
            unsafe
            {
                bytes.CopyTo(new Span<byte>(_mapped + _activeOffset, bytes.Length));
            }

            _gl.MemoryBarrier(ClientMappedBufferBarrierBit);
        }
        else
        {
            _gl.BufferSubData<byte>(_target, _activeOffset, bytes);
        }

        BindRange();
        _gl.BindBuffer(_target, 0);
    }

    public void BindBase()
    {
        if (!_disposed && _buffer != 0)
        {
            BindRange();
        }
    }

    public void BindBuffer()
    {
        if (!_disposed && _buffer != 0)
        {
            _gl.BindBuffer(_target, _buffer);
        }
    }

    /// <summary>Fence the current ring segment after issuing draws which consume it.</summary>
    public void MarkSubmitted()
    {
        if (_disposed || !_persistent || _buffer == 0)
        {
            return;
        }

        DeleteFence(_activeSegment);
        _segmentFences[_activeSegment] = _gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, (uint)0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < SegmentCount; i++)
        {
            DeleteFence(i);
        }
        unsafe
        {
            if (_persistent && _mapped is not null)
            {
                _gl.BindBuffer(_target, _buffer);
                _gl.UnmapBuffer(_target);
                _gl.BindBuffer(_target, 0);
                _mapped = null;
            }
        }

        if (_buffer != 0)
        {
            _gl.DeleteBuffer(_buffer);
            _buffer = 0;
        }
    }

    private void CreateBuffer()
    {
        _buffer = _gl.GenBuffer();
        _gl.BindBuffer(_target, _buffer);

        if (_preferPersistent && TryCreatePersistentStorage())
        {
            BindRange();
            _gl.BindBuffer(_target, 0);
            return;
        }

        if (_preferPersistent)
        {
            _gl.BindBuffer(_target, 0);
            _gl.DeleteBuffer(_buffer);
            _buffer = _gl.GenBuffer();
            _gl.BindBuffer(_target, _buffer);
        }

        _persistent = false;
        unsafe
        {
            _gl.BufferData(_target, (nuint)_bufferByteSize, null, BufferUsageARB.DynamicDraw);
        }

        BindRange();
        _gl.BindBuffer(_target, 0);
    }

    private bool TryCreatePersistentStorage()
    {
        try
        {
            unsafe
            {
                _gl.BufferStorage(
                    (GLEnum)_target,
                    (nuint)_bufferByteSize,
                    null,
                    DynamicStorageBit | ClientStorageBit | MapWriteBit | MapPersistentBit | MapCoherentBit);
                _mapped = (byte*)_gl.MapBufferRange(
                    _target,
                    0,
                    (uint)_bufferByteSize,
                    MapWriteBit | MapPersistentBit | MapCoherentBit);
                _persistent = _mapped is not null;
            }

            return _persistent;
        }
        catch
        {
            unsafe
            {
                _mapped = null;
            }

            _persistent = false;
            return false;
        }
    }

    private void BindRange()
    {
        if (_indexedBinding)
        {
            _gl.BindBufferRange(_target, _bindingPoint, _buffer, _activeOffset, (nuint)_payloadByteSize);
        }
    }

    private void WaitForSegment(int segment)
    {
        var fence = _segmentFences[segment];
        if (!_persistent || fence == 0)
        {
            return;
        }

        const uint syncFlushCommandsBit = 0x00000001;
        _ = _gl.ClientWaitSync(fence, syncFlushCommandsBit, ulong.MaxValue);
        DeleteFence(segment);
    }

    private void DeleteFence(int segment)
    {
        if (_segmentFences[segment] == 0)
        {
            return;
        }

        _gl.DeleteSync(_segmentFences[segment]);
        _segmentFences[segment] = 0;
    }

    private static int AlignUp(int value, int alignment) =>
        checked(((value + alignment - 1) / alignment) * alignment);
}
