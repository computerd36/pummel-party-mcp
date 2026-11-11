using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using PummelBridge.Game;

namespace PummelBridge.Tools
{
    /// <summary>list_mods (read); create_test_mod, open_test_mod (write, Lifecycle); save_mod (write, OpenMod).</summary>
    internal static class ModTools
    {
        /// <summary>The tool's own limit for an asynchronous load (create/open).</summary>
        public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Dispatcher limit for create/open: a little above <see cref="LoadTimeout"/> so the tool's own TIMEOUT message wins.</summary>
        private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(35);

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

        public static void Register(ToolRegistry registry, FramePoller poller, IBridgeLog log)
        {
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

            registry.Register(ToolDefinition.Write("save_mod", WriteGuard.OpenMod(), null, inv => SaveMod(inv, log)));
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

        private static void CreateTestMod(WriteInvocation inv, FramePoller poller, IBridgeLog log)
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
            WaitForLoad(inv, poller, pm, extraReady: () => File.Exists(metaFile) && File.Exists(workshopFile), onReady: () =>
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

        private static void OpenTestMod(WriteInvocation inv, FramePoller poller, IBridgeLog log)
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

            // Same call the Open Mod dialog ends in (OpenModWithPopupPrompt -> OpenMod) without its unsaved-changes
            // popup, which CheckCanSwitchAway replaces.
            ed.OpenMod(folder);
            PummelMod pm = ed.CurrentMod;
            if (pm == null)
            {
                throw RpcException.Internal("OpenMod did not set the current mod");
            }
            log.Info($"open_test_mod: opening '{args.Name}'");
            WaitForLoad(inv, poller, pm, extraReady: null, onReady: () =>
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

        private static void SaveMod(WriteInvocation inv, IBridgeLog log)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            RefuseIfLifecyclePending();
            PummelMod mod = ed.CurrentMod; // guard: loaded test mod, not loading, state Editing
            EditorAvailableModManager manager = ed.GetAvailableModManager();
            RefuseIfBrokenModList(manager);
            EnsureListed(manager, mod);
            GameEditorGuardHost.EnforceDefaultWorkshopId(mod, log);

            // Saving while a prefab is being edited is what Ctrl+S does as well: the scene serializer stores
            // activeSelf, so the (deactivated) hierarchy root does not change the saved scene; the edited prefab is
            // saved with the other assets. Only the hierarchy's dirty flag stays set and Preview.jpg shows the prefab view.
            ed.SaveCurrentMod();
            log.Info($"save_mod: saved '{EditorAccess.ModFolderName(mod)}'");
            bool? dirty = EditorAccess.IsDirty(ed);
            inv.Complete(new JObject
            {
                ["saved"] = true,
                ["name"] = EditorAccess.ModName(mod),
                ["isDirty"] = dirty.HasValue ? new JValue(dirty.Value) : JValue.CreateNull(),
            });
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Completes the invocation from a later frame once <paramref name="pm"/> is the loaded current mod (and
        /// <paramref name="extraReady"/> holds), or fails it on load failure / after <see cref="LoadTimeout"/>.
        /// </summary>
        private static void WaitForLoad(WriteInvocation inv, FramePoller poller, PummelMod pm, Func<bool> extraReady, Func<JObject> onReady)
        {
            Stopwatch sw = Stopwatch.StartNew();
            s_lifecyclePending = true;
            poller.Add(() =>
            {
                try
                {
                    if (inv.Completion.IsCancelled)
                    {
                        s_lifecyclePending = false;
                        return true;
                    }
                    EditorSystem ed = EditorAccess.EditorOrNull();
                    if (ed == null)
                    {
                        return Finish(inv, new RpcException(ErrorCodes.EditorNotOpen, "the editor was closed while the mod was loading"));
                    }
                    if (!ed.IsLoading)
                    {
                        if (ed.CurrentMod != pm)
                        {
                            return Finish(inv, RpcException.Internal($"the mod failed to load ({pm.lastResult}); see the editor's popup and get_log"));
                        }
                        if (pm.IsLoaded && (extraReady == null || extraReady()))
                        {
                            s_lifecyclePending = false;
                            inv.Complete(onReady());
                            return true;
                        }
                    }
                    if (sw.Elapsed >= LoadTimeout)
                    {
                        return Finish(inv, new RpcException(ErrorCodes.Timeout,
                            $"the mod did not finish loading within {LoadTimeout.TotalSeconds:0} s (it may still finish; check get_status)"));
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

        /// <summary>
        /// Rules for leaving the open mod (create/open). Mirrors the editor's unsaved-changes popup, which is driven by
        /// EditorSystem.IsModDirty (undo manager of the current context; set by the first undo snapshot after any load,
        /// so it is already true for an untouched, freshly opened mod).
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
                    $"a prefab ('{ed.CurrentPrefab.name}') is being edited in '{EditorAccess.ModFolderName(current)}'; return to the scene first");
            }
            bool? dirty = EditorAccess.IsDirty(ed);
            if (dirty != false && !discardUnsaved)
            {
                throw new RpcException(ErrorCodes.InvalidState,
                    $"the open mod '{EditorAccess.ModFolderName(current)}' has unsaved changes (editor dirty flag{(dirty == null ? " unreadable" : "")}); " +
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
