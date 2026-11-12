using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ModSystem.Logic;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using UnityEngine;

namespace PummelPartyMcp.Game
{
    /// <summary>A validated parameter change; <see cref="Apply"/> writes it into the owner (an action or a trigger).</summary>
    internal sealed class ParamWrite
    {
        public string Name;
        public Action Apply;
    }

    internal sealed class ParamPlan
    {
        public readonly List<ParamWrite> Writes = new List<ParamWrite>();
        public readonly List<string> Clamped = new List<string>();

        /// <summary>Field name -> new value (built objects; for cross-field checks).</summary>
        public readonly Dictionary<string, object> Final = new Dictionary<string, object>(StringComparer.Ordinal);

        public bool IsEmpty => Writes.Count == 0;

        public void ApplyAll()
        {
            foreach (ParamWrite w in Writes)
            {
                w.Apply();
            }
        }
    }

    /// <summary>
    /// Parameters of actions and triggers: their Unity-serialized fields (the model stores them in private fields and the
    /// Action Editor's DrawInspector code writes those fields directly). Validates/converts JSON values against the field
    /// types and the rules in <see cref="ActionParamRules"/>. Main thread only.
    /// </summary>
    internal static class ActionParams
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly HashSet<string> s_actionCore = new HashSet<string> { "m_type", "m_version", "m_targetFlags", "m_targets" };
        private static readonly HashSet<string> s_triggerCore = new HashSet<string> { "m_type", "m_actions" };

        public static FieldInfo[] Fields(Type ownerType)
        {
            HashSet<string> core = typeof(ModLogicTrigger).IsAssignableFrom(ownerType) ? s_triggerCore : s_actionCore;
            return ValueConverter.SerializedFieldsOf(ownerType).Where(f => !core.Contains(f.Name)).ToArray();
        }

        public static bool IsAssetRef(Type t, out Type element)
        {
            element = null;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ModAssetReference<>))
            {
                element = t.GetGenericArguments()[0];
                return true;
            }
            return false;
        }

        public static bool IsComplex(Type t, out bool integer)
        {
            integer = typeof(ModComplexValue<int>).IsAssignableFrom(t);
            return integer || typeof(ModComplexValue<float>).IsAssignableFrom(t);
        }

        // ================================================================== metadata

        /// <summary>Parameter description for list_action_types / list_trigger_types.</summary>
        public static JObject Describe(Type ownerType, FieldInfo f, object fresh, ValueConverter conv)
        {
            string cls = ownerType.Name;
            ParamRule rule = ActionParamRules.Get(cls, f.Name);
            JObject j = new JObject { ["name"] = f.Name };
            string alias = ParamNames.Alias(f.Name);
            if (alias != null)
            {
                j["alias"] = alias;
            }
            j["type"] = ValueConverter.FriendlyTypeName(f.FieldType);
            string kind = Kind(cls, f, out string refType, out ValueTypeDesc desc);
            j["kind"] = kind;
            if (refType != null)
            {
                j["referenceType"] = refType;
            }
            if (desc != null && desc.Kind == ValueKind.Enum)
            {
                JArray values = new JArray();
                for (int i = 0; i < desc.EnumNames.Length; i++)
                {
                    if (ActionParamRules.CheckEnum(rule, desc.EnumValues[i], desc.EnumNames[i]) == null)
                    {
                        values.Add(desc.EnumNames[i]);
                    }
                }
                j["enumValues"] = values;
                if (desc.IsFlags)
                {
                    j["flags"] = true;
                }
            }
            if (fresh != null)
            {
                try
                {
                    j["default"] = conv.Convert(f.GetValue(fresh));
                }
                catch (Exception e)
                {
                    j["default"] = new JObject { ["error"] = e.Message };
                }
            }
            if (rule?.Min != null) j["min"] = rule.Min.Value;
            if (rule?.Max != null) j["max"] = rule.Max.Value;
            string why = rule?.Refusal ?? (kind == "unsupported" ? $"values of type {ValueConverter.FriendlyTypeName(f.FieldType)} cannot be set by the plugin" : null);
            j["writable"] = why == null;
            if (why != null)
            {
                j["reason"] = why;
            }
            if (rule?.Note != null)
            {
                j["notes"] = rule.Note;
            }
            return j;
        }

        private static string Kind(string cls, FieldInfo f, out string refType, out ValueTypeDesc desc)
        {
            refType = null;
            desc = null;
            Type t = f.FieldType;
            ParamRule rule = ActionParamRules.Get(cls, f.Name);
            if (IsComplex(t, out bool integer))
            {
                return integer ? "complexInt" : "complexFloat";
            }
            if (IsAssetRef(t, out Type el))
            {
                refType = el.Name;
                return "reference";
            }
            if (t.IsArray && IsAssetRef(t.GetElementType(), out Type el2))
            {
                refType = el2.Name;
                return "referenceArray";
            }
            if (rule?.Subfields != null)
            {
                return cls == "BoardPopupAction" && f.Name == "m_choices" ? "choices" : "object";
            }
            desc = ValueBuilder.Describe(t);
            switch (desc.Kind)
            {
                case ValueKind.Bool: return "bool";
                case ValueKind.Integer: return "integer";
                case ValueKind.Float:
                case ValueKind.Double: return "number";
                case ValueKind.String: return "string";
                case ValueKind.Enum: return desc.IsFlags ? "flags" : "enum";
                case ValueKind.Vector2: return "vector2";
                case ValueKind.Vector3: return "vector3";
                case ValueKind.Vector4: return "vector4";
                case ValueKind.Color: return "color";
                case ValueKind.Unsupported: return "unsupported";
                default: return desc.Kind.ToString().ToLowerInvariant();
            }
        }

        // ================================================================== prepare

        /// <summary>
        /// Validates and converts every given parameter (all-or-nothing, nothing is written). The returned writes are
        /// applied later to the same owner instance (callers verify it is still in the model).
        /// </summary>
        public static ParamPlan Prepare(object owner, JObject parameters, ReferenceResolver refs, string label)
        {
            ParamPlan plan = new ParamPlan();
            if (parameters == null || parameters.Count == 0)
            {
                return plan;
            }
            Type type = owner.GetType();
            string cls = type.Name;
            FieldInfo[] fields = Fields(type);
            List<string> names = fields.Select(f => f.Name).ToList();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            // Classes that upgrade their data when the editor draws them: run the same upgrade first.
            if (owner is GiveItemAction || owner is SetPlayerVisualAction)
            {
                MethodInfo upgrade = type.GetMethod("UpdateVersion", Inst);
                if (upgrade != null)
                {
                    plan.Writes.Add(new ParamWrite { Name = "(version upgrade)", Apply = () => upgrade.Invoke(owner, null) });
                }
            }

            List<KeyValuePair<FieldInfo, JToken>> ordered = new List<KeyValuePair<FieldInfo, JToken>>();
            foreach (JProperty p in parameters.Properties())
            {
                string name = ParamNames.Resolve(p.Name, names);
                if (name == null)
                {
                    throw RpcException.InvalidParams(names.Count == 0
                        ? $"{label} ({cls}) has no parameters"
                        : $"{label} ({cls}) has no parameter '{p.Name}' (parameters: {string.Join(", ", names.ToArray())})");
                }
                if (!seen.Add(name))
                {
                    throw RpcException.InvalidParams($"parameter '{name}' is given twice");
                }
                ordered.Add(new KeyValuePair<FieldInfo, JToken>(fields.First(f => f.Name == name), p.Value));
            }
            // m_choiceCount before m_choices (the patch may address choices created by a raised count).
            ordered.Sort((a, b) => Rank(a.Key.Name).CompareTo(Rank(b.Key.Name)));

            foreach (KeyValuePair<FieldInfo, JToken> kv in ordered)
            {
                PrepareField(owner, cls, kv.Key, kv.Value, refs, plan);
            }
            CrossChecks(owner, plan);
            return plan;
        }

        private static int Rank(string field) => field == "m_choiceCount" ? 0 : field == "m_choices" ? 2 : 1;

        private static void PrepareField(object owner, string cls, FieldInfo f, JToken value, ReferenceResolver refs, ParamPlan plan)
        {
            string path = f.Name;
            ParamRule rule = ActionParamRules.Get(cls, f.Name);
            if (rule?.Refusal != null)
            {
                throw RpcException.InvalidParams($"'{path}': {rule.Refusal}");
            }
            Type t = f.FieldType;

            // ---- ModComplexFloat / ModComplexInt
            if (IsComplex(t, out bool integer))
            {
                PComplex patch = ComplexValues.Parse(value, path, integer);
                object cur = f.GetValue(owner);
                ComplexValue current = ReadComplex(cur ?? Activator.CreateInstance(t), integer);
                ComplexValue merged = ComplexValues.Merge(current, patch, rule?.Complex ?? ComplexClamp.None, path, plan.Clamped);
                plan.Final[f.Name] = merged;
                plan.Writes.Add(new ParamWrite
                {
                    Name = f.Name,
                    Apply = () =>
                    {
                        object inst = f.GetValue(owner);
                        if (inst == null)
                        {
                            inst = Activator.CreateInstance(t);
                            f.SetValue(owner, inst);
                        }
                        WriteComplex(inst, merged, integer);
                    },
                });
                return;
            }

            // ---- ModAssetReference<T>
            if (IsAssetRef(t, out Type element))
            {
                PRef r = (PRef)ValueParser.Parse(value, ValueTypeDesc.Reference(element.Name), path);
                object target = ResolveRegistered(r.Guid, element, refs, path);
                if (cls == "GiveMinigameItemAction" && f.Name == "m_item" && target is PummelPrefab pp)
                {
                    GameObject po = pp.PrefabObject;
                    if (po == null || !po.TryGetComponent<ModItem>(out _))
                    {
                        throw RpcException.InvalidParams($"'{path}': could not set prefab {pp.name}. The prefab must contain an item component on its root gameobject.");
                    }
                }
                plan.Final[f.Name] = target;
                PropertyInfo wrapper = Wrapper(owner.GetType(), rule);
                plan.Writes.Add(new ParamWrite
                {
                    Name = f.Name,
                    Apply = () =>
                    {
                        if (wrapper != null)
                        {
                            Invoke(wrapper, owner, target, path);
                            return;
                        }
                        object reference = f.GetValue(owner);
                        if (reference == null)
                        {
                            f.SetValue(owner, Activator.CreateInstance(t, target));
                        }
                        else
                        {
                            t.GetProperty("Asset").SetValue(reference, target, null); // what the inspectors do (m_clip.Asset = ...)
                        }
                    },
                });
                return;
            }

            // ---- ModAssetReference<T>[]
            if (t.IsArray && IsAssetRef(t.GetElementType(), out Type el2))
            {
                if (!(value is JArray arr))
                {
                    throw RpcException.InvalidParams($"'{path}': expected an array of references (guid strings, {{\"guid\": ...}} or null)");
                }
                if (arr.Count > ValueParser.MaxArrayLength)
                {
                    throw RpcException.InvalidParams($"'{path}': more than {ValueParser.MaxArrayLength} elements");
                }
                Array built = Array.CreateInstance(el2, arr.Count);
                for (int i = 0; i < arr.Count; i++)
                {
                    PRef r = (PRef)ValueParser.Parse(arr[i], ValueTypeDesc.Reference(el2.Name), $"{path}[{i}]");
                    built.SetValue(ResolveRegistered(r.Guid, el2, refs, $"{path}[{i}]"), i);
                }
                plan.Final[f.Name] = built;
                PropertyInfo wrapper = Wrapper(owner.GetType(), rule);
                plan.Writes.Add(new ParamWrite
                {
                    Name = f.Name,
                    Apply = () =>
                    {
                        if (wrapper != null)
                        {
                            Invoke(wrapper, owner, built, path);
                            return;
                        }
                        Type refType = t.GetElementType();
                        Array refs2 = Array.CreateInstance(refType, built.Length);
                        for (int i = 0; i < built.Length; i++)
                        {
                            refs2.SetValue(Activator.CreateInstance(refType, built.GetValue(i)), i);
                        }
                        f.SetValue(owner, refs2);
                    },
                });
                return;
            }

            // ---- Display Popup choices (array patch)
            if (cls == "BoardPopupAction" && f.Name == "m_choices")
            {
                PrepareChoices((BoardPopupAction)owner, value, refs, plan, rule);
                return;
            }

            // ---- struct/class with drawn sub-fields (DamageInstance)
            if (rule?.Subfields != null)
            {
                if (!(value is JObject obj))
                {
                    throw RpcException.InvalidParams($"'{path}': expected an object with any of {string.Join(", ", rule.Subfields)}");
                }
                List<KeyValuePair<FieldInfo, object>> subs = new List<KeyValuePair<FieldInfo, object>>();
                foreach (JProperty p in obj.Properties())
                {
                    if (Array.IndexOf(rule.Subfields, p.Name) < 0)
                    {
                        throw RpcException.InvalidParams($"'{path}.{p.Name}': not editable in the Action Editor (editable: {string.Join(", ", rule.Subfields)})");
                    }
                    FieldInfo sf = t.GetField(p.Name, Inst) ?? throw RpcException.Internal(GameReflection.MissingMessage(t, p.Name));
                    ParamRule subRule = ActionParamRules.Get(cls, f.Name + "." + p.Name);
                    subs.Add(new KeyValuePair<FieldInfo, object>(sf, BuildPlain(sf.FieldType, p.Value, subRule, $"{path}.{p.Name}", refs, plan)));
                }
                plan.Final[f.Name] = subs;
                plan.Writes.Add(new ParamWrite
                {
                    Name = f.Name,
                    Apply = () =>
                    {
                        object boxed = f.GetValue(owner) ?? Activator.CreateInstance(t);
                        foreach (KeyValuePair<FieldInfo, object> s in subs)
                        {
                            s.Key.SetValue(boxed, s.Value);
                        }
                        f.SetValue(owner, boxed);
                    },
                });
                return;
            }

            // ---- plain values (numbers, bools, strings, enums, vectors, colors)
            object v = BuildPlain(t, value, rule, path, refs, plan);
            plan.Final[f.Name] = v;
            if (cls == "BoardPopupAction" && f.Name == "m_choiceCount")
            {
                BoardPopupAction popup = (BoardPopupAction)owner;
                plan.Writes.Add(new ParamWrite
                {
                    Name = f.Name,
                    Apply = () =>
                    {
                        f.SetValue(popup, v);
                        GrowChoices(popup, (int)v); // DrawInspector adds new choices when the count exceeds the list
                    },
                });
                return;
            }
            plan.Writes.Add(new ParamWrite { Name = f.Name, Apply = () => f.SetValue(owner, v) });
        }

        /// <summary>Plain value through ValueParser/ValueBuilder with the rule's clamp and enum exclusions.</summary>
        private static object BuildPlain(Type t, JToken value, ParamRule rule, string path, ReferenceResolver refs, ParamPlan plan)
        {
            ValueTypeDesc desc = ValueBuilder.Describe(t);
            if (desc.Kind == ValueKind.Reference || (desc.Kind == ValueKind.Array && desc.Element?.Kind == ValueKind.Reference))
            {
                throw RpcException.InvalidParams($"'{path}': values of type {desc.TypeName} cannot be set by the plugin");
            }
            PValue parsed = ValueParser.Parse(value, desc, path);
            if (parsed is PEnum pe)
            {
                int idx = Array.IndexOf(desc.EnumValues, pe.Value);
                string why = ActionParamRules.CheckEnum(rule, pe.Value, idx >= 0 ? desc.EnumNames[idx] : pe.Value.ToString(CultureInfo.InvariantCulture));
                if (why != null)
                {
                    throw RpcException.InvalidParams($"'{path}': {why}");
                }
            }
            if (ActionParamRules.ApplyClamp(rule, parsed))
            {
                plan.Clamped.Add(path);
            }
            return ValueBuilder.Build(parsed, t, refs, path);
        }

        /// <summary>Resolves a reference like the editor's object picker and requires a registered mod asset (else the guid would be empty on save).</summary>
        public static object ResolveRegistered(string guid, Type expected, ReferenceResolver refs, string path)
        {
            if (guid == null)
            {
                return null;
            }
            object value = refs.Resolve(guid, expected, path);
            if (value != null)
            {
                WorkshopController wc = WorkshopController.Instance;
                if (wc == null || wc.GetAssetWithObject(value) == null)
                {
                    throw RpcException.InvalidParams($"'{path}': the resolved {value.GetType().Name} is not registered as a mod asset; the reference would be saved as empty");
                }
            }
            return value;
        }

        private static PropertyInfo Wrapper(Type ownerType, ParamRule rule)
        {
            if (rule?.WrapperProperty == null)
            {
                return null;
            }
            PropertyInfo pi = ownerType.GetProperty(rule.WrapperProperty, BindingFlags.Instance | BindingFlags.Public);
            if (pi == null || pi.GetSetMethod() == null)
            {
                throw RpcException.Internal(GameReflection.MissingMessage(ownerType, rule.WrapperProperty));
            }
            return pi;
        }

        private static void Invoke(PropertyInfo pi, object owner, object value, string path)
        {
            try
            {
                pi.SetValue(owner, value, null);
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException ?? e;
                throw RpcException.Internal($"setting '{path}' ({pi.Name}) threw {inner.GetType().Name}: {inner.Message}");
            }
        }

        // ================================================================== complex values

        public static ComplexValue ReadComplex(object inst, bool integer)
        {
            Type t = inst.GetType();
            object vt = ComplexField(t, "valueType").GetValue(inst);
            object min = ComplexField(t, "min").GetValue(inst);
            object max = ComplexField(t, "max").GetValue(inst);
            return new ComplexValue
            {
                ValueType = vt.ToString(),
                Min = Convert.ToDouble(min, CultureInfo.InvariantCulture),
                Max = Convert.ToDouble(max, CultureInfo.InvariantCulture),
            };
        }

        private static void WriteComplex(object inst, ComplexValue v, bool integer)
        {
            Type t = inst.GetType();
            FieldInfo valueType = ComplexField(t, "valueType");
            FieldInfo min = ComplexField(t, "min");
            FieldInfo max = ComplexField(t, "max");
            valueType.SetValue(inst, Enum.Parse(typeof(ComplexValueType), v.ValueType));
            if (integer)
            {
                min.SetValue(inst, (int)v.Min);
                max.SetValue(inst, (int)v.Max);
            }
            else
            {
                min.SetValue(inst, (float)v.Min);
                max.SetValue(inst, (float)v.Max);
            }
        }

        /// <summary>Public instance field of a complex-value type; a missing member (game update) names itself instead of a bare NullReferenceException.</summary>
        private static FieldInfo ComplexField(Type t, string name) =>
            t.GetField(name) ?? throw RpcException.Internal(GameReflection.MissingMessage(t, name));

        // ================================================================== popup choices

        private static void GrowChoices(BoardPopupAction popup, int count)
        {
            List<InteractionPopupChoice> choices = ActionModel.PopupChoices(popup);
            while (choices.Count < count)
            {
                choices.Add(new InteractionPopupChoice());
            }
        }

        private static void PrepareChoices(BoardPopupAction popup, JToken value, ReferenceResolver refs, ParamPlan plan, ParamRule rule)
        {
            const string path = "m_choices";
            if (!(value is JArray arr))
            {
                throw RpcException.InvalidParams($"'{path}': expected an array of choice patches ({string.Join(", ", rule.Subfields)}); element i updates choice i, null leaves it unchanged");
            }
            int count = plan.Final.TryGetValue("m_choiceCount", out object c) ? (int)c : ActionModel.PopupChoiceCount(popup);
            if (arr.Count > count)
            {
                throw RpcException.InvalidParams($"'{path}': {arr.Count} elements, but the popup shows {count} choice{(count == 1 ? "" : "s")} (set m_choiceCount first, 0..4)");
            }
            List<InteractionPopupChoice> existing = ActionModel.PopupChoices(popup);
            List<Action<InteractionPopupChoice>> perChoice = new List<Action<InteractionPopupChoice>>();
            for (int i = 0; i < arr.Count; i++)
            {
                string ep = $"{path}[{i}]";
                if (arr[i].Type == JTokenType.Null)
                {
                    perChoice.Add(null);
                    continue;
                }
                if (!(arr[i] is JObject o))
                {
                    throw RpcException.InvalidParams($"'{ep}': expected an object or null");
                }
                InteractionPopupChoice cur = i < existing.Count ? existing[i] : null;
                string costType = cur?.CostType.ToString() ?? "None";
                List<Action<InteractionPopupChoice>> sets = new List<Action<InteractionPopupChoice>>();
                decimal? amount = null;
                foreach (JProperty p in o.Properties())
                {
                    string sp = $"{ep}.{p.Name}";
                    switch (p.Name)
                    {
                        case "m_buttonText":
                        case "buttonText":
                        {
                            string s = (string)BuildPlain(typeof(string), p.Value, null, sp, refs, plan);
                            sets.Add(ch => ch.ChoiceLabel = s);
                            break;
                        }
                        case "m_costType":
                        case "costType":
                        {
                            InteractionCostType ct = (InteractionCostType)BuildPlain(typeof(InteractionCostType), p.Value, null, sp, refs, plan);
                            costType = ct.ToString();
                            sets.Add(ch => ch.CostType = ct);
                            break;
                        }
                        case "m_costAmount":
                        case "costAmount":
                        {
                            PValue pv = ValueParser.Parse(p.Value, ValueBuilder.Describe(typeof(int)), sp);
                            amount = ((PInteger)pv).Value;
                            break;
                        }
                        case "m_itemCostType":
                        case "itemCostType":
                        {
                            Items it = (Items)BuildPlain(typeof(Items), p.Value, null, sp, refs, plan);
                            sets.Add(ch => ch.ItemCostType = it);
                            break;
                        }
                        case "m_choiceIconReference":
                        case "choiceIconReference":
                        {
                            PRef r = (PRef)ValueParser.Parse(p.Value, ValueTypeDesc.Reference("Texture2D"), sp);
                            Texture2D tex = (Texture2D)ResolveRegistered(r.Guid, typeof(Texture2D), refs, sp);
                            sets.Add(ch => ch.ChoiceIcon = tex);
                            break;
                        }
                        default:
                            throw RpcException.InvalidParams($"'{sp}': not editable through m_choices (editable: {string.Join(", ", rule.Subfields)}; edit the choice's actions with path [{{action, choice: {i}}}])");
                    }
                }
                // DrawButtonDataUI clamps the cost amount (by cost type) on every drawn frame while the choice has a cost.
                decimal basis = amount ?? (cur?.CostAmount ?? 0);
                if (amount.HasValue || costType != (cur?.CostType.ToString() ?? "None"))
                {
                    decimal clampedAmount = ActionParamRules.ClampChoiceCost(costType, basis, out bool wasClamped);
                    if (wasClamped)
                    {
                        plan.Clamped.Add($"{ep}.m_costAmount");
                    }
                    int finalAmount = (int)clampedAmount;
                    sets.Add(ch => ch.CostAmount = finalAmount);
                }
                perChoice.Add(ch =>
                {
                    foreach (Action<InteractionPopupChoice> s in sets)
                    {
                        s(ch);
                    }
                });
            }
            plan.Final["m_choices"] = arr.Count;
            plan.Writes.Add(new ParamWrite
            {
                Name = path,
                Apply = () =>
                {
                    GrowChoices(popup, arr.Count);
                    List<InteractionPopupChoice> list = ActionModel.PopupChoices(popup);
                    for (int i = 0; i < perChoice.Count; i++)
                    {
                        perChoice[i]?.Invoke(list[i]);
                    }
                },
            });
        }

        // ================================================================== cross-field checks

        private static void CrossChecks(object owner, ParamPlan plan)
        {
            if (owner is SetPlayerVisualAction spv)
            {
                PummelPrefab prefab = plan.Final.TryGetValue("m_prefab", out object p) ? p as PummelPrefab : spv.Prefab;
                GameObject po = prefab != null ? prefab.PrefabObject : null;
                bool disabled = po == null;
                if (disabled && plan.Final.ContainsKey("m_recolorPrefabUsingPlayerColor"))
                {
                    throw RpcException.InvalidParams("'m_recolorPrefabUsingPlayerColor': editable only while a prefab is set (m_prefab)");
                }
                int children = po != null ? po.transform.childCount : 0;
                int vertical = plan.Final.TryGetValue("m_verticalLookTargetChildIndex", out object v) ? (int)v : (int)GameReflection.GetField(spv, "m_verticalLookTargetChildIndex");
                int movement = plan.Final.TryGetValue("m_movementRotationTargetChildIndex", out object m) ? (int)m : (int)GameReflection.GetField(spv, "m_movementRotationTargetChildIndex");
                foreach (string key in new[] { "m_verticalLookTargetChildIndex", "m_movementRotationTargetChildIndex" })
                {
                    if (!plan.Final.TryGetValue(key, out object val))
                    {
                        continue;
                    }
                    int i = (int)val;
                    if (disabled && i != -1)
                    {
                        throw RpcException.InvalidParams($"'{key}': editable only while a prefab is set (m_prefab); only -1 (None) is allowed");
                    }
                    if (i < -1 || i >= children)
                    {
                        throw RpcException.InvalidParams($"'{key}': must be -1 (None) or a child index 0..{children - 1} of the prefab's root object");
                    }
                }
                if (vertical == movement && vertical != -1 && (plan.Final.ContainsKey("m_verticalLookTargetChildIndex") || plan.Final.ContainsKey("m_movementRotationTargetChildIndex")))
                {
                    throw RpcException.InvalidParams("'m_verticalLookTargetChildIndex' and 'm_movementRotationTargetChildIndex' cannot be the same child (the editor resets the other to None)");
                }
            }
            if (owner is BoardPopupAction popup && plan.Final.ContainsKey("m_showCloseButton"))
            {
                MethodInfo costless = typeof(BoardPopupAction).GetMethod("HasACostlessChoice", Inst);
                if (costless != null && !(bool)costless.Invoke(popup, null))
                {
                    throw RpcException.InvalidParams("'m_showCloseButton': the editor forces the close button on (checkbox disabled) while every shown choice has a cost");
                }
            }
        }
    }
}
