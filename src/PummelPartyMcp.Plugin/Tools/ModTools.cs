using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;

namespace PummelPartyMcp.Tools
{
    /// <summary>list_mods (read); create_test_mod, open_test_mod (write, Lifecycle); save_mod (write, OpenMod).</summary>
    internal static class ModTools
    {
        /// <summary>The tool's own limit for an asynchronous load (create/open).</summary>
        public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Dispatcher limit for create/open: a little above <see cref="LoadTimeout"/> so the tool's own TIMEOUT message wins.</summary>
        private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(35);

        /// <summary>Create/open refuse to start (TIMEOUT, nothing changed) with less than this left until the deadline.</summary>
        private static readonly TimeSpan MinLoadBudget = TimeSpan.FromSeconds(10);

        /// <summary>The load wait ends this long before the deadline, so the tool's own reply wins.</summary>
        private static readonly TimeSpan LoadReplyMargin = TimeSpan.FromSeconds(1);

        /// <summary>save_mod refuses to start (TIMEOUT, nothing saved) with less than this left until the deadline.</summary>
        private static readonly TimeSpan SaveBudget = CommitBudgets.Save;

        /// <summary>A create/open is waiting for its load (main thread only). Other writes refuse meanwhile.</summary>
        private static bool s_lifecyclePending;

        private sealed class CreateArgs
        {
            public string Name;
            public PummelModType Type;
            public bool DiscardUnsaved;
        }

        private sealed class OpenArgs
        {
            public string Name;
            public bool DiscardUnsaved;
        }

        private static EditServices s_services;

        public static void Register(ToolRegistry registry, FramePoller poller, IPluginLog log, EditServices services)
        {
            s_services = services;
            registry.Register(ToolDefinition.Read("list_mods", ctx => ctx.OnMainThread(ListMods)));

            registry.Register(ToolDefinition.Write("create_test_mod",
                WriteGuard.Lifecycle(a => ((CreateArgs)a).Name),
                ctx =>
                {
                    string type = ctx.GetString("type") ?? "Minigame";
                    PummelModType t;
                    if (type == "Minigame")
                    {
                        t = PummelModType.Minigame;
                    }
                    else if (type == "Board")
                    {
                        t = PummelModType.Board;
                    }
                    else
                    {
                        throw RpcException.InvalidParams("'type' must be \"Minigame\" or \"Board\"");
                    }
                    return new CreateArgs { Name = ctx.GetString("name", required: true), Type = t, DiscardUnsaved = ctx.GetBool("discardUnsaved", false) };
                },
                inv => CreateTestMod(inv, poller, log),
                DispatchTimeout));

            registry.Register(ToolDefinition.Write("open_test_mod",
                WriteGuard.Lifecycle(a => ((OpenArgs)a).Name),
                ctx => new OpenArgs { Name = ctx.GetString("name", required: true), DiscardUnsaved = ctx.GetBool("discardUnsaved", false) },
                inv => OpenTestMod(inv, poller, log),
                DispatchTimeout));

            registry.Register(ToolDefinition.Write("save_mod", WriteGuard.OpenMod(), null, inv => SaveMod(inv, log), CommitBudgets.SaveTimeout));
        }

        // ------------------------------------------------------------------ list_mods

        private static JArray ListMods()
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            EditorAvailableModManager manager = ed.GetAvailableModManager();
            if (manager == null)
            {
                throw RpcException.Internal("the editor has no available-mod manager");
            }
            // The editor's cached list (scanned when the editor opened, updated by the editor's own create/rename).
            // ReadListOfAvailableMods is not called: it rebuilds the editor's list.
            PummelMod open = ed.CurrentMod;
            string openFolder = open == null ? null : EditorAccess.ModFolderName(open);
            List<JObject> rows = new List<JObject>();
            foreach (EditorAvailableMod m in manager.GetAvailableMods().ToArray())
            {
                string folder = LeafName(m.ModFolderName);
                rows.Add(new JObject
                {
                    ["name"] = m.ModName,
                    ["folderName"] = folder,
                    ["type"] = m.ModType.ToString(),
                    ["isTestMod"] = TestModName.IsValid(m.ModName) && TestModName.IsValid(folder),
                    ["isOpen"] = openFolder != null && folder != null && string.Equals(folder, openFolder, StringComparison.OrdinalIgnoreCase),
                });
            }
            return new JArray(rows.OrderBy(r => (string)r["folderName"] ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => (string)r["name"] ?? "", StringComparer.Ordinal).ToArray());
        }

        // ------------------------------------------------------------------ create_test_mod

        private static void CreateTestMod(WriteInvocation inv, FramePoller poller, IPluginLog log)
        {
            CreateArgs args = inv.ArgsAs<CreateArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            RefuseIfLifecyclePending();
            string root = RequireModFolder();
            EditorAvailableModManager manager = ed.GetAvailableModManager();
            RefuseIfBrokenModList(manager);
            CheckCanSwitchAway(ed, args.DiscardUnsaved);

            // The New Mod popup validates with EditorPopup.IsValidModnameInput (filename rules + no Meta name collision);
            // EditorSystem.CreateNewMod checks nothing, so all of it is repeated here, plus a folder collision check.
            if (!EditorPopup.IsValidFilenameInput(args.Name))
            {
                throw RpcException.InvalidParams($"'{args.Name}' is not a valid mod folder name");
            }
            string path = Path.Combine(root, args.Name);
            foreach (string dir in Directory.GetDirectories(root))
            {
                if (string.Equals(Path.GetFileName(dir), args.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RpcException(ErrorCodes.AlreadyExists, $"a mod folder named '{Path.GetFileName(dir)}' already exists");
                }
            }
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new RpcException(ErrorCodes.AlreadyExists, $"'{args.Name}' already exists in the mod folder");
            }
            foreach (EditorAvailableMod m in manager.GetAvailableMods())
            {
                if (m.ModName != null && string.Equals(m.ModName, args.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RpcException(ErrorCodes.AlreadyExists, $"a mod named '{m.ModName}' already exists (folder '{LeafName(m.ModFolderName)}')");
                }
            }
            // The cached list can be stale; also compare against the Meta.json names on disk (read only).
            foreach (string dir in Directory.GetDirectories(root))
            {
                string metaName = ReadMetaName(dir);
                if (metaName != null && string.Equals(metaName, args.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RpcException(ErrorCodes.AlreadyExists, $"a mod named '{metaName}' already exists (folder '{Path.GetFileName(dir)}')");
                }
            }

            // Listed BEFORE CreateNewMod (the popup does it after): CreateNewMod -> SaveCurrentMod(delayed) ->
            // UpdateLoadedModLastModifiedTime looks the mod up by name and, if it is missing, inserts an entry with a
            // null name that later breaks the list's Sort / DoesModWithNameAlreadyExist.
            TimeSpan loadLimit = CommitLifecycle(inv);
            EditorAvailableMod entry = new EditorAvailableMod(args.Name, args.Name, args.Type, DateTime.Now);
            manager.AddAvailableMod(entry);
            try
            {
                ed.CreateNewMod(path, args.Type, null); // empty mod, no template
            }
            catch
            {
                if (!Directory.Exists(path))
                {
                    manager.GetAvailableMods().Remove(entry);
                }
                throw;
            }
            PummelMod pm = ed.CurrentMod;
            if (pm == null)
            {
                throw RpcException.Internal("CreateNewMod did not open the new mod");
            }
            log.Info($"create_test_mod: creating '{args.Name}' ({args.Type})");
            string metaFile = Path.Combine(path, "Data", "Meta.json");
            string workshopFile = Path.Combine(path, "Data", "WorkshopItem.json");

            // The empty-mod load is synchronous, but the files are written by a delayed save one or more frames later.
            WaitForLoad(inv, poller, pm, loadLimit, "was created", extraReady: () => File.Exists(metaFile) && File.Exists(workshopFile), onReady: () =>
            {
                if (!string.Equals(EditorAccess.ModFolderName(pm), args.Name, StringComparison.Ordinal) || !TestModName.IsValid(EditorAccess.ModName(pm)))
                {
                    throw RpcException.Internal("the created mod does not have the expected name");
                }
                GameEditorGuardHost.EnforceDefaultWorkshopId(pm, log);
                return ModSummary(pm);
            });
        }

        // ------------------------------------------------------------------ open_test_mod

        private static void OpenTestMod(WriteInvocation inv, FramePoller poller, IPluginLog log)
        {
            OpenArgs args = inv.ArgsAs<OpenArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            RefuseIfLifecyclePending();

            PummelMod current = ed.CurrentMod;
            if (current != null && current.IsLoaded && string.Equals(EditorAccess.ModFolderName(current), args.Name, StringComparison.Ordinal))
            {
                // The guard already verified that the open mod is a test mod and applied the id hygiene.
                JObject same = ModSummary(current);
                same["alreadyOpen"] = true;
                inv.Complete(same);
                return;
            }

            string root = RequireModFolder();
            string folder = null;
            string caseOnlyMatch = null;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string leaf = Path.GetFileName(dir);
                if (string.Equals(leaf, args.Name, StringComparison.Ordinal))
                {
                    folder = dir;
                    break;
                }
                if (string.Equals(leaf, args.Name, StringComparison.OrdinalIgnoreCase))
                {
                    caseOnlyMatch = leaf;
                }
            }
            if (folder == null)
            {
                throw RpcException.NotFound(caseOnlyMatch != null
                    ? $"no mod folder '{args.Name}' (folder names are matched exactly; found '{caseOnlyMatch}')"
                    : $"no mod folder '{args.Name}'");
            }
            if (!File.Exists(Path.Combine(folder, "Data", "Meta.json")))
            {
                throw RpcException.NotFound($"'{args.Name}' is not a mod folder (no Data/Meta.json)");
            }
            string metaName = ReadMetaName(folder);
            if (metaName == null)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"Data/Meta.json of '{args.Name}' cannot be read");
            }
            string why = TestModName.Validate(metaName);
            if (why != null)
            {
                throw new RpcException(ErrorCodes.GuardNotTestMod,
                    $"folder '{args.Name}' contains a mod named '{metaName}', which is not a test-mod name: it {why}");
            }
            RefuseIfBrokenModList(ed.GetAvailableModManager());
            CheckCanSwitchAway(ed, args.DiscardUnsaved);
            SandboxPath.RequireOpenable(folder); // C1: loading writes missing .pmeta files; never through a link

            // Same call the Open Mod dialog ends in (OpenModWithPopupPrompt -> OpenMod) without its unsaved-changes
            // popup, which CheckCanSwitchAway replaces.
            TimeSpan loadLimit = CommitLifecycle(inv);
            // M1: an import interrupted by a crash is not decoded again. After the commit point: it may rename files, and a call that
            // fails before the commit reports "nothing was changed". Never throws; only OpenMod below depends on it.
            foreach (string line in ImportJournal.Recover(folder))
            {
                log.Warning("open_test_mod: import recovery: " + line);
            }
            ed.OpenMod(folder);
            PummelMod pm = ed.CurrentMod;
            if (pm == null)
            {
                throw RpcException.Internal("OpenMod did not set the current mod");
            }
            log.Info($"open_test_mod: opening '{args.Name}'");
            WaitForLoad(inv, poller, pm, loadLimit, "is being opened", extraReady: null, onReady: () =>
            {
                if (!TestModName.IsValid(EditorAccess.ModName(pm)) || !TestModName.IsValid(EditorAccess.ModFolderName(pm)))
                {
                    throw new RpcException(ErrorCodes.GuardNotTestMod, "the loaded mod is not a test mod (its Meta.json name changed while loading?)");
                }
                GameEditorGuardHost.EnforceDefaultWorkshopId(pm, log);
                JObject result = ModSummary(pm);
                result["alreadyOpen"] = false;
                return result;
            });
        }

        // ------------------------------------------------------------------ save_mod

        private static void SaveMod(WriteInvocation inv, IPluginLog log)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            RefuseIfLifecyclePending();
            PummelMod mod = ed.CurrentMod; // guard: loaded test mod, not loading, state Editing
            EditorAvailableModManager manager = ed.GetAvailableModManager();
            RefuseIfBrokenModList(manager);

            // PummelMod.Save rewrites every prefab in place (FileMode.Create, serialising straight into the file): one that throws while
            // being written leaves a truncated .pfab, and a truncated prefab makes the whole mod fail to load. So: dry-run every prefab
            // in memory first (refuse, nothing saved, when one cannot be serialised), keep a safety copy of each file, and verify them all
            // after the save; any that fails is restored (the save's other files stay saved).
            string assetsFull = AssetAccess.AssetsFolderOf(mod);
            // C1: every file the save writes (Data files, Preview.jpg, every .pmeta/.pmat/.pfab); one link refuses the whole save
            AssetAccess.RequireModSavable(mod, includePreview: true, "save_mod");
            List<PrefabFileJob> jobs = new List<PrefabFileJob>();
            foreach (PummelPrefab p in PrefabAccess.SavedPrefabs(mod))
            {
                jobs.Add(PrefabAccess.Prepare(p, assetsFull));
            }

            inv.Commit(SaveBudget); // TIMEOUT (nothing saved) if the caller gave up or too little time is left
            EnsureListed(manager, mod);
            GameEditorGuardHost.EnforceDefaultWorkshopId(mod, log);

            // Saving while a prefab is being edited is what Ctrl+S does as well: the scene serializer stores
            // activeSelf, so the (deactivated) hierarchy root does not change the saved scene; the edited prefab is
            // saved with the other assets. Only the hierarchy's dirty flag stays set and Preview.jpg shows the prefab view.
            Exception saveError = null;
            try
            {
                ed.SaveCurrentMod();
            }
            catch (Exception e)
            {
                saveError = e;
            }
            List<PrefabFileOutcome> outcomes = jobs.Select(PrefabWriteGuard.Finish).ToList();
            List<PrefabFileOutcome> failed = outcomes.Where(o => !o.Ok).ToList();
            if (saveError != null || failed.Count > 0)
            {
                s_services?.MarkEdit(mod, "save_mod (incomplete)"); // the mod must not look saved
                log.Error("save_mod: " + (saveError?.Message ?? "prefab verification failed"));
                throw new RpcException(ErrorCodes.Internal,
                    (saveError != null ? $"the editor's save threw ({saveError.GetType().Name}: {saveError.Message}); " : "the save finished, but ") +
                    (failed.Count > 0 ? "these prefab files failed the check and were put back as they were before the save: " + PrefabWriteGuard.Describe(failed) + ". " : string.Empty) +
                    "The mod is NOT fully saved (other files may have been written); the in-memory edits are intact, fix the cause and call save_mod again",
                    new JObject { ["prefabs"] = new JArray(outcomes.Select(o => (JToken)o.ToJson()).ToArray()) });
            }
            s_services?.OnSaved(); // also done by the SaveCurrentMod postfix; idempotent
            log.Info($"save_mod: saved '{EditorAccess.ModFolderName(mod)}'");
            bool? dirty = EditorAccess.IsDirty(ed);
            bool? unsaved = s_services?.HasUnsavedEdits(ed);
            inv.Complete(new JObject
            {
                ["saved"] = true,
                ["name"] = EditorAccess.ModName(mod),
                ["isDirty"] = dirty.HasValue ? new JValue(dirty.Value) : JValue.CreateNull(),
                ["hasUnsavedEdits"] = unsaved.HasValue ? new JValue(unsaved.Value) : JValue.CreateNull(),
                ["prefabsVerified"] = outcomes.Count,
            });
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Commit point of create/open (right before the first change): needs <see cref="MinLoadBudget"/> left until the
        /// caller's deadline, else TIMEOUT with nothing changed. Returns the load limit: <see cref="LoadTimeout"/>, shortened
        /// so the tool's own reply is sent before the deadline when the call waited in the write queue.
        /// </summary>
        private static TimeSpan CommitLifecycle(WriteInvocation inv)
        {
            inv.Commit(MinLoadBudget);
            TimeSpan left = inv.Completion.Remaining - LoadReplyMargin;
            return left < LoadTimeout ? left : LoadTimeout;
        }

        /// <summary>
        /// Completes the invocation from a later frame once <paramref name="pm"/> is the loaded current mod (and
        /// <paramref name="extraReady"/> holds), or fails it on load failure / after <paramref name="limit"/>.
        /// </summary>
        private static void WaitForLoad(WriteInvocation inv, FramePoller poller, PummelMod pm, TimeSpan limit, string what, Func<bool> extraReady, Func<JObject> onReady)
        {
            Stopwatch sw = Stopwatch.StartNew();
            s_lifecyclePending = true;
            poller.Add(() =>
            {
                try
                {
                    // No cancel branch: CommitLifecycle committed before the load started, and a committed call can no
                    // longer be cancelled by the dispatcher (see IWorkCompletion.TryCommit), so IsCancelled is never true here.
                    EditorSystem ed = EditorAccess.EditorOrNull();
                    if (ed == null)
                    {
                        return Finish(inv, new RpcException(ErrorCodes.EditorNotOpen, "the editor was closed while the mod was loading"));
                    }
                    if (!ed.IsLoading)
                    {
                        if (ed.CurrentMod != pm)
                        {
                            if (ed.CurrentMod != null)
                            {
                                return Finish(inv, new RpcException(ErrorCodes.InvalidState,
                                    "the open mod changed while waiting for the load (another mod was opened by hand); check get_status"));
                            }
                            return Finish(inv, RpcException.Internal($"the mod failed to load ({pm.lastResult}); see the editor's popup and get_log"));
                        }
                        if (pm.IsLoaded && (extraReady == null || extraReady()))
                        {
                            s_lifecyclePending = false;
                            inv.Complete(onReady());
                            return true;
                        }
                    }
                    if (sw.Elapsed >= limit)
                    {
                        return Finish(inv, new RpcException(ErrorCodes.Timeout,
                            $"the mod {what}, but it did not finish loading within {limit.TotalSeconds:0} s (it may still finish; check get_status before retrying)"));
                    }
                    return false;
                }
                catch (Exception e)
                {
                    return Finish(inv, e);
                }
            });
        }

        private static bool Finish(WriteInvocation inv, Exception error)
        {
            s_lifecyclePending = false;
            inv.Fail(error);
            return true;
        }

        private static void RefuseIfLifecyclePending()
        {
            if (s_lifecyclePending)
            {
                throw new RpcException(ErrorCodes.BusyLoading, "a create_test_mod/open_test_mod call is still waiting for its mod to load");
            }
        }

        internal static void RefuseIfLifecyclePendingPublic() => RefuseIfLifecyclePending();

        /// <summary>
        /// Rules for leaving the open mod (create/open). Mirrors the editor's unsaved-changes popup, but uses the plugin's
        /// own unsaved-edits tracking (EditServices.HasUnsavedEdits: undo-stack changes and plugin edits since load/save,
        /// ignoring the editor's initial snapshot after a load) instead of EditorSystem.IsModDirty, which is already true
        /// for an untouched, freshly opened mod. Falls back to the editor's flag when the tracker has no baseline.
        /// </summary>
        private static void CheckCanSwitchAway(EditorSystem ed, bool discardUnsaved)
        {
            EditorPopup popup = EditorPopupManager.CurrentPopup;
            if (popup != null)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"an editor dialog is open ('{popup.GetTitle()}'); close it first");
            }
            PummelMod current = ed.CurrentMod;
            if (current == null)
            {
                return;
            }
            if (ed.CurrentPrefab != null)
            {
                // Leaving the prefab context (SetCurrentMod -> CurrentPrefab = null) would save that prefab.
                throw new RpcException(ErrorCodes.InvalidState,
                    $"a prefab ('{ed.CurrentPrefab.name}') is being edited in '{EditorAccess.ModFolderName(current)}'; call close_prefab first (or use Return to Hierarchy in the editor)");
            }
            bool? dirty = s_services != null ? s_services.HasUnsavedEdits(ed) : EditorAccess.IsDirty(ed);
            if (dirty != false && !discardUnsaved)
            {
                string source = s_services?.Source(ed) == "tracker" ? "edits since it was opened or saved" : "editor dirty flag";
                string why = s_services?.Tracker.DirtyReason;
                throw new RpcException(ErrorCodes.InvalidState,
                    $"the open mod '{EditorAccess.ModFolderName(current)}' has unsaved changes ({source}{(dirty == null ? " unreadable" : "")}{(why != null && source != "editor dirty flag" ? ": " + why : "")}); " +
                    "call save_mod first, or pass discardUnsaved: true to drop them");
            }
        }

        /// <summary>An entry with a null name (left by UpdateLoadedModLastModifiedTime) makes the list's Sort throw.</summary>
        private static void RefuseIfBrokenModList(EditorAvailableModManager manager)
        {
            if (manager == null)
            {
                throw RpcException.Internal("the editor has no available-mod manager");
            }
            if (manager.GetAvailableMods().Any(m => m.ModName == null))
            {
                throw new RpcException(ErrorCodes.InvalidState,
                    "the editor's mod list contains a broken entry (a mod saved under a name that was not in the list); exit and re-enter the mod editor first");
            }
        }

        /// <summary>
        /// SaveCurrentMod updates the list entry whose name equals the mod's Meta name; if there is none it inserts a
        /// broken entry. Add the entry the editor would have after a rescan.
        /// </summary>
        private static void EnsureListed(EditorAvailableModManager manager, PummelMod mod)
        {
            string name = mod.GetModName();
            if (manager.GetAvailableMods().Any(m => m.ModName == name))
            {
                return;
            }
            manager.AddAvailableMod(new EditorAvailableMod(name, mod.GetModPath(), mod.ModType, DateTime.Now));
        }

        private static string RequireModFolder()
        {
            string root = EditorSystem.ModFolder;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                throw RpcException.Internal("the editor's mod folder is not set or does not exist");
            }
            return root;
        }

        /// <summary>Meta.json name of a mod folder via the game's own JSON reader (read only); null if unreadable.</summary>
        private static string ReadMetaName(string modDir)
        {
            string meta = Path.Combine(modDir, "Data", "Meta.json");
            if (!File.Exists(meta))
            {
                return null;
            }
            return ModDataReaderWriter.LoadObjectFromJson<ModMetaData>(meta, out ModMetaData val) == ModLoadResult.Success ? val?.Name : null;
        }

        private static JObject ModSummary(PummelMod pm)
        {
            ulong id = pm.workshopItem?.publishedFileId ?? ModWorkshopItem.DefaultWorkshopID;
            return new JObject
            {
                ["name"] = EditorAccess.ModName(pm),
                ["folderName"] = EditorAccess.ModFolderName(pm),
                ["type"] = pm.ModType.ToString(),
                ["publishedFileId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        private static string LeafName(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return folder;
            }
            return Path.GetFileName(folder.TrimEnd('\\', '/'));
        }
    }
}
