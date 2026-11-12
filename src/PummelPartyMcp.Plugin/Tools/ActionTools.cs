using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModSystem.Logic;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;
using UnityEngine;

namespace PummelPartyMcp.Tools
{
    /// <summary>
    /// Read tools of the Action Editor model: get_action_graph (triggers and their flat action lists, doc 04),
    /// list_action_types (the palette with gates), list_trigger_types (the Logic component's Event dropdown).
    /// </summary>
    internal static class ActionTools
    {
        public static void Register(ToolRegistry registry)
        {
            registry.Register(ToolDefinition.Read("get_action_graph", ctx =>
            {
                TriggerAddress scope = ParseScopeOnly(ctx);
                return ctx.OnMainThread(() => GetActionGraph(scope));
            }));

            registry.Register(ToolDefinition.Read("list_action_types", ctx =>
            {
                TriggerAddress addr = null;
                bool any = ctx.Params["guid"] != null || ctx.Params["trigger"] != null || ctx.Params["scope"] != null || ctx.Params["component"] != null || ctx.Params["path"] != null;
                if (any)
                {
                    addr = TriggerAddress.Parse(ctx.Params, EditorAccess.NormalizeGuid, requireTrigger: true);
                }
                return ctx.OnMainThread(() => ListActionTypes(addr));
            }));

            registry.Register(ToolDefinition.Read("list_trigger_types", ctx => ctx.OnMainThread(ListTriggerTypes)));
        }

        /// <summary>get_action_graph params: guid (+component) or scope "playerSettings".</summary>
        private static TriggerAddress ParseScopeOnly(ToolContext ctx)
        {
            if (ctx.Params["trigger"] != null || ctx.Params["path"] != null)
            {
                throw RpcException.InvalidParams("get_action_graph takes guid (+ component) or scope \"playerSettings\"; it lists all triggers");
            }
            return TriggerAddress.Parse(ctx.Params, EditorAccess.NormalizeGuid, requireTrigger: false);
        }

        // ------------------------------------------------------------------ get_action_graph

        private static JArray GetActionGraph(TriggerAddress scope)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            PlaytestAccess.RequireEditMode(ed, "get_action_graph");
            ValueConverter conv = new ValueConverter(mod, summarizeTriggers: false, maxDepth: 12);
            JArray result = new JArray();

            if (scope.IsPlayerSettings)
            {
                foreach (string field in CallSites.PlayerSettingTriggers)
                {
                    TriggerAddress a = new TriggerAddress { Scope = TriggerAddress.ScopePlayerSettings, Trigger = field };
                    SlotName.TryParse(field, out a.Slot);
                    ResolvedTrigger r = ActionModel.Resolve(ed, mod, a, forWrite: false);
                    result.Add(Entry(conv, r));
                }
                return result;
            }

            ModTransform mt = EditorAccess.FindByGuid(ed, scope.Guid, out _) ?? throw RpcException.NotFound($"no object with guid {scope.Guid}");
            bool componentMatched = scope.Component == null;
            foreach (ModBehaviour mb in EditorAccess.ModComponents(mt.gameObject))
            {
                if (!ActionModel.MatchesComponent(mb, scope.Component))
                {
                    continue;
                }
                componentMatched = true;
                foreach (TriggerSlot slot in ActionModel.Slots(mb))
                {
                    result.Add(SlotEntry(conv, slot));
                }
            }
            if (!componentMatched)
            {
                throw RpcException.NotFound($"object {scope.Guid} has no component matching '{scope.Component}'");
            }
            return result;
        }

        /// <summary>One get_action_graph entry for a component trigger slot.</summary>
        internal static JObject SlotEntry(ValueConverter conv, TriggerSlot slot)
        {
            JObject entry = new JObject
            {
                ["component"] = slot.Reflected.name,
                ["componentGuid"] = EditorAccess.GuidOf(slot.Component),
                ["triggerName"] = slot.Name,
            };
            AddTrigger(conv, entry, slot.Trigger);
            string hidden = ActionModel.WhySlotHidden(slot);
            entry["callSite"] = ActionModel.SiteFor(slot).ToJson();
            entry["editable"] = hidden == null;
            if (hidden != null)
            {
                entry["reason"] = hidden;
            }
            return entry;
        }

        /// <summary>Entry for any resolved trigger (player settings, nested popup choices, or a slot).</summary>
        internal static JObject Entry(ValueConverter conv, ResolvedTrigger r)
        {
            if (r.Slot != null && !r.IsNested)
            {
                return SlotEntry(conv, r.Slot);
            }
            JObject entry = new JObject();
            if (r.Address.IsPlayerSettings)
            {
                entry["scope"] = TriggerAddress.ScopePlayerSettings;
                entry["component"] = null;
                entry["componentGuid"] = null;
            }
            else
            {
                entry["component"] = r.Slot.Reflected.name;
                entry["componentGuid"] = EditorAccess.GuidOf(r.Slot.Component);
            }
            entry["triggerName"] = r.Address.Trigger;
            if (r.IsNested)
            {
                entry["path"] = new JArray(r.Address.Path.Select(s => new JObject { ["action"] = s.Action, ["choice"] = s.Choice }));
            }
            AddTrigger(conv, entry, r.Trigger);
            entry["callSite"] = r.Site.ToJson();
            entry["editable"] = r.NotEditable == null;
            if (r.NotEditable != null)
            {
                entry["reason"] = r.NotEditable;
            }
            return entry;
        }

        private static void AddTrigger(ValueConverter conv, JObject entry, ModLogicTrigger trigger)
        {
            if (trigger == null)
            {
                entry["triggerType"] = null;
                entry["actions"] = new JArray();
                return;
            }
            JObject full = (JObject)conv.Trigger(trigger, 0);
            entry["triggerType"] = full["triggerType"];
            entry["triggerClass"] = full["triggerClass"];
            entry["triggerParameters"] = full["triggerParameters"];
            entry["actions"] = full["actions"];
        }

        // ------------------------------------------------------------------ list_action_types

        private static JObject ListActionTypes(TriggerAddress addr)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            if (addr != null)
            {
                // The trigger entry carries action targets (references); without an address the palette is static.
                PlaytestAccess.RequireEditMode(ed, "list_action_types with a trigger address");
            }
            ResolvedTrigger r = addr != null ? ActionModel.Resolve(ed, mod, addr, forWrite: false) : null;
            CallSite site = r?.Site;
            ValueConverter conv = new ValueConverter(mod, summarizeTriggers: false, maxDepth: 12);
            JArray rows = new JArray();
            foreach (ActionTypeInfo t in ActionModel.ActionTypes())
            {
                ModAction fresh = (ModAction)Activator.CreateInstance(t.ClassType);
                JObject row = new JObject
                {
                    ["type"] = t.EnumName,
                    ["value"] = t.EnumValue,
                    ["className"] = t.ClassType.Name,
                    ["uiName"] = t.UiName,
                    ["category"] = ActionModel.CategoryName(t.Meta.Category),
                    ["helpText"] = t.Meta.HelpText,
                    ["modTypes"] = new JArray(PaletteGates.ModTypeNames((int)t.Meta.ModTypes)),
                    ["boardSpaceOnly"] = (((int)t.Meta.UsageFlags) & PaletteGates.UsageBoardSpaceOnly) != 0,
                };
                JArray parameters = new JArray();
                foreach (FieldInfo f in ActionParams.Fields(t.ClassType))
                {
                    parameters.Add(ActionParams.Describe(t.ClassType, f, fresh, conv));
                }
                row["parameters"] = parameters;
                if (fresh is ModSystem.Logic.TargetAction ta)
                {
                    bool[] hideAction = ActionModel.HideFlags(ta);
                    row["supportsTargets"] = true;
                    row["defaultTargetFlags"] = TargetBits.Format((int)ta.TargetFlags);
                    row["hiddenTargetFlags"] = new JArray(Enumerable.Range(0, 3).Where(i => hideAction.Length > i && hideAction[i]).Select(i => TargetBits.Names[i]));
                    row["targetType"] = ActionModel.AcceptedTargetType(ta).Name;
                    if (site != null)
                    {
                        bool[] hidden = TargetFlagRules.Hidden(hideAction, site.HideSource);
                        row["availableTargetFlags"] = new JArray(Enumerable.Range(0, 3).Where(i => !hidden[i]).Select(i => TargetBits.Names[i]));
                        row["targetFlagsLocked"] = hidden.Count(b => b) >= 2;
                        row["appendTargetFlags"] = TargetBits.Format(site.DefaultTargets != 0 ? site.DefaultTargets : (int)ta.TargetFlags);
                    }
                }
                else
                {
                    row["supportsTargets"] = false;
                    row["defaultTargetFlags"] = null;
                }
                string why = ActionModel.WhyNotUsable(t, mod, site);
                row["usableHere"] = why == null;
                if (why != null)
                {
                    row["reason"] = why;
                }
                rows.Add(row);
            }
            JObject result = new JObject { ["modType"] = mod.ModType.ToString() };
            if (r != null)
            {
                result["trigger"] = Entry(conv, r);
            }
            result["actionTypes"] = rows;
            if (ed.State != EditorState.Editing)
            {
                result["playMode"] = true;
            }
            return result;
        }

        // ------------------------------------------------------------------ list_trigger_types

        private static JObject ListTriggerTypes()
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            string modType = mod.ModType.ToString();
            ValueConverter conv = new ValueConverter(mod, summarizeTriggers: false, maxDepth: 12);
            JArray rows = new JArray();
            foreach (ModTriggerType tt in Enum.GetValues(typeof(ModTriggerType)))
            {
                Type cls = ModLogicTrigger.GetTriggerClassType(tt);
                if (cls == null)
                {
                    continue;
                }
                object fresh = Activator.CreateInstance(cls);
                JArray parameters = new JArray();
                foreach (FieldInfo f in ActionParams.Fields(cls))
                {
                    parameters.Add(ActionParams.Describe(cls, f, fresh, conv));
                }
                string why = LogicTriggerTypes.WhyNotOffered(tt.ToString(), modType);
                JObject row = new JObject
                {
                    ["type"] = tt.ToString(),
                    ["value"] = (int)tt,
                    ["className"] = cls.Name,
                    ["parameters"] = parameters,
                    ["sourceTargetAvailable"] = tt == ModTriggerType.Hit || tt == ModTriggerType.WeaponHit,
                    ["addable"] = why == null,
                };
                if (why != null)
                {
                    row["reason"] = why;
                }
                rows.Add(row);
            }
            JObject result = new JObject { ["modType"] = modType, ["triggerTypes"] = rows };
            if (ed.State != EditorState.Editing)
            {
                result["playMode"] = true;
            }
            return result;
        }
    }
}
