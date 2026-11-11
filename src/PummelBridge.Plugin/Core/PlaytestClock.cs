using System;

namespace PummelBridge.Core
{
    /// <summary>
    /// Playtest timeline for get_status, fed once per frame with the editor state (main thread; pure, unit-tested).
    /// Records when the editor left Editing (playtest requested), when it reached PlayMode and when it returned to Editing.
    /// </summary>
    public sealed class PlaytestClock
    {
        private PlayState m_last = PlayState.Closed;

        /// <summary>UTC time the editor left Editing for EnteringPlayMode (null if no playtest seen yet / editor closed since).</summary>
        public DateTime? StartedAt { get; private set; }

        /// <summary>UTC time PlayMode was reached in the current/last playtest.</summary>
        public DateTime? PlayModeSince { get; private set; }

        /// <summary>UTC time the last playtest returned to Editing.</summary>
        public DateTime? EndedAt { get; private set; }

        /// <summary>The last playtest was started by start_playtest (set by the tool; cleared when a playtest is started by hand).</summary>
        public bool StartedByBridge { get; private set; }

        private bool m_bridgeStartPending;

        public PlayState LastState => m_last;

        /// <summary>start_playtest is about to enter play mode (the next Editing -> EnteringPlayMode transition is the bridge's).</summary>
        public void MarkBridgeStart() => m_bridgeStartPending = true;

        /// <summary>The bridge's start did not leave Editing (the editor refused/failed at once).</summary>
        public void CancelBridgeStart() => m_bridgeStartPending = false;

        public void Observe(PlayState state, DateTime nowUtc)
        {
            if (state == m_last)
            {
                return;
            }
            if (state == PlayState.Closed)
            {
                StartedAt = PlayModeSince = EndedAt = null;
                StartedByBridge = m_bridgeStartPending = false;
            }
            else if (state != PlayState.Editing && (m_last == PlayState.Editing || m_last == PlayState.Closed))
            {
                StartedAt = nowUtc;
                PlayModeSince = null;
                EndedAt = null;
                StartedByBridge = m_bridgeStartPending;
                m_bridgeStartPending = false;
            }
            if (state == PlayState.PlayMode && PlayModeSince == null)
            {
                PlayModeSince = nowUtc;
            }
            if (state == PlayState.Editing && m_last != PlayState.Closed && StartedAt != null)
            {
                EndedAt = nowUtc;
            }
            m_last = state;
        }

        /// <summary>Seconds since PlayMode was reached (until the end of that playtest); null when it was not reached.</summary>
        public double? PlaySeconds(DateTime nowUtc)
        {
            if (PlayModeSince == null)
            {
                return null;
            }
            DateTime end = EndedAt ?? nowUtc;
            double s = (end - PlayModeSince.Value).TotalSeconds;
            return s < 0 ? 0 : Math.Round(s, 1);
        }
    }
}
