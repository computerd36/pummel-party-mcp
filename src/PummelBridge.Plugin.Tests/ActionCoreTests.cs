using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;

namespace PummelBridge.Tests
{
    /// <summary>Phase 2 increment 3: trigger addressing, action list rules, palette gates, call sites, target flags, parameter rules, complex values.</summary>
    internal static class ActionCoreTests
    {
        private static Action<string, bool> s_check;

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            AddressTests();
            ListOpsTests();
            GateTests();
            CallSiteTests();
            TargetFlagTests();
            ParamNameTests();
            ParamRuleTests();
            ComplexTests();
            FakeSchemaTests();
        }

        private static void Check(string name, bool ok) => s_check(name, ok);

        private static string Code(Action a)
        {
            try
            {
                a();
                return null;
            }
            catch (RpcException e)
            {
                return e.Code;
            }
            catch (Exception e)
            {
                return "EXC:" + e.GetType().Name;
            }
        }

        private static string Msg(Action a)
        {
            try
            {
                a();
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        private static string Norm(string g, string name)
        {
            if (!Guid.TryParse(g, out Guid x))
            {
                throw RpcException.InvalidParams($"'{name}' is not a valid GUID");
            }
            return x.ToString();
        }

        private static TriggerAddress A(string json, bool requireTrigger = true) => TriggerAddress.Parse(JObject.Parse(json), Norm, requireTrigger);

        private static bool Bad(Action a) => Code(a) == ErrorCodes.InvalidParams;

        // ================================================================== addressing

        private static void AddressTests()
        {
            Console.WriteLine("-- unit: trigger addressing");
            const string g = "6F1C0000-1111-2222-3333-444455556666";
            TriggerAddress a = A("{\"guid\":\"" + g + "\",\"component\":\"Logic\",\"trigger\":\"Triggers[2]\"}");
            Check("object address: guid normalised, slot parsed", a.Guid == g.ToLowerInvariant() && a.Slot.Property == "Triggers" && a.Slot.Index == 2 && a.Component == "Logic" && !a.IsPlayerSettings);
            TriggerAddress b = A("{\"guid\":\"" + g + "\",\"trigger\":\"OnEnterActions\"}");
            Check("fixed slot name has index -1", b.Slot.Property == "OnEnterActions" && b.Slot.Index == -1 && b.Component == null);
            TriggerAddress s = A("{\"scope\":\"playerSettings\",\"trigger\":\"onDeathTrigger\"}");
            Check("playerSettings scope", s.IsPlayerSettings && s.Slot.Property == "onDeathTrigger" && s.Guid == null);
            Check("scope is case-insensitive", A("{\"scope\":\"PLAYERSETTINGS\",\"trigger\":\"tickTrigger\"}").IsPlayerSettings);
            Check("playerSettings refuses guid/component", Bad(() => A("{\"scope\":\"playerSettings\",\"guid\":\"" + g + "\",\"trigger\":\"onDeathTrigger\"}")));
            Check("playerSettings refuses an index", Bad(() => A("{\"scope\":\"playerSettings\",\"trigger\":\"tickTrigger[0]\"}")));
            Check("unknown scope refused", Bad(() => A("{\"scope\":\"world\",\"trigger\":\"x\"}")));
            Check("guid required for object scope", Bad(() => A("{\"trigger\":\"Triggers[0]\"}")));
            Check("malformed guid refused", Bad(() => A("{\"guid\":\"nope\",\"trigger\":\"Triggers[0]\"}")));
            Check("trigger required when requested", Bad(() => A("{\"guid\":\"" + g + "\"}")));
            Check("trigger optional for listing", A("{\"guid\":\"" + g + "\"}", requireTrigger: false).Trigger == null);
            Check("malformed trigger names refused", Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"Triggers[x]\"}"))
                && Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"Triggers[-1]\"}"))
                && Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"[0]\"}"))
                && Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"On Enter\"}"))
                && Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"Triggers[0\"}")));
            Check("non-string trigger refused", Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":5}")));

            TriggerAddress p = A("{\"guid\":\"" + g + "\",\"trigger\":\"OnEnterTrigger\",\"path\":[{\"action\":1,\"choice\":0},{\"action\":0,\"choice\":3}]}");
            Check("nested path parsed", p.Path.Count == 2 && p.Path[0].Action == 1 && p.Path[0].Choice == 0 && p.Path[1].Choice == 3);
            Check("describe includes path", p.Describe().Contains("popup action 1 choice 0") && p.Describe().Contains("choice 3"));
            Check("path: negative / missing / non-integer / unknown key refused",
                Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"T\",\"path\":[{\"action\":-1,\"choice\":0}]}"))
                && Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"T\",\"path\":[{\"action\":1}]}"))
                && Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"T\",\"path\":[{\"action\":1.5,\"choice\":0}]}"))
                && Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"T\",\"path\":[{\"action\":1,\"choice\":0,\"x\":1}]}"))
                && Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"T\",\"path\":{}}")));
            string deep = "[" + string.Join(",", Enumerable.Repeat("{\"action\":0,\"choice\":0}", TriggerAddress.MaxPathDepth + 1)) + "]";
            Check("path depth limit", Bad(() => A("{\"guid\":\"" + g + "\",\"trigger\":\"T\",\"path\":" + deep + "}")));
            Check("empty path ok", A("{\"guid\":\"" + g + "\",\"trigger\":\"T\",\"path\":[]}").Path.Count == 0);
        }

        // ================================================================== list ops

        private static void ListOpsTests()
        {
            Console.WriteLine("-- unit: action list index rules");
            Check("omitted index = append", ActionListOps.ResolveInsertIndex(null, 3, out bool ap1) == 3 && ap1);
            Check("index == count = append", ActionListOps.ResolveInsertIndex(3, 3, out bool ap2) == 3 && ap2);
            Check("index 0 = insert", ActionListOps.ResolveInsertIndex(0, 3, out bool ap3) == 0 && !ap3);
            Check("empty list: 0 = append", ActionListOps.ResolveInsertIndex(0, 0, out bool ap4) == 0 && ap4);
            Check("index > count refused", Bad(() => ActionListOps.ResolveInsertIndex(4, 3, out _)));
            Check("negative refused", Bad(() => ActionListOps.ResolveInsertIndex(-1, 3, out _)));
            Check("insert message gives range", Msg(() => ActionListOps.ResolveInsertIndex(9, 2, out _)).Contains("between 0 and 2"));
            Check("CheckIndex bounds", Code(() => ActionListOps.CheckIndex(2, 3, "index")) == null && Bad(() => ActionListOps.CheckIndex(3, 3, "index")) && Bad(() => ActionListOps.CheckIndex(-1, 3, "index")));
            Check("CheckIndex on empty list", Msg(() => ActionListOps.CheckIndex(0, 0, "index")).Contains("empty"));

            string[] l = { "a", "b", "c", "d" };
            Check("insert middle", string.Join("", ActionListOps.Insert(l, 1, "x")) == "axbcd");
            Check("insert end", string.Join("", ActionListOps.Insert(l, 4, "x")) == "abcdx");
            Check("insert into null", string.Join("", ActionListOps.Insert<string>(null, 0, "x")) == "x");
            Check("remove", string.Join("", ActionListOps.RemoveAt(l, 0)) == "bcd" && string.Join("", ActionListOps.RemoveAt(l, 3)) == "abc");
            Check("move down", string.Join("", ActionListOps.Move(l, 0, 2)) == "bcad");
            Check("move up", string.Join("", ActionListOps.Move(l, 3, 1)) == "adbc");
            Check("move same index keeps order", string.Join("", ActionListOps.Move(l, 2, 2)) == "abcd");
            Check("ops do not mutate the input", string.Join("", l) == "abcd");
            // Move equals successive adjacent swaps (the window's drag over the neighbours).
            string[] sw = (string[])l.Clone();
            for (int i = 0; i < 2; i++)
            {
                string t = sw[i]; sw[i] = sw[i + 1]; sw[i + 1] = t;
            }
            Check("move == adjacent swaps while dragging", string.Join("", sw) == string.Join("", ActionListOps.Move(l, 0, 2)));
        }

        // ================================================================== palette gates

        private static void GateTests()
        {
            Console.WriteLine("-- unit: palette gates");
            Check("mod type flags", PaletteGates.ModTypeFlag("Minigame") == 1 && PaletteGates.ModTypeFlag("Board") == 2 && PaletteGates.ModTypeFlag("Item") == 4 && PaletteGates.ModTypeFlag("BoardSpace") == 8 && PaletteGates.ModTypeFlag("x") == 0);
            const int all = 15;
            Check("All actions usable everywhere (gate 1)", PaletteGates.WhyNotUsable("Wait", all, 0, "Minigame", 0) == null && PaletteGates.WhyNotUsable("Wait", all, 0, "Item", null) == null);
            Check("minigame action refused in a board mod", PaletteGates.WhyNotUsable("Change Health", 1, 0, "Board", 0).Contains("Minigame"));
            Check("board action refused in a minigame mod", PaletteGates.WhyNotUsable("Heal Player", 2, 2, "Minigame", 2) != null);
            Check("board action refused on a Logic list in a board mod (gate 2)", PaletteGates.WhyNotUsable("Heal Player", 2, 2, "Board", 0, "Logic: On Start").Contains("Board Space Event"));
            Check("board action allowed at a BoardSpaceOnly site", PaletteGates.WhyNotUsable("Heal Player", 2, 2, "Board", 2) == null);
            Check("no call site: gate 2 skipped", PaletteGates.WhyNotUsable("Heal Player", 2, 2, "Board", null) == null);
            Check("usage None always satisfied", PaletteGates.UsageGate(0, 0) && PaletteGates.UsageGate(0, 2));
            Check("unknown mod type never usable", PaletteGates.WhyNotUsable("Wait", all, 0, "Weird", 0) != null);
            Check("mod type names", string.Join("/", PaletteGates.ModTypeNames(3)) == "Minigame/Board");
        }

        // ================================================================== call sites

        private static void CallSiteTests()
        {
            Console.WriteLine("-- unit: call sites and slot visibility");
            CallSite start = CallSites.ForSlot("ModLogic", "Triggers", "Start");
            Check("Logic Start hides Source, no default targets", start.HideSource && start.DefaultTargets == 0 && start.ReceiverName == "This Object" && start.SourceName == "Activator");
            Check("Logic Hit / WeaponHit show Source", !CallSites.ForSlot("ModLogic", "Triggers", "Hit").HideSource && !CallSites.ForSlot("ModLogic", "Triggers", "WeaponHit").HideSource);
            Check("Trigger Enter/Exit/Stay default Source, Hit none",
                CallSites.ForSlot("ModTrigger", "OnEnterActions", null).DefaultTargets == TargetBits.Source
                && CallSites.ForSlot("ModTrigger", "OnStayActions", null).DefaultTargets == TargetBits.Source
                && CallSites.ForSlot("ModTrigger", "OnHitActions", null).DefaultTargets == 0);
            Check("Board Space Event is BoardSpaceOnly", CallSites.ForSlot("ModBoardSpaceEvent", "OnPassTrigger", null).UsageFlags == PaletteGates.UsageBoardSpaceOnly);
            Check("popup choice is BoardSpaceOnly", CallSites.PopupChoice(1).UsageFlags == PaletteGates.UsageBoardSpaceOnly && CallSites.PopupChoice(1).Label.Contains("Choice 2"));
            Check("generic component slot = ModEditorGui defaults", CallSites.ForSlot("ModFoo", "Bar", null).ReceiverName == "Receiver" && !CallSites.ForSlot("ModFoo", "Bar", null).HideSource);
            Check("player settings: tick hides Source + default Receiver", CallSites.ForPlayerSetting("tickTrigger").HideSource && CallSites.ForPlayerSetting("tickTrigger").DefaultTargets == TargetBits.Receiver);
            Check("player settings: death shows Source, no default", !CallSites.ForPlayerSetting("onDeathTrigger").HideSource && CallSites.ForPlayerSetting("onDeathTrigger").DefaultTargets == 0);
            Check("player settings: hitTrigger not shown by the editor", CallSites.ForPlayerSetting("hitTrigger") == null);
            Check("player-settings list has six entries, all with call sites", CallSites.PlayerSettingTriggers.Length == 6 && CallSites.PlayerSettingTriggers.All(f => CallSites.ForPlayerSetting(f) != null));
            JObject j = CallSites.ForSlot("ModTrigger", "OnEnterActions", null).ToJson();
            Check("call site json", (string)j["appendDefaultTargetFlags"] == "Source" && (bool)j["boardSpaceOnlyActions"] == false);

            Dictionary<string, object> trig = new Dictionary<string, object> { ["TriggerOnHit"] = false, ["TriggerShape"] = "Box", ["TriggerOnEnter"] = true, ["TriggerOnExit"] = false, ["TriggerOnStay"] = false };
            Func<string, object> rd = n => trig.TryGetValue(n, out object v) ? v : null;
            Check("trigger: Enter shown when TriggerOnEnter", CallSites.WhySlotHidden("ModTrigger", "OnEnterActions", rd) == null);
            Check("trigger: Exit hidden when TriggerOnExit false", CallSites.WhySlotHidden("ModTrigger", "OnExitActions", rd).Contains("TriggerOnExit"));
            Check("trigger: Hit hidden while TriggerOnHit false", CallSites.WhySlotHidden("ModTrigger", "OnHitActions", rd) != null);
            trig["TriggerShape"] = "CustomCollider";
            Check("trigger: CustomCollider shows Hit, hides Enter", CallSites.WhySlotHidden("ModTrigger", "OnHitActions", rd) == null && CallSites.WhySlotHidden("ModTrigger", "OnEnterActions", rd) != null);
            trig["TriggerShape"] = "Box";
            trig["TriggerOnHit"] = true;
            Check("trigger: TriggerOnHit hides Enter", CallSites.WhySlotHidden("ModTrigger", "OnEnterActions", rd) != null);

            Func<string, object> item(string type) => n => n == "ItemType" ? type : null;
            Check("item: pickup shows only On Pickup", CallSites.WhySlotHidden("ModItem", "OnPickupTrigger", item("Pickup")) == null
                && CallSites.WhySlotHidden("ModItem", "OnDropTrigger", item("Pickup")) != null
                && CallSites.WhySlotHidden("ModItem", "OnWhileHeldTrigger", item("Pickup")) != null
                && CallSites.WhySlotHidden("ModItem", "OnUseTrigger", item("Pickup")) != null);
            Check("item: Use only for Carried/Weapon", CallSites.WhySlotHidden("ModItem", "OnUseTrigger", item("Carried")) == null
                && CallSites.WhySlotHidden("ModItem", "OnUseTrigger", item("Weapon")) == null
                && CallSites.WhySlotHidden("ModItem", "OnUseTrigger", item("Worn")) != null
                && CallSites.WhySlotHidden("ModItem", "OnDropTrigger", item("Worn")) == null);
            Check("other components: always shown", CallSites.WhySlotHidden("ModLogic", "Triggers", _ => null) == null);

            Check("Logic dropdown: minigame hides None/BoardSpace only", LogicTriggerTypes.WhyNotOffered("Hit", "Minigame") == null
                && LogicTriggerTypes.WhyNotOffered("None", "Minigame") != null && LogicTriggerTypes.WhyNotOffered("BoardSpace", "Minigame") != null);
            Check("Logic dropdown: board hides Hit/WeaponHit/RoundStart/RoundEnd", new[] { "Hit", "WeaponHit", "RoundStart", "RoundEnd" }.All(v => LogicTriggerTypes.WhyNotOffered(v, "Board") != null)
                && LogicTriggerTypes.WhyNotOffered("Timer", "Board") == null);
        }

        // ================================================================== target flags

        private static void TargetFlagTests()
        {
            Console.WriteLine("-- unit: target flags");
            Func<string, int> P = s => TargetFlagRules.Parse(JToken.Parse(s), "targetFlags");
            Check("parse names", P("\"Source, Advanced\"") == 5 && P("\"receiver\"") == 2);
            Check("parse array", P("[\"Source\",\"Receiver\"]") == 3);
            Check("parse integer / none", P("6") == 6 && P("0") == 0 && P("\"0\"") == 0 && P("\"\"") == 0 && P("\"None\"") == 0);
            Check("parse refuses junk", Bad(() => P("8")) && Bad(() => P("-1")) && Bad(() => P("\"Self\"")) && Bad(() => P("true")) && Bad(() => P("[1]")) && Bad(() => TargetFlagRules.Parse(null, "t")));

            bool[] none = TargetFlagRules.Hidden(new bool[3], false);
            Check("nothing hidden: any combination", TargetFlagRules.Check(none, 2, 7) == null && TargetFlagRules.Check(none, 2, 0) == null);
            bool[] srcHidden = TargetFlagRules.Hidden(new bool[3], true);
            Check("site hides Source: Source refused", TargetFlagRules.Check(srcHidden, 2, 3).Contains("Source") && TargetFlagRules.Check(srcHidden, 2, 6) == null);
            bool[] advHidden = TargetFlagRules.Hidden(new[] { false, false, true }, false);
            Check("action hides Advanced: Advanced refused", TargetFlagRules.Check(advHidden, 2, 4) != null && TargetFlagRules.Check(advHidden, 2, 1) == null);
            bool[] two = TargetFlagRules.Hidden(new[] { false, false, true }, true);
            Check("two hidden: remaining toggle locked to current", TargetFlagRules.Check(two, 2, 2) == null && TargetFlagRules.Check(two, 2, 0).Contains("disabled"));
            bool[] popup = TargetFlagRules.Hidden(new[] { false, true, true }, false);
            Check("popup (Receiver+Advanced hidden): Source locked", TargetFlagRules.Check(popup, 1, 1) == null && TargetFlagRules.Check(popup, 1, 0) != null && TargetFlagRules.Check(popup, 1, 2) != null);
            Check("visible mask", TargetFlagRules.VisibleMask(srcHidden) == 6 && TargetFlagRules.VisibleMask(none) == 7);
            Check("format", TargetBits.Format(5) == "Source, Advanced" && TargetBits.Format(0) == "0");
        }

        // ================================================================== names

        private static void ParamNameTests()
        {
            Console.WriteLine("-- unit: parameter names");
            List<string> f = new List<string> { "m_operation", "m_value", "m_Speed" };
            Check("exact", ParamNames.Resolve("m_value", f) == "m_value");
            Check("alias without m_", ParamNames.Resolve("value", f) == "m_value");
            Check("case-insensitive", ParamNames.Resolve("OPERATION", f) == "m_operation" && ParamNames.Resolve("speed", f) == "m_Speed");
            Check("unknown -> null", ParamNames.Resolve("nope", f) == null);
            Check("ambiguous refused", Bad(() => ParamNames.Resolve("x", new List<string> { "m_X", "m_x2", "M_x" })));
            Check("alias helper", ParamNames.Alias("m_seconds") == "seconds" && ParamNames.Alias("damage") == null);
        }

        // ================================================================== rules

        private static void ParamRuleTests()
        {
            Console.WriteLine("-- unit: parameter rules (inspector clamps)");
            PNumber n(double v) => new PNumber { Value = v };
            PInteger i(decimal v) => new PInteger { Value = v };
            ParamRule vol = ActionParamRules.Get("PlaySoundAction", "m_volume");
            PNumber a = n(1.5);
            Check("volume clamped to 1", ActionParamRules.ApplyClamp(vol, a) && a.Value == 1);
            PNumber b = n(0.5);
            Check("in-range value unchanged", !ActionParamRules.ApplyClamp(vol, b) && b.Value == 0.5);
            ParamRule time = ActionParamRules.Get("PlayTweenAnimationAction", "m_time");
            PNumber t = n(-2);
            Check("tween time: Abs then clamp", ActionParamRules.ApplyClamp(time, t) && t.Value == 2);
            PNumber t2 = n(5000);
            Check("tween time max 3600", ActionParamRules.ApplyClamp(time, t2) && t2.Value == 3600);
            ParamRule keys = ActionParamRules.Get("GiveKeysAction", "m_value");
            PInteger k = i(-5);
            Check("give keys: Abs(-5) = 5", ActionParamRules.ApplyClamp(keys, k) && k.Value == 5);
            PInteger k0 = i(0);
            Check("give keys: 0 -> 1", ActionParamRules.ApplyClamp(keys, k0) && k0.Value == 1);
            ParamRule grav = ActionParamRules.Get("SetGravityAction", "m_value");
            PFloats g = new PFloats { Values = new[] { 0f, -2000f, 5f } };
            Check("gravity per-axis clamp", ActionParamRules.ApplyClamp(grav, g) && g.Values[1] == -1000f && g.Values[2] == 5f);
            Check("heal slider 1..30", ActionParamRules.Get("HealPlayerAction", "m_value").Min == 1 && ActionParamRules.Get("HealPlayerAction", "m_value").Max == 30);
            Check("damage sub-field rule", ActionParamRules.Get("DamagePlayerAction", "m_damageInstance.damage").Max == 30 && ActionParamRules.Get("DamagePlayerAction", "m_damageInstance").Subfields.Contains("volume"));
            Check("no rule -> no clamp", !ActionParamRules.ApplyClamp(null, n(1e9)));
            ParamRule ease = ActionParamRules.Get("PlayTweenAnimationAction", "m_easeType");
            Check("excluded enum values", ActionParamRules.CheckEnum(ease, 35, "punch") != null && ActionParamRules.CheckEnum(ease, 1, "linear") == null);
            Check("drop item slot 0 excluded", ActionParamRules.CheckEnum(ActionParamRules.Get("DropMinigameItemAction", "m_itemSlot"), 0, "DefaultForItemType") != null);
            Check("refused fields", ActionParamRules.Get("GiveItemAction", "m_item").Refusal != null
                && ActionParamRules.Get("SetConnectionActiveAction", "m_connectionMappings").Refusal != null
                && ActionParamRules.Get("BoardPopupAction", "m_isTimed").Refusal != null
                && ActionParamRules.Get("BoardSpaceTrigger", "m_hitType").Refusal != null);
            Check("wrapper properties", ActionParamRules.Get("GiveMinigameItemAction", "m_item").WrapperProperty == "ItemPrefab"
                && ActionParamRules.Get("SpawnPrefabAction", "m_prefabs").WrapperProperty == "Prefabs"
                && ActionParamRules.Get("MatchTransformAction", "m_transformToMatch").WrapperProperty == "TransformToMatch");
            Check("choice cost clamps", ActionParamRules.ClampChoiceCost("Health", 40, out bool c1) == 29 && c1
                && ActionParamRules.ClampChoiceCost("Key", 0, out bool c2) == 1 && c2
                && ActionParamRules.ClampChoiceCost("Key", 5000, out _) == 1000
                && ActionParamRules.ClampChoiceCost("None", 0, out bool c3) == 0 && !c3);
            Check("every rule key is Class.field[.sub]", ActionParamRules.Keys.All(key => key.Split('.').Length >= 2));
        }

        // ================================================================== complex values

        private static void ComplexTests()
        {
            Console.WriteLine("-- unit: complex values (ModComplexFloat / ModComplexInt)");
            PComplex P(string json, bool integer = false) => ComplexValues.Parse(JToken.Parse(json), "m_seconds", integer);
            ComplexValue cur = new ComplexValue { ValueType = "Constant", Min = 1, Max = 2 };
            PComplex num = P("3.5");
            Check("bare number = Constant min", num.ValueType == "Constant" && num.Min == 3.5 && num.Max == null);
            PComplex full = P("{\"valueType\":\"RandomBetweenTwoValues\",\"min\":1,\"max\":4}");
            Check("full object", full.ValueType == "RandomBetweenTwoValues" && full.Min == 1 && full.Max == 4);
            Check("valueType case-insensitive / integer form", P("{\"valueType\":\"constant\"}").ValueType == "Constant" && P("{\"valueType\":1}").ValueType == "RandomBetweenTwoValues");
            Check("partial object", P("{\"max\":9}").Max == 9 && P("{\"max\":9}").Min == null);
            Check("refusals: string / unknown key / bad type / NaN-like", Bad(() => P("\"1\"")) && Bad(() => P("{\"mode\":\"x\"}")) && Bad(() => P("{\"valueType\":\"Curve\"}")) && Bad(() => P("{\"min\":\"a\"}")) && Bad(() => P("null")));
            Check("integer complex refuses fractions", Bad(() => P("1.5", true)) && P("2", true).Min == 2);

            List<string> cl = new List<string>();
            ComplexValue m = ComplexValues.Merge(cur, P("{\"max\":5}"), ComplexClamp.None, "m_seconds", cl);
            Check("merge keeps unspecified parts", m.ValueType == "Constant" && m.Min == 1 && m.Max == 5 && cl.Count == 0);
            cl.Clear();
            ComplexValue w = ComplexValues.Merge(cur, P("-3"), ComplexClamp.Wait, "m_seconds", cl);
            Check("wait: min clamped to 0", w.Min == 0 && cl.Contains("m_seconds.min"));
            cl.Clear();
            ComplexValue w2 = ComplexValues.Merge(cur, P("10"), ComplexClamp.Wait, "m_seconds", cl);
            Check("wait: max raised to min (reported)", w2.Min == 10 && w2.Max == 10 && cl.Contains("m_seconds.max") && !cl.Contains("m_seconds.min"));
            cl.Clear();
            ComplexValue w3 = ComplexValues.Merge(cur, P("{\"min\":4000,\"max\":5000}"), ComplexClamp.Wait, "m_seconds", cl);
            Check("wait: 3600 limit", w3.Min == 3600 && w3.Max == 3600 && cl.Count == 2);
            cl.Clear();
            ComplexValue tm = ComplexValues.Merge(cur, P("0.01"), ComplexClamp.Timer, "m_interval", cl);
            Check("timer: min >= 0.1", tm.Min == 0.1 && cl.Contains("m_interval.min"));
            cl.Clear();
            Check("random with min > max refused (no clamp)", Bad(() => ComplexValues.Merge(cur, P("{\"valueType\":\"RandomBetweenTwoValues\",\"min\":5,\"max\":3}"), ComplexClamp.Timer, "m_interval", cl)));
            cl.Clear();
            ComplexValue wr = ComplexValues.Merge(cur, P("{\"valueType\":\"RandomBetweenTwoValues\",\"min\":5,\"max\":3}"), ComplexClamp.Wait, "m_seconds", cl);
            Check("wait random: max clamped up to min instead", wr.Min == 5 && wr.Max == 5 && cl.Contains("m_seconds.max"));
            cl.Clear();
            Check("constant ignores max order", ComplexValues.Merge(cur, P("{\"min\":5}"), ComplexClamp.None, "x", cl).Min == 5);
        }

        // ================================================================== parameter map against a fake schema

        /// <summary>
        /// The game layer resolves names with ParamNames, parses with ValueParser against the field's ValueTypeDesc,
        /// applies the ParamRule clamp/enum checks. This runs the same chain on a fake "ChangeHealthAction" schema.
        /// </summary>
        private static void FakeSchemaTests()
        {
            Console.WriteLine("-- unit: parameter map validation (fake schema)");
            Dictionary<string, ValueTypeDesc> schema = new Dictionary<string, ValueTypeDesc>
            {
                ["m_operation"] = ValueTypeDesc.Enum("ModActionOperation", new[] { "Set", "Add", "Subtract", "Multiply", "Divide" }, new long[] { 0, 1, 2, 3, 4 }, false),
                ["m_value"] = ValueTypeDesc.Integer("Int32", int.MinValue, int.MaxValue),
                ["m_volume"] = ValueTypeDesc.Simple(ValueKind.Float, "Single"),
                ["m_easeType"] = ValueTypeDesc.Enum("LeanTweenType", new[] { "notUsed", "linear", "punch" }, new long[] { 0, 1, 35 }, false),
            };
            Dictionary<string, ParamRule> rules = new Dictionary<string, ParamRule>
            {
                ["m_volume"] = ActionParamRules.Get("PlaySoundAction", "m_volume"),
                ["m_easeType"] = ActionParamRules.Get("PlayTweenAnimationAction", "m_easeType"),
            };
            List<string> names = schema.Keys.ToList();

            Dictionary<string, PValue> Validate(string json, List<string> clamped)
            {
                Dictionary<string, PValue> result = new Dictionary<string, PValue>();
                foreach (JProperty p in JObject.Parse(json).Properties())
                {
                    string name = ParamNames.Resolve(p.Name, names) ?? throw RpcException.InvalidParams($"no parameter '{p.Name}' (parameters: {string.Join(", ", names)})");
                    if (result.ContainsKey(name))
                    {
                        throw RpcException.InvalidParams($"parameter '{name}' is given twice");
                    }
                    ValueTypeDesc d = schema[name];
                    PValue v = ValueParser.Parse(p.Value, d, name);
                    rules.TryGetValue(name, out ParamRule rule);
                    if (v is PEnum pe)
                    {
                        int idx = Array.IndexOf(d.EnumValues, pe.Value);
                        string why = ActionParamRules.CheckEnum(rule, pe.Value, d.EnumNames[idx]);
                        if (why != null)
                        {
                            throw RpcException.InvalidParams($"'{name}': {why}");
                        }
                    }
                    if (ActionParamRules.ApplyClamp(rule, v))
                    {
                        clamped.Add(name);
                    }
                    result[name] = v;
                }
                return result;
            }

            List<string> c = new List<string>();
            Dictionary<string, PValue> ok = Validate("{\"operation\":\"add\",\"m_value\":5,\"volume\":3}", c);
            Check("schema: aliases + enum + int + clamp", ((PEnum)ok["m_operation"]).Value == 1 && ((PInteger)ok["m_value"]).Value == 5 && ((PNumber)ok["m_volume"]).Value == 1 && c.SequenceEqual(new[] { "m_volume" }));
            Check("schema: unknown parameter listed", Msg(() => Validate("{\"m_speed\":1}", new List<string>())).Contains("m_operation"));
            Check("schema: duplicate via alias refused", Bad(() => Validate("{\"value\":1,\"m_value\":2}", new List<string>())));
            Check("schema: wrong type refused", Bad(() => Validate("{\"m_value\":\"5\"}", new List<string>())) && Bad(() => Validate("{\"m_value\":1.5}", new List<string>())));
            Check("schema: out-of-range enum refused", Bad(() => Validate("{\"m_operation\":9}", new List<string>())) && Bad(() => Validate("{\"m_operation\":\"Power\"}", new List<string>())));
            Check("schema: excluded enum value refused", Msg(() => Validate("{\"m_easeType\":\"punch\"}", new List<string>())).Contains("not offered") && Code(() => Validate("{\"m_easeType\":\"linear\"}", new List<string>())) == null);
        }
    }
}
