using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using PummelBridge.Hooks;
using UnityEngine;

namespace PummelBridge.Game
{
    /// <summary>
    /// Per-frame sampler of the running playtest (main thread). Feeds <see cref="PlaytestFeed"/>: the editor state every frame
    /// (cheap), and - only while the editor is in PlayMode - one allocation-free snapshot of the players and the controller
    /// state. Reads only pure getters (verified in the decompiled source: CharacterBase.IsDead/Score/GamePlayer,
    /// SimpleModPlayer.CurHealth/UsingHealth/MaxHealth/Stunned/LastSourceOfPlayerDamage, GamePlayer.GlobalID/Name/IsAI/GameTeam,
    /// MinigameController.State/players, BoardPlayer.ServerHealth/Gold/GoalScore, GameBoardController.CurPlayer/CurTurnNum). Nothing
    /// is modified. When no playtest runs, the work is one comparison.
    /// </summary>
    internal sealed class PlaytestWatch
    {
        private readonly PlaytestFeed m_feed;
        private readonly IBridgeLog m_log;
        private PlayerSnap[] m_snaps = new PlayerSnap[16];
        private readonly Func<JObject> m_results;
        private MinigameController m_resultsFor;

        public static readonly MinigameStateIds StateIds = new MinigameStateIds
        {
            Playing = (int)MinigameControllerState.Playing,
            FinishedA = (int)MinigameControllerState.PreScoreScreen,
            FinishedB = (int)MinigameControllerState.ShowScoreScreen,
        };

        public PlaytestWatch(PlaytestFeed feed, IBridgeLog log)
        {
            m_feed = feed;
            m_log = log ?? NullBridgeLog.Instance;
            m_results = () => PlaytestStateReader.MinigameResults(m_resultsFor);
        }

        public void Frame(EditorSystem ed, PlayState state, DateTime nowUtc)
        {
            m_feed.OnState(state, nowUtc);
            PlaytestHooks.Collecting = m_feed.Collecting;
            if (!m_feed.Collecting)
            {
                return;
            }
            try
            {
                PummelMod mod = ed?.CurrentMod;
                if (mod == null)
                {
                    return;
                }
                if (mod.ModType == PummelModType.Minigame)
                {
                    SampleMinigame(nowUtc);
                }
                else if (mod.ModType == PummelModType.Board)
                {
                    SampleBoard(nowUtc);
                }
            }
            catch (Exception e)
            {
                GameReflection.WarnOnce("playtestwatch.frame", "playtest event feed: cannot sample the running playtest: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Grow(int needed)
        {
            if (needed > m_snaps.Length)
            {
                Array.Resize(ref m_snaps, Math.Max(needed, m_snaps.Length * 2));
            }
        }

        private void SampleMinigame(DateTime now)
        {
            MinigameController mg = GameManager.Minigame;
            if (mg == null)
            {
                return;
            }
            bool team = GameManager.PlayingTeamMode;
            List<CharacterBase> players = mg.players;
            int n = 0;
            if (players != null)
            {
                Grow(players.Count);
                for (int i = 0; i < players.Count; i++)
                {
                    CharacterBase cb = players[i];
                    if (cb == null)
                    {
                        continue;
                    }
                    GamePlayer gp = cb.GamePlayer;
                    if (gp == null)
                    {
                        continue;
                    }
                    PlayerSnap s = default(PlayerSnap);
                    s.Id = gp.GlobalID;
                    s.Name = gp.Name;
                    s.IsAi = gp.IsAI;
                    s.Team = team && gp.GameTeam != null ? gp.GameTeam.TeamID : -1;
                    s.Dead = cb.IsDead;
                    s.Score = cb.Score;
                    s.Health = -1;
                    s.Aux = PlayerSnap.NoAux;
                    s.Attacker = -1;
                    SimpleModPlayer sp = cb as SimpleModPlayer;
                    if (sp != null)
                    {
                        s.Stunned = sp.Stunned;
                        if (sp.UsingHealth)
                        {
                            s.Health = sp.CurHealth;
                            s.MaxHealth = sp.MaxHealth;
                        }
                        SimpleModPlayer by = sp.LastSourceOfPlayerDamage;
                        if (by != null && by.GamePlayer != null)
                        {
                            s.Attacker = by.GamePlayer.GlobalID;
                        }
                    }
                    m_snaps[n++] = s;
                }
            }
            m_feed.ObservePlayers(m_snaps, n, Time.frameCount, now, "score_changed", null);
            MinigameSnap snap = new MinigameSnap { Present = true, State = (int)mg.State };
            // The round counters are only read when the state changed (reflection boxes; not needed every frame).
            if (m_feed.MinigameStateChanged(snap.State))
            {
                snap.Rounds = mg.rounds;
                snap.Round = PlaytestStateReader.DisplayRound(mg);
                m_resultsFor = mg;
                m_feed.ObserveMinigame(snap, StateIds, now, m_results);
                m_resultsFor = null;
            }
        }

        private void SampleBoard(DateTime now)
        {
            GameBoardController board = GameManager.Board;
            if (board == null || board.BoardPlayers == null)
            {
                return;
            }
            List<BoardPlayer> players = board.BoardPlayers;
            Grow(players.Count);
            bool team = GameManager.PlayingTeamMode;
            int n = 0;
            for (int i = 0; i < players.Count; i++)
            {
                BoardPlayer bp = players[i];
                if (bp == null)
                {
                    continue;
                }
                GamePlayer gp = bp.GamePlayer;
                if (gp == null)
                {
                    continue;
                }
                PlayerSnap s = default(PlayerSnap);
                s.Id = gp.GlobalID;
                s.Name = gp.Name;
                s.IsAi = gp.IsAI;
                s.Team = team && gp.GameTeam != null ? gp.GameTeam.TeamID : -1;
                s.Health = bp.ServerHealth;
                s.MaxHealth = bp.maxHealth;
                s.Score = bp.Gold;
                s.Aux = bp.GoalScore;
                s.Attacker = -1;
                m_snaps[n++] = s;
            }
            m_feed.ObservePlayers(m_snaps, n, Time.frameCount, now, "keys_changed", "trophies_changed");
            BoardPlayer cur = board.CurPlayer;
            if (cur != null && cur.GamePlayer != null)
            {
                m_feed.ObserveTurn(cur.GamePlayer.GlobalID, cur.GamePlayer.Name, board.CurTurnNum, now);
            }
        }
    }
}
