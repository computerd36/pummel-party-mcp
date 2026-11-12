using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>Playtest visibility: snapshot diffing to events, the ring buffer (paging, dropped, clear on a new playtest), the state gate, the wire shape.</summary>
    internal static class PlaytestFeedTests
    {
        private static Action<string, bool> s_check;
        private static readonly DateTime T0 = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        private static readonly MinigameStateIds Ids = new MinigameStateIds { Playing = 7, FinishedA = 8, FinishedB = 9 };

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            Console.WriteLine("-- playtest feed: buffer, gate, diffing, throttle");
            BufferTests();
            GateTests();
            LifecycleTests();
            DiffTests();
            NoChangeTests();
            MinigameTests();
            BoardTests();
            CauseTests();
            LogicThrottleTests();
            JsonTests();
        }

        private static void Check(string name, bool ok) => s_check(name, ok);

        private static DateTime At(double seconds) => T0.AddSeconds(seconds);

        /// <summary>A feed that has reached PlayMode at T0 (the previous playtest's state is irrelevant).</summary>
        private static PlaytestFeed Playing(int capacity = 500)
        {
            PlaytestFeed f = new PlaytestFeed(capacity, s => "S" + s);
            f.OnState(PlayState.Editing, T0);
            f.OnState(PlayState.EnteringPlayMode, T0);
            f.OnState(PlayState.PlayMode, T0);
            return f;
        }

        private static PlayerSnap P(int id, int health = -1, int score = 0, bool dead = false, bool stunned = false, int attacker = -1)
        {
            return new PlayerSnap { Id = id, Name = "Bot " + id, IsAi = true, Team = -1, Health = health, MaxHealth = health >= 0 ? 100 : 0, Score = score, Dead = dead, Stunned = stunned, Attacker = attacker, Aux = PlayerSnap.NoAux };
        }

        private static void Observe(PlaytestFeed f, double t, int frame, params PlayerSnap[] snaps) =>
            f.ObservePlayers(snaps, snaps.Length, frame, At(t), "score_changed", null);

        private static PlaytestEvent[] All(PlaytestFeed f) => f.Query(500, 0).Entries.ToArray();

        private static string Types(PlaytestFeed f) => string.Join(",", All(f).Select(e => e.Type));

        // ------------------------------------------------------------------ ring buffer

        private static void BufferTests()
        {
            PlaytestEventBuffer b = new PlaytestEventBuffer(5);
            for (int i = 1; i <= 3; i++)
            {
                b.Add(new PlaytestEvent { Type = "e" + i });
            }
            PlaytestEventQueryResult q = b.Query(10, null);
            Check("event buffer: ids start at 1, oldest first", q.Entries.Count == 3 && q.Entries[0].Id == 1 && q.Entries[2].Id == 3 && q.NextId == 3 && !q.Dropped);
            q = b.Query(2, null);
            Check("event buffer: without sinceId the newest max entries", q.Entries.Count == 2 && q.Entries[0].Id == 2 && q.NextId == 3);
            q = b.Query(10, 1);
            Check("event buffer: sinceId pages forward", q.Entries.Count == 2 && q.Entries[0].Id == 2 && q.NextId == 3 && !q.Dropped);
            q = b.Query(1, 0);
            Check("event buffer: max limits a paged read and nextId follows the last returned entry", q.Entries.Count == 1 && q.NextId == 1);
            q = b.Query(10, 3);
            Check("event buffer: nothing new returns the same nextId", q.Entries.Count == 0 && q.NextId == 3 && !q.Dropped);
            for (int i = 4; i <= 9; i++)
            {
                b.Add(new PlaytestEvent { Type = "e" + i });
            }
            q = b.Query(10, 1);
            Check("event buffer: overwritten entries after sinceId set dropped", q.Dropped && q.Entries.Count == 5 && q.Entries[0].Id == 5 && b.Count == 5);
            q = b.Query(10, 4);
            Check("event buffer: sinceId just before the oldest kept entry is not dropped", !q.Dropped && q.Entries.Count == 5);
            b.Clear();
            Check("event buffer: Clear empties it but ids keep counting", b.Count == 0 && b.LastId == 9);
            b.Add(new PlaytestEvent { Type = "x" });
            q = b.Query(10, null);
            Check("event buffer: first entry after Clear has id 10", q.Entries.Count == 1 && q.Entries[0].Id == 10);
            q = b.Query(10, 4);
            Check("event buffer: a sinceId from before the clear reads dropped and the new entries", q.Dropped && q.Entries.Count == 1);
            q = b.Query(10, 9);
            Check("event buffer: a sinceId at the clear point is not dropped", !q.Dropped && q.Entries.Count == 1);
            PlaytestEventBuffer empty = new PlaytestEventBuffer(3);
            q = empty.Query(5, 0);
            Check("event buffer: empty buffer, sinceId 0: nothing, not dropped, nextId 0", q.Entries.Count == 0 && !q.Dropped && q.NextId == 0);
            Check("event buffer: capacity must be positive", Code(() => new PlaytestEventBuffer(0)) == "ArgumentOutOfRangeException");
        }

        private static string Code(Action a)
        {
            try { a(); return null; }
            catch (RpcException e) { return e.Code; }
            catch (Exception e) { return e.GetType().Name; }
        }

        // ------------------------------------------------------------------ state gate

        private static void GateTests()
        {
            Check("gate: PlayMode passes", PlaytestGate.CheckRunning(PlayState.PlayMode) == null);
            foreach (PlayState s in new[] { PlayState.Editing, PlayState.EnteringPlayMode, PlayState.ExitingPlayMode, PlayState.Closed })
            {
                RpcException e = PlaytestGate.CheckRunning(s);
                Check($"gate: {s} gives INVALID_STATE with a message", e != null && e.Code == ErrorCodes.InvalidState && e.Message.Length > 30);
            }
            Check("gate: the Editing message says how to start one", PlaytestGate.CheckRunning(PlayState.Editing).Message.Contains("start_playtest"));
            Check("gate: the Exiting message points at get_playtest_events", PlaytestGate.CheckRunning(PlayState.ExitingPlayMode).Message.Contains("get_playtest_events"));
        }

        // ------------------------------------------------------------------ playtest lifecycle

        private static void LifecycleTests()
        {
            PlaytestFeed f = new PlaytestFeed(50, null);
            Check("feed: idle at first (Closed), nothing collecting", !f.Collecting && f.EventCount == 0 && f.PlaytestSeq == 0 && f.AlivePlayers == null);
            f.OnState(PlayState.Editing, T0);
            Observe(f, 0, 1, P(0));
            Check("feed: samples are ignored while no playtest runs", f.EventCount == 0 && f.AlivePlayers == null);
            f.OnState(PlayState.EnteringPlayMode, At(1));
            Check("feed: leaving Editing starts a playtest (not collecting before PlayMode)", f.PlaytestSeq == 1 && !f.Collecting);
            f.OnState(PlayState.PlayMode, At(3));
            Check("feed: reaching PlayMode records playtest_started and collects", f.Collecting && Types(f) == "playtest_started");
            Observe(f, 4, 10, P(0, 100), P(1, 100));
            Check("feed: alive counter follows the samples", f.AlivePlayers == 2);
            f.OnState(PlayState.ExitingPlayMode, At(10));
            f.OnState(PlayState.Editing, At(11));
            PlaytestEvent[] ev = All(f);
            Check("feed: leaving PlayMode records playtest_ended with playSeconds", ev.Last().Type == "playtest_ended" && (double)ev.Last().Data["playSeconds"] == 7.0 && !f.Collecting);
            Check("feed: events stay readable after the playtest stopped", f.EventCount >= 4);
            long lastBefore = f.LastEventId;
            Observe(f, 12, 11, P(0, 50));
            Check("feed: no recording after the playtest ended", f.LastEventId == lastBefore);
            f.OnState(PlayState.EnteringPlayMode, At(20));
            Check("feed: a new playtest clears the buffer and counts up (also when started by hand)", f.PlaytestSeq == 2 && f.EventCount == 0 && f.AlivePlayers == null);
            Check("feed: ids are not reset by the clear", f.LastEventId == lastBefore);
            f.OnState(PlayState.PlayMode, At(22));
            Check("feed: the new playtest has its own playtest_started", All(f).Length == 1 && All(f)[0].Id == lastBefore + 1 && All(f)[0].PlaySeconds == 0);
            PlaytestFeed g = new PlaytestFeed(10, null);
            g.OnState(PlayState.PlayMode, T0);
            Check("feed: PlayMode seen without a prior Entering (plugin attached late) still starts a playtest", g.PlaytestSeq == 1 && g.Collecting);
        }

        // ------------------------------------------------------------------ diffing

        private static void DiffTests()
        {
            PlaytestFeed f = Playing();
            Observe(f, 1, 1, P(0, 100), P(1, 100));
            PlaytestEvent[] ev = All(f);
            Check("diff: the first sample adds players (player_added with isAI and health)",
                Types(f) == "playtest_started,player_added,player_added" && ev[1].Player == 0 && ev[1].PlayerName == "Bot 0" && (bool)ev[1].Data["isAI"] && (int)ev[1].Data["health"] == 100);
            Observe(f, 2, 2, P(0, 100), P(1, 75));
            ev = All(f);
            PlaytestEvent h = ev.Last();
            Check("diff: health down gives health_changed old/new/delta/max for that player only",
                h.Type == "health_changed" && h.Player == 1 && (int)h.Data["old"] == 100 && (int)h.Data["new"] == 75 && (int)h.Data["delta"] == -25 && (int)h.Data["max"] == 100 && ev.Length == 4);
            Check("diff: playSeconds of an event is the time since PlayMode", h.PlaySeconds == 2.0 && h.TimeUtc == At(2));
            Observe(f, 3, 3, P(0, 100), P(1, 90));
            h = All(f).Last();
            Check("diff: health up gives a positive delta", h.Type == "health_changed" && (int)h.Data["delta"] == 15);
            Observe(f, 4, 4, P(0, 100), P(1, 0, dead: true, attacker: 0));
            string t = Types(f);
            ev = All(f);
            Check("diff: death gives health_changed then player_died (deaths 1, by the attacker)",
                t.EndsWith("health_changed,player_died") && (int)ev.Last().Data["deaths"] == 1 && (int)ev.Last().Data["by"] == 0 && (int)ev.Last().Data["health"] == 0);
            Check("diff: alive counter drops", f.AlivePlayers == 1 && f.Deaths(1) == 1 && f.Deaths(0) == 0);
            long mark = f.LastEventId;
            Observe(f, 5, 5, P(0, 100), P(1, 0, dead: true));
            Check("diff: dead and unchanged produces nothing", f.LastEventId == mark);
            Observe(f, 8, 6, P(0, 100), P(1, 100));
            ev = All(f);
            Check("diff: dead -> alive gives player_respawned with health and no separate health_changed",
                ev.Last().Type == "player_respawned" && (int)ev.Last().Data["health"] == 100 && ev.Length == (int)(f.LastEventId - 0) && !Types(f).EndsWith("health_changed,player_respawned"));
            Observe(f, 9, 7, P(0, 100), P(1, 0, dead: true));
            Observe(f, 12, 8, P(0, 100), P(1, 100));
            Check("diff: a second death counts 2", f.Deaths(1) == 2);
            Observe(f, 13, 9, P(0, 100, score: 5), P(1, 100));
            h = All(f).Last();
            Check("diff: score change gives score_changed old/new/delta", h.Type == "score_changed" && h.Player == 0 && (int)h.Data["old"] == 0 && (int)h.Data["new"] == 5 && (int)h.Data["delta"] == 5);
            Observe(f, 14, 10, P(0, 100, score: 5, stunned: true), P(1, 100));
            Check("diff: stunned false -> true gives player_stunned", All(f).Last().Type == "player_stunned" && All(f).Last().Player == 0);
            Observe(f, 15, 11, P(0, 100, score: 5, stunned: false), P(1, 100));
            Check("diff: the end of a stun records nothing", All(f).Last().Type == "player_stunned");
            Observe(f, 16, 12, P(0, 100, score: 5), P(1, 100), P(2, 100));
            Check("diff: a new player id gives player_added", All(f).Last().Type == "player_added" && All(f).Last().Player == 2);
            Observe(f, 17, 13, P(0, 100, score: 5), P(2, 100));
            Check("diff: a missing player id gives player_removed", All(f).Last().Type == "player_removed" && All(f).Last().Player == 1);
            Observe(f, 18, 14, P(2, 100), P(0, 100, score: 5));
            Check("diff: reordering the players produces nothing", All(f).Last().Type == "player_removed");
            // health not tracked (useHealth off): no health events, deaths still work, score works
            PlaytestFeed g = Playing();
            Observe(g, 1, 1, P(0), P(1));
            Observe(g, 2, 2, P(0), P(1, dead: true));
            Check("diff: without health only the death shows (no health_changed)", Types(g) == "playtest_started,player_added,player_added,player_died" && !All(g).Last().Data.ContainsKey("health"));
            // health tracked state toggling from untracked to tracked must not fake a change
            PlaytestFeed k = Playing();
            Observe(k, 1, 1, P(0, -1));
            Observe(k, 2, 2, P(0, 100));
            Check("diff: -1 (untracked) to a value is not a health change", Types(k) == "playtest_started,player_added");
            // more than 16 players are capped, never an exception
            PlaytestFeed big = Playing();
            PlayerSnap[] many = Enumerable.Range(0, 20).Select(i => P(i, 10)).ToArray();
            big.ObservePlayers(many, many.Length, 1, At(1), "score_changed", null);
            Check("diff: more than 16 players are capped, not an error", All(big).Count(e => e.Type == "player_added") == 16);
        }

        private static void NoChangeTests()
        {
            PlaytestFeed f = Playing();
            PlayerSnap[] snaps = { P(0, 100, 3), P(1, 80, 4), P(2, 60, 5), P(3, 40, 6) };
            f.ObservePlayers(snaps, snaps.Length, 1, At(1), "score_changed", null);
            long mark = f.LastEventId;
            int count = f.EventCount;
            // warm up (JIT) and then measure allocations of an unchanged frame
            for (int i = 0; i < 100; i++)
            {
                f.ObservePlayers(snaps, snaps.Length, 2 + i, At(2), "score_changed", null);
                f.ObserveMinigame(new MinigameSnap { Present = true, State = 7, Round = 1, Rounds = 3 }, Ids, At(2), null);
            }
            Check("no change: unchanged frames add no event", f.LastEventId == mark + 1 && f.EventCount == count + 1); // +1: the single round_started of the first minigame sample
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++)
            {
                f.ObservePlayers(snaps, snaps.Length, 1000 + i, At(3), "score_changed", null);
                f.ObserveMinigame(new MinigameSnap { Present = true, State = 7, Round = 1, Rounds = 3 }, Ids, At(3), null);
                f.MinigameStateChanged(7);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check($"no change: 20000 unchanged frames allocate nothing ({allocated} bytes)", allocated < 2048);
            Check("no change: no event was added by them", f.EventCount == count + 1);

            PlaytestFeed idle = new PlaytestFeed(10, null);
            idle.OnState(PlayState.Editing, T0);
            long b2 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++)
            {
                idle.OnState(PlayState.Editing, At(i));
                idle.ObservePlayers(snaps, snaps.Length, i, At(i), "score_changed", null);
                idle.TryBeginLogic(i, At(i), out int r);
            }
            Check("no playtest: per-frame calls do no work and allocate nothing", GC.GetAllocatedBytesForCurrentThread() - b2 < 2048 && idle.EventCount == 0);
        }

        // ------------------------------------------------------------------ minigame transitions

        private static void MinigameTests()
        {
            PlaytestFeed f = Playing();
            int resultCalls = 0;
            Func<JObject> results = () => { resultCalls++; return new JObject { ["results"] = new JArray(new JObject { ["rank"] = 1, ["name"] = "Bot 0" }), ["winners"] = new JArray("Bot 0") }; };
            f.ObserveMinigame(new MinigameSnap { Present = true, State = 3 }, Ids, At(1), results);
            Check("minigame: states other than Playing record nothing", Types(f) == "playtest_started");
            f.ObserveMinigame(new MinigameSnap { Present = true, State = 7, Round = 1, Rounds = 2 }, Ids, At(2), results);
            PlaytestEvent e = All(f).Last();
            Check("minigame: entering Playing records round_started with round/rounds", e.Type == "round_started" && (int)e.Data["round"] == 1 && (int)e.Data["rounds"] == 2);
            Check("minigame: the state is reported as changed only once", !f.MinigameStateChanged(7) && f.MinigameStateChanged(10));
            f.ObserveMinigame(new MinigameSnap { Present = true, State = 10, Round = 1, Rounds = 2 }, Ids, At(30), results);
            e = All(f).Last();
            Check("minigame: leaving Playing records round_ended with the state name", e.Type == "round_ended" && (int)e.Data["round"] == 1 && (string)e.Data["state"] == "S10" && e.PlaySeconds == 30.0);
            f.ObserveMinigame(new MinigameSnap { Present = true, State = 7, Round = 2, Rounds = 2 }, Ids, At(35), results);
            Check("minigame: the second round starts", All(f).Last().Type == "round_started" && (int)All(f).Last().Data["round"] == 2);
            f.ObserveMinigame(new MinigameSnap { Present = true, State = 10, Round = 2, Rounds = 2 }, Ids, At(60), results);
            f.ObserveMinigame(new MinigameSnap { Present = true, State = 8, Round = 2, Rounds = 2 }, Ids, At(61), results);
            PlaytestEvent[] ev = All(f);
            Check("minigame: PreScoreScreen records minigame_finished with the results exactly once",
                ev.Last().Type == "minigame_finished" && resultCalls == 1 && ((string)ev.Last().Data["winners"][0]) == "Bot 0" && (int)ev.Last().Data["rounds"] == 2);
            f.ObserveMinigame(new MinigameSnap { Present = true, State = 9, Round = 2, Rounds = 2 }, Ids, At(65), results);
            Check("minigame: PreScoreScreen -> ShowScoreScreen does not finish twice", resultCalls == 1 && All(f).Count(x => x.Type == "minigame_finished") == 1);
            f.ObserveMinigame(new MinigameSnap { Present = false, State = 7 }, Ids, At(66), results);
            Check("minigame: an absent controller records nothing", All(f).Count(x => x.Type == "round_started") == 2);
            // attaching in the middle of a round
            PlaytestFeed g = Playing();
            g.ObserveMinigame(new MinigameSnap { Present = true, State = 7, Round = 1, Rounds = 1 }, Ids, At(1), null);
            Check("minigame: first sample already Playing records round_started", Types(g) == "playtest_started,round_started");
            // finished without a results provider
            PlaytestFeed h = Playing();
            h.ObserveMinigame(new MinigameSnap { Present = true, State = 8, Round = 1, Rounds = 1 }, Ids, At(1), null);
            Check("minigame: finished without a provider still records minigame_finished", Types(h) == "playtest_started,minigame_finished");
        }

        // ------------------------------------------------------------------ board

        private static void BoardTests()
        {
            PlaytestFeed f = Playing();
            PlayerSnap Bp(int id, int hp, int keys, int trophies) =>
                new PlayerSnap { Id = id, Name = "Bot " + id, IsAi = true, Team = -1, Health = hp, MaxHealth = 30, Score = keys, Aux = trophies, Attacker = -1 };
            PlayerSnap[] a = { Bp(0, 30, 5, 0), Bp(1, 30, 5, 0) };
            f.ObservePlayers(a, 2, 1, At(1), "keys_changed", "trophies_changed");
            PlayerSnap[] b = { Bp(0, 20, 8, 1), Bp(1, 30, 5, 0) };
            f.ObservePlayers(b, 2, 2, At(2), "keys_changed", "trophies_changed");
            string t = Types(f);
            Check("board: health, keys and trophies diff with their own event types", t.EndsWith("health_changed,keys_changed,trophies_changed"));
            Check("board: trophies delta", (int)All(f).Last().Data["delta"] == 1);
            f.ObserveTurn(1, "Bot 1", 3, At(3));
            f.ObserveTurn(1, "Bot 1", 3, At(4));
            f.ObserveTurn(0, "Bot 0", 3, At(5));
            f.ObserveTurn(-1, null, 3, At(6));
            Check("board: turn_started once per change, none for nobody", All(f).Count(e => e.Type == "turn_started") == 2 && (int)All(f).Last().Data["turn"] == 3);
        }

        // ------------------------------------------------------------------ cause hints

        private static void CauseTests()
        {
            PlaytestFeed f = Playing();
            Observe(f, 1, 100, P(0, 100), P(1, 100));
            f.NoteCauseAction(101, PlaytestFeed.KindHealth, "ChangeHealth action [receiver Hazard, source player 1]");
            Observe(f, 2, 101, P(0, 100), P(1, 75));
            PlaytestEvent h = All(f).Last();
            Check("cause: a health action of the same frame is attached to health_changed", h.Type == "health_changed" && ((string)h.Data["cause"]).Contains("ChangeHealth action") && ((string)h.Data["cause"]).Contains("Hazard"));
            f.NoteCauseAction(101, PlaytestFeed.KindScore, "ChangeScore action");
            Observe(f, 3, 102, P(0, 100), P(1, 50));
            Check("cause: a one-frame-old health action still counts, a score action does not attach to health", ((string)All(f).Last().Data["cause"]) == "ChangeHealth action [receiver Hazard, source player 1]");
            Observe(f, 4, 120, P(0, 100), P(1, 25));
            Check("cause: an old action is not attached", !All(f).Last().Data.ContainsKey("cause"));
            f.NoteCauseAction(130, PlaytestFeed.KindScore, "ChangeScore action [receiver Goal]");
            Observe(f, 5, 130, P(0, 100, score: 1), P(1, 25));
            Check("cause: a score action is attached to score_changed", ((string)All(f).Last().Data["cause"]) == "ChangeScore action [receiver Goal]");
            f.NoteCauseAction(140, PlaytestFeed.KindHealth, "Kill action [receiver Lava]");
            Observe(f, 6, 140, P(0, 100, score: 1), P(1, 0, dead: true));
            PlaytestEvent died = All(f).Last();
            Check("cause: the death carries the kill action", died.Type == "player_died" && ((string)died.Data["cause"]).StartsWith("Kill action"));
            Observe(f, 7, 141, P(0, 100, score: 1, attacker: 1), P(1, 100));
            Observe(f, 8, 142, P(0, 60, score: 1, attacker: 1), P(1, 100));
            Check("cause: a damaging player is reported as by", (int)All(f).Last().Data["by"] == 1);
            Observe(f, 9, 143, P(0, 90, score: 1, attacker: 1), P(1, 100));
            Check("cause: healing never reports by", !All(f).Last().Data.ContainsKey("by"));
            f.NoteCauseAction(300, PlaytestFeed.KindHealth, "A");
            f.NoteCauseAction(300, PlaytestFeed.KindHealth, "B");
            f.NoteCauseAction(300, PlaytestFeed.KindHealth, "C");
            f.NoteCauseAction(300, PlaytestFeed.KindHealth, "D");
            Observe(f, 10, 300, P(0, 10, score: 1), P(1, 100));
            Check("cause: at most three hints are joined", ((string)All(f).Last().Data["cause"]).Split(';').Length == 3);
        }

        // ------------------------------------------------------------------ mod logic records

        private static void LogicThrottleTests()
        {
            PlaytestFeed f = Playing();
            Check("logic: the first record of a key passes", f.TryBeginLogic(1, At(1), out int r) && r == 0);
            f.RecordLogic("trigger_fired", At(1), -1, null, new JObject { ["trigger"] = "Triggers[0]" }, 0);
            Check("logic: the same key within a second is skipped", !f.TryBeginLogic(1, At(1.5), out r));
            Check("logic: another key is independent", f.TryBeginLogic(2, At(1.5), out r) && r == 0);
            Check("logic: after the interval it passes again and reports the skipped repeats", f.TryBeginLogic(1, At(2.2), out r) && r == 1);
            f.RecordLogic("trigger_fired", At(2.2), 3, "Bot 3", new JObject { ["trigger"] = "Triggers[0]" }, r);
            PlaytestEvent e = All(f).Last();
            Check("logic: the record carries repeats and the player", (int)e.Data["repeats"] == 1 && e.Player == 3 && e.Type == "trigger_fired");
            Check("logic: skipped ones are counted", f.LogicSuppressed == 1);
            // token bucket: a storm of different keys at one instant
            PlaytestFeed g = Playing();
            int passed = 0;
            for (int i = 0; i < 500; i++)
            {
                if (g.TryBeginLogic(1000 + i, At(1), out int rr))
                {
                    passed++;
                    g.RecordLogic("action_executed", At(1), -1, null, new JObject(), rr);
                }
            }
            Check($"logic: a burst is capped by the token bucket ({passed} of 500)", passed == (int)PlaytestFeed.LogicBurst && g.LogicSuppressed == 500 - passed);
            int more = 0;
            for (int i = 0; i < 500; i++)
            {
                if (g.TryBeginLogic(5000 + i, At(2), out int rr))
                {
                    more++;
                }
            }
            Check($"logic: it refills about {PlaytestFeed.LogicPerSecond} per second ({more})", more == (int)PlaytestFeed.LogicPerSecond);
            // a flood cannot push the deaths out of a small ring more than the limits allow, and a death is never throttled
            PlaytestFeed h = Playing(30);
            Observe(h, 0, 1, P(0, 10), P(1, 10));
            for (int i = 0; i < 100; i++)
            {
                if (h.TryBeginLogic(i, At(0.5), out int rr))
                {
                    h.RecordLogic("action_executed", At(0.5), -1, null, null, rr);
                }
            }
            Observe(h, 1, 2, P(0, 0, dead: true), P(1, 10));
            Check("logic: gameplay events are never rate limited", All(h).Last().Type == "player_died");
            // not collecting: nothing passes
            PlaytestFeed idle = new PlaytestFeed(10, null);
            Check("logic: no record while no playtest runs", !idle.TryBeginLogic(1, T0, out _));
            idle.RecordLogic("trigger_fired", T0, -1, null, null, 0);
            Check("logic: RecordLogic while idle stores nothing", idle.EventCount == 0);
        }

        // ------------------------------------------------------------------ wire shape

        private static void JsonTests()
        {
            PlaytestFeed f = Playing();
            Observe(f, 1, 1, P(0, 100), P(1, 100));
            Observe(f, 2, 2, P(0, 100), P(1, 75));
            JObject r = PlaytestEventJson.Result(f, 100, null, true, "sess");
            JArray entries = (JArray)r["entries"];
            JObject last = (JObject)entries.Last;
            Check("json: result carries entries, nextId, dropped, running, playtest, logicEvents, suppressedLogicEvents, sessionId",
                new[] { "entries", "nextId", "dropped", "running", "playtest", "logicEvents", "suppressedLogicEvents", "sessionId" }.All(k => r[k] != null) && (bool)r["running"] && (string)r["sessionId"] == "sess");
            Check("json: an entry is flat with id, time, playSeconds, type, player, name and the data fields",
                (string)last["type"] == "health_changed" && (int)last["player"] == 1 && (string)last["name"] == "Bot 1" && (int)last["old"] == 100 && (int)last["new"] == 75
                && ((string)last["time"]).EndsWith("Z") && (double)last["playSeconds"] == 2.0 && (long)last["id"] == f.LastEventId);
            Check("json: an event without a player has neither player nor name", ((JObject)entries[0])["player"] == null && ((JObject)entries[0])["name"] == null);
            JObject paged = PlaytestEventJson.Result(f, 2, 1, false, null);
            Check("json: paging by sinceId/max and nextId", ((JArray)paged["entries"]).Count == 2 && (long)paged["nextId"] == 3 && !(bool)paged["dropped"] && !(bool)paged["logicEvents"]);
            JObject again = PlaytestEventJson.Result(f, 100, (long)paged["nextId"], true, null);
            Check("json: continuing from nextId returns the rest", ((JArray)again["entries"]).Count == (int)(f.LastEventId - 3));
            f.OnState(PlayState.ExitingPlayMode, At(5));
            f.OnState(PlayState.Editing, At(6));
            JObject after = PlaytestEventJson.Result(f, 100, null, true, null);
            Check("json: readable after the playtest stopped (running false, entries kept)", !(bool)after["running"] && ((JArray)after["entries"]).Count >= 5 && (long)after["playtest"] == 1);
            Check("json: the entry is independent of the stored data (DeepClone)", ((JArray)after["entries"]).Last["type"] != null && last.Parent != null);
        }
    }
}
