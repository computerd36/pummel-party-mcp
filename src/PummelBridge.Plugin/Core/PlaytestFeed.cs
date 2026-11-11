using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace PummelBridge.Core
{
    /// <summary>One gameplay event of the current / last playtest (get_playtest_events).</summary>
    public sealed class PlaytestEvent
    {
        public long Id;
        public DateTime TimeUtc;
        /// <summary>Seconds since PlayMode was reached (2 decimals); 0 before.</summary>
        public double PlaySeconds;
        public string Type;
        /// <summary>Global player index the event is about; -1 = none.</summary>
        public int Player = -1;
        public string PlayerName;
        /// <summary>Type specific extra fields (flattened into the entry by the tool); may be null.</summary>
        public JObject Data;
    }

    public sealed class PlaytestEventQueryResult
    {
        public List<PlaytestEvent> Entries;
        public long NextId;
        public bool Dropped;
    }

    /// <summary>
    /// Thread-safe fixed-size ring of playtest events with monotonically increasing ids (first id = 1). Same paging
    /// contract as the log ring (<see cref="LogRingBuffer"/>), plus <see cref="Clear"/>: the ids are NOT reset by it, so a
    /// sinceId from before a new playtest still works (it reads <c>dropped: true</c> when it pointed at cleared entries).
    /// </summary>
    public sealed class PlaytestEventBuffer
    {
        private readonly PlaytestEvent[] m_buffer;
        private readonly object m_lock = new object();
        private long m_lastId;
        private int m_count;

        public PlaytestEventBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            m_buffer = new PlaytestEvent[capacity];
        }

        public int Capacity => m_buffer.Length;

        public int Count
        {
            get { lock (m_lock) { return m_count; } }
        }

        public long LastId
        {
            get { lock (m_lock) { return m_lastId; } }
        }

        public PlaytestEvent Add(PlaytestEvent e)
        {
            lock (m_lock)
            {
                e.Id = ++m_lastId;
                m_buffer[(int)((e.Id - 1) % m_buffer.Length)] = e;
                if (m_count < m_buffer.Length)
                {
                    m_count++;
                }
            }
            return e;
        }

        /// <summary>Forgets every entry; ids keep counting.</summary>
        public void Clear()
        {
            lock (m_lock)
            {
                Array.Clear(m_buffer, 0, m_buffer.Length);
                m_count = 0;
            }
        }

        /// <summary>
        /// Without sinceId: the newest <paramref name="max"/> entries. With sinceId: entries with id &gt; sinceId, oldest
        /// first, at most <paramref name="max"/>. NextId is the value to pass as sinceId next time.
        /// </summary>
        public PlaytestEventQueryResult Query(int max, long? sinceId)
        {
            List<PlaytestEvent> result = new List<PlaytestEvent>();
            lock (m_lock)
            {
                long oldestId = m_lastId - m_count + 1;
                long from;
                bool dropped = false;
                if (sinceId.HasValue)
                {
                    from = Math.Max(sinceId.Value + 1, oldestId);
                    dropped = sinceId.Value + 1 < oldestId;
                }
                else
                {
                    from = Math.Max(oldestId, m_lastId - max + 1);
                }
                for (long id = from; id <= m_lastId && result.Count < max; id++)
                {
                    result.Add(m_buffer[(int)((id - 1) % m_buffer.Length)]);
                }
                long nextId = result.Count > 0 ? result[result.Count - 1].Id : (sinceId.HasValue ? Math.Min(sinceId.Value, m_lastId) : m_lastId);
                return new PlaytestEventQueryResult { Entries = result, NextId = nextId, Dropped = dropped };
            }
        }
    }

    /// <summary>
    /// Per-player values sampled once per frame (filled by the game side from pure getters). A plain struct so that a frame
    /// in which nothing changed allocates nothing. <see cref="Health"/> -1 and <see cref="Aux"/> <see cref="NoAux"/> mean
    /// "not tracked for this player".
    /// </summary>
    public struct PlayerSnap
    {
        public const int NoAux = int.MinValue;

        public int Id;
        public string Name;
        public bool IsAi;
        /// <summary>Team id, -1 when not in team mode.</summary>
        public int Team;
        public bool Dead;
        public int Health;
        public int MaxHealth;
        public int Score;
        public int Aux;
        public bool Stunned;
        /// <summary>Global id of the player that last damaged this one, -1 none / unknown.</summary>
        public int Attacker;
    }

    /// <summary>Minigame controller sample (state is the game's MinigameControllerState value).</summary>
    public struct MinigameSnap
    {
        public bool Present;
        public int State;
        public int Round;
        public int Rounds;
    }

    /// <summary>The MinigameControllerState values the feed needs, supplied by the game side (Core has no game types).</summary>
    public sealed class MinigameStateIds
    {
        public int Playing;
        public int FinishedA;
        public int FinishedB;
    }

    /// <summary>The gate of get_playtest_state (pure, unit-tested): only a running playtest (PlayMode) has runtime state to read.</summary>
    public static class PlaytestGate
    {
        public static RpcException CheckRunning(PlayState state)
        {
            switch (state)
            {
                case PlayState.PlayMode:
                    return null;
                case PlayState.EnteringPlayMode:
                    return new RpcException(ErrorCodes.InvalidState,
                        "the playtest is still starting (editor state EnteringPlayMode): the minigame/board is not running yet; retry in a moment (get_status.playtest.state)");
                case PlayState.ExitingPlayMode:
                    return new RpcException(ErrorCodes.InvalidState,
                        "the playtest is ending (editor state ExitingPlayMode): its runtime state is gone; read what happened with get_playtest_events (kept until the next playtest starts)");
                case PlayState.Closed:
                    return new RpcException(ErrorCodes.InvalidState,
                        "no playtest is running: the Mod Editor is not open; get_playtest_events still returns the last playtest's events");
                default:
                    return new RpcException(ErrorCodes.InvalidState,
                        "no playtest is running (editor state Editing); start one with start_playtest (or the Play button). get_playtest_events still returns the last playtest's events");
            }
        }
    }

    /// <summary>
    /// The playtest event feed: turns per-frame snapshots into events (diffing: died / respawned / health / score / stunned /
    /// round and minigame transitions / players added or removed), stores them in a ring buffer cleared when a new playtest
    /// starts, and records rate-limited "trigger fired" / "action executed" notes from recording-only hooks.
    /// Main thread for the Observe* / Record* calls; the buffer itself is thread-safe (get_playtest_events reads it from the
    /// HTTP thread). Pure: no Unity or game types, so it is unit-tested.
    /// </summary>
    public sealed class PlaytestFeed
    {
        public const int DefaultCapacity = 500;

        /// <summary>At most one trigger/action record per key and this many seconds (the rest only counts as repeats).</summary>
        public const double LogicKeyIntervalSeconds = 1.0;
        /// <summary>Token bucket for all trigger/action records together, so mod logic cannot flush deaths out of the ring.</summary>
        public const double LogicBurst = 20;
        public const double LogicPerSecond = 10;

        private const int MaxPlayers = 16;
        private const int RecentActions = 8;

        private readonly PlaytestEventBuffer m_buffer;
        private readonly Func<int, string> m_stateName;
        private PlayState m_state = PlayState.Closed;
        private DateTime? m_playModeSince;
        private long m_playtestSeq;

        // diff state
        private readonly PlayerSnap[] m_prev = new PlayerSnap[MaxPlayers];
        private int m_prevCount;
        private bool m_havePrev;
        private int m_alive = -1;
        private readonly int[] m_deathIds = new int[MaxPlayers];
        private readonly int[] m_deathCounts = new int[MaxPlayers];
        private int m_deathSlots;
        private int m_prevMgState = -1;
        private int m_prevTurnPlayer = -1;

        // causes: the last few damaging / score actions (frame they ran in)
        private struct ActionNote
        {
            public int Frame;
            public int Kind;
            public string Text;
        }

        private readonly ActionNote[] m_recent = new ActionNote[RecentActions];
        private int m_recentNext;

        // logic record throttling
        private sealed class KeyState
        {
            public double LastSeconds = double.NegativeInfinity;
            public int Suppressed;
        }

        private readonly Dictionary<long, KeyState> m_keys = new Dictionary<long, KeyState>();
        private double m_bucket = LogicBurst;
        private double m_bucketSeconds;
        private long m_logicSuppressedTotal;

        public const int KindHealth = 1;
        public const int KindScore = 2;

        public PlaytestFeed(int capacity = DefaultCapacity, Func<int, string> stateName = null)
        {
            m_buffer = new PlaytestEventBuffer(capacity);
            m_stateName = stateName ?? (s => s.ToString());
        }

        public int Capacity => m_buffer.Capacity;
        public int EventCount => m_buffer.Count;
        public long LastEventId => m_buffer.LastId;

        /// <summary>Counts playtests seen since the plugin started (1 = the first).</summary>
        public long PlaytestSeq => m_playtestSeq;

        public PlayState State => m_state;

        /// <summary>True while the editor is in PlayMode: the only time per-frame sampling and hook recording run.</summary>
        public bool Collecting => m_state == PlayState.PlayMode;

        /// <summary>Players alive at the last observation; null before the first one of this playtest.</summary>
        public int? AlivePlayers => m_alive < 0 ? (int?)null : m_alive;

        /// <summary>Trigger/action records skipped by the rate limits since the plugin started.</summary>
        public long LogicSuppressed => m_logicSuppressedTotal;

        public PlaytestEventQueryResult Query(int max, long? sinceId) => m_buffer.Query(max, sinceId);

        /// <summary>Deaths the bridge counted for this player in the current / last playtest (the game keeps no per-player death count).</summary>
        public int Deaths(int playerId)
        {
            for (int i = 0; i < m_deathSlots; i++)
            {
                if (m_deathIds[i] == playerId)
                {
                    return m_deathCounts[i];
                }
            }
            return 0;
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>
        /// Feeds the editor state once per frame. A transition from Editing/Closed to any play state starts a new playtest and
        /// clears the buffer (also for a playtest the human started by hand); reaching PlayMode and leaving it are recorded.
        /// </summary>
        public void OnState(PlayState state, DateTime nowUtc)
        {
            if (state == m_state)
            {
                return;
            }
            PlayState prev = m_state;
            bool starting = state != PlayState.Editing && state != PlayState.Closed && (prev == PlayState.Editing || prev == PlayState.Closed);
            if (starting)
            {
                BeginPlaytest();
            }
            m_state = state;
            if (state == PlayState.PlayMode && m_playModeSince == null)
            {
                m_playModeSince = nowUtc;
                Add("playtest_started", nowUtc, -1, null, null);
            }
            else if (prev == PlayState.PlayMode && state != PlayState.PlayMode)
            {
                Add("playtest_ended", nowUtc, -1, null, new JObject { ["playSeconds"] = PlaySeconds(nowUtc) });
            }
        }

        private void BeginPlaytest()
        {
            m_buffer.Clear();
            m_playtestSeq++;
            m_playModeSince = null;
            m_havePrev = false;
            m_prevCount = 0;
            m_alive = -1;
            m_deathSlots = 0;
            m_prevMgState = -1;
            m_prevTurnPlayer = -1;
            m_recentNext = 0;
            Array.Clear(m_recent, 0, m_recent.Length);
            m_keys.Clear();
            m_bucket = LogicBurst;
            m_bucketSeconds = 0;
        }

        private double PlaySeconds(DateTime nowUtc)
        {
            if (m_playModeSince == null)
            {
                return 0;
            }
            double s = (nowUtc - m_playModeSince.Value).TotalSeconds;
            return s < 0 ? 0 : Math.Round(s, 2);
        }

        private PlaytestEvent Add(string type, DateTime nowUtc, int player, string playerName, JObject data)
        {
            return m_buffer.Add(new PlaytestEvent
            {
                TimeUtc = nowUtc,
                PlaySeconds = PlaySeconds(nowUtc),
                Type = type,
                Player = player,
                PlayerName = playerName,
                Data = data,
            });
        }

        // ------------------------------------------------------------------ per-frame diffing

        /// <summary>
        /// Compares this frame's player samples with the previous ones. Allocation-free while nothing changes.
        /// <paramref name="scoreEvent"/> / <paramref name="auxEvent"/> name the event types for the Score / Aux channels
        /// ("score_changed"; board: "keys_changed" / "trophies_changed"); a null aux event ignores Aux.
        /// </summary>
        public void ObservePlayers(PlayerSnap[] cur, int count, int frame, DateTime nowUtc, string scoreEvent, string auxEvent)
        {
            if (!Collecting)
            {
                return;
            }
            if (count > MaxPlayers)
            {
                count = MaxPlayers;
            }
            int alive = 0;
            for (int i = 0; i < count; i++)
            {
                if (!cur[i].Dead)
                {
                    alive++;
                }
                int p = FindPrev(cur[i].Id, i);
                if (p < 0)
                {
                    EmitAdded(ref cur[i], nowUtc);
                    continue;
                }
                Compare(ref m_prev[p], ref cur[i], frame, nowUtc, scoreEvent, auxEvent);
            }
            if (m_havePrev)
            {
                for (int p = 0; p < m_prevCount; p++)
                {
                    if (!Contains(cur, count, m_prev[p].Id))
                    {
                        Add("player_removed", nowUtc, m_prev[p].Id, m_prev[p].Name, null);
                    }
                }
            }
            Array.Copy(cur, m_prev, count);
            m_prevCount = count;
            m_havePrev = true;
            m_alive = alive;
        }

        private int FindPrev(int id, int hint)
        {
            if (hint < m_prevCount && m_prev[hint].Id == id)
            {
                return hint;
            }
            for (int p = 0; p < m_prevCount; p++)
            {
                if (m_prev[p].Id == id)
                {
                    return p;
                }
            }
            return -1;
        }

        private static bool Contains(PlayerSnap[] cur, int count, int id)
        {
            for (int i = 0; i < count; i++)
            {
                if (cur[i].Id == id)
                {
                    return true;
                }
            }
            return false;
        }

        private void EmitAdded(ref PlayerSnap c, DateTime now)
        {
            JObject d = new JObject { ["isAI"] = c.IsAi };
            if (c.Team >= 0)
            {
                d["team"] = c.Team;
            }
            if (c.Health >= 0)
            {
                d["health"] = c.Health;
            }
            Add("player_added", now, c.Id, c.Name, d);
        }

        private void Compare(ref PlayerSnap p, ref PlayerSnap c, int frame, DateTime now, string scoreEvent, string auxEvent)
        {
            bool respawned = p.Dead && !c.Dead;
            if (c.Health >= 0 && p.Health >= 0 && c.Health != p.Health && !respawned)
            {
                JObject d = new JObject { ["old"] = p.Health, ["new"] = c.Health, ["delta"] = c.Health - p.Health };
                if (c.MaxHealth > 0)
                {
                    d["max"] = c.MaxHealth;
                }
                Cause(d, KindHealth, frame, c.Attacker >= 0 ? c.Attacker : p.Attacker, c.Health < p.Health);
                Add("health_changed", now, c.Id, c.Name, d);
            }
            if (!p.Dead && c.Dead)
            {
                JObject d = new JObject();
                if (c.Health >= 0)
                {
                    d["health"] = c.Health;
                }
                Cause(d, KindHealth, frame, c.Attacker >= 0 ? c.Attacker : p.Attacker, true);
                NoteDeath(c.Id);
                d["deaths"] = Deaths(c.Id);
                Add("player_died", now, c.Id, c.Name, d);
            }
            else if (respawned)
            {
                JObject d = new JObject();
                if (c.Health >= 0)
                {
                    d["health"] = c.Health;
                }
                Add("player_respawned", now, c.Id, c.Name, d);
            }
            if (c.Score != p.Score && scoreEvent != null)
            {
                JObject d = new JObject { ["old"] = p.Score, ["new"] = c.Score, ["delta"] = c.Score - p.Score };
                Cause(d, KindScore, frame, -1, false);
                Add(scoreEvent, now, c.Id, c.Name, d);
            }
            if (auxEvent != null && c.Aux != p.Aux && c.Aux != PlayerSnap.NoAux && p.Aux != PlayerSnap.NoAux)
            {
                Add(auxEvent, now, c.Id, c.Name, new JObject { ["old"] = p.Aux, ["new"] = c.Aux, ["delta"] = c.Aux - p.Aux });
            }
            if (!p.Stunned && c.Stunned)
            {
                Add("player_stunned", now, c.Id, c.Name, null);
            }
        }

        private void NoteDeath(int id)
        {
            for (int i = 0; i < m_deathSlots; i++)
            {
                if (m_deathIds[i] == id)
                {
                    m_deathCounts[i]++;
                    return;
                }
            }
            if (m_deathSlots < MaxPlayers)
            {
                m_deathIds[m_deathSlots] = id;
                m_deathCounts[m_deathSlots] = 1;
                m_deathSlots++;
            }
        }

        private void Cause(JObject d, int kind, int frame, int attacker, bool damaging)
        {
            if (damaging && attacker >= 0)
            {
                d["by"] = attacker;
            }
            string text = null;
            int n = 0;
            for (int i = 0; i < RecentActions && n < 3; i++)
            {
                ActionNote a = m_recent[i];
                if (a.Text != null && (a.Kind & kind) != 0 && frame - a.Frame >= 0 && frame - a.Frame <= 1)
                {
                    text = text == null ? a.Text : text + "; " + a.Text;
                    n++;
                }
            }
            if (text != null)
            {
                d["cause"] = text;
            }
        }

        /// <summary>A health- or score-affecting action ran in this frame (kind: KindHealth / KindScore flags); kept for ~2 frames as a cause hint.</summary>
        public void NoteCauseAction(int frame, int kind, string text)
        {
            m_recent[m_recentNext] = new ActionNote { Frame = frame, Kind = kind, Text = text };
            m_recentNext = (m_recentNext + 1) % RecentActions;
        }

        /// <summary>True when <see cref="ObserveMinigame"/> would see a new controller state (lets the caller read the round counters only then).</summary>
        public bool MinigameStateChanged(int state) => state != m_prevMgState;

        /// <summary>Minigame transitions: round started / ended, minigame finished (with <paramref name="results"/>, evaluated lazily).</summary>
        public void ObserveMinigame(MinigameSnap s, MinigameStateIds ids, DateTime nowUtc, Func<JObject> results)
        {
            if (!Collecting || !s.Present || ids == null)
            {
                return;
            }
            int prev = m_prevMgState;
            if (s.State == prev)
            {
                return;
            }
            m_prevMgState = s.State;
            bool finished = s.State == ids.FinishedA || s.State == ids.FinishedB;
            bool wasFinished = prev == ids.FinishedA || prev == ids.FinishedB;
            if (s.State == ids.Playing)
            {
                Add("round_started", nowUtc, -1, null, new JObject { ["round"] = s.Round, ["rounds"] = s.Rounds });
            }
            else if (prev == ids.Playing)
            {
                Add("round_ended", nowUtc, -1, null, new JObject { ["round"] = s.Round, ["rounds"] = s.Rounds, ["state"] = m_stateName(s.State) });
            }
            if (finished && !wasFinished)
            {
                JObject d = results != null ? results() : null;
                if (d == null)
                {
                    d = new JObject();
                }
                d["rounds"] = s.Rounds;
                Add("minigame_finished", nowUtc, -1, null, d);
            }
        }

        /// <summary>Board: whose turn it is (-1 = nobody); a change records turn_started.</summary>
        public void ObserveTurn(int playerId, string playerName, int turn, DateTime nowUtc)
        {
            if (!Collecting || playerId == m_prevTurnPlayer)
            {
                return;
            }
            m_prevTurnPlayer = playerId;
            if (playerId >= 0)
            {
                Add("turn_started", nowUtc, playerId, playerName, new JObject { ["turn"] = turn });
            }
        }

        // ------------------------------------------------------------------ mod logic records (from the recording hooks)

        /// <summary>
        /// Rate limit for trigger/action records: false = skip this one (it is counted and reported as <c>repeats</c> on the
        /// next record of the same key, or as suppressed by the token bucket). Build the record's data only when true.
        /// </summary>
        public bool TryBeginLogic(long key, DateTime nowUtc, out int repeats)
        {
            repeats = 0;
            if (!Collecting)
            {
                return false;
            }
            double now = PlaySeconds(nowUtc);
            if (!m_keys.TryGetValue(key, out KeyState ks))
            {
                if (m_keys.Count > 4096)
                {
                    m_keys.Clear();
                }
                ks = new KeyState();
                m_keys[key] = ks;
            }
            if (now - ks.LastSeconds < LogicKeyIntervalSeconds)
            {
                ks.Suppressed++;
                m_logicSuppressedTotal++;
                return false;
            }
            m_bucket = Math.Min(LogicBurst, m_bucket + Math.Max(0, now - m_bucketSeconds) * LogicPerSecond);
            m_bucketSeconds = now;
            if (m_bucket < 1)
            {
                ks.Suppressed++;
                m_logicSuppressedTotal++;
                return false;
            }
            m_bucket -= 1;
            ks.LastSeconds = now;
            repeats = ks.Suppressed;
            ks.Suppressed = 0;
            return true;
        }

        /// <summary>Stores a record that passed <see cref="TryBeginLogic"/>.</summary>
        public void RecordLogic(string type, DateTime nowUtc, int player, string playerName, JObject data, int repeats)
        {
            if (!Collecting)
            {
                return;
            }
            if (repeats > 0)
            {
                data = data ?? new JObject();
                data["repeats"] = repeats;
            }
            Add(type, nowUtc, player, playerName, data);
        }
    }
}
