using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using UnityEngine;
using ModSystem.Logic;
using UnityEngine.Rendering;

namespace PummelPartyMcp.Game
{
    /// <summary>
    /// Game-side helpers of the asset tools (import_asset, get_asset, set_import_settings, create_material, set_material,
    /// remove_asset, list_asset_rules). Main thread only. The file-system rules live in Core/AssetImportCore.cs; the editor's own
    /// functions are called only where the doc 07 recon and the decompiled source show what they write and delete.
    /// </summary>
    internal static class AssetAccess
    {
        public static ModAsset Find(EditorSystem ed, string guid) =>
            ed.GetAssetWithGuid(new SerializableGuid(Guid.Parse(guid)));

        public static bool InModList(PummelMod mod, ModAsset a)
        {
            if (mod.Assets?.assets == null)
            {
                return false;
            }
            foreach (ModAsset x in mod.Assets.assets)
            {
                if (ReferenceEquals(x, a))
                {
                    return true;
                }
            }
            return false;
        }

        public static string AssetsFolderOf(PummelMod mod) => AssetImportRules.ValidateAssetsFolder(mod.GetModPath(), mod.FullAssetsFolder);

        public static string ClassLabel(ModAsset a) => ValueConverter.AssetTypeName(a);

        /// <summary>The kind label used by the tools: Texture, Prop, AudioClip, Material, Prefab, ...</summary>
        public static string Kind(ModAsset a) => ClassLabel(a);

        public static string RelativePath(string assetsFull, string file)
        {
            try
            {
                string modRoot = Path.GetDirectoryName(assetsFull.TrimEnd('\\', '/')) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(file);
                return full.StartsWith(modRoot, StringComparison.OrdinalIgnoreCase) ? full.Substring(modRoot.Length).Replace('\\', '/') : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The asset as list_assets shows it, plus file facts for external ones.</summary>
        public static JObject Row(ModAsset a, string assetsFull)
        {
            JObject row = new JObject
            {
                ["guid"] = a.guid.ToString(),
                ["name"] = a.name,
                ["type"] = ValueConverter.AssetTypeName(a),
                ["className"] = a.GetType().Name,
                ["location"] = ValueConverter.LocationName(a),
            };
            if (a is ModAssetExternal ext && !string.IsNullOrEmpty(ext.filePath))
            {
                string rel = assetsFull == null ? null : RelativePath(assetsFull, ext.filePath);
                if (rel != null)
                {
                    row["relativePath"] = rel;
                    AddLinkFlag(row, AssetImportRules.ModRootOf(assetsFull), ext);
                }
                try
                {
                    FileInfo fi = new FileInfo(ext.filePath);
                    row["fileExists"] = fi.Exists;
                    if (fi.Exists)
                    {
                        row["sizeBytes"] = fi.Length;
                    }
                }
                catch (Exception)
                {
                    row["fileExists"] = false;
                }
            }
            return row;
        }

        /// <summary>list_assets/get_asset: marks an asset whose file (or a prop's mesh file) runs through a link inside the mod (reading stays allowed).</summary>
        public static void AddLinkFlag(JObject row, string modRoot, ModAssetExternal ext)
        {
            if (string.IsNullOrEmpty(modRoot) || ext == null || string.IsNullOrEmpty(ext.filePath))
            {
                return;
            }
            string why = SandboxPath.FindLink(modRoot, ext.filePath);
            if (why == null && ext is PummelProp pp && pp.MeshAsset != null && !string.IsNullOrEmpty(pp.MeshAsset.filePath))
            {
                why = SandboxPath.FindLink(modRoot, pp.MeshAsset.filePath);
            }
            if (why != null)
            {
                row["linked"] = true;
                row["linkedReason"] = why + "; the plugin refuses to write or delete through it (reading works)";
            }
        }

        /// <summary>Every path PummelMod.RemoveAsset deletes for the asset: its file and .pmeta, and for a prop the nested mesh's file and .pmeta.</summary>
        public static List<string> RemovalPaths(ModAssetExternal a)
        {
            List<string> paths = new List<string> { a.filePath, a.filePath + AssetImportRules.MetaExtension };
            if (a is PummelProp pp && pp.MeshAsset != null)
            {
                string nested = pp.MeshAsset.filePath;
                if (string.IsNullOrEmpty(nested))
                {
                    throw new RpcException(ErrorCodes.InvalidState, $"the prop '{a.name}' has a nested mesh without a file path; the editor's removal would delete '.pmeta' relative to the game folder, so it is not called");
                }
                paths.Add(nested);
                paths.Add(nested + AssetImportRules.MetaExtension);
            }
            return paths;
        }

        /// <summary>C1: refuses unless every path the game's RemoveAsset deletes for <paramref name="a"/> may be deleted (no link on the way, single hard link).</summary>
        public static void RequireRemovable(string assetsFull, ModAssetExternal a, string purpose)
        {
            if (string.IsNullOrEmpty(a.filePath))
            {
                throw new RpcException(ErrorCodes.InvalidState, $"'{a.name}' has no file path; nothing was deleted");
            }
            SandboxPath.RequireAllInsideMod(AssetImportRules.ModRootOf(assetsFull), RemovalPaths(a), purpose);
        }

        /// <summary>C1: refuses unless the asset's .pmeta (and, for materials/prefabs/visual scripts, the file itself) may be rewritten.</summary>
        public static void RequireSavable(string assetsFull, ModAssetExternal a, string purpose)
        {
            List<string> paths = ModSaveTargetsFor(new[] { a });
            SandboxPath.RequireAllInsideMod(AssetImportRules.ModRootOf(assetsFull), paths, purpose);
        }

        private static List<string> ModSaveTargetsFor(IEnumerable<ModAssetExternal> assets)
        {
            List<string> paths = new List<string>();
            foreach (ModAssetExternal a in assets)
            {
                if (string.IsNullOrEmpty(a.filePath))
                {
                    throw new RpcException(ErrorCodes.InvalidState, $"the asset '{a.name}' has no file path; nothing was written");
                }
                paths.Add(a.filePath + AssetImportRules.MetaExtension);
                if (RewritesFile(a))
                {
                    paths.Add(a.filePath);
                }
            }
            return paths;
        }

        private static bool RewritesFile(ModAssetExternal a) => a is PummelMaterial || a is PummelPrefab || a is PummelVisualScript;

        /// <summary>
        /// C1: every file PummelMod.Save writes for <paramref name="mod"/> (and SaveCurrentMod's Preview.jpg when <paramref name="includePreview"/>):
        /// refuses the whole save, naming each target that runs through a link or is hard-linked, before anything is written.
        /// </summary>
        public static void RequireModSavable(PummelMod mod, bool includePreview, string purpose)
        {
            string assetsFull = AssetsFolderOf(mod); // root name + root/Assets not a link
            string root = AssetImportRules.ModRootOf(assetsFull);
            List<SavedAssetFile> files = new List<SavedAssetFile>();
            if (mod.Assets?.assets != null)
            {
                foreach (ModAsset a in mod.Assets.assets)
                {
                    // the same filter as PummelMod.Save
                    if (a != null && a.GetType().IsSubclassOf(typeof(ModAssetExternal)) && !((ModAssetExternal)a).HideAndDontSave)
                    {
                        ModAssetExternal ext = (ModAssetExternal)a;
                        files.Add(new SavedAssetFile { Name = ext.name, FilePath = ext.filePath, RewritesFile = RewritesFile(ext) });
                    }
                }
            }
            SandboxPath.RequireAllWritable(root, ModSaveTargets.Build(root, files, includePreview), purpose);
        }

        public static bool IsOwnCustom(PummelMod mod, ModAsset a, string assetsFull) =>
            a is ModAssetExternal ext && ValueConverter.LocationName(a) == "external" && InModList(mod, a)
            && !string.IsNullOrEmpty(ext.filePath) && AssetImportRules.IsStrictlyUnder(assetsFull, ext.filePath);

        // ------------------------------------------------------------------ references

        public sealed class RefResult
        {
            public List<AssetReference> Items = new List<AssetReference>();
            public bool Incomplete;
            public bool PreviewImage;
            public int ObjectsScanned;

            /// <summary>The mod settings (or the preview image lookup) could not be read: the asset may be the preview image, so even force does not remove it (L3).</summary>
            public bool SettingsScanFailed;
        }

        private const int MaxObjects = 5000;

        /// <summary>
        /// Everything that holds the asset: component properties and action parameters/targets of every scene and prefab object (read
        /// with the plugin's own converter, so the same references get_object shows are found), materials' texture slots, and the mod
        /// settings (music, ambience, preview image, ...). Read-only.
        /// </summary>
        public static RefResult FindReferences(EditorSystem ed, PummelMod mod, ModAsset target)
        {
            RefResult result = new RefResult();
            string guid = target.guid.ToString();
            ValueConverter conv = new ValueConverter(mod, summarizeTriggers: false);

            foreach (Transform root in new[] { ed.m_hierarchyRoot, ed.m_prefabsRoot })
            {
                if (root == null)
                {
                    continue;
                }
                Stack<Transform> stack = new Stack<Transform>();
                for (int i = root.childCount - 1; i >= 0; i--)
                {
                    stack.Push(root.GetChild(i));
                }
                while (stack.Count > 0)
                {
                    Transform t = stack.Pop();
                    ModTransform mt = t.GetComponent<ModTransform>();
                    if (mt == null)
                    {
                        continue;
                    }
                    if (++result.ObjectsScanned > MaxObjects)
                    {
                        result.Incomplete = true;
                        break;
                    }
                    ScanObject(mod, conv, mt, guid, result);
                    if (EditorAccess.HidesChildren(t))
                    {
                        continue;
                    }
                    for (int i = t.childCount - 1; i >= 0; i--)
                    {
                        stack.Push(t.GetChild(i));
                    }
                }
            }

            ScanMaterials(mod, target, result);
            ScanSettings(ed, mod, guid, result);
            ScanPlayerTriggers(mod, conv, guid, result);
            ScanItemDetails(mod, target, guid, result);
            ScanWorkshopItem(mod, guid, result);
            return result;
        }

        /// <summary>
        /// H1: the action lists held by the Minigame player settings (the six "Event Triggers" and the hidden hitTrigger): every public
        /// ModLogicTrigger field of SimpleModPlayerSettings, read with the same converter as get_action_graph (actions, targets, parameters,
        /// nested popup choices). Not scene objects, so the object walk does not see them.
        /// </summary>
        private static void ScanPlayerTriggers(PummelMod mod, ValueConverter conv, string guid, RefResult result)
        {
            if (!(mod is MinigameMod mm))
            {
                return;
            }
            try
            {
                SimpleModPlayerSettings settings = mm.MinigameSettings?.SimpleModPlayerSettings;
                if (settings == null)
                {
                    result.Incomplete = true;
                    return;
                }
                foreach (System.Reflection.FieldInfo f in typeof(SimpleModPlayerSettings).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                {
                    if (!typeof(ModLogicTrigger).IsAssignableFrom(f.FieldType))
                    {
                        continue;
                    }
                    if (!(f.GetValue(settings) is ModLogicTrigger trig))
                    {
                        continue;
                    }
                    Add(result, new ReferenceSource { Kind = "playerTrigger", Owner = "playerSettings." + f.Name, Data = conv.Trigger(trig, 0) }, guid);
                }
            }
            catch (Exception)
            {
                result.Incomplete = true;
            }
        }

        /// <summary>Item mods: the item's prefabs (use, receive, held) and its icon texture (ItemMod.GetItemDetails).</summary>
        private static void ScanItemDetails(PummelMod mod, ModAsset target, string guid, RefResult result)
        {
            if (!(mod is ItemMod im))
            {
                return;
            }
            try
            {
                ItemDetails d = im.GetItemDetails();
                if (d == null)
                {
                    result.Incomplete = true;
                    return;
                }
                foreach (KeyValuePair<string, SerializableGuid> p in new[]
                {
                    new KeyValuePair<string, SerializableGuid>("prefab", d.prefabGuid),
                    new KeyValuePair<string, SerializableGuid>("recievePrefab", d.recievePrefabGuid),
                    new KeyValuePair<string, SerializableGuid>("heldPrefab", d.heldPrefabGuid),
                })
                {
                    Add(result, new ReferenceSource { Kind = "setting", Owner = "itemDetails." + p.Key, Data = GuidToken(p.Value), FixedWhere = "the item mod's details (" + p.Key + ")" }, guid);
                }
                UnityEngine.Object t = target.unityObject;
                if (d.iconTexture != null && t != null && ReferenceEquals(d.iconTexture, t))
                {
                    Add(result, new ReferenceSource { Kind = "setting", Owner = "itemDetails.iconTexture", Data = new JValue(guid), FixedWhere = "the item mod's icon" }, guid);
                }
            }
            catch (Exception)
            {
                result.Incomplete = true;
                result.SettingsScanFailed = true;
            }
        }

        /// <summary>
        /// L2: the Workshop item's thumbnail (previewImageGuid) and preview files. Read only: the plugin never writes the Workshop item, so
        /// such an asset is reported (and refused without force); the human changes the thumbnail in the editor's Workshop dialog.
        /// </summary>
        private static void ScanWorkshopItem(PummelMod mod, string guid, RefResult result)
        {
            try
            {
                ModWorkshopItem w = mod.workshopItem;
                if (w == null)
                {
                    return;
                }
                Add(result, new ReferenceSource
                {
                    Kind = "workshop", Owner = "workshopItem.previewImage", Data = GuidToken(w.previewImageGuid),
                    FixedWhere = "the Workshop item's thumbnail (WorkshopItem.json; changed only by the human in the editor's Workshop dialog)",
                }, guid);
                if (w.previewFiles != null)
                {
                    for (int i = 0; i < w.previewFiles.Count; i++)
                    {
                        WorkshopPreviewFile pf = w.previewFiles[i];
                        if (pf != null)
                        {
                            Add(result, new ReferenceSource { Kind = "workshop", Owner = $"workshopItem.previewFiles[{i}]", Data = GuidToken(pf.guid), FixedWhere = "a Workshop preview file (WorkshopItem.json)" }, guid);
                        }
                    }
                }
            }
            catch (Exception)
            {
                result.Incomplete = true;
            }
        }

        private static void ScanObject(PummelMod mod, ValueConverter conv, ModTransform mt, string guid, RefResult result)
        {
            GameObject go = mt.gameObject;
            foreach (ModBehaviour mb in EditorAccess.ModComponents(go))
            {
                try
                {
                    ModReflectedType reflected = ModReflectionSystem.GetType(mb.GetType());
                    JObject entry = ComponentReader.Read(mb, reflected, conv);
                    Add(result, new ReferenceSource { Kind = "component", Owner = go.name + " / " + reflected.name, OwnerGuid = EditorAccess.GuidOf(mt), Data = entry["properties"] }, guid);
                }
                catch (Exception)
                {
                    result.Incomplete = true;
                }
            }
        }

        /// <summary>Feeds one place through the Core collector and merges the hits.</summary>
        private static void Add(RefResult result, ReferenceSource s, string guid)
        {
            ReferenceCollector.Result r = new ReferenceCollector.Result();
            ReferenceCollector.Add(r, s, guid);
            result.Items.AddRange(r.Items);
            result.Incomplete |= r.Incomplete;
            result.PreviewImage |= r.PreviewImage;
        }

        private static JToken GuidToken(SerializableGuid g) => new JValue(g.ToString());

        private static void ScanMaterials(PummelMod mod, ModAsset target, RefResult result)
        {
            UnityEngine.Object targetObject = target.unityObject;
            if (targetObject == null || !(targetObject is Texture))
            {
                return;
            }
            foreach (ModAsset a in mod.Assets.assets.ToArray())
            {
                if (!(a is PummelMaterial pm) || ReferenceEquals(pm, target))
                {
                    continue;
                }
                try
                {
                    Material m = pm.UnityMaterial;
                    if (m == null || m.shader == null)
                    {
                        continue;
                    }
                    foreach (string prop in TextureSlots(m))
                    {
                        if (m.GetTexture(prop) == targetObject)
                        {
                            Add(result, new ReferenceSource { Kind = "material", Owner = pm.name, OwnerGuid = pm.guid.ToString(), Data = new JValue(target.guid.ToString()), FixedWhere = prop }, target.guid.ToString());
                        }
                    }
                }
                catch (Exception)
                {
                    result.Incomplete = true;
                }
            }
        }

        public static List<string> TextureSlots(Material m)
        {
            List<string> names = new List<string>();
            Shader s = m.shader;
            int n = s.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (s.GetPropertyType(i) == ShaderPropertyType.Texture)
                {
                    names.Add(s.GetPropertyName(i));
                }
            }
            return names;
        }

        private static void ScanSettings(EditorSystem ed, PummelMod mod, string guid, RefResult result)
        {
            try
            {
                ScanSettingsFields(ed, mod, guid, result);
            }
            catch (Exception)
            {
                result.Incomplete = true;
                result.SettingsScanFailed = true;
            }
        }

        private static void ScanSettingsFields(EditorSystem ed, PummelMod mod, string guid, RefResult result)
        {
            string modType = SettingsHost.ModTypeName(mod);
            if (modType != null)
            {
                SettingsHost host = new SettingsHost(ed, mod);
                foreach (SettingsSection sec in SettingsRules.ForModType(modType))
                {
                    foreach (ModSettingField f in sec.Fields)
                    {
                        if (f.Kind != SettingKind.Asset && f.Kind != SettingKind.AssetList)
                        {
                            continue;
                        }
                        try
                        {
                            object cur = host.Current(f);
                            JToken data = cur is AssetValue av ? new JValue(av.Guid)
                                : cur is List<AssetValue> list ? new JArray(list.Where(x => x != null).Select(x => (JToken)new JValue(x.Guid)).ToArray())
                                : null;
                            Add(result, new ReferenceSource
                            {
                                Kind = "setting", Owner = sec.Name + "." + f.Name, Data = data, FixedWhere = $"get_mod_settings {sec.Name}.{f.Name}",
                                IsPreviewImage = f.Member == "screenshotTexture" || f.Member == "previewImage",
                            }, guid);
                        }
                        catch (Exception)
                        {
                            result.Incomplete = true;
                            result.SettingsScanFailed = true; // L3: it may be the preview image: never removed, even with force
                        }
                    }
                }
            }
            try
            {
                if (string.Equals(mod.GetPreviewImageGuid().ToString(), guid, StringComparison.OrdinalIgnoreCase))
                {
                    result.PreviewImage = true;
                    if (!result.Items.Any(i => i.Kind == "setting" && i.Owner.EndsWith(".previewImage", StringComparison.Ordinal)))
                    {
                        result.Items.Add(new AssetReference { Kind = "setting", Owner = "details.previewImage", Where = "get_mod_settings details.previewImage" });
                    }
                }
            }
            catch (Exception)
            {
                result.Incomplete = true;
                result.SettingsScanFailed = true;
            }
        }

        public static JObject RefsJson(RefResult r)
        {
            return new JObject
            {
                ["count"] = r.Items.Count,
                ["items"] = new JArray(r.Items.Select(ReferenceScan.ToJson).ToArray()),
                ["scanComplete"] = !r.Incomplete,
                ["objectsScanned"] = r.ObjectsScanned,
                ["scanned"] = "components and action parameters/targets (incl. nested popup choices) of every scene and prefab object, the player-settings triggers, materials' texture slots, mod settings (music, ambience, preview image, custom spaces, item prefabs and icon), the Workshop item's thumbnail and preview files",
                ["settingsScanFailed"] = r.SettingsScanFailed,
            };
        }
    }

    /// <summary>Live material state for the material rule engine (a real material, or a fresh one for a given shader).</summary>
    internal sealed class MaterialAccess : IMaterialState
    {
        private readonly EditorSystem m_ed;
        private readonly Material m_material; // null = a fresh material (values are the defaults)
        private readonly Shader m_shader;
        private readonly ShaderRule m_rule;

        public MaterialAccess(EditorSystem ed, Material liveOrNull, Shader shader)
        {
            m_ed = ed;
            m_material = liveOrNull;
            m_shader = shader;
            m_rule = MaterialRules.ForShader(shader.name);
        }

        public bool HasProperty(string key)
        {
            MaterialProp p = m_rule?.Props.FirstOrDefault(x => x.Key == key);
            if (p == null)
            {
                return false;
            }
            return p.ShaderProp == null || m_shader.FindPropertyIndex(p.ShaderProp) >= 0;
        }

        public object Get(string key)
        {
            MaterialProp p = m_rule?.Props.FirstOrDefault(x => x.Key == key);
            if (p == null)
            {
                return null;
            }
            if (m_material == null)
            {
                return p.Kind == MatKind.Mode ? "Opaque" : p.Kind == MatKind.Bool ? (object)false : null;
            }
            if (p.ShaderProp != null && !m_material.HasProperty(p.ShaderProp))
            {
                return null;
            }
            switch (p.Kind)
            {
                case MatKind.Mode:
                {
                    int mode = Mathf.Clamp(Mathf.RoundToInt(m_material.GetFloat(p.ShaderProp)), 0, MaterialRules.Modes.Length - 1);
                    return MaterialRules.Modes[mode];
                }
                case MatKind.Bool: return m_material.IsKeywordEnabled("_EMISSION");
                case MatKind.Float:
                case MatKind.Range: return (double)m_material.GetFloat(p.ShaderProp);
                case MatKind.Color:
                case MatKind.ColorHdr:
                {
                    Color c = m_material.GetColor(p.ShaderProp);
                    return new double[] { c.r, c.g, c.b, c.a };
                }
                case MatKind.Vec2:
                {
                    Vector2 v = p.Key == "Tiling" ? m_material.mainTextureScale : m_material.mainTextureOffset;
                    return new double[] { v.x, v.y };
                }
                default:
                {
                    Texture t = m_material.GetTexture(p.ShaderProp);
                    if (t == null)
                    {
                        return null;
                    }
                    WorkshopController wc = WorkshopController.Instance;
                    ModAsset asset = wc?.GetAssetWithObject(t);
                    return asset?.guid.ToString();
                }
            }
        }

        public bool TryRange(string key, out double min, out double max)
        {
            min = max = 0;
            MaterialProp p = m_rule?.Props.FirstOrDefault(x => x.Key == key);
            if (p?.ShaderProp == null)
            {
                return false;
            }
            int idx = m_shader.FindPropertyIndex(p.ShaderProp);
            if (idx < 0 || m_shader.GetPropertyType(idx) != ShaderPropertyType.Range)
            {
                return false;
            }
            Vector2 lim = m_shader.GetPropertyRangeLimits(idx);
            min = lim.x;
            max = lim.y;
            return true;
        }

        public string CheckTexture(string guid)
        {
            ModAsset asset = AssetAccess.Find(m_ed, guid);
            if (asset == null)
            {
                return $"no asset with guid {guid}; use a guid from list_assets (type Texture)";
            }
            if (ValueConverter.LocationName(asset) == "runtime")
            {
                return $"'{asset.name}' is not an asset";
            }
            UnityEngine.Object uo = asset.unityObjectAddressable;
            if (!(uo is Texture2D))
            {
                return $"'{asset.name}' is a {ValueConverter.AssetTypeName(asset)}, expected a texture";
            }
            return null;
        }

        /// <summary>Plan-time check that every key exists on the actual shader build.</summary>
        public void RequirePresent(MaterialPlan plan)
        {
            foreach (MaterialAssignment a in plan.Assignments)
            {
                if (!HasProperty(a.Prop.Key))
                {
                    throw RpcException.InvalidParams($"'{a.Prop.Key}': shader '{m_shader.name}' in this game build has no property {a.Prop.ShaderProp}");
                }
            }
        }

        /// <summary>
        /// Applies a validated plan to a live material the way the shader inspector does (property setters, texture keywords, blend
        /// keywords/queue of the rendering mode). Textures are resolved to the registered Texture2D objects.
        /// </summary>
        public static void Apply(EditorSystem ed, PummelMaterial pm, MaterialPlan plan)
        {
            Material mat = pm.UnityMaterial;
            foreach (MaterialAssignment a in plan.Assignments)
            {
                MaterialProp p = a.Prop;
                switch (p.Kind)
                {
                    case MatKind.Mode:
                        mat.SetFloat(p.ShaderProp, Array.IndexOf(MaterialRules.Modes, (string)a.Value));
                        break;
                    case MatKind.Bool:
                        if ((bool)a.Value)
                        {
                            mat.EnableKeyword("_EMISSION");
                        }
                        else
                        {
                            mat.DisableKeyword("_EMISSION");
                        }
                        break;
                    case MatKind.Float:
                    case MatKind.Range:
                        mat.SetFloat(p.ShaderProp, (float)(double)a.Value);
                        break;
                    case MatKind.Color:
                    case MatKind.ColorHdr:
                    {
                        double[] c = (double[])a.Value;
                        mat.SetColor(p.ShaderProp, new Color((float)c[0], (float)c[1], (float)c[2], (float)c[3]));
                        break;
                    }
                    case MatKind.Vec2:
                    {
                        double[] c = (double[])a.Value;
                        Vector2 v = new Vector2((float)c[0], (float)c[1]);
                        if (p.Key == "Tiling")
                        {
                            mat.mainTextureScale = v;
                        }
                        else
                        {
                            mat.mainTextureOffset = v;
                        }
                        break;
                    }
                    case MatKind.Texture:
                    {
                        Texture2D tex = null;
                        if (a.Value != null)
                        {
                            ModAsset asset = AssetAccess.Find(ed, (string)a.Value);
                            tex = asset?.unityObjectAddressable as Texture2D;
                            if (tex == null)
                            {
                                throw RpcException.InvalidParams($"'{p.Key}': the texture asset could not be loaded");
                            }
                        }
                        mat.SetTexture(p.ShaderProp, tex);
                        break;
                    }
                }
            }
            if (MaterialRules.UsesStandardKeywords(mat.shader.name))
            {
                // what StandardShaderInspector.DrawInspector does after each widget, for every slot
                SetKeyword(mat, "_METALLICGLOSSMAP", mat.HasProperty("_MetallicGlossMap") && mat.GetTexture("_MetallicGlossMap") != null);
                bool normal = mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null;
                SetKeyword(mat, "_NORMALMAP", normal);
                if (normal && mat.HasProperty("_BumpScale"))
                {
                    mat.SetFloat("_BumpScale", 1f);
                }
                SetKeyword(mat, "_PARALLAXMAP", mat.HasProperty("_ParallaxMap") && mat.GetTexture("_ParallaxMap") != null);
                StandardShaderInspector.UpdateKeywords(pm);
            }
            else
            {
                DefaultShaderInspector.UpdateKeywords(pm);
            }
        }

        private static void SetKeyword(Material mat, string keyword, bool on)
        {
            if (on)
            {
                mat.EnableKeyword(keyword);
            }
            else
            {
                mat.DisableKeyword(keyword);
            }
        }

        public JObject Describe()
        {
            JObject props = new JObject();
            if (m_rule != null && m_material != null)
            {
                foreach (MaterialProp p in m_rule.Props)
                {
                    if (!HasProperty(p.Key))
                    {
                        continue;
                    }
                    object v = Get(p.Key);
                    props[p.Key] = v == null && p.Kind != MatKind.Texture ? JValue.CreateNull() : MaterialRules.ToJson(p, v);
                }
            }
            return props;
        }
    }
}
