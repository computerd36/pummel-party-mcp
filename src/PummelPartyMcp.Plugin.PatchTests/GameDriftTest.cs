using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PummelPartyMcp.Core;
using PummelPartyMcp.Safety;

namespace PummelPartyMcp.PatchTests
{
    /// <summary>
    /// Offline drift test: loads the real Assembly-CSharp.dll and com.rlabrecque.steamworks.net.dll from the local game
    /// install for REFLECTION ONLY (no code of the game runs, no static constructors, no Steam, no Unity, no patch applied)
    /// and checks that <see cref="PublishBlockTargets.Build(Assembly, Assembly, string)"/> resolves every required Workshop
    /// target and yields the expected number of targets. Catches a game update that renames or re-signs a blocked method
    /// before the plugin fails closed in the field. Nothing is copied: the assemblies are read from the game folder.
    /// </summary>
    internal static class GameDriftTest
    {
        /// <summary>Methods the block patches for the game build the plugin was written against (docs/plugin-api.md, patchedCount).</summary>
        private const int ExpectedTargetCount = 59;

        public static void Run(Action<string, bool> check)
        {
            Console.WriteLine("-- offline drift test: PublishBlockTargets against the real game assemblies");
            string gameDir = ReadGameDir();
            string managed = gameDir == null ? null : Path.Combine(gameDir, "PummelParty_Data", "Managed");
            string gameDll = managed == null ? null : Path.Combine(managed, "Assembly-CSharp.dll");
            string steamDll = managed == null ? null : Path.Combine(managed, "com.rlabrecque.steamworks.net.dll");
            if (gameDll == null || !File.Exists(gameDll) || !File.Exists(steamDll))
            {
                Console.WriteLine("  SKIP  game assemblies not found (GameDir = " + (gameDir ?? "<not set>") + "); drift test not run");
                return;
            }

            ResolveEventHandler resolver = (sender, args) => ReflectionOnlyResolve(args, managed);
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += resolver;
            try
            {
                Assembly game = Assembly.ReflectionOnlyLoadFrom(gameDll);
                Assembly steam = Assembly.ReflectionOnlyLoadFrom(steamDll);
                List<BlockTarget> targets = PublishBlockTargets.Build(game, steam, null);

                List<BlockTarget> unresolved = targets.Where(t => t.Required && t.Method == null).ToList();
                foreach (BlockTarget t in unresolved)
                {
                    Console.WriteLine("    unresolved required target: " + t.Name + " (" + t.Problem + ")");
                }
                check("every required target resolves in the real assemblies (" + targets.Count(t => t.Required) + " required)", unresolved.Count == 0 && targets.Any(t => t.Required));
                // The same method can be listed twice (a required SteamUGC target that also takes an update handle); the block
                // patches each method once, so the expected number counts distinct methods, like PublishBlockStatus.PatchedCount.
                int distinct = targets.Where(t => t.Method != null).Select(t => t.Method).Distinct().Count();
                check($"distinct patched methods = {ExpectedTargetCount} (got {distinct}; {targets.Count} list entries, {targets.Count(t => t.Method == null)} unresolved)",
                    distinct == ExpectedTargetCount && targets.All(t => t.Method != null));
                check("every target belongs to the game or Steamworks.NET assembly", targets.Where(t => t.Method != null)
                    .All(t => t.Method.DeclaringType.Assembly == game || t.Method.DeclaringType.Assembly == steam));
                ReflectedMembers(game, check);
                RegistryAndAudit(game, check);
                SettingsDrift(game, check);
                AssetsDrift(game, check);
                PrefabsDrift(game, check);
                LinkSafetyDrift(game, check);
            }
            catch (Exception e)
            {
                check("drift test could not run: " + e.GetType().Name + ": " + e.Message, false);
            }
            finally
            {
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= resolver;
            }
        }

        /// <summary>
        /// Private game fields the plugin reads/writes by name (playtest and validator tools, increment 4). Public members
        /// are checked by the compiler; these are not, so a game update renaming one would only show up at runtime.
        /// </summary>
        private static void ReflectedMembers(Assembly game, Action<string, bool> check)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            (string type, string field, string fieldType, bool isStatic)[] fields =
            {
                ("EditorSystem", "m_toolbarWidget", "ToolbarWidget", false),
                ("ToolbarWidget", "m_playerCountChoice", "System.Int32", false),
                ("ToolbarWidget", "m_aiOnly", "System.Boolean", false),
                ("ToolbarWidget", "m_backgroundModLoadTimer", "System.Single", false),
                ("ToolbarWidget", "m_playerCount", "System.String[]", false),
                ("ModValidationSystem", "m_validators", "System.Collections.Generic.List`1[ModValidationCallbackReceiver]", true),
                ("MinigameController", "round", "System.Int32", false),
                // playtest visibility (get_playtest_state / get_playtest_events, plugin 0.6.0)
                ("MinigameController", "round_begin_time", "System.Single", false),
                ("SimpleModPlayer", "m_lastDeathTime", "System.Single", false),
                ("SimpleModPlayer", "m_filoHeldItemStack", "System.Collections.Generic.List`1[ModItem]", false),
                ("ModSystem.Logic.ModLogicTrigger", "m_owner", "ModSystem.Logic.IEventReceiver", false),
            };
            foreach (var f in fields)
            {
                Type t = game.GetType(f.type, false);
                FieldInfo fi = t?.GetField(f.field, any);
                check($"reflected field {f.type}.{f.field} exists ({f.fieldType}{(f.isStatic ? ", static" : "")})",
                    fi != null && fi.FieldType.ToString() == f.fieldType && fi.IsStatic == f.isStatic);
            }
            PlaytestHookTargets(game, check);
            Type modWorkshopController = game.GetType("WorkshopController", false);
            check("WorkshopController.CurrentBackgroundLoadMod / CurrentActiveMod are static properties",
                modWorkshopController?.GetProperty("CurrentBackgroundLoadMod", BindingFlags.Public | BindingFlags.Static) != null
                && modWorkshopController.GetProperty("CurrentActiveMod", BindingFlags.Public | BindingFlags.Static) != null);
        }

        /// <summary>
        /// The central registry (Core/RequiredMembers.cs, the same list the runtime self_test tool resolves) against the real game
        /// assembly, and the inspector audit snapshot: the custom-inspector components and their reflected properties must be exactly
        /// the ones audited, so a game update that adds, renames or attaches an inspector is caught until the inspectors are re-read.
        /// </summary>
        private static void RegistryAndAudit(Assembly game, Action<string, bool> check)
        {
            System.Collections.Generic.List<string> problems = RequiredMembers.ResolveAll(game);
            foreach (string p in problems)
            {
                Console.WriteLine("    unresolved registry member: " + p);
            }
            check($"every member of the central registry resolves in the real assembly ({RequiredMembers.All.Count} members, {problems.Count} problems)", problems.Count == 0);

            Type[] types = SafeTypes(game);
            string[] inspectorTypes = types
                .Where(t => CustomAttributeData.GetCustomAttributes(t).Any(a => a.AttributeType.Name == "ModSerialized")
                         && CustomAttributeData.GetCustomAttributes(t).Any(a => a.AttributeType.Name == "ModCustomInspector"))
                .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            check("the custom-inspector components are exactly the 21 audited (" + string.Join(", ", inspectorTypes) + ")",
                inspectorTypes.SequenceEqual(InspectorRules.CustomInspectorComponents.OrderBy(n => n, StringComparer.Ordinal)));

            foreach (string name in InspectorRules.CustomInspectorComponents)
            {
                Type t = game.GetType(name, false);
                if (t == null)
                {
                    check("audited component " + name + " exists", false);
                    continue;
                }
                string[] actual = t.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.GetGetMethod(false) != null && p.GetGetMethod(false).GetBaseDefinition() == p.GetGetMethod(false))
                    .Where(p => !CustomAttributeData.GetCustomAttributes(p).Any(a => a.AttributeType.Name == "ModIgnore"))
                    .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                string[] audited = InspectorRules.AuditedProperties[name].Split(' ').OrderBy(n => n, StringComparer.Ordinal).ToArray();
                bool same = actual.SequenceEqual(audited);
                if (!same)
                {
                    Console.WriteLine("    " + name + ": new = [" + string.Join(", ", actual.Except(audited)) + "], gone = [" + string.Join(", ", audited.Except(actual)) + "]");
                }
                check($"audit snapshot of {name} equals its reflected properties ({actual.Length})", same);
            }
        }

        /// <summary>
        /// Mod settings (plugin 0.8.0): every field, enum, range and method that Core/SettingsRules.cs names against the real game
        /// assembly (the same entries self_test resolves), plus completeness: the data classes the settings window edits must have no
        /// public member the audit has not seen, so a game update that adds a setting fails here until the rule table is extended.
        /// </summary>
        /// <summary>String literals (ldstr operands) of a method body, read from the IL without running anything.</summary>
        private static HashSet<string> LdStrs(MethodBase method)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            MethodBody body = method.GetMethodBody();
            byte[] il = body?.GetILAsByteArray();
            if (il == null)
            {
                return result;
            }
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x72)
                {
                    continue;
                }
                int token = BitConverter.ToInt32(il, i + 1);
                if ((token >> 24) != 0x70)
                {
                    continue; // not a user-string token
                }
                try
                {
                    result.Add(method.Module.ResolveString(token));
                }
                catch (Exception)
                {
                    // an operand byte that only looked like ldstr
                }
            }
            return result;
        }

        /// <summary>Asset tools (plugin 0.8.0): the game facts the import/removal safety rules rest on, checked against the real assembly.</summary>
        private static void AssetsDrift(Assembly game, Action<string, bool> check)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            string[] users = { "asset tools", "import_asset", "set_import_settings", "create_material", "set_material", "remove_asset", "get_asset" };
            List<RequiredMember> assets = RequiredMembers.All.Where(m => m.UsedBy != null && users.Any(u => m.UsedBy.StartsWith(u, StringComparison.Ordinal))).ToList();
            List<string> problems = new List<string>();
            foreach (RequiredMember m in assets)
            {
                string why = RequiredMembers.Resolve(game, m);
                if (why != null)
                {
                    problems.Add(m.Kind + " " + m.Id + ": " + why);
                }
            }
            foreach (string p in problems)
            {
                Console.WriteLine("    asset drift: " + p);
            }
            check($"every member the asset tools use resolves in the real game ({assets.Count} members; {problems.Count} problems)", problems.Count == 0 && assets.Count > 30);

            // the import-setting table equals the game enums
            List<string> enumProblems = new List<string>();
            foreach (ImportSettingField f in ImportSettingRules.All.Where(x => !x.IsBool))
            {
                Type t = RequiredMembers.ResolveType(game, f.EnumType, f.EnumAssembly);
                if (t == null || !t.IsEnum)
                {
                    enumProblems.Add(f.Name + ": enum " + f.EnumType + " not found");
                    continue;
                }
                string actual = string.Join(",", t.GetFields(BindingFlags.Public | BindingFlags.Static).Select(x => x.Name + "=" + Convert.ToInt64(x.GetRawConstantValue())).OrderBy(x => x).ToArray());
                string expected = string.Join(",", f.EnumNames.Select((n, i) => n + "=" + f.EnumValues[i]).OrderBy(x => x).ToArray());
                if (actual != expected)
                {
                    enumProblems.Add($"{f.Name}: game [{actual}] table [{expected}]");
                }
            }
            check("the import-setting choices equal the game's enums (" + string.Join("; ", enumProblems.ToArray()) + ")", enumProblems.Count == 0);

            // the importer table: the extensions the allow-list relies on; .bmp (offered by the menu) has no importer
            Type importer = game.GetType("PummelAssetImporter", false);
            MethodInfo getImporter = importer?.GetMethod("GetImporter", all);
            if (getImporter == null)
            {
                check("PummelAssetImporter.GetImporter exists", false);
            }
            else
            {
                HashSet<string> exts = LdStrs(getImporter);
                check("GetImporter knows every allow-listed extension (" + string.Join(" ", AssetImportRules.AllowedExtensions.Where(e => !exts.Contains(e)).ToArray()) + " missing)",
                    AssetImportRules.AllowedExtensions.All(e => exts.Contains(e)));
                check("GetImporter has no .bmp importer (the editor menu offers it, the allow-list does not)", !exts.Contains(".bmp"));
            }

            // removal deletes <file> and <file>.pmeta (the two paths the plugin re-checks)
            MethodInfo deleteFile = game.GetType("PummelMod", false)?.GetMethod("DeleteFile", all);
            check("PummelMod.DeleteFile deletes the file and its .pmeta (what remove_asset re-checks)", deleteFile != null && LdStrs(deleteFile).Contains(".pmeta"));

            // doc 07 hazards H1/H2: AddAsset(string, string, bool copy) still has this signature (the plugin never calls it; if it changes, redo the analysis)
            MethodInfo addAsset = game.GetType("PummelMod", false)?.GetMethod("AddAsset", new[] { typeof(string), typeof(string), typeof(bool) });
            check("PummelMod.AddAsset(string, string, bool copy) is unchanged (never called by the plugin; doc 07 hazards H1/H2)", addAsset != null);
            MethodInfo createMat = game.GetType("EditorSystem", false)?.GetMethod("CreateNewMaterial", all);
            check("EditorSystem.CreateNewMaterial writes a .pmat (create_material checks the file name first)", createMat != null && LdStrs(createMat).Contains(".pmat"));
        }

        /// <summary>Names ("Type.Method") of the methods called (call/callvirt) in a method body; unresolvable operands are skipped.</summary>
        private static HashSet<string> Calls(MethodBase method)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
            {
                return result;
            }
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F)
                {
                    continue;
                }
                int token = BitConverter.ToInt32(il, i + 1);
                if ((token >> 24) != 0x0A && (token >> 24) != 0x06)
                {
                    continue;
                }
                try
                {
                    MethodBase m = method.Module.ResolveMethod(token);
                    result.Add(m.DeclaringType.Name + "." + m.Name);
                }
                catch (Exception)
                {
                    // an operand byte that only looked like a call
                }
            }
            return result;
        }

        /// <summary>Names of static fields assigned right after a given one-byte opcode (e.g. ldc.i4.1) in a method body.</summary>
        private static List<string> ConstStores(MethodBase method, byte ldc)
        {
            List<string> names = new List<string>();
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            for (int i = 0; il != null && i + 5 < il.Length; i++)
            {
                if (il[i] == ldc && il[i + 1] == 0x80)
                {
                    try
                    {
                        names.Add(method.Module.ResolveField(BitConverter.ToInt32(il, i + 2)).Name);
                    }
                    catch (Exception)
                    {
                        // not a field token
                    }
                }
            }
            return names;
        }

        /// <summary>
        /// Prefab tools (plugin 0.8.0): the game facts the .pfab write protection and the Return to Hierarchy sequence rest on, read from
        /// the real assembly's IL (reflection only, nothing runs).
        /// </summary>
        private static void PrefabsDrift(Assembly game, Action<string, bool> check)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            List<RequiredMember> members = RequiredMembers.All.Where(m => m.UsedBy != null && (m.UsedBy.IndexOf("prefab", StringComparison.OrdinalIgnoreCase) >= 0
                || m.UsedBy.IndexOf("pfab", StringComparison.OrdinalIgnoreCase) >= 0 || m.UsedBy.StartsWith("dry run", StringComparison.Ordinal))).ToList();
            List<string> problems = new List<string>();
            foreach (RequiredMember m in members)
            {
                string why = RequiredMembers.Resolve(game, m);
                if (why != null)
                {
                    problems.Add(m.Kind + " " + m.Id + ": " + why);
                }
            }
            foreach (string p in problems)
            {
                Console.WriteLine("    prefab drift: " + p);
            }
            check($"every member the prefab tools use resolves in the real game ({members.Count} members; {problems.Count} problems)", problems.Count == 0 && members.Count >= 12);

            Type ser = game.GetType("ModHierarchySerializer", false);
            Type pp = game.GetType("PummelPrefab", false);
            Type ed = game.GetType("EditorSystem", false);
            Type hw = game.GetType("HierarchyWidget", false);
            MethodBase cctor = ser?.TypeInitializer;
            check("the .pfab header: ModHierarchySerializer's static constructor assigns \"PMH\" and major version 1 (the plugin checks 03 'PMH' 01 00 00 00)",
                cctor != null && LdStrs(cctor).Contains("PMH") && ConstStores(cctor, 0x17).Contains("MajorVersion"));
            MethodInfo saveVersion = ser?.GetMethod("SaveVersion", all);
            check("SaveVersion writes identifier, major and minor (the 8-byte header the plugin checks)", saveVersion != null && Calls(saveVersion).Contains("BinaryWriter.Write"));
            MethodInfo saveTransform = ser?.GetMethod("SaveTransform", all);
            check("SaveTransform writes version, hierarchy, component list and component values (what the dry run runs)",
                saveTransform != null && new[] { "ModHierarchySerializer.SaveVersion", "ModHierarchySerializer.SaveHierarchy", "ModHierarchySerializer.SaveComponents", "ModHierarchySerializer.SaveComponentValues" }.All(Calls(saveTransform).Contains));
            MethodInfo prefabSave = pp?.GetMethod("Save", all);
            check("PummelPrefab.Save serialises with ModHierarchySerializer.SaveTransform straight into File.Open (the truncating write the plugin guards)",
                prefabSave != null && Calls(prefabSave).Contains("ModHierarchySerializer.SaveTransform") && Calls(prefabSave).Contains("File.Open") && Calls(prefabSave).Contains("ModAssetExternal.Save"));
            MethodInfo create = ed?.GetMethod("CreateNewPrefab", all);
            check("CreateNewPrefab registers the asset, saves the prefab and names the file from asset names (create_prefab verifies after it)",
                create != null && new[] { "PummelMod.AddAsset", "ModAssetExternal.Save", "PummelMod.get_FullPrefabsFolder", "Directory.CreateDirectory" }.All(Calls(create).Contains) && LdStrs(create).Contains(".pfab") && LdStrs(create).Contains("/Prefabs/"));
            MethodInfo setter = ed?.GetProperty("CurrentPrefab", all)?.GetSetMethod();
            check("the CurrentPrefab setter saves the prefab it leaves", setter != null && Calls(setter).Contains("ModAssetExternal.Save") && Calls(setter).Contains("EditorSelectionManager.DeselectAllObjectsAndGizmos"));
            MethodInfo modSave = game.GetType("PummelMod", false)?.GetMethod("Save", Type.EmptyTypes);
            check("PummelMod.Save rewrites every external asset (save_mod protects all prefabs)", modSave != null && Calls(modSave).Contains("ModAssetExternal.Save"));
            MethodBase handler = hw == null ? null : hw.GetMethods(all).Cast<MethodBase>().Concat(hw.GetNestedTypes(all).SelectMany(n => n.GetMethods(all)).Cast<MethodBase>())
                .FirstOrDefault(m => m.GetMethodBody() != null && LdStrs(m).Any(x => x.Contains("Return to Hierarchy")));
            HashSet<string> hc = handler == null ? new HashSet<string>() : Calls(handler);
            check("the Return to Hierarchy handler refreshes instances (ForceUpdate), leaves the context and reimports (the plugin does the first two only)",
                handler != null && hc.Contains("ModPrefabInstance.ForceUpdate") && hc.Contains("EditorSystem.set_CurrentPrefab") && hc.Contains("EditorSystem.ReimportAsset") && hc.Contains("EditorSelectionManager.DeselectAllObjectsAndGizmos"));
            MethodInfo reimport = ed?.GetMethod("ReimportAsset", all);
            check("ReimportAsset saves and re-imports the file (the destructive step close_prefab skips)", reimport != null && Calls(reimport).Contains("ModAssetExternal.Save") && Calls(reimport).Contains("PummelMod.ImportFile"));
            MethodInfo loadMeta = game.GetType("PummelPrefabImporter", false)?.GetMethod("LoadMetaData", all);
            check("PummelPrefabImporter destroys the in-memory prefab object before it parses the file (hazard H6)", loadMeta != null && Calls(loadMeta).Contains("Object.DestroyImmediate"));
            MethodInfo onDestroy = pp?.GetMethod("OnDestroy", all);
            check("a prefab asset's OnDestroy nulls the scene instances' reference (remove_asset reports them as empty)", onDestroy != null && Calls(onDestroy).Contains("ModPrefabInstance.set_PrefabReference"));
        }

        /// <summary>
        /// Review C1/C2/H1/M1: the game facts the link pre-checks rest on (which files a save writes, which classes rewrite their file,
        /// the nested mesh removal, the screenshot name, the .pmeta written before decoding), read from the real assembly's IL.
        /// </summary>
        private static void LinkSafetyDrift(Assembly game, Action<string, bool> check)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type mod = game.GetType("PummelMod", false);
            Type rw = game.GetType("ModDataReaderWriter", false);
            Type ed = game.GetType("EditorSystem", false);
            HashSet<string> strs = new HashSet<string>(StringComparer.Ordinal);
            foreach (Type t in new[] { mod, rw, ed, game.GetType("MinigameMod", false), game.GetType("BoardMod", false), game.GetType("ItemMod", false), game.GetType("BoardSpaceMod", false) })
            {
                if (t == null)
                {
                    continue;
                }
                foreach (MethodBase m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)).Concat(t.TypeInitializer == null ? new MethodBase[0] : new MethodBase[] { t.TypeInitializer }))
                {
                    if (m.GetMethodBody() != null)
                    {
                        strs.UnionWith(LdStrs(m));
                    }
                }
            }
            string[] missing = ModSaveTargets.DataFiles.Concat(new[] { ModSaveTargets.PreviewFile }).Where(f => !strs.Contains(f)).ToArray();
            check("every Data file the save pre-check covers is named in the game's save code (" + string.Join(", ", missing) + " missing)", missing.Length == 0);
            MethodInfo modSave = mod?.GetMethod("Save", Type.EmptyTypes);
            HashSet<string> sc = modSave == null ? new HashSet<string>() : Calls(modSave);
            check("PummelMod.Save writes metadata, workshop item, lighting, post-processing, scene, mod-specific data and every asset (the save pre-check list)",
                new[] { "PummelMod.SaveMetaData", "PummelMod.SaveWorkshopItemDetails", "ModDataReaderWriter.SaveLightingSettings", "ModDataReaderWriter.SavePostProcessSettings", "ModDataReaderWriter.SaveScene", "PummelMod.SaveModSpecificData", "ModAssetExternal.Save" }.All(sc.Contains));
            MethodInfo saveCurrent = ed?.GetMethod("SaveCurrentMod", all);
            check("SaveCurrentMod also writes Preview.jpg (save_mod pre-check includes it)", saveCurrent != null && Calls(saveCurrent).Contains("EditorSystem.SavePreviewScreenshot"));
            MethodInfo enter = ed?.GetMethod("SetEditorState", all);
            check("SetEditorState(EnteringPlayMode) calls PummelMod.Save synchronously (start_playtest protection)", enter != null && Calls(enter).Contains("PummelMod.Save"));
            MethodInfo matSave = game.GetType("PummelMaterial", false)?.GetMethod("Save", all);
            MethodInfo vsSave = game.GetType("PummelVisualScript", false)?.GetMethod("Save", all);
            check("materials and visual scripts rewrite their own file on save (the pre-check covers the file, not only the .pmeta)",
                matSave != null && Calls(matSave).Contains("File.Open") && vsSave != null && Calls(vsSave).Contains("File.WriteAllText"));
            MethodInfo onRemove = game.GetType("PummelProp", false)?.GetMethod("OnRemove", all);
            check("removing a prop also removes its nested mesh asset (remove_asset checks the mesh's paths too)", onRemove != null && Calls(onRemove).Contains("PummelMod.RemoveAsset"));
            MethodBase shot = ed == null ? null : ed.GetMethods(all).Cast<MethodBase>().Concat(ed.GetNestedTypes(all).SelectMany(n => n.GetMethods(all)).Cast<MethodBase>())
                .FirstOrDefault(m => m.GetMethodBody() != null && LdStrs(m).Any(x => x.StartsWith("Screenshot", StringComparison.Ordinal) && x.EndsWith(".jpg", StringComparison.Ordinal)));
            check("the play-mode screenshot is written as Textures/Screenshot<N>.jpg (capture_preview_image pre-checks that name)", shot != null && Calls(shot).Contains("PummelMod.get_FullTexturesFolder"));
            ModSwitchGuardDrift(game, ed, check);
            MethodInfo tryMeta = game.GetType("PummelAssetImporter", false)?.GetMethod("TryCreateOrLoadMetaAsset", all);
            check("the importer writes a missing .pmeta (SaveMetaFile) before the decoder runs (open pre-check, import rollback)", tryMeta != null && Calls(tryMeta).Contains("ModAssetExternal.SaveMetaFile"));
            MethodInfo saveMeta = game.GetType("ModAssetExternal", false)?.GetMethod("SaveMetaFile", all);
            check("SaveMetaFile rewrites <file>.pmeta in place (File.WriteAllBytes; a hard link would be written through)", saveMeta != null && Calls(saveMeta).Contains("File.WriteAllBytes"));
            MethodInfo rename = game.GetType("ModAssetExternal", false)?.GetMethod("Rename", all);
            check("ModAssetExternal.Rename moves the file, then the .pmeta, and raises a popup on failure (L8 partial-rename detection)",
                rename != null && Calls(rename).Contains("File.Move") && Calls(rename).Contains("EditorPopupManager.AddPopup"));
        }

        /// <summary>
        /// Review G: the mod-switch guard prefixes EditorSystem.OpenMod(string) and CreateNewMod(string, PummelModType, string). That covers
        /// every way the game loads a mod into the editor only if those two are the only callers of LoadModIntoEditor in the whole
        /// assembly, and the Open Mod dialog's OpenModWithPopupPrompt ends in OpenMod. Read from the IL (nothing runs).
        /// </summary>
        private static void ModSwitchGuardDrift(Assembly game, Type ed, Action<string, bool> check)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type modType = game.GetType("PummelModType", false);
            MethodInfo open = ed?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == "OpenMod" && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string) }));
            MethodInfo create = ed?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == "CreateNewMod" && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string), modType, typeof(string) }));
            check("mod-switch guard targets exist: EditorSystem.OpenMod(String) and CreateNewMod(String, PummelModType, String)", open != null && create != null && modType != null);
            Type popup = game.GetType("NewModPopup", false);
            MethodInfo dialogCreate = popup?.GetMethods(all).FirstOrDefault(m => m.Name == "CreateNewMod" && m.GetParameters().Length == 1);
            HashSet<string> dialogCalls = dialogCreate == null ? new HashSet<string>() : Calls(dialogCreate);
            check("NewModPopup.CreateNewMod(List) calls EditorSystem.CreateNewMod and then EditorAvailableModManager.AddAvailableMod (why the dialog's action is guarded as a whole)",
                dialogCalls.Contains("EditorSystem.CreateNewMod") && dialogCalls.Contains("EditorAvailableModManager.AddAvailableMod"));
            Type pummelMod = game.GetType("PummelMod", false);
            check("close/delete guard targets exist: EditorSystem.SetCurrentMod(PummelMod), EditorSystem.OnDisable(), MainMenuWidget/LoadModPopup.DeleteModResult(Int32)",
                ed?.GetMethod("SetCurrentMod", BindingFlags.Instance | BindingFlags.Public, null, new[] { pummelMod }, null) != null
                && ed.GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null) != null
                && game.GetType("MainMenuWidget", false)?.GetMethod("DeleteModResult", all, null, new[] { typeof(int) }, null) != null
                && game.GetType("LoadModPopup", false)?.GetMethod("DeleteModResult", all, null, new[] { typeof(int) }, null) != null);

            // Who calls what (whole assembly; a compiler-generated lambda/iterator is reported as Outer/<nested>.Method). Pinned: a game
            // update that adds a path into these methods must be reviewed (does it do something after the call that a refusal would break?).
            Dictionary<string, List<string>> callers = CallersOf(game, "EditorSystem.LoadModIntoEditor", "EditorSystem.OpenMod", "EditorSystem.CreateNewMod",
                "EditorSystem.OpenModWithPopupPrompt", "EditorSystem.SetCurrentMod");
            void Pin(string callee, params string[] expected)
            {
                List<string> found = callers[callee];
                check($"{callee} is called only by {string.Join(", ", expected)} (found: {string.Join(", ", found)})", found.SequenceEqual(expected.OrderBy(n => n, StringComparer.Ordinal)));
            }
            Pin("EditorSystem.LoadModIntoEditor", "EditorSystem.CreateNewMod", "EditorSystem.OpenMod");
            // OpenMod: the automated test, OpenModWithPopupPrompt and its unsaved-changes lambda (nothing runs after the call in any of them)
            Pin("EditorSystem.OpenMod", "AT_OpenMod/<generated>", "EditorSystem.OpenModWithPopupPrompt", "EditorSystem/<generated>");
            // OpenModWithPopupPrompt: Open dialog, Recent Mods (File menu), Published Mods: each closes its popups first, nothing after the call
            Pin("EditorSystem.OpenModWithPopupPrompt", "LoadModPopup.LoadModIntoEditor", "MainMenuWidget.Draw", "PublishedModsPopup.LoadModIntoEditor");
            // CreateNewMod: the automated test and the New Mod dialog's action (guarded as a whole: it adds a list entry afterwards)
            Pin("EditorSystem.CreateNewMod", "AT_NewMod/<generated>", "NewModPopup.CreateNewMod");
            // SetCurrentMod: LoadModIntoEditor (non-null, passes), editor shutdown (OnDisable ends the hold; OnDestroy runs after it), the two
            // Delete Mod results (guarded as a whole), Close Mod in the File menu and its unsaved-changes lambda (refused: nothing after the
            // call), a failed editor load (PostLoadCleanup; no load can run during a capture: it needs a loaded mod and opens are refused)
            Pin("EditorSystem.SetCurrentMod", "EditorSystem.LoadModIntoEditor", "EditorSystem.OnDestroy", "EditorSystem.OnDisable", "LoadModPopup.DeleteModResult",
                "MainMenuWidget.DeleteModResult", "MainMenuWidget.Draw", "MainMenuWidget/<generated>", "PummelMod.PostLoadCleanup");
            MethodBase prompt = ed == null ? null : ed.GetMethods(all).Cast<MethodBase>().Concat(ed.GetNestedTypes(all).SelectMany(n => n.GetMethods(all)).Cast<MethodBase>())
                .FirstOrDefault(m => m.Name == "OpenModWithPopupPrompt");
            HashSet<string> promptCalls = new HashSet<string>(StringComparer.Ordinal);
            if (ed != null)
            {
                foreach (MethodBase m in ed.GetMethods(all).Cast<MethodBase>().Concat(ed.GetNestedTypes(all).SelectMany(n => n.GetMethods(all)).Cast<MethodBase>())
                    .Where(m => m.Name.Contains("OpenModWithPopupPrompt") && m.GetMethodBody() != null))
                {
                    promptCalls.UnionWith(Calls(m));
                }
            }
            check("OpenModWithPopupPrompt (the Open Mod dialog) ends in OpenMod", prompt != null && promptCalls.Contains("EditorSystem.OpenMod"));
        }

        /// <summary>Callers ("Type.Method", nested compiler-generated types as "Outer/Nested.Method") of each callee, sorted, whole assembly.</summary>
        private static Dictionary<string, List<string>> CallersOf(Assembly game, params string[] callees)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Dictionary<string, HashSet<string>> found = callees.ToDictionary(c => c, c => new HashSet<string>(StringComparer.Ordinal));
            foreach (Type t in SafeTypes(game))
            {
                List<MethodBase> methods;
                string typeName;
                try
                {
                    methods = t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)).ToList();
                    // compiler-generated lambda/iterator classes: "Outer/<generated>" (their numbered names change with any rebuild)
                    typeName = t.DeclaringType == null ? t.Name : t.DeclaringType.Name + "/" + (t.Name.StartsWith("<", StringComparison.Ordinal) ? "<generated>" : t.Name);
                }
                catch (Exception)
                {
                    // a type that cannot be loaded for reflection (e.g. it needs a framework type this runtime lacks): its callers are not seen
                    Console.WriteLine("    caller scan: type skipped (cannot be loaded for reflection): " + t.FullName);
                    continue;
                }
                foreach (MethodBase m in methods)
                {
                    try
                    {
                        if (m.GetMethodBody() == null)
                        {
                            continue;
                        }
                        HashSet<string> calls = Calls(m);
                        foreach (string c in callees)
                        {
                            if (calls.Contains(c))
                            {
                                found[c].Add(typeName.EndsWith("<generated>", StringComparison.Ordinal) ? typeName : typeName + "." + m.Name);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // a method whose body cannot be read is not a caller we can see
                    }
                }
            }
            return found.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(n => n, StringComparer.Ordinal).ToList());
        }

        private static void SettingsDrift(Assembly game, Action<string, bool> check)
        {
            const BindingFlags pub = BindingFlags.Instance | BindingFlags.Public;
            List<RequiredMember> settings = RequiredMembers.All.Where(m => m.UsedBy != null && (m.UsedBy.StartsWith("settings rules", StringComparison.Ordinal) || m.UsedBy.StartsWith("settings tools", StringComparison.Ordinal) || m.UsedBy == "capture_preview_image")).ToList();
            List<string> problems = new List<string>();
            foreach (RequiredMember m in settings)
            {
                string why = RequiredMembers.Resolve(game, m);
                if (why != null)
                {
                    problems.Add(m.Kind + " " + m.Id + ": " + why);
                }
            }
            foreach (string p in problems)
            {
                Console.WriteLine("    settings drift: " + p);
            }
            int fieldCount = SettingsRules.All.SelectMany(s => s.Fields).Count(f => f.Member != null);
            check($"every settings field resolves in the real game ({fieldCount} fields with a member, {settings.Count(m => m.Kind == "enumType")} enums, {settings.Count(m => m.Kind == "settingsRange")} slider ranges, {settings.Count(m => m.Kind == "method")} methods; {problems.Count} problems)", problems.Count == 0 && fieldCount > 200);

            // completeness: every public instance member of these classes is a table field or a listed hidden member
            (string type, string target)[] audited =
            {
                ("SimpleModPlayerSettings", "player"),
                ("SimpleModControllerSettings", "controller"),
                ("BoardModCameraSettings", "camera"),
                ("ModLightingSettings", "lighting"),
            };
            foreach (var a in audited)
            {
                Type t = game.GetType(a.type, false);
                HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);
                foreach (SettingsSection s in SettingsRules.All)
                {
                    foreach (ModSettingField f in s.Fields.Where(f => f.Target == a.target && f.Member != null))
                    {
                        known.Add(f.Member);
                    }
                    if (s.HiddenTarget == a.target)
                    {
                        foreach (string h in s.HiddenNames)
                        {
                            known.Add(h);
                        }
                    }
                }
                string[] actual = t == null ? new string[0] : t.GetFields(pub).Select(f => f.Name).ToArray();
                string[] unknown = actual.Where(n => !known.Contains(n)).ToArray();
                if (unknown.Length > 0)
                {
                    Console.WriteLine("    " + a.type + ": public fields the settings rules do not know: " + string.Join(", ", unknown));
                }
                check($"every public field of {a.type} is in the settings rules or listed as not shown ({actual.Length} fields)", t != null && unknown.Length == 0);
            }
            Type board = game.GetType("BoardModSettings", false);
            string[] boardFields = board?.GetFields(pub | BindingFlags.DeclaredOnly).Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray() ?? new string[0];
            check("BoardModSettings declares exactly CameraSettings, CustomSpaceSettings, UseCustomBoardSize (" + string.Join(", ", boardFields) + ")",
                boardFields.SequenceEqual(new[] { "CameraSettings", "CustomSpaceSettings", "UseCustomBoardSize" }));
            Type minigame = game.GetType("MinigameModSettings", false);
            string[] mgFields = minigame?.GetFields(pub | BindingFlags.DeclaredOnly).Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray() ?? new string[0];
            check("MinigameModSettings declares exactly SimpleMinigameSettings, SimpleModPlayerSettings and the two unused [NonSerialized] custom-prefab fields (" + string.Join(", ", mgFields) + ")",
                mgFields.SequenceEqual(new[] { "SimpleMinigameSettings", "SimpleModPlayerSettings", "m_customMinigameController", "m_customMinigamePlayer" }.OrderBy(n => n, StringComparer.Ordinal)));

            // the two screen-effect asset-less facts the tools rely on
            Type profile = RequiredMembers.ResolveType(game, "UnityEngine.Rendering.PostProcessing.PostProcessProfile", "Unity.Postprocessing.Runtime");
            Type effect = RequiredMembers.ResolveType(game, "UnityEngine.Rendering.PostProcessing.PostProcessEffectSettings", "Unity.Postprocessing.Runtime");
            bool allEffects = effect != null && SettingsRules.EffectTypeNames.All(n =>
            {
                Type et = RequiredMembers.ResolveType(game, "UnityEngine.Rendering.PostProcessing." + n, "Unity.Postprocessing.Runtime");
                return et != null && effect.IsAssignableFrom(et);
            });
            check("all 8 effect types the rules name derive from PostProcessEffectSettings and the profile has AddSettings(Type) / RemoveSettings(Type)",
                allEffects && profile != null && profile.GetMethod("AddSettings", new[] { typeof(Type) }) != null && profile.GetMethod("RemoveSettings", new[] { typeof(Type) }) != null);
        }

        /// <summary>
        /// The recording hooks of the playtest event feed (Hooks/PlaytestHooks.cs): ModLogicTrigger.RunActionsLocal(ModEvent) and the
        /// declared ModAction.Execute(ModEvent) overrides (resolved the same way the hook installer does). A game update that renames
        /// or re-signs them would silently reduce the feed to the diff-based events, so it is caught here.
        /// </summary>
        private static void PlaytestHookTargets(Assembly game, Action<string, bool> check)
        {
            Type trigger = game.GetType("ModSystem.Logic.ModLogicTrigger", false);
            Type action = game.GetType("ModSystem.Logic.ModAction", false);
            Type ev = game.GetType("ModSystem.Logic.ModEvent", false);
            MethodInfo run = trigger?.GetMethod("RunActionsLocal", BindingFlags.Instance | BindingFlags.Public, null, new[] { ev }, null);
            check("hook target ModLogicTrigger.RunActionsLocal(ModEvent) exists", run != null && run.ReturnType == typeof(void));
            string[] expected = { "GiveItemAction", "ModAction", "MoveSpaceAction", "SetConnectionActiveAction", "SetGravityAction", "SpawnPrefabAction", "TargetAction", "WaitAction" };
            string[] found = action == null || ev == null
                ? new string[0]
                : SafeTypes(game)
                    .Where(t => t == action || t.IsSubclassOf(action))
                    .Where(t => t.GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { ev }, null) != null)
                    .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            check("hook targets: ModAction.Execute(ModEvent) is declared by exactly " + string.Join(", ", expected) + " (found: " + string.Join(", ", found) + ")",
                found.SequenceEqual(expected));
            check("hook target ModAction.Execute returns YieldInstruction",
                action?.GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { ev }, null)?.ReturnType.Name == "YieldInstruction");
        }

        private static Type[] SafeTypes(Assembly a)
        {
            try
            {
                return a.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null).ToArray();
            }
        }

        /// <summary>GameDir as the project file saw it at build time (same property the plugin csproj uses).</summary>
        private static string ReadGameDir()
        {
            foreach (AssemblyMetadataAttribute a in typeof(GameDriftTest).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false).Cast<AssemblyMetadataAttribute>())
            {
                if (a.Key == "GameDir" && !string.IsNullOrWhiteSpace(a.Value))
                {
                    return a.Value;
                }
            }
            return null;
        }

        /// <summary>Dependencies of the game assemblies: the game's Managed folder first, then the runtime's own (framework) assemblies.</summary>
        private static Assembly ReflectionOnlyResolve(ResolveEventArgs args, string managed)
        {
            string simple = new AssemblyName(args.Name).Name;
            string candidate = Path.Combine(managed, simple + ".dll");
            if (File.Exists(candidate))
            {
                return Assembly.ReflectionOnlyLoadFrom(candidate);
            }
            try
            {
                return Assembly.ReflectionOnlyLoad(args.Name);
            }
            catch (Exception)
            {
                return Assembly.ReflectionOnlyLoad(simple);
            }
        }
    }
}
