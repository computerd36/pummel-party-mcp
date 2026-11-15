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
            "useHealth is off in the Minigame Settings (player settings): players have no health points, health is a 0/1 flag (0 until the first player reset at round start, then 1, and 1 again after every respawn). " +
            "A Change Health value of 0 does nothing (a hit animation only), whatever the operation, so Set 0, Add 0 and Multiply 0 kill nobody. " +
            "For a non-zero value the result is clamped to 0..1: a result of 0 or less kills at once (Add -25, Subtract 25, Set -1; from health 1, Divide 2 gives 0 and kills, Multiply 2 clamps to 1 and does not), a result above 0 leaves health at 1. " +
            "Only a player target is affected: in a Trigger or Logic Hit the player is Source (Activator). Kill/Destroy (type Kill) kills a player target without this arithmetic, with the same refusals as any death; a non-player target is deactivated. " +
            "Turn on Use Health (and set Health) by hand for hit points.";

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
            JObject audio = ReadAudio(ed);
            if (audio != null)
            {
                j["audio"] = audio;
            }
            j["events"] = new JObject { ["count"] = feed.EventCount, ["lastId"] = feed.LastEventId };
            return j;
        }

        // ------------------------------------------------------------------ audio

        /// <summary>
        /// The game's audio state: music source (AudioSystemObj.currentMusic), the ambience source (the pooled TempAudioSource whose
        /// SoundType is Ambience and that is playing), the number of pooled sources currently playing, and the player's own volume
        /// settings (AudioSystem's applied master/music/effects volumes, 0..1). Read-only; every value that cannot be read is left
        /// out (or null), and nothing here may fail the state call.
        /// </summary>
        public static JObject ReadAudio(EditorSystem ed)
        {
            try
            {
                JObject a = new JObject();
                object sys = null;
                try
                {
                    sys = GameReflection.FindField(typeof(AudioSystem), "audioSystemObj")?.GetValue(null);
                }
                catch (Exception)
                {
                    // leave null
                }
                AudioSystemObj obj = sys as AudioSystemObj;
                TempAudioSource music = null;
                TempAudioSource ambience = null;
                int active = -1;
                if (obj != null)
                {
                    try
                    {
                        music = GameReflection.FindField(typeof(AudioSystemObj), "currentMusic")?.GetValue(obj) as TempAudioSource;
                    }
                    catch (Exception)
                    {
                        // leave null
                    }
                    try
                    {
                        if (GameReflection.FindField(typeof(AudioSystemObj), "audioSources")?.GetValue(obj) is IList list)
                        {
                            active = 0;
                            for (int i = 0; i < list.Count; i++)
                            {
                                TempAudioSource src = list[i] as TempAudioSource;
                                if (src == null || !src.IsPlaying)
                                {
                                    continue;
                                }
                                active++;
                                if (ambience == null && src.SoundType == SoundType.Ambience)
                                {
                                    ambience = src;
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        active = -1;
                    }
                }
                a["music"] = AudioSourceJson(music);
                a["ambience"] = AudioSourceJson(ambience);
                if (active >= 0)
                {
                    a["activeSources"] = active;
                }
                AddModAudioSources(a, ed);
                AddVolume(a, "masterVolume", "masterVolume");
                AddVolume(a, "musicVolume", "musicVolume");
                AddVolume(a, "effectsVolume", "effectsVolume");
                return a;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public const int MaxModAudioSources = 20;

        /// <summary>
        /// The mod's own Audio Source components in the running scene (ModAudioSource wraps a plain AudioSource that Play Sound on an
        /// Audio Source target plays directly, outside the game's pool). The editor's own edit-time copies (under the hierarchy and
        /// prefab roots) are skipped. Nothing is loaded; at most 20 are listed, modAudioSourcesTotal says how many exist.
        /// </summary>
        private static void AddModAudioSources(JObject a, EditorSystem ed)
        {
            try
            {
                Transform h = ed == null ? null : ed.m_hierarchyRoot;
                Transform pr = ed == null ? null : ed.m_prefabsRoot;
                JArray list = new JArray();
                int total = 0;
                foreach (ModAudioSource m in UnityEngine.Object.FindObjectsOfType<ModAudioSource>())
                {
                    if (m == null || (h != null && m.transform.IsChildOf(h)) || (pr != null && m.transform.IsChildOf(pr)))
                    {
                        continue;
                    }
                    total++;
                    if (list.Count >= MaxModAudioSources)
                    {
                        continue;
                    }
                    try
                    {
                        AudioSource s = m.Component as AudioSource;
                        if (s == null)
                        {
                            continue;
                        }
                        JObject o = new JObject { ["object"] = m.gameObject.name };
                        AudioClip clip = s.clip;
                        o["clip"] = clip == null ? null : clip.name;
                        o["volume"] = Math.Round(s.volume, 3);
                        o["playing"] = s.isPlaying;
                        o["loop"] = s.loop;
                        if (clip != null)
                        {
                            o["timeSeconds"] = Math.Round(s.time, 3);
                            o["lengthSeconds"] = Math.Round(clip.length, 3);
                        }
                        list.Add(o);
                    }
                    catch (Exception)
                    {
                        // skip this one
                    }
                }
                a["modAudioSources"] = list;
                if (total > list.Count)
                {
                    a["modAudioSourcesTotal"] = total;
                }
            }
            catch (Exception)
            {
                // omitted
            }
        }

        private static void AddVolume(JObject a, string name, string field)
        {
            try
            {
                if (GameReflection.FindField(typeof(AudioSystem), field)?.GetValue(null) is float v && !float.IsNaN(v) && !float.IsInfinity(v))
                {
                    a[name] = Math.Round(v, 3);
                }
            }
            catch (Exception)
            {
                // omitted
            }
        }

        /// <summary>{clip, volume, playing, timeSeconds, lengthSeconds} of a source that holds a clip; null for none.</summary>
        private static JToken AudioSourceJson(TempAudioSource t)
        {
            try
            {
                AudioSource s = t == null ? null : t.audioSource;
                if (s == null)
                {
                    return JValue.CreateNull();
                }
                AudioClip clip = s.clip;
                if (clip == null)
                {
                    return JValue.CreateNull();
                }
                JObject o = new JObject { ["clip"] = clip.name };
                o["volume"] = Math.Round(s.volume, 3);
                o["playing"] = s.isPlaying;
                o["timeSeconds"] = Math.Round(s.time, 3);
                o["lengthSeconds"] = Math.Round(clip.length, 3);
                return o;
            }
            catch (Exception)
            {
                return JValue.CreateNull();
            }
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
