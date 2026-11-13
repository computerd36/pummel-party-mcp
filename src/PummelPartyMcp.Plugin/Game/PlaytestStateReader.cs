using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using UnityEngine;

namespace PummelPartyMcp.Game
{
    /// <summary>
    /// get_playtest_state: a compact snapshot of the running playtest's players and minigame/board state (main thread).
    /// Side-effect free: every member read here was checked in the decompiled source to be a plain getter (field read) or is a
    /// private field read by reflection (m_lastDeathTime, m_filoHeldItemStack, MinigameController.round / round_begin_time).
    /// Nothing is written or saved, whoever started the playtest and whichever mod (real or test) is being played.
    /// </summary>
    internal static class PlaytestStateReader
    {
        private static readonly Dictionary<int, string> s_stateNames = BuildStateNames();

        private static Dictionary<int, string> BuildStateNames()
        {
            Dictionary<int, string> d = new Dictionary<int, string>();
            foreach (MinigameControllerState s in Enum.GetValues(typeof(MinigameControllerState)))
            {
                d[(int)s] = s.ToString();
            }
            return d;
        }

        /// <summary>Name of a MinigameControllerState value (cached: ToString allocates).</summary>
        public static string StateName(int state) => s_stateNames.TryGetValue(state, out string n) ? n : state.ToString(CultureInfo.InvariantCulture);

        public const string HealthOffNote =
            "useHealth is off in the Minigame Settings (player settings): players have no health points. Change Health still works as a kill switch: " +
            "the stored health starts at 0 (max is 1), so ANY Change Health whose result is 0 or less (Add -25, Subtract 25, Set 0, Multiply...) kills the player at once, " +
            "whatever the amount; a result above 0 only sets health to 1 and the next decrease kills. Turn on Use Health (and set Health) by hand for hit points.";

        /// <summary>The round as the round UI shows it (1-based, clamped to rounds); 0 when unreadable.</summary>
        public static int DisplayRound(MinigameController mg)
        {
            FieldInfo f = GameReflection.FindField(typeof(MinigameController), "round");
            if (f != null && f.GetValue(mg) is int r)
            {
                return Math.Min(r + 1, Math.Max(1, mg.rounds));
            }
            return 0;
        }

        public static JObject Read(EditorSystem ed, PlaytestFeed feed)
        {
            PummelMod mod = ed.CurrentMod;
            JObject j = new JObject
            {
                ["running"] = true,
                ["mod"] = mod == null ? null : EditorAccess.ModName(mod),
                ["modType"] = mod == null ? null : mod.ModType.ToString(),
                ["playtest"] = feed.PlaytestSeq,
                ["paused"] = Time.timeScale <= 0f,
            };
            if (mod != null && mod.ModType == PummelModType.Minigame)
            {
                ReadMinigame(j, feed);
            }
            else if (mod != null && mod.ModType == PummelModType.Board)
            {
                ReadBoard(j, feed);
            }
            else
            {
                j["supported"] = false;
                j["reason"] = "only Minigame and Board mods have a readable playtest state";
                j["players"] = new JArray();
            }
            j["events"] = new JObject { ["count"] = feed.EventCount, ["lastId"] = feed.LastEventId };
            return j;
        }

        // ------------------------------------------------------------------ minigame

        private static void ReadMinigame(JObject j, PlaytestFeed feed)
        {
            MinigameController mg = GameManager.Minigame;
            if (mg == null)
            {
                j["supported"] = false;
                j["reason"] = "the minigame controller does not exist: either the play scene is still loading (retry in a moment) or the round/minigame is already over and the game has dropped it (get_playtest_events still has what happened)";
                j["players"] = new JArray();
                return;
            }
            SimpleModController simple = mg as SimpleModController;
            SimpleModPlayerSettings ps = simple?.PlayerSettings;
            SimpleModControllerSettings cs = simple?.ControllerSettings;
            if (ps != null)
            {
                j["useHealth"] = ps.useHealth;
                if (ps.useHealth)
                {
                    j["maxHealth"] = ps.health;
                }
                else
                {
                    j["note"] = HealthOffNote;
                }
                j["respawn"] = ps.respawnEnabled
                    ? new JObject { ["enabled"] = true, ["time"] = ps.respawnTime, ["invulnerableTime"] = ps.respawnInvulnerableTime }
                    : new JObject { ["enabled"] = false };
            }
            JObject m = new JObject
            {
                ["type"] = simple != null ? "simple" : "other",
                ["state"] = StateName((int)mg.State),
                ["round"] = DisplayRound(mg),
                ["rounds"] = mg.rounds,
            };
            bool playing = mg.State == MinigameControllerState.Playing;
            if (cs != null)
            {
                m["endCondition"] = cs.EndConditions.ToString();
                m["placement"] = cs.PlacementCondition.ToString();
                m["playerType"] = cs.ChosenPlayerType.ToString();
                if (cs.EndConditions.HasFlag(SimpleMinigameEndCondition.Timer))
                {
                    m["roundLength"] = Math.Round(mg.RoundLength, 1);
                    if (playing)
                    {
                        m["timeRemaining"] = Math.Round(Math.Max(0f, mg.TimeRemaining), 1);
                    }
                }
                if (cs.EndConditions.HasFlag(SimpleMinigameEndCondition.ObtainPoints))
                {
                    m["pointsToWin"] = simple.pointsToWin;
                }
                if (cs.EndConditions.HasFlag(SimpleMinigameEndCondition.RemainingPlayersAlive))
                {
                    m["minPlayersAlive"] = simple.minPlayersAlive;
                }
                if (cs.PlacementCondition == SimpleMinigamePlacementCondition.FinishOrder || cs.EndConditions.HasFlag(SimpleMinigameEndCondition.FinishMinigame))
                {
                    m["finishedPlayers"] = simple.FinishedPlayers;
                }
            }
            if (playing && GameReflection.FindField(typeof(MinigameController), "round_begin_time")?.GetValue(mg) is float began)
            {
                m["timeElapsed"] = Math.Round(Math.Max(0f, Time.time - began), 1);
            }
            bool finished = mg.State == MinigameControllerState.PreScoreScreen || mg.State == MinigameControllerState.ShowScoreScreen;
            m["finished"] = finished;
            if (finished)
            {
                foreach (JProperty p in MinigameResults(mg).Properties())
                {
                    m[p.Name] = p.Value;
                }
            }
            j["minigame"] = m;

            bool team = GameManager.PlayingTeamMode;
            JArray players = new JArray();
            List<CharacterBase> list = mg.players;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                CharacterBase cb = list[i];
                if (cb == null || cb.GamePlayer == null)
                {
                    continue;
                }
                players.Add(MinigamePlayer(cb, mg, ps, team, feed));
            }
            j["players"] = players;
            int alive = 0;
            foreach (JToken p in players)
            {
                if ((bool)p["alive"])
                {
                    alive++;
                }
            }
            j["alivePlayers"] = alive;
        }

        private static JObject MinigamePlayer(CharacterBase cb, MinigameController mg, SimpleModPlayerSettings ps, bool team, PlaytestFeed feed)
        {
            GamePlayer gp = cb.GamePlayer;
            JObject p = new JObject
            {
                ["index"] = (int)gp.GlobalID,
                ["name"] = gp.Name,
                ["isAI"] = gp.IsAI,
            };
            if (team && gp.GameTeam != null)
            {
                p["team"] = (int)gp.GameTeam.TeamID;
            }
            p["alive"] = !cb.IsDead;
            SimpleModPlayer sp = cb as SimpleModPlayer;
            if (sp != null && sp.UsingHealth)
            {
                p["health"] = (int)sp.CurHealth;
                p["maxHealth"] = (int)sp.MaxHealth;
            }
            p["score"] = (int)cb.Score;
            Vector3 pos = cb.transform.position;
            p["position"] = new JObject { ["x"] = Math.Round(pos.x, 2), ["y"] = Math.Round(pos.y, 2), ["z"] = Math.Round(pos.z, 2) };
            p["deaths"] = feed.Deaths(gp.GlobalID);
            if (sp == null)
            {
                return p;
            }
            if (sp.HasFinished)
            {
                p["finished"] = true;
                if (sp.Placement != int.MaxValue)
                {
                    p["placement"] = sp.Placement + 1;
                }
            }
            if (sp.Stunned)
            {
                p["stunned"] = true;
            }
            if (sp.IsInulnerable)
            {
                p["invulnerable"] = true;
            }
            if (cb.IsDead && ps != null && ps.respawnEnabled && mg.State == MinigameControllerState.Playing
                && GameReflection.FindField(typeof(SimpleModPlayer), "m_lastDeathTime")?.GetValue(sp) is float died)
            {
                p["respawnIn"] = Math.Round(Math.Max(0f, ps.respawnTime - (Time.time - died)), 1);
            }
            SimpleModPlayer by = sp.LastSourceOfPlayerDamage;
            if (by != null && by.GamePlayer != null)
            {
                p["lastDamagedBy"] = (int)by.GamePlayer.GlobalID;
            }
            if (GameReflection.FindField(typeof(SimpleModPlayer), "m_filoHeldItemStack")?.GetValue(sp) is IList held && held.Count > 0)
            {
                p["heldItemCount"] = held.Count;
                ModItem last = held[held.Count - 1] as ModItem;
                if (last != null)
                {
                    p["heldItem"] = last.name;
                    p["heldItemType"] = last.ItemType.ToString();
                }
            }
            return p;
        }

        /// <summary>
        /// Final standings of a finished minigame: ranked by the game's own value (GamePlayer.MinigameScore, which BuildResults filled;
        /// negated points for LeastPoints), with each player's raw points. Ties share a rank. Also used for the minigame_finished event.
        /// </summary>
        public static JObject MinigameResults(MinigameController mg)
        {
            JObject r = new JObject();
            if (mg == null || mg.players == null)
            {
                return r;
            }
            List<CharacterBase> list = new List<CharacterBase>();
            foreach (CharacterBase cb in mg.players)
            {
                if (cb != null && cb.GamePlayer != null)
                {
                    list.Add(cb);
                }
            }
            List<JObject> rows = new List<JObject>();
            JArray winners = new JArray();
            foreach (CharacterBase cb in list)
            {
                int rank = 1;
                foreach (CharacterBase other in list)
                {
                    if (other.GamePlayer.MinigameScore > cb.GamePlayer.MinigameScore)
                    {
                        rank++;
                    }
                }
                rows.Add(new JObject
                {
                    ["rank"] = rank,
                    ["index"] = (int)cb.GamePlayer.GlobalID,
                    ["name"] = cb.GamePlayer.Name,
                    ["score"] = (int)cb.Score,
                });
                if (rank == 1)
                {
                    winners.Add(cb.GamePlayer.Name);
                }
            }
            rows.Sort((a, b) => ((int)a["rank"]).CompareTo((int)b["rank"]));
            r["results"] = new JArray(rows.ToArray());
            r["winners"] = winners;
            return r;
        }

        // ------------------------------------------------------------------ board

        private static void ReadBoard(JObject j, PlaytestFeed feed)
        {
            GameBoardController board = GameManager.Board;
            if (board == null || board.BoardPlayers == null)
            {
                j["board"] = new JObject { ["supported"] = false, ["reason"] = "the board controller does not exist yet (the play scene is still loading); retry in a moment" };
                j["players"] = new JArray();
                return;
            }
            JObject b = new JObject
            {
                ["supported"] = true,
                ["state"] = board.BoardState.ToString(),
                ["turn"] = board.CurTurnNum,
                ["keysAre"] = "BoardPlayer.Gold",
            };
            BoardPlayer cur = board.CurPlayer;
            if (cur != null && cur.GamePlayer != null)
            {
                b["currentPlayer"] = (int)cur.GamePlayer.GlobalID;
            }
            j["board"] = b;
            bool team = GameManager.PlayingTeamMode;
            JArray players = new JArray();
            foreach (BoardPlayer bp in board.BoardPlayers)
            {
                if (bp == null || bp.GamePlayer == null)
                {
                    continue;
                }
                GamePlayer gp = bp.GamePlayer;
                JObject p = new JObject
                {
                    ["index"] = (int)gp.GlobalID,
                    ["name"] = gp.Name,
                    ["isAI"] = gp.IsAI,
                };
                if (team && gp.GameTeam != null)
                {
                    p["team"] = (int)gp.GameTeam.TeamID;
                }
                p["health"] = (int)bp.ServerHealth;
                p["maxHealth"] = (int)bp.maxHealth;
                p["keys"] = bp.Gold;
                p["trophies"] = bp.GoalScore;
                BoardNode node = bp.CurrentNode;
                if (node != null)
                {
                    p["space"] = node.NodeID;
                }
                p["state"] = bp.PlayerState.ToString();
                Vector3 pos = bp.transform.position;
                p["position"] = new JObject { ["x"] = Math.Round(pos.x, 2), ["y"] = Math.Round(pos.y, 2), ["z"] = Math.Round(pos.z, 2) };
                players.Add(p);
            }
            j["players"] = players;
        }
    }
}
