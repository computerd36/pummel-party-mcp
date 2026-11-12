using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    /// <summary>Which tools may run inside run_batch (pure; unit-tested).</summary>
    public static class BatchRules
    {
        public const int MaxSteps = 100;

        /// <summary>Write tools of the open-mod kind that may be batch steps.</summary>
        public static readonly HashSet<string> AllowedWrite = new HashSet<string>(StringComparer.Ordinal)
        {
            "create_object", "duplicate_object", "delete_object", "reparent_object", "set_object", "set_transform",
            "add_component", "remove_component", "set_property", "set_properties",
            "add_action", "remove_action", "move_action", "set_action", "add_trigger", "remove_trigger", "set_trigger",
            "undo", "save_mod", "set_mod_settings",
        };

        /// <summary>Read tools that may be batch steps (they run synchronously on the main thread inside the batch).</summary>
        public static readonly HashSet<string> AllowedRead = new HashSet<string>(StringComparer.Ordinal)
        {
            "get_object", "get_hierarchy", "get_action_graph",
            "list_mods", "list_assets", "list_component_types", "list_action_types", "list_trigger_types",
            "get_mod_settings", "list_mod_settings",
        };

        /// <summary>Explicitly refused, with the reason given in the error.</summary>
        public static readonly Dictionary<string, string> Disallowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run_batch"] = "batches cannot be nested",
            ["create_test_mod"] = "switching mods inside a batch is not allowed",
            ["open_test_mod"] = "switching mods inside a batch is not allowed",
            ["start_playtest"] = "playtests cannot be started inside a batch",
            ["stop_playtest"] = "playtests cannot be stopped inside a batch",
            ["screenshot"] = "screenshot needs end-of-frame rendering; call it outside the batch",
            ["import_asset"] = "it copies files on disk and runs the game's importer (decoding can take a while); call it outside the batch",
            ["set_import_settings"] = "it re-imports an asset and writes files; call it outside the batch",
            ["create_material"] = "it creates files immediately and is not undoable; call it outside the batch",
            ["set_material"] = "it writes the material's files immediately and is not undoable; call it outside the batch",
            ["remove_asset"] = "it deletes files immediately and is not undoable; call it outside the batch",
            ["capture_preview_image"] = "it needs several frames (the editor renders the screenshot at the end of the next frames); call it outside the batch",
        };

        /// <summary>Null when <paramref name="tool"/> may be a step; otherwise why not.</summary>
        public static string Refusal(string tool, bool registered)
        {
            if (tool != null && Disallowed.TryGetValue(tool, out string why))
            {
                return $"'{tool}' is not allowed inside run_batch ({why})";
            }
            if (!registered)
            {
                return $"unknown tool '{Truncate(tool, 80)}'";
            }
            if (!AllowedWrite.Contains(tool) && !AllowedRead.Contains(tool))
            {
                return $"'{tool}' is not allowed inside run_batch (allowed: the object/component/property/action/trigger edit tools, undo, save_mod, set_mod_settings, " +
                       "get_object, get_hierarchy, get_action_graph, get_mod_settings and the list_* tools)";
            }
            return null;
        }

        internal static string Truncate(string s, int max) => s == null ? "<null>" : s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    /// <summary>One step of a run_batch call after up-front validation.</summary>
    public sealed class BatchStep
    {
        public int Index;
        public string Id;
        public string Tool;
        /// <summary>The step's params as given (placeholders unresolved).</summary>
        public JObject Params;
        public bool IsWrite;
        public bool HasPlaceholders;
        /// <summary>The step's own time limit (the tool's limit).</summary>
        public TimeSpan Timeout;
        /// <summary>Write steps without placeholders: params already parsed up front (reused when the step runs).</summary>
        public bool PreParsed;
        public object PreParsedArgs;

        public string Label => Id != null ? $"steps[{Index}] (id '{Id}', {Tool})" : $"steps[{Index}] ({Tool})";
    }

    /// <summary>What the batch needs to know about a registered tool.</summary>
    public sealed class BatchToolInfo
    {
        public bool IsWrite;
        public TimeSpan Timeout;
        /// <summary>Write tools: parses params without game access (throws INVALID_PARAMS); null when the tool has no parser.</summary>
        public Func<JObject, object> Parse;
    }

    /// <summary>A validated run_batch request. <see cref="Parse"/> does every check that is possible before anything runs.</summary>
    public sealed class BatchPlan
    {
        private static readonly Regex s_id = new Regex(@"^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

        public readonly List<BatchStep> Steps = new List<BatchStep>();
        public bool StopOnError = true;

        /// <summary>
        /// Up-front validation of the whole batch: shape of every step, unknown/disallowed tools, ids, placeholder syntax and
        /// references (only to ids of earlier steps), and the params of every write step without placeholders (parsed with
        /// the tool's own parser). Any problem refuses the whole batch with INVALID_PARAMS naming the step; nothing runs.
        /// </summary>
        /// <param name="lookup">Registered tool info by name, or null when no such tool is registered.</param>
        public static BatchPlan Parse(JObject p, Func<string, BatchToolInfo> lookup)
        {
            if (p == null)
            {
                throw RpcException.InvalidParams("params required");
            }
            BatchPlan plan = new BatchPlan();
            JToken stop = p["stopOnError"];
            if (stop != null && stop.Type != JTokenType.Null)
            {
                if (stop.Type != JTokenType.Boolean)
                {
                    throw RpcException.InvalidParams("'stopOnError' must be a boolean");
                }
                plan.StopOnError = (bool)stop;
            }
            if (!(p["steps"] is JArray steps))
            {
                throw RpcException.InvalidParams("'steps' is required: an array of {tool, params?, id?}");
            }
            if (steps.Count == 0 || steps.Count > BatchRules.MaxSteps)
            {
                throw RpcException.InvalidParams($"'steps' must have 1-{BatchRules.MaxSteps} entries (got {steps.Count})");
            }

            Dictionary<string, int> ids = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < steps.Count; i++)
            {
                if (!(steps[i] is JObject s))
                {
                    throw RpcException.InvalidParams($"steps[{i}] must be an object {{tool, params?, id?}}");
                }
                foreach (JProperty prop in s.Properties())
                {
                    if (prop.Name != "tool" && prop.Name != "params" && prop.Name != "id")
                    {
                        throw RpcException.InvalidParams($"steps[{i}]: unknown key '{BatchRules.Truncate(prop.Name, 40)}' (a step has tool, params and id)");
                    }
                }
                JToken toolTok = s["tool"];
                if (toolTok == null || toolTok.Type != JTokenType.String || string.IsNullOrEmpty((string)toolTok))
                {
                    throw RpcException.InvalidParams($"steps[{i}]: 'tool' must be a non-empty string");
                }
                string tool = (string)toolTok;
                JToken paramsTok = s["params"];
                JObject stepParams;
                if (paramsTok == null || paramsTok.Type == JTokenType.Null)
                {
                    stepParams = new JObject();
                }
                else if (paramsTok is JObject po)
                {
                    stepParams = po;
                }
                else
                {
                    throw RpcException.InvalidParams($"steps[{i}] ({tool}): 'params' must be an object");
                }
                string id = null;
                JToken idTok = s["id"];
                if (idTok != null && idTok.Type != JTokenType.Null)
                {
                    if (idTok.Type != JTokenType.String || !s_id.IsMatch((string)idTok))
                    {
                        throw RpcException.InvalidParams($"steps[{i}] ({tool}): 'id' must match [A-Za-z][A-Za-z0-9_-]{{0,63}}");
                    }
                    id = (string)idTok;
                }

                BatchToolInfo info = lookup(tool);
                string refusal = BatchRules.Refusal(tool, info != null);
                if (refusal != null)
                {
                    throw RpcException.InvalidParams($"steps[{i}]: {refusal}; nothing was run");
                }

                BatchStep step = new BatchStep { Index = i, Id = id, Tool = tool, Params = stepParams, IsWrite = info.IsWrite, Timeout = info.Timeout };
                try
                {
                    step.HasPlaceholders = Placeholders.Validate(stepParams, ids);
                }
                catch (RpcException e)
                {
                    throw RpcException.InvalidParams($"{step.Label}: {e.Message}; nothing was run");
                }
                if (id != null)
                {
                    if (ids.ContainsKey(id))
                    {
                        throw RpcException.InvalidParams($"steps[{i}] ({tool}): duplicate id '{id}' (also steps[{ids[id]}])");
                    }
                    ids.Add(id, i);
                }
                if (step.IsWrite && !step.HasPlaceholders && info.Parse != null)
                {
                    try
                    {
                        step.PreParsedArgs = info.Parse(Placeholders.Resolve(stepParams, null));
                        step.PreParsed = true;
                    }
                    catch (RpcException e) when (e.Code == ErrorCodes.InvalidParams)
                    {
                        throw RpcException.InvalidParams($"{step.Label}: {e.Message}; nothing was run");
                    }
                }
                plan.Steps.Add(step);
            }
            return plan;
        }
    }

    /// <summary>
    /// "$id.path" references in batch step params. Any JSON <b>string value</b> (at any depth inside the step's params,
    /// object keys excluded) that starts with "$" is a reference and must match
    /// <c>$&lt;id&gt;&lt;accessor&gt;{1,8}</c>, where id is an earlier step's id and an accessor is <c>.name</c>
    /// (name = [A-Za-z_][A-Za-z0-9_]{0,63}, an object member) or <c>[n]</c> (n = 0..9999, an array element), e.g.
    /// <c>$floor.guid</c>, <c>$dup.objects[0].guid</c>, <c>$t.triggerName</c>. The whole string is replaced by a copy of
    /// the JSON value found at that path in the referenced step's <c>result</c> (any JSON type). A string starting with
    /// "$$" is not a reference: one "$" is removed ("$$5" is the literal "$5"). Pure; unit-tested.
    /// </summary>
    public static class Placeholders
    {
        public const int MaxAccessors = 8;

        private static readonly Regex s_ref = new Regex(
            @"^\$(?<id>[A-Za-z][A-Za-z0-9_-]{0,63})(?<path>(?:\.[A-Za-z_][A-Za-z0-9_]{0,63}|\[(?:0|[1-9][0-9]{0,3})\]){1," + MaxAccessors + @"})$",
            RegexOptions.CultureInvariant);

        private static readonly Regex s_accessor = new Regex(@"\.(?<name>[A-Za-z_][A-Za-z0-9_]{0,63})|\[(?<index>[0-9]+)\]", RegexOptions.CultureInvariant);

        /// <summary>A parsed reference.</summary>
        public sealed class Reference
        {
            public string Text;
            public string Id;
            /// <summary>Accessors in order: a string (member) or an int (index).</summary>
            public List<object> Path = new List<object>();
        }

        /// <summary>
        /// Null when <paramref name="s"/> is not a reference (does not start with a single "$"); the parsed reference
        /// otherwise. Throws INVALID_PARAMS for a malformed reference.
        /// </summary>
        public static Reference TryParse(string s)
        {
            if (s == null || s.Length == 0 || s[0] != '$' || (s.Length > 1 && s[1] == '$'))
            {
                return null;
            }
            Match m = s_ref.Match(s);
            if (!m.Success)
            {
                throw RpcException.InvalidParams(
                    $"'{BatchRules.Truncate(s, 80)}' is not a valid step reference (expected $<id>.<member> / $<id>[<index>] ..., up to {MaxAccessors} accessors, " +
                    "e.g. \"$floor.guid\"; write \"$$\" for a literal \"$\")");
            }
            Reference r = new Reference { Text = s, Id = m.Groups["id"].Value };
            foreach (Match a in s_accessor.Matches(m.Groups["path"].Value))
            {
                if (a.Groups["name"].Success)
                {
                    r.Path.Add(a.Groups["name"].Value);
                }
                else
                {
                    r.Path.Add(int.Parse(a.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return r;
        }

        /// <summary>
        /// Checks every reference in <paramref name="node"/>: syntax, and that its id belongs to an earlier step
        /// (<paramref name="earlierIds"/>). Returns whether any reference was found. Throws INVALID_PARAMS.
        /// </summary>
        public static bool Validate(JToken node, IDictionary<string, int> earlierIds)
        {
            bool found = false;
            Walk(node, s =>
            {
                Reference r = TryParse(s);
                if (r != null)
                {
                    found = true;
                    if (earlierIds == null || !earlierIds.ContainsKey(r.Id))
                    {
                        throw RpcException.InvalidParams($"'{r.Text}' refers to '{r.Id}', which is not the id of an earlier step (references may only point backwards)");
                    }
                }
            });
            return found;
        }

        /// <summary>
        /// Returns a deep copy of <paramref name="node"/> with every reference replaced and every "$$" escape unescaped.
        /// <paramref name="resultOf"/> gives the result of a successful step by id, or throws INVALID_PARAMS (step failed /
        /// not run); null is only allowed when the node contains no reference.
        /// </summary>
        public static JObject Resolve(JObject node, Func<string, JToken> resultOf)
        {
            return (JObject)ResolveToken(node, resultOf, 0);
        }

        private static JToken ResolveToken(JToken t, Func<string, JToken> resultOf, int depth)
        {
            if (depth > 128)
            {
                throw RpcException.InvalidParams("params nested too deeply");
            }
            switch (t)
            {
                case JObject o:
                {
                    JObject copy = new JObject();
                    foreach (JProperty p in o.Properties())
                    {
                        copy.Add(p.Name, ResolveToken(p.Value, resultOf, depth + 1));
                    }
                    return copy;
                }
                case JArray a:
                {
                    JArray copy = new JArray();
                    foreach (JToken item in a)
                    {
                        copy.Add(ResolveToken(item, resultOf, depth + 1));
                    }
                    return copy;
                }
                case JValue v when v.Type == JTokenType.String:
                {
                    string s = (string)v;
                    if (s.Length > 1 && s[0] == '$' && s[1] == '$')
                    {
                        return new JValue(s.Substring(1));
                    }
                    Reference r = TryParse(s);
                    if (r == null)
                    {
                        return new JValue(s);
                    }
                    if (resultOf == null)
                    {
                        throw RpcException.InvalidParams($"'{r.Text}': references cannot be resolved here");
                    }
                    return Lookup(r, resultOf(r.Id)).DeepClone();
                }
                default:
                    return t.DeepClone();
            }
        }

        /// <summary>Follows the reference's path inside a step result. Throws INVALID_PARAMS when a member/element is missing.</summary>
        public static JToken Lookup(Reference r, JToken result)
        {
            JToken cur = result;
            string walked = "$" + r.Id;
            foreach (object acc in r.Path)
            {
                if (acc is string name)
                {
                    JToken next = cur is JObject o ? o[name] : null;
                    if (next == null)
                    {
                        throw RpcException.InvalidParams($"'{r.Text}': {walked} has no member '{name}'" + (cur is JObject ? "" : $" (it is {Kind(cur)})"));
                    }
                    walked += "." + name;
                    cur = next;
                }
                else
                {
                    int index = (int)acc;
                    if (!(cur is JArray arr) || index >= arr.Count)
                    {
                        throw RpcException.InvalidParams($"'{r.Text}': {walked} has no element [{index}]" +
                            (cur is JArray a2 ? $" (it has {a2.Count})" : $" (it is {Kind(cur)})"));
                    }
                    walked += "[" + index + "]";
                    cur = arr[index];
                }
            }
            return cur;
        }

        private static string Kind(JToken t) => t == null ? "missing" : t.Type == JTokenType.Object ? "an object" : t.Type == JTokenType.Array ? "an array" : t.Type.ToString().ToLowerInvariant();

        private static void Walk(JToken t, Action<string> onString)
        {
            switch (t)
            {
                case JObject o:
                    foreach (JProperty p in o.Properties())
                    {
                        Walk(p.Value, onString);
                    }
                    break;
                case JArray a:
                    foreach (JToken item in a)
                    {
                        Walk(item, onString);
                    }
                    break;
                case JValue v when v.Type == JTokenType.String:
                    onString((string)v);
                    break;
            }
        }
    }
}
