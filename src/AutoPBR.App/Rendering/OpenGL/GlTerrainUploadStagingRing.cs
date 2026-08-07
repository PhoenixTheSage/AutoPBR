using System.Runtime.InteropServices;

using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Persistent-mapped (or BufferSubData fallback) COPY_READ staging ring for terrain chunk uploads.
/// Packs multiple chunk payloads into one ring segment per frame; fence once via
/// <see cref="EndFrame"/>. Advancing every upload with a flushing ClientWaitSync stalls the GPU.
/// </summary>
internal sealed class GlTerrainUploadStagingRing : IDisposable
{
    private const uint MapWriteBit = 0x0002;
    private const uint MapPersistentBit = 0x0040;
    private const uint MapCoherentBit = 0x0080;
    private const uint DynamicStorageBit = 0x0100;
    private const uint ClientStorageBit = 0x0200;
    private const uint ClientMappedBufferBarrierBit = 0x00004000;
    private const int SegmentCount = 3;
    private const int Alignment = 256;

    private readonly GL _gl;
    private readonly int _segmentByteSize;
    private readonly int _bufferByteSize;
    private readonly bool _preferPersistent;
    private uint _buffer;
    private unsafe byte* _mapped;
    private bool _persistent;
    private bool _disposed;
    private int _nextSegment;
    private int _activeSegment;
    private int _cursorInSegment;
    private bool _segmentHasWrites;
    private readonly nint[] _segmentFences = new nint[SegmentCount];

    public GlTerrainUploadStagingRing(GL gl, bool preferPersistent, int segmentByteSize = 0)
    {
        _gl = gl;
        _preferPersistent = preferPersistent;
        if (segmentByteSize <= 0)
        {
            segmentByteSize = (int)PreviewStageConstants.TerrainMaxUploadBytesPerFrameCatchUp;
        }

        _segmentByteSize = AlignUp(segmentByteSize, Alignment);
        _bufferByteSize = checked(_segmentByteSize * SegmentCount);
        CreateBuffer();
    }

    public uint Handle => _buffer;
    public bool UsesPersistentMapping => _persistent;
    public bool IsValid => !_disposed && _buffer != 0;

    /// <summary>
    /// Pack remapped verts+indices into the current ring segment (or the next one when full).
    /// Returns false when the payload exceeds one segment (caller falls back to BufferSubData).
    /// </summary>
    public bool TryWrite(
        ReadOnlySpan<float> interleavedVertices,
        ReadOnlySpan<uint> remappedIndices,
        out nint vertexByteOffset,
        out nint indexByteOffset)
    {
        vertexByteOffset = 0;
        indexByteOffset = 0;
        if (_disposed || _buffer == 0)
        {
            return false;
        }

        var vertBytes = checked(interleavedVertices.Length * sizeof(float));
        var indexBytes = checked(remappedIndices.Length * sizeof(uint));
        var vertAligned = AlignUp(vertBytes, Alignment);
        var packed = checked(vertAligned + indexBytes);
        if (packed > _segmentByteSize)
        {
            return false;
        }

        if (!_segmentHasWrites || _cursorInSegment + packed > _segmentByteSize)
        {
            if (_segmentHasWrites)
            {
                // Segment full mid-frame: fence it before recycling. Rare with a 16 MiB segment.
                FenceActiveSegment();
            }

            // Nonblocking: if the next ring segment's fence is still busy, defer the upload to a
            // later frame instead of ClientWaitSync(…, ulong.MaxValue) on the render thread.
            if (!TryAdvanceSegment())
            {
                return false;
            }

            _cursorInSegment = 0;
            _segmentHasWrites = true;
        }

        var baseOffset = _activeSegment * _segmentByteSize + _cursorInSegment;
        vertexByteOffset = baseOffset;
        indexByteOffset = baseOffset + vertAligned;

        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, _buffer);
        if (_persistent)
        {
            unsafe
            {
                interleavedVertices.CopyTo(
                    new Span<float>((float*)(_mapped + vertexByteOffset), interleavedVertices.Length));
                remappedIndices.CopyTo(
                    new Span<uint>((uint*)(_mapped + indexByteOffset), remappedIndices.Length));
            }

            _gl.MemoryBarrier(ClientMappedBufferBarrierBit);
        }
        else
        {
            _gl.BufferSubData(
                BufferTargetARB.CopyReadBuffer,
                vertexByteOffset,
                MemoryMarshal.AsBytes(interleavedVertices));
            _gl.BufferSubData(
                BufferTargetARB.CopyReadBuffer,
                indexByteOffset,
                MemoryMarshal.AsBytes(remappedIndices));
        }

        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
        _cursorInSegment = checked(_cursorInSegment + AlignUp(packed, Alignment));
        return true;
    }

    /// <summary>
    /// Fence the active segment after a frame's staging copies. Call once after terrain uploads,
    /// not after every chunk (flushing ClientWaitSync mid-drain stalls the GPU).
    /// </summary>
    public void EndFrame()
    {
        if (!_segmentHasWrites)
        {
            return;
        }

        FenceActiveSegment();
        _segmentHasWrites = false;
        _cursorInSegment = 0;
    }

    /// <summary>Obsolete alias — prefer <see cref="EndFrame"/>.</summary>
    public void MarkSubmitted() => EndFrame();

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
                _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, _buffer);
                _gl.UnmapBuffer(BufferTargetARB.CopyReadBuffer);
                _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
                _mapped = null;
            }
        }

        if (_buffer != 0)
        {
            _gl.DeleteBuffer(_buffer);
            _buffer = 0;
        }
    }

    private void FenceActiveSegment()
    {
        if (_disposed || !_persistent || _buffer == 0)
        {
            return;
        }

        DeleteFence(_activeSegment);
        _segmentFences[_activeSegment] = _gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, (uint)0);
    }

    /// <summary>
    /// Zero-timeout poll for a staging segment. Used by the transfer queue so pressure defers
    /// uploads instead of stalling the Scene thread.
    /// </summary>
    public bool TryAcquireSegment(int segment)
    {
        if ((uint)segment >= (uint)SegmentCount)
        {
            return false;
        }

        return TryWaitForSegmentReady(segment);
    }

    private bool TryAdvanceSegment()
    {
        var candidate = _nextSegment;
        if (!TryWaitForSegmentReady(candidate))
        {
            return false;
        }

        _activeSegment = candidate;
        _nextSegment = (_nextSegment + 1) % SegmentCount;
        return true;
    }

    private void CreateBuffer()
    {
        _buffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, _buffer);

        if (_preferPersistent && TryCreatePersistentStorage())
        {
            _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
            return;
        }

        if (_preferPersistent)
        {
            _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
            _gl.DeleteBuffer(_buffer);
            _buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, _buffer);
        }

        _persistent = false;
        unsafe
        {
            _gl.BufferData(
                BufferTargetARB.CopyReadBuffer,
                (nuint)_bufferByteSize,
                null,
                BufferUsageARB.DynamicDraw);
        }

        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
    }

    private bool TryCreatePersistentStorage()
    {
        try
        {
            unsafe
            {
                _gl.BufferStorage(
                    GLEnum.CopyReadBuffer,
                    (nuint)_bufferByteSize,
                    null,
                    DynamicStorageBit | ClientStorageBit | MapWriteBit | MapPersistentBit | MapCoherentBit);
                _mapped = (byte*)_gl.MapBufferRange(
                    BufferTargetARB.CopyReadBuffer,
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

    private bool TryWaitForSegmentReady(int segment)
    {
        var fence = _segmentFences[segment];
        if (!_persistent || fence == 0)
        {
            return true;
        }

        // Zero-timeout poll only — never ClientWaitSync with a positive/infinite timeout on the
        // render thread (plan acceptance gate). Do not pass SYNC_FLUSH_COMMANDS_BIT.
        var status = _gl.ClientWaitSync(fence, 0u, 0);
        if (status is GLEnum.TimeoutExpired || (int)status == 0x911B)
        {
            return false;
        }

        DeleteFence(segment);
        return true;
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
