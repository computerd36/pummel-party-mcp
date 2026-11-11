using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using PummelBridge.Game;
using UnityEngine;

namespace PummelBridge.Tools
{
    /// <summary>Helpers shared by the object/component edit tools. Main thread unless noted.</summary>
    internal static class EditCommon
    {
        /// <summary>Edit tools: queueing + selection/baseline wait + edit + push wait.</summary>
        public static readonly TimeSpan EditTimeout = TimeSpan.FromSeconds(15);

        public static readonly ValueTypeDesc Vector3Desc = ValueTypeDesc.Simple(ValueKind.Vector3, "Vector3");

        /// <summary>Starts an edit pipeline; ticks it from the frame poller until it completes.</summary>
        public static void Run(EditServices services, WriteInvocation inv, EditHost host, bool selectFirst, Func<EditOutcome> apply)
        {
            EditPipeline pipeline = new EditPipeline(host, inv.Completion, selectFirst, apply);
            if (!pipeline.Start())
            {
                services.Poller.Add(pipeline.Tick);
            }
        }

        // ------------------------------------------------------------------ params (HTTP thread)

        public static string Guid(ToolContext ctx, string name, bool required)
        {
            string s = ctx.GetString(name, required);
            return s == null ? null : EditorAccess.NormalizeGuid(s, name);
        }

        public static float[] OptVector3(ToolContext ctx, string name)
        {
            JToken t = ctx.Params[name];
            if (t == null || t.Type == JTokenType.Null)
            {
                return null;
            }
            return ((PFloats)ValueParser.Parse(t, Vector3Desc, name)).Values;
        }

        public static bool? OptBool(ToolContext ctx, string name)
        {
            JToken t = ctx.Params[name];
            if (t == null || t.Type == JTokenType.Null)
            {
                return null;
            }
            if (t.Type != JTokenType.Boolean)
            {
                throw RpcException.InvalidParams($"'{name}' must be a boolean");
            }
            return (bool)t;
        }

        public static string OptObjectName(ToolContext ctx, string name)
        {
            string s = ctx.GetString(name);
            if (s == null)
            {
                return null;
            }
            if (s.Length == 0 || s.Length > 256)
            {
                throw RpcException.InvalidParams($"'{name}' must be 1-256 characters");
            }
            foreach (char c in s)
            {
                if (char.IsControl(c))
                {
                    throw RpcException.InvalidParams($"'{name}' must not contain control characters (newlines, tabs, ...)");
                }
            }
            return s;
        }

        // ------------------------------------------------------------------ results

        public static JToken Nullable(int? v) => v.HasValue ? new JValue(v.Value) : JValue.CreateNull();

        /// <summary>Guid of the parent object, or null when the parent is a context root (same rule as get_object).</summary>
        public static string ParentGuid(EditorSystem ed, Transform t)
        {
            Transform p = t.parent;
            if (p == null || p == ed.m_hierarchyRoot || p == ed.m_prefabsRoot)
            {
                return null;
            }
            ModTransform pmt = p.GetComponent<ModTransform>();
            return pmt != null ? EditorAccess.GuidOf(pmt) : null;
        }

        public static JObject TransformJson(PummelMod mod, Transform t)
        {
            ValueConverter conv = new ValueConverter(mod, summarizeTriggers: true);
            return new JObject
            {
                ["localPosition"] = conv.Convert(t.localPosition),
                ["localEulerAngles"] = conv.Convert(t.localEulerAngles),
                ["localScale"] = conv.Convert(t.localScale),
            };
        }

        /// <summary>get_hierarchy node of an object plus parentGuid, siblingIndex and transform.</summary>
        public static JObject ObjectNode(EditorSystem ed, PummelMod mod, ModTransform mt)
        {
            JObject node = HierarchyTools.NodeFor(mt);
            node["parentGuid"] = ParentGuid(ed, mt.transform);
            node["siblingIndex"] = mt.transform.GetSiblingIndex();
            node["transform"] = TransformJson(mod, mt.transform);
            return node;
        }

        /// <summary>Adds the common edit result fields.</summary>
        public static JObject WithEditFields(JObject result, EditorSystem ed, EditReport report, bool undoable)
        {
            result["changed"] = report.Changed;
            result["undoable"] = undoable;
            if (report.UndoPending)
            {
                result["undoPending"] = true;
                result["note"] = "the editor records the undo step when the left mouse button is released or the selection changes";
            }
            result["undoStackLength"] = Nullable(EditorAccess.UndoStackLength(ed));
            return result;
        }

        public static bool Undoable(EditReport r) => r.Changed && (r.UndoRecorded || r.UndoPending);

        /// <summary>
        /// After applying an edit to the selected target: compares its snapshot with the editor's baseline to decide
        /// whether the editor will push an undo step.
        /// </summary>
        public static bool SnapshotChanged(EditHost host)
        {
            byte[] baseline = host.BaselineData();
            byte[] now = UndoAccess.Snapshot(host.Target);
            return !UndoAccess.SameBytes(baseline, now);
        }

        /// <summary>
        /// Public setter of a reflected property: the concrete class's declared property, else ModBehaviour's (same lookup
        /// as ModReflectionSystem.GetReflectedProperties). Invoking it runs exactly the setter the inspector's
        /// ReflectedProperty.SetValue (FastMember) runs, but reports exceptions instead of only logging them.
        /// </summary>
        public static PropertyInfo Setter(Type componentType, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            PropertyInfo pi = componentType.GetProperty(name, flags) ?? typeof(ModBehaviour).GetProperty(name, flags);
            return pi != null && pi.CanWrite && pi.GetSetMethod(false) != null ? pi : null;
        }

        public static void InvokeSetter(PropertyInfo pi, object target, object value, string label)
        {
            try
            {
                pi.SetValue(target, value, null);
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException ?? e;
                throw new RpcException(ErrorCodes.Internal, $"setting {label} threw {inner.GetType().Name}: {inner.Message}");
            }
            catch (ArgumentException e)
            {
                throw RpcException.InvalidParams($"setting {label} failed: {e.Message}");
            }
        }
    }
}
