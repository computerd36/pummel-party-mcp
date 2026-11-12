using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace PummelPartyMcp.Core
{
    /// <summary>Handle given to main-thread work; may be completed later (e.g. from a coroutine).</summary>
    public interface IWorkCompletion
    {
        /// <summary>True once the waiting caller gave up (timeout/shutdown); late results are ignored. Never true after a successful <see cref="TryCommit"/>.</summary>
        bool IsCancelled { get; }

        /// <summary>
        /// Commit point: call right before the work's first change to the editor or the mod. Returns false when the caller
        /// already gave up; the work must then change nothing (and complete, so a write queue moves on). After it returned
        /// true the caller can no longer cancel: it waits for the work's own result (bounded by a grace period), so the
        /// work must complete promptly and report what it did.
        /// </summary>
        bool TryCommit();

        /// <summary>Time left until the caller's deadline (may be negative); <see cref="TimeSpan.MaxValue"/> when unknown.</summary>
        TimeSpan Remaining { get; }

        void SetResult(object result);
        void SetException(Exception error);
    }

    public interface IMainThreadDispatcher
    {
        /// <summary>
        /// Queues <paramref name="start"/> to run on the main thread and blocks the calling (non-main) thread until the
        /// work completes. When <paramref name="timeout"/> elapses before the work committed (<see cref="IWorkCompletion.TryCommit"/>)
        /// the work is cancelled and RpcException TIMEOUT is thrown (it then never changes anything); committed work is
        /// waited for (bounded by a grace period) and its own result returned.
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
        /// <summary>
        /// How long a caller whose deadline passed keeps waiting for work that already committed. Committed work normally
        /// finishes before the deadline (write tools only commit with enough time left); this only covers frame stalls.
        /// Edit tools: 15 s + 4 s stays below the MCP server's 20 s request timeout.
        /// </summary>
        public static readonly TimeSpan DefaultCommitGrace = TimeSpan.FromSeconds(4);

        private readonly ConcurrentQueue<WorkItem> m_queue = new ConcurrentQueue<WorkItem>();
        private readonly TimeSpan m_commitGrace;
        private readonly Func<TimeSpan> m_clock;
        private volatile bool m_shutdown;

        public MainThreadDispatcher(TimeSpan? commitGrace = null)
        {
            m_commitGrace = commitGrace ?? DefaultCommitGrace;
            Stopwatch sw = Stopwatch.StartNew();
            m_clock = () => sw.Elapsed;
        }

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
            WorkItem item = new WorkItem(start, m_clock, m_clock() + timeout);
            m_queue.Enqueue(item);
            if (!item.Done.Wait(timeout))
            {
                if (item.TryCancel())
                {
                    throw new RpcException(ErrorCodes.Timeout,
                        $"the call did not complete within {timeout.TotalSeconds:0.#} s and was cancelled before it changed anything (nothing was applied; safe to retry)");
                }
                // Committed (or completed in the race window): the work runs to its own result.
                if (!item.Done.Wait(m_commitGrace))
                {
                    throw new RpcException(ErrorCodes.Timeout,
                        $"the change was applied, but the call did not finish within {(timeout + m_commitGrace).TotalSeconds:0.#} s (the game's main thread is stalled?); " +
                        "check get_status / get_object before retrying");
                }
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
            private const int Pending = 0, Running = 1, Completed = 2, Cancelled = 3, Committed = 4;
            private int m_state;
            private readonly Func<TimeSpan> m_clock;
            private readonly TimeSpan m_deadline;

            public readonly Action<IWorkCompletion> Start;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public object Result;
            public Exception Error;

            public WorkItem(Action<IWorkCompletion> start, Func<TimeSpan> clock, TimeSpan deadline)
            {
                Start = start;
                m_clock = clock;
                m_deadline = deadline;
            }

            public bool IsCancelled => Volatile.Read(ref m_state) == Cancelled;

            public TimeSpan Remaining => m_deadline - m_clock();

            public bool TryStart() => Interlocked.CompareExchange(ref m_state, Running, Pending) == Pending;

            public bool TryCommit()
            {
                int s = Interlocked.CompareExchange(ref m_state, Committed, Running);
                return s == Running || s == Committed;
            }

            /// <summary>Caller side: succeeds only while the work has not committed (or completed).</summary>
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
                int s = Volatile.Read(ref m_state);
                if (s != Running && s != Committed)
                {
                    return;
                }
                Result = result;
                Error = error;
                if (Interlocked.CompareExchange(ref m_state, Completed, s) == s)
                {
                    Done.Set();
                }
            }
        }
    }
}
