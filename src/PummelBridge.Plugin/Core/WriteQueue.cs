using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PummelBridge.Core
{
    /// <summary>
    /// Serialises write tools on the main thread: only one write tool body is active at a time; later ones wait in FIFO
    /// order and start (guard evaluated afresh) when the active one completes. A waiting entry whose caller gave up
    /// (dispatcher timeout) is dropped without running. Bodies that span frames must eventually complete their
    /// completion, also when cancelled; as a safety net an active entry whose caller gave up is force-released after
    /// <see cref="StuckGrace"/>. Main thread only (no locking); pure, so it is unit-tested.
    /// </summary>
    public sealed class WriteQueue
    {
        public static readonly TimeSpan StuckGrace = TimeSpan.FromSeconds(10);

        private readonly Queue<Entry> m_pending = new Queue<Entry>();
        private readonly Func<TimeSpan> m_clock;
        private readonly IBridgeLog m_log;
        private Entry m_active;
        private bool m_pumping;

        public WriteQueue(IBridgeLog log = null, Func<TimeSpan> clock = null)
        {
            m_log = log ?? NullBridgeLog.Instance;
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
                while (m_active == null && m_pending.Count > 0)
                {
                    Entry next = m_pending.Dequeue();
                    if (next.Inner.IsCancelled)
                    {
                        continue; // caller already got TIMEOUT; never run it
                    }
                    m_active = next;
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

            public Entry(WriteQueue queue, string name, IWorkCompletion inner, Action<IWorkCompletion> start)
            {
                m_queue = queue;
                Name = name;
                Inner = inner;
                Start = start;
            }

            public bool IsCancelled => Inner.IsCancelled;

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
