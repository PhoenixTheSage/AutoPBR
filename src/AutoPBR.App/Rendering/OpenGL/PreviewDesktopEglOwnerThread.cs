using System.Collections.Concurrent;
using System.Diagnostics;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Single background thread that owns the Linux EGL desktop OpenGL context.</summary>
internal static class PreviewDesktopEglOwnerThread
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static BlockingCollection<IWorkItem>? _queue;
    private static int _pendingCount;

    public static void PostDeferred(Action work, string phase = "posted")
    {
        EnsureStarted();
        Interlocked.Increment(ref _pendingCount);
        _queue!.Add(new PostedWorkItem(work, phase));
    }

    public static void Run(Action work, TimeSpan? timeout = null) =>
        Run(() =>
        {
            work();
            return true;
        }, timeout);

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
                throw new TimeoutException("Desktop EGL owner thread work timed out.");
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
                Name = "AutoPBR.EglOwner",
            };
            _thread.Start();
        }
    }

    private static void Loop()
    {
        foreach (var item in _queue!.GetConsumingEnumerable())
        {
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
                if (item.DecrementsPending)
                {
                    Interlocked.Decrement(ref _pendingCount);
                }

                item.Done.Set();
            }
        }
    }

    private interface IWorkItem
    {
        string Phase { get; }
        bool DecrementsPending { get; }
        Exception? Failure { get; set; }
        ManualResetEventSlim Done { get; }
        void Execute();
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<T> _work;
        public WorkItem(Func<T> work) => _work = work;
        public string Phase => "run";
        public bool DecrementsPending => false;
        public Exception? Failure { get; set; }
        public ManualResetEventSlim Done { get; } = new(false);
        public T? Result { get; private set; }
        public void Execute() => Result = _work();
    }

    private sealed class PostedWorkItem : IWorkItem
    {
        private readonly Action _work;
        public PostedWorkItem(Action work, string phase)
        {
            _work = work;
            Phase = phase;
        }

        public string Phase { get; }
        public bool DecrementsPending => true;
        public Exception? Failure { get; set; }
        public ManualResetEventSlim Done { get; } = new(true);
        public void Execute() => _work();
    }
}
