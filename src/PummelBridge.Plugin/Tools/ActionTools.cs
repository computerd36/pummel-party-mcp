using System;
using ModSystem.Logic;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using PummelBridge.Game;
using UnityEngine;

namespace PummelBridge.Tools
{
    /// <summary>get_action_graph: triggers and their flat action lists on an object's components (doc 04).</summary>
    internal static class ActionTools
    {
        public static void Register(ToolRegistry registry)
        {
            registry.Register(ToolDefinition.Read("get_action_graph", ctx =>
            {
                string guid = EditorAccess.NormalizeGuid(ctx.GetString("guid", required: true), "guid");
                string component = ctx.GetString("component");
                return ctx.OnMainThread(() => GetActionGraph(guid, component));
            }));
        }

        private static JArray GetActionGraph(string guid, string componentFilter)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            ModTransform mt = EditorAccess.FindByGuid(ed, guid, out _);
            if (mt == null)
            {
                throw RpcException.NotFound($"no object with guid {guid}");
            }

            ValueConverter conv = new ValueConverter(mod, summarizeTriggers: false, maxDepth: 12);
            JArray result = new JArray();
            bool componentMatched = componentFilter == null;
            foreach (ModBehaviour mb in EditorAccess.ModComponents(mt.gameObject))
            {
                ModReflectedType reflected = ModReflectionSystem.GetType(mb.GetType());
                string compGuid = EditorAccess.GuidOf(mb);
                if (componentFilter != null &&
                    !string.Equals(componentFilter, reflected.name, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(componentFilter, reflected.uiName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(componentFilter, compGuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                componentMatched = true;

                // Trigger slots are the component's reflected properties of type ModLogicTrigger / ModLogicTrigger[]
                // (ModLogic.Triggers, ModTrigger.On*Actions, ModItem.On*Trigger, ...). Their getters return fields.
                foreach (ReflectedProperty p in reflected.properties ?? new ReflectedProperty[0])
                {
                    Type baseType = p.isArray ? p.elementType : p.systemType;
                    if (baseType == null || !typeof(ModLogicTrigger).IsAssignableFrom(baseType))
                    {
                        continue;
                    }
                    object value = p.GetValue(mb);
                    if (value is ModLogicTrigger[] arr)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            result.Add(TriggerEntry(conv, reflected, compGuid, $"{p.name}[{i}]", arr[i]));
                        }
                    }
                    else if (value is ModLogicTrigger single)
                    {
                        result.Add(TriggerEntry(conv, reflected, compGuid, p.name, single));
                    }
                }
            }
            if (!componentMatched)
            {
                throw RpcException.NotFound($"object {guid} has no component matching '{componentFilter}'");
            }
            return result;
        }

        private static JObject TriggerEntry(ValueConverter conv, ModReflectedType reflected, string compGuid, string triggerName, ModLogicTrigger trigger)
        {
            JObject entry = new JObject
            {
                ["component"] = reflected.name,
                ["componentGuid"] = compGuid,
                ["triggerName"] = triggerName,
            };
            if (trigger == null)
            {
                entry["triggerType"] = null;
                entry["actions"] = new JArray();
                return entry;
            }
            JObject full = (JObject)conv.Trigger(trigger, 0);
            entry["triggerType"] = full["triggerType"];
            entry["triggerClass"] = full["triggerClass"];
            entry["triggerParameters"] = full["triggerParameters"];
            entry["actions"] = full["actions"];
            return entry;
        }
    }
}
