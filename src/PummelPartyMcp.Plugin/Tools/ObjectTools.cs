using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;
using UnityEngine;

namespace PummelPartyMcp.Tools
{
    /// <summary>get_object.</summary>
    internal static class ObjectTools
    {
        public static void Register(ToolRegistry registry)
        {
            registry.Register(ToolDefinition.Read("get_object", ctx =>
            {
                string guid = EditorAccess.NormalizeGuid(ctx.GetString("guid", required: true), "guid");
                return ctx.OnMainThread(() => GetObject(guid));
            }));
        }

        private static JObject GetObject(string guid)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            PlaytestAccess.RequireEditMode(ed, "get_object");
            ModTransform mt =EditorAccess.FindByGuid(ed, guid, out Transform root);
            if (mt == null)
            {
                throw RpcException.NotFound($"no object with guid {guid}");
            }
            Transform t = mt.transform;
            GameObject go = t.gameObject;

            ModTransform parentMt = t.parent != null && t.parent != root ? t.parent.GetComponent<ModTransform>() : null;

            // Triggers are summarized here ({triggerType, actionCount}); get_action_graph returns them in full.
            ValueConverter conv = new ValueConverter(mod, summarizeTriggers: true);
            JArray components = new JArray();
            foreach (ModBehaviour mb in EditorAccess.ModComponents(go))
            {
                ModReflectedType reflected = ModReflectionSystem.GetType(mb.GetType());
                components.Add(ComponentReader.Read(mb, reflected, conv));
            }

            return new JObject
            {
                ["guid"] = guid,
                ["name"] = go.name,
                ["active"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["layer"] = go.layer,
                ["tag"] = go.tag,
                ["path"] = EditorAccess.PathOf(t, root),
                ["context"] = root == ed.m_hierarchyRoot ? "scene" : "prefabs",
                ["parentGuid"] = parentMt != null ? EditorAccess.GuidOf(parentMt) : null,
                ["childCount"] = EditorAccess.HidesChildren(t) ? 0 : t.childCount,
                ["transform"] = new JObject
                {
                    ["localPosition"] = conv.Convert(t.localPosition),
                    ["localEulerAngles"] = conv.Convert(t.localEulerAngles),
                    ["localScale"] = conv.Convert(t.localScale),
                },
                ["components"] = components,
            };
        }
    }
}
