using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ModSystem.Logic;
using PummelPartyMcp.Core;
using UnityEngine;

namespace PummelPartyMcp.Game
{
    /// <summary>
    /// Maps property types to <see cref="ValueTypeDesc"/> and builds typed game values from parsed JSON values
    /// (the inverse of ValueConverter for the writable types). Main thread only.
    /// </summary>
    internal static class ValueBuilder
    {
        private static readonly ConcurrentDictionary<Type, ValueTypeDesc> s_descs = new ConcurrentDictionary<Type, ValueTypeDesc>();

        public const string TriggerMessage = "triggers and action lists (ModLogicTrigger, ModAction) are edited with the action tools (add_action, set_action, remove_action, move_action; add_trigger, set_trigger, remove_trigger), not with set_property";

        public static ValueTypeDesc Describe(Type t) => s_descs.GetOrAdd(t, DescribeUncached);

        private static ValueTypeDesc DescribeUncached(Type t)
        {
            string name = ValueConverter.FriendlyTypeName(t);
            if (t == typeof(bool)) return ValueTypeDesc.Simple(ValueKind.Bool, name);
            if (t == typeof(byte)) return ValueTypeDesc.Integer(name, byte.MinValue, byte.MaxValue);
            if (t == typeof(sbyte)) return ValueTypeDesc.Integer(name, sbyte.MinValue, sbyte.MaxValue);
            if (t == typeof(short)) return ValueTypeDesc.Integer(name, short.MinValue, short.MaxValue);
            if (t == typeof(ushort)) return ValueTypeDesc.Integer(name, ushort.MinValue, ushort.MaxValue);
            if (t == typeof(int)) return ValueTypeDesc.Integer(name, int.MinValue, int.MaxValue);
            if (t == typeof(uint)) return ValueTypeDesc.Integer(name, uint.MinValue, uint.MaxValue);
            if (t == typeof(long)) return ValueTypeDesc.Integer(name, long.MinValue, long.MaxValue);
            if (t == typeof(ulong)) return ValueTypeDesc.Integer(name, ulong.MinValue, ulong.MaxValue);
            if (t == typeof(float)) return ValueTypeDesc.Simple(ValueKind.Float, name);
            if (t == typeof(double)) return ValueTypeDesc.Simple(ValueKind.Double, name);
            if (t == typeof(string)) return ValueTypeDesc.Simple(ValueKind.String, name);
            if (t.IsEnum)
            {
                string[] names = Enum.GetNames(t);
                long[] values = new long[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    values[i] = Convert.ToInt64(Enum.Parse(t, names[i]), CultureInfo.InvariantCulture);
                }
                return ValueTypeDesc.Enum(name, names, values, t.IsDefined(typeof(FlagsAttribute), false));
            }
            if (t == typeof(Vector2)) return ValueTypeDesc.Simple(ValueKind.Vector2, name);
            if (t == typeof(Vector3)) return ValueTypeDesc.Simple(ValueKind.Vector3, name);
            if (t == typeof(Vector4)) return ValueTypeDesc.Simple(ValueKind.Vector4, name);
            if (t == typeof(Quaternion)) return ValueTypeDesc.Simple(ValueKind.Quaternion, name);
            if (t == typeof(Color)) return ValueTypeDesc.Simple(ValueKind.Color, name);
            if (t == typeof(Color32)) return ValueTypeDesc.Simple(ValueKind.Color32, name);
            if (t == typeof(Rect)) return ValueTypeDesc.Simple(ValueKind.Rect, name);
            if (t == typeof(LayerMask)) return ValueTypeDesc.Simple(ValueKind.LayerMask, name);
            if (t == typeof(AnimationCurve)) return ValueTypeDesc.Simple(ValueKind.AnimationCurve, name);
            if (t == typeof(Gradient)) return ValueTypeDesc.Simple(ValueKind.Gradient, name);
            if (t == typeof(ParticleSystem.MinMaxCurve)) return ValueTypeDesc.Simple(ValueKind.MinMaxCurve, name);
            if (t == typeof(ParticleSystem.MinMaxGradient)) return ValueTypeDesc.Simple(ValueKind.MinMaxGradient, name);
            if (t == typeof(SerializableGuid))
            {
                return ValueTypeDesc.Unsupported(name, "guid properties are maintained by the editor; set the matching reference property instead (e.g. ItemPrefab instead of ItemPrefabGUID)");
            }
            if (typeof(ModLogicTrigger).IsAssignableFrom(t) || typeof(ModAction).IsAssignableFrom(t))
            {
                return ValueTypeDesc.Unsupported(name, TriggerMessage);
            }
            if (t.IsArray)
            {
                Type et = t.GetElementType();
                if (et == typeof(byte))
                {
                    return ValueTypeDesc.Unsupported(name, "binary data cannot be set by the plugin");
                }
                ValueTypeDesc element = Describe(et);
                if (element.Kind == ValueKind.Unsupported)
                {
                    return ValueTypeDesc.Unsupported(name, element.UnsupportedReason);
                }
                return ValueTypeDesc.ArrayOf(element);
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(t) || typeof(ModAsset).IsAssignableFrom(t))
            {
                return ValueTypeDesc.Reference(name);
            }
            return ValueTypeDesc.Unsupported(name, $"values of type {name} cannot be set by the plugin yet");
        }

        /// <summary>Builds a value of exactly <paramref name="t"/> from a parsed value (Parse already validated the shape).</summary>
        public static object Build(PValue v, Type t, ReferenceResolver refs, string path)
        {
            if (t.IsArray)
            {
                PArray arr = (PArray)v;
                Type et = t.GetElementType();
                Array result = Array.CreateInstance(et, arr.Items.Count);
                for (int i = 0; i < arr.Items.Count; i++)
                {
                    result.SetValue(Build(arr.Items[i], et, refs, $"{path}[{i}]"), i);
                }
                return result;
            }
            switch (v)
            {
                case PBool b: return b.Value;
                case PInteger i:
                    if (t == typeof(LayerMask))
                    {
                        return new LayerMask { value = (int)i.Value };
                    }
                    return Convert.ChangeType(i.Value, t, CultureInfo.InvariantCulture);
                case PNumber n: return t == typeof(float) ? (object)(float)n.Value : n.Value;
                case PString s: return s.Value;
                case PEnum e: return Enum.ToObject(t, e.Value);
                case PFloats f:
                    float[] a = f.Values;
                    if (t == typeof(Vector2)) return new Vector2(a[0], a[1]);
                    if (t == typeof(Vector3)) return new Vector3(a[0], a[1], a[2]);
                    if (t == typeof(Vector4)) return new Vector4(a[0], a[1], a[2], a[3]);
                    if (t == typeof(Quaternion)) return new Quaternion(a[0], a[1], a[2], a[3]);
                    if (t == typeof(Color)) return new Color(a[0], a[1], a[2], a[3]);
                    if (t == typeof(Rect)) return new Rect(a[0], a[1], a[2], a[3]);
                    break;
                case PBytes c: return new Color32(c.Values[0], c.Values[1], c.Values[2], c.Values[3]);
                case PRef r: return refs.Resolve(r.Guid, t, path);
                case PCurve curve: return Curve(curve, path);
                case PMinMaxCurve mmc: return MinMaxCurve(mmc, path);
                case PGradient g: return Gradient(g, path);
                case PMinMaxGradient mmg: return MinMaxGradient(mmg, path);
            }
            throw RpcException.Internal($"'{path}': cannot build a {t.Name} from {v?.GetType().Name ?? "null"}");
        }

        private static AnimationCurve Curve(PCurve c, string path)
        {
            Keyframe[] keys = new Keyframe[c.Keys.Count];
            for (int i = 0; i < keys.Length; i++)
            {
                PKey k = c.Keys[i];
                keys[i] = new Keyframe(k.Time, k.Value, k.InTangent, k.OutTangent);
            }
            AnimationCurve curve = new AnimationCurve(keys)
            {
                preWrapMode = (WrapMode)Enum.Parse(typeof(WrapMode), c.PreWrapMode, true),
                postWrapMode = (WrapMode)Enum.Parse(typeof(WrapMode), c.PostWrapMode, true),
            };
            return curve;
        }

        private static ParticleSystem.MinMaxCurve MinMaxCurve(PMinMaxCurve c, string path)
        {
            switch (c.Mode)
            {
                case "Constant": return new ParticleSystem.MinMaxCurve(c.Constant);
                case "TwoConstants": return new ParticleSystem.MinMaxCurve(c.ConstantMin, c.ConstantMax);
                case "Curve": return new ParticleSystem.MinMaxCurve(c.CurveMultiplier, Curve(c.Curve, path + ".curve"));
                case "TwoCurves": return new ParticleSystem.MinMaxCurve(c.CurveMultiplier, Curve(c.CurveMin, path + ".curveMin"), Curve(c.CurveMax, path + ".curveMax"));
            }
            throw RpcException.InvalidParams($"'{path}': unknown curve mode {c.Mode}");
        }

        private static Gradient Gradient(PGradient g, string path)
        {
            Gradient result = new Gradient();
            if (!Enum.IsDefined(typeof(UnityEngine.GradientMode), g.Mode))
            {
                throw RpcException.InvalidParams($"'{path}.mode': gradient mode '{g.Mode}' is not available in this Unity version");
            }
            result.mode = (UnityEngine.GradientMode)Enum.Parse(typeof(UnityEngine.GradientMode), g.Mode);
            GradientColorKey[] colors = new GradientColorKey[g.ColorKeys.Count];
            for (int i = 0; i < colors.Length; i++)
            {
                float[] k = g.ColorKeys[i];
                colors[i] = new GradientColorKey(new Color(k[0], k[1], k[2], k[3]), k[4]);
            }
            GradientAlphaKey[] alphas = new GradientAlphaKey[g.AlphaKeys.Count];
            for (int i = 0; i < alphas.Length; i++)
            {
                alphas[i] = new GradientAlphaKey(g.AlphaKeys[i][0], g.AlphaKeys[i][1]);
            }
            result.SetKeys(colors, alphas);
            return result;
        }

        private static ParticleSystem.MinMaxGradient MinMaxGradient(PMinMaxGradient g, string path)
        {
            Color C(float[] a) => new Color(a[0], a[1], a[2], a[3]);
            switch (g.Mode)
            {
                case "Color": return new ParticleSystem.MinMaxGradient(C(g.Color));
                case "TwoColors": return new ParticleSystem.MinMaxGradient(C(g.ColorMin), C(g.ColorMax));
                case "Gradient": return new ParticleSystem.MinMaxGradient(Gradient(g.Gradient, path + ".gradient"));
                case "TwoGradients": return new ParticleSystem.MinMaxGradient(Gradient(g.GradientMin, path + ".gradientMin"), Gradient(g.GradientMax, path + ".gradientMax"));
                case "RandomColor":
                    ParticleSystem.MinMaxGradient r = new ParticleSystem.MinMaxGradient(Gradient(g.Gradient, path + ".gradient"));
                    r.mode = ParticleSystemGradientMode.RandomColor;
                    return r;
            }
            throw RpcException.InvalidParams($"'{path}': unknown gradient mode {g.Mode}");
        }
    }

    /// <summary>
    /// Resolves reference guids the way the editor's object picker / drag and drop produce property values
    /// (ModEditorGui.StartObject callback and TrySetBehaviour):
    /// <list type="bullet">
    /// <item>a mod asset guid (list_assets, or a scene component's guid as get_object shows it in references): the value is
    /// the asset itself for non-Unity assets (PummelPrefab, PummelVisualScript), else its unityObjectAddressable; a
    /// Transform becomes its GameObject when the property is a GameObject;</item>
    /// <item>an object guid (ModTransform guid from get_hierarchy): the object's Transform (or GameObject) when that fits
    /// the property type; else the one component on that object whose class or wrapped Unity component matches it, like
    /// dropping the object from the hierarchy (none or several matching: refused, the candidates and guids listed). The
    /// editor registers every ModTransform as an asset, so the object case is detected on the asset (decision logic in
    /// Core/ReferenceRules.cs).</item>
    /// </list>
    /// Scene-object references must point into the same editing context as the edited component (scene objects to scene
    /// objects, prefab objects to the same prefab), as the picker's location filter does. The resolved value must be an
    /// instance of the property's (element) type.
    /// </summary>
    internal sealed class ReferenceResolver
    {
        private readonly EditorSystem m_ed;
        private readonly Transform m_ownerContext;

        public ReferenceResolver(EditorSystem ed, Component owner)
        {
            m_ed = ed;
            m_ownerContext = owner != null ? EditContext.ContextOf(ed, owner.transform) : null;
        }

        public object Resolve(string guid, Type expected, string path)
        {
            if (guid == null)
            {
                return null;
            }
            SerializableGuid sg = new SerializableGuid(Guid.Parse(guid));
            ModAsset asset = m_ed.GetAssetWithGuid(sg);

            // The editor registers each object's ModTransform as a runtime asset (guid = object guid, value = Transform),
            // so an object guid usually finds an asset. Detect that case; otherwise look the object up in the hierarchy.
            ModTransform mt = asset != null ? OwnObjectOf(asset, guid) : EditorAccess.FindByGuid(m_ed, guid, out _);
            ExpectedRefKind kind = expected == typeof(GameObject) ? ExpectedRefKind.GameObject
                : expected == typeof(Transform) ? ExpectedRefKind.Transform : ExpectedRefKind.Other;
            List<ModBehaviour> comps = null;
            RefDecision decision = ReferenceRules.Decide(guid, asset != null, mt != null ? EditorAccess.GuidOf(mt) : null, mt != null ? mt.name : null,
                kind, mt != null && expected.IsInstanceOfType(mt.transform),
                () =>
                {
                    CheckSameContext(mt.transform, path); // deleted / other-context objects are refused before listing components
                    comps = EditorAccess.ModComponents(mt.gameObject);
                    return comps.Select(mb => new RefComponent
                    {
                        TypeName = mb.GetType().Name,
                        Guid = EditorAccess.GuidOf(mb),
                        Matches = ComponentMatches(mb, expected),
                    }).ToList();
                },
                expected.Name, path);
            if (decision.Source == RefSource.Asset)
            {
                return FromAsset(asset, expected, path);
            }

            // An object dropped on a field that wants one of its components (TrySetBehaviour): that component, preferring
            // its registered asset (so the reference has a guid when the mod or an undo snapshot is serialised).
            ModBehaviour hit = comps[decision.ComponentIndex];
            ModAsset registered = m_ed.GetAssetWithGuid(hit.guid);
            if (registered != null)
            {
                object fromAsset = AssetValue(registered, expected, path);
                if (expected.IsInstanceOfType(fromAsset))
                {
                    return FromAsset(registered, expected, path);
                }
            }
            Component wrapped = hit.Component;
            object direct = expected == typeof(GameObject) ? mt.gameObject : (expected.IsInstanceOfType(hit) ? hit : (object)wrapped);
            if (!expected.IsInstanceOfType(direct))
            {
                throw RpcException.InvalidParams($"'{path}': the {hit.GetType().Name} of '{mt.name}' does not give a {expected.Name}");
            }
            return direct;
        }

        /// <summary>The component's class or its wrapped Unity component fits (what TrySetBehaviour accepts), or a GameObject field and the object's Transform.</summary>
        private static bool ComponentMatches(ModBehaviour mb, Type expected)
        {
            if (expected.IsInstanceOfType(mb) || (expected == typeof(GameObject) && mb is ModTransform))
            {
                return true;
            }
            Component wrapped = mb.Component; // what TrySetBehaviour reads (also sets up a missing wrapped component)
            return wrapped != null && expected.IsInstanceOfType(wrapped);
        }

        /// <summary>The mod object whose own ModTransform is <paramref name="asset"/> (asset value = its Transform, same guid), else null.</summary>
        private static ModTransform OwnObjectOf(ModAsset asset, string guid)
        {
            if (!asset.IsUnityObject || ValueConverter.LocationName(asset) == "internal")
            {
                return null;
            }
            Transform tr = asset.unityObjectAddressable as Transform;
            ModTransform mt = tr != null ? tr.GetComponent<ModTransform>() : null;
            return mt != null && string.Equals(EditorAccess.GuidOf(mt), guid, StringComparison.OrdinalIgnoreCase) ? mt : null;
        }

        /// <summary>The value an asset gives for a field of type <paramref name="expected"/> (not type-checked).</summary>
        private static object AssetValue(ModAsset asset, Type expected, string path)
        {
            if (!asset.IsUnityObject)
            {
                return asset;
            }
            UnityEngine.Object uo = asset.unityObjectAddressable; // loads internal assets on demand, as the picker does
            if (uo == null)
            {
                throw RpcException.InvalidParams($"'{path}': asset '{asset.name}' could not be loaded");
            }
            return expected == typeof(GameObject) && uo is Transform tr ? tr.gameObject : (object)uo;
        }

        private object FromAsset(ModAsset asset, Type expected, string path)
        {
            if (!asset.enabled)
            {
                throw RpcException.InvalidParams($"'{path}': '{asset.name}' is deleted (missing)");
            }
            object value = AssetValue(asset, expected, path);
            if (!expected.IsInstanceOfType(value))
            {
                throw RpcException.InvalidParams($"'{path}': '{asset.name}' is a {ValueConverter.AssetTypeName(asset)} ({value.GetType().Name}), expected {expected.Name}");
            }
            GameObject go = value is Component c ? c.gameObject : value as GameObject;
            if (go != null && go.GetComponent<ModTransform>() != null && ValueConverter.LocationName(asset) != "internal")
            {
                CheckSameContext(go.transform, path); // scene/prefab object reference
            }
            return value;
        }

        private void CheckSameContext(Transform target, string path)
        {
            Transform ctx = EditContext.ContextOf(m_ed, target);
            if (ctx == null)
            {
                throw RpcException.InvalidParams($"'{path}': '{target.name}' is not part of the mod (deleted?)");
            }
            if (m_ownerContext != null && ctx != m_ownerContext)
            {
                throw RpcException.InvalidParams($"'{path}': '{target.name}' is in a different context (scene objects can only reference scene objects, prefab objects only objects of the same prefab)");
            }
        }
    }
}
