using System;
using System.Collections.Generic;
using System.Linq;
using ModSystem.Logic;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using PummelBridge.Game;
using UnityEngine;

namespace PummelBridge.Tools
{
    /// <summary>
    /// Phase 2 increment 3: write tools of the Action Editor model (all guard kind OpenMod). They replicate what the
    /// Action Editor window (ActionEditorWindow.DrawActionPanel) and ModLogicInspector do to the model: replace
    /// ModLogicTrigger.Actions / ModLogic.Triggers arrays and write the action's/trigger's serialized fields. Object-scope
    /// edits run through the edit pipeline (select, wait for the undo baseline, apply in one frame, wait for the push), so
    /// each call is one undo step; player-settings edits apply at once and are not undoable (the editor has no undo for
    /// mod settings).
    /// </summary>
    internal static class ActionEditTools
    {
        public const int MaxParameters = 64;

        private sealed class ActionArgs
        {
            public TriggerAddress Address;
            public string Type;
            public int? Index;
            public int? NewIndex;
            public JObject Parameters;
            public JToken TargetFlags;
            public JToken Targets;
        }

        private sealed class TriggerArgs
        {
            public TriggerAddress Address;
            public string Guid;
            public string Component;
            public string Type;
            public JObject Parameters;
        }

        /// <summary>Validated target changes for one action.</summary>
        private sealed class TargetPlan
        {
            public int? Flags;
            public Component[] Targets;
            public bool IsEmpty => !Flags.HasValue && Targets == null;
        }

        public static void Register(ToolRegistry registry, EditServices services)
        {
            registry.Register(ToolDefinition.Write("add_action", WriteGuard.OpenMod(), ctx =>
            {
                ActionArgs a = ParseActionArgs(ctx, needIndex: false);
                a.Type = ctx.GetString("type", required: true);
                a.Index = OptIndex(ctx, "index");
                return a;
            }, inv => AddAction(inv, services), EditCommon.EditTimeout));

            registry.Register(ToolDefinition.Write("remove_action", WriteGuard.OpenMod(),
                ctx => ParseActionArgs(ctx, needIndex: true, allowValues: false),
                inv => RemoveAction(inv, services), EditCommon.EditTimeout));

            registry.Register(ToolDefinition.Write("move_action", WriteGuard.OpenMod(), ctx =>
            {
                ActionArgs a = ParseActionArgs(ctx, needIndex: true, allowValues: false);
                a.NewIndex = OptIndex(ctx, "newIndex") ?? throw RpcException.InvalidParams("'newIndex' is required");
                return a;
            }, inv => MoveAction(inv, services), EditCommon.EditTimeout));

            registry.Register(ToolDefinition.Write("set_action", WriteGuard.OpenMod(), ctx =>
            {
                ActionArgs a = ParseActionArgs(ctx, needIndex: true);
                if ((a.Parameters == null || a.Parameters.Count == 0) && a.TargetFlags == null && a.Targets == null)
                {
                    throw RpcException.InvalidParams("give at least one of 'parameters', 'targetFlags', 'targets'");
                }
                return a;
            }, inv => SetAction(inv, services), EditCommon.EditTimeout));

            registry.Register(ToolDefinition.Write("add_trigger", WriteGuard.OpenMod(), ctx =>
            {
                if (ctx.Params["trigger"] != null || ctx.Params["scope"] != null || ctx.Params["path"] != null)
                {
                    throw RpcException.InvalidParams("add_trigger takes guid (+ component), type and parameters; triggers can only be added to a Logic component");
                }
                return new TriggerArgs
                {
                    Guid = EditCommon.Guid(ctx, "guid", true),
                    Component = ctx.GetString("component"),
                    Type = ctx.GetString("type") ?? "Start",
                    Parameters = OptParameters(ctx),
                };
            }, inv => AddTrigger(inv, services), EditCommon.EditTimeout));

            registry.Register(ToolDefinition.Write("remove_trigger", WriteGuard.OpenMod(), ctx =>
            {
                TriggerArgs a = new TriggerArgs { Address = TriggerAddress.Parse(ctx.Params, EditorAccess.NormalizeGuid) };
                RejectExtra(ctx, "type", "parameters", "index");
                return a;
            }, inv => RemoveTrigger(inv, services), EditCommon.EditTimeout));

            registry.Register(ToolDefinition.Write("set_trigger", WriteGuard.OpenMod(), ctx =>
            {
                TriggerArgs a = new TriggerArgs
                {
                    Address = TriggerAddress.Parse(ctx.Params, EditorAccess.NormalizeGuid),
                    Type = ctx.GetString("type"),
                    Parameters = OptParameters(ctx),
                };
                if (a.Type == null && (a.Parameters == null || a.Parameters.Count == 0))
                {
                    throw RpcException.InvalidParams("give 'type' and/or 'parameters'");
                }
                return a;
            }, inv => SetTrigger(inv, services), EditCommon.EditTimeout));
        }

        // ================================================================== params (HTTP thread)

        private static ActionArgs ParseActionArgs(ToolContext ctx, bool needIndex, bool allowValues = true)
        {
            ActionArgs a = new ActionArgs { Address = TriggerAddress.Parse(ctx.Params, EditorAccess.NormalizeGuid) };
            if (needIndex)
            {
                a.Index = OptIndex(ctx, "index") ?? throw RpcException.InvalidParams("'index' (action index in the list) is required");
            }
            if (allowValues)
            {
                a.Parameters = OptParameters(ctx);
                a.TargetFlags = NullToNone(ctx.Params["targetFlags"]);
                a.Targets = NullToNone(ctx.Params["targets"]);
                if (a.Targets != null && a.Targets.Type != JTokenType.Array)
                {
                    throw RpcException.InvalidParams("'targets' must be an array of object references (guid strings, {\"guid\": ...} or null)");
                }
            }
            else
            {
                RejectExtra(ctx, "parameters", "targetFlags", "targets", "type");
            }
            return a;
        }

        private static JToken NullToNone(JToken t) => t == null || t.Type == JTokenType.Null ? null : t;

        private static void RejectExtra(ToolContext ctx, params string[] names)
        {
            foreach (string n in names)
            {
                if (ctx.Params[n] != null)
                {
                    throw RpcException.InvalidParams($"'{n}' is not a parameter of {ctx.Method}");
                }
            }
        }

        private static int? OptIndex(ToolContext ctx, string name)
        {
            long? v = ctx.GetLong(name, 0, 100000);
            return v.HasValue ? (int)v.Value : (int?)null;
        }

        private static JObject OptParameters(ToolContext ctx)
        {
            JToken t = ctx.Params["parameters"];
            if (t == null || t.Type == JTokenType.Null)
            {
                return null;
            }
            if (!(t is JObject o))
            {
                throw RpcException.InvalidParams("'parameters' must be an object {name: value, ...}");
            }
            if (o.Count > MaxParameters)
            {
                throw RpcException.InvalidParams($"at most {MaxParameters} parameters per call");
            }
            return o;
        }

        // ================================================================== shared

        private sealed class Ctx
        {
            public EditorSystem Ed;
            public PummelMod Mod;
            public ResolvedTrigger First;
        }

        /// <summary>Guard-side preconditions + first resolution (main thread, before anything is selected).</summary>
        private static Ctx Begin(TriggerAddress address)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            ResolvedTrigger r = ActionModel.Resolve(ed, mod, address, forWrite: true);
            if (r.NotEditable != null)
            {
                throw RpcException.InvalidParams($"'{address.Describe()}': {r.NotEditable}");
            }
            return new Ctx { Ed = ed, Mod = mod, First = r };
        }

        /// <summary>Re-resolves the address in the edit frame (ModLogicInspector replaces the Triggers array every frame).</summary>
        private static ResolvedTrigger Fresh(Ctx c)
        {
            ResolvedTrigger r = ActionModel.Resolve(c.Ed, c.Mod, c.First.Address, forWrite: true);
            if (r.NotEditable != null)
            {
                throw RpcException.InvalidParams($"'{r.Name}': {r.NotEditable}; nothing was changed");
            }
            return r;
        }

        /// <summary>
        /// Runs an edit: object scope through the undoable pipeline (select the object, wait for its baseline, apply,
        /// wait for the push); player settings at once (no undo exists for mod settings in the editor).
        /// <paramref name="apply"/> returns the BuildResult body; <c>changed</c> is computed here.
        /// </summary>
        private static void Run(WriteInvocation inv, EditServices services, Ctx c, string what, Func<ResolvedTrigger, Func<JObject>> apply)
        {
            bool settings = c.First.Address.IsPlayerSettings;
            EditHost host = new EditHost(c.Ed, settings ? null : c.First.Object.gameObject);
            EditCommon.Run(services, inv, host, selectFirst: !settings, apply: () =>
            {
                ResolvedTrigger r = Fresh(c);
                ValueConverter conv = new ValueConverter(c.Mod, summarizeTriggers: false, maxDepth: 12);
                JToken before = settings ? conv.Trigger(r.Trigger, 0) : null;
                services.Observe();
                Func<JObject> build = apply(r);
                bool changed = settings
                    ? !JToken.DeepEquals(before, new ValueConverter(c.Mod, summarizeTriggers: false, maxDepth: 12).Trigger(r.Trigger, 0))
                    : EditCommon.SnapshotChanged(host);
                services.MarkEditIfChanged(c.Mod, what, changed);
                return new EditOutcome
                {
                    Changed = changed,
                    Then = settings ? PostEditWait.None : PostEditWait.AwaitPush,
                    BuildResult = report =>
                    {
                        JObject result = build();
                        bool undoable = !settings && EditCommon.Undoable(report);
                        EditCommon.WithEditFields(result, c.Ed, report, undoable);
                        if (settings && report.Changed)
                        {
                            result["note"] = "player-settings triggers are not undoable (the editor has no undo for mod settings); save_mod writes them to ModSettings.json";
                        }
                        return result;
                    },
                };
            });
        }

        private static JObject AddressJson(ResolvedTrigger r)
        {
            JObject j = new JObject();
            if (r.Address.IsPlayerSettings)
            {
                j["scope"] = TriggerAddress.ScopePlayerSettings;
            }
            else
            {
                j["guid"] = r.Address.Guid;
                j["component"] = r.Slot.Reflected.name;
                j["componentGuid"] = EditorAccess.GuidOf(r.Slot.Component);
            }
            j["trigger"] = r.Address.Trigger;
            if (r.IsNested)
            {
                j["path"] = new JArray(r.Address.Path.Select(s => new JObject { ["action"] = s.Action, ["choice"] = s.Choice }));
            }
            return j;
        }

        private static ModAction[] ActionsOf(ResolvedTrigger r) => r.Trigger.Actions ?? new ModAction[0];

        private static ModAction RequireSameAction(ResolvedTrigger r, int index, ModAction expected)
        {
            ModAction[] actions = ActionsOf(r);
            if (index >= actions.Length || !ReferenceEquals(actions[index], expected))
            {
                throw new RpcException(ErrorCodes.InvalidState, "the action list changed while the edit was waiting for the editor; nothing was changed (read get_action_graph again)");
            }
            return expected;
        }

        private static ReferenceResolver Refs(Ctx c, ResolvedTrigger r) => new ReferenceResolver(c.Ed, r.ReferenceOwner);

        /// <summary>
        /// Validates targetFlags/targets against the action and the call site (ModSystem.Logic.TargetAction.DrawToggleButtonSet / DrawTargets).
        /// <paramref name="currentFlags"/> is the flags value the change starts from.
        /// </summary>
        private static TargetPlan PrepareTargets(ModAction action, CallSite site, JToken flagsTok, JToken targetsTok, int? currentFlags, ReferenceResolver refs)
        {
            TargetPlan plan = new TargetPlan();
            if (flagsTok == null && targetsTok == null)
            {
                return plan;
            }
            if (!(action is ModSystem.Logic.TargetAction ta))
            {
                throw RpcException.InvalidParams($"{action.Type} has no targets ('targetFlags'/'targets' do not apply)");
            }
            bool[] hidden = TargetFlagRules.Hidden(ActionModel.HideFlags(ta), site.HideSource);
            int current = currentFlags ?? (int)ta.TargetFlags;
            int flags = current;
            if (flagsTok != null)
            {
                flags = TargetFlagRules.Parse(flagsTok, "targetFlags");
                string why = TargetFlagRules.Check(hidden, current, flags);
                if (why != null)
                {
                    throw RpcException.InvalidParams($"'targetFlags': {why}");
                }
                plan.Flags = flags;
            }
            if (targetsTok != null)
            {
                if ((flags & TargetBits.Advanced) == 0)
                {
                    throw RpcException.InvalidParams("'targets': the explicit target list is shown (and used) only while the Advanced target flag is set; pass targetFlags including Advanced");
                }
                Type element = ActionModel.AcceptedTargetType(ta);
                JArray arr = (JArray)targetsTok;
                if (arr.Count > ValueParser.MaxArrayLength)
                {
                    throw RpcException.InvalidParams($"'targets': more than {ValueParser.MaxArrayLength} elements");
                }
                Component[] comps = new Component[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                {
                    PRef r = (PRef)ValueParser.Parse(arr[i], ValueTypeDesc.Reference(element.Name), $"targets[{i}]");
                    comps[i] = (Component)ActionParams.ResolveRegistered(r.Guid, element, refs, $"targets[{i}]");
                }
                plan.Targets = comps;
            }
            return plan;
        }

        private static void ApplyTargets(ModAction action, TargetPlan plan)
        {
            if (plan.IsEmpty)
            {
                return;
            }
            ModSystem.Logic.TargetAction ta = (ModSystem.Logic.TargetAction)action;
            if (plan.Flags.HasValue)
            {
                ta.TargetFlags = (ModActionTargets)plan.Flags.Value; // public setter, as DrawToggleButtonSet
            }
            if (plan.Targets != null)
            {
                ta.Targets = plan.Targets; // wraps each in a new ModAssetReference<Component>, as DrawTargets' DrawValue does
            }
        }

        private static JObject ActionJson(PummelMod mod, ModAction action, int index) =>
            (JObject)new ValueConverter(mod, summarizeTriggers: false, maxDepth: 12).Action(action, index, 0);

        // ================================================================== add_action

        private static void AddAction(WriteInvocation inv, EditServices services)
        {
            ActionArgs a = inv.ArgsAs<ActionArgs>();
            Ctx c = Begin(a.Address);
            ActionTypeInfo info = ActionModel.FindActionType(a.Type)
                ?? throw RpcException.InvalidParams($"unknown action type '{a.Type}' (a ModActionType name like ChangeHealth, a class name or a UI name; see list_action_types)");
            string gate = ActionModel.WhyNotUsable(info, c.Mod, c.First.Site);
            if (gate != null)
            {
                throw RpcException.InvalidParams(gate);
            }
            int count = ActionsOf(c.First).Length;
            int index = ActionListOps.ResolveInsertIndex(a.Index, count, out bool append);

            // Exactly what the window does: Activator.CreateInstance(type) (Type.GetType of the palette entry's class).
            ModAction action = (ModAction)Activator.CreateInstance(info.ClassType);
            // Append only: the call site's default targets for ModSystem.Logic.TargetAction subclasses (ActionEditorWindow.cs append path);
            // the insert path (drop onto an action) keeps the constructor default.
            bool defaultsApplied = false;
            if (append && info.ClassType.IsSubclassOf(typeof(ModSystem.Logic.TargetAction)) && c.First.Site.DefaultTargets != 0)
            {
                ((ModSystem.Logic.TargetAction)action).TargetFlags = (ModActionTargets)c.First.Site.DefaultTargets;
                defaultsApplied = true;
            }
            ReferenceResolver refs = Refs(c, c.First);
            ParamPlan plan = ActionParams.Prepare(action, a.Parameters, refs, info.UiName);
            TargetPlan targets = PrepareTargets(action, c.First.Site, a.TargetFlags, a.Targets, null, refs);
            // The new instance is not in the model yet, so it can be configured now.
            plan.ApplyAll();
            ApplyTargets(action, targets);

            Run(inv, services, c, "add_action", r =>
            {
                if (ActionsOf(r).Length != count)
                {
                    throw new RpcException(ErrorCodes.InvalidState, "the action list changed while the edit was waiting for the editor; nothing was changed (read get_action_graph again)");
                }
                if (append)
                {
                    // ActionEditorWindow append: Actions = Actions.AddElement(typeof(ModAction)); SetValue(new, last)
                    ModAction[] grown = (ModAction[])ActionsOf(r).AddElement(typeof(ModAction));
                    grown.SetValue(action, grown.Length - 1);
                    r.Trigger.Actions = grown;
                }
                else
                {
                    // ActionEditorWindow insert: List<ModAction>.Insert(i, new) then ToArray()
                    r.Trigger.Actions = ActionListOps.Insert(ActionsOf(r), index, action);
                }
                return () =>
                {
                    JObject res = AddressJson(r);
                    res["index"] = index;
                    res["mode"] = append ? "append" : "insert";
                    res["defaultTargetsApplied"] = defaultsApplied;
                    res["action"] = ActionJson(c.Mod, action, index);
                    res["clamped"] = new JArray(plan.Clamped);
                    res["actionCount"] = ActionsOf(r).Length;
                    return res;
                };
            });
        }

        // ================================================================== remove_action

        private static void RemoveAction(WriteInvocation inv, EditServices services)
        {
            ActionArgs a = inv.ArgsAs<ActionArgs>();
            Ctx c = Begin(a.Address);
            ModAction[] actions = ActionsOf(c.First);
            int index = a.Index.Value;
            ActionListOps.CheckIndex(index, actions.Length, "index");
            ModAction target = actions[index];
            string removedType = target?.Type.ToString();
            Run(inv, services, c, "remove_action", r =>
            {
                RequireSameAction(r, index, target);
                // ActionEditorWindow close (X) button: Actions = Actions.DeleteElement(i, typeof(ModAction))
                r.Trigger.Actions = (ModAction[])ActionsOf(r).DeleteElement(index, typeof(ModAction));
                return () =>
                {
                    JObject res = AddressJson(r);
                    res["index"] = index;
                    res["removed"] = new JObject { ["type"] = removedType, ["className"] = target?.GetType().Name };
                    res["actionCount"] = ActionsOf(r).Length;
                    return res;
                };
            });
        }

        // ================================================================== move_action

        private static void MoveAction(WriteInvocation inv, EditServices services)
        {
            ActionArgs a = inv.ArgsAs<ActionArgs>();
            Ctx c = Begin(a.Address);
            ModAction[] actions = ActionsOf(c.First);
            int from = a.Index.Value;
            int to = a.NewIndex.Value;
            ActionListOps.CheckIndex(from, actions.Length, "index");
            ActionListOps.CheckIndex(to, actions.Length, "newIndex");
            ModAction target = actions[from];
            Run(inv, services, c, "move_action", r =>
            {
                RequireSameAction(r, from, target);
                if (from != to)
                {
                    // Equivalent to dragging the header over the actions in between (the window swaps the dragged action
                    // with each hovered neighbour).
                    r.Trigger.Actions = ActionListOps.Move(ActionsOf(r), from, to);
                }
                return () =>
                {
                    JObject res = AddressJson(r);
                    res["index"] = from;
                    res["newIndex"] = to;
                    res["order"] = new JArray(ActionsOf(r).Select(x => x?.Type.ToString()));
                    return res;
                };
            });
        }

        // ================================================================== set_action

        private static void SetAction(WriteInvocation inv, EditServices services)
        {
            ActionArgs a = inv.ArgsAs<ActionArgs>();
            Ctx c = Begin(a.Address);
            ModAction[] actions = ActionsOf(c.First);
            int index = a.Index.Value;
            ActionListOps.CheckIndex(index, actions.Length, "index");
            ModAction action = actions[index] ?? throw new RpcException(ErrorCodes.InvalidState, $"action {index} is null in the model");
            ActionTypeInfo info = ActionModel.InfoFor(action.GetType());
            ReferenceResolver refs = Refs(c, c.First);
            ParamPlan plan = ActionParams.Prepare(action, a.Parameters, refs, info?.UiName ?? action.GetType().Name);
            TargetPlan targets = PrepareTargets(action, c.First.Site, a.TargetFlags, a.Targets, null, refs);
            Run(inv, services, c, "set_action", r =>
            {
                RequireSameAction(r, index, action);
                plan.ApplyAll();
                ApplyTargets(action, targets);
                return () =>
                {
                    JObject res = AddressJson(r);
                    res["index"] = index;
                    res["action"] = ActionJson(c.Mod, action, index);
                    res["clamped"] = new JArray(plan.Clamped);
                    return res;
                };
            });
        }

        // ================================================================== triggers (ModLogic list)

        private static Type TriggerClass(string typeName, string modType, out ModTriggerType tt)
        {
            tt = ModTriggerType.None;
            string q = typeName.Trim();
            bool found = false;
            foreach (ModTriggerType v in Enum.GetValues(typeof(ModTriggerType)))
            {
                Type cls = ModLogicTrigger.GetTriggerClassType(v);
                if (string.Equals(v.ToString(), q, StringComparison.OrdinalIgnoreCase) || (cls != null && string.Equals(cls.Name, q, StringComparison.OrdinalIgnoreCase)))
                {
                    tt = v;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                throw RpcException.InvalidParams($"unknown trigger type '{typeName}' (see list_trigger_types)");
            }
            string why = LogicTriggerTypes.WhyNotOffered(tt.ToString(), modType);
            if (why != null)
            {
                throw RpcException.InvalidParams(why);
            }
            return ModLogicTrigger.GetTriggerClassType(tt);
        }

        private static void AddTrigger(WriteInvocation inv, EditServices services)
        {
            TriggerArgs a = inv.ArgsAs<TriggerArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            ModTransform mt = EditContext.RequireEditable(ed, a.Guid);
            List<ModLogic> logics = EditorAccess.ModComponents(mt.gameObject).OfType<ModLogic>().Where(l => ActionModel.MatchesComponent(l, a.Component)).ToList();
            if (logics.Count == 0)
            {
                throw RpcException.NotFound($"'{mt.name}' has no Logic component{(a.Component != null ? $" matching '{a.Component}'" : "")}; triggers can only be added to a Logic component (add one with add_component type Logic)");
            }
            if (logics.Count > 1)
            {
                throw RpcException.InvalidParams($"'{mt.name}' has several Logic components; pass 'component' ({string.Join(", ", logics.Select(EditorAccess.GuidOf).ToArray())})");
            }
            ModLogic logic = logics[0];
            if (mt.GetComponent<ModPrefabInstance>() != null)
            {
                throw RpcException.InvalidParams("the inspector shows only Transform and Prefab Instance on a prefab instance; edit the prefab instead");
            }
            Type cls = TriggerClass(a.Type, mod.ModType.ToString(), out ModTriggerType tt);
            // "Add Trigger" appends new StartTrigger(); choosing another Event then replaces it with CreateInstance(type)
            // and copies Actions (empty). The result equals a fresh instance of that type.
            ModLogicTrigger trigger = tt == ModTriggerType.Start ? new StartTrigger() : new StartTrigger().CreateInstance(tt);
            ReferenceResolver refs = new ReferenceResolver(ed, logic);
            ParamPlan plan = ActionParams.Prepare(trigger, a.Parameters, refs, $"{tt} trigger");
            plan.ApplyAll();
            int count = logic.Triggers.Length;
            TriggerAddress address = new TriggerAddress { Guid = a.Guid, Component = EditorAccess.GuidOf(logic), Trigger = $"Triggers[{count}]" };
            SlotName.TryParse(address.Trigger, out address.Slot);
            EditHost host = new EditHost(ed, mt.gameObject);
            EditCommon.Run(services, inv, host, selectFirst: true, apply: () =>
            {
                if (logic == null)
                {
                    throw RpcException.NotFound("the Logic component was removed meanwhile");
                }
                ModLogicTrigger[] current = logic.Triggers; // re-fetched in the edit frame
                if (current.Length != count)
                {
                    throw new RpcException(ErrorCodes.InvalidState, "the trigger list changed while the edit was waiting for the editor; nothing was changed");
                }
                services.Observe();
                List<ModLogicTrigger> list = new List<ModLogicTrigger>(current) { trigger };
                logic.Triggers = list.ToArray(); // ModLogicInspector: modLogic.Triggers = list.ToArray()
                bool changed = EditCommon.SnapshotChanged(host);
                services.MarkEditIfChanged(mod, "add_trigger", changed);
                return new EditOutcome
                {
                    Changed = changed,
                    Then = PostEditWait.AwaitPush,
                    BuildResult = report =>
                    {
                        ResolvedTrigger r = ActionModel.Resolve(ed, mod, address, forWrite: false);
                        JObject res = ActionTools.Entry(new ValueConverter(mod, summarizeTriggers: false, maxDepth: 12), r);
                        res["guid"] = a.Guid;
                        res["clamped"] = new JArray(plan.Clamped);
                        return EditCommon.WithEditFields(res, ed, report, EditCommon.Undoable(report));
                    },
                };
            });
        }

        private static void RemoveTrigger(WriteInvocation inv, EditServices services)
        {
            TriggerArgs a = inv.ArgsAs<TriggerArgs>();
            Ctx c = Begin(a.Address);
            if (c.First.IsNested || c.First.Slot == null || !c.First.Slot.IsLogicList)
            {
                throw RpcException.InvalidParams($"'{a.Address.Describe()}' is a fixed trigger slot; only the triggers of a Logic component (Triggers[i]) can be removed (remove its actions with remove_action)");
            }
            ModLogicTrigger target = c.First.Trigger;
            int index = c.First.Slot.Index;
            string type = target.TriggerType.ToString();
            int actionCount = ActionsOf(c.First).Length;
            Run(inv, services, c, "remove_trigger", r =>
            {
                if (!ReferenceEquals(r.Trigger, target))
                {
                    throw new RpcException(ErrorCodes.InvalidState, "the trigger list changed while the edit was waiting for the editor; nothing was changed");
                }
                ModLogic logic = (ModLogic)r.Slot.Component;
                // ModLogicInspector header X: list.Remove(trigger); modLogic.Triggers = list.ToArray()
                List<ModLogicTrigger> list = new List<ModLogicTrigger>(logic.Triggers);
                list.Remove(target);
                logic.Triggers = list.ToArray();
                return () =>
                {
                    JObject res = AddressJson(r);
                    res["removed"] = new JObject { ["triggerType"] = type, ["actionCount"] = actionCount };
                    res["triggerCount"] = logic.Triggers.Length;
                    res["indexNote"] = $"the following triggers moved up by one (Triggers[{index + 1}] is now Triggers[{index}], ...)";
                    return res;
                };
            });
        }

        private static void SetTrigger(WriteInvocation inv, EditServices services)
        {
            TriggerArgs a = inv.ArgsAs<TriggerArgs>();
            Ctx c = Begin(a.Address);
            ModLogicTrigger original = c.First.Trigger;
            ModLogicTrigger subject = original;
            bool typeChange = false;
            if (a.Type != null)
            {
                if (c.First.IsNested || c.First.Slot == null || !c.First.Slot.IsLogicList)
                {
                    throw RpcException.InvalidParams($"'{a.Address.Describe()}' is a fixed trigger slot; its type cannot be changed (only Logic component triggers have an Event dropdown)");
                }
                TriggerClass(a.Type, c.Mod.ModType.ToString(), out ModTriggerType tt);
                if (tt != original.TriggerType)
                {
                    // ModLogicInspector Event dropdown: CreateInstance(newType), copy only Actions (parameters reset).
                    subject = original.CreateInstance(tt);
                    subject.Actions = original.Actions;
                    typeChange = true;
                }
            }
            ReferenceResolver refs = Refs(c, c.First);
            ParamPlan plan = ActionParams.Prepare(subject, a.Parameters, refs, $"{subject.TriggerType} trigger");
            if (typeChange)
            {
                plan.ApplyAll(); // fresh instance, not yet in the model
            }
            string oldType = original.TriggerType.ToString();
            Run(inv, services, c, "set_trigger", r =>
            {
                if (!ReferenceEquals(r.Trigger, original))
                {
                    throw new RpcException(ErrorCodes.InvalidState, "the trigger changed while the edit was waiting for the editor; nothing was changed");
                }
                if (typeChange)
                {
                    subject.Actions = original.Actions; // re-copy in the edit frame
                    ActionModel.ReplaceLogicTrigger(r, subject);
                }
                else
                {
                    plan.ApplyAll();
                }
                return () =>
                {
                    JObject res = ActionTools.Entry(new ValueConverter(c.Mod, summarizeTriggers: false, maxDepth: 12), r);
                    if (!r.Address.IsPlayerSettings)
                    {
                        res["guid"] = r.Address.Guid;
                    }
                    if (typeChange)
                    {
                        res["previousTriggerType"] = oldType;
                        res["note"] = "changing the type keeps the actions and resets the trigger parameters to the new type's defaults, as the Logic inspector does";
                    }
                    res["clamped"] = new JArray(plan.Clamped);
                    return res;
                };
            });
        }
    }
}
