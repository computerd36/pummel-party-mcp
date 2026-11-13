using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;

namespace PummelPartyMcp.Tools
{
    /// <summary>validate_mod (read); get_playtest_state / get_playtest_events (read); start_playtest (write, OpenMod); stop_playtest (write, OpenMod, allowed in play mode).</summary>
    internal static class PlaytestTools
    {
        /// <summary>Queueing + validation + Play + wait for play mode (scene and mod load). Commit grace (4 s) comes on top.</summary>
        public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Queueing + (wait for play mode) + Stop + wait for edit mode.</summary>
        public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

        private sealed class StartArgs
        {
            public int? Players;
            public bool? AiOnly;
            public bool ValidateFirst = true;
        }

        public static void Register(ToolRegistry registry, EditServices services, PluginRuntimeInfo info)
        {
            registry.Register(ToolDefinition.Read("validate_mod", ctx => ctx.OnMainThread(ValidateMod)));
            registry.Register(ToolDefinition.Read("get_playtest_state", ctx => ctx.OnMainThread(() => PlaytestState(services))));
            registry.Register(ToolDefinition.Read("get_playtest_events", ctx => PlaytestEvents(ctx, services.Feed, info)));

            registry.Register(ToolDefinition.Write("start_playtest", WriteGuard.OpenMod(),
                ctx =>
                {
                    StartArgs a = new StartArgs
                    {
                        Players = PlaytestOptions.ParsePlayers(ctx.Params["players"]),
                        ValidateFirst = ctx.GetBool("validateFirst", true),
                    };
                    JToken ai = ctx.Params["aiOnly"];
                    if (ai != null && ai.Type != JTokenType.Null)
                    {
                        a.AiOnly = ctx.GetBool("aiOnly", false);
                    }
                    return a;
                },
                inv => Start(inv, services), StartTimeout));

            registry.Register(ToolDefinition.Write("stop_playtest", WriteGuard.OpenMod(allowPlayMode: true), null,
                inv => Stop(inv, services), StopTimeout));
        }

        // ------------------------------------------------------------------ get_playtest_state / get_playtest_events

        private static JObject PlaytestState(EditServices services)
        {
            EditorSystem ed = EditorAccess.EditorOrNull();
            RpcException gate = PlaytestGate.CheckRunning(PlaytestAccess.StateOf(ed));
            if (gate != null)
            {
                throw gate;
            }
            services.Observe(); // record the newest frame first so the event counters match the state
            return PlaytestStateReader.Read(ed, services.Feed);
        }

        /// <summary>Answered on the HTTP thread (the ring is thread-safe): works while the game is busy and after the playtest ended.</summary>
        private static JObject PlaytestEvents(ToolContext ctx, PlaytestFeed feed, PluginRuntimeInfo info)
        {
            int max = ctx.GetInt("max", 100, 1, PlaytestFeed.DefaultCapacity);
            long? sinceId = ctx.GetLong("sinceId", 0, long.MaxValue);
            return PlaytestEventJson.Result(feed, max, sinceId, PummelPartyMcp.Hooks.PlaytestHooks.LogicEvents, info?.SessionId);
        }

        // ------------------------------------------------------------------ validate_mod

        private static JObject ValidateMod()
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            PlaytestAccess.RequireEditMode(ed, "validate_mod");
            PlaytestAccess.ValidationRun run = PlaytestAccess.Validate(ed);
            JObject summary = ValidationReport.Summary(run.Issues);
            bool valid = (bool)summary["valid"];
            bool playable = mod.ModType != PummelModType.BoardSpace;
            JObject result = new JObject
            {
                ["mod"] = EditorAccess.ModName(mod),
                ["modType"] = mod.ModType.ToString(),
                ["valid"] = valid,
                ["canPlaytest"] = valid && playable,
                ["canPublishPerValidator"] = valid,
            };
            foreach (JProperty p in summary.Properties())
            {
                if (p.Name != "valid")
                {
                    result[p.Name] = p.Value;
                }
            }
            result["validators"] = new JArray(run.Validators.ToArray());
            if (!playable)
            {
                result["canPlaytestReason"] = "BoardSpace mods have no playtest (the editor would hide itself without opening a scene); start_playtest refuses them";
            }
            if (ed.CurrentPrefab != null)
            {
                result["note"] = $"a prefab ('{ed.CurrentPrefab.name}') is being edited, so the scene is hidden and the validator does not see scene objects; " +
                                 "the Play button leaves the prefab first. Call close_prefab for a meaningful result.";
            }
            else if (run.ValidatorListCreatedNow)
            {
                result["note"] = "this was the session's first validation: the editor built its validator list now (as on its own first Validate)";
            }
            return result;
        }

        // ------------------------------------------------------------------ start_playtest

        private static void Start(WriteInvocation inv, EditServices services)
        {
            StartArgs args = inv.ArgsAs<StartArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            ModTools.RefuseIfLifecyclePendingPublic();
            PummelMod mod = ed.CurrentMod; // guard: loaded test mod, state Editing

            // Preconditions the Play button has (disabled states, the popup blocking clicks) plus the editor's hazards.
            EditorPopup popup = EditorPopupManager.CurrentPopup;
            if (popup != null)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"an editor dialog is open ('{popup.GetTitle()}'); close it first");
            }
            if (ed.CurrentPrefab != null)
            {
                // The Play button saves the open prefab and returns to the scene (CurrentPrefab = null) before validating;
                // the plugin never saves a prefab implicitly.
                throw new RpcException(ErrorCodes.InvalidState,
                    $"a prefab ('{ed.CurrentPrefab.name}') is being edited; the Play button would save it and return to the scene. Call close_prefab first (or use Return to Hierarchy in the editor)");
            }
            if (mod.ModType == PummelModType.BoardSpace)
            {
                throw new RpcException(ErrorCodes.InvalidState,
                    "BoardSpace mods cannot be playtested: the editor has no play scene for them (it would hide itself and stay in EnteringPlayMode)");
            }
            if (WorkshopController.Instance == null)
            {
                throw RpcException.Internal("the WorkshopController is not available");
            }
            if (WorkshopController.CurrentBackgroundLoadMod != null)
            {
                // SetEditorState(EnteringPlayMode) would set the state and then do nothing: the editor would be stuck.
                throw new RpcException(ErrorCodes.InvalidState, "Subscribed mods are being installed in the background. Please wait... (the Play button is disabled); retry in a few seconds");
            }
            ToolbarWidget toolbar = PlaytestAccess.Toolbar(ed);
            if (PlaytestAccess.BackgroundLoadTimer(toolbar) > 0f)
            {
                throw new RpcException(ErrorCodes.InvalidState, "Subscribed mods were being installed in the background (the Play button is disabled for 1 s afterwards); retry shortly");
            }
            if (WorkshopController.CurrentActiveMod == null)
            {
                // SetEditorState(EnteringPlayMode) dereferences it right after switching the state.
                throw new RpcException(ErrorCodes.InvalidState, "the game has no active mod (WorkshopController.CurrentActiveMod is null); reopen the mod");
            }

            // The Play button validates first and shows the validator popup on errors ("Run With Errors" = validateFirst false).
            PlaytestAccess.ValidationRun run = PlaytestAccess.Validate(ed);
            JObject validation = ValidationReport.Summary(run.Issues);
            if (args.ValidateFirst && !(bool)validation["valid"])
            {
                throw new RpcException(ErrorCodes.InvalidState,
                    $"the mod does not pass the editor's validator ({ValidationReport.ErrorSummary(run.Issues)}); nothing was changed. " +
                    "Fix the errors (see validate_mod), or pass validateFirst: false to play anyway like the validator popup's \"Run With Errors\"",
                    new JObject { ["validation"] = validation });
            }

            int choice = args.Players ?? PlaytestAccess.PlayerChoice(toolbar);
            if (choice < 0 || choice >= PlaytestOptions.Labels.Length)
            {
                choice = PlaytestOptions.DefaultChoice;
            }
            bool aiOnly = args.AiOnly ?? PlaytestAccess.AiOnly(toolbar);
            string modName = EditorAccess.ModName(mod);
            IPluginLog log = services.Log;

            PlaytestHost host = new PlaytestHost(mod, () =>
            {
                // Play button: (CurrentPrefab = null: refused above), ValidateCurrentMod (done above),
                // SetEditorState(EnteringPlayMode): saves the mod (PummelMod.Save), loads the playtest copy, opens the play scene.
                PlaytestAccess.ApplyToolbarChoices(toolbar, choice, aiOnly);
                services.Playtest.MarkPluginStart();
                log.Info($"start_playtest: entering play mode for '{modName}' ({PlaytestOptions.Labels[choice]}, aiOnly {aiOnly}); the editor saves the mod now");
                ed.SetEditorState(EditorState.EnteringPlayMode);
                if (ed.State == EditorState.Editing)
                {
                    services.Playtest.CancelPluginStart();
                }
            });
            PlaytestFlow flow = PlaytestFlow.ForStart(host, inv.Completion, outcome =>
            {
                JObject r = new JObject
                {
                    ["playing"] = true,
                    ["saved"] = true,
                    ["state"] = outcome.FinalState.ToString(),
                    ["mod"] = modName,
                    ["modType"] = mod.ModType.ToString(),
                };
                foreach (JProperty p in PlaytestOptions.ToJson(PlaytestOptions.Describe(choice), aiOnly).Properties())
                {
                    r[p.Name] = p.Value;
                }
                r["validated"] = args.ValidateFirst;
                r["validation"] = validation;
                r["secondsToPlayMode"] = Math.Round(outcome.Elapsed.TotalSeconds, 1);
                if (outcome.SaveReport != null)
                {
                    r["prefabsVerified"] = outcome.SaveReport.Verified;
                    JArray prefabWarnings = new JArray(outcome.SaveReport.Outcomes.SelectMany(o => o.Warnings.Select(w => (JToken)(o.Name + ": " + w))).ToArray());
                    if (prefabWarnings.Count > 0)
                    {
                        r["prefabWarnings"] = prefabWarnings;
                    }
                }
                r["note"] = "Entering play mode saved the mod's files (PummelMod.Save: Meta.json, WorkshopItem.json, MainScene.scene, ModSettings.json, " +
                            "Lighting/PostProcessing, the minigame definition or map details, asset .pmeta files and every .pfab/.pmat; the prefab files were " +
                            "dry-run before and verified after the save like save_mod (prefabsVerified); no Preview.jpg; the dirty flag and " +
                            "get_status.hasUnsavedEdits are unchanged). " +
                            (aiOnly ? "AI Only: every player is an AI, the rounds run by themselves. " : "Player 1 is controlled from the keyboard/gamepad by a human; the plugin sends no input. ") +
                            "Edit tools refuse until stop_playtest; observe with get_status, get_log and screenshot.";
                return r;
            });
            // C2 + C1: the Play call saves the mod (PummelMod.Save rewrites every .pfab with FileMode.Create): the same link pre-check,
            // dry run and safety copy as save_mod before it, verification and restore after it (in the same dispatched step).
            flow.SaveProtection = new PrefabSaveProtection(() =>
            {
                AssetAccess.RequireModSavable(mod, includePreview: false, "start_playtest (entering play mode saves the mod)");
                string assetsFull = AssetAccess.AssetsFolderOf(mod);
                return PrefabAccess.SavedPrefabs(mod).Select(p => PrefabAccess.Prepare(p, assetsFull)).ToList();
            });
            if (!flow.Start())
            {
                services.Poller.Add(flow.Tick);
            }
        }

        // ------------------------------------------------------------------ stop_playtest

        private static void Stop(WriteInvocation inv, EditServices services)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = ed.CurrentMod; // guard: loaded test mod (any editor state)
            PlaytestClock clock = services.Playtest;
            IPluginLog log = services.Log;
            PlaytestHost host = new PlaytestHost(mod, null);
            if (host.State == PlayState.PlayMode)
            {
                log.Info($"stop_playtest: pressing Stop for '{EditorAccess.ModName(mod)}'");
            }
            PlaytestFlow flow = PlaytestFlow.ForStop(host, inv.Completion, outcome =>
            {
                services.Observe(); // record the end in the playtest clock before reading it
                JObject r = new JObject
                {
                    ["stopped"] = true,
                    ["alreadyStopped"] = outcome.AlreadyStopped,
                    ["state"] = outcome.FinalState.ToString(),
                };
                if (outcome.AlreadyStopping)
                {
                    r["alreadyStopping"] = true;
                }
                if (!outcome.AlreadyStopped)
                {
                    double? played = clock.PlaySeconds(DateTime.UtcNow);
                    r["playedSeconds"] = played.HasValue ? new JValue(played.Value) : JValue.CreateNull();
                    r["secondsToEditMode"] = Math.Round(outcome.Elapsed.TotalSeconds, 1);
                }
                r["note"] = outcome.AlreadyStopped
                    ? "no playtest was running; nothing was changed"
                    : "back in edit mode: the edit-time scene was restored as it was before Play (the playtest ran on a separate copy loaded from the saved files; " +
                      "nothing was saved when stopping)";
                return r;
            });
            if (!flow.Start())
            {
                services.Poller.Add(flow.Tick);
            }
        }

        internal static JObject StatusJson(EditorSystem ed, EditServices services)
        {
            if (ed == null)
            {
                return null;
            }
            PlaytestClock clock = services?.Playtest;
            PlayState state = PlaytestAccess.StateOf(ed);
            DateTime now = DateTime.UtcNow;
            JObject j = new JObject
            {
                ["state"] = state.ToString(),
                ["running"] = state != PlayState.Editing,
            };
            if (clock != null)
            {
                j["startedAt"] = Iso(clock.StartedAt);
                j["playModeSince"] = Iso(clock.PlayModeSince);
                j["endedAt"] = Iso(clock.EndedAt);
                double? played = clock.PlaySeconds(now);
                j["playSeconds"] = played.HasValue ? new JValue(played.Value) : JValue.CreateNull();
                j["startedByPlugin"] = clock.StartedAt != null && clock.StartedByPlugin;
            }
            try
            {
                ToolbarWidget tb = PlaytestAccess.Toolbar(ed);
                int choice = PlaytestAccess.PlayerChoice(tb);
                if (choice >= 0 && choice < PlaytestOptions.Labels.Length)
                {
                    foreach (JProperty p in PlaytestOptions.ToJson(PlaytestOptions.Describe(choice), PlaytestAccess.AiOnly(tb)).Properties())
                    {
                        j[p.Name] = p.Value;
                    }
                }
            }
            catch (RpcException)
            {
                // toolbar fields unreadable (game changed): leave the choice out
            }
            if (services?.Feed != null)
            {
                int? alive = services.Feed.AlivePlayers;
                j["eventCount"] = services.Feed.EventCount;
                j["lastEventId"] = services.Feed.LastEventId;
                j["alivePlayers"] = alive.HasValue ? new JValue(alive.Value) : JValue.CreateNull();
            }
            if (state != PlayState.Editing)
            {
                foreach (JProperty p in PlaytestAccess.LiveInfo().Properties())
                {
                    j[p.Name] = p.Value;
                }
            }
            return j;
        }

        private static JToken Iso(DateTime? t) =>
            t.HasValue ? new JValue(t.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)) : JValue.CreateNull();
    }
}
