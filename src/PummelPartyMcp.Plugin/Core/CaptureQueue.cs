using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PummelPartyMcp.Core
{
    /// <summary>
    /// Serialises screenshot captures (main thread only, no locking; pure, so it is unit-tested). A capture that moves the editor
    /// camera waits two frames before it reads the screen; without this queue a second call could move the camera in between and
    /// the first call would capture the second call's view while reporting its own camera.
    /// <para>
    /// FIFO: <see cref="Enqueue"/> adds an entry and pumps. The pump skips entries whose caller already gave up, and starts the
    /// head entry only when no capture holds the slot. <c>start</c> returns true when it started a capture that holds the slot
    /// until <see cref="Release"/> is called for that entry (normally from the capture coroutine's finally); false when it
    /// finished synchronously (refused, cancelled at its commit point, or completed). A <c>start</c> that throws fails only its own
    /// entry. Liveness: a slot held longer than <c>slotTimeout</c> and for at least <see cref="MinFramesBeforeReclaim"/> frames
    /// (coroutine host destroyed, coroutine stopped) is taken back on the next pump; that entry is failed with TIMEOUT and a late
    /// <see cref="Release"/> of it is ignored.
    /// </para>
    /// </summary>
    public sealed class CaptureQueue
    {
        public static readonly TimeSpan DefaultSlotTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// A slot is taken back only after the timeout AND this many <see cref="FrameTick"/> calls since the capture started: one very
        /// long frame (a stall over the timeout) must not reclaim a capture that finishes in that same frame.
        /// </summary>
        public const int MinFramesBeforeReclaim = 10;

        public sealed class Entry
        {
            public readonly IWorkCompletion Completion;
            public readonly object State;

            public Entry(IWorkCompletion completion, object state)
            {
                Completion = completion ?? throw new ArgumentNullException(nameof(completion));
                State = state;
            }
        }

        private readonly Queue<Entry> m_waiting = new Queue<Entry>();
        private readonly Func<Entry, bool> m_start;
        private readonly Func<TimeSpan> m_clock;
        private readonly TimeSpan m_slotTimeout;
        private readonly IPluginLog m_log;
        private Entry m_active;
        private TimeSpan m_activeSince;
        private long m_frames;
        private long m_activeSinceFrame;
        private bool m_pumping;

        public CaptureQueue(Func<Entry, bool> start, IPluginLog log = null, TimeSpan? slotTimeout = null, Func<TimeSpan> clock = null)
        {
            m_start = start ?? throw new ArgumentNullException(nameof(start));
            m_log = log ?? NullPluginLog.Instance;
            m_slotTimeout = slotTimeout ?? DefaultSlotTimeout;
            if (clock == null)
            {
                Stopwatch sw = Stopwatch.StartNew();
                clock = () => sw.Elapsed;
            }
            m_clock = clock;
        }

        /// <summary>No capture holds the slot and none is waiting.</summary>
        public bool Idle => m_active == null && m_waiting.Count == 0;

        public bool Busy => m_active != null;

        public int WaitingCount => m_waiting.Count;

        public void Enqueue(Entry entry)
        {
            m_waiting.Enqueue(entry ?? throw new ArgumentNullException(nameof(entry)));
            Pump();
        }

        /// <summary>Frees the slot if <paramref name="entry"/> holds it (a stale release is ignored), then pumps. Never throws.</summary>
        public void Release(Entry entry)
        {
            if (entry != null && ReferenceEquals(m_active, entry))
            {
                m_active = null;
            }
            Pump();
        }

        /// <summary>Once per rendered frame while captures are queued or running (the frame watchdog): counts the frame and pumps. Never throws.</summary>
        public void FrameTick()
        {
            m_frames++;
            Pump();
        }

        /// <summary>Starts waiting captures while the slot is free; takes back a slot held too long. Never throws.</summary>
        public void Pump()
        {
            if (m_pumping)
            {
                return; // a start/release from inside start: the running pump continues
            }
            m_pumping = true;
            try
            {
                while (true)
                {
                    if (m_active != null)
                    {
                        if (m_clock() - m_activeSince < m_slotTimeout || m_frames - m_activeSinceFrame < MinFramesBeforeReclaim)
                        {
                            return;
                        }
                        Entry stuck = m_active;
                        m_active = null;
                        m_log.Warning($"screenshot: a capture did not finish within {m_slotTimeout.TotalSeconds:0.#} s (frames stalled or the coroutine stopped); releasing the capture slot");
                        Fail(stuck, new RpcException(ErrorCodes.Timeout,
                            $"the capture did not finish within {m_slotTimeout.TotalSeconds:0.#} s (the game's frames stalled?); call screenshot again"));
                    }
                    if (m_waiting.Count == 0)
                    {
                        return;
                    }
                    Entry next = m_waiting.Dequeue();
                    if (next.Completion.IsCancelled)
                    {
                        continue; // the caller already got TIMEOUT; never move the camera for it
                    }
                    m_active = next;
                    m_activeSince = m_clock();
                    m_activeSinceFrame = m_frames;
                    bool holds;
                    try
                    {
                        holds = m_start(next);
                    }
                    catch (Exception e)
                    {
                        Fail(next, e);
                        holds = false;
                    }
                    if (ReferenceEquals(m_active, next))
                    {
                        if (holds)
                        {
                            return; // the capture runs; its Release pumps again
                        }
                        m_active = null;
                    }
                }
            }
            finally
            {
                m_pumping = false;
            }
        }

        private static void Fail(Entry e, Exception error)
        {
            try
            {
                e.Completion.SetException(error); // ignored when it already completed
            }
            catch (Exception)
            {
                // never let one entry stop the queue
            }
        }
    }
}
