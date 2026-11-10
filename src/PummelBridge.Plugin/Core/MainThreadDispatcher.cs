using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace PummelBridge.Core
{
    /// <summary>Handle given to main-thread work; may be completed later (e.g. from a coroutine).</summary>
    public interface IWorkCompletion
    {
        /// <summary>True once the waiting caller gave up (timeout/shutdown); late results are ignored.</summary>
        bool IsCancelled { get; }
        void SetResult(object result);
        void SetException(Exception error);
    }

    public interface IMainThreadDispatcher
    {
        /// <summary>
        /// Queues <paramref name="start"/> to run on the main thread and blocks the calling (non-main) thread
        /// until the work completes or <paramref name="timeout"/> elapses (then throws RpcException TIMEOUT).
        /// </summary>
        object Invoke(Action<IWorkCompletion> start, TimeSpan timeout);
    }

    public static class DispatcherExtensions
    {
        public static T Invoke<T>(this IMainThreadDispatcher dispatcher, Func<T> work, TimeSpan timeout)
        {
            return (T)dispatcher.Invoke(c => c.SetResult(work()), timeout);
        }
    }

    /// <summary>
    /// Thread-safe queue drained by the game loop (plugin Update()). Unity-free so it can be tested.
    /// </summary>
    public sealed class MainThreadDispatcher : IMainThreadDispatcher
    {
        private readonly ConcurrentQueue<WorkItem> m_queue = new ConcurrentQueue<WorkItem>();
        private volatile bool m_shutdown;

        public int PendingCount => m_queue.Count;

        public object Invoke(Action<IWorkCompletion> start, TimeSpan timeout)
        {
            if (start == null)
            {
                throw new ArgumentNullException(nameof(start));
            }
            if (m_shutdown)
            {
                throw RpcException.Internal("plugin is shutting down");
            }
            WorkItem item = new WorkItem(start);
            m_queue.Enqueue(item);
            if (!item.Done.Wait(timeout))
            {
                if (item.TryCancel())
                {
                    throw new RpcException(ErrorCodes.Timeout,
                        $"main thread did not service the call within {timeout.TotalSeconds:0.#} s");
                }
                // Completed in the race window; fall through and return its outcome.
                item.Done.Wait();
            }
            if (item.Error != null)
            {
                ExceptionDispatchInfo.Capture(item.Error).Throw();
            }
            return item.Result;
        }

        /// <summary>Runs queued work on the calling (main) thread. Never throws.</summary>
        public int Drain(int maxItems, double maxMilliseconds)
        {
            int processed = 0;
            Stopwatch sw = Stopwatch.StartNew();
            while (processed < maxItems && sw.Elapsed.TotalMilliseconds < maxMilliseconds && m_queue.TryDequeue(out WorkItem item))
            {
                if (!item.TryStart())
                {
                    continue; // cancelled while queued
                }
                processed++;
                try
                {
                    item.Start(item);
                }
                catch (Exception e)
                {
                    item.SetException(e);
                }
            }
            return processed;
        }

        /// <summary>Rejects new work and fails everything still queued.</summary>
        public void Shutdown()
        {
            m_shutdown = true;
            while (m_queue.TryDequeue(out WorkItem item))
            {
                item.FailAndRelease(RpcException.Internal("plugin is shutting down"));
            }
        }

        private sealed class WorkItem : IWorkCompletion
        {
            private const int Pending = 0, Running = 1, Completed = 2, Cancelled = 3;
            private int m_state;

            public readonly Action<IWorkCompletion> Start;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public object Result;
            public Exception Error;

            public WorkItem(Action<IWorkCompletion> start)
            {
                Start = start;
            }

            public bool IsCancelled => Volatile.Read(ref m_state) == Cancelled;

            public bool TryStart() => Interlocked.CompareExchange(ref m_state, Running, Pending) == Pending;

            public bool TryCancel()
            {
                if (Interlocked.CompareExchange(ref m_state, Cancelled, Pending) == Pending)
                {
                    return true;
                }
                return Interlocked.CompareExchange(ref m_state, Cancelled, Running) == Running;
            }

            public void SetResult(object result) => Complete(result, null);

            public void SetException(Exception error) => Complete(null, error ?? new Exception("unknown error"));

            public void FailAndRelease(Exception error)
            {
                if (Interlocked.CompareExchange(ref m_state, Completed, Pending) == Pending)
                {
                    Error = error;
                    Done.Set();
                }
            }

            private void Complete(object result, Exception error)
            {
                // Result/Error are written before the state flip; the waiter reads them only after Done is set.
                if (Volatile.Read(ref m_state) != Running)
                {
                    return;
                }
                Result = result;
                Error = error;
                if (Interlocked.CompareExchange(ref m_state, Completed, Running) == Running)
                {
                    Done.Set();
                }
            }
        }
    }
}
