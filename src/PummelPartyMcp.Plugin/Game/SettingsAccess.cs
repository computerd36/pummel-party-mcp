using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace PummelPartyMcp.Game
{
    /// <summary>
    /// Game side of the mod settings tools: reads the fields of Core/SettingsRules.cs into normalised values, resolves asset
    /// pickers and preset lists, and applies a validated <see cref="SettingsPlan"/> the way the settings window would
    /// (same members, same side effects, no draw code). Main thread only. One instance per call: it caches what it read, so
    /// create a new one to read back after applying.
    /// </summary>
    internal sealed class SettingsHost : ISettingsState
    {
        private readonly EditorSystem m_ed;
        private readonly PummelMod m_mod;
        private readonly ValueConverter m_conv;
        private readonly Dictionary<ModSettingField, object> m_cache = new Dictionary<ModSettingField, object>();
        private readonly Dictionary<string, UnityEngine.Object> m_assets = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);

        public SettingsHost(EditorSystem ed, PummelMod mod)
        {
            m_ed = ed;
            m_mod = mod;
            m_conv = new ValueConverter(mod, summarizeTriggers: false);
        }

        public PummelMod Mod => m_mod;

        public static string ModTypeName(PummelMod mod)
        {
            if (mod is MinigameMod)
            {
                return "Minigame";
            }
            return mod is BoardMod ? "Board" : null;
        }

        public string ModType => ModTypeName(m_mod);

        // ------------------------------------------------------------------ targets

        private object Target(string target)
        {
            switch (target)
            {
                case "definition": return (m_mod as MinigameMod)?.GetMinigameDefinition();
                case "controller": return (m_mod as MinigameMod)?.MinigameSettings?.SimpleMinigameSettings;
                case "player": return (m_mod as MinigameMod)?.MinigameSettings?.SimpleModPlayerSettings;
                case "boardSettings": return m_mod.GetModSettings() as BoardModSettings;
                case "mapDetails": return (m_mod as BoardMod)?.GetMapDetails();
                case "camera": return (m_mod.GetModSettings() as BoardModSettings)?.CameraSettings;
                case "customSpaces": return (m_mod.GetModSettings() as BoardModSettings)?.CustomSpaceSettings;
                case "lighting": return m_mod.CustomLighting;
                case "lightingPreset":
                case "effectsPreset": return m_mod;
            }
            if (target.StartsWith("effect:", StringComparison.Ordinal))
            {
                return FindEffect(target.Substring("effect:".Length));
            }
            throw RpcException.Internal("unknown settings target " + target);
        }

        private PostProcessProfile CustomProfile() => m_mod.CustomPostProcess?.GetPostProcessProfile();

        private PostProcessEffectSettings FindEffect(string typeName)
        {
            PostProcessProfile profile = CustomProfile();
            if (profile == null)
            {
                return null;
            }
            foreach (PostProcessEffectSettings e in profile.settings)
            {
                if (e != null && e.GetType().Name == typeName)
                {
                    return e;
                }
            }
            return null;
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public;
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo fi = t.GetField(name, f | BindingFlags.DeclaredOnly);
                if (fi != null)
                {
                    return fi;
                }
                PropertyInfo pi = t.GetProperty(name, f | BindingFlags.DeclaredOnly);
                if (pi != null)
                {
                    return pi;
                }
            }
            return null;
        }

        private static object GetMember(object target, string name)
        {
            MemberInfo m = FindMember(target.GetType(), name);
            if (m == null)
            {
                throw RpcException.Internal(GameReflection.MissingMessage(target.GetType(), name));
            }
            return m is FieldInfo fi ? fi.GetValue(target) : ((PropertyInfo)m).GetValue(target, null);
        }

        private static Type MemberType(object target, string name)
        {
            MemberInfo m = FindMember(target.GetType(), name);
            if (m == null)
            {
                throw RpcException.Internal(GameReflection.MissingMessage(target.GetType(), name));
            }
            return m is FieldInfo fi ? fi.FieldType : ((PropertyInfo)m).PropertyType;
        }

        private static void SetMember(object target, string name, object value)
        {
            MemberInfo m = FindMember(target.GetType(), name);
            if (m == null)
            {
                throw RpcException.Internal(GameReflection.MissingMessage(target.GetType(), name));
            }
            if (m is FieldInfo fi)
            {
                fi.SetValue(target, value);
            }
            else
            {
                ((PropertyInfo)m).SetValue(target, value, null);
            }
        }

        // ------------------------------------------------------------------ reading

        public object Current(ModSettingField f)
        {
            if (m_cache.TryGetValue(f, out object cached))
            {
                return cached;
            }
            object v = Read(f);
            m_cache[f] = v;
            return v;
        }

        private object Read(ModSettingField f)
        {
            if (f.Kind == SettingKind.Preset)
            {
                return f.Target == "lightingPreset" ? m_mod.GetSelectedLightingPreset() : m_mod.GetSelectedPostProcessingPreset();
            }
            object target = Target(f.Target);
            if (f.IsEffect)
            {
                return target != null;
            }
            if (target == null)
            {
                return null;
            }
            object raw = GetMember(target, f.Member);
            if (raw != null && IsParameter(raw))
            {
                object value = ParamField(raw, "value");
                if (f.IsParam)
                {
                    return new ParamValue { Value = Normalize(f, value), Override = (bool)ParamField(raw, "overrideState") };
                }
                return Normalize(f, value);
            }
            return Normalize(f, raw);
        }

        private static bool IsParameter(object o)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition().Name == "ParameterOverride`1")
                {
                    return true;
                }
            }
            return false;
        }

        private static object ParamField(object parameter, string name)
        {
            FieldInfo fi = GameReflection.RequireField(ParameterBase(parameter.GetType()), name);
            return fi.GetValue(parameter);
        }

        private static void SetParamField(object parameter, string name, object value)
        {
            GameReflection.RequireField(ParameterBase(parameter.GetType()), name).SetValue(parameter, value);
        }

        private static Type ParameterBase(Type t)
        {
            for (Type c = t; c != null; c = c.BaseType)
            {
                if (c.IsGenericType && c.GetGenericTypeDefinition().Name == "ParameterOverride`1")
                {
                    return c;
                }
            }
            return t;
        }

        private object Normalize(ModSettingField f, object raw)
        {
            switch (f.Kind)
            {
                case SettingKind.Bool:
                    return raw is bool b ? (object)b : null;
                case SettingKind.Int:
                case SettingKind.Float:
                    return raw == null ? null : (object)Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                case SettingKind.String:
                    return raw is Enum en ? en.ToString() : (string)raw;
                case SettingKind.Enum:
                    return raw?.ToString();
                case SettingKind.NamedInt:
                {
                    long n = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                    int i = Array.IndexOf(f.EnumValues, n);
                    return i >= 0 ? f.EnumNames[i] : n.ToString(CultureInfo.InvariantCulture);
                }
                case SettingKind.Flags:
                {
                    long bits = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                    List<string> set = new List<string>();
                    for (int i = 0; i < f.EnumValues.Length; i++)
                    {
                        if ((bits & f.EnumValues[i]) != 0)
                        {
                            set.Add(f.EnumNames[i]);
                        }
                    }
                    return set.ToArray();
                }
                case SettingKind.Vector2:
                {
                    Vector2 v = (Vector2)raw;
                    return new double[] { v.x, v.y };
                }
                case SettingKind.Vector3:
                {
                    Vector3 v = (Vector3)raw;
                    return new double[] { v.x, v.y, v.z };
                }
                case SettingKind.Rect:
                {
                    Rect r = (Rect)raw;
                    return new double[] { r.x, r.y, r.width, r.height };
                }
                case SettingKind.Color:
                {
                    Color c = (Color)raw;
                    return new double[] { c.r, c.g, c.b, c.a };
                }
                case SettingKind.Asset:
                {
                    UnityEngine.Object uo = raw as UnityEngine.Object;
                    if (uo == null)
                    {
                        return new AssetValue();
                    }
                    JObject info = m_conv.UnityObjectRef(uo);
                    string guid = info["guid"]?.Type == JTokenType.String ? (string)info["guid"] : null;
                    return new AssetValue { Guid = guid, Info = info };
                }
                case SettingKind.AssetList:
                {
                    List<AssetValue> list = new List<AssetValue>();
                    if (raw is IEnumerable items)
                    {
                        foreach (object item in items)
                        {
                            JToken t = m_conv.Convert(item);
                            JObject info = t as JObject;
                            list.Add(new AssetValue { Guid = info?["guid"]?.Type == JTokenType.String ? (string)info["guid"] : null, Info = info });
                        }
                    }
                    return list;
                }
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ ISettingsState

        public bool? Derived(string name)
        {
            if (name == "hasPlayerSpawn")
            {
                // The camera preview's own lookup (FindObjectOfType: active objects), which it dereferences without a null check.
                return UnityEngine.Object.FindObjectOfType<ModPlayerSpawn>() != null;
            }
            return null;
        }

        public IList<string> PresetNames(ModSettingField f)
        {
            return Presets(f).Names;
        }

        /// <summary>Names in the editor's preset list that have no preset object with that internal name (unreachable in the UI).</summary>
        public IList<string> UnusablePresets(ModSettingField f) => Presets(f).Unusable;

        private sealed class PresetInfo
        {
            public List<string> Names = new List<string>();
            public List<string> Unusable = new List<string>();
            public IList Objects;
        }

        private readonly Dictionary<string, PresetInfo> m_presets = new Dictionary<string, PresetInfo>();

        private PresetInfo Presets(ModSettingField f)
        {
            bool lighting = f.Target == "lightingPreset";
            string key = lighting ? "l" : "p";
            if (m_presets.TryGetValue(key, out PresetInfo cached))
            {
                return cached;
            }
            ModSettings settings = m_mod.GetModSettings();
            if (settings == null)
            {
                throw RpcException.Internal("the mod has no settings object");
            }
            object widget;
            string[] names;
            IList objects;
            if (lighting)
            {
                settings.SetupLightingWidget(); // creates the preset cache the settings window would create (idempotent)
                widget = GameReflection.RequireField(typeof(ModSettings), "m_lightingWidgetLogic").GetValue(settings);
                names = GameReflection.RequireField(typeof(LightingWidgetLogic), "m_presetNames").GetValue(widget) as string[];
                objects = GameReflection.RequireField(typeof(LightingWidgetLogic), "m_lightingPresets").GetValue(widget) as IList;
            }
            else
            {
                settings.SetupPostProcessingWidget();
                widget = GameReflection.RequireField(typeof(ModSettings), "m_postProcessWidgetLogic").GetValue(settings);
                names = GameReflection.RequireField(typeof(PostProcessWidgetLogic), "m_presetNames").GetValue(widget) as string[];
                objects = GameReflection.RequireField(typeof(PostProcessWidgetLogic), "m_postProcessPresets").GetValue(widget) as IList;
            }
            if (names == null || objects == null)
            {
                throw RpcException.Internal("the editor's preset list is not available");
            }
            PresetInfo info = new PresetInfo { Objects = objects };
            foreach (string n in names)
            {
                // The editor matches the combo entry to a preset object by the object's internal name, not by file name.
                if (n == SettingsRules.Custom || FindPreset(objects, lighting, n) != null)
                {
                    info.Names.Add(n);
                }
                else
                {
                    info.Unusable.Add(n);
                }
            }
            m_presets[key] = info;
            return info;
        }

        private static object FindPreset(IList objects, bool lighting, string name)
        {
            foreach (object o in objects)
            {
                string pn = lighting ? (o as ModLightingSettings)?.PresetName : (o as ModPostProcessSettings)?.PresetName;
                if (pn == name)
                {
                    return o;
                }
            }
            return null;
        }

        public bool TryGetLimits(ModSettingField f, out double min, out double max)
        {
            min = 0;
            max = 10; // the UI's slider range for a parameter without a RangeAttribute
            if (!f.RangeFromAttribute)
            {
                return false;
            }
            Type t = RequiredMembers.ResolveType(typeof(EditorSystem).Assembly, f.GameType, f.Assembly);
            FieldInfo fi = t == null ? null : GameReflection.FindField(t, f.Member);
            RangeAttribute range = fi == null ? null : (RangeAttribute)fi.GetCustomAttributes(typeof(RangeAttribute), true).FirstOrDefault();
            if (range != null)
            {
                min = range.min;
                max = range.max;
            }
            return true;
        }

        /// <summary>list_assets types the mod's own textures as Texture and built-in ones as Texture2D; the field's type name is the Unity type.</summary>
        private static string ListTypeHint(string assetType) => assetType == "Texture2D" ? "Texture for the mod's own, Texture2D for built-in" : assetType;

        public string CheckAsset(ModSettingField f, string guid)
        {
            Type expected = f.AssetTypes != null && f.AssetTypes.Length > 0 && f.AssetTypes[0] == "AudioClip" ? typeof(AudioClip)
                : f.AssetTypes != null && f.AssetTypes[0] == "Texture2D" ? typeof(Texture2D) : null;
            if (expected == null)
            {
                return "this asset type cannot be written";
            }
            ModAsset asset = m_ed.GetAssetWithGuid(new SerializableGuid(Guid.Parse(guid)));
            if (asset == null)
            {
                return $"no asset with guid {guid}; use a guid from list_assets (type {ListTypeHint(f.AssetTypes[0])}{(f.AssetLocations.Contains("internal") ? ", includeBuiltIn for built-in ones" : "")}); a scene object's guid is not an asset";
            }
            if (!asset.enabled)
            {
                return $"asset '{asset.name}' is deleted (missing)";
            }
            string loc = ValueConverter.LocationName(asset);
            if (loc == "runtime" || (loc == "internal" && !f.AssetLocations.Contains("internal")) || (loc == "external" && !f.AssetLocations.Contains("external")))
            {
                return $"'{asset.name}' is a {loc} asset; the editor's picker offers only {f.AssetLocations} assets here";
            }
            if (!asset.IsUnityObject)
            {
                return $"'{asset.name}' is a {ValueConverter.AssetTypeName(asset)}, expected {f.AssetTypes[0]}";
            }
            UnityEngine.Object uo = asset.unityObjectAddressable;
            if (uo == null)
            {
                return $"asset '{asset.name}' could not be loaded";
            }
            if (!expected.IsInstanceOfType(uo))
            {
                return $"'{asset.name}' is a {ValueConverter.AssetTypeName(asset)} ({uo.GetType().Name}), expected {f.AssetTypes[0]}";
            }
            m_assets[guid] = uo;
            return null;
        }

        // ------------------------------------------------------------------ applying

        /// <summary>Applies the assignments in table order. Returns the fields written. Throws on a game error (earlier fields stay applied).</summary>
        public List<string> Apply(SettingsPlan plan, List<string> applied)
        {
            SettingsSection section = plan.Section;
            bool lighting = section.Name == "lighting";
            bool effects = section.Name == "screenEffects";
            bool touchedLighting = false;
            HashSet<string> touchedEffects = new HashSet<string>(StringComparer.Ordinal);
            if (effects)
            {
                // effect parameters are written through ParameterOverride<T>.value / overrideState (Core/RequiredMembers.cs): a renamed
                // member fails here, before the first field is written
                GameReflection.RequireField(typeof(ParameterOverride<>), "value");
                GameReflection.RequireField(typeof(ParameterOverride<>), "overrideState");
            }

            foreach (SettingsAssignment a in plan.Assignments)
            {
                ModSettingField f = a.Field;
                if (f.Kind == SettingKind.Preset)
                {
                    SelectPreset(f, (string)a.Value);
                    touchedLighting = lighting;
                    applied.Add(f.Name);
                    continue;
                }
                if (f.IsEffect)
                {
                    SetEffectPresent(f, (bool)a.Value);
                    touchedEffects.Add(f.GameType.Substring(f.GameType.LastIndexOf('.') + 1));
                    applied.Add(f.Name);
                    continue;
                }
                object target = Target(f.Target);
                if (target == null)
                {
                    throw RpcException.Internal($"the object behind '{f.Name}' is not available (the effect was removed?)");
                }
                WriteField(f, target, a.Value);
                if (lighting)
                {
                    touchedLighting = true;
                }
                if (effects)
                {
                    touchedEffects.Add(f.GameType.Substring(f.GameType.LastIndexOf('.') + 1));
                }
                applied.Add(f.Name);
            }

            // side effects the settings window triggers
            if (section.ModType == "Minigame" && (section.Name == "playerSettings" || section.Name == "minigameSettings"))
            {
                (m_mod as MinigameMod)?.MinigameSettings?.GenerateMinigameControls();
            }
            if (touchedLighting)
            {
                m_mod.lighting?.ApplyLightingChangesToRenderSettings(true);
            }
            if (effects)
            {
                ForceEffectValues(touchedEffects);
                RefreshPostProcessLayer();
            }
            return applied;
        }

        private object ToGame(ModSettingField f, Type memberType, object v)
        {
            switch (f.Kind)
            {
                case SettingKind.Bool: return (bool)v;
                case SettingKind.Int: return memberType == typeof(long) ? (object)(long)Math.Round((double)v) : (int)Math.Round((double)v);
                case SettingKind.Float: return (float)(double)v;
                case SettingKind.String: return (string)v;
                case SettingKind.Enum:
                {
                    int i = Array.IndexOf(f.EnumNames, (string)v);
                    return Enum.ToObject(memberType, f.EnumValues[i]);
                }
                case SettingKind.NamedInt:
                {
                    int i = Array.IndexOf(f.EnumNames, (string)v);
                    return (int)f.EnumValues[i];
                }
                case SettingKind.Flags:
                {
                    long bits = 0;
                    foreach (string n in (string[])v)
                    {
                        bits |= f.EnumValues[Array.IndexOf(f.EnumNames, n)];
                    }
                    return Enum.ToObject(memberType, bits);
                }
                case SettingKind.Vector2:
                {
                    double[] a = (double[])v;
                    return new Vector2((float)a[0], (float)a[1]);
                }
                case SettingKind.Vector3:
                {
                    double[] a = (double[])v;
                    return new Vector3((float)a[0], (float)a[1], (float)a[2]);
                }
                case SettingKind.Rect:
                {
                    double[] a = (double[])v;
                    return new Rect((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                }
                case SettingKind.Color:
                {
                    double[] a = (double[])v;
                    return new Color((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                }
                case SettingKind.Asset:
                {
                    string guid = ((AssetValue)v).Guid;
                    return guid == null ? null : m_assets.TryGetValue(guid, out UnityEngine.Object uo) ? uo : null;
                }
                default:
                    throw RpcException.Internal("cannot write a value of kind " + f.Kind);
            }
        }

        private void WriteField(ModSettingField f, object target, object value)
        {
            object current = GetMember(target, f.Member);
            if (current != null && IsParameter(current))
            {
                Type valueType = GameReflection.RequireField(ParameterBase(current.GetType()), "value").FieldType;
                if (f.IsParam)
                {
                    ParamValue pv = (ParamValue)value;
                    if (pv.Value != null)
                    {
                        SetParamField(current, "value", ToGame(f, valueType, pv.Value));
                    }
                    if (pv.Override.HasValue)
                    {
                        SetParamField(current, "overrideState", pv.Override.Value);
                    }
                }
                else
                {
                    SetParamField(current, "value", ToGame(f, valueType, value));
                }
                return;
            }
            Type mt = MemberType(target, f.Member);
            object game = ToGame(f, mt, value);
            if (f.Kind == SettingKind.Asset && game == null && ((AssetValue)value).Guid != null)
            {
                throw RpcException.Internal("the asset was not resolved before applying");
            }
            SetMember(target, f.Member, game);
            if (f.Kind == SettingKind.Asset && (f.Member == "screenshotTexture" || f.Member == "previewImage"))
            {
                SetPreviewGuid(target, game as Texture2D);
            }
        }

        /// <summary>The definition / map details also keep the preview image's guid (not serialised; read by GetPreviewImageGuid).</summary>
        private static void SetPreviewGuid(object target, Texture2D texture)
        {
            SerializableGuid guid = texture == null ? SerializableGuid.Empty : (WorkshopController.Instance?.GetAssetWithObject(texture)?.guid ?? SerializableGuid.Empty);
            if (target is MinigameDefinition d)
            {
                d.screenshotTextureGuid = guid;
            }
            else if (target is MapDetails m)
            {
                m.previewImageGuid = guid;
            }
        }

        // ---- presets

        private void SelectPreset(ModSettingField f, string name)
        {
            bool lighting = f.Target == "lightingPreset";
            PresetInfo info = Presets(f);
            if (lighting)
            {
                // What the Lighting Preset combo does on a change: remember the name, pick the object, assign it, apply it.
                ModLightingSettings obj = name == SettingsRules.Custom ? m_mod.CustomLighting : (ModLightingSettings)FindPreset(info.Objects, true, name);
                if (obj == null)
                {
                    throw RpcException.Internal($"no lighting object for preset '{name}'; nothing was changed");
                }
                m_mod.SetSelectedLightingPreset(name);
                m_mod.lighting = obj;
                obj.ApplyLightingChangesToRenderSettings(true);
            }
            else
            {
                ModPostProcessSettings obj = name == SettingsRules.Custom ? m_mod.CustomPostProcess : (ModPostProcessSettings)FindPreset(info.Objects, false, name);
                if (obj == null)
                {
                    throw RpcException.Internal($"no screen-effects object for preset '{name}'; nothing was changed");
                }
                m_mod.SetSelectedPostProcessingPreset(name);
                m_mod.postProcess = obj;
                m_ed.m_postProcessVolume.sharedProfile = obj.GetPostProcessProfile();
                RefreshPostProcessLayer();
            }
        }

        // ---- screen effects

        private PostProcessEffectSettings EffectType(ModSettingField f, out Type type)
        {
            type = RequiredMembers.ResolveType(typeof(EditorSystem).Assembly, f.GameType, f.Assembly);
            if (type == null)
            {
                throw RpcException.Internal(GameReflection.MissingMessage(typeof(PostProcessProfile), f.GameType));
            }
            return FindEffect(type.Name);
        }

        private void SetEffectPresent(ModSettingField f, bool present)
        {
            PostProcessProfile profile = CustomProfile();
            if (profile == null)
            {
                throw RpcException.Internal("the mod has no custom screen-effects profile");
            }
            PostProcessEffectSettings existing = EffectType(f, out Type type);
            if (present && existing == null)
            {
                profile.AddSettings(type); // the Add Effect menu
            }
            else if (!present && existing != null)
            {
                profile.RemoveSettings(type); // closing the effect's header
            }
        }

        /// <summary>Values the editor forces while an effect's header is open (see SettingsRules.ForcedEffectValues).</summary>
        private void ForceEffectValues(HashSet<string> touched)
        {
            foreach (string[] forced in SettingsRules.ForcedEffectValues)
            {
                if (!touched.Contains(forced[0]))
                {
                    continue;
                }
                PostProcessEffectSettings effect = FindEffect(forced[0]);
                if (effect == null)
                {
                    continue;
                }
                object parameter = GetMember(effect, forced[1]);
                Type valueType = GameReflection.RequireField(ParameterBase(parameter.GetType()), "value").FieldType;
                if (valueType.IsEnum)
                {
                    SetParamField(parameter, "value", Enum.Parse(valueType, forced[3]));
                }
                if (forced[4] == "override")
                {
                    SetParamField(parameter, "overrideState", true);
                }
            }
        }

        /// <summary>The toolbar's Post Processing toggle (what the editor does after adding/removing effects and flipping overrides).</summary>
        private void RefreshPostProcessLayer()
        {
            Camera cam = m_ed.m_sceneViewCamera;
            PostProcessLayer layer = cam == null ? null : cam.GetComponent<PostProcessLayer>();
            if (layer != null)
            {
                layer.enabled = false;
                layer.enabled = true;
            }
        }

        /// <summary>Assigns a texture as the minigame's / board's presentation image (what the Preview Image picker does) and returns the field it set.</summary>
        public string AssignPreview(Texture2D texture)
        {
            object target;
            string member;
            if (m_mod is MinigameMod mg)
            {
                target = mg.GetMinigameDefinition();
                member = "screenshotTexture";
            }
            else
            {
                target = (m_mod as BoardMod)?.GetMapDetails();
                member = "previewImage";
            }
            if (target == null)
            {
                throw RpcException.Internal("the mod has no details object");
            }
            SetMember(target, member, texture);
            SetPreviewGuid(target, texture);
            return "details.previewImage";
        }

        // ------------------------------------------------------------------ summaries for replies

        /// <summary>Number of effects in the custom screen-effects profile.</summary>
        public int CustomEffectCount() => CustomProfile()?.settings?.Count ?? 0;
    }
}
