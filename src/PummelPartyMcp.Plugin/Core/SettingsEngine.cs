using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    /// <summary>Normalised asset reference value (null Guid = none). Info is the game layer's description (name, assetType, location).</summary>
    public sealed class AssetValue
    {
        public string Guid;
        public JObject Info;
    }

    /// <summary>Normalised screen-effect parameter: value (null = not given) and override flag (null = not given).</summary>
    public sealed class ParamValue
    {
        public object Value;
        public bool? Override;
    }

    /// <summary>
    /// What the engine needs from the game: the current normalised values (bool, double, string, string[] flags, double[] vector/
    /// rect/color, <see cref="AssetValue"/>, <see cref="ParamValue"/>; null = cannot be read, e.g. the effect is absent) and a few
    /// answers only the game knows. Faked in tests.
    /// </summary>
    public interface ISettingsState
    {
        object Current(ModSettingField field);

        bool? Derived(string name);

        /// <summary>Preset names the editor offers for a Preset field (ending with "Custom"); null when they cannot be read.</summary>
        IList<string> PresetNames(ModSettingField field);

        /// <summary>Null when the guid is an asset the picker would accept for the field; otherwise why not.</summary>
        string CheckAsset(ModSettingField field, string guid);

        /// <summary>Run-time limits of a slider parameter (the game's RangeAttribute); false = use the table's documented range.</summary>
        bool TryGetLimits(ModSettingField field, out double min, out double max);
    }

    public sealed class SettingsAssignment
    {
        public ModSettingField Field;
        public object Value;
        public bool Implied;
        public string Why;
    }

    public sealed class SettingsClamp
    {
        public string Field;
        public JToken Requested;
        public JToken Applied;
        public double? Min;
        public double? Max;
        public string Reason;
    }

    public sealed class SettingsPlan
    {
        public SettingsSection Section;
        public List<SettingsAssignment> Assignments = new List<SettingsAssignment>();
        public List<SettingsClamp> Clamped = new List<SettingsClamp>();

        public IEnumerable<SettingsAssignment> Explicit => Assignments.Where(a => !a.Implied);
        public IEnumerable<SettingsAssignment> Implied => Assignments.Where(a => a.Implied);
    }

    /// <summary>
    /// Validation and normalisation of set_mod_settings (all before anything changes), and the JSON shapes of list_mod_settings /
    /// get_mod_settings. Pure; the game is reached only through <see cref="ISettingsState"/>.
    /// </summary>
    public static class SettingsEngine
    {
        public const int MaxFields = 128;

        // ------------------------------------------------------------------ context

        private sealed class PlanContext : IRuleContext
        {
            private readonly SettingsSection m_section;
            private readonly ISettingsState m_state;
            private readonly Dictionary<ModSettingField, object> m_assigned;

            public PlanContext(SettingsSection section, ISettingsState state, Dictionary<ModSettingField, object> assigned)
            {
                m_section = section;
                m_state = state;
                m_assigned = assigned;
            }

            public object Get(string property)
            {
                ModSettingField f = m_section.Find(property);
                if (f == null)
                {
                    return InspectorRules.Unknown;
                }
                object v;
                if (m_assigned.TryGetValue(f, out v))
                {
                    if (v is ParamValue assigned && assigned.Value == null)
                    {
                        v = m_state.Current(f); // {override} only: the value stays the current one
                    }
                }
                else
                {
                    v = m_state.Current(f);
                }
                if (v is ParamValue pv)
                {
                    v = pv.Value;
                }
                return v ?? InspectorRules.Unknown;
            }

            public bool? Derived(string name) => m_state.Derived(name);
        }

        // ------------------------------------------------------------------ plan

        public static SettingsPlan Plan(SettingsSection section, JObject values, ISettingsState state)
        {
            if (section == null)
            {
                throw new ArgumentNullException(nameof(section));
            }
            if (values == null || values.Count == 0)
            {
                throw RpcException.InvalidParams("'values' must contain at least one field of section '" + section.Name + "'");
            }
            if (values.Count > MaxFields)
            {
                throw RpcException.InvalidParams($"'values' has more than {MaxFields} fields");
            }

            Dictionary<ModSettingField, object> assigned = new Dictionary<ModSettingField, object>();
            Dictionary<ModSettingField, JToken> requested = new Dictionary<ModSettingField, JToken>();
            foreach (JProperty p in values.Properties())
            {
                ModSettingField f = section.Find(p.Name);
                if (f == null)
                {
                    throw RpcException.InvalidParams(UnknownFieldMessage(section, p.Name));
                }
                if (assigned.ContainsKey(f))
                {
                    throw RpcException.InvalidParams($"field '{f.Name}' is given twice (names are matched case-insensitively)");
                }
                if (!f.Writable)
                {
                    throw RpcException.InvalidParams($"'{f.Name}' of section '{section.Name}' is read-only: {f.ReadOnlyReason}; nothing was changed");
                }
                assigned[f] = ParseValue(f, p.Value, "values." + f.Name, state);
                requested[f] = p.Value.DeepClone();
            }

            PlanContext ctx = new PlanContext(section, state, assigned);
            SettingsPlan plan = new SettingsPlan { Section = section };

            // limits the UI applies (in table order, so the report is stable)
            foreach (ModSettingField f in section.Fields.Where(assigned.ContainsKey).ToList())
            {
                object clamped = ApplyLimits(f, assigned[f], requested[f], ctx, state, plan.Clamped);
                assigned[f] = clamped;
            }

            // conditional fields: judged after applying all values of the call
            foreach (ModSettingField f in section.Fields.Where(assigned.ContainsKey))
            {
                if (f.Shown != null && f.Shown.Evaluate(ctx) == false)
                {
                    throw RpcException.InvalidParams(
                        $"'{f.Name}' is only shown by the editor while {f.Shown.Describe()}{InspectorRules.DescribeState(f.Shown, ctx)}; " +
                        "set the controlling field first (or in the same call); nothing was changed");
                }
            }

            // value combinations the game cannot handle
            foreach (SettingGuard g in section.Guards)
            {
                ModSettingField f = section.Find(g.Field);
                if (f != null && assigned.TryGetValue(f, out object v) && v is string name && string.Equals(name, g.Value, StringComparison.OrdinalIgnoreCase))
                {
                    if (g.Require.Evaluate(ctx) != true)
                    {
                        throw RpcException.InvalidParams($"'{f.Name}' = {g.Value}: {g.Message}; nothing was changed");
                    }
                }
            }

            foreach (ModSettingField f in section.Fields.Where(assigned.ContainsKey))
            {
                plan.Assignments.Add(new SettingsAssignment { Field = f, Value = assigned[f] });
            }

            // what the UI does as a side effect
            foreach (ImpliedWrite im in section.Implied)
            {
                ModSettingField trig = section.Find(im.TriggerField);
                ModSettingField target = section.Find(im.Field);
                if (trig == null || target == null || !assigned.TryGetValue(trig, out object tv))
                {
                    continue;
                }
                string tname = tv as string;
                if (im.TriggerValues != null && (tname == null || Array.IndexOf(im.TriggerValues, tname) < 0))
                {
                    continue;
                }
                if (im.OnChangeOnly && ValuesEqual(trig, tv, state.Current(trig)))
                {
                    continue;
                }
                if (im.ExplicitWins && assigned.ContainsKey(target))
                {
                    continue;
                }
                object value = im.ValueByTrigger != null && tname != null && im.ValueByTrigger.TryGetValue(tname, out object byTrig) ? byTrig : im.Value;
                if (value == null)
                {
                    continue;
                }
                object coerced = target.Kind == SettingKind.Float || target.Kind == SettingKind.Int ? (object)Convert.ToDouble(value, CultureInfo.InvariantCulture) : value;
                if (!assigned.ContainsKey(target) && ValuesEqual(target, coerced, state.Current(target)))
                {
                    continue;
                }
                plan.Assignments.RemoveAll(a => a.Field == target);
                plan.Assignments.Add(new SettingsAssignment { Field = target, Value = coerced, Implied = true, Why = im.Why });
                assigned[target] = coerced;
            }
            plan.Assignments.Sort((a, b) => section.Fields.IndexOf(a.Field).CompareTo(section.Fields.IndexOf(b.Field)));
            return plan;
        }

        private static string UnknownFieldMessage(SettingsSection section, string name)
        {
            if (section.HiddenNames.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase)))
            {
                return $"'{name}' has no control in the editor's {section.Title} window; the plugin only writes what the editor's UI offers (not shown: {string.Join(", ", section.HiddenNames)})";
            }
            string names = string.Join(", ", section.Fields.Where(f => f.Writable).Select(f => f.Name).Take(60).ToArray());
            return $"unknown field '{name}' in section '{section.Name}'; writable fields: {names}{(section.Fields.Count(f => f.Writable) > 60 ? ", ..." : "")} (list_mod_settings shows them all)";
        }

        // ------------------------------------------------------------------ parsing

        private static ValueTypeDesc DescOf(ModSettingField f)
        {
            switch (f.Kind)
            {
                case SettingKind.Bool: return ValueTypeDesc.Simple(ValueKind.Bool, "bool");
                case SettingKind.Int: return ValueTypeDesc.Integer("int", int.MinValue, int.MaxValue);
                case SettingKind.Float: return ValueTypeDesc.Simple(ValueKind.Float, "float");
                case SettingKind.String: return ValueTypeDesc.Simple(ValueKind.String, "string");
                case SettingKind.Enum:
                case SettingKind.NamedInt: return ValueTypeDesc.Enum(f.Name, f.EnumNames, f.EnumValues, false);
                case SettingKind.Flags: return ValueTypeDesc.Enum(f.Name, f.EnumNames, f.EnumValues, true);
                case SettingKind.Vector2: return ValueTypeDesc.Simple(ValueKind.Vector2, "vector2");
                case SettingKind.Vector3: return ValueTypeDesc.Simple(ValueKind.Vector3, "vector3");
                case SettingKind.Rect: return ValueTypeDesc.Simple(ValueKind.Rect, "rect");
                case SettingKind.Color: return ValueTypeDesc.Simple(ValueKind.Color, "color");
                case SettingKind.Asset: return ValueTypeDesc.Reference(f.AssetTypes != null && f.AssetTypes.Length > 0 ? f.AssetTypes[0] : "asset");
                default: return null;
            }
        }

        public static object ParseValue(ModSettingField f, JToken token, string path, ISettingsState state)
        {
            if (f.IsParam)
            {
                return ParseParam(f, token, path, state);
            }
            return ParseBase(f, token, path, state);
        }

        private static object ParseParam(ModSettingField f, JToken token, string path, ISettingsState state)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                throw RpcException.InvalidParams($"'{path}': a parameter needs a value or {{override: false}} (null is not accepted)");
            }
            if (token is JObject obj && (obj.ContainsKey("value") || obj.ContainsKey("override")))
            {
                foreach (JProperty p in obj.Properties())
                {
                    if (p.Name != "value" && p.Name != "override")
                    {
                        throw RpcException.InvalidParams($"'{path}': unexpected key '{p.Name}'; a parameter is {{value?, override?}}");
                    }
                }
                ParamValue pv = new ParamValue();
                JToken o = obj["override"];
                if (o != null)
                {
                    if (o.Type != JTokenType.Boolean)
                    {
                        throw RpcException.InvalidParams($"'{path}.override': expected a boolean");
                    }
                    pv.Override = (bool)o;
                }
                JToken v = obj["value"];
                if (v != null)
                {
                    pv.Value = ParseBase(f, v, path + ".value", state);
                }
                if (pv.Value == null && pv.Override == null)
                {
                    throw RpcException.InvalidParams($"'{path}': give value and/or override");
                }
                if (pv.Value != null && pv.Override == null)
                {
                    pv.Override = true;
                }
                return pv;
            }
            return new ParamValue { Value = ParseBase(f, token, path, state), Override = true };
        }

        private static object ParseBase(ModSettingField f, JToken token, string path, ISettingsState state)
        {
            token = token ?? JValue.CreateNull();
            switch (f.Kind)
            {
                case SettingKind.Bool:
                    return ((PBool)ValueParser.Parse(token, DescOf(f), path)).Value;

                case SettingKind.Int:
                    return (double)((PInteger)ValueParser.Parse(token, DescOf(f), path)).Value;

                case SettingKind.Float:
                    return (double)(float)((PNumber)ValueParser.Parse(token, DescOf(f), path)).Value;

                case SettingKind.String:
                    return ((PString)ValueParser.Parse(token, DescOf(f), path)).Value;

                case SettingKind.Enum:
                case SettingKind.NamedInt:
                {
                    long v = ((PEnum)ValueParser.Parse(token, DescOf(f), path)).Value;
                    string name = f.EnumNames[Array.IndexOf(f.EnumValues, v)];
                    string[] offered = f.OfferedNames;
                    if (offered != null && Array.IndexOf(offered, name) < 0)
                    {
                        throw RpcException.InvalidParams($"'{path}': the editor does not offer {name} for {f.Name} (offered: {string.Join(", ", offered)})");
                    }
                    return name;
                }

                case SettingKind.Flags:
                {
                    if (token.Type == JTokenType.Array && token.All(t => t.Type == JTokenType.String))
                    {
                        token = new JValue(string.Join(",", token.Select(t => (string)t).ToArray()));
                    }
                    if (token.Type == JTokenType.String && (((string)token).Trim().Length == 0 || string.Equals(((string)token).Trim(), "None", StringComparison.OrdinalIgnoreCase)))
                    {
                        token = new JValue(0);
                    }
                    long bits = ((PEnum)ValueParser.Parse(token, DescOf(f), path)).Value;
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
                case SettingKind.Vector3:
                case SettingKind.Rect:
                case SettingKind.Color:
                    return ((PFloats)ValueParser.Parse(token, DescOf(f), path)).Values.Select(x => (double)x).ToArray();

                case SettingKind.Asset:
                {
                    PRef r = (PRef)ValueParser.Parse(token, DescOf(f), path);
                    if (r.Guid != null)
                    {
                        string why = state.CheckAsset(f, r.Guid);
                        if (why != null)
                        {
                            throw RpcException.InvalidParams($"'{path}': {why}");
                        }
                    }
                    return new AssetValue { Guid = r.Guid };
                }

                case SettingKind.Preset:
                {
                    if (token.Type != JTokenType.String)
                    {
                        throw RpcException.InvalidParams($"'{path}': expected a preset name (string)");
                    }
                    string text = ((string)token).Trim();
                    IList<string> names = state.PresetNames(f);
                    if (names == null || names.Count == 0)
                    {
                        throw RpcException.Internal("the editor's preset list could not be read; nothing was changed");
                    }
                    string hit = names.FirstOrDefault(n => string.Equals(n, text, StringComparison.Ordinal)) ?? names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                    if (hit == null)
                    {
                        throw RpcException.InvalidParams($"'{path}': '{Shorten(text)}' is not a preset of the editor; choose one of: {string.Join(", ", names.ToArray())}");
                    }
                    return hit;
                }

                default:
                    throw RpcException.InvalidParams($"'{path}': values of this type cannot be written");
            }
        }

        private static string Shorten(string s) => s.Length > 60 ? s.Substring(0, 60) + "..." : s;

        // ------------------------------------------------------------------ limits

        private static double Round(double d) => (double)(float)d;

        private static object ApplyLimits(ModSettingField f, object value, JToken requested, PlanContext ctx, ISettingsState state, List<SettingsClamp> report)
        {
            ParamValue pv = value as ParamValue;
            object inner = pv != null ? pv.Value : value;
            if (inner == null)
            {
                return value;
            }
            object result = inner;
            if (inner is double d && (f.Kind == SettingKind.Float || f.Kind == SettingKind.Int))
            {
                double? min = f.Min;
                double? max = f.Max;
                if (f.RangeFromAttribute && state.TryGetLimits(f, out double rmin, out double rmax))
                {
                    min = rmin;
                    max = rmax;
                }
                double c = d;
                if (min.HasValue && c < min.Value)
                {
                    c = min.Value;
                }
                if (max.HasValue && c > max.Value)
                {
                    c = max.Value;
                }
                c = Round(c);
                if (Math.Abs(c - d) > 1e-9)
                {
                    report.Add(new SettingsClamp { Field = f.Name, Requested = Num(f, d), Applied = Num(f, c), Min = min, Max = max, Reason = "the editor limits this value to this range" });
                }
                result = c;
            }
            else if (inner is double[] r && f.Kind == SettingKind.Rect && f.Special != SettingSpecial.None)
            {
                bool circle = f.Special == SettingSpecial.RectCenteredCircleAware && ctx.Get("topDownCameraFollowAllType") is string shape && shape == "Circle";
                double[] fixedRect = CenterClamp(r, circle);
                if (!fixedRect.SequenceEqual(r.Select(Round)))
                {
                    report.Add(new SettingsClamp
                    {
                        Field = f.Name, Requested = ToJson(f, r), Applied = ToJson(f, fixedRect), Min = 0, Max = 2000,
                        Reason = circle ? "size 0..2000, centre within +-(1000 - half size), height = width for the Circle shape" : "size 0..2000, centre within +-(1000 - half size)",
                    });
                }
                result = fixedRect;
            }
            if (pv != null)
            {
                return new ParamValue { Value = result, Override = pv.Override };
            }
            return result;
        }

        private static JToken Num(ModSettingField f, double d) => f.Kind == SettingKind.Int ? new JValue((long)Math.Round(d)) : new JValue(Clean(d));

        /// <summary>The editor's centre + size rebuild (Mathf.Clamp as in its draw code).</summary>
        public static double[] CenterClamp(double[] rect, bool circle)
        {
            double wIn = rect[2];
            double hIn = rect[3];
            double cx = rect[0] + 0.5 * wIn;
            double cy = rect[1] + 0.5 * hIn;
            double w = Math.Min(2000, Math.Max(0, wIn));
            double h = circle ? w : Math.Min(2000, Math.Max(0, hIn));
            cx = Math.Min(1000 - w / 2, Math.Max(-1000 + w / 2, cx));
            cy = Math.Min(1000 - h / 2, Math.Max(-1000 + h / 2, cy));
            return new[] { Round(cx - 0.5 * w), Round(cy - 0.5 * h), Round(w), Round(h) };
        }

        // ------------------------------------------------------------------ equality and JSON

        public static bool ValuesEqual(ModSettingField f, object a, object b)
        {
            if (a == null || b == null)
            {
                return a == null && b == null;
            }
            if (a is ParamValue pa && b is ParamValue pb)
            {
                return ValuesEqual(f, pa.Value, pb.Value) && pa.Override == pb.Override;
            }
            if (a is string[] sa && b is string[] sb)
            {
                return sa.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(sb.OrderBy(x => x, StringComparer.Ordinal));
            }
            if (a is double[] da && b is double[] db)
            {
                return da.Length == db.Length && da.Zip(db, (x, y) => Math.Abs(x - y) < 1e-6).All(x => x);
            }
            if (a is double xa && b is double xb)
            {
                return Math.Abs(xa - xb) < 1e-6;
            }
            if (a is AssetValue aa && b is AssetValue ab)
            {
                return string.Equals(aa.Guid, ab.Guid, StringComparison.OrdinalIgnoreCase);
            }
            return a.Equals(b);
        }

        private static double Clean(double d) => double.Parse(((float)d).ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        /// <summary>The value in exactly the shape set_mod_settings accepts (the round trip).</summary>
        public static JToken ToJson(ModSettingField f, object v)
        {
            if (v == null)
            {
                return JValue.CreateNull();
            }
            if (v is ParamValue pv)
            {
                JObject o = new JObject();
                o["value"] = pv.Value == null ? JValue.CreateNull() : ToJson(f, pv.Value);
                o["override"] = pv.Override ?? false;
                return o;
            }
            switch (v)
            {
                case bool b: return new JValue(b);
                case string s: return new JValue(s);
                case string[] flags: return new JValue(string.Join(", ", flags));
                case AssetValue a:
                    if (a.Guid == null)
                    {
                        return JValue.CreateNull();
                    }
                    return a.Info != null ? (JToken)a.Info : new JObject { ["guid"] = a.Guid };
                case double d: return f.Kind == SettingKind.Int ? new JValue((long)Math.Round(d)) : new JValue(Clean(d));
                case double[] arr:
                {
                    string[] keys = f.Kind == SettingKind.Rect ? new[] { "x", "y", "width", "height" }
                        : f.Kind == SettingKind.Color ? new[] { "r", "g", "b", "a" }
                        : f.Kind == SettingKind.Vector2 ? new[] { "x", "y" }
                        : new[] { "x", "y", "z" };
                    JObject o = new JObject();
                    for (int i = 0; i < keys.Length && i < arr.Length; i++)
                    {
                        o[keys[i]] = new JValue(Clean(arr[i]));
                    }
                    return o;
                }
                case List<AssetValue> list:
                    return new JArray(list.Select(x => ToJson(f, x)).ToArray());
                default:
                    return new JValue(v.ToString());
            }
        }

        // ------------------------------------------------------------------ descriptions

        public static string TypeText(ModSettingField f) => SettingsRules.TypeName(f);

        /// <summary>The accepted JSON shape in a few words.</summary>
        public static string ShapeText(ModSettingField f)
        {
            string s;
            switch (f.Kind)
            {
                case SettingKind.Bool: s = "true | false"; break;
                case SettingKind.Int: s = "integer"; break;
                case SettingKind.Float: s = "number"; break;
                case SettingKind.String: s = "string"; break;
                case SettingKind.Enum:
                case SettingKind.NamedInt: s = "one of " + string.Join(" | ", f.OfferedNames ?? new string[0]) + " (case-insensitive; the numbers of the values also work)"; break;
                case SettingKind.Flags: s = "flag names joined by ',' (\"Timer, ObtainPoints\") or an array of names; \"\" = none; names: " + string.Join(", ", f.EnumNames ?? new string[0]); break;
                case SettingKind.Vector2: s = "{x, y}"; break;
                case SettingKind.Vector3: s = "{x, y, z}"; break;
                case SettingKind.Rect: s = "{x, y, width, height}"; break;
                case SettingKind.Color: s = "{r, g, b, a?} floats 0-1 (a defaults to 1)"; break;
                case SettingKind.Asset: s = "null, a guid string or {guid}"; break;
                case SettingKind.AssetList: s = "array of {guid, name, ...} | null"; break;
                case SettingKind.Trigger: s = "read-only action trigger (get_action_graph scope playerSettings)"; break;
                default: s = "preset name"; break;
            }
            return f.IsParam ? "{value: " + s + ", override?: bool} or just the value (= override on)" : s;
        }

        public static JObject FieldJson(SettingsSection section, ModSettingField f, ISettingsState state, IRuleContext ctx)
        {
            JObject j = new JObject
            {
                ["name"] = f.Name,
                ["label"] = f.Label,
                ["type"] = TypeText(f),
                ["shape"] = ShapeText(f),
                ["writable"] = f.Writable,
            };
            if (!f.Writable)
            {
                j["reason"] = f.ReadOnlyReason;
            }
            if (f.NeverShown)
            {
                j["neverShown"] = true;
            }
            if (f.HasDefault)
            {
                j["default"] = ToJson(f, f.IsParam ? null : f.Default);
            }
            object current = null;
            try
            {
                current = state.Current(f);
            }
            catch (RpcException)
            {
                // unreadable: value stays null
            }
            j["value"] = current == null ? JValue.CreateNull() : ToJson(f, current);
            if (f.Shown != null)
            {
                bool? shown = f.Shown.Evaluate(ctx);
                j["shown"] = shown.HasValue ? new JValue(shown.Value) : JValue.CreateNull();
                j["shownWhen"] = f.Shown.Describe();
            }
            if (f.Writable && (f.Kind == SettingKind.Enum || f.Kind == SettingKind.NamedInt || f.Kind == SettingKind.Flags) && f.OfferedNames != null)
            {
                j["choices"] = new JArray(f.OfferedNames);
            }
            if (f.Kind == SettingKind.Preset)
            {
                IList<string> names = state.PresetNames(f);
                j["choices"] = names == null ? JValue.CreateNull() : (JToken)new JArray(names.ToArray());
            }
            if (f.Kind == SettingKind.Asset || f.Kind == SettingKind.AssetList)
            {
                j["assets"] = new JObject { ["types"] = new JArray(f.AssetTypes), ["locations"] = f.AssetLocations, ["nullAllowed"] = f.Kind == SettingKind.Asset };
            }
            if (f.HasLimits || f.Special != SettingSpecial.None)
            {
                JObject lim = new JObject();
                double min = f.Min ?? double.NaN;
                double max = f.Max ?? double.NaN;
                if (f.RangeFromAttribute && state.TryGetLimits(f, out double rmin, out double rmax))
                {
                    min = rmin;
                    max = rmax;
                    lim["source"] = "the game's Range attribute (read at run time)";
                }
                if (!double.IsNaN(min))
                {
                    lim["min"] = Clean(min);
                }
                if (!double.IsNaN(max))
                {
                    lim["max"] = Clean(max);
                }
                if (f.Special != SettingSpecial.None)
                {
                    lim["rect"] = "width and height 0..2000, centre within +-(1000 - half size)" + (f.Special == SettingSpecial.RectCenteredCircleAware ? "; height = width for Circle" : "");
                }
                lim["applied"] = "values outside are clamped and reported in set_mod_settings.clamped";
                j["limits"] = lim;
            }
            else if (f.IsParam && f.Kind == SettingKind.Float)
            {
                j["limits"] = new JObject { ["note"] = "none (free input in the editor)" };
            }
            if (f.Note != null)
            {
                j["note"] = f.Note;
            }
            return j;
        }

        private sealed class ReadContext : IRuleContext
        {
            private readonly SettingsSection m_section;
            private readonly ISettingsState m_state;

            public ReadContext(SettingsSection section, ISettingsState state)
            {
                m_section = section;
                m_state = state;
            }

            public object Get(string property)
            {
                ModSettingField f = m_section.Find(property);
                object v = f == null ? null : m_state.Current(f);
                if (v is ParamValue pv)
                {
                    v = pv.Value;
                }
                return v ?? InspectorRules.Unknown;
            }

            public bool? Derived(string name) => m_state.Derived(name);
        }

        public static IRuleContext ReadOnlyContext(SettingsSection section, ISettingsState state) => new ReadContext(section, state);

        /// <summary>One section for list_mod_settings: description and every field with its current value.</summary>
        public static JObject SectionListJson(SettingsSection section, ISettingsState state)
        {
            IRuleContext ctx = ReadOnlyContext(section, state);
            JArray fields = new JArray();
            foreach (ModSettingField f in section.Fields)
            {
                fields.Add(FieldJson(section, f, state, ctx));
            }
            JObject j = new JObject
            {
                ["name"] = section.Name,
                ["title"] = section.Title,
                ["savedTo"] = section.SavedTo,
                ["note"] = section.Note,
                ["fields"] = fields,
            };
            if (section.HiddenNames.Length > 0)
            {
                j["notShown"] = new JArray(section.HiddenNames);
            }
            if (section.Implied.Count > 0)
            {
                j["implied"] = new JArray(section.Implied.Select(i => (object)i.Why).ToArray());
            }
            if (section.Guards.Count > 0)
            {
                j["refused"] = new JArray(section.Guards.Select(g => (object)$"{g.Field} = {g.Value} unless {g.Require.Describe()}").ToArray());
            }
            ModSettingField preset = section.Fields.FirstOrDefault(f => f.Kind == SettingKind.Preset);
            if (preset != null)
            {
                IList<string> names = state.PresetNames(preset);
                j["presets"] = names == null ? JValue.CreateNull() : (JToken)new JArray(names.ToArray());
            }
            return j;
        }

        /// <summary>One section for get_mod_settings: values in the shapes set_mod_settings accepts.</summary>
        public static JObject SectionValuesJson(SettingsSection section, ISettingsState state)
        {
            JObject values = new JObject();
            foreach (ModSettingField f in section.Fields)
            {
                if (f.OmitFromGet)
                {
                    continue;
                }
                object v;
                try
                {
                    v = state.Current(f);
                }
                catch (RpcException)
                {
                    continue;
                }
                if (v == null && !(f.Kind == SettingKind.Asset))
                {
                    continue; // e.g. the parameters of an absent effect
                }
                values[f.Name] = v == null ? JValue.CreateNull() : ToJson(f, v);
            }
            return values;
        }
    }
}
