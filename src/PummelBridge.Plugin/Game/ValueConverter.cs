using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ModSystem.Logic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PummelBridge.Game
{
    /// <summary>
    /// Converts game values to JSON without calling property getters on game classes (except Unity value types and
    /// the few pure Unity getters listed below). Nested serializable classes are walked by their Unity-serialized
    /// fields, i.e. what JsonUtility / the mod file format persists. Main thread only.
    /// </summary>
    internal sealed class ValueConverter
    {
        public const int DefaultMaxDepth = 8;
        public const int MaxArrayItems = 512;

        private static readonly ConcurrentDictionary<Type, FieldInfo[]> s_serializedFields = new ConcurrentDictionary<Type, FieldInfo[]>();

        private static readonly HashSet<string> s_actionCoreFields = new HashSet<string> { "m_type", "m_version", "m_targetFlags", "m_targets" };
        private static readonly HashSet<string> s_triggerCoreFields = new HashSet<string> { "m_type", "m_actions" };

        private readonly PummelMod m_mod;
        private readonly int m_maxDepth;
        private readonly bool m_summarizeTriggers;
        private readonly HashSet<object> m_visiting = new HashSet<object>(ReferenceComparer.Instance);
        private Dictionary<string, ModAsset> m_assetsByGuid;

        public ValueConverter(PummelMod mod, bool summarizeTriggers, int maxDepth = DefaultMaxDepth)
        {
            m_mod = mod;
            m_summarizeTriggers = summarizeTriggers;
            m_maxDepth = maxDepth;
        }

        public JToken Convert(object value, int depth = 0)
        {
            if (value == null)
            {
                return JValue.CreateNull();
            }
            if (value is UnityEngine.Object uo)
            {
                return uo == null ? JValue.CreateNull() : UnityObjectRef(uo);
            }

            switch (value)
            {
                case string s: return new JValue(s);
                case bool b: return new JValue(b);
                case char c: return new JValue(c.ToString());
                case float f: return Number(f);
                case double d: return Number(d);
                case decimal m: return new JValue(m);
                case byte or sbyte or short or ushort or int or uint or long: return new JValue(System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
                case ulong ul: return new JValue(ul);
                case Enum e: return new JValue(e.ToString());
                case SerializableGuid g: return new JValue(g.ToString());
                case Guid g2: return new JValue(g2.ToString());
                case Vector2 v2: return new JObject { ["x"] = Number(v2.x), ["y"] = Number(v2.y) };
                case Vector3 v3: return Vec3(v3);
                case Vector4 v4: return new JObject { ["x"] = Number(v4.x), ["y"] = Number(v4.y), ["z"] = Number(v4.z), ["w"] = Number(v4.w) };
                case Vector2Int vi2: return new JObject { ["x"] = vi2.x, ["y"] = vi2.y };
                case Vector3Int vi3: return new JObject { ["x"] = vi3.x, ["y"] = vi3.y, ["z"] = vi3.z };
                case Quaternion q: return new JObject { ["x"] = Number(q.x), ["y"] = Number(q.y), ["z"] = Number(q.z), ["w"] = Number(q.w) };
                case Color col: return ColorJson(col);
                case Color32 c32: return new JObject { ["r"] = c32.r, ["g"] = c32.g, ["b"] = c32.b, ["a"] = c32.a };
                case Rect r: return new JObject { ["x"] = Number(r.x), ["y"] = Number(r.y), ["width"] = Number(r.width), ["height"] = Number(r.height) };
                case Bounds bo: return new JObject { ["center"] = Vec3(bo.center), ["size"] = Vec3(bo.size) };
                case LayerMask lm: return new JValue(lm.value);
                case AnimationCurve curve: return CurveJson(curve);
                case Gradient grad: return GradientJson(grad);
                case ParticleSystem.MinMaxCurve mmc: return MinMaxCurveJson(mmc);
                case ParticleSystem.MinMaxGradient mmg: return MinMaxGradientJson(mmg);
                case ParticleSystem.Burst burst:
                    return new JObject
                    {
                        ["time"] = Number(burst.time),
                        ["count"] = MinMaxCurveJson(burst.count),
                        ["cycleCount"] = burst.cycleCount,
                        ["repeatInterval"] = Number(burst.repeatInterval),
                        ["probability"] = Number(burst.probability),
                    };
                case ModAsset asset: return AssetRef(asset);
                case ModLogicTrigger trigger: return Trigger(trigger, depth);
                case ModAction action: return Action(action, -1, depth);
                case Delegate _: return Unsupported(value.GetType());
            }

            Type type = value.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ModAssetReference<>))
            {
                return AssetReferenceJson(value);
            }
            if (depth >= m_maxDepth)
            {
                return new JObject { ["truncated"] = FriendlyTypeName(type) };
            }
            if (value is IDictionary)
            {
                return Unsupported(type);
            }
            if (value is IList list)
            {
                return ListJson(list, depth);
            }
            if (type.IsSerializable && (type.IsClass || (type.IsValueType && !type.IsPrimitive)))
            {
                return SerializedFields(value, depth, null);
            }
            return Unsupported(type);
        }

        // ---------------- logic (triggers / actions) ----------------

        public JToken Trigger(ModLogicTrigger trigger, int depth)
        {
            if (trigger == null)
            {
                return JValue.CreateNull();
            }
            if (depth >= m_maxDepth)
            {
                return new JObject { ["truncated"] = FriendlyTypeName(trigger.GetType()) };
            }
            ModAction[] actions = trigger.Actions;
            if (m_summarizeTriggers)
            {
                return new JObject
                {
                    ["triggerType"] = trigger.TriggerType.ToString(),
                    ["actionCount"] = actions?.Length ?? 0,
                };
            }
            JObject result = new JObject
            {
                ["triggerType"] = trigger.TriggerType.ToString(),
                ["triggerClass"] = trigger.GetType().Name,
                ["triggerParameters"] = SerializedFields(trigger, depth, s_triggerCoreFields),
            };
            JArray list = new JArray();
            if (actions != null)
            {
                for (int i = 0; i < actions.Length; i++)
                {
                    list.Add(Action(actions[i], i, depth + 1));
                }
            }
            result["actions"] = list;
            return result;
        }

        public JToken Action(ModAction action, int index, int depth)
        {
            if (action == null)
            {
                JObject empty = new JObject { ["type"] = null };
                if (index >= 0)
                {
                    empty.AddFirst(new JProperty("index", index));
                }
                return empty;
            }
            JObject result = new JObject();
            if (index >= 0)
            {
                result["index"] = index;
            }
            result["type"] = action.Type.ToString();
            result["className"] = action.GetType().Name;
            FieldInfo versionField = GameReflection.FindField(typeof(ModAction), "m_version");
            if (versionField != null)
            {
                result["version"] = Convert(versionField.GetValue(action), depth + 1);
            }
            if (action is ModSystem.Logic.TargetAction target)
            {
                result["targetFlags"] = target.TargetFlags.ToString();
                JArray targets = new JArray();
                ModAssetReference<Component>[] refs = target.TargetReferences;
                if (refs != null)
                {
                    foreach (ModAssetReference<Component> r in refs)
                    {
                        targets.Add(r == null ? JValue.CreateNull() : AssetReferenceJson(r));
                    }
                }
                result["targets"] = targets;
            }
            else
            {
                result["targetFlags"] = null;
                result["targets"] = new JArray();
            }
            result["parameters"] = SerializedFields(action, depth, s_actionCoreFields);
            return result;
        }

        // ---------------- references ----------------

        /// <summary>Reference to a Unity object: resolved to its mod asset (same lookup the scene serializer uses).</summary>
        public JObject UnityObjectRef(UnityEngine.Object uo)
        {
            JObject j = new JObject();
            ModAsset asset = null;
            WorkshopController wc = WorkshopController.Instance;
            if (wc != null)
            {
                try
                {
                    asset = wc.GetAssetWithObject(uo);
                }
                catch (Exception)
                {
                    asset = null;
                }
            }
            j["guid"] = asset != null ? asset.guid.ToString() : null;
            j["name"] = asset != null && !string.IsNullOrEmpty(asset.name) ? asset.name : uo.name;
            j["assetType"] = uo.GetType().Name;
            if (asset != null)
            {
                j["location"] = LocationName(asset);
            }
            GameObject go = uo is Component comp ? comp.gameObject : uo as GameObject;
            if (go != null)
            {
                ModTransform mt = go.GetComponent<ModTransform>();
                if (mt != null)
                {
                    j["objectGuid"] = EditorAccess.GuidOf(mt);
                    j["objectName"] = go.name;
                }
            }
            if (asset == null)
            {
                j["registered"] = false;
            }
            return j;
        }

        public JObject AssetRef(ModAsset asset)
        {
            return new JObject
            {
                ["guid"] = asset.guid.ToString(),
                ["name"] = asset.name,
                ["assetType"] = AssetTypeName(asset),
                ["location"] = LocationName(asset),
            };
        }

        /// <summary>Reference given only by guid (backing fields of lazily-resolving getters).</summary>
        public JToken GuidRef(SerializableGuid guid)
        {
            if (guid.Equals(SerializableGuid.Empty))
            {
                return JValue.CreateNull();
            }
            string key = guid.ToString();
            if (LookupAsset(key, out ModAsset asset))
            {
                return AssetRef(asset);
            }
            return new JObject { ["guid"] = key, ["missing"] = true };
        }

        private JToken AssetReferenceJson(object reference)
        {
            Type t = reference.GetType();
            object asset = GameReflection.FindField(t, "m_asset")?.GetValue(reference);
            if (asset is UnityEngine.Object uo && uo != null)
            {
                return UnityObjectRef(uo);
            }
            if (asset is ModAsset ma)
            {
                return AssetRef(ma);
            }
            object cur = GameReflection.FindField(t, "m_curAssetGUID")?.GetValue(reference);
            object stored = GameReflection.FindField(t, "m_assetGUID")?.GetValue(reference);
            SerializableGuid g = cur is SerializableGuid c && !c.Equals(SerializableGuid.Empty) ? c
                : stored is SerializableGuid s ? s : SerializableGuid.Empty;
            return GuidRef(g);
        }

        private bool LookupAsset(string guid, out ModAsset asset)
        {
            if (m_assetsByGuid == null)
            {
                m_assetsByGuid = new Dictionary<string, ModAsset>();
                WorkshopController wc = WorkshopController.Instance;
                if (wc != null && wc.Assets != null)
                {
                    AddAll(wc.Assets.assets);
                }
                if (m_mod?.Assets != null)
                {
                    AddAll(m_mod.Assets.assets);
                }
            }
            return m_assetsByGuid.TryGetValue(guid, out asset);
        }

        private void AddAll(List<ModAsset> assets)
        {
            if (assets == null)
            {
                return;
            }
            foreach (ModAsset a in assets)
            {
                if (a != null)
                {
                    m_assetsByGuid[a.guid.ToString()] = a;
                }
            }
        }

        public static string LocationName(ModAsset asset)
        {
            SerializableGuid loc = asset.assetLocation;
            if (loc.Equals(AssetLocationGuid.Internal)) return "internal";
            if (loc.Equals(AssetLocationGuid.External)) return "external";
            if (loc.Equals(AssetLocationGuid.Runtime)) return "runtime";
            return "unknown";
        }

        /// <summary>Short type name without touching lazily-loading getters (unityObjectAddressable / UIName).</summary>
        public static string AssetTypeName(ModAsset asset)
        {
            if (asset is ModAssetExternal)
            {
                string n = asset.GetType().Name;
                return n.StartsWith("Pummel", StringComparison.Ordinal) && n.Length > 6 ? n.Substring(6) : n;
            }
            if (!string.IsNullOrEmpty(asset.assetTypeString))
            {
                int dot = asset.assetTypeString.LastIndexOf('.');
                return dot >= 0 ? asset.assetTypeString.Substring(dot + 1) : asset.assetTypeString;
            }
            Type t = asset.AssetType;
            return t != null ? t.Name : asset.GetType().Name;
        }

        // ---------------- generic helpers ----------------

        private JToken ListJson(IList list, int depth)
        {
            if (!list.GetType().IsValueType && !m_visiting.Add(list))
            {
                return new JObject { ["cycle"] = true };
            }
            try
            {
                JArray arr = new JArray();
                int n = list.Count;
                for (int i = 0; i < n && i < MaxArrayItems; i++)
                {
                    arr.Add(Convert(list[i], depth + 1));
                }
                if (n > MaxArrayItems)
                {
                    arr.Add(new JObject { ["truncatedItems"] = n - MaxArrayItems });
                }
                return arr;
            }
            finally
            {
                m_visiting.Remove(list);
            }
        }

        /// <summary>Unity-serialized instance fields (public non-[NonSerialized], or [SerializeField]/[SerializeReference]).</summary>
        public JObject SerializedFields(object value, int depth, HashSet<string> exclude)
        {
            Type type = value.GetType();
            bool isRef = !type.IsValueType;
            if (isRef && !m_visiting.Add(value))
            {
                return new JObject { ["cycle"] = FriendlyTypeName(type) };
            }
            try
            {
                JObject obj = new JObject();
                foreach (FieldInfo f in SerializedFieldsOf(type))
                {
                    if (exclude != null && exclude.Contains(f.Name))
                    {
                        continue;
                    }
                    string key = obj.ContainsKey(f.Name) ? f.DeclaringType.Name + "." + f.Name : f.Name;
                    JToken v;
                    try
                    {
                        v = Convert(f.GetValue(value), depth + 1);
                    }
                    catch (Exception e)
                    {
                        v = new JObject { ["error"] = e.Message };
                    }
                    obj[key] = v;
                }
                return obj;
            }
            finally
            {
                if (isRef)
                {
                    m_visiting.Remove(value);
                }
            }
        }

        public static FieldInfo[] SerializedFieldsOf(Type type)
        {
            return s_serializedFields.GetOrAdd(type, t =>
            {
                List<Type> chain = new List<Type>();
                for (Type cur = t; cur != null && cur != typeof(object) && cur != typeof(ValueType); cur = cur.BaseType)
                {
                    chain.Add(cur);
                }
                chain.Reverse(); // base class fields first, like Unity
                List<FieldInfo> result = new List<FieldInfo>();
                foreach (Type cur in chain)
                {
                    foreach (FieldInfo f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.IsNotSerialized || f.IsInitOnly && !f.IsDefined(typeof(SerializeField), false))
                        {
                            continue;
                        }
                        if (typeof(Delegate).IsAssignableFrom(f.FieldType))
                        {
                            continue;
                        }
                        bool serialized = f.IsPublic || f.IsDefined(typeof(SerializeField), false) || f.IsDefined(typeof(SerializeReference), false);
                        if (serialized)
                        {
                            result.Add(f);
                        }
                    }
                }
                return result.ToArray();
            });
        }

        public static string FriendlyTypeName(Type t)
        {
            if (t == null)
            {
                return "null";
            }
            if (t.IsArray)
            {
                return FriendlyTypeName(t.GetElementType()) + "[]";
            }
            if (t.IsGenericType)
            {
                string name = t.Name;
                int tick = name.IndexOf('`');
                if (tick >= 0)
                {
                    name = name.Substring(0, tick);
                }
                return name + "<" + string.Join(",", t.GetGenericArguments().Select(FriendlyTypeName).ToArray()) + ">";
            }
            return t.Name;
        }

        private static JObject Unsupported(Type t) => new JObject { ["unsupported"] = FriendlyTypeName(t) };

        private static JToken Number(float f) => float.IsNaN(f) || float.IsInfinity(f) ? new JValue(f.ToString(CultureInfo.InvariantCulture)) : new JValue(f);

        private static JToken Number(double d) => double.IsNaN(d) || double.IsInfinity(d) ? new JValue(d.ToString(CultureInfo.InvariantCulture)) : new JValue(d);

        private static JObject Vec3(Vector3 v) => new JObject { ["x"] = Number(v.x), ["y"] = Number(v.y), ["z"] = Number(v.z) };

        private static JObject ColorJson(Color c) => new JObject { ["r"] = Number(c.r), ["g"] = Number(c.g), ["b"] = Number(c.b), ["a"] = Number(c.a) };

        private static JToken CurveJson(AnimationCurve curve)
        {
            if (curve == null)
            {
                return JValue.CreateNull();
            }
            JArray keys = new JArray();
            foreach (Keyframe k in curve.keys)
            {
                keys.Add(new JObject
                {
                    ["time"] = Number(k.time),
                    ["value"] = Number(k.value),
                    ["inTangent"] = Number(k.inTangent),
                    ["outTangent"] = Number(k.outTangent),
                });
            }
            return new JObject
            {
                ["keys"] = keys,
                ["preWrapMode"] = curve.preWrapMode.ToString(),
                ["postWrapMode"] = curve.postWrapMode.ToString(),
            };
        }

        private static JToken GradientJson(Gradient g)
        {
            if (g == null)
            {
                return JValue.CreateNull();
            }
            JArray colors = new JArray();
            foreach (GradientColorKey k in g.colorKeys)
            {
                colors.Add(new JObject { ["color"] = ColorJson(k.color), ["time"] = Number(k.time) });
            }
            JArray alphas = new JArray();
            foreach (GradientAlphaKey k in g.alphaKeys)
            {
                alphas.Add(new JObject { ["alpha"] = Number(k.alpha), ["time"] = Number(k.time) });
            }
            return new JObject { ["mode"] = g.mode.ToString(), ["colorKeys"] = colors, ["alphaKeys"] = alphas };
        }

        private static JObject MinMaxCurveJson(ParticleSystem.MinMaxCurve c)
        {
            JObject j = new JObject { ["mode"] = c.mode.ToString() };
            switch (c.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    j["constant"] = Number(c.constant);
                    break;
                case ParticleSystemCurveMode.TwoConstants:
                    j["constantMin"] = Number(c.constantMin);
                    j["constantMax"] = Number(c.constantMax);
                    break;
                case ParticleSystemCurveMode.Curve:
                    j["curveMultiplier"] = Number(c.curveMultiplier);
                    j["curve"] = CurveJson(c.curve);
                    break;
                case ParticleSystemCurveMode.TwoCurves:
                    j["curveMultiplier"] = Number(c.curveMultiplier);
                    j["curveMin"] = CurveJson(c.curveMin);
                    j["curveMax"] = CurveJson(c.curveMax);
                    break;
            }
            return j;
        }

        private static JObject MinMaxGradientJson(ParticleSystem.MinMaxGradient g)
        {
            JObject j = new JObject { ["mode"] = g.mode.ToString() };
            switch (g.mode)
            {
                case ParticleSystemGradientMode.Color:
                    j["color"] = ColorJson(g.color);
                    break;
                case ParticleSystemGradientMode.TwoColors:
                    j["colorMin"] = ColorJson(g.colorMin);
                    j["colorMax"] = ColorJson(g.colorMax);
                    break;
                case ParticleSystemGradientMode.Gradient:
                case ParticleSystemGradientMode.RandomColor:
                    j["gradient"] = GradientJson(g.gradient);
                    break;
                case ParticleSystemGradientMode.TwoGradients:
                    j["gradientMin"] = GradientJson(g.gradientMin);
                    j["gradientMax"] = GradientJson(g.gradientMax);
                    break;
            }
            return j;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
