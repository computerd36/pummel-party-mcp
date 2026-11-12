using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ModSystem.Logic;
using PummelPartyMcp.Core;
using UnityEngine;

namespace PummelPartyMcp.Game
{
    /// <summary>
    /// Game side of the inspector rules (Core/InspectorRules.cs): normalizes values for the rule engine, reads the
    /// component's current values without side effects, and answers the "derived" conditions. Main thread only.
    /// </summary>
    internal static class InspectorRuleHost
    {
        /// <summary>
        /// Value as the rule engine sees it: bool, double, string, string[] (enum names / curve mode), bool for references
        /// and arrays of references, <see cref="InspectorRules.Unknown"/> for anything else (vectors, colors, ...).
        /// </summary>
        public static object Normalize(object v)
        {
            if (v == null)
            {
                return false; // null reference / unset
            }
            switch (v)
            {
                case bool b: return b;
                case string s: return s;
                case ParticleSystem.MinMaxCurve curve: return new[] { curve.mode.ToString() };
                case UnityEngine.Object uo: return uo != null;
                case ModAsset _: return true;
            }
            Type t = v.GetType();
            if (t.IsEnum)
            {
                return EnumNames(t, v);
            }
            if (v is IConvertible && !(v is char) && t.IsPrimitive)
            {
                return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (v is Array arr)
            {
                bool refs = typeof(UnityEngine.Object).IsAssignableFrom(t.GetElementType()) || typeof(ModAsset).IsAssignableFrom(t.GetElementType());
                if (!refs)
                {
                    return arr.Length > 0;
                }
                foreach (object o in arr)
                {
                    if (o is UnityEngine.Object u ? u != null : o != null)
                    {
                        return true;
                    }
                }
                return false;
            }
            return InspectorRules.Unknown;
        }

        /// <summary>Flags: the names of the set single-bit flags; otherwise the one value name.</summary>
        private static string[] EnumNames(Type t, object v)
        {
            if (t.IsDefined(typeof(FlagsAttribute), false))
            {
                long bits = Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
                List<string> names = new List<string>();
                foreach (object value in Enum.GetValues(t))
                {
                    long f = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (f != 0 && (f & (f - 1)) == 0 && (bits & f) == f)
                    {
                        names.Add(Enum.GetName(t, value));
                    }
                }
                return names.ToArray();
            }
            return new[] { Enum.GetName(t, v) ?? v.ToString() };
        }

        /// <summary>
        /// Safe to call the component's property getters: the wrapper pattern's getters run SetupComponents() while the underlying
        /// Unity component is missing, and the particle renderer getters add a renderer. Those are reported as unknown instead.
        /// </summary>
        private static bool CanRead(ModBehaviour comp, string property)
        {
            if (ComponentReader.WrapperNotReady(comp))
            {
                return false;
            }
            return !(comp is ModParticleSystem && ComponentReader.ParticleSetterNeedsRenderer(property) && ComponentReader.ParticleRendererMissing(comp));
        }

        /// <summary>Context for one set_property(ies) call: the call's built values win over the component's current ones.</summary>
        public static IRuleContext Context(ModBehaviour comp, ModReflectedType rt, IDictionary<string, object> pendingBuilt)
        {
            Dictionary<string, object> pending = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> kv in pendingBuilt)
            {
                pending[kv.Key] = Normalize(kv.Value);
            }
            return new InspectorRules.AfterCallContext(pending, name => Live(comp, rt, name), name => Derived(comp, name));
        }

        /// <summary>Context of the component as it is now (get_object hints).</summary>
        public static IRuleContext Context(ModBehaviour comp, ModReflectedType rt) => Context(comp, rt, new Dictionary<string, object>());

        private static object Live(ModBehaviour comp, ModReflectedType rt, string name)
        {
            if (!rt.propertyMap.TryGetValue(name, out ReflectedProperty p) || !CanRead(comp, name))
            {
                return InspectorRules.Unknown;
            }
            return Normalize(p.GetValue(comp));
        }

        private static bool? Derived(ModBehaviour comp, string name)
        {
            switch (name)
            {
                case "NoLinkedRenderer":
                    return comp is ModMeshCollider mc && !ComponentReader.WrapperNotReady(comp) ? !mc.HasLinkedRenderer() : (bool?)null;
                case "WeaponHitTypeIsBeam":
                    if (comp is ModFireWeapon fw && fw.weaponComp != null && fw.weaponComp.WeaponInfo != null)
                    {
                        return fw.weaponComp.WeaponInfo.weaponHitType == WeaponHitType.Beam;
                    }
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Refusal for a property the component's inspector never draws (custom inspector rule, or a type the generic inspector
        /// draws no control for). Null = drawn.
        /// </summary>
        public static string NeverDrawn(ModReflectedType rt, ReflectedProperty p)
        {
            if (rt.hasInspector)
            {
                return InspectorRules.NeverDrawnRefusal(rt.name, rt.uiName, p.name);
            }
            return InspectorRules.GenericNotDrawnRefusal(p.reflectionType.ToString(), ValueConverter.FriendlyTypeName(p.systemType), p.name);
        }

        /// <summary>Rule summary for a property (get_object hint), or null when the property has no rule.</summary>
        public static string Hint(ModReflectedType rt, ReflectedProperty p, out bool neverShown)
        {
            neverShown = false;
            if (!rt.hasInspector)
            {
                if (InspectorRules.GenericNotDrawnRefusal(p.reflectionType.ToString(), "", p.name) != null)
                {
                    neverShown = true;
                    return "the generic inspector draws no control for this type";
                }
                return null;
            }
            PropertyRule r = InspectorRules.Find(rt.name, p.name);
            if (r == null)
            {
                return null;
            }
            neverShown = r.NeverDrawn;
            return InspectorRules.Summary(r);
        }
    }
}
