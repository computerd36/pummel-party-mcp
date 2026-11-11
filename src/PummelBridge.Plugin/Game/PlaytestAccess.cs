using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using UnityEngine;
using ZP.Net;

namespace PummelBridge.Game
{
    /// <summary>
    /// Playtest and validator access to the game (main thread only). Members verified against the decompiled
    /// Assembly-CSharp: ToolbarWidget (Play/Stop buttons, player-count dropdown, AI Only checkbox), EditorSystem.SetEditorState,
    /// ModValidationSystem, NetSystem.OnDisconnect.
    /// </summary>
    internal static class PlaytestAccess
    {
        public static PlayState StateOf(EditorSystem ed)
        {
            if (ed == null)
            {
                return PlayState.Closed;
            }
            switch (ed.State)
            {
                case EditorState.Editing: return PlayState.Editing;
                case EditorState.EnteringPlayMode: return PlayState.EnteringPlayMode;
                case EditorState.PlayMode: return PlayState.PlayMode;
                case EditorState.ExitingPlayMode: return PlayState.ExitingPlayMode;
                default: return PlayState.EnteringPlayMode; // unknown future state: treat as "not editing"
            }
        }

        /// <summary>
        /// Read tools that would report wrong data during a playtest refuse in every state except Editing: while a playtest
        /// starts, runs or ends, WorkshopController.CurrentActiveMod is the playtest's runtime copy of the mod, so asset and
        /// object references of the edit-time objects no longer resolve (they would read as unregistered), and the validator
        /// would see the runtime copies (FindObjectsOfType).
        /// </summary>
        public static void RequireEditMode(EditorSystem ed, string tool)
        {
            if (ed.State != EditorState.Editing)
            {
                throw new RpcException(ErrorCodes.InvalidState,
                    $"a playtest is running (editor state {ed.State}): {tool} is only available in edit mode, because during a playtest references resolve " +
                    "against the playtest's runtime copy of the mod; use get_status, get_log, screenshot or get_hierarchy (edit-time scene), or stop_playtest first");
            }
        }

        // ------------------------------------------------------------------ toolbar (Play / Stop / players / AI Only)

        public static ToolbarWidget Toolbar(EditorSystem ed)
        {
            ToolbarWidget tb = GameReflection.RequireField(typeof(EditorSystem), "m_toolbarWidget").GetValue(ed) as ToolbarWidget;
            if (tb == null)
            {
                throw RpcException.Internal("the editor has no toolbar widget");
            }
            return tb;
        }

        public static int PlayerChoice(ToolbarWidget tb) => (int)GameReflection.RequireField(typeof(ToolbarWidget), "m_playerCountChoice").GetValue(tb);

        public static bool AiOnly(ToolbarWidget tb) => (bool)GameReflection.RequireField(typeof(ToolbarWidget), "m_aiOnly").GetValue(tb);

        /// <summary>ToolbarWidget.IsDoingBackgroundModLoad(): the Play button stays disabled for 1 s after a background (Workshop) mod load.</summary>
        public static float BackgroundLoadTimer(ToolbarWidget tb) => (float)GameReflection.RequireField(typeof(ToolbarWidget), "m_backgroundModLoadTimer").GetValue(tb);

        /// <summary>The dropdown labels as the game has them (cross-check against PlaytestOptions.Labels).</summary>
        public static string[] PlayerLabels(ToolbarWidget tb) => GameReflection.RequireField(typeof(ToolbarWidget), "m_playerCount").GetValue(tb) as string[];

        /// <summary>
        /// What choosing in the dropdown / ticking "AI Only" does: the widget fields (its Draw() rewrites the GameManager values
        /// from them every frame), PlayerPrefs "ModEditorPlayerCountChoice" when the choice changes (the dropdown persists it),
        /// and the GameManager values Draw() derives, set at once so they are right before the next Draw.
        /// </summary>
        public static void ApplyToolbarChoices(ToolbarWidget tb, int choice, bool aiOnly)
        {
            PlaytestOptions.Choice c = PlaytestOptions.Describe(choice);
            int previous = PlayerChoice(tb);
            GameReflection.RequireField(typeof(ToolbarWidget), "m_playerCountChoice").SetValue(tb, choice);
            if (previous != choice)
            {
                PlayerPrefs.SetInt("ModEditorPlayerCountChoice", choice);
            }
            GameReflection.RequireField(typeof(ToolbarWidget), "m_aiOnly").SetValue(tb, aiOnly);
            GameManager.TeamMode = c.TeamMode;
            GameManager.NumTestPlayers = c.NumTestPlayers;
            if (c.TeamComposition != null)
            {
                GameManager.TestPlayersTeamComposition = (int[])c.TeamComposition.Clone();
            }
            GameManager.AIOnlyTest = aiOnly;
        }

        /// <summary>The Stop button's click handler (ToolbarWidget.Draw), verbatim.</summary>
        public static void PressStop()
        {
            Time.timeScale = 1f;
            GameManager.disconnectUserEvent = DisconnectUserEventType.LeftGame;
            NetSystem.OnDisconnect(NetworkDisconnectReason.DisconnectReasonLeft);
        }

        // ------------------------------------------------------------------ validator

        public sealed class ValidationRun
        {
            public List<ValidationIssue> Issues = new List<ValidationIssue>();
            /// <summary>Validator classes in ModValidationSystem's cached list after the run.</summary>
            public List<string> Validators = new List<string>();
            public bool ValidatorListCreatedNow;
        }

        /// <summary>
        /// EditorSystem.ValidateCurrentMod() (the call behind File > Validate Mod, the Play button and the Publish dialog).
        /// It changes nothing in the mod or the editor: the validators only read (FindObjectsOfType, Physics.Raycast,
        /// settings) and their fix actions are stored, never run. The one piece of global state it touches is the static
        /// validator list, created on the first call by an AppDomain type scan; if that scan failed the list would stay
        /// incomplete for the whole session (for the editor's own checks too), so the list is verified afterwards.
        /// </summary>
        public static ValidationRun Validate(EditorSystem ed)
        {
            ValidationRun run = new ValidationRun();
            System.Reflection.FieldInfo listField = GameReflection.RequireField(typeof(ModValidationSystem), "m_validators");
            run.ValidatorListCreatedNow = listField.GetValue(null) == null;
            ModValidationResult result;
            try
            {
                result = ed.ValidateCurrentMod();
            }
            catch (Exception e)
            {
                throw RpcException.Internal($"the editor's mod validator threw {e.GetType().Name}: {e.Message}");
            }
            if (result == null)
            {
                throw new RpcException(ErrorCodes.NoModOpen, "no mod is open in the editor");
            }
            if (listField.GetValue(null) is IList validators)
            {
                foreach (object v in validators)
                {
                    if (v != null)
                    {
                        run.Validators.Add(v.GetType().Name);
                    }
                }
            }
            if (!run.Validators.Contains(nameof(ModMinigameValidator)) || !run.Validators.Contains(nameof(ModBoardValidator)))
            {
                throw RpcException.Internal(
                    $"the editor's validator list is incomplete ({(run.Validators.Count == 0 ? "empty" : string.Join(", ", run.Validators.ToArray()))}); " +
                    "its one-time type scan probably failed. The editor's own Validate / Play / Publish checks use the same list in this session; restart the game");
            }
            Add(run.Issues, result.Errors, "Error");
            Add(run.Issues, result.Warnings, "Warning");
            Add(run.Issues, result.Info, "Information");
            return run;
        }

        private static void Add(List<ValidationIssue> into, List<ModIssueInfo> from, string severity)
        {
            if (from == null)
            {
                return;
            }
            foreach (ModIssueInfo i in from)
            {
                if (i == null)
                {
                    continue;
                }
                into.Add(new ValidationIssue
                {
                    Severity = i.Severity.ToString() ?? severity,
                    Title = i.Title,
                    Description = i.Description,
                    HelpLink = i.HelpLink,
                    Fixable = i.HasFixAction(),
                });
            }
        }

        // ------------------------------------------------------------------ status

        /// <summary>Cheap live info for get_status while a playtest runs (null parts when not available).</summary>
        public static JObject LiveInfo()
        {
            JObject j = new JObject { ["paused"] = Time.timeScale <= 0f };
            MinigameController mg = GameManager.Minigame;
            if (mg != null)
            {
                JObject m = new JObject { ["state"] = mg.State.ToString(), ["rounds"] = mg.rounds };
                object round = GameReflection.FindField(typeof(MinigameController), "round")?.GetValue(mg);
                if (round is int r)
                {
                    m["round"] = r + 1; // 1-based, as the round UI shows it
                }
                j["minigame"] = m;
            }
            GameBoardController board = GameManager.Board;
            if (board != null)
            {
                j["board"] = new JObject { ["state"] = board.BoardState.ToString() };
            }
            return j;
        }
    }

    /// <summary>IPlaytestHost over the live editor for one start/stop call.</summary>
    internal sealed class PlaytestHost : IPlaytestHost
    {
        private readonly PummelMod m_mod;
        private readonly Action m_enter;

        public PlaytestHost(PummelMod mod, Action enter)
        {
            m_mod = mod;
            m_enter = enter;
        }

        public PlayState State => PlaytestAccess.StateOf(EditorAccess.EditorOrNull());

        public RpcException CheckSameMod()
        {
            EditorSystem ed = EditorAccess.EditorOrNull();
            if (ed == null)
            {
                return new RpcException(ErrorCodes.EditorNotOpen, "the mod editor was closed");
            }
            // The editor instance is not compared: only the open mod identifies the playtest (the mod object stays the
            // same through Entering/Play/Exiting; a different or no mod means it was closed or switched).
            if (!ReferenceEquals(ed.CurrentMod, m_mod) || m_mod == null)
            {
                return new RpcException(ErrorCodes.InvalidState, "the open mod changed (closed or switched)");
            }
            return null;
        }

        public void EnterPlayMode()
        {
            if (m_enter == null)
            {
                throw RpcException.Internal("no start action");
            }
            m_enter();
        }

        public void PressStop() => PlaytestAccess.PressStop();

        public bool EditSceneRestored()
        {
            EditorSystem ed = EditorAccess.EditorOrNull();
            // EditorSystem.SetEditorState(Editing) -> SetActive(true) restores the stashed active flags synchronously; the
            // hierarchy root is active again unless a prefab is being edited (never the case after Play, which leaves it).
            return ed != null && ed.State == EditorState.Editing && ed.m_hierarchyRoot != null
                   && (ed.m_hierarchyRoot.gameObject.activeSelf || ed.CurrentPrefab != null);
        }
    }
}
