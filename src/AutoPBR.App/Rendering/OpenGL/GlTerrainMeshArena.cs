namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Pure allocation model for fixed-size terrain vertex and index buffers. Each reservation owns
/// paired ranges in one immutable segment; ranges are never moved and the arena never grows.
/// </summary>
internal sealed class GlTerrainMeshArena
{
    private readonly Segment[] _segments;
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly int _vertexPageBytes;
    private readonly int _indexPageBytes;
    private readonly int _vertexHeadroomPages;
    private readonly int _indexHeadroomPages;
    private long _nextId;

    public GlTerrainMeshArena(
        int segmentCount,
        int vertexSegmentBytes,
        int indexSegmentBytes,
        int vertexPageBytes = 4096,
        int indexPageBytes = 4096,
        int transitionVertexHeadroomBytes = 0,
        int transitionIndexHeadroomBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentCount);
        ValidatePageLayout(vertexSegmentBytes, vertexPageBytes, nameof(vertexSegmentBytes));
        ValidatePageLayout(indexSegmentBytes, indexPageBytes, nameof(indexSegmentBytes));
        ArgumentOutOfRangeException.ThrowIfNegative(transitionVertexHeadroomBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(transitionIndexHeadroomBytes);

        _vertexPageBytes = vertexPageBytes;
        _indexPageBytes = indexPageBytes;
        var vertexPages = vertexSegmentBytes / vertexPageBytes;
        var indexPages = indexSegmentBytes / indexPageBytes;
        _vertexHeadroomPages = PagesFor(transitionVertexHeadroomBytes, vertexPageBytes);
        _indexHeadroomPages = PagesFor(transitionIndexHeadroomBytes, indexPageBytes);
        if (_vertexHeadroomPages > vertexPages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transitionVertexHeadroomBytes),
                "Transition headroom cannot exceed a vertex segment.");
        }

        if (_indexHeadroomPages > indexPages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transitionIndexHeadroomBytes),
                "Transition headroom cannot exceed an index segment.");
        }

        _segments = new Segment[segmentCount];
        for (var i = 0; i < segmentCount; i++)
        {
            _segments[i] = new Segment(vertexPages, indexPages);
        }

        VertexSegmentBytes = vertexSegmentBytes;
        IndexSegmentBytes = indexSegmentBytes;
    }

    public int SegmentCount => _segments.Length;
    public int VertexSegmentBytes { get; }
    public int IndexSegmentBytes { get; }
    public int VertexPageBytes => _vertexPageBytes;
    public int IndexPageBytes => _indexPageBytes;
    public int VertexCapacityBytes => checked(VertexSegmentBytes * SegmentCount);
    public int IndexCapacityBytes => checked(IndexSegmentBytes * SegmentCount);
    public int TransitionVertexHeadroomBytes =>
        checked(_vertexHeadroomPages * _vertexPageBytes * SegmentCount);
    public int TransitionIndexHeadroomBytes =>
        checked(_indexHeadroomPages * _indexPageBytes * SegmentCount);

    /// <summary>
    /// Scales the profile-wide, combined vertex/index transition reserve to the capacity that
    /// the segmented arena actually materialized, then distributes it across both streams and
    /// all segments. At least one ordinary page remains available in every segment.
    /// </summary>
    internal static int ResolveTransitionHeadroomPerStreamSegment(
        long requestedArenaBytes,
        long requestedTransitionReserveBytes,
        long realizedArenaBytes,
        int segmentCount,
        int pageBytes,
        int segmentBytes)
    {
        if (requestedArenaBytes <= 0 ||
            requestedTransitionReserveBytes <= 0 ||
            realizedArenaBytes <= 0 ||
            segmentCount <= 0 ||
            pageBytes <= 0 ||
            segmentBytes <= pageBytes)
        {
            return 0;
        }

        var boundedReserve = Math.Min(requestedTransitionReserveBytes, requestedArenaBytes);
        var scaledCombinedReserve = boundedReserve > long.MaxValue / realizedArenaBytes
            ? (long)Math.Ceiling((double)boundedReserve * realizedArenaBytes / requestedArenaBytes)
            : DivideRoundUp(boundedReserve * realizedArenaBytes, requestedArenaBytes);
        var perStreamSegment = DivideRoundUp(
            scaledCombinedReserve,
            checked((long)segmentCount * 2));
        var aligned = checked(DivideRoundUp(perStreamSegment, pageBytes) * pageBytes);
        return (int)Math.Min(aligned, segmentBytes - pageBytes);
    }

    /// <summary>
    /// Atomically reserves page-aligned vertex and index ranges in the same segment.
    /// Ordinary requests preserve transition headroom in that segment; transition requests may
    /// consume it. A failed paired request changes no arena state.
    /// </summary>
    public bool TryReserve(
        int vertexBytes,
        int indexBytes,
        bool isTransition,
        out Reservation reservation)
    {
        reservation = default;
        if (vertexBytes <= 0 || indexBytes <= 0)
        {
            return false;
        }

        int vertexPages;
        int indexPages;
        try
        {
            vertexPages = PagesFor(vertexBytes, _vertexPageBytes);
            indexPages = PagesFor(indexBytes, _indexPageBytes);
        }
        catch (OverflowException)
        {
            return false;
        }

        var bestSegment = -1;
        var bestVertexStart = 0;
        var bestIndexStart = 0;
        var bestWaste = int.MaxValue;
        for (var i = 0; i < _segments.Length; i++)
        {
            var segment = _segments[i];
            if (!isTransition &&
                (segment.Vertices.FreePages - vertexPages < _vertexHeadroomPages ||
                 segment.Indices.FreePages - indexPages < _indexHeadroomPages))
            {
                continue;
            }

            if (!segment.Vertices.TryFind(vertexPages, out var vertexStart, out var vertexBlock) ||
                !segment.Indices.TryFind(indexPages, out var indexStart, out var indexBlock))
            {
                continue;
            }

            var waste = checked(vertexBlock - vertexPages + indexBlock - indexPages);
            if (waste >= bestWaste)
            {
                continue;
            }

            bestSegment = i;
            bestVertexStart = vertexStart;
            bestIndexStart = indexStart;
            bestWaste = waste;
        }

        if (bestSegment < 0)
        {
            return false;
        }

        var selected = _segments[bestSegment];
        selected.Vertices.Reserve(bestVertexStart, vertexPages);
        selected.Indices.Reserve(bestIndexStart, indexPages);

        var id = checked(++_nextId);
        reservation = new Reservation(
            id,
            bestSegment,
            bestVertexStart * _vertexPageBytes,
            vertexBytes,
            vertexPages * _vertexPageBytes,
            bestIndexStart * _indexPageBytes,
            indexBytes,
            indexPages * _indexPageBytes,
            isTransition);
        _entries.Add(
            id,
            new Entry(
                reservation,
                bestVertexStart,
                vertexPages,
                bestIndexStart,
                indexPages,
                EntryState.Reserved));
        return true;
    }

    public bool TryReserve(int vertexBytes, int indexBytes, out Reservation reservation) =>
        TryReserve(vertexBytes, indexBytes, isTransition: false, out reservation);

    private static long DivideRoundUp(long value, long divisor) =>
        value == 0 ? 0 : checked(1 + ((value - 1) / divisor));

    /// <summary>Marks a fully uploaded reservation as visible to draw submission.</summary>
    public bool TryPublish(Reservation reservation, out Allocation allocation)
    {
        allocation = default;
        if (!TryGetEntry(reservation, EntryState.Reserved, out var entry))
        {
            return false;
        }

        entry.State = EntryState.Live;
        allocation = new Allocation(
            reservation.Id,
            reservation.SegmentIndex,
            reservation.VertexOffsetBytes,
            reservation.VertexBytes,
            reservation.VertexReservedBytes,
            reservation.IndexOffsetBytes,
            reservation.IndexBytes,
            reservation.IndexReservedBytes,
            reservation.IsTransition);
        return true;
    }

    /// <summary>Immediately releases a reservation that has never been submitted to the GPU.</summary>
    public bool Cancel(Reservation reservation)
    {
        if (!TryGetEntry(reservation, EntryState.Reserved, out var entry))
        {
            return false;
        }

        Release(entry);
        return true;
    }

    /// <summary>
    /// Defers release of a partially uploaded reservation until its frame/fence token completes.
    /// </summary>
    public bool Retire(Reservation reservation, long completionToken)
    {
        if (!TryGetEntry(reservation, EntryState.Reserved, out var entry))
        {
            return false;
        }

        entry.State = EntryState.Retiring;
        entry.CompletionToken = completionToken;
        return true;
    }

    /// <summary>Defers release of a live allocation until its frame/fence token completes.</summary>
    public bool Retire(Allocation allocation, long completionToken)
    {
        if (!_entries.TryGetValue(allocation.Id, out var entry) ||
            entry.State != EntryState.Live ||
            !Matches(entry.Reservation, allocation))
        {
            return false;
        }

        entry.State = EntryState.Retiring;
        entry.CompletionToken = completionToken;
        return true;
    }

    /// <summary>
    /// Polls retirement tokens without waiting and reclaims all ranges whose token is complete.
    /// The completion callback is evaluated at most once for each token in this call.
    /// </summary>
    public int ReclaimCompleted(Func<long, bool> isComplete)
    {
        ArgumentNullException.ThrowIfNull(isComplete);
        Dictionary<long, bool> tokenResults = [];
        List<Entry> completed = [];
        foreach (var entry in _entries.Values)
        {
            if (entry.State != EntryState.Retiring)
            {
                continue;
            }

            if (!tokenResults.TryGetValue(entry.CompletionToken, out var done))
            {
                done = isComplete(entry.CompletionToken);
                tokenResults.Add(entry.CompletionToken, done);
            }

            if (done)
            {
                completed.Add(entry);
            }
        }

        foreach (var entry in completed)
        {
            Release(entry);
        }

        return completed.Count;
    }

    public bool IsReserved(Reservation reservation) =>
        TryGetEntry(reservation, EntryState.Reserved, out _);

    public Telemetry GetTelemetry()
    {
        long liveVertex = 0;
        long liveIndex = 0;
        long reservedVertex = 0;
        long reservedIndex = 0;
        long retiringVertex = 0;
        long retiringIndex = 0;
        var liveCount = 0;
        var reservedCount = 0;
        var retiringCount = 0;

        foreach (var entry in _entries.Values)
        {
            var vertexBytes = (long)entry.VertexPages * _vertexPageBytes;
            var indexBytes = (long)entry.IndexPages * _indexPageBytes;
            switch (entry.State)
            {
                case EntryState.Reserved:
                    reservedVertex += vertexBytes;
                    reservedIndex += indexBytes;
                    reservedCount++;
                    break;
                case EntryState.Live:
                    liveVertex += vertexBytes;
                    liveIndex += indexBytes;
                    liveCount++;
                    break;
                case EntryState.Retiring:
                    retiringVertex += vertexBytes;
                    retiringIndex += indexBytes;
                    retiringCount++;
                    break;
            }
        }

        var freeVertex = 0;
        var freeIndex = 0;
        var largestVertex = 0;
        var largestIndex = 0;
        foreach (var segment in _segments)
        {
            freeVertex = checked(freeVertex + segment.Vertices.FreePages * _vertexPageBytes);
            freeIndex = checked(freeIndex + segment.Indices.FreePages * _indexPageBytes);
            largestVertex = Math.Max(
                largestVertex,
                segment.Vertices.LargestFreePages * _vertexPageBytes);
            largestIndex = Math.Max(
                largestIndex,
                segment.Indices.LargestFreePages * _indexPageBytes);
        }

        return new Telemetry(
            VertexCapacityBytes,
            IndexCapacityBytes,
            freeVertex,
            freeIndex,
            largestVertex,
            largestIndex,
            Fragmentation(freeVertex, largestVertex),
            Fragmentation(freeIndex, largestIndex),
            liveVertex,
            liveIndex,
            reservedVertex,
            reservedIndex,
            retiringVertex,
            retiringIndex,
            liveCount,
            reservedCount,
            retiringCount);
    }

    private bool TryGetEntry(Reservation reservation, EntryState state, out Entry entry)
    {
        if (_entries.TryGetValue(reservation.Id, out var found) &&
            found.State == state &&
            found.Reservation == reservation)
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    private void Release(Entry entry)
    {
        var segment = _segments[entry.Reservation.SegmentIndex];
        segment.Vertices.Free(entry.VertexStartPage, entry.VertexPages);
        segment.Indices.Free(entry.IndexStartPage, entry.IndexPages);
        _entries.Remove(entry.Reservation.Id);
    }

    private static bool Matches(Reservation reservation, Allocation allocation) =>
        reservation.Id == allocation.Id &&
        reservation.SegmentIndex == allocation.SegmentIndex &&
        reservation.VertexOffsetBytes == allocation.VertexOffsetBytes &&
        reservation.VertexBytes == allocation.VertexBytes &&
        reservation.VertexReservedBytes == allocation.VertexReservedBytes &&
        reservation.IndexOffsetBytes == allocation.IndexOffsetBytes &&
        reservation.IndexBytes == allocation.IndexBytes &&
        reservation.IndexReservedBytes == allocation.IndexReservedBytes &&
        reservation.IsTransition == allocation.IsTransition;

    private static void ValidatePageLayout(int segmentBytes, int pageBytes, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentBytes, parameterName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageBytes);
        if (segmentBytes % pageBytes != 0)
        {
            throw new ArgumentException("Segment size must be a whole number of pages.", parameterName);
        }
    }

    private static int PagesFor(int bytes, int pageBytes) =>
        bytes == 0 ? 0 : checked((bytes + pageBytes - 1) / pageBytes);

    private static double Fragmentation(int freeBytes, int largestFreeBytes) =>
        freeBytes == 0 ? 0d : 1d - (double)largestFreeBytes / freeBytes;

    internal readonly record struct Reservation(
        long Id,
        int SegmentIndex,
        int VertexOffsetBytes,
        int VertexBytes,
        int VertexReservedBytes,
        int IndexOffsetBytes,
        int IndexBytes,
        int IndexReservedBytes,
        bool IsTransition);

    internal readonly record struct Allocation(
        long Id,
        int SegmentIndex,
        int VertexOffsetBytes,
        int VertexBytes,
        int VertexReservedBytes,
        int IndexOffsetBytes,
        int IndexBytes,
        int IndexReservedBytes,
        bool IsTransition);

    internal readonly record struct Telemetry(
        int VertexCapacityBytes,
        int IndexCapacityBytes,
        int FreeVertexBytes,
        int FreeIndexBytes,
        int LargestFreeVertexRangeBytes,
        int LargestFreeIndexRangeBytes,
        double VertexFragmentation,
        double IndexFragmentation,
        long LiveVertexBytes,
        long LiveIndexBytes,
        long ReservedVertexBytes,
        long ReservedIndexBytes,
        long RetiringVertexBytes,
        long RetiringIndexBytes,
        int LiveCount,
        int ReservedCount,
        int RetiringCount);

    private enum EntryState
    {
        Reserved,
        Live,
        Retiring,
    }

    private sealed class Entry(
        Reservation reservation,
        int vertexStartPage,
        int vertexPages,
        int indexStartPage,
        int indexPages,
        EntryState state)
    {
        public Reservation Reservation { get; } = reservation;
        public int VertexStartPage { get; } = vertexStartPage;
        public int VertexPages { get; } = vertexPages;
        public int IndexStartPage { get; } = indexStartPage;
        public int IndexPages { get; } = indexPages;
        public EntryState State { get; set; } = state;
        public long CompletionToken { get; set; }
    }

    private sealed class Segment(int vertexPages, int indexPages)
    {
        public PageAllocator Vertices { get; } = new(vertexPages);
        public PageAllocator Indices { get; } = new(indexPages);
    }

    private sealed class PageAllocator
    {
        private readonly List<PageRange> _free;

        public PageAllocator(int pages)
        {
            _free = [new PageRange(0, pages)];
            FreePages = pages;
            LargestFreePages = pages;
        }

        public int FreePages { get; private set; }
        public int LargestFreePages { get; private set; }

        public bool TryFind(int pages, out int start, out int blockPages)
        {
            var bestIndex = -1;
            var bestSize = int.MaxValue;
            for (var i = 0; i < _free.Count; i++)
            {
                var range = _free[i];
                if (range.Pages >= pages && range.Pages < bestSize)
                {
                    bestIndex = i;
                    bestSize = range.Pages;
                }
            }

            if (bestIndex < 0)
            {
                start = 0;
                blockPages = 0;
                return false;
            }

            start = _free[bestIndex].Start;
            blockPages = _free[bestIndex].Pages;
            return true;
        }

        public void Reserve(int start, int pages)
        {
            for (var i = 0; i < _free.Count; i++)
            {
                var range = _free[i];
                if (range.Start != start || range.Pages < pages)
                {
                    continue;
                }

                if (range.Pages == pages)
                {
                    _free.RemoveAt(i);
                }
                else
                {
                    _free[i] = new PageRange(start + pages, range.Pages - pages);
                }

                FreePages -= pages;
                RefreshLargest();
                return;
            }

            throw new InvalidOperationException("The selected page range is no longer free.");
        }

        public void Free(int start, int pages)
        {
            var insertAt = 0;
            while (insertAt < _free.Count && _free[insertAt].Start < start)
            {
                insertAt++;
            }

            _free.Insert(insertAt, new PageRange(start, pages));
            if (insertAt > 0)
            {
                var previous = _free[insertAt - 1];
                var current = _free[insertAt];
                if (previous.End == current.Start)
                {
                    _free[insertAt - 1] =
                        new PageRange(previous.Start, previous.Pages + current.Pages);
                    _free.RemoveAt(insertAt);
                    insertAt--;
                }
            }

            if (insertAt + 1 < _free.Count)
            {
                var current = _free[insertAt];
                var next = _free[insertAt + 1];
                if (current.End == next.Start)
                {
                    _free[insertAt] = new PageRange(current.Start, current.Pages + next.Pages);
                    _free.RemoveAt(insertAt + 1);
                }
            }

            FreePages += pages;
            RefreshLargest();
        }

        private void RefreshLargest()
        {
            var largest = 0;
            foreach (var range in _free)
            {
                largest = Math.Max(largest, range.Pages);
            }

            LargestFreePages = largest;
        }

        private readonly record struct PageRange(int Start, int Pages)
        {
            public int End => Start + Pages;
        }
    }
}
