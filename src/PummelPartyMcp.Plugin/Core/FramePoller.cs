using System;
using System.Collections.Generic;

namespace PummelPartyMcp.Core
{
    /// <summary>
    /// Per-frame callbacks for work that completes across frames (e.g. waiting for a mod load). Added and ticked on
    /// the main thread only. A callback returns true when it is finished; an exception also removes it.
    /// </summary>
    public sealed class FramePoller
    {
        private readonly List<Func<bool>> m_active = new List<Func<bool>>();
        private readonly IPluginLog m_log;

        public FramePoller(IPluginLog log)
        {
            m_log = log ?? NullPluginLog.Instance;
        }

        public int Count => m_active.Count;

        public void Add(Func<bool> tick)
        {
            m_active.Add(tick ?? throw new ArgumentNullException(nameof(tick)));
        }

        /// <summary>Drops every callback without running it (end of an editor session). Returns how many were dropped.</summary>
        public int Clear()
        {
            int n = m_active.Count;
            m_active.Clear();
            return n;
        }

        /// <summary>Runs every callback once. Never throws.</summary>
        public void Tick()
        {
            if (m_active.Count == 0)
            {
                return;
            }
            Func<bool>[] snapshot = m_active.ToArray(); // callbacks may Add
            foreach (Func<bool> tick in snapshot)
            {
                bool done;
                try
                {
                    done = tick();
                }
                catch (Exception e)
                {
                    m_log.Error($"frame poller callback threw {e.GetType().Name}: {e.Message}");
                    done = true;
                }
                if (done)
                {
                    m_active.Remove(tick);
                }
            }
        }
    }
}
