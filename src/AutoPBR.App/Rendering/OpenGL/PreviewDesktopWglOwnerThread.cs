using System.Collections.Concurrent;
using System.Diagnostics;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Single STA thread that owns desktop WGL contexts (WGL is thread-affine on Windows).</summary>
internal static class PreviewDesktopWglOwnerThread
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static BlockingCollection<IWorkItem>? _queue;
    private static int _pendingCount;
    private static long _lastItemStartedTicks;
    private static long _lastItemCompletedTicks;
    private static string _currentPhase = "idle";

    public static int PendingCount => Volatile.Read(ref _pendingCount);

    public static long LastItemStartedTicks => Volatile.Read(ref _lastItemStartedTicks);

    public static long LastItemCompletedTicks => Volatile.Read(ref _lastItemCompletedTicks);

    public static string CurrentPhase => Volatile.Read(ref _currentPhase) ?? "idle";

    public static void Run(Action work) => Run(() =>
    {
        work();
        return true;
    });

    public static void Run(Action work, TimeSpan? timeout) => Run(() =>
    {
        work();
        return true;
    }, timeout);

    /// <summary>Queues work on the WGL owner thread without blocking the caller.</summary>
    public static void Post(Action work) => Post(work, phase: "posted");

    public static void Post(Action work, string phase)
    {
        if (IsOwnerThread)
        {
            Volatile.Write(ref _currentPhase, phase);
            try
            {
                work();
            }
            finally
            {
                Volatile.Write(ref _currentPhase, "idle");
            }

            return;
        }

        PostDeferred(work, phase);
    }

    /// <summary>
    /// Always enqueues work for a later turn of the owner loop.
    /// Use this for follow-up frames — <see cref="Post"/> invokes inline when already on the owner
    /// thread, which would recurse forever if a frame posts the next frame.
    /// </summary>
    public static void PostDeferred(Action work, string phase = "posted")
    {
        EnsureStarted();
        Interlocked.Increment(ref _pendingCount);
        _queue!.Add(new PostedWorkItem(work, phase));
    }

    public static T Run<T>(Func<T> work, TimeSpan? timeout = null)
    {
        if (IsOwnerThread)
        {
            return work();
        }

        EnsureStarted();
        var item = new WorkItem<T>(work);
        _queue!.Add(item);
        if (timeout is { } limit)
        {
            if (!item.Done.Wait(limit))
            {
                throw new TimeoutException("Desktop WGL owner thread work timed out.");
            }
        }
        else
        {
            item.Done.Wait();
        }

        if (item.Failure is not null)
        {
            throw item.Failure;
        }

        return item.Result!;
    }

    public static bool IsOwnerThread => _thread is not null && Thread.CurrentThread == _thread;

    private static void EnsureStarted()
    {
        if (_thread is not null)
        {
            return;
        }

        lock (Gate)
        {
            if (_thread is not null)
            {
                return;
            }

            _queue = new BlockingCollection<IWorkItem>();
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "AutoPBR.WglOwner",
            };
            if (OperatingSystem.IsWindows())
            {
                _thread.SetApartmentState(ApartmentState.STA);
            }
            _thread.Start();
        }
    }

    private static void Loop()
    {
        foreach (var item in _queue!.GetConsumingEnumerable())
        {
            Interlocked.Exchange(ref _lastItemStartedTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _currentPhase, item.Phase);
            try
            {
                item.Execute();
            }
            catch (Exception ex)
            {
                item.Failure = ex;
            }
            finally
            {
                Interlocked.Exchange(ref _lastItemCompletedTicks, Stopwatch.GetTimestamp());
                Volatile.Write(ref _currentPhase, "idle");
                if (item.DecrementsPending)
                {
                    Interlocked.Decrement(ref _pendingCount);
                }

                item.Done?.Set();
            }
        }
    }

    private interface IWorkItem
    {
        Exception? Failure { get; set; }
        ManualResetEventSlim? Done { get; }
        string Phase { get; }
        bool DecrementsPending { get; }
        void Execute();
    }

    private sealed class PostedWorkItem : IWorkItem
    {
        private readonly Action _work;

        public PostedWorkItem(Action work, string phase)
        {
            _work = work;
            Phase = phase;
        }

        public Exception? Failure { get; set; }

        public ManualResetEventSlim? Done => null;

        public string Phase { get; }

        public bool DecrementsPending => true;

        public void Execute() => _work();
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<T> _work;

        public WorkItem(Func<T> work) => _work = work;

        public T? Result { get; private set; }

        public Exception? Failure { get; set; }

        public ManualResetEventSlim Done { get; } = new(false);

        ManualResetEventSlim? IWorkItem.Done => Done;

        public string Phase => "invoke";

        public bool DecrementsPending => false;

        public void Execute() => Result = _work();
    }
}
