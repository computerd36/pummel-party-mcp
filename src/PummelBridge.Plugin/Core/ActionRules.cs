using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PummelBridge.Core
{
    // Phase 2 increment 3: game-independent rules of the Action Editor model (trigger addressing, action list index
    // rules, palette gates, call sites, target flag rules, parameter rules, complex values). Everything here is pure so
    // the net8.0 harness can test it; the game layer (Game/ActionModel.cs, Tools/ActionEditTools.cs) feeds it facts.

    /// <summary>ModActionTargets bits (Source=1, Receiver=2, Advanced=4).</summary>
    public static class TargetBits
    {
        public const int Source = 1;
        public const int Receiver = 2;
        public const int Advanced = 4;
        public static readonly string[] Names = { "Source", "Receiver", "Advanced" };

        /// <summary>Index 0..2 (Source, Receiver, Advanced) -> bit.</summary>
        public static int Bit(int index) => 1 << index;

        public static string Format(int flags)
        {
            if (flags == 0)
            {
                return "0";
            }
            List<string> parts = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                if ((flags & Bit(i)) != 0)
                {
                    parts.Add(Names[i]);
                }
            }
            return string.Join(", ", parts.ToArray());
        }
    }

    // ===================================================================== addressing

    public sealed class TriggerPathStep
    {
        /// <summary>Index of a Display Popup (BoardPopup) action in the current list.</summary>
        public int Action;

        /// <summary>Index of the popup choice (0-based) whose On Select list is entered.</summary>
        public int Choice;
    }

    /// <summary>"Triggers[2]" or "OnEnterActions": a component's reflected trigger property plus an optional array index.</summary>
    public struct SlotName
    {
        public string Property;
        public int Index; // -1 = not an array element

        public static bool TryParse(string text, out SlotName name)
        {
            name = new SlotName { Property = null, Index = -1 };
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string s = text.Trim();
            int open = s.IndexOf('[');
            if (open < 0)
            {
                if (!IsIdentifier(s))
                {
                    return false;
                }
                name.Property = s;
                return true;
            }
            if (!s.EndsWith("]", StringComparison.Ordinal) || open == 0)
            {
                return false;
            }
            string prop = s.Substring(0, open);
            string idx = s.Substring(open + 1, s.Length - open - 2);
            if (!IsIdentifier(prop) || idx.Length == 0 || idx.Length > 6 || !idx.All(char.IsDigit))
            {
                return false;
            }
            name.Property = prop;
            name.Index = int.Parse(idx, CultureInfo.InvariantCulture);
            return true;
        }

        private static bool IsIdentifier(string s) =>
            s.Length > 0 && s.Length <= 128 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

        public override string ToString() => Index >= 0 ? $"{Property}[{Index}]" : Property;
    }

    /// <summary>
    /// Where a trigger (an action list) lives. Object scope: object guid + optional component + trigger name as
    /// get_action_graph reports it. Player-settings scope: a SimpleModPlayerSettings trigger field. Both may continue
    /// into nested popup-choice lists with <see cref="Path"/>.
    /// </summary>
    public sealed class TriggerAddress
    {
        public const string ScopeObject = "object";
        public const string ScopePlayerSettings = "playerSettings";
        public const int MaxPathDepth = 8;

        public string Scope = ScopeObject;
        public string Guid;
        public string Component;
        public string Trigger;
        public SlotName Slot;
        public List<TriggerPathStep> Path = new List<TriggerPathStep>();

        public bool IsPlayerSettings => Scope == ScopePlayerSettings;

        /// <summary>
        /// Parses scope/guid/component/trigger/path from the params. <paramref name="normalizeGuid"/> normalises and
        /// validates the object guid (INVALID_PARAMS on a malformed one). With <paramref name="requireTrigger"/> false
        /// a missing trigger is allowed (read tools that list for an object).
        /// </summary>
        public static TriggerAddress Parse(JObject p, Func<string, string, string> normalizeGuid, bool requireTrigger = true)
        {
            TriggerAddress a = new TriggerAddress();
            JToken scope = p["scope"];
            if (scope != null && scope.Type != JTokenType.Null)
            {
                if (scope.Type != JTokenType.String)
                {
                    throw RpcException.InvalidParams("'scope' must be \"object\" or \"playerSettings\"");
                }
                string s = (string)scope;
                if (string.Equals(s, ScopeObject, StringComparison.OrdinalIgnoreCase))
                {
                    a.Scope = ScopeObject;
                }
                else if (string.Equals(s, ScopePlayerSettings, StringComparison.OrdinalIgnoreCase))
                {
                    a.Scope = ScopePlayerSettings;
                }
                else
                {
                    throw RpcException.InvalidParams($"'scope' must be \"object\" or \"playerSettings\" (got \"{s}\")");
                }
            }
            string guid = Str(p, "guid");
            string component = Str(p, "component");
            string trigger = Str(p, "trigger");
            if (a.IsPlayerSettings)
            {
                if (guid != null || component != null)
                {
                    throw RpcException.InvalidParams("scope \"playerSettings\" takes no 'guid'/'component'; 'trigger' names the player-settings trigger (e.g. \"onDeathTrigger\")");
                }
            }
            else
            {
                if (guid == null)
                {
                    throw RpcException.InvalidParams("'guid' (object guid) is required, or pass scope \"playerSettings\"");
                }
                a.Guid = normalizeGuid(guid, "guid");
                a.Component = component;
            }
            if (trigger == null)
            {
                if (requireTrigger)
                {
                    throw RpcException.InvalidParams(a.IsPlayerSettings
                        ? "'trigger' is required (one of " + string.Join(", ", CallSites.PlayerSettingTriggers) + ")"
                        : "'trigger' is required: the triggerName from get_action_graph, e.g. \"Triggers[0]\" or \"OnEnterActions\"");
                }
            }
            else
            {
                if (!SlotName.TryParse(trigger, out SlotName slot))
                {
                    throw RpcException.InvalidParams($"'trigger': \"{trigger}\" is not a trigger name (expected e.g. \"Triggers[0]\" or \"OnEnterActions\", as get_action_graph reports it)");
                }
                if (a.IsPlayerSettings && slot.Index >= 0)
                {
                    throw RpcException.InvalidParams("'trigger': player-settings triggers have no index");
                }
                a.Trigger = trigger.Trim();
                a.Slot = slot;
            }
            JToken path = p["path"];
            if (path != null && path.Type != JTokenType.Null)
            {
                if (!(path is JArray arr))
                {
                    throw RpcException.InvalidParams("'path' must be an array of {\"action\": <index>, \"choice\": <index>}");
                }
                if (arr.Count > MaxPathDepth)
                {
                    throw RpcException.InvalidParams($"'path' is limited to {MaxPathDepth} steps");
                }
                if (arr.Count > 0 && a.Trigger == null)
                {
                    throw RpcException.InvalidParams("'path' needs 'trigger'");
                }
                for (int i = 0; i < arr.Count; i++)
                {
                    if (!(arr[i] is JObject step))
                    {
                        throw RpcException.InvalidParams($"'path[{i}]' must be {{\"action\": <index>, \"choice\": <index>}}");
                    }
                    foreach (JProperty prop in step.Properties())
                    {
                        if (prop.Name != "action" && prop.Name != "choice")
                        {
                            throw RpcException.InvalidParams($"'path[{i}]': unknown key '{prop.Name}' (expected action, choice)");
                        }
                    }
                    a.Path.Add(new TriggerPathStep
                    {
                        Action = NonNegative(step["action"], $"path[{i}].action"),
                        Choice = NonNegative(step["choice"], $"path[{i}].choice"),
                    });
                }
            }
            return a;
        }

        private static string Str(JObject p, string name)
        {
            JToken t = p[name];
            if (t == null || t.Type == JTokenType.Null)
            {
                return null;
            }
            if (t.Type != JTokenType.String || ((string)t).Trim().Length == 0)
            {
                throw RpcException.InvalidParams($"'{name}' must be a non-empty string");
            }
            return (string)t;
        }

        private static int NonNegative(JToken t, string name)
        {
            if (t == null || t.Type != JTokenType.Integer)
            {
                throw RpcException.InvalidParams($"'{name}' must be an integer >= 0");
            }
            long v = (long)t;
            if (v < 0 || v > 100000)
            {
                throw RpcException.InvalidParams($"'{name}' must be an integer >= 0");
            }
            return (int)v;
        }

        /// <summary>Human-readable address for messages ("Triggers[0] > action 2 choice 1").</summary>
        public string Describe()
        {
            string s = IsPlayerSettings ? "player settings " + Trigger : Trigger;
            foreach (TriggerPathStep st in Path)
            {
                s += $" > popup action {st.Action} choice {st.Choice}";
            }
            return s;
        }
    }

    // ===================================================================== action list index rules

    /// <summary>Index rules and array operations on an action list (ModLogicTrigger.Actions).</summary>
    public static class ActionListOps
    {
        /// <summary>
        /// Insert position: omitted or == count -> append (the window's "Drag New Action Here"); 0..count-1 -> insert
        /// before the action currently at that index (the window's drop onto action index-1).
        /// </summary>
        public static int ResolveInsertIndex(int? index, int count, out bool append)
        {
            if (!index.HasValue || index.Value == count)
            {
                append = true;
                return count;
            }
            if (index.Value < 0 || index.Value > count)
            {
                throw RpcException.InvalidParams($"'index' must be between 0 and {count} (the list has {count} action{(count == 1 ? "" : "s")}; {count} or omitted = append)");
            }
            append = false;
            return index.Value;
        }

        public static void CheckIndex(int index, int count, string name)
        {
            if (count == 0)
            {
                throw RpcException.InvalidParams($"'{name}': the action list is empty");
            }
            if (index < 0 || index >= count)
            {
                throw RpcException.InvalidParams($"'{name}' must be between 0 and {count - 1} (the list has {count} action{(count == 1 ? "" : "s")})");
            }
        }

        public static T[] Insert<T>(T[] arr, int index, T item)
        {
            List<T> list = new List<T>(arr ?? new T[0]);
            list.Insert(index, item);
            return list.ToArray();
        }

        public static T[] RemoveAt<T>(T[] arr, int index)
        {
            List<T> list = new List<T>(arr);
            list.RemoveAt(index);
            return list.ToArray();
        }

        /// <summary>Moves one element so that it ends at <paramref name="to"/> (the others keep their order).</summary>
        public static T[] Move<T>(T[] arr, int from, int to)
        {
            List<T> list = new List<T>(arr);
            T item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
            return list.ToArray();
        }
    }

    // ===================================================================== palette gates

    /// <summary>
    /// The two gates of ActionEditorWindow.GetAvailableActionTypes: EditorSystem.CanUseItem(meta.ModTypes) against the open
    /// mod's type, and m_usageFlags.HasFlag(meta.UsageFlags) against the call site's AdditionalActionUsageFlags.
    /// </summary>
    public static class PaletteGates
    {
        public const int Minigame = 1, Map = 2, Item = 4, BoardSpace = 8;
        public const int UsageBoardSpaceOnly = 2;

        /// <summary>PummelModType name -> the PummelModTypeFlags bit CanUseItem tests (0 for unknown types: never usable).</summary>
        public static int ModTypeFlag(string modType)
        {
            switch (modType)
            {
                case "Minigame": return Minigame;
                case "Board": return Map;
                case "Item": return Item;
                case "BoardSpace": return BoardSpace;
                default: return 0;
            }
        }

        public static string[] ModTypeNames(int flags)
        {
            List<string> n = new List<string>();
            if ((flags & Minigame) != 0) n.Add("Minigame");
            if ((flags & Map) != 0) n.Add("Board");
            if ((flags & Item) != 0) n.Add("Item");
            if ((flags & BoardSpace) != 0) n.Add("BoardSpace");
            return n.ToArray();
        }

        public static bool ModTypeGate(int actionModTypes, string modType)
        {
            int bit = ModTypeFlag(modType);
            return bit != 0 && (actionModTypes & bit) == bit;
        }

        /// <summary>Enum.HasFlag semantics: every bit of the action's usage flags must be set at the call site.</summary>
        public static bool UsageGate(int actionUsage, int siteUsage) => (siteUsage & actionUsage) == actionUsage;

        /// <summary>Null if the palette would offer the action; else why not. <paramref name="siteUsage"/> null = no call site given (gate 2 not evaluated).</summary>
        public static string WhyNotUsable(string uiName, int actionModTypes, int actionUsage, string modType, int? siteUsage, string siteLabel = null)
        {
            if (!ModTypeGate(actionModTypes, modType))
            {
                string[] names = ModTypeNames(actionModTypes);
                return $"{uiName} is only available in {(names.Length == 0 ? "no" : string.Join("/", names))} mods (this is a {modType} mod)";
            }
            if (siteUsage.HasValue && !UsageGate(actionUsage, siteUsage.Value))
            {
                return $"{uiName} is only available in the action lists of Board Space Event triggers and Display Popup choices"
                       + (siteLabel != null ? $" (not in {siteLabel})" : "");
            }
            return null;
        }
    }

    // ===================================================================== call sites

    /// <summary>What an inspector passes to ModEditorGui.InputModLogicTrigger for one trigger slot (ActionWindowTriggerInfo).</summary>
    public sealed class CallSite
    {
        public string Label;
        public string ReceiverName = "Receiver";
        public string SourceName = "Source";
        public bool HideSource;
        public int DefaultTargets;
        public int UsageFlags;

        public JObject ToJson() => new JObject
        {
            ["label"] = Label,
            ["receiverName"] = ReceiverName,
            ["sourceName"] = SourceName,
            ["hideSource"] = HideSource,
            ["appendDefaultTargetFlags"] = DefaultTargets == 0 ? null : TargetBits.Format(DefaultTargets),
            ["boardSpaceOnlyActions"] = (UsageFlags & PaletteGates.UsageBoardSpaceOnly) != 0,
        };
    }

    /// <summary>
    /// Call-site table, transcribed from the inspectors: ModLogicInspector, ModTriggerInspector, ModItemInspector,
    /// ModBoardSpaceEventInspector, ModDestructibleInspector, ModFireWeaponInspector, MinigameModSettings
    /// (player "Event Triggers"), BoardPopupAction.DrawButtonDataUI (choice "On Select"), and ModEditorGui's generic
    /// MODLOGICTRIGGER case (defaults) for any other component.
    /// </summary>
    public static class CallSites
    {
        public static readonly string[] PlayerSettingTriggers =
            { "weaponHitTrigger", "tickTrigger", "onDeathTrigger", "onRespawnTrigger", "onFirstSpawnTrigger", "onRoundEndTrigger" };

        private static CallSite S(string label, string recv, string src, bool hide, int def, int usage = 0) =>
            new CallSite { Label = label, ReceiverName = recv, SourceName = src, HideSource = hide, DefaultTargets = def, UsageFlags = usage };

        /// <summary>Call site of a component trigger slot. <paramref name="logicTriggerType"/>: ModTriggerType name of a ModLogic entry.</summary>
        public static CallSite ForSlot(string componentType, string property, string logicTriggerType)
        {
            switch (componentType + "." + property)
            {
                case "ModLogic.Triggers":
                    // hideSourceFlag = type != Hit && type != WeaponHit
                    return S($"Logic: On {logicTriggerType}", "This Object", "Activator",
                        logicTriggerType != "Hit" && logicTriggerType != "WeaponHit", 0);
                case "ModTrigger.OnHitActions": return S("Trigger: On Hit Actions", "This Trigger", "Activator", false, 0);
                case "ModTrigger.OnEnterActions": return S("Trigger: On Enter Actions", "This Trigger", "Activator", false, TargetBits.Source);
                case "ModTrigger.OnExitActions": return S("Trigger: On Exit Actions", "This Trigger", "Activator", false, TargetBits.Source);
                case "ModTrigger.OnStayActions": return S("Trigger: On Stay Actions", "This Trigger", "Activator", false, TargetBits.Source);
                case "ModItem.OnPickupTrigger": return S("Item: 'On Pickup' Actions", "Item", "Player", false, 0);
                case "ModItem.OnWhileHeldTrigger": return S("Item: 'While held' Actions", "Item", "Player", false, 0);
                case "ModItem.OnUseTrigger": return S("Item: 'On Use' Actions", "Item", "Player", false, 0);
                case "ModItem.OnDropTrigger": return S("Item: 'On Drop' Actions", "Item", "Player", false, 0);
                case "ModBoardSpaceEvent.OnEnterTrigger": return S("Board Space Event: On Player Land", "This Board Space", "This Player", false, 0, PaletteGates.UsageBoardSpaceOnly);
                case "ModBoardSpaceEvent.OnPassTrigger": return S("Board Space Event: On Player Passing", "This Board Space", "This Player", false, 0, PaletteGates.UsageBoardSpaceOnly);
                case "ModDestructible.OnHitActions": return S("Hittable: On Hit", "This (Destructible)", "Hitter", false, 0);
                case "ModFireWeapon.WeaponHitTrigger": return S("Weapon: On Hit", "Weapon", "Hit Object", false, 0);
                default: return S(componentType + "." + property, "Receiver", "Source", false, 0); // ModEditorGui generic case
            }
        }

        /// <summary>Player-settings trigger (MinigameModSettings "Event Triggers"); null if the editor does not show it (hitTrigger).</summary>
        public static CallSite ForPlayerSetting(string field)
        {
            switch (field)
            {
                case "weaponHitTrigger": return S("Player Settings: On Weapon Hit", "Hit Player", "Source Player", false, 0);
                case "tickTrigger": return S("Player Settings: On Tick", "Player", "Source", true, TargetBits.Receiver);
                case "onDeathTrigger": return S("Player Settings: On Death", "Dying Player", "Damage Source", false, 0);
                case "onRespawnTrigger": return S("Player Settings: On Respawn", "Player", "Source", true, TargetBits.Receiver);
                case "onFirstSpawnTrigger": return S("Player Settings: On Round Start", "Player", "Source", true, TargetBits.Receiver);
                case "onRoundEndTrigger": return S("Player Settings: On Round End", "Player", "Source", true, TargetBits.Receiver);
                default: return null;
            }
        }

        /// <summary>Display Popup choice "On Select" (BoardPopupAction.DrawButtonDataUI).</summary>
        public static CallSite PopupChoice(int choiceIndex) =>
            S($"Display Popup Choice {choiceIndex + 1}: On Select", "This Board Space", "This Player", false, 0, PaletteGates.UsageBoardSpaceOnly);

        /// <summary>
        /// Why the inspector does not currently show a slot's "Edit" button (null = shown). <paramref name="read"/> returns
        /// component property values: TriggerOnHit/TriggerOnEnter/TriggerOnExit/TriggerOnStay (bool), TriggerShape and
        /// ItemType (enum names).
        /// </summary>
        public static string WhySlotHidden(string componentType, string property, Func<string, object> read)
        {
            if (componentType == "ModTrigger")
            {
                bool hit = Equals(read("TriggerOnHit"), true) || (read("TriggerShape") as string) == "CustomCollider";
                switch (property)
                {
                    case "OnHitActions":
                        return hit ? null : "the Trigger inspector shows 'On Hit Actions' only while TriggerOnHit is true (or TriggerShape is CustomCollider); set it with set_property first";
                    case "OnEnterActions":
                    case "OnExitActions":
                    case "OnStayActions":
                        string flag = "TriggerOn" + property.Substring(2, property.Length - 9);
                        if (hit)
                        {
                            return $"the Trigger inspector hides '{property}' while TriggerOnHit is true or TriggerShape is CustomCollider";
                        }
                        return Equals(read(flag), true) ? null : $"the Trigger inspector shows '{property}' only while {flag} is true; set it with set_property first";
                }
            }
            if (componentType == "ModItem")
            {
                string type = read("ItemType") as string;
                switch (property)
                {
                    case "OnWhileHeldTrigger":
                    case "OnDropTrigger":
                        return type != "Pickup" ? null : $"the Item inspector hides '{property}' for ItemType Pickup";
                    case "OnUseTrigger":
                        return type == "Carried" || type == "Weapon" ? null : "the Item inspector shows 'On Use' only for ItemType Carried or Weapon";
                }
            }
            return null;
        }
    }

    // ===================================================================== Logic trigger types

    /// <summary>ModLogicInspector's "Event" dropdown: ModTriggerType values minus the per-mod-type hide list.</summary>
    public static class LogicTriggerTypes
    {
        private static readonly int[] s_minigameHidden = { 0, 112 };          // None, BoardSpace
        private static readonly int[] s_boardHidden = { 0, 112, 64, 80, 128, 144 }; // + Hit, WeaponHit, RoundStart, RoundEnd

        public static int[] Hidden(string modType) => modType == "Board" ? s_boardHidden : s_minigameHidden;

        public static string WhyNotOffered(string typeName, int value, string modType)
        {
            if (Array.IndexOf(Hidden(modType), value) < 0)
            {
                return null;
            }
            if (value == 0)
            {
                return "None is not offered by the Logic component's Event dropdown";
            }
            if (value == 112)
            {
                return "BoardSpace triggers exist only on Board Space Event components; the Event dropdown does not offer them";
            }
            return $"the Event dropdown does not offer {typeName} in {modType} mods";
        }
    }

    // ===================================================================== target flags

    public static class TargetFlagRules
    {
        /// <summary>
        /// Effective hidden toggles (TargetAction.GetCalculatedHideFlags): the action's GetHideFlags, plus Source when the
        /// call site hides it.
        /// </summary>
        public static bool[] Hidden(bool[] actionHide, bool siteHideSource)
        {
            bool[] h = new bool[3];
            for (int i = 0; i < 3; i++)
            {
                h[i] = actionHide != null && actionHide.Length > i && actionHide[i];
            }
            if (siteHideSource)
            {
                h[0] = true;
            }
            return h;
        }

        /// <summary>
        /// Null if <paramref name="requested"/> is a value TargetAction.DrawToggleButtonSet can produce/keep. That method
        /// recomputes TargetFlags from the visible toggles on every drawn frame (hidden flags are dropped), and disables
        /// the remaining toggle when two are hidden (its state is then fixed).
        /// </summary>
        public static string Check(bool[] hidden, int current, int requested)
        {
            if ((requested & ~7) != 0)
            {
                return "target flags must be a combination of Source, Receiver, Advanced";
            }
            int hiddenCount = hidden.Count(b => b);
            for (int i = 0; i < 3; i++)
            {
                if (hidden[i] && (requested & TargetBits.Bit(i)) != 0)
                {
                    return $"the {TargetBits.Names[i]} target is not available here (the Action Editor hides that toggle for this action/trigger; it would be cleared as soon as the action is shown)";
                }
            }
            if (hiddenCount >= 2 && requested != (current & VisibleMask(hidden)))
            {
                return $"the Action Editor shows the only remaining target toggle disabled for this action/trigger, so the target flags cannot be changed (current: {TargetBits.Format(current)})";
            }
            return null;
        }

        public static int VisibleMask(bool[] hidden)
        {
            int m = 0;
            for (int i = 0; i < 3; i++)
            {
                if (!hidden[i])
                {
                    m |= TargetBits.Bit(i);
                }
            }
            return m;
        }

        /// <summary>Parses "Source, Advanced", ["Source","Advanced"], an integer bit mask, or "0"/"" for none.</summary>
        public static int Parse(JToken t, string path)
        {
            if (t == null || t.Type == JTokenType.Null)
            {
                throw RpcException.InvalidParams($"'{path}' must be flag names (\"Source\", \"Receiver\", \"Advanced\"), joined by ',' or as an array, or an integer 0-7");
            }
            if (t.Type == JTokenType.Integer)
            {
                long v = (long)t;
                if (v < 0 || v > 7)
                {
                    throw RpcException.InvalidParams($"'{path}': {v} is not a combination of Source=1, Receiver=2, Advanced=4");
                }
                return (int)v;
            }
            IEnumerable<string> parts;
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                if (s.Length == 0 || s == "0" || string.Equals(s, "None", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }
                parts = s.Split(',');
            }
            else if (t is JArray arr)
            {
                List<string> l = new List<string>();
                foreach (JToken e in arr)
                {
                    if (e.Type != JTokenType.String)
                    {
                        throw RpcException.InvalidParams($"'{path}': array elements must be flag names");
                    }
                    l.Add((string)e);
                }
                parts = l;
            }
            else
            {
                throw RpcException.InvalidParams($"'{path}' must be flag names (\"Source\", \"Receiver\", \"Advanced\"), joined by ',' or as an array, or an integer 0-7");
            }
            int flags = 0;
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                int idx = Array.FindIndex(TargetBits.Names, n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                {
                    throw RpcException.InvalidParams($"'{path}': '{p}' is not a target flag (Source, Receiver, Advanced)");
                }
                flags |= TargetBits.Bit(idx);
            }
            return flags;
        }
    }

    // ===================================================================== parameter names

    public static class ParamNames
    {
        /// <summary>
        /// Resolves a caller's parameter name against the serialized field names: exact, then with the "m_" prefix added
        /// ("seconds" -> "m_seconds"), then a unique case-insensitive match of either form. Null if none.
        /// </summary>
        public static string Resolve(string input, IList<string> fields)
        {
            if (fields.Contains(input))
            {
                return input;
            }
            if (fields.Contains("m_" + input))
            {
                return "m_" + input;
            }
            List<string> ci = fields.Where(f => string.Equals(f, input, StringComparison.OrdinalIgnoreCase) || string.Equals(f, "m_" + input, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ci.Count > 1)
            {
                throw RpcException.InvalidParams($"parameter '{input}' is ambiguous ({string.Join(", ", ci.ToArray())})");
            }
            return ci.Count == 1 ? ci[0] : null;
        }

        /// <summary>The name without the "m_" prefix (alias accepted on input).</summary>
        public static string Alias(string field) => field.StartsWith("m_", StringComparison.Ordinal) && field.Length > 2 ? field.Substring(2) : null;
    }

    // ===================================================================== parameter rules

    /// <summary>One rule for an action/trigger field (or a sub-field "Class.field.sub"), transcribed from the DrawInspector code.</summary>
    public sealed class ParamRule
    {
        /// <summary>Non-null: the field cannot be set (the editor does not let users edit it).</summary>
        public string Refusal;

        public double? Min;
        public double? Max;

        /// <summary>Mathf.Abs before the clamp (PlayTweenAnimation time, Give/Drop Keys).</summary>
        public bool Abs;

        /// <summary>Enum values the inspector's dropdown does not offer.</summary>
        public long[] ExcludedEnumValues;

        /// <summary>Public property the inspector writes through (wrapping a ModAssetReference); null = the field itself.</summary>
        public string WrapperProperty;

        /// <summary>Struct/class fields: the sub-fields the inspector draws (others refused).</summary>
        public string[] Subfields;

        public ComplexClamp Complex = ComplexClamp.None;

        public string Note;
    }

    public enum ComplexClamp
    {
        None,

        /// <summary>WaitAction: min = Clamp(min, 0, 3600); max = Clamp(max, min, 3600) every drawn frame.</summary>
        Wait,

        /// <summary>TimerTrigger: min = Max(0.1, min) every drawn frame.</summary>
        Timer,
    }

    public static class ActionParamRules
    {
        private static ParamRule R(double? min, double? max, string note = null) => new ParamRule { Min = min, Max = max, Note = note };

        private static readonly Dictionary<string, ParamRule> s_rules = new Dictionary<string, ParamRule>(StringComparer.Ordinal)
        {
            // ---- actions (DrawInspector of each class)
            ["WaitAction.m_seconds"] = new ParamRule { Complex = ComplexClamp.Wait, Note = "min clamped to 0..3600, max clamped to min..3600 (WaitAction.DrawInspector)" },
            ["PlayTweenAnimationAction.m_time"] = new ParamRule { Min = 0, Max = 3600, Abs = true, Note = "Clamp(Abs(time), 0, 3600)" },
            ["PlayTweenAnimationAction.m_easeType"] = new ParamRule { ExcludedEnumValues = new long[] { 0, 35, 36, 37, 38 }, Note = "notUsed, punch, once, clamp, pingPong are not offered" },
            ["PlayTweenAnimationAction.m_startValueType"] = new ParamRule { Note = "ignored (drawn disabled as Current) for the RotateAround* tween types" },
            ["PlayTweenAnimationAction.m_endValueType"] = new ParamRule { Note = "ignored (drawn disabled as Offset) for the RotateAround* tween types" },
            ["SetGravityAction.m_value"] = new ParamRule { Min = -1000, Max = 1000, Note = "each axis clamped to -1000..1000" },
            ["ModifyPlayerAction.m_movementSpeed"] = R(0, 100, "clamped 0..100; used when m_playerActionType is MovementSpeedMultiplier"),
            ["ModifyPlayerAction.m_gravity"] = new ParamRule { Min = -1000, Max = 1000, Note = "each axis clamped to -1000..1000; used when m_playerActionType is Gravity" },
            ["PlaySoundAction.m_volume"] = R(0, 1, "slider 0..1"),
            ["PlaySoundAction.m_clip"] = new ParamRule { Note = "AudioClip asset (list_assets type AudioClip)" },
            ["SpawnPrefabAction.m_prefabs"] = new ParamRule { WrapperProperty = "Prefabs", Note = "array of Prefab assets of the mod (written through the Prefabs property)" },
            ["SpawnPrefabAction.m_parentToTarget"] = new ParamRule { Note = "shown only while at least one target flag is set" },
            ["SpawnPrefabAction.m_targetPositionOffset"] = new ParamRule { Note = "shown only while at least one target flag is set" },
            ["SpawnPrefabAction.m_targetRotationOffset"] = new ParamRule { Note = "shown only while at least one target flag is set" },
            ["SpawnPrefabAction.m_position"] = new ParamRule { Note = "used when m_spawnAtPosition is true" },
            ["SpawnPrefabAction.m_rotation"] = new ParamRule { Note = "used when m_spawnAtPosition is true" },
            ["ChangeVelocityAction.m_localTarget"] = new ParamRule { Note = "used when m_space is Local; labels in the editor follow the call site's Source/Receiver names" },
            ["ChangeVelocityAction.m_customDir"] = new ParamRule { Note = "used when m_velocityDir is Custom" },
            ["ShowMessageAction.m_duration"] = R(0.5, 1200, "clamped 0.5..1200"),
            ["ShowMessageAction.m_messageTarget"] = new ParamRule { Note = "the target toggles are shown only while this includes Target" },
            ["GiveMinigameItemAction.m_item"] = new ParamRule { WrapperProperty = "ItemPrefab", Note = "Prefab asset whose root object has an Item component (written through the ItemPrefab property)" },
            ["StunPlayerAction.m_stunDuration"] = R(0, 1000, "clamped 0..1000"),
            ["DropMinigameItemAction.m_itemSlot"] = new ParamRule { ExcludedEnumValues = new long[] { 0 }, Note = "DefaultForItemType is not offered; used when m_dropAllItems is false" },
            ["MatchTransformAction.m_transformToMatch"] = new ParamRule { WrapperProperty = "TransformToMatch", Note = "object reference (Transform), written through the TransformToMatch property" },
            ["SetPlayerVisualAction.m_prefab"] = new ParamRule { WrapperProperty = "Prefab", Note = "Prefab asset of the mod, or null for the default character" },
            ["SetPlayerVisualAction.m_recolorPrefabUsingPlayerColor"] = new ParamRule { Note = "editable only while a prefab is set" },
            ["SetPlayerVisualAction.m_verticalLookTargetChildIndex"] = new ParamRule { Note = "-1 (None) or a child index of the prefab's root object; not equal to m_movementRotationTargetChildIndex" },
            ["SetPlayerVisualAction.m_movementRotationTargetChildIndex"] = new ParamRule { Note = "-1 (None) or a child index of the prefab's root object; not equal to m_verticalLookTargetChildIndex" },
            ["HealPlayerAction.m_value"] = R(1, 30, "slider 1..30"),
            ["DamagePlayerAction.m_damageInstance"] = new ParamRule
            {
                Subfields = new[] { "damage", "ragdoll", "ragdollVel", "hitAnim", "removeKeys", "blood", "bloodVel", "bloodAmount", "sound", "volume" },
                Note = "object with the drawn fields only (partial updates allowed): damage 1..30, ragdoll, ragdollVel 0..20, hitAnim, removeKeys, blood, bloodVel 0..20, bloodAmount 0..4, sound, volume 0..1",
            },
            ["DamagePlayerAction.m_damageInstance.damage"] = R(1, 30),
            ["DamagePlayerAction.m_damageInstance.ragdollVel"] = R(0, 20),
            ["DamagePlayerAction.m_damageInstance.bloodVel"] = R(0, 20),
            ["DamagePlayerAction.m_damageInstance.bloodAmount"] = R(0, 4),
            ["DamagePlayerAction.m_damageInstance.volume"] = R(0, 1),
            ["GiveKeysAction.m_value"] = new ParamRule { Min = 1, Max = 200, Abs = true, Note = "Clamp(Abs(value), 1, 200)" },
            ["DropKeysAction.m_keys"] = new ParamRule { Min = 1, Max = 200, Abs = true, Note = "Clamp(Abs(keys), 1, 200)" },
            ["GiveTrophyAction.m_trophiesToGive"] = R(1, 20, "slider 1..20"),
            ["RemoveTrophyAction.m_trophiesToRemove"] = R(1, 20, "slider 1..20"),
            ["GiveItemAction.m_item"] = new ParamRule { Refusal = "obsolete field (only read when upgrading version-0 data); set m_items instead" },
            ["GiveItemAction.m_items"] = new ParamRule { Note = "flags; the bridge first runs the action's own version upgrade, as the editor does when it draws the action" },
            ["SpawnKeysAction.m_keys"] = R(1, 40, "clamped 1..40"),
            ["MoveSpaceAction.m_spaces"] = new ParamRule { WrapperProperty = "Spaces", Note = "array of board space objects (BoardNode), written through the Spaces property" },
            ["SetConnectionActiveAction.m_connectionMappings"] = new ParamRule { Refusal = "connection mappings are built by the Action Editor from the targets' board connections; the bridge cannot set them (edit them in the Action Editor)" },
            ["BoardPopupAction.m_speakerImageReference"] = new ParamRule { WrapperProperty = "SpeakerImage", Note = "Texture asset (written through the SpeakerImage property)" },
            ["BoardPopupAction.m_choiceCount"] = new ParamRule { Min = 0, Max = 4, Note = "slider 0..4; raising it adds empty choices to m_choices, as the inspector does" },
            ["BoardPopupAction.m_choices"] = new ParamRule
            {
                Subfields = new[] { "m_buttonText", "m_costType", "m_costAmount", "m_itemCostType", "m_choiceIconReference" },
                Note = "array patch: element i updates choice i (at most m_choiceCount elements; given keys only): m_buttonText, m_costType, m_costAmount (Health 1..29, other costs 1..1000), m_itemCostType, m_choiceIconReference (Texture). Edit a choice's actions with path [{action, choice}]",
            },
            ["BoardPopupAction.m_showCloseButton"] = new ParamRule { Note = "editable only while at least one shown choice has no cost (otherwise the close button is forced on)" },
            ["BoardPopupAction.m_isTimed"] = new ParamRule { Refusal = "not shown in the Action Editor" },
            ["BoardPopupAction.m_timedDuration"] = new ParamRule { Refusal = "not shown in the Action Editor" },

            // ---- triggers (DrawInspector of each trigger class)
            ["TimerTrigger.m_interval"] = new ParamRule { Complex = ComplexClamp.Timer, Note = "min clamped to >= 0.1 (TimerTrigger.DrawInspector)" },
            ["HitTrigger.m_hitType"] = new ParamRule { Note = "flags CollisionEnter, CollisionStay, CollisionExit" },
            ["WeaponHitTrigger.m_hitType"] = new ParamRule { Note = "flags Melee, Projectile" },
            ["BoardSpaceTrigger.m_hitType"] = new ParamRule { Refusal = "not shown in the editor (board space triggers have no parameters in the inspector)" },
        };

        public static ParamRule Get(string cls, string field) => s_rules.TryGetValue(cls + "." + field, out ParamRule r) ? r : null;

        public static IEnumerable<string> Keys => s_rules.Keys;

        /// <summary>Applies Abs/Min/Max in place to a parsed number, integer or vector (per axis). Returns true if it changed.</summary>
        public static bool ApplyClamp(ParamRule r, PValue v)
        {
            if (r == null || (!r.Min.HasValue && !r.Max.HasValue && !r.Abs))
            {
                return false;
            }
            switch (v)
            {
                case PNumber n:
                {
                    double before = n.Value;
                    n.Value = ClampD(r, n.Value);
                    return n.Value != before;
                }
                case PInteger i:
                {
                    decimal before = i.Value;
                    decimal x = r.Abs ? Math.Abs(i.Value) : i.Value;
                    if (r.Min.HasValue && x < (decimal)r.Min.Value) x = (decimal)Math.Ceiling(r.Min.Value);
                    if (r.Max.HasValue && x > (decimal)r.Max.Value) x = (decimal)Math.Floor(r.Max.Value);
                    i.Value = x;
                    return x != before;
                }
                case PFloats f:
                {
                    bool changed = false;
                    for (int k = 0; k < f.Values.Length; k++)
                    {
                        float before = f.Values[k];
                        f.Values[k] = (float)ClampD(r, f.Values[k]);
                        changed |= f.Values[k] != before;
                    }
                    return changed;
                }
            }
            return false;
        }

        private static double ClampD(ParamRule r, double v)
        {
            if (r.Abs) v = Math.Abs(v);
            if (r.Min.HasValue && v < r.Min.Value) v = r.Min.Value;
            if (r.Max.HasValue && v > r.Max.Value) v = r.Max.Value;
            return v;
        }

        /// <summary>Null if allowed; else the refusal for an enum value the dropdown does not offer.</summary>
        public static string CheckEnum(ParamRule r, long value, string valueName)
        {
            if (r?.ExcludedEnumValues != null && Array.IndexOf(r.ExcludedEnumValues, value) >= 0)
            {
                return $"{valueName} is not offered by the Action Editor here";
            }
            return null;
        }

        /// <summary>BoardPopupAction choice cost clamp (DrawButtonDataUI): Health 1..29, other costs 1..1000, None: not drawn.</summary>
        public static decimal ClampChoiceCost(string costType, decimal amount, out bool clamped)
        {
            decimal v = amount;
            if (costType == "Health")
            {
                v = Math.Min(29, Math.Max(1, v));
            }
            else if (costType != "None")
            {
                v = Math.Min(1000, Math.Max(1, v));
            }
            clamped = v != amount;
            return v;
        }
    }

    // ===================================================================== complex values

    /// <summary>A ModComplexFloat / ModComplexInt patch: {valueType?, min?, max?} or a bare number (= Constant, min).</summary>
    public sealed class PComplex : PValue
    {
        public string ValueType;
        public double? Min;
        public double? Max;
    }

    public sealed class ComplexValue
    {
        public string ValueType;
        public double Min;
        public double Max;
    }

    public static class ComplexValues
    {
        public static readonly string[] Types = { "Constant", "RandomBetweenTwoValues" };

        public static PComplex Parse(JToken t, string path, bool integer)
        {
            string expect = integer
                ? "an integer (= Constant) or {valueType: Constant|RandomBetweenTwoValues, min, max} (integers; keys optional)"
                : "a number (= Constant) or {valueType: Constant|RandomBetweenTwoValues, min, max} (keys optional)";
            if (t == null || t.Type == JTokenType.Null)
            {
                throw RpcException.InvalidParams($"'{path}': expected {expect}");
            }
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            {
                return new PComplex { ValueType = "Constant", Min = Num(t, path, integer) };
            }
            if (!(t is JObject o))
            {
                throw RpcException.InvalidParams($"'{path}': expected {expect}");
            }
            PComplex c = new PComplex();
            foreach (JProperty p in o.Properties())
            {
                switch (p.Name)
                {
                    case "valueType":
                        if (p.Value.Type == JTokenType.Integer && ((long)p.Value == 0 || (long)p.Value == 1))
                        {
                            c.ValueType = Types[(long)p.Value];
                            break;
                        }
                        string s = p.Value.Type == JTokenType.String ? ((string)p.Value).Trim() : null;
                        c.ValueType = Types.FirstOrDefault(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))
                            ?? throw RpcException.InvalidParams($"'{path}.valueType' must be Constant or RandomBetweenTwoValues");
                        break;
                    case "min":
                        c.Min = Num(p.Value, path + ".min", integer);
                        break;
                    case "max":
                        c.Max = Num(p.Value, path + ".max", integer);
                        break;
                    default:
                        throw RpcException.InvalidParams($"'{path}': unknown key '{p.Name}'; expected {expect}");
                }
            }
            return c;
        }

        private static double Num(JToken t, string path, bool integer)
        {
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)
            {
                throw RpcException.InvalidParams($"'{path}' must be a number");
            }
            double d = (double)t;
            if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > float.MaxValue)
            {
                throw RpcException.InvalidParams($"'{path}' must be a finite 32-bit number");
            }
            if (integer && (Math.Floor(d) != d || d < int.MinValue || d > int.MaxValue))
            {
                throw RpcException.InvalidParams($"'{path}' must be a 32-bit integer");
            }
            return d;
        }

        /// <summary>
        /// Merges a patch into the current value, applies the inspector's clamp and checks the random range.
        /// <paramref name="clamped"/> lists the parts whose value differs from the request.
        /// </summary>
        public static ComplexValue Merge(ComplexValue current, PComplex patch, ComplexClamp clamp, string path, List<string> clamped)
        {
            ComplexValue r = new ComplexValue
            {
                ValueType = patch.ValueType ?? current.ValueType,
                Min = patch.Min ?? current.Min,
                Max = patch.Max ?? current.Max,
            };
            double reqMin = r.Min, reqMax = r.Max;
            switch (clamp)
            {
                case ComplexClamp.Wait:
                    r.Min = Math.Min(3600, Math.Max(0, r.Min));
                    r.Max = Math.Min(3600, Math.Max(r.Min, r.Max));
                    break;
                case ComplexClamp.Timer:
                    r.Min = Math.Max(0.1, r.Min);
                    break;
            }
            // Reported whenever the stored part differs from the requested-or-kept value (e.g. Wait raises max to min).
            if (r.Min != reqMin)
            {
                clamped.Add(path + ".min");
            }
            if (r.Max != reqMax)
            {
                clamped.Add(path + ".max");
            }
            if (r.ValueType == "RandomBetweenTwoValues" && r.Min > r.Max)
            {
                throw RpcException.InvalidParams($"'{path}': min ({r.Min.ToString(CultureInfo.InvariantCulture)}) must not be greater than max ({r.Max.ToString(CultureInfo.InvariantCulture)}) for RandomBetweenTwoValues (the editor clamps them on edit)");
            }
            return r;
        }
    }
}
