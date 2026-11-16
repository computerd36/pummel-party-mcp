using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PummelPartyMcp.Core
{
    /// <summary>
    /// Serialises write tools on the main thread: only one write tool body is active at a time; later ones wait in FIFO
    /// order and start (guard evaluated afresh) when the active one completes. A waiting entry whose caller gave up
    /// (dispatcher timeout) is dropped without running, and one with less than <see cref="MinStartBudget"/> left fails
    /// with TIMEOUT without running. Bodies that span frames must eventually complete their
    /// completion, also when cancelled; as a safety net an active entry whose caller gave up is force-released after
    /// <see cref="StuckGrace"/>. Main thread only (no locking); pure, so it is unit-tested.
    /// </summary>
    public sealed class WriteQueue
    {
        public static readonly TimeSpan StuckGrace = TimeSpan.FromSeconds(10);

        /// <summary>
        /// A queued call with less time than this left until its deadline is failed with TIMEOUT instead of being started
        /// (it never runs). Bodies that need more check their own budget at their commit point
        /// (<see cref="WriteInvocation.Commit"/>, <see cref="EditPipeline"/>).
        /// </summary>
        public static readonly TimeSpan MinStartBudget = TimeSpan.FromSeconds(1);

        private readonly Queue<Entry> m_pending = new Queue<Entry>();
        private readonly Func<TimeSpan> m_clock;
        private readonly IPluginLog m_log;
        private Entry m_active;
        private bool m_pumping;

        public WriteQueue(IPluginLog log = null, Func<TimeSpan> clock = null)
        {
            m_log = log ?? NullPluginLog.Instance;
            if (clock == null)
            {
                Stopwatch sw = Stopwatch.StartNew();
                clock = () => sw.Elapsed;
            }
            m_clock = clock;
        }

        public bool IsBusy => m_active != null;

        public int PendingCount => m_pending.Count;

        /// <summary>Name of the active write tool (diagnostics), or null.</summary>
        public string ActiveName => m_active?.Name;

        /// <summary>How long the active write tool has been running (diagnostics, get_status activeWrite), or null.</summary>
        public TimeSpan? ActiveAge => m_active == null ? (TimeSpan?)null : m_clock() - m_active.StartedAt;

        /// <summary>
        /// An active entry whose caller's deadline passed longer ago than this has outlived the dispatcher's commit grace and the
        /// stuck grace: one error is logged (visibility only; a committed body is never force-released).
        /// </summary>
        public static TimeSpan OverdueAfterDeadline => MainThreadDispatcher.DefaultCommitGrace + StuckGrace;

        /// <summary>
        /// Queues a write body. <paramref name="start"/> receives a completion that forwards to <paramref name="completion"/>
        /// and releases the queue when completed. Starts immediately when idle.
        /// </summary>
        public void Enqueue(string name, IWorkCompletion completion, Action<IWorkCompletion> start)
        {
            if (completion == null)
            {
                throw new ArgumentNullException(nameof(completion));
            }
            if (start == null)
            {
                throw new ArgumentNullException(nameof(start));
            }
            m_pending.Enqueue(new Entry(this, name, completion, start));
            Pump();
        }

        /// <summary>Starts the next entry if idle; releases a stuck active entry. Call once per frame as well.</summary>
        public void Pump()
        {
            if (m_pumping)
            {
                return;
            }
            m_pumping = true;
            try
            {
                if (m_active != null && m_active.Inner.IsCancelled && m_clock() - m_active.CancelSeenAt(m_clock()) >= StuckGrace)
                {
                    m_log.Warning($"write tool '{m_active.Name}' did not finish after its caller timed out; releasing the write queue");
                    m_active.Release();
                }
                if (m_active != null && !m_active.OverdueLogged && m_active.Inner.Remaining < -OverdueAfterDeadline)
                {
                    m_active.OverdueLogged = true;
                    m_log.Error($"write tool '{m_active.Name}' is still running {(m_clock() - m_active.StartedAt).TotalSeconds:0} s after it started, " +
                                $"more than {OverdueAfterDeadline.TotalSeconds:0} s past its caller's deadline; later write calls wait behind it (get_status activeWrite)");
                }
                while (m_active == null && m_pending.Count > 0)
                {
                    Entry next = m_pending.Dequeue();
                    if (next.Inner.IsCancelled)
                    {
                        continue; // caller already got TIMEOUT; never run it
                    }
                    TimeSpan left = next.Inner.Remaining;
                    if (left < MinStartBudget)
                    {
                        // Waited too long behind other writes: fail before the guard or body touches anything.
                        next.Inner.SetException(new RpcException(ErrorCodes.Timeout,
                            $"the call waited in the write queue behind other write calls and could not start within its time limit ({Math.Max(0, left.TotalSeconds):0.#} s left); " +
                            "it was not started and nothing was changed (safe to retry)"));
                        continue;
                    }
                    m_active = next;
                    next.StartedAt = m_clock();
                    try
                    {
                        next.Start(next);
                    }
                    catch (Exception e)
                    {
                        next.SetException(e);
                    }
                }
            }
            finally
            {
                m_pumping = false;
            }
        }

        private void OnEntryDone(Entry e)
        {
            if (ReferenceEquals(m_active, e))
            {
                m_active = null;
                Pump();
            }
        }

        private sealed class Entry : IWorkCompletion
        {
            private readonly WriteQueue m_queue;
            private bool m_done;
            private TimeSpan? m_cancelSeenAt;

            public readonly string Name;
            public readonly IWorkCompletion Inner;
            public readonly Action<IWorkCompletion> Start;
            public TimeSpan StartedAt;
            public bool OverdueLogged;

            public Entry(WriteQueue queue, string name, IWorkCompletion inner, Action<IWorkCompletion> start)
            {
                m_queue = queue;
                Name = name;
                Inner = inner;
                Start = start;
            }

            public bool IsCancelled => Inner.IsCancelled;

            public bool TryCommit() => !m_done && Inner.TryCommit();

            public TimeSpan Remaining => Inner.Remaining;

            public TimeSpan CancelSeenAt(TimeSpan now)
            {
                if (!m_cancelSeenAt.HasValue)
                {
                    m_cancelSeenAt = now;
                }
                return m_cancelSeenAt.Value;
            }

            public void SetResult(object result)
            {
                if (m_done)
                {
                    return;
                }
                m_done = true;
                Inner.SetResult(result); // ignored by the inner completion if the caller gave up
                m_queue.OnEntryDone(this);
            }

            public void SetException(Exception error)
            {
                if (m_done)
                {
                    return;
                }
                m_done = true;
                Inner.SetException(error);
                m_queue.OnEntryDone(this);
            }

            public void Release()
            {
                m_done = true;
                m_queue.m_active = null;
            }
        }
    }
}
