using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModSystem.Logic;
using PummelPartyMcp.Core;
using UnityEngine;

namespace PummelPartyMcp.Game
{
    /// <summary>One trigger slot of a component: a reflected property of type ModLogicTrigger (or an element of a ModLogicTrigger[]).</summary>
    internal sealed class TriggerSlot
    {
        public ModBehaviour Component;
        public ModReflectedType Reflected;
        public ReflectedProperty Property;
        public int Index = -1;
        public ModLogicTrigger Trigger;

        public string Name => Index >= 0 ? $"{Property.name}[{Index}]" : Property.name;
        public bool IsLogicList => Component is ModLogic && Property.name == "Triggers" && Index >= 0;
    }

    /// <summary>A resolved trigger address (fresh references; never keep across frames).</summary>
    internal sealed class ResolvedTrigger
    {
        public TriggerAddress Address;

        // object scope
        public ModTransform Object;
        public TriggerSlot Slot;

        // player-settings scope
        public SimpleModPlayerSettings Settings;
        public FieldInfo SettingsField;

        /// <summary>The Display Popup actions passed through (one per path step).</summary>
        public List<BoardPopupAction> Popups = new List<BoardPopupAction>();

        public ModLogicTrigger Trigger;
        public CallSite Site;

        /// <summary>Owner for reference context checks (the component holding the trigger, or the scene root for settings).</summary>
        public Component ReferenceOwner;

        /// <summary>Why the editor does not currently let the user edit this list (null = editable).</summary>
        public string NotEditable;

        public bool IsNested => Popups.Count > 0;
        public string Name => Address.Describe();
    }

    /// <summary>Metadata of one palette action type (ModActionMeta registry entry).</summary>
    internal sealed class ActionTypeInfo
    {
        public Type ClassType;
        public ModActionMeta Meta;
        public string EnumName;
        public int EnumValue;
        public string UiName;
    }

    /// <summary>Access to the Action Editor model: trigger slots, addressing, action types. Main thread only.</summary>
    internal static class ActionModel
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static List<ActionTypeInfo> s_types;

        // ------------------------------------------------------------------ slots

        /// <summary>Trigger slots of a component, in reflected property order (what get_action_graph lists).</summary>
        public static List<TriggerSlot> Slots(ModBehaviour mb)
        {
            List<TriggerSlot> result = new List<TriggerSlot>();
            ModReflectedType reflected = ModReflectionSystem.GetType(mb.GetType());
            if (reflected == null)
            {
                return result;
            }
            foreach (ReflectedProperty p in reflected.properties ?? new ReflectedProperty[0])
            {
                Type baseType = p.isArray ? p.elementType : p.systemType;
                if (baseType == null || !typeof(ModLogicTrigger).IsAssignableFrom(baseType))
                {
                    continue;
                }
                object value = p.GetValue(mb); // the trigger getters return their fields
                if (value is ModLogicTrigger[] arr)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        result.Add(new TriggerSlot { Component = mb, Reflected = reflected, Property = p, Index = i, Trigger = arr[i] });
                    }
                }
                else if (!p.isArray)
                {
                    result.Add(new TriggerSlot { Component = mb, Reflected = reflected, Property = p, Trigger = value as ModLogicTrigger });
                }
            }
            return result;
        }

        public static bool MatchesComponent(ModBehaviour mb, string filter)
        {
            if (filter == null)
            {
                return true;
            }
            ModReflectedType rt = ModReflectionSystem.GetType(mb.GetType());
            return string.Equals(filter, rt.name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(filter, rt.uiName, StringComparison.OrdinalIgnoreCase)
                || (Guid.TryParse(filter, out Guid g) && g.ToString() == EditorAccess.GuidOf(mb));
        }

        public static CallSite SiteFor(TriggerSlot slot) =>
            CallSites.ForSlot(slot.Reflected.name, slot.Property.name, slot.Trigger?.TriggerType.ToString());

        /// <summary>Why the component's inspector does not show this slot's Edit button (null = shown).</summary>
        public static string WhySlotHidden(TriggerSlot slot)
        {
            ModBehaviour c = slot.Component;
            if (c.GetComponent<ModPrefabInstance>() != null && !(c is ModPrefabInstance) && !(c is ModTransform))
            {
                return "the inspector shows only Transform and Prefab Instance on a prefab instance; edit the prefab instead";
            }
            return CallSites.WhySlotHidden(slot.Reflected.name, slot.Property.name, name =>
            {
                switch (c)
                {
                    case ModTrigger t:
                        switch (name)
                        {
                            case "TriggerOnHit": return t.TriggerOnHit;
                            case "TriggerOnEnter": return t.TriggerOnEnter;
                            case "TriggerOnExit": return t.TriggerOnExit;
                            case "TriggerOnStay": return t.TriggerOnStay;
                            case "TriggerShape": return t.TriggerShape.ToString();
                        }
                        break;
                    case ModItem item:
                        if (name == "ItemType")
                        {
                            return item.ItemType.ToString();
                        }
                        break;
                }
                return null;
            });
        }

        // ------------------------------------------------------------------ resolution

        /// <summary>
        /// Resolves an address to the live trigger. <paramref name="forWrite"/>: the object must be in the current editing
        /// context (EditContext rules). Throws NOT_FOUND / INVALID_PARAMS / INVALID_STATE.
        /// </summary>
        public static ResolvedTrigger Resolve(EditorSystem ed, PummelMod mod, TriggerAddress a, bool forWrite)
        {
            ResolvedTrigger r = new ResolvedTrigger { Address = a };
            if (a.IsPlayerSettings)
            {
                if (!(mod is MinigameMod mm))
                {
                    throw RpcException.InvalidParams($"player-settings triggers exist only in Minigame mods (this is a {mod.ModType} mod)");
                }
                SimpleModPlayerSettings settings = mm.MinigameSettings?.SimpleModPlayerSettings;
                if (settings == null)
                {
                    throw new RpcException(ErrorCodes.InvalidState, "the minigame's player settings are not loaded");
                }
                CallSite site = CallSites.ForPlayerSetting(a.Slot.Property);
                FieldInfo f = typeof(SimpleModPlayerSettings).GetField(a.Slot.Property, BindingFlags.Instance | BindingFlags.Public);
                if (site == null || f == null || !typeof(ModLogicTrigger).IsAssignableFrom(f.FieldType))
                {
                    throw RpcException.NotFound($"no player-settings trigger '{a.Slot.Property}' in the editor (one of {string.Join(", ", CallSites.PlayerSettingTriggers)})");
                }
                r.Settings = settings;
                r.SettingsField = f;
                r.Trigger = (ModLogicTrigger)f.GetValue(settings);
                r.Site = site;
                r.ReferenceOwner = ed.m_hierarchyRoot;
            }
            else
            {
                ModTransform mt;
                if (forWrite)
                {
                    mt = EditContext.RequireEditable(ed, a.Guid);
                }
                else
                {
                    mt = EditorAccess.FindByGuid(ed, a.Guid, out _) ?? throw RpcException.NotFound($"no object with guid {a.Guid}");
                }
                r.Object = mt;
                List<ModBehaviour> comps = EditorAccess.ModComponents(mt.gameObject).Where(c => MatchesComponent(c, a.Component)).ToList();
                if (comps.Count == 0)
                {
                    throw RpcException.NotFound($"'{mt.name}' has no component matching '{a.Component}'");
                }
                List<TriggerSlot> all = comps.SelectMany(Slots).ToList();
                List<TriggerSlot> hits = all.Where(s => s.Property.name == a.Slot.Property && s.Index == a.Slot.Index).ToList();
                if (hits.Count == 0)
                {
                    hits = all.Where(s => string.Equals(s.Property.name, a.Slot.Property, StringComparison.OrdinalIgnoreCase) && s.Index == a.Slot.Index).ToList();
                }
                if (hits.Count == 0)
                {
                    string names = all.Count == 0 ? "none" : string.Join(", ", all.Select(s => ModReflectionSystem.GetType(s.Component.GetType()).name + "." + s.Name).ToArray());
                    throw RpcException.NotFound($"'{mt.name}' has no trigger '{a.Trigger}'{(a.Component != null ? $" on {a.Component}" : "")} (triggers: {names})");
                }
                if (hits.Count > 1)
                {
                    throw RpcException.InvalidParams($"trigger '{a.Trigger}' exists on several components of '{mt.name}'; pass 'component' ({string.Join(", ", hits.Select(s => EditorAccess.GuidOf(s.Component)).ToArray())})");
                }
                TriggerSlot slot = hits[0];
                r.Slot = slot;
                r.Trigger = slot.Trigger;
                r.Site = SiteFor(slot);
                r.ReferenceOwner = slot.Component;
                r.NotEditable = WhySlotHidden(slot);
            }
            if (r.Trigger == null)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"trigger '{a.Describe()}' is empty (null) in the model");
            }

            // Nested popup-choice lists.
            for (int i = 0; i < a.Path.Count; i++)
            {
                TriggerPathStep step = a.Path[i];
                ModAction[] actions = r.Trigger.Actions ?? new ModAction[0];
                if (step.Action >= actions.Length)
                {
                    throw RpcException.NotFound($"'path[{i}].action': index {step.Action} is out of range (the list has {actions.Length} actions)");
                }
                if (!(actions[step.Action] is BoardPopupAction popup))
                {
                    throw RpcException.InvalidParams($"'path[{i}].action': action {step.Action} is {actions[step.Action]?.Type.ToString() ?? "null"}, not a Display Popup (BoardPopup) action; only popup choices contain nested action lists");
                }
                int count = PopupChoiceCount(popup);
                List<InteractionPopupChoice> choices = PopupChoices(popup);
                if (step.Choice >= count || step.Choice >= choices.Count)
                {
                    throw RpcException.NotFound($"'path[{i}].choice': choice {step.Choice} does not exist (the popup shows {count} choice{(count == 1 ? "" : "s")}; raise m_choiceCount with set_action first)");
                }
                InteractionPopupChoice choice = choices[step.Choice];
                if (choice?.OnSelectTrigger == null)
                {
                    throw new RpcException(ErrorCodes.InvalidState, $"popup choice {step.Choice} has no On Select trigger");
                }
                r.Popups.Add(popup);
                r.Trigger = choice.OnSelectTrigger;
                r.Site = CallSites.PopupChoice(step.Choice);
            }
            return r;
        }

        public static int PopupChoiceCount(BoardPopupAction popup) => (int)GameReflection.GetField(popup, "m_choiceCount");

        public static List<InteractionPopupChoice> PopupChoices(BoardPopupAction popup) =>
            (List<InteractionPopupChoice>)GameReflection.GetField(popup, "m_choices") ?? new List<InteractionPopupChoice>();

        /// <summary>Replaces the trigger object of a resolved ModLogic list entry the way ModLogicInspector does (new array).</summary>
        public static void ReplaceLogicTrigger(ResolvedTrigger r, ModLogicTrigger replacement)
        {
            ModLogic logic = (ModLogic)r.Slot.Component;
            ModLogicTrigger[] list = (ModLogicTrigger[])logic.Triggers.Clone();
            list[r.Slot.Index] = replacement;
            logic.Triggers = list;
            r.Trigger = replacement;
            r.Slot.Trigger = replacement;
        }

        // ------------------------------------------------------------------ targets

        /// <summary>ModSystem.Logic.TargetAction.GetHideFlags() (protected virtual): [Source, Receiver, Advanced].</summary>
        public static bool[] HideFlags(ModSystem.Logic.TargetAction action)
        {
            MethodInfo m = typeof(ModSystem.Logic.TargetAction).GetMethod("GetHideFlags", Inst)
                ?? throw RpcException.Internal(GameReflection.MissingMessage(typeof(ModSystem.Logic.TargetAction), "GetHideFlags"));
            return (bool[])m.Invoke(action, null) ?? new bool[3];
        }

        /// <summary>Element type of ModSystem.Logic.TargetAction.GetAcceptedTargetType() (protected virtual; Component[] by default).</summary>
        public static Type AcceptedTargetType(ModSystem.Logic.TargetAction action)
        {
            MethodInfo m = typeof(ModSystem.Logic.TargetAction).GetMethod("GetAcceptedTargetType", Inst)
                ?? throw RpcException.Internal(GameReflection.MissingMessage(typeof(ModSystem.Logic.TargetAction), "GetAcceptedTargetType"));
            Type t = (Type)m.Invoke(action, null);
            return t != null && t.IsArray ? t.GetElementType() : (t ?? typeof(Component));
        }

        // ------------------------------------------------------------------ action types

        /// <summary>The palette's action types (ModActionMeta registry), ordered by ModActionType value.</summary>
        public static List<ActionTypeInfo> ActionTypes()
        {
            if (s_types != null)
            {
                return s_types;
            }
            FieldInfo uiNameField = GameReflection.FindField(typeof(ModActionMeta), "m_uiName");
            List<ActionTypeInfo> list = new List<ActionTypeInfo>();
            foreach (KeyValuePair<Type, ModActionMeta> kv in ModActionMeta.GetAllActions())
            {
                if (!typeof(ModAction).IsAssignableFrom(kv.Key) || kv.Key.IsAbstract || kv.Key.GetConstructor(Type.EmptyTypes) == null)
                {
                    continue;
                }
                ModAction probe = (ModAction)Activator.CreateInstance(kv.Key);
                string ui = uiNameField?.GetValue(kv.Value) as string;
                if (string.IsNullOrEmpty(ui))
                {
                    string s = kv.Value.UIString ?? kv.Key.Name;
                    int sp = s.IndexOf(' ');
                    ui = sp >= 0 ? s.Substring(sp + 1) : s;
                }
                list.Add(new ActionTypeInfo
                {
                    ClassType = kv.Key,
                    Meta = kv.Value,
                    EnumName = probe.Type.ToString(),
                    EnumValue = (int)probe.Type,
                    UiName = ui,
                });
            }
            list.Sort((x, y) => x.EnumValue.CompareTo(y.EnumValue));
            if (list.Count > 0)
            {
                s_types = list; // the registry is filled at BeforeSceneLoad; never cache an empty list
            }
            return list;
        }

        /// <summary>By ModActionType name, class name or UI name (case-insensitive).</summary>
        public static ActionTypeInfo FindActionType(string query)
        {
            string q = query.Trim();
            return ActionTypes().FirstOrDefault(t => string.Equals(t.EnumName, q, StringComparison.OrdinalIgnoreCase))
                ?? ActionTypes().FirstOrDefault(t => string.Equals(t.ClassType.Name, q, StringComparison.OrdinalIgnoreCase))
                ?? ActionTypes().FirstOrDefault(t => string.Equals(t.UiName, q, StringComparison.OrdinalIgnoreCase));
        }

        public static ActionTypeInfo InfoFor(Type actionClass) => ActionTypes().FirstOrDefault(t => t.ClassType == actionClass);

        /// <summary>Palette gates for the open mod and (when given) a call site; null = usable.</summary>
        public static string WhyNotUsable(ActionTypeInfo t, PummelMod mod, CallSite site)
        {
            return PaletteGates.WhyNotUsable(t.UiName, (int)t.Meta.ModTypes, (int)t.Meta.UsageFlags, mod.ModType.ToString(),
                site != null ? site.UsageFlags : (int?)null, site?.Label);
        }

        public static string CategoryName(ActionCategory c) => ModActionMeta.GetCategoryName(c);
    }
}
