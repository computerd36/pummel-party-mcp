using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;
using UnityEngine;

namespace PummelPartyMcp.Tools
{
    /// <summary>
    /// Plugin 0.8.0, Phase 4 part B (prefabs): create_prefab, open_prefab, close_prefab (write, guard OpenMod, never inside run_batch).
    /// Every editor call that makes the game write a .pfab goes through <see cref="PrefabWriteGuard"/> (dry run before, verification and
    /// restore/removal after): PummelPrefab.Save truncates the file first and serialises straight into it, and a truncated prefab makes the
    /// whole mod fail to load. See docs/architecture/07-asset-import.md section 8 and docs/plugin-api.md.
    /// </summary>
    internal static class PrefabTools
    {
        private static readonly TimeSpan PrefabTimeout = CommitBudgets.PrefabTimeout;

        private sealed class CreateArgs
        {
            public string Name;
            public string FromObject;
        }

        private sealed class GuidArgs
        {
            public string Guid;
        }

        public static void Register(ToolRegistry registry, EditServices services)
        {
            registry.Register(ToolDefinition.Write("create_prefab", WriteGuard.OpenMod(), ctx =>
            {
                string name = ctx.GetString("name");
                if (name != null)
                {
                    string why = AssetImportRules.ValidateName(name);
                    if (why != null)
                    {
                        throw RpcException.InvalidParams($"'name' {why}");
                    }
                }
                return new CreateArgs { Name = name, FromObject = EditCommon.Guid(ctx, "fromObject", false) };
            }, inv => Create(inv, services), PrefabTimeout));
            registry.Register(ToolDefinition.Write("open_prefab", WriteGuard.OpenMod(),
                ctx => new GuidArgs { Guid = EditCommon.Guid(ctx, "guid", true) }, inv => Open(inv, services), PrefabTimeout));
            registry.Register(ToolDefinition.Write("close_prefab", WriteGuard.OpenMod(), null, inv => Close(inv, services), PrefabTimeout));
        }

        private sealed class Ready
        {
            public EditorSystem Ed;
            public PummelMod Mod;
            public string AssetsFull;
        }

        private static Ready RequireReady()
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            EditContext.RequireEditReady(ed);
            return new Ready { Ed = ed, Mod = mod, AssetsFull = AssetAccess.AssetsFolderOf(mod) };
        }

        private static string Rel(Ready r, ModAssetExternal a) => AssetAccess.RelativePath(r.AssetsFull, a.filePath);

        // ------------------------------------------------------------------ create_prefab

        private static void Create(WriteInvocation inv, EditServices services)
        {
            CreateArgs args = inv.ArgsAs<CreateArgs>();
            Ready r = RequireReady();
            if (r.Ed.CurrentPrefab != null)
            {
                throw new RpcException(ErrorCodes.InvalidState, PrefabContextRules.Message("create_prefab", r.Ed.CurrentPrefab.name));
            }

            ModTransform source = null;
            JObject fromObject = null;
            List<string> warnings = new List<string>();
            if (args.FromObject != null)
            {
                source = EditContext.RequireEditable(r.Ed, args.FromObject, "source object");
                string rule = PrefabSourceRules.Check(
                    source.GetComponentsInChildren<ModPrefabInstance>(true).Length, source.GetComponentsInChildren<ModBoardSpace>(true).Length);
                if (rule != null)
                {
                    throw RpcException.InvalidParams($"'{source.name}': {rule}");
                }
                List<SubtreeReference> refs = PrefabAccess.SubtreeReferences(r.Mod, source.transform, out bool incomplete);
                string refusal = PrefabSourceRules.RefusalForReferences(refs);
                if (refusal != null)
                {
                    throw RpcException.InvalidParams(refusal);
                }
                string incompleteRefusal = PrefabSourceRules.RefusalForIncompleteScan(incomplete);
                if (incompleteRefusal != null)
                {
                    throw new RpcException(ErrorCodes.InvalidState, $"'{source.name}': {incompleteRefusal}"); // M2: incomplete means "may hold references"
                }
                try
                {
                    PrefabAccess.DryRun(source.transform); // would the editor's serializer cope with the copy?
                }
                catch (Exception e)
                {
                    throw new RpcException(ErrorCodes.InvalidState,
                        $"the subtree of '{source.name}' cannot be serialised ({e.GetType().Name}: {e.Message}); the prefab file would be truncated, so nothing was created");
                }
                fromObject = new JObject { ["guid"] = args.FromObject, ["name"] = source.name };
            }

            string folder = Path.Combine(r.AssetsFull, "Prefabs");
            // L4: CreateNewPrefab writes into PummelMod.FullPrefabsFolder; it must be exactly the folder every check below looks at
            string gameFolder;
            try
            {
                gameFolder = Path.GetFullPath(r.Mod.FullPrefabsFolder).TrimEnd('\\', '/');
            }
            catch (Exception e)
            {
                throw RpcException.Internal("the game's Prefabs folder path is invalid: " + e.Message);
            }
            if (!string.Equals(gameFolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                throw RpcException.Internal("the game's Prefabs folder is not <mod>\\Assets\\Prefabs; refusing to create a prefab there");
            }
            if (File.Exists(folder))
            {
                throw new RpcException(ErrorCodes.InvalidState, "'Assets\\Prefabs' exists as a file; nothing was created");
            }
            SandboxPath.RequireFolderInsideMod(AssetImportRules.ModRootOf(r.AssetsFull), folder, "create_prefab (Assets\\Prefabs)");

            string editorName = r.Ed.GetUniqueAssetName<PummelPrefab>(source == null ? null : source.name);
            IEnumerable<string> assetNames = r.Mod.Assets.assets.OfType<PummelPrefab>().Select(p => p.name).ToArray();
            PrefabNamePlan plan = PrefabNaming.Plan(args.Name, editorName, assetNames, n => AssetImportRules.IsFree(Path.Combine(folder, n + ".pfab")));
            if (plan.NeedsRename && !AssetImportRules.IsFree(Path.Combine(folder, plan.Name + ".pfab")))
            {
                throw new RpcException(ErrorCodes.AlreadyExists, $"'Assets\\Prefabs\\{plan.Name}.pfab' (or its .pmeta) already exists; nothing was created");
            }
            string pfabPath = Path.Combine(folder, editorName + ".pfab");
            PrefabFileJob job = PrefabWriteGuard.PrepareNew(editorName, pfabPath, r.AssetsFull);

            inv.Commit(CommitBudgets.CreatePrefab);
            services.Observe();

            PummelPrefab prefab = null;
            try
            {
                PrefabWriteGuard.Recheck(job); // C1, right before the editor writes the two files
                prefab = r.Ed.CreateNewPrefab(source); // registers the asset and writes the .pfab and .pmeta immediately
            }
            catch (Exception e)
            {
                throw new RpcException(ErrorCodes.Internal,
                    $"creating the prefab failed ({e.GetType().Name}: {e.Message}); " + CleanupAfterFailedCreate(r, job, editorName));
            }

            // verify what the editor wrote
            try
            {
                job.Expected = PrefabAccess.DryRun(prefab);
            }
            catch (Exception e)
            {
                job.Expected = null;
                warnings.Add("the new prefab could not be re-serialised for the length check (" + e.Message + "); only the header was checked");
            }
            PrefabFileOutcome outcome = PrefabWriteGuard.Finish(job);
            if (!outcome.Ok)
            {
                string cleanup = RemoveRegistered(r, prefab, job);
                throw new RpcException(ErrorCodes.Internal,
                    "the editor wrote a bad prefab file and it was removed again: " + PrefabWriteGuard.Describe(new[] { outcome }) + "; " + cleanup);
            }

            bool renamed = false;
            if (plan.NeedsRename)
            {
                string renamedPath = Path.Combine(folder, plan.Name + ".pfab");
                string createdAs = prefab.name;
                RenameResult rr;
                try
                {
                    // L8 + C1: link check of the four paths, new name free, old bytes recorded; success only if both files moved
                    RenameSnapshot rs = RenameGuard.Before(AssetImportRules.ModRootOf(r.AssetsFull), prefab.filePath, renamedPath, EditorPopupManager.CurrentPopup != null);
                    prefab.Rename(plan.Name); // moves .pfab and .pmeta inside the same folder
                    rr = RenameGuard.After(rs, prefab.filePath, EditorPopupManager.CurrentPopup != null);
                }
                catch (RpcException e)
                {
                    rr = new RenameResult { Problem = e.Message };
                }
                renamed = rr.Ok && string.Equals(prefab.name, plan.Name, StringComparison.Ordinal);
                if (!renamed)
                {
                    throw new RpcException(ErrorCodes.Internal,
                        $"the prefab was created and verified as '{createdAs}' (Assets/Prefabs/{createdAs}.pfab), but renaming it to '{plan.Name}' failed: {rr.Problem ?? "the name did not change"}. " +
                        "It exists under its current name (list_assets); rename it by hand or remove it with remove_asset",
                        new JObject { ["created"] = AssetAccess.Row(prefab, r.AssetsFull), ["movedBack"] = rr.MovedBack, ["orphanFile"] = rr.Orphan });
                }
            }

            string finalFile = prefab.filePath;
            bool filesOk = File.Exists(finalFile) && File.Exists(finalFile + AssetImportRules.MetaExtension);
            if (!filesOk)
            {
                warnings.Add("one of the prefab's files is missing after the rename; check Assets/Prefabs");
            }

            JObject written = new JObject
            {
                ["files"] = new JArray(Rel(r, prefab), Rel(r, prefab) + AssetImportRules.MetaExtension),
                ["sizeBytes"] = outcome.Length,
                ["verified"] = true,
                ["identicalToSerialisation"] = outcome.Identical,
                ["immediately"] = true,
            };
            JObject result = new JObject
            {
                ["asset"] = AssetAccess.Row(prefab, r.AssetsFull),
                ["requestedName"] = args.Name,
                ["nameAdjusted"] = plan.Adjusted,
                ["fromObject"] = fromObject,
                ["objects"] = PrefabAccess.TreeOf(prefab),
                ["written"] = written,
                ["persisted"] = true,
                ["undoable"] = false,
                ["note"] = "the editor wrote Assets/Prefabs/<name>.pfab and .pmeta NOW (like Create > Prefab or dragging an object onto the Asset Browser); they stay on disk even if you do not call save_mod, and the plugin verified them. " +
                           (source == null
                               ? "The prefab is empty: open_prefab it, add objects and components, then close_prefab to save and refresh instances."
                               : $"The source object '{source.name}' and its children are unchanged in the scene (no instance replaces them); the prefab holds a copy with new object and component guids, its root at origin with identity rotation and scale. create_object kind prefabInstance places an instance."),
            };
            if (warnings.Count > 0)
            {
                result["warnings"] = new JArray(warnings.ToArray());
            }
            inv.Complete(result);
        }

        /// <summary>After a throw inside CreateNewPrefab: unregister what it registered, destroy the copy, remove exactly the files this call created.</summary>
        private static string CleanupAfterFailedCreate(Ready r, PrefabFileJob job, string editorName)
        {
            List<string> done = new List<string>();
            try
            {
                PummelPrefab reg = r.Mod.Assets.assets.OfType<PummelPrefab>().FirstOrDefault(p => PathsEqual(p.filePath, job.PfabPath));
                if (reg != null)
                {
                    done.Add(RemoveRegistered(r, reg, job));
                }
                else
                {
                    // the copy may exist without being registered: destroy objects of the prefabs root that no registered prefab owns
                    int destroyed = 0;
                    if (r.Ed.m_prefabsRoot != null)
                    {
                        HashSet<PummelPrefab> known = new HashSet<PummelPrefab>(r.Mod.Assets.assets.OfType<PummelPrefab>());
                        foreach (Transform child in r.Ed.m_prefabsRoot.Cast<Transform>().ToArray())
                        {
                            ModPrefab mp = child.GetComponent<ModPrefab>();
                            if (mp != null && (mp.targetPrefab == null || !known.Contains(mp.targetPrefab)) && child.name == editorName)
                            {
                                UnityEngine.Object.Destroy(child.gameObject);
                                destroyed++;
                            }
                        }
                    }
                    if (destroyed > 0)
                    {
                        done.Add($"destroyed {destroyed} unregistered prefab object(s)");
                    }
                    PrefabFileOutcome o = PrefabWriteGuard.Discard(job, "editor call failed");
                    done.Add(o.Removed ? "removed the files it created" : "no files to remove");
                    if (o.RestoreProblem != null)
                    {
                        done.Add(o.RestoreProblem);
                    }
                }
            }
            catch (Exception ex)
            {
                done.Add("cleanup problem: " + ex.Message);
            }
            return "cleaned up: " + (done.Count == 0 ? "nothing" : string.Join("; ", done.ToArray()));
        }

        private static string RemoveRegistered(Ready r, PummelPrefab prefab, PrefabFileJob job)
        {
            List<string> done = new List<string>();
            try
            {
                if (AssetImportRules.IsStrictlyUnder(r.AssetsFull, prefab.filePath) && AssetAccess.InModList(r.Mod, prefab))
                {
                    AssetAccess.RequireRemovable(r.AssetsFull, prefab, "create_prefab cleanup");
                    r.Mod.RemoveAsset(prefab); // destroys the prefab object, deletes <file> and <file>.pmeta, unlists it
                    done.Add("unregistered the new prefab");
                }
            }
            catch (Exception e)
            {
                done.Add("could not unregister: " + e.Message);
            }
            PrefabFileOutcome o = PrefabWriteGuard.Discard(job, "failed create");
            if (o.Removed)
            {
                done.Add("removed the files it created");
            }
            if (o.RestoreProblem != null)
            {
                done.Add(o.RestoreProblem);
            }
            return string.Join("; ", done.ToArray());
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ open_prefab

        private static void Open(WriteInvocation inv, EditServices services)
        {
            string guid = inv.ArgsAs<GuidArgs>().Guid;
            Ready r = RequireReady();
            if (r.Ed.CurrentPrefab != null)
            {
                throw new RpcException(ErrorCodes.InvalidState, PrefabContextRules.Message("open_prefab", r.Ed.CurrentPrefab.name));
            }
            ModAsset a = AssetAccess.Find(r.Ed, guid);
            if (a == null)
            {
                throw RpcException.NotFound($"no asset with guid {guid}");
            }
            if (!(a is PummelPrefab prefab))
            {
                throw RpcException.InvalidParams($"'{a.name}' is a {AssetAccess.Kind(a)}, not a prefab (list_assets type Prefab)");
            }
            if (!AssetAccess.IsOwnCustom(r.Mod, prefab, r.AssetsFull) || !AssetImportRules.IsStrictlyUnder(r.AssetsFull, prefab.filePath))
            {
                throw RpcException.InvalidParams($"'{prefab.name}' is not a prefab of the open sandbox mod; only the mod's own prefabs can be opened");
            }
            if (prefab.PrefabObject == null || !prefab.enabled)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"the prefab '{prefab.name}' has no object in memory (its file failed to load?); it cannot be opened");
            }
            List<string> warnings = new List<string>();
            try
            {
                PrefabAccess.DryRun(prefab);
            }
            catch (Exception e)
            {
                warnings.Add($"the prefab cannot be serialised right now ({e.GetType().Name}: {e.Message}); close_prefab and save_mod will refuse to write it until that is fixed");
            }

            inv.Commit(CommitBudgets.OpenPrefab);
            services.Observe();
            r.Ed.CurrentPrefab = prefab; // = PummelPrefab.Open(): the double-click in the Asset Browser. Saves nothing (no prefab was open).
            if (!ReferenceEquals(r.Ed.CurrentPrefab, prefab))
            {
                throw RpcException.Internal("the editor did not enter the prefab");
            }
            JObject result = new JObject
            {
                ["opened"] = AssetAccess.Row(prefab, r.AssetsFull),
                ["context"] = "prefab",
                ["objects"] = PrefabAccess.TreeOf(prefab),
                ["undoStackLength"] = EditCommon.Nullable(EditorAccess.UndoStackLength(r.Ed)),
                ["undoable"] = false,
                ["note"] = "the prefab edit context is open: the scene is hidden and get_hierarchy shows the prefab; object/component/property/action tools and undo (this prefab's own undo stack) act on it. " +
                           "Edits stay in memory until close_prefab (saves the prefab and refreshes its instances) or save_mod. Lifecycle tools, start_playtest, create_prefab, open_prefab, import_asset and remove_asset refuse while it is open.",
            };
            if (warnings.Count > 0)
            {
                result["warnings"] = new JArray(warnings.ToArray());
            }
            inv.Complete(result);
        }

        // ------------------------------------------------------------------ close_prefab

        private static void Close(WriteInvocation inv, EditServices services)
        {
            Ready r = RequireReady();
            PummelPrefab prefab = r.Ed.CurrentPrefab;
            if (prefab == null)
            {
                throw new RpcException(ErrorCodes.InvalidState, "no prefab is being edited (open_prefab first)");
            }
            if (prefab.PrefabObject == null)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"the open prefab '{prefab.name}' has lost its object; nothing was saved");
            }
            // dry run + safety copy BEFORE anything changes: refuses when the editor's serializer cannot write the prefab
            PrefabFileJob job = PrefabAccess.Prepare(prefab, r.AssetsFull);
            string rel = Rel(r, prefab);
            string name = prefab.name;
            string guid = prefab.guid.ToString();

            inv.Commit(CommitBudgets.ClosePrefab);
            services.Observe();

            // The sequence of the UI's "Return to Hierarchy" (HierarchyWidget): refresh the live instances, then leave the context, which
            // saves the prefab (the CurrentPrefab setter). The UI's final ReimportAsset is NOT called: it destroys the in-memory prefab before
            // parsing the file again, and the plugin never reimports a prefab.
            EditorSelectionManager sel = r.Ed.GetSelectionManager();
            sel.DeselectAllObjectsAndGizmos();
            prefab.isEnabled = prefab.PrefabObject.activeSelf;
            List<ModPrefabInstance> instances = PrefabAccess.SceneInstances(r.Ed, prefab);
            int refreshed = 0;
            try
            {
                foreach (ModPrefabInstance inst in instances)
                {
                    inst.ForceUpdate(prefab);
                    refreshed++;
                }
            }
            catch (Exception e)
            {
                throw RpcException.Internal($"refreshing the prefab instances failed after {refreshed} of {instances.Count} ({e.GetType().Name}: {e.Message}); the prefab is still open and nothing was saved");
            }
            sel.DeselectAllObjectsAndGizmos();

            Exception saveError = null;
            try
            {
                PrefabWriteGuard.Recheck(job); // C1, right before the setter writes .pfab and .pmeta
                r.Ed.CurrentPrefab = null; // saves the prefab (isEnabled, Save, hides it) and returns to the scene
            }
            catch (Exception e)
            {
                saveError = e;
            }
            PrefabFileOutcome outcome = PrefabWriteGuard.Finish(job);
            bool closed = r.Ed.CurrentPrefab == null;
            if (saveError != null || !outcome.Ok)
            {
                throw new RpcException(ErrorCodes.Internal,
                    (saveError != null ? $"saving the prefab failed ({saveError.GetType().Name}: {saveError.Message}); " : string.Empty) +
                    (outcome.Ok ? string.Empty : "file check: " + PrefabWriteGuard.Describe(new[] { outcome }) + "; ") +
                    (closed ? "the prefab was closed, " : "the prefab is still open, ") +
                    "its edits are still in memory but NOT on disk (the file has its previous content); fix the cause and call close_prefab or save_mod again");
            }
            JObject result = new JObject
            {
                ["closed"] = new JObject { ["guid"] = guid, ["name"] = name, ["relativePath"] = rel },
                ["context"] = "scene",
                ["savedToDisk"] = true,
                ["file"] = new JObject { ["sizeBytes"] = outcome.Length, ["verified"] = true, ["identicalToSerialisation"] = outcome.Identical },
                ["instancesRefreshed"] = refreshed,
                ["instances"] = PrefabAccess.InstancesJson(instances),
                ["reimported"] = false,
                ["undoable"] = false,
                ["note"] = "the prefab's .pfab and .pmeta were written to disk NOW (leaving the edit context saves the prefab) and verified, and every instance in the scene was refreshed (the Return to Hierarchy handler's ForceUpdate). " +
                           "The prefab's undo history stays in the editor and is available again after open_prefab. Instances are not saved with children: after a reload they reflect the file.",
            };
            if (outcome.Warnings.Count > 0)
            {
                result["warnings"] = new JArray(outcome.Warnings.ToArray());
            }
            inv.Complete(result);
        }
    }
}
