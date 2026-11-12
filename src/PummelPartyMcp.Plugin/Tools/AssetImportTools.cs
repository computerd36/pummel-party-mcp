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
    /// Plugin 0.8.0, Phase 4 part B: get_asset, list_asset_rules (read); import_asset, set_import_settings, create_material,
    /// set_material, remove_asset (write, guard OpenMod, never inside run_batch). The plugin copies the file itself (never the
    /// editor's AddAsset(copy:true), which can delete the source) and registers it with PummelMod.ImportFile; removal replicates
    /// PummelMod.RemoveAsset after a path/class/reference check. See docs/architecture/07-asset-import.md and docs/plugin-api.md.
    /// </summary>
    internal static class AssetImportTools
    {
        public static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan EditTimeout = TimeSpan.FromSeconds(30);

        private sealed class ImportArgs
        {
            public string Path;
            public string Name;
            public AssetImportKind? Type;
        }

        private sealed class SettingsArgs
        {
            public string Guid;
            public JObject Values;
        }

        private sealed class CreateMaterialArgs
        {
            public string Name;
            public string Shader;
            public JObject Properties;
        }

        private sealed class SetMaterialArgs
        {
            public string Guid;
            public string Shader;
            public JObject Properties;
        }

        private sealed class RemoveArgs
        {
            public string Guid;
            public bool Force;
        }

        public static void Register(ToolRegistry registry, EditServices services, Func<AssetLimits> limits)
        {
            registry.Register(ToolDefinition.Read("get_asset", ctx =>
            {
                string guid = EditorAccess.NormalizeGuid(ctx.GetString("guid", required: true), "guid");
                return ctx.OnMainThread(() => GetAsset(guid, limits()));
            }));
            registry.Register(ToolDefinition.Read("list_asset_rules", ctx => ctx.OnMainThread(() => ListRules(limits()))));

            registry.Register(ToolDefinition.Write("import_asset", WriteGuard.OpenMod(), ParseImport, inv => Import(inv, services, limits), ImportTimeout));
            registry.Register(ToolDefinition.Write("set_import_settings", WriteGuard.OpenMod(), ParseSettings, inv => SetImportSettings(inv, services), EditTimeout));
            registry.Register(ToolDefinition.Write("create_material", WriteGuard.OpenMod(), ParseCreateMaterial, inv => CreateMaterial(inv, services), EditTimeout));
            registry.Register(ToolDefinition.Write("set_material", WriteGuard.OpenMod(), ParseSetMaterial, inv => SetMaterial(inv, services), EditTimeout));
            registry.Register(ToolDefinition.Write("remove_asset", WriteGuard.OpenMod(), ParseRemove, inv => Remove(inv, services), EditTimeout));
        }

        // ------------------------------------------------------------------ parsing (HTTP thread, no game access)

        private static ImportArgs ParseImport(ToolContext ctx)
        {
            string path = ctx.GetString("path", required: true);
            string why = AssetImportRules.CheckSourcePathText(path);
            if (why != null)
            {
                throw RpcException.InvalidParams(why);
            }
            string name = ctx.GetString("name");
            if (name != null)
            {
                string nameProblem = AssetImportRules.ValidateName(name);
                if (nameProblem != null)
                {
                    throw RpcException.InvalidParams($"'name' {nameProblem}");
                }
            }
            AssetImportKind? kind = null;
            string type = ctx.GetString("type");
            if (type != null)
            {
                switch (type.ToLowerInvariant())
                {
                    case "texture": kind = AssetImportKind.Texture; break;
                    case "prop":
                    case "mesh": kind = AssetImportKind.Mesh; break;
                    case "audio":
                    case "audioclip": kind = AssetImportKind.Audio; break;
                    default: throw RpcException.InvalidParams("'type' must be Texture, Prop (Mesh) or AudioClip (Audio); it is optional and only checked against the file");
                }
            }
            return new ImportArgs { Path = path, Name = name, Type = kind };
        }

        private static SettingsArgs ParseSettings(ToolContext ctx)
        {
            string guid = EditorAccess.NormalizeGuid(ctx.GetString("guid", required: true), "guid");
            if (!(ctx.Params["values"] is JObject values) || values.Count == 0)
            {
                throw RpcException.InvalidParams("'values' must be an object {field: value, ...} with at least one field (list_asset_rules / get_asset show the fields)");
            }
            return new SettingsArgs { Guid = guid, Values = values };
        }

        private static string OptionalNameCheck(string name)
        {
            if (name == null)
            {
                return null;
            }
            string problem = AssetImportRules.ValidateName(name);
            if (problem != null)
            {
                throw RpcException.InvalidParams($"'name' {problem}");
            }
            return name;
        }

        private static JObject OptionalProperties(ToolContext ctx)
        {
            JToken t = ctx.Params["properties"];
            if (t == null || t.Type == JTokenType.Null)
            {
                return null;
            }
            if (!(t is JObject o) || o.Count == 0)
            {
                throw RpcException.InvalidParams("'properties' must be an object {property: value, ...} (list_asset_rules shows the properties per shader)");
            }
            return o;
        }

        private static CreateMaterialArgs ParseCreateMaterial(ToolContext ctx) => new CreateMaterialArgs
        {
            Name = OptionalNameCheck(ctx.GetString("name")),
            Shader = ctx.GetString("shader"),
            Properties = OptionalProperties(ctx),
        };

        private static SetMaterialArgs ParseSetMaterial(ToolContext ctx)
        {
            SetMaterialArgs a = new SetMaterialArgs
            {
                Guid = EditorAccess.NormalizeGuid(ctx.GetString("guid", required: true), "guid"),
                Shader = ctx.GetString("shader"),
                Properties = OptionalProperties(ctx),
            };
            if (a.Shader == null && a.Properties == null)
            {
                throw RpcException.InvalidParams("give 'shader' and/or 'properties'");
            }
            return a;
        }

        private static RemoveArgs ParseRemove(ToolContext ctx) => new RemoveArgs
        {
            Guid = EditorAccess.NormalizeGuid(ctx.GetString("guid", required: true), "guid"),
            Force = ctx.GetBool("force", false),
        };

        // ------------------------------------------------------------------ shared preconditions

        private sealed class Ready
        {
            public EditorSystem Ed;
            public PummelMod Mod;
            public string AssetsFull;
        }

        private static Ready RequireReady(bool noPrefab)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            EditContext.RequireEditReady(ed);
            if (noPrefab && ed.CurrentPrefab != null)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"a prefab ('{ed.CurrentPrefab.name}') is being edited; return to the scene first");
            }
            return new Ready { Ed = ed, Mod = mod, AssetsFull = AssetAccess.AssetsFolderOf(mod) };
        }

        private static ModAssetExternal RequireOwnAsset(Ready r, string guid, string tool)
        {
            ModAsset a = AssetAccess.Find(r.Ed, guid);
            if (a == null)
            {
                throw RpcException.NotFound($"no asset with guid {guid}");
            }
            if (!(a is ModAssetExternal ext) || ValueConverter.LocationName(a) != "external" || !AssetAccess.InModList(r.Mod, a))
            {
                throw RpcException.InvalidParams($"'{a.name}' is a {ValueConverter.LocationName(a)} asset; {tool} only acts on the open sandbox mod's own custom assets (list_assets location external)");
            }
            if (!AssetImportRules.IsStrictlyUnder(r.AssetsFull, ext.filePath))
            {
                throw RpcException.InvalidParams($"'{a.name}' is external but its file is not inside the open mod's Assets folder; {tool} does not act on it");
            }
            return ext;
        }

        private static string RelOf(Ready r, ModAssetExternal a) => AssetAccess.RelativePath(r.AssetsFull, a.filePath);

        // ------------------------------------------------------------------ get_asset

        private static JObject GetAsset(string guid, AssetLimits limits)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            PlaytestAccess.RequireEditMode(ed, "get_asset");
            ModAsset a = AssetAccess.Find(ed, guid);
            if (a == null || ValueConverter.LocationName(a) == "runtime")
            {
                throw RpcException.NotFound($"no asset with guid {guid}");
            }
            string assetsFull = SafeAssetsFolder(mod);
            JObject result = AssetAccess.Row(a, assetsFull);
            bool own = assetsFull != null && AssetAccess.IsOwnCustom(mod, a, assetsFull);
            result["ownCustomAsset"] = own;
            string kind = AssetAccess.Kind(a);

            if (a is PummelTexture pt && own)
            {
                Texture2D tex = pt.UnityTexture;
                if (tex != null)
                {
                    result["width"] = tex.width;
                    result["height"] = tex.height;
                }
                result["importSettings"] = new JObject
                {
                    ["compression"] = pt.GetCompression().ToString(),
                    ["filterMode"] = pt.GetFilterMode().ToString(),
                    ["wrapMode"] = pt.GetWrapMode().ToString(),
                };
                result["importSettingFields"] = ImportSettingRules.ToJson("Texture");
            }
            else if (a is PummelProp pp && own)
            {
                Mesh mesh = pp.MeshAsset?.Mesh;
                if (mesh != null)
                {
                    int tris = 0;
                    for (int i = 0; i < mesh.subMeshCount; i++)
                    {
                        tris += (int)(mesh.GetIndexCount(i) / 3);
                    }
                    result["vertices"] = mesh.vertexCount;
                    result["triangles"] = tris;
                    result["subMeshes"] = mesh.subMeshCount;
                }
                result["importSettings"] = new JObject { ["recalculateNormals"] = pp.MeshAsset != null && pp.MeshAsset.RecalculateNormals };
                result["importSettingFields"] = ImportSettingRules.ToJson("Prop");
            }
            else if (a is PummelAudioClip pa && own)
            {
                AudioClip clip = pa.UnityAudioClip;
                if (clip != null)
                {
                    result["lengthSeconds"] = Math.Round(clip.length, 3);
                    result["frequency"] = clip.frequency;
                    result["channels"] = clip.channels;
                }
                result["importSettings"] = new JObject();
                result["importSettingsNote"] = "audio clips have no import settings in the editor";
            }
            else if (a is PummelMaterial pm && own)
            {
                Material mat = pm.UnityMaterial;
                if (mat != null && mat.shader != null)
                {
                    result["shader"] = mat.shader.name;
                    ShaderRule rule = MaterialRules.ForShader(mat.shader.name);
                    result["knownShader"] = rule != null;
                    if (rule != null)
                    {
                        MaterialAccess state = new MaterialAccess(ed, mat, mat.shader);
                        result["properties"] = state.Describe();
                        result["propertyFields"] = MaterialRules.RulesJson()[string.Join(" | ", rule.ShaderNames)]?["properties"];
                    }
                    else
                    {
                        result["propertiesNote"] = "this shader has no known inspector in this plugin; its properties are not exposed (see list_asset_rules)";
                    }
                }
            }
            else if (a is PummelPrefab && own)
            {
                result["note"] = "prefabs are read-only here (no prefab tools in this plugin version); use get_object / get_hierarchy for the objects that use it";
            }

            if (own)
            {
                string why = AssetRemovalRules.Check(Facts(ed, a, assetsFull));
                result["removable"] = why == null;
                if (why != null)
                {
                    result["removableReason"] = why;
                }
            }

            result["references"] = AssetAccess.RefsJson(AssetAccess.FindReferences(ed, mod, a));
            result["limits"] = LimitsJson(limits);
            return result;
        }

        private static string SafeAssetsFolder(PummelMod mod)
        {
            try
            {
                return AssetAccess.AssetsFolderOf(mod);
            }
            catch (RpcException)
            {
                return null; // e.g. a real mod is open: reads still work, own-asset facts are omitted
            }
        }

        private static JObject LimitsJson(AssetLimits l) => new JObject
        {
            ["maxImportFileMB"] = l.MaxImportFileMB,
            ["maxModAssetsMB"] = l.MaxModAssetsMB,
            ["configSection"] = "[Assets] MaxImportFileMB, MaxModAssetsMB in BepInEx\\config\\com.pummelpartymcp.plugin.cfg (re-read on every call)",
        };

        private static RemovalFacts Facts(EditorSystem ed, ModAsset a, string assetsFull)
        {
            ModAssetExternal ext = a as ModAssetExternal;
            EditorSelectionManager sel = ed.GetSelectionManager();
            RemovalFacts f = new RemovalFacts
            {
                IsExternal = a.ExternalAsset && ValueConverter.LocationName(a) == "external",
                IsBuiltIn = ValueConverter.LocationName(a) == "internal",
                ClassName = a.GetType().Name,
                FilePath = ext?.filePath,
                AssetsFolder = assetsFull,
                IsOpenPrefab = ed.CurrentPrefab != null && ReferenceEquals(ed.CurrentPrefab, a),
                IsSelectedAndDirty = ext != null && sel.IsSelected(ext) && sel.IsSelectedAssetDirty(),
            };
            if (a is PummelProp pp)
            {
                f.HasNested = true;
                f.NestedPath = pp.MeshAsset?.filePath;
            }
            return f;
        }

        // ------------------------------------------------------------------ list_asset_rules

        private static JObject ListRules(AssetLimits limits)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            JObject import = new JObject
            {
                ["extensions"] = new JArray(AssetImportRules.AllowedExtensions),
                ["destinations"] = new JObject { ["Texture"] = "Assets/Textures (png, jpg, jpeg)", ["Prop"] = "Assets/Props (obj)", ["AudioClip"] = "Assets/Audio (wav, mp3, ogg)" },
                ["limits"] = LimitsJson(limits),
                ["caps"] = new JObject
                {
                    ["maxNameLength"] = AssetImportRules.MaxStemLength,
                    ["nameCharacters"] = "A-Z a-z 0-9 space _ . ( ) -",
                    ["maxTextureSidePixels"] = AssetImportRules.MaxTextureSide,
                    ["maxObjVertexLines"] = AssetImportRules.MaxObjVertexLines,
                    ["maxNumberedDuplicates"] = AssetImportRules.MaxDuplicateSuffix,
                },
                ["sources"] = "absolute local paths only (X:\\...); UNC/network/device paths are refused in this version; the source is only read (never moved, changed, locked or deleted)",
                ["noOverwrite"] = "an existing name gets ' (n)' appended; nothing is ever overwritten",
            };
            JObject importSettings = new JObject();
            foreach (string t in ImportSettingRules.AssetTypes)
            {
                importSettings[t] = ImportSettingRules.ToJson(t);
            }
            JArray shaders = new JArray();
            try
            {
                WorkshopController wc = WorkshopController.Instance;
                if (wc?.Assets?.assets != null)
                {
                    foreach (ModAsset a in wc.Assets.assets)
                    {
                        if (a != null && ValueConverter.AssetTypeName(a) == "Shader")
                        {
                            shaders.Add(new JObject { ["guid"] = a.guid.ToString(), ["name"] = a.name });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                GameReflection.WarnOnce("assetrules.shaders", "list_asset_rules: cannot list shaders: " + e.Message);
            }
            return new JObject
            {
                ["import"] = import,
                ["importSettings"] = importSettings,
                ["materials"] = new JObject
                {
                    ["shaderRules"] = MaterialRules.RulesJson(),
                    ["builtInShaderAssets"] = shaders,
                    ["note"] = "create_material/set_material accept any built-in Shader asset (name or guid) but expose properties only for the shaders with an entry in shaderRules; other shaders' properties are refused (the generic inspector is not supported)",
                },
                ["removal"] = "remove_asset: own custom textures, props, audio clips and materials only; refused while referenced unless force (the preview image is never removed); not undoable",
            };
        }

        // ------------------------------------------------------------------ import_asset

        private static void Import(WriteInvocation inv, EditServices services, Func<AssetLimits> limitsFn)
        {
            ImportArgs args = inv.ArgsAs<ImportArgs>();
            Ready r = RequireReady(noPrefab: true);
            AssetLimits limits = limitsFn();

            ImportSource src = AssetImportRules.CheckSource(args.Path, r.AssetsFull, limits);
            if (args.Type.HasValue && args.Type.Value != src.Kind)
            {
                throw RpcException.InvalidParams($"'type' {args.Type.Value} does not match the file: a {src.Extension} file is a {AssetImportRules.TypeNameOf(src.Kind)}");
            }
            bool derived = args.Name == null;
            string stem = args.Name ?? AssetImportRules.DeriveStem(src.StemRaw);
            string totalProblem = AssetImportRules.CheckModTotal(r.AssetsFull, src.Length, limits);
            if (totalProblem != null)
            {
                throw RpcException.InvalidParams(totalProblem);
            }
            ImportDestination plan = AssetImportRules.ChooseFree(r.AssetsFull, src.Kind, stem, src.Extension); // refuses when all names are taken

            inv.Commit(TimeSpan.FromSeconds(15)); // from here on the caller always gets this call's own result
            services.Observe();

            AssetImportRules.Created created = AssetImportRules.CopyIntoMod(src, r.AssetsFull, stem); // rolls back its own file on failure
            ModAssetExternal asset = null;
            try
            {
                List<ModAssetExternal> list = r.Mod.ImportFile(new FileInfo(created.Destination.FullPath));
                if (list == null || list.Count == 0)
                {
                    throw new InvalidOperationException(list == null
                        ? "the game's importer rejected the file (see get_log)"
                        : "the game's importer could not read the file and registered nothing (see get_log)");
                }
                asset = list[0];
                ModAsset registered = r.Mod.Assets.GetAssetWithGuid(asset.guid);
                if (registered == null || !(registered is ModAssetExternal) || (registered.unityObjectAddressable == null && !(registered is PummelProp)))
                {
                    throw new InvalidOperationException("the asset was not registered in the mod");
                }
                if (!AssetImportRules.IsStrictlyUnder(r.AssetsFull, ((ModAssetExternal)registered).filePath))
                {
                    throw new InvalidOperationException("the registered asset's file is not inside the mod's Assets folder");
                }
                asset = (ModAssetExternal)registered;
            }
            catch (Exception e)
            {
                throw ImportFailure(r, created, e);
            }

            JObject row = AssetAccess.Row(asset, r.AssetsFull);
            JObject copy = new JObject
            {
                ["relativePath"] = AssetAccess.RelativePath(r.AssetsFull, created.Destination.FullPath),
                ["sizeBytes"] = created.Bytes,
                ["sha256"] = created.Sha256,
            };
            JObject result = new JObject
            {
                ["asset"] = row,
                ["copy"] = copy,
                ["source"] = new JObject { ["path"] = src.FullPath, ["sizeBytes"] = src.Length, ["modified"] = false },
                ["nameDerived"] = derived,
                ["renamedBecauseExists"] = created.Destination.Suffix > 0,
                ["checks"] = new JArray(src.Checks.ToArray()),
                ["limits"] = LimitsJson(limits),
                ["modAssetsBytesAfter"] = AssetImportRules.FolderSize(r.AssetsFull),
                ["undoable"] = false,
                ["note"] = "the file and its .pmeta exist on disk NOW (not only after save_mod); they stay if you never save. The source file was only read. " +
                           "The thumbnail appears in the Asset Browser one frame later. Use remove_asset to delete it again. The game may also have created an empty PummelModCache folder in its data folder (PNG/JPG import).",
            };
            if (src.Kind == AssetImportKind.Mesh)
            {
                result["note"] = (string)result["note"] + " A neighbouring .mtl file is not copied (materials are set with create_material/set_material).";
            }
            inv.Complete(result);
        }

        private static RpcException ImportFailure(Ready r, AssetImportRules.Created created, Exception e)
        {
            List<string> done = new List<string>();
            try
            {
                // registered at our path (exception after registration): unregister with the editor's own removal, which deletes only
                // the two files at that path (they are ours: the path equals the destination this call created)
                ModAssetExternal reg = r.Mod.Assets.assets.OfType<ModAssetExternal>().FirstOrDefault(x =>
                    !string.IsNullOrEmpty(x.filePath) && string.Equals(SafeFull(x.filePath), SafeFull(created.Destination.FullPath), StringComparison.OrdinalIgnoreCase));
                if (reg != null)
                {
                    r.Mod.RemoveAsset(reg);
                    done.Add("unregistered the asset");
                }
            }
            catch (Exception ex)
            {
                done.Add("could not unregister: " + ex.Message);
            }
            AssetImportRules.CleanupReport report = AssetImportRules.Rollback(created);
            done.AddRange(report.Deleted.Select(d => "deleted " + d));
            done.AddRange(report.Problems);
            JObject data = new JObject { ["cleanedUp"] = new JArray(done.ToArray()), ["sourceTouched"] = false };
            return new RpcException(ErrorCodes.ImportFailed,
                $"the import failed ({e.Message}); cleaned up: {(done.Count == 0 ? "nothing left to clean" : string.Join("; ", done.ToArray()))}; the source file was not touched", data);
        }

        private static string SafeFull(string p)
        {
            try
            {
                return Path.GetFullPath(p);
            }
            catch (Exception)
            {
                return p;
            }
        }

        // ------------------------------------------------------------------ set_import_settings

        private static string CurrentSetting(ModAssetExternal a, ImportSettingField f)
        {
            if (a is PummelTexture pt)
            {
                switch (f.Name)
                {
                    case "compression": return pt.GetCompression().ToString();
                    case "filterMode": return pt.GetFilterMode().ToString();
                    default: return pt.GetWrapMode().ToString();
                }
            }
            PummelProp pp = (PummelProp)a;
            return pp.MeshAsset.RecalculateNormals ? "true" : "false";
        }

        private static void WriteSetting(ModAssetExternal a, ImportSettingField f, string value)
        {
            if (a is PummelTexture pt)
            {
                System.Reflection.FieldInfo fi = f.Name == "compression" ? GameReflection.RequireField(typeof(PummelTexture), "m_compression")
                    : f.Name == "filterMode" ? GameReflection.RequireField(typeof(PummelTexture), "m_filter")
                    : GameReflection.RequireField(typeof(PummelTexture), "m_wrapMode");
                fi.SetValue(pt, Enum.Parse(fi.FieldType, value));
                return;
            }
            ((PummelProp)a).MeshAsset.RecalculateNormals = value == "true";
        }

        private static void SetImportSettings(WriteInvocation inv, EditServices services)
        {
            SettingsArgs args = inv.ArgsAs<SettingsArgs>();
            Ready r = RequireReady(noPrefab: true);
            ModAssetExternal asset = RequireOwnAsset(r, args.Guid, "set_import_settings");
            string type = asset is PummelTexture ? "Texture" : asset is PummelProp ? "Prop" : null;
            if (type == null)
            {
                string label = AssetAccess.Kind(asset);
                throw RpcException.InvalidParams(asset is PummelMaterial
                    ? "materials have no import settings; use set_material"
                    : $"{label} assets have no import settings in the editor (only textures and props do)");
            }
            if (!File.Exists(asset.filePath))
            {
                throw new RpcException(ErrorCodes.InvalidState, $"the file of '{asset.name}' is missing on disk; the editor's re-import would remove the asset, so nothing was done");
            }

            ImportSettingsPlan plan = ImportSettingRules.Plan(type, args.Values, f => CurrentSetting(asset, f));
            JObject previous = new JObject();
            foreach (ImportSettingChange c in plan.Changes)
            {
                previous[c.Field.Name] = c.OldValue;
            }
            if (plan.Changes.Count == 0)
            {
                inv.Complete(new JObject
                {
                    ["guid"] = args.Guid,
                    ["changed"] = false,
                    ["unchanged"] = new JArray(plan.Unchanged.ToArray()),
                    ["note"] = "every given value already matches; nothing was done",
                });
                return;
            }

            bool needsReimport = plan.Changes.Any(c => c.Field.Name == "compression" || c.Field.Name == "recalculateNormals");
            AssetAccess.RefResult before = null;
            List<KeyValuePair<PummelMaterial, string>> materialSlots = new List<KeyValuePair<PummelMaterial, string>>();
            if (asset is PummelTexture && needsReimport)
            {
                // The editor's Apply re-imports: the decoder makes a NEW Texture2D and the asset list swaps it in. Things that hold the
                // old object by identity (components, action parameters, the preview image) would no longer find the asset and would be
                // saved as "none". Materials' texture slots are re-pointed by this tool; for everything else the call is refused.
                before = AssetAccess.FindReferences(r.Ed, r.Mod, asset);
                List<AssetReference> blocking = before.Items.Where(i => i.Kind != "material").ToList();
                if (blocking.Count > 0 || before.Incomplete)
                {
                    throw new RpcException(ErrorCodes.AssetInUse,
                        "changing the compression re-imports the texture and would break these references (the editor would save them as none): " +
                        (blocking.Count == 0 ? "the reference scan was incomplete" : string.Join("; ", blocking.Take(12).Select(ReferenceScan.Describe).ToArray())) +
                        ". Clear them first (or change only filterMode / wrapMode, which are applied in place), or import the texture again under another name.",
                        new JObject { ["references"] = new JArray(blocking.Select(ReferenceScan.ToJson).ToArray()), ["scanComplete"] = !before.Incomplete });
                }
                foreach (AssetReference m in before.Items)
                {
                    PummelMaterial pm = AssetAccess.Find(r.Ed, m.OwnerGuid) as PummelMaterial;
                    if (pm != null)
                    {
                        materialSlots.Add(new KeyValuePair<PummelMaterial, string>(pm, m.Where));
                    }
                }
            }

            inv.Commit(TimeSpan.FromSeconds(3));
            services.Observe();
            UnityEngine.Object oldObject = asset.unityObjectAddressable;
            Mesh oldMesh = (asset as PummelProp)?.MeshAsset?.Mesh;
            List<string> applied = new List<string>();
            string failure = null;
            try
            {
                foreach (ImportSettingChange c in plan.Changes)
                {
                    WriteSetting(asset, c.Field, c.NewValue);
                    applied.Add(c.Field.Name);
                }
                if (needsReimport)
                {
                    r.Ed.ReimportAsset(asset); // Apply: Save() (.pmeta), then PummelMod.ImportFile on the file that is inside the mod
                    if (asset is PummelTexture)
                    {
                        UnityEngine.Object now = asset.unityObjectAddressable;
                        if (now == null || ReferenceEquals(now, oldObject))
                        {
                            throw new InvalidOperationException("the re-import did not produce a new texture (see get_log)");
                        }
                        foreach (KeyValuePair<PummelMaterial, string> slot in materialSlots)
                        {
                            slot.Key.UnityMaterial.SetTexture(slot.Value, (Texture)now);
                        }
                    }
                    else if (asset is PummelProp pp && ReferenceEquals(pp.MeshAsset?.Mesh, oldMesh))
                    {
                        throw new InvalidOperationException("the re-import did not produce a new mesh (see get_log)");
                    }
                }
                else
                {
                    Texture2D tex = ((PummelTexture)asset).UnityTexture;
                    if (tex != null)
                    {
                        tex.filterMode = ((PummelTexture)asset).GetFilterMode();
                        tex.wrapMode = ((PummelTexture)asset).GetWrapMode();
                    }
                    asset.Save(); // writes the .pmeta only
                }
            }
            catch (Exception e)
            {
                failure = e.Message;
                foreach (ImportSettingChange c in plan.Changes.Where(c => applied.Contains(c.Field.Name)))
                {
                    try
                    {
                        WriteSetting(asset, c.Field, c.OldValue);
                    }
                    catch (Exception)
                    {
                        // best effort
                    }
                }
                try
                {
                    asset.Save();
                }
                catch (Exception)
                {
                    // best effort
                }
            }
            EditorSelectionManager sel = r.Ed.GetSelectionManager();
            if (sel.IsSelected(asset))
            {
                sel.SerializeSelectedObject(); // new clean baseline, like ReimportAsset does (no "Unapplied Import Settings" popup later)
            }
            if (failure != null)
            {
                throw new RpcException(ErrorCodes.Internal, $"applying the import settings failed ({failure}); the previous values were restored. See get_log.");
            }

            JObject now2 = new JObject();
            foreach (ImportSettingChange c in plan.Changes)
            {
                now2[c.Field.Name] = CurrentSetting(asset, c.Field);
            }
            JObject result = new JObject
            {
                ["guid"] = args.Guid,
                ["changed"] = true,
                ["applied"] = now2,
                ["previous"] = previous,
                ["unchanged"] = new JArray(plan.Unchanged.ToArray()),
                ["reimported"] = needsReimport,
                ["undoable"] = false,
                ["note"] = needsReimport
                    ? "applied the way the inspector's Apply does: the .pmeta was written and the file re-imported now (not only at save_mod)."
                    : "filterMode/wrapMode were set on the texture in place and the .pmeta was written now (no re-import needed).",
            };
            if (materialSlots.Count > 0)
            {
                result["materialSlotsRepointed"] = new JArray(materialSlots.Select(s => (JToken)$"{s.Key.name}.{s.Value}").ToArray());
            }
            if (asset is PummelProp)
            {
                result["warnings"] = new JArray("objects already placed in the scene may keep showing the previous mesh until the mod is reopened; the saved data is not affected");
            }
            if (before != null)
            {
                AssetAccess.RefResult after = AssetAccess.FindReferences(r.Ed, r.Mod, asset);
                if (after.Items.Count != before.Items.Count)
                {
                    result["warnings"] = new JArray($"reference count changed from {before.Items.Count} to {after.Items.Count} after the re-import: check get_asset references");
                }
            }
            inv.Complete(result);
        }

        // ------------------------------------------------------------------ materials

        private static Shader ResolveShader(EditorSystem ed, string spec, out ModAsset shaderAsset)
        {
            shaderAsset = null;
            WorkshopController wc = WorkshopController.Instance;
            if (wc?.Assets?.assets == null)
            {
                throw RpcException.Internal("the built-in asset list is not available");
            }
            List<ModAsset> shaders = wc.Assets.assets.Where(a => a != null && ValueConverter.LocationName(a) == "internal" && ValueConverter.AssetTypeName(a) == "Shader").ToList();
            if (spec == null)
            {
                shaderAsset = wc.Assets.GetAssetWithGuid(ed.defaultShaderGuid);
            }
            else if (Guid.TryParse(spec, out Guid g))
            {
                shaderAsset = shaders.FirstOrDefault(a => a.guid.ToString() == g.ToString());
            }
            else
            {
                shaderAsset = shaders.FirstOrDefault(a => string.Equals(a.name, spec, StringComparison.OrdinalIgnoreCase));
                if (shaderAsset == null)
                {
                    foreach (ModAsset a in shaders)
                    {
                        Shader s = a.unityObjectAddressable as Shader; // loads the built-in shader (what the editor's picker does)
                        if (s != null && string.Equals(s.name, spec, StringComparison.OrdinalIgnoreCase))
                        {
                            shaderAsset = a;
                            break;
                        }
                    }
                }
            }
            Shader shader = shaderAsset?.unityObjectAddressable as Shader;
            if (shader == null)
            {
                throw RpcException.InvalidParams(spec == null
                    ? "the editor's default shader could not be resolved (EditorSystem.defaultShaderGuid); nothing was created"
                    : $"'{spec}' is not a built-in Shader asset (list_asset_rules shows the shader assets; use a name or guid)");
            }
            return shader;
        }

        private static string MaterialsFolder(Ready r)
        {
            string expected = Path.Combine(r.AssetsFull, "Materials");
            if (!string.Equals(Path.GetFullPath(r.Mod.FullMaterialsFolder).TrimEnd('\\', '/'), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw RpcException.Internal("the game's Materials folder is not <mod>\\Assets\\Materials; refusing to create a material there");
            }
            if (File.Exists(expected))
            {
                throw new RpcException(ErrorCodes.InvalidState, "'Assets\\Materials' exists as a file; nothing was created");
            }
            if (Directory.Exists(expected) && (new DirectoryInfo(expected).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new RpcException(ErrorCodes.InvalidState, "'Assets\\Materials' is a link (junction/symlink); refusing to write through it");
            }
            return expected;
        }

        private static JArray ClampsJson(MaterialPlan plan) =>
            new JArray(plan.Clamped.Select(c => (JToken)new JObject { ["property"] = c.Key, ["requested"] = c.Requested, ["applied"] = c.Applied, ["min"] = c.Min, ["max"] = c.Max }).ToArray());

        private static void CreateMaterial(WriteInvocation inv, EditServices services)
        {
            CreateMaterialArgs args = inv.ArgsAs<CreateMaterialArgs>();
            Ready r = RequireReady(noPrefab: false);
            string folder = MaterialsFolder(r);
            Shader shader = ResolveShader(r.Ed, args.Shader, out ModAsset shaderAsset);
            ShaderRule rule = MaterialRules.ForShader(shader.name);
            MaterialAccess state = new MaterialAccess(r.Ed, null, shader);
            MaterialPlan plan = null;
            if (args.Properties != null)
            {
                if (rule == null)
                {
                    throw RpcException.InvalidParams($"shader '{shader.name}' has no known inspector in this plugin, so its properties are not exposed; create the material without properties (known shaders: {MaterialRules.KnownShaderNames})");
                }
                plan = MaterialRules.Plan(rule, args.Properties, state);
                state.RequirePresent(plan);
            }

            // the editor names the file Material_<n> by asset names only: refuse if that file (or the requested name) exists on disk
            string uniqueName = r.Ed.GetUniqueAssetName<Material>();
            string uniqueFile = Path.Combine(folder, uniqueName + ".pmat");
            if (!AssetImportRules.IsFree(uniqueFile))
            {
                throw new RpcException(ErrorCodes.AlreadyExists, $"the editor would create 'Assets\\Materials\\{uniqueName}.pmat' but that file (or its .pmeta) already exists on disk without a loaded material; nothing was created, because the editor would overwrite it");
            }
            string finalName = args.Name ?? uniqueName;
            if (args.Name != null && !AssetImportRules.IsFree(Path.Combine(folder, args.Name + ".pmat")))
            {
                throw new RpcException(ErrorCodes.AlreadyExists, $"'Assets\\Materials\\{args.Name}.pmat' (or its .pmeta) already exists; pick another name (nothing was created)");
            }
            if (args.Name != null && r.Mod.Assets.assets.Any(a => a is PummelMaterial && string.Equals(a.name, args.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(ErrorCodes.AlreadyExists, $"a material named '{args.Name}' already exists; pick another name (nothing was created)");
            }

            inv.Commit(TimeSpan.FromSeconds(5));
            services.Observe();
            AssetImportRules.Created created = new AssetImportRules.Created
            {
                Destination = new ImportDestination
                {
                    AssetsFolder = r.AssetsFull, SubFolder = "Materials", Stem = uniqueName, Extension = ".pmat",
                    FullPath = uniqueFile, MetaPath = uniqueFile + AssetImportRules.MetaExtension,
                },
                FileCreated = true,
            };
            PummelMaterial pm = null;
            List<string> notes = new List<string>();
            try
            {
                pm = r.Ed.CreateNewMaterial(); // writes Assets\Materials\<unique>.pmat and .pmeta immediately
                if (!ReferenceEquals(pm.UnityMaterial.shader, shader))
                {
                    pm.SetShader(shader);
                }
                if (args.Name != null && args.Name != pm.name)
                {
                    pm.Rename(args.Name); // moves the two files inside the same folder (both names were checked free above)
                    if (EditorPopupManager.CurrentPopup != null)
                    {
                        notes.Add("the editor raised a dialog while renaming; close it by hand (see get_log)");
                    }
                }
                if (plan != null)
                {
                    MaterialAccess.Apply(r.Ed, pm, plan);
                }
                else
                {
                    MaterialAccess.Apply(r.Ed, pm, new MaterialPlan { Rule = rule });
                }
                pm.Save();
            }
            catch (Exception e)
            {
                List<string> done = new List<string>();
                try
                {
                    ModAssetExternal reg = r.Mod.Assets.assets.OfType<PummelMaterial>().FirstOrDefault(x => pm != null && ReferenceEquals(x, pm));
                    if (reg != null && AssetImportRules.IsStrictlyUnder(r.AssetsFull, reg.filePath))
                    {
                        r.Mod.RemoveAsset(reg);
                        done.Add("removed the new material");
                    }
                }
                catch (Exception ex)
                {
                    done.Add("could not remove: " + ex.Message);
                }
                AssetImportRules.CleanupReport rep = AssetImportRules.Rollback(created);
                done.AddRange(rep.Deleted.Select(d => "deleted " + d));
                throw new RpcException(ErrorCodes.Internal, $"creating the material failed ({e.Message}); cleaned up: {(done.Count == 0 ? "nothing" : string.Join("; ", done.ToArray()))}");
            }

            EditorSelectionManager sel = r.Ed.GetSelectionManager();
            if (sel.IsSelected(pm))
            {
                sel.SerializeSelectedObject();
            }
            JObject result = new JObject
            {
                ["asset"] = AssetAccess.Row(pm, r.AssetsFull),
                ["shader"] = shader.name,
                ["knownShader"] = rule != null,
                ["properties"] = new MaterialAccess(r.Ed, pm.UnityMaterial, shader).Describe(),
                ["clamped"] = plan == null ? new JArray() : ClampsJson(plan),
                ["persisted"] = true,
                ["undoable"] = false,
                ["note"] = "the editor wrote Assets/Materials/<name>.pmat and .pmeta NOW (like its Create > Material menu), and this call saved the properties into the .pmeta (like the inspector's Apply); they stay on disk even if you do not call save_mod. " +
                           "Assign it with set_property on a renderer's 'materials' (or a prop's 'customMaterials') using the guid; remove it with remove_asset.",
            };
            if (notes.Count > 0)
            {
                result["warnings"] = new JArray(notes.ToArray());
            }
            if (rule == null)
            {
                result["propertiesNote"] = "this shader has no known inspector in this plugin; its properties are not exposed";
            }
            inv.Complete(result);
        }

        private static void SetMaterial(WriteInvocation inv, EditServices services)
        {
            SetMaterialArgs args = inv.ArgsAs<SetMaterialArgs>();
            Ready r = RequireReady(noPrefab: false);
            ModAssetExternal ext = RequireOwnAsset(r, args.Guid, "set_material");
            if (!(ext is PummelMaterial pm) || pm.UnityMaterial == null)
            {
                throw RpcException.InvalidParams($"'{ext.name}' is a {AssetAccess.Kind(ext)}, not a material");
            }
            Material mat = pm.UnityMaterial;
            Shader shader = mat.shader;
            bool shaderChanges = false;
            if (args.Shader != null)
            {
                Shader wanted = ResolveShader(r.Ed, args.Shader, out ModAsset _);
                shaderChanges = !ReferenceEquals(wanted, shader);
                shader = wanted;
            }
            ShaderRule rule = MaterialRules.ForShader(shader.name);
            MaterialAccess state = new MaterialAccess(r.Ed, shaderChanges ? null : mat, shader);
            MaterialPlan plan = null;
            if (args.Properties != null)
            {
                if (rule == null)
                {
                    throw RpcException.InvalidParams($"shader '{shader.name}' has no known inspector in this plugin, so its properties are not exposed (known shaders: {MaterialRules.KnownShaderNames})");
                }
                plan = MaterialRules.Plan(rule, args.Properties, state);
                state.RequirePresent(plan);
            }
            if (!File.Exists(ext.filePath))
            {
                throw new RpcException(ErrorCodes.InvalidState, $"the file of '{ext.name}' is missing on disk; nothing was changed");
            }

            JObject previous = new JObject();
            if (plan != null && !shaderChanges)
            {
                foreach (MaterialAssignment a in plan.Assignments)
                {
                    object old = state.Get(a.Prop.Key);
                    previous[a.Prop.Key] = old == null ? JValue.CreateNull() : MaterialRules.ToJson(a.Prop, old);
                }
            }
            string previousShader = mat.shader.name;

            inv.Commit(TimeSpan.FromSeconds(3));
            services.Observe();
            if (shaderChanges)
            {
                pm.SetShader(shader);
            }
            MaterialAccess.Apply(r.Ed, pm, plan ?? new MaterialPlan { Rule = rule });
            pm.Save(); // .pmat + .pmeta, like the inspector's Apply (which saves, then re-imports the same object)
            EditorSelectionManager sel = r.Ed.GetSelectionManager();
            if (sel.IsSelected(pm))
            {
                sel.SerializeSelectedObject();
            }

            JObject result = new JObject
            {
                ["guid"] = args.Guid,
                ["name"] = pm.name,
                ["shader"] = shader.name,
                ["shaderChanged"] = shaderChanges,
                ["properties"] = new MaterialAccess(r.Ed, pm.UnityMaterial, shader).Describe(),
                ["previous"] = previous,
                ["clamped"] = plan == null ? new JArray() : ClampsJson(plan),
                ["persisted"] = true,
                ["undoable"] = false,
                ["note"] = "saved to the material's .pmat/.pmeta now (like the inspector's Apply); not undoable: use 'previous' to revert by hand." + (shaderChanges ? $" Shader changed from '{previousShader}': properties of the old shader that the new one does not have are dropped, and conditional properties had to be set in this call." : string.Empty),
            };
            if (rule == null)
            {
                result["propertiesNote"] = "this shader has no known inspector in this plugin; its properties are not exposed";
            }
            inv.Complete(result);
        }

        // ------------------------------------------------------------------ remove_asset

        private static void Remove(WriteInvocation inv, EditServices services)
        {
            RemoveArgs args = inv.ArgsAs<RemoveArgs>();
            Ready r = RequireReady(noPrefab: true);
            ModAsset a = AssetAccess.Find(r.Ed, args.Guid);
            if (a == null || ValueConverter.LocationName(a) == "runtime")
            {
                throw RpcException.NotFound($"no asset with guid {args.Guid}");
            }
            RemovalFacts facts = Facts(r.Ed, a, r.AssetsFull);
            if (!AssetAccess.InModList(r.Mod, a))
            {
                facts.IsBuiltIn = true;
            }
            string why = AssetRemovalRules.Check(facts);
            if (why != null)
            {
                throw RpcException.InvalidParams($"'{a.name}' is not removed: {why}");
            }
            ModAssetExternal ext = (ModAssetExternal)a;

            AssetAccess.RefResult refs = AssetAccess.FindReferences(r.Ed, r.Mod, a);
            if (refs.PreviewImage)
            {
                throw new RpcException(ErrorCodes.AssetInUse,
                    $"'{a.name}' is the mod's preview image; it is never removed (even with force). Set the preview image to another texture or null first (set_mod_settings section details, field previewImage), then retry.",
                    AssetAccess.RefsJson(refs));
            }
            bool blocked = refs.Items.Count > 0 || refs.Incomplete;
            if (blocked && !args.Force)
            {
                throw new RpcException(ErrorCodes.AssetInUse,
                    $"'{a.name}' is still used ({refs.Items.Count} reference(s){(refs.Incomplete ? ", and the scan could not read everything" : string.Empty)}): " +
                    string.Join("; ", refs.Items.Take(12).Select(ReferenceScan.Describe).ToArray()) +
                    ". Clear them first, or pass force:true to remove it anyway (the references then become null).",
                    AssetAccess.RefsJson(refs));
            }

            string rel = AssetAccess.RelativePath(r.AssetsFull, ext.filePath);
            string file = SafeFull(ext.filePath);
            string meta = file + AssetImportRules.MetaExtension;
            // the two paths the game's RemoveAsset deletes: both must be strictly inside the Assets folder (checked in Check for the file)
            if (!AssetImportRules.IsStrictlyUnder(r.AssetsFull, meta))
            {
                throw RpcException.InvalidParams("the .pmeta path is not inside the mod's Assets folder; nothing was removed");
            }

            inv.Commit(TimeSpan.FromSeconds(3));
            services.Observe();
            EditorSelectionManager sel = r.Ed.GetSelectionManager();
            if (sel.IsSelected(ext))
            {
                sel.DeselectSelectedAsset(); // not dirty (checked): no popup
                if (sel.IsSelected(ext))
                {
                    sel.DeselectSelectedAsset(true);
                }
            }
            r.Mod.RemoveAsset(ext); // destroys the Unity object, deletes <file> and <file>.pmeta, unlists it, updates the Asset Browser

            List<string> deleted = new List<string>();
            List<string> remaining = new List<string>();
            foreach (string p in new[] { file, meta })
            {
                (File.Exists(p) ? remaining : deleted).Add(Path.GetFileName(p));
            }
            bool stillListed = AssetAccess.InModList(r.Mod, ext);
            if (refs.Items.Count > 0)
            {
                services.MarkEdit(r.Mod, "remove_asset");
            }
            JObject result = new JObject
            {
                ["removed"] = new JObject { ["guid"] = args.Guid, ["name"] = ext.name, ["type"] = AssetAccess.Kind(ext), ["relativePath"] = rel },
                ["deletedFiles"] = new JArray(deleted.ToArray()),
                ["undoable"] = false,
                ["note"] = "the file and its .pmeta were deleted from the mod's Assets folder NOW (the editor has no undo for this).",
            };
            if (remaining.Count > 0 || stillListed)
            {
                result["warnings"] = new JArray(
                    (remaining.Count > 0 ? $"could not delete {string.Join(", ", remaining.ToArray())} (in use?); the asset reappears when the mod is reopened" : null) ??
                    "the asset is still listed");
            }
            if (refs.Items.Count > 0)
            {
                result["referencesNulled"] = new JArray(refs.Items.Select(ReferenceScan.ToJson).ToArray());
                result["referencesNote"] = "force was used: these references now point at a destroyed object in memory and are saved as none at the next save_mod (check them with get_object)";
            }
            inv.Complete(result);
        }
    }
}
