using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using UnityEngine;
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
            return result;
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
                    List<string> found = new List<string>();
                    bool incomplete = false;
                    ReferenceScan.FindGuid(entry["properties"], guid, string.Empty, found, ref incomplete);
                    if (incomplete)
                    {
                        result.Incomplete = true;
                    }
                    foreach (string path in found)
                    {
                        result.Items.Add(new AssetReference
                        {
                            Kind = path.Contains("actions[") ? "action" : "component",
                            Owner = go.name + " / " + reflected.name,
                            OwnerGuid = EditorAccess.GuidOf(mt),
                            Where = path,
                        });
                    }
                }
                catch (Exception)
                {
                    result.Incomplete = true;
                }
            }
        }

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
                            result.Items.Add(new AssetReference { Kind = "material", Owner = pm.name, OwnerGuid = pm.guid.ToString(), Where = prop });
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
                            bool hit = false;
                            if (cur is AssetValue av)
                            {
                                hit = string.Equals(av.Guid, guid, StringComparison.OrdinalIgnoreCase);
                            }
                            else if (cur is List<AssetValue> list)
                            {
                                hit = list.Any(x => x != null && string.Equals(x.Guid, guid, StringComparison.OrdinalIgnoreCase));
                            }
                            if (hit)
                            {
                                result.Items.Add(new AssetReference { Kind = "setting", Owner = sec.Name + "." + f.Name, Where = $"get_mod_settings {sec.Name}.{f.Name}" });
                                if (f.Member == "screenshotTexture" || f.Member == "previewImage")
                                {
                                    result.PreviewImage = true;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            result.Incomplete = true;
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
                ["scanned"] = "components and action parameters/targets of every scene and prefab object, materials' texture slots, mod settings",
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
