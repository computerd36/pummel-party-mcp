using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>Game-version gate (plugin 0.7.0), inspector rule engine and the central member registry.</summary>
    internal static class InspectorAndVersionTests
    {
        private static Action<string, bool> s_check;

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            VersionMatching();
            VersionGuard();
            RuleEngine();
            RealRuleTable();
            RegistryAndDocs();
        }

        private static void Check(string name, bool ok) => s_check(name, ok);

        private sealed class NoHost : IEditorGuardHost
        {
            public EditorSnapshot Capture() => new EditorSnapshot();
            public void EnforceDefaultWorkshopId() { }
        }

        private static EditorSnapshot GoodSnapshot() => new EditorSnapshot
        {
            EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "MCP_Arena", FolderName = "MCP_Arena", IsEditing = true, StateName = "Editing",
        };

        // ------------------------------------------------------------------ version matching

        private static void VersionMatching()
        {
            Console.WriteLine("-- unit: game version gate (matching)");
            string[] tested = { "1.14.1k" };
            Check("tested list is exactly 1.14.1k", GameVersionGate.TestedVersions.SequenceEqual(new[] { "1.14.1k" }));
            Check("exact string is tested", GameVersionGate.Evaluate("1.14.1k", tested, null).IsTested);
            Check("case matters (1.14.1K)", !GameVersionGate.Evaluate("1.14.1K", tested, null).IsTested);
            Check("trailing whitespace is a different version", !GameVersionGate.Evaluate("1.14.1k ", tested, null).IsTested);
            Check("leading whitespace is a different version", !GameVersionGate.Evaluate(" 1.14.1k", tested, null).IsTested);
            Check("prefix/suffix are different versions", !GameVersionGate.Evaluate("1.14.1", tested, null).IsTested && !GameVersionGate.Evaluate("1.14.1kk", tested, null).IsTested);
            Check("a different version is untested and refused", !GameVersionGate.Evaluate("1.15.0", tested, null).WritesAllowed);

            GameVersionStatus acc = GameVersionGate.Evaluate("1.15.0", tested, "1.15.0");
            Check("accepted exact version allows writes", acc.Accepted && !acc.IsTested && acc.WritesAllowed);
            Check("accepted value is trimmed (hand-edited cfg)", GameVersionGate.Evaluate("1.15.0", tested, "  1.15.0\t").Accepted);
            Check("acceptance is case-sensitive", !GameVersionGate.Evaluate("1.15.0b", tested, "1.15.0B").Accepted);
            Check("acceptance of another version does not apply", !GameVersionGate.Evaluate("1.15.1", tested, "1.15.0").WritesAllowed);
            Check("acceptance of a prefix does not apply", !GameVersionGate.Evaluate("1.15.0", tested, "1.15").WritesAllowed);
            Check("empty acceptance accepts nothing", !GameVersionGate.Evaluate("1.15.0", tested, "").Accepted && !GameVersionGate.Evaluate("1.15.0", tested, "   ").Accepted
                && !GameVersionGate.Evaluate("1.15.0", tested, null).Accepted);
            Check("wildcards are not special", !GameVersionGate.Evaluate("1.15.0", tested, "*").Accepted && !GameVersionGate.Evaluate("1.15.0", tested, "1.*").Accepted);

            foreach (string unreadable in new[] { null, "", "   ", "\t" })
            {
                GameVersionStatus u = GameVersionGate.Evaluate(unreadable, tested, unreadable);
                Check($"unreadable version '{(unreadable ?? "<null>").Replace("\t", "\\t")}': not readable, not tested, not accepted, writes refused",
                    !u.Readable && !u.IsTested && !u.Accepted && !u.WritesAllowed);
            }
            GameVersionStatus u2 = GameVersionGate.Evaluate(null, tested, "null");
            Check("unreadable version message says so", GameVersionGate.Refusal(u2).Message.Contains("could not be read") && GameVersionGate.Refusal(u2).Code == ErrorCodes.GameVersionUntested);

            GameVersionStatus forced = GameVersionGate.Evaluate("1.14.1k", tested, null, forceUntested: true);
            Check("ForceGameVersionUntested makes a tested version untested (stricter)", !forced.IsTested && !forced.WritesAllowed && forced.ForcedUntested
                && GameVersionGate.Refusal(forced).Message.Contains("ForceGameVersionUntested"));
            Check("the force switch never accepts anything by itself", !GameVersionGate.Evaluate("1.15.0", tested, null, forceUntested: true).WritesAllowed);
            Check("...and acceptance still follows the config (it can only be stricter than tested)",
                GameVersionGate.Evaluate("1.14.1k", tested, "1.14.1k", forceUntested: true).WritesAllowed);
            RpcException msg = GameVersionGate.Refusal(GameVersionGate.Evaluate("1.15.0", tested, null));
            Check("refusal names running version, tested versions and the config key",
                msg.Code == "GAME_VERSION_UNTESTED" && msg.Message.Contains("'1.15.0'") && msg.Message.Contains("1.14.1k")
                && msg.Message.Contains("[Safety] AcceptedGameVersion = 1.15.0") && msg.Message.Contains("self_test") && msg.Message.Contains("no restart"));
            Check("no refusal when writes are allowed", GameVersionGate.Refusal(GameVersionGate.AlwaysTested()) == null);
            Check("null status fails closed", GameVersionGate.Refusal(null)?.Code == ErrorCodes.GameVersionUntested);
        }

        // ------------------------------------------------------------------ guard

        private static string Code(bool writes, bool block, GameVersionStatus version, WriteGuard guard, object args, EditorSnapshot snap) =>
            WriteGuardEvaluator.Evaluate(new WriteSafety(() => writes, () => block, () => version, new NoHost()), guard, args, snap)?.Code;

        private static void VersionGuard()
        {
            Console.WriteLine("-- unit: game version in the write guard");
            GameVersionStatus untested = GameVersionGate.Evaluate("1.15.0", GameVersionGate.TestedVersions, null);
            GameVersionStatus accepted = GameVersionGate.Evaluate("1.15.0", GameVersionGate.TestedVersions, "1.15.0");
            WriteGuard open = WriteGuard.OpenMod();
            WriteGuard life = WriteGuard.Lifecycle(a => (string)a);
            EditorSnapshot closed = new EditorSnapshot { EditorOpen = false };

            Check("tested + all good -> allowed", Code(true, true, GameVersionGate.AlwaysTested(), open, null, GoodSnapshot()) == null);
            Check("untested -> GAME_VERSION_UNTESTED (OpenMod)", Code(true, true, untested, open, null, GoodSnapshot()) == ErrorCodes.GameVersionUntested);
            Check("untested -> GAME_VERSION_UNTESTED (Lifecycle)", Code(true, true, untested, life, "MCP_New", GoodSnapshot()) == ErrorCodes.GameVersionUntested);
            Check("accepted -> allowed", Code(true, true, accepted, open, null, GoodSnapshot()) == null);

            // order: EnableWrites, publish block, game version, then editor state
            Check("WRITES_DISABLED comes before the version check", Code(false, true, untested, open, null, GoodSnapshot()) == ErrorCodes.WritesDisabled);
            Check("PUBLISH_BLOCK_INCOMPLETE comes before the version check", Code(true, false, untested, open, null, GoodSnapshot()) == ErrorCodes.PublishBlockIncomplete);
            Check("both writes disabled and untested -> WRITES_DISABLED", Code(false, false, untested, open, null, closed) == ErrorCodes.WritesDisabled);
            Check("version check comes before EDITOR_NOT_OPEN", Code(true, true, untested, open, null, closed) == ErrorCodes.GameVersionUntested);
            Check("version check comes before EDITOR_NOT_OPEN (null snapshot)", Code(true, true, untested, open, null, null) == ErrorCodes.GameVersionUntested);
            Check("version check comes before GUARD_NOT_TEST_MOD", Code(true, true, untested, life, "Real Mod", GoodSnapshot()) == ErrorCodes.GameVersionUntested);
            Check("tested version: editor checks still apply afterwards", Code(true, true, GameVersionGate.AlwaysTested(), open, null, closed) == ErrorCodes.EditorNotOpen);

            Check("a throwing status provider fails closed",
                WriteGuardEvaluator.Evaluate(new WriteSafety(() => true, () => true, () => throw new InvalidOperationException("x"), new NoHost()), open, null, GoodSnapshot())?.Code == ErrorCodes.GameVersionUntested);
            Check("a null status fails closed",
                WriteGuardEvaluator.Evaluate(new WriteSafety(() => true, () => true, () => null, new NoHost()), open, null, GoodSnapshot())?.Code == ErrorCodes.GameVersionUntested);
            Check("unreadable version -> refused with the unreadable message",
                WriteGuardEvaluator.Evaluate(new WriteSafety(() => true, () => true, () => GameVersionGate.Evaluate(null, GameVersionGate.TestedVersions, null), new NoHost()), open, null, GoodSnapshot())
                    ?.Message.Contains("could not be read") == true);
            Check("WriteSafety requires the version provider",
                Throws<ArgumentNullException>(() => new WriteSafety(() => true, () => true, null, new NoHost())));

            // config changes between calls (the provider is evaluated on every call)
            string acceptedConfig = "";
            WriteSafety live = new WriteSafety(() => true, () => true, () => GameVersionGate.Evaluate("1.15.0", GameVersionGate.TestedVersions, acceptedConfig), new NoHost());
            Check("call 1 (nothing accepted) refused", WriteGuardEvaluator.Evaluate(live, open, null, GoodSnapshot())?.Code == ErrorCodes.GameVersionUntested);
            acceptedConfig = "1.15.0";
            Check("call 2 (accepted meanwhile) allowed", WriteGuardEvaluator.Evaluate(live, open, null, GoodSnapshot()) == null);
            acceptedConfig = "1.15.1";
            Check("call 3 (config now names another version) refused again", WriteGuardEvaluator.Evaluate(live, open, null, GoodSnapshot())?.Code == ErrorCodes.GameVersionUntested);
            acceptedConfig = "";
            Check("call 4 (acceptance removed) refused", WriteGuardEvaluator.Evaluate(live, open, null, GoodSnapshot())?.Code == ErrorCodes.GameVersionUntested);

            // (the registry-level checks, body not run, are in GuardTests.ThroughRegistry)
        }

        private static bool Throws<T>(Action a) where T : Exception
        {
            try
            {
                a();
                return false;
            }
            catch (T)
            {
                return true;
            }
        }

        // ------------------------------------------------------------------ rule engine (fake rule data)

        private sealed class FakeCtx : IRuleContext
        {
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);
            public readonly Dictionary<string, bool?> Derived0 = new Dictionary<string, bool?>(StringComparer.Ordinal);
            public object Get(string property) => Values.TryGetValue(property, out object v) ? v : InspectorRules.Unknown;
            public bool? Derived(string name) => Derived0.TryGetValue(name, out bool? v) ? v : null;
        }

        private static IRuleContext After(FakeCtx live, params (string, object)[] assigned)
        {
            Dictionary<string, object> pending = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach ((string k, object v) in assigned)
            {
                pending[k] = v;
            }
            return new InspectorRules.AfterCallContext(pending, name => live.Values.TryGetValue(name, out object v) ? v : null, live.Derived);
        }

        private static string[] E(params string[] names) => names;

        private static void RuleEngine()
        {
            Console.WriteLine("-- unit: inspector rule engine (fake rules)");
            PropertyRule never = new PropertyRule { Component = "ModFake", Property = "hidden", NeverDrawn = true, Note = "never drawn" };
            Check("never-drawn property refused, message says the inspector does not offer it",
                InspectorRules.CheckRule(never, "Fake", true, After(new FakeCtx())) is string m
                && m.Contains("does not offer 'hidden'") && m.Contains("Fake"));

            PropertyRule spot = new PropertyRule { Component = "ModFake", Property = "spotAngle", Shown = Cond.Is("type", "Spot") };
            FakeCtx point = new FakeCtx();
            point.Values["type"] = E("Point");
            string refusal = InspectorRules.CheckRule(spot, "Fake", 40.0, After(point));
            Check("conditional (false): refused naming the condition and the current state",
                refusal != null && refusal.Contains("'spotAngle'") && refusal.Contains("type is Spot") && refusal.Contains("type is Point after this call") && refusal.Contains("set the controlling property first"));
            Check("conditional (true): allowed", InspectorRules.CheckRule(spot, "Fake", 40.0, After(new FakeCtx { Values = { ["type"] = E("Spot") } })) == null);
            Check("conditional satisfied within the same call", InspectorRules.CheckRule(spot, "Fake", 40.0, After(point, ("type", E("Spot")))) == null);
            Check("conditional broken within the same call", InspectorRules.CheckRule(spot, "Fake", 40.0, After(new FakeCtx { Values = { ["type"] = E("Spot") } }, ("type", E("Point")))) != null);
            Check("unknown controlling value allows the write (cannot tell)", InspectorRules.CheckRule(spot, "Fake", 40.0, After(new FakeCtx())) == null);

            PropertyRule notDir = new PropertyRule { Component = "ModFake", Property = "range", Shown = Cond.Is("type", "Directional").Not() };
            Check("negated condition", InspectorRules.CheckRule(notDir, "F", 1.0, After(new FakeCtx { Values = { ["type"] = E("Directional") } })) != null
                && InspectorRules.CheckRule(notDir, "F", 1.0, After(new FakeCtx { Values = { ["type"] = E("Point") } })) == null
                && InspectorRules.CheckRule(notDir, "F", 1.0, After(new FakeCtx { Values = { ["type"] = E("Directional") } }, ("type", E("Spot")))) == null);
            Check("negated condition text", Cond.Is("type", "Directional").Not().Describe() == "type is not Directional"
                && Cond.Is("shape", "Sphere", "Capsule").Describe() == "shape is Sphere or Capsule");

            // flags and bools
            PropertyRule shared = new PropertyRule { Component = "ModFake", Property = "shared", Shown = Cond.Is("usage", "InitialSpawn") };
            Check("flag set contains the name", InspectorRules.CheckRule(shared, "F", true, After(new FakeCtx { Values = { ["usage"] = E("InitialSpawn", "Respawn") } })) == null
                && InspectorRules.CheckRule(shared, "F", true, After(new FakeCtx { Values = { ["usage"] = E("Respawn") } })) != null
                && InspectorRules.CheckRule(shared, "F", true, After(new FakeCtx { Values = { ["usage"] = E() } })) != null);
            PropertyRule color = new PropertyRule { Component = "ModFake", Property = "color", Shown = Cond.On("outline") };
            Check("bool condition", InspectorRules.CheckRule(color, "F", 1.0, After(new FakeCtx { Values = { ["outline"] = false } })) is string t && t.Contains("outline is on") && t.Contains("outline is off")
                && InspectorRules.CheckRule(color, "F", 1.0, After(new FakeCtx { Values = { ["outline"] = false } }, ("outline", true))) == null);
            PropertyRule octave = new PropertyRule { Component = "ModFake", Property = "scale", Shown = Cond.NumGt("octaves", 1) };
            Check("numeric condition", InspectorRules.CheckRule(octave, "F", 1.0, After(new FakeCtx { Values = { ["octaves"] = 1.0 } })) != null
                && InspectorRules.CheckRule(octave, "F", 1.0, After(new FakeCtx { Values = { ["octaves"] = 2.0 } })) == null);
            PropertyRule stereo = new PropertyRule { Component = "ModFake", Property = "pan", Shown = Cond.NumEq("blend", 1).Not() };
            Check("numeric equality, negated", InspectorRules.CheckRule(stereo, "F", 0.5, After(new FakeCtx { Values = { ["blend"] = 1.0 } })) != null
                && InspectorRules.CheckRule(stereo, "F", 0.5, After(new FakeCtx { Values = { ["blend"] = 0.5 } })) == null);

            // groups and three-valued logic
            RuleCondition standard = Cond.All(Cond.On("hit").Not(), Cond.Is("shape", "Custom").Not());
            FakeCtx c1 = new FakeCtx { Values = { ["hit"] = false, ["shape"] = E("Box") } };
            FakeCtx c2 = new FakeCtx { Values = { ["hit"] = true, ["shape"] = E("Box") } };
            Check("All: true / false", standard.Evaluate(c1) == true && standard.Evaluate(c2) == false);
            Check("All: unknown part, other part false -> false; other part true -> unknown",
                Cond.All(Cond.On("x"), Cond.On("hit")).Evaluate(c1) == false && Cond.All(Cond.On("x"), Cond.On("hit").Not()).Evaluate(c1) == null);
            Check("Any: true wins over unknown; false + unknown -> unknown",
                Cond.Any(Cond.On("x"), Cond.On("hit").Not()).Evaluate(c1) == true && Cond.Any(Cond.On("x"), Cond.On("hit")).Evaluate(c1) == null);
            RuleCondition d = Cond.Derived("D", "yes text", "no text");
            FakeCtx dc = new FakeCtx();
            Check("derived: unknown -> allowed; true/false follow the host", d.Evaluate(dc) == null);
            dc.Derived0["D"] = true;
            Check("derived true", d.Evaluate(dc) == true && d.Not().Evaluate(dc) == false && d.Not().Describe() == "no text");

            // choices
            PropertyRule rolloff = new PropertyRule { Component = "ModFake", Property = "rolloff", ExcludedValues = E("Custom") };
            Check("excluded enum value refused, others allowed",
                InspectorRules.CheckRule(rolloff, "F", E("Custom"), After(new FakeCtx())) is string x && x.Contains("does not offer the value Custom")
                && InspectorRules.CheckRule(rolloff, "F", E("Linear"), After(new FakeCtx())) == null);
            PropertyRule shapeType = new PropertyRule { Component = "ModFake", Property = "shapeType", AllowedValues = E("Sphere", "Box", "Cone") };
            Check("restricted choices: others refused, listing the offered values",
                InspectorRules.CheckRule(shapeType, "F", E("Mesh"), After(new FakeCtx())) is string y && y.Contains("cannot choose Mesh") && y.Contains("Sphere, Box or Cone")
                && InspectorRules.CheckRule(shapeType, "F", E("Box"), After(new FakeCtx())) == null);
            PropertyRule styles = new PropertyRule { Component = "ModFake", Property = "styles", AllowedFlags = E("Bold", "Italic") };
            Check("restricted flags: a flag outside the set is refused",
                InspectorRules.CheckRule(styles, "F", E("Bold", "Italic"), After(new FakeCtx())) == null
                && InspectorRules.CheckRule(styles, "F", E("Bold", "UpperCase"), After(new FakeCtx())) is string z && z.Contains("UpperCase")
                && InspectorRules.CheckRule(styles, "F", E(), After(new FakeCtx())) == null);
            PropertyRule both = new PropertyRule { Component = "ModFake", Property = "both", Shown = Cond.On("on"), ExcludedValues = E("Bad") };
            Check("condition is checked before choices", InspectorRules.CheckRule(both, "F", E("Bad"), After(new FakeCtx { Values = { ["on"] = false } })).Contains("only shown"));

            // clamps use the real table through InspectorClamps (limits are clamped, not refused)
            PNumber angle = new PNumber { Value = 200 };
            Check("clamp: ModLight.spotAngle 200 -> 180, reported", InspectorClamps.Apply("ModLight", "spotAngle", angle) && angle.Value == 180);
            PNumber arc = new PNumber { Value = 400 };
            PNumber thickness = new PNumber { Value = -1 };
            Check("clamp: particle arc 0..360 and radiusThickness 0..1",
                InspectorClamps.Apply("ModParticleSystem", "arc", arc) && arc.Value == 360 && InspectorClamps.Apply("ModParticleSystem", "radiusThickness", thickness) && thickness.Value == 0);
            Check("clamp table survives the move (19 original entries + arc and radiusThickness)", InspectorRules.All.Count(r => r.Min.HasValue || r.Max.HasValue) == 21);
        }

        // ------------------------------------------------------------------ the real table

        private static string Run(string component, string property, object value, FakeCtx live, params (string, object)[] assigned) =>
            InspectorRules.Check(component, component, property, value, After(live, assigned));

        private static void RealRuleTable()
        {
            Console.WriteLine("-- unit: inspector rule table (real data)");
            Check("21 custom-inspector components", InspectorRules.CustomInspectorComponents.Length == 21 && InspectorRules.CustomInspectorComponents.Distinct().Count() == 21);
            Check("every rule belongs to a custom-inspector component", InspectorRules.All.All(r => InspectorRules.HasCustomInspector(r.Component)));
            Check("rule keys are unique", InspectorRules.All.Select(r => r.Component + "." + r.Property).Distinct().Count() == InspectorRules.All.Count);
            Check("audit snapshot covers exactly the 21 components", InspectorRules.AuditedProperties.Keys.OrderBy(k => k).SequenceEqual(InspectorRules.CustomInspectorComponents.OrderBy(k => k)));
            List<string> badRefs = new List<string>();
            foreach (PropertyRule r in InspectorRules.All)
            {
                HashSet<string> props = new HashSet<string>(InspectorRules.AuditedProperties[r.Component].Split(' '));
                if (!props.Contains(r.Property))
                {
                    badRefs.Add(r.Component + "." + r.Property);
                }
                List<RuleRef> refs = new List<RuleRef>();
                r.Shown?.CollectRefs(refs);
                badRefs.AddRange(refs.Where(f => !props.Contains(f.Property)).Select(f => r.Component + "." + r.Property + " -> " + f.Property));
            }
            Check("every rule and condition names a property of its component's audit snapshot" + (badRefs.Count > 0 ? ": " + string.Join(", ", badRefs) : ""), badRefs.Count == 0);
            Check("a rule never has both a condition and NeverDrawn", InspectorRules.All.All(r => !(r.NeverDrawn && r.Shown != null)));

            // Light: type controls range and spotAngle
            FakeCtx point = new FakeCtx { Values = { ["type"] = E("Point") } };
            Check("Light: spotAngle on a Point light refused", Run("ModLight", "spotAngle", 40.0, point) is string a && a.Contains("type is Spot") && a.Contains("type is Point after this call"));
            Check("Light: {type: Spot, spotAngle: 40} in one call allowed", Run("ModLight", "spotAngle", 40.0, point, ("type", E("Spot")), ("spotAngle", 40.0)) == null);
            Check("Light: range on a Directional light refused, on Point/Spot allowed",
                Run("ModLight", "range", 5.0, new FakeCtx { Values = { ["type"] = E("Directional") } }) != null && Run("ModLight", "range", 5.0, point) == null
                && Run("ModLight", "range", 5.0, new FakeCtx { Values = { ["type"] = E("Directional") } }, ("type", E("Point"))) == null);
            Check("Light: type, color, intensity, shadows always allowed", new[] { "type", "color", "intensity", "shadows" }.All(p => Run("ModLight", p, E("Spot"), point) == null));

            // Trigger
            FakeCtx box = new FakeCtx { Values = { ["TriggerShape"] = E("Box"), ["TriggerOnHit"] = false, ["TriggerOnStay"] = false } };
            Check("Trigger: Radius on a Box trigger refused", Run("ModTrigger", "Radius", 2.0, box) is string r1 && r1.Contains("TriggerShape is Sphere or Capsule"));
            Check("Trigger: Radius with the shape changed to Sphere in the same call allowed", Run("ModTrigger", "Radius", 2.0, box, ("TriggerShape", E("Sphere")), ("Radius", 2.0)) == null);
            Check("Trigger: Size only for Box, Height only for Capsule",
                Run("ModTrigger", "Size", true, box) == null && Run("ModTrigger", "Height", 1.0, box) != null
                && Run("ModTrigger", "Height", 1.0, box, ("TriggerShape", E("Capsule"))) == null
                && Run("ModTrigger", "Size", true, box, ("TriggerShape", E("Sphere"))) != null);
            Check("Trigger: Center hidden for CustomCollider", Run("ModTrigger", "Center", true, box) == null && Run("ModTrigger", "Center", true, box, ("TriggerShape", E("CustomCollider"))) != null);
            Check("Trigger: TriggerOnEnter/Exit/Stay allowed on a standard trigger", new[] { "TriggerOnEnter", "TriggerOnExit", "TriggerOnStay" }.All(p => Run("ModTrigger", p, true, box) == null));
            Check("Trigger: TriggerOnEnter refused while TriggerOnHit is on, allowed when turned off in the same call",
                Run("ModTrigger", "TriggerOnEnter", true, box, ("TriggerOnHit", true)) is string r2 && r2.Contains("TriggerOnHit is off")
                && Run("ModTrigger", "TriggerOnEnter", true, new FakeCtx { Values = { ["TriggerShape"] = E("Box"), ["TriggerOnHit"] = true } }, ("TriggerOnHit", false)) == null);
            Check("Trigger: TriggerOnEnter refused for CustomCollider", Run("ModTrigger", "TriggerOnEnter", true, box, ("TriggerShape", E("CustomCollider"))) != null);
            Check("Trigger: StayTriggerInterval needs TriggerOnStay (same call ok)", Run("ModTrigger", "StayTriggerInterval", 0.5, box) != null
                && Run("ModTrigger", "StayTriggerInterval", 0.5, box, ("TriggerOnStay", true)) == null);
            Check("Trigger: DisableAfterTriggered, OneUsePerPlayer, TriggerShape, TriggerOnHit always allowed",
                new[] { "DisableAfterTriggered", "OneUsePerPlayer", "TriggerShape", "TriggerOnHit" }.All(p => Run("ModTrigger", p, true, box, ("TriggerOnHit", true)) == null));

            // Prop
            FakeCtx noMats = new FakeCtx { Values = { ["customMaterials"] = false } };
            Check("Prop: tintColor allowed without custom materials, refused with them, allowed when cleared in the same call",
                Run("ModProp", "tintColor", true, noMats) == null && Run("ModProp", "tintColor", true, noMats, ("customMaterials", true)) is string p1 && p1.Contains("customMaterials holds no material")
                && Run("ModProp", "tintColor", true, new FakeCtx { Values = { ["customMaterials"] = true } }, ("customMaterials", false)) == null);
            Check("Prop: prop, collisionType, shadowCastingMode, customMaterials always allowed", new[] { "prop", "collisionType", "shadowCastingMode", "customMaterials" }.All(p => Run("ModProp", p, true, noMats, ("customMaterials", true)) == null));

            // never-drawn
            Check("ParticleSystem.startSize, NavMeshAgent.speed, SimpleAnimator.UseLateUpdate, BoardSpace.nodeEventType never drawn",
                InspectorRules.NeverDrawnRefusal("ModParticleSystem", "Particle System", "startSize") != null && InspectorRules.NeverDrawnRefusal("ModNavMeshAgent", "Nav Mesh Agent", "speed") != null
                && InspectorRules.NeverDrawnRefusal("ModSimpleAnimator", "Simple Animator", "UseLateUpdate") != null && InspectorRules.NeverDrawnRefusal("ModBoardSpace", "Board Space", "nodeEventType") != null
                && InspectorRules.NeverDrawnRefusal("ModLight", "Light", "type") == null);
            Check("generic inspector: types without a control are named, drawn types are not",
                InspectorRules.GenericNotDrawnRefusal("MINMAXCURVE", "MinMaxCurve", "x") != null && InspectorRules.GenericNotDrawnRefusal("LONG", "long", "x") != null
                && InspectorRules.GenericNotDrawnRefusal("FLOAT", "float", "x") == null && InspectorRules.GenericNotDrawnRefusal("MINMAXGRADIENT", "x", "x") == null
                && InspectorRules.GenericNotDrawnRefusal("ANIMATIONCURVE", "x", "x") == null);

            // choices
            Check("AudioSource.rolloffMode Custom refused, Linear allowed", Run("ModAudioSource", "rolloffMode", E("Custom"), new FakeCtx { Values = { ["spatialBlend"] = 1.0 } }) != null
                && Run("ModAudioSource", "rolloffMode", E("Linear"), new FakeCtx { Values = { ["spatialBlend"] = 1.0 } }) == null);
            Check("AudioSource.stereoPanning hidden at spatialBlend 1, shown below", Run("ModAudioSource", "stereoPanning", 0.0, new FakeCtx { Values = { ["spatialBlend"] = 1.0 } }) != null
                && Run("ModAudioSource", "stereoPanning", 0.0, new FakeCtx { Values = { ["spatialBlend"] = 1.0 } }, ("spatialBlend", 0.5)) == null);
            Check("ParticleSystem.shapeType Mesh refused, Donut allowed", Run("ModParticleSystem", "shapeType", E("Mesh"), new FakeCtx()) != null && Run("ModParticleSystem", "shapeType", E("Donut"), new FakeCtx()) == null);
            Check("ParticleSystem.radius hidden for Box shapes, shown for Sphere", Run("ModParticleSystem", "radius", 1.0, new FakeCtx { Values = { ["shapeType"] = E("BoxShell") } }) != null
                && Run("ModParticleSystem", "radius", 1.0, new FakeCtx { Values = { ["shapeType"] = E("Box") } }, ("shapeType", E("Sphere"))) == null);
            Check("ParticleSystem.forceOverLifetimeRandomize needs TwoConstants curve mode", Run("ModParticleSystem", "forceOverLifetimeRandomize", true, new FakeCtx { Values = { ["forceOverLifetimeX"] = E("Constant") } }) != null
                && Run("ModParticleSystem", "forceOverLifetimeRandomize", true, new FakeCtx { Values = { ["forceOverLifetimeX"] = E("Constant") } }, ("forceOverLifetimeX", E("TwoConstants"))) == null);
            Check("Text: Font COUNT, HorizontalAlignment Flush and FontStyles UpperCase refused",
                Run("ModText", "Font", E("COUNT"), new FakeCtx()) != null && Run("ModText", "HorizontalAlignment", E("Flush"), new FakeCtx()) != null
                && Run("ModText", "FontStyles", E("Bold", "UpperCase"), new FakeCtx()) != null && Run("ModText", "FontStyles", E("Bold", "Strikethrough"), new FakeCtx()) == null
                && Run("ModText", "HorizontalAlignment", E("Justified"), new FakeCtx()) == null);
            Check("Text: OutlineColor needs Outline, ShadowColor needs Shadow", Run("ModText", "OutlineColor", true, new FakeCtx { Values = { ["Outline"] = false } }) != null
                && Run("ModText", "ShadowColor", true, new FakeCtx { Values = { ["Shadow"] = false } }, ("Shadow", true)) == null);
            Check("Item: ItemSlot refused on a Pickup, allowed on Carried; Cooldown only Carried/Weapon",
                Run("ModItem", "ItemSlot", E("Back"), new FakeCtx { Values = { ["ItemType"] = E("Pickup") } }) != null && Run("ModItem", "ItemSlot", E("Back"), new FakeCtx { Values = { ["ItemType"] = E("Pickup") } }, ("ItemType", E("Carried"))) == null
                && Run("ModItem", "Cooldown", 1.0, new FakeCtx { Values = { ["ItemType"] = E("Worn") } }) != null && Run("ModItem", "Cooldown", 1.0, new FakeCtx { Values = { ["ItemType"] = E("Weapon") } }) == null);
            Check("PlayerSpawn: LineLength needs Line, SharedSpawn needs InitialSpawn, AllowedPlayers needs Advanced",
                Run("ModPlayerSpawn", "LineLength", 3.0, new FakeCtx { Values = { ["SpawnShape"] = E("Point") } }) != null && Run("ModPlayerSpawn", "LineLength", 3.0, new FakeCtx { Values = { ["SpawnShape"] = E("Point") } }, ("SpawnShape", E("Line"))) == null
                && Run("ModPlayerSpawn", "SharedSpawn", true, new FakeCtx { Values = { ["SpawnUsageType"] = E("Respawn") } }) != null
                && Run("ModPlayerSpawn", "AllowedPlayers", E("All"), new FakeCtx { Values = { ["Advanced"] = false } }) != null);
            Check("Mover.Direction needs DirectionType Custom; Spawner.SpawnPoints needs OnTargets",
                Run("ModMover", "Direction", true, new FakeCtx { Values = { ["DirectionType"] = E("Up") } }) != null && Run("ModMover", "Direction", true, new FakeCtx { Values = { ["DirectionType"] = E("Up") } }, ("DirectionType", E("Custom"))) == null
                && Run("ModSpawner", "SpawnPoints", true, new FakeCtx { Values = { ["LocationType"] = E("OnSpawner") } }) != null);
            Check("BoardSpace: ConsumeStepOnPass only for Custom, baseNodeType Trophy not offered",
                Run("ModBoardSpace", "ConsumeStepOnPass", true, new FakeCtx { Values = { ["baseNodeType"] = E("Gold") } }) != null && Run("ModBoardSpace", "baseNodeType", E("Trophy"), new FakeCtx()) != null
                && Run("ModBoardSpace", "baseNodeType", E("Gold"), new FakeCtx()) == null);
            Check("MeshCollider.mesh follows the derived linked-renderer state",
                Run("ModMeshCollider", "mesh", true, new FakeCtx { Derived0 = { ["NoLinkedRenderer"] = false } }) != null && Run("ModMeshCollider", "mesh", true, new FakeCtx { Derived0 = { ["NoLinkedRenderer"] = true } }) == null
                && Run("ModMeshCollider", "mesh", true, new FakeCtx()) == null);
            Check("properties without a rule are always allowed", Run("ModLight", "nonexistent", true, new FakeCtx()) == null && Run("ModNote", "Text", "x", new FakeCtx()) == null);
        }

        // ------------------------------------------------------------------ registry, source scan, docs

        private static string FindUp(string relative)
        {
            for (DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string p = Path.Combine(d.FullName, relative);
                if (File.Exists(p) || Directory.Exists(p))
                {
                    return p;
                }
            }
            return null;
        }

        private static void RegistryAndDocs()
        {
            Console.WriteLine("-- unit: member registry, source scan, docs");
            IReadOnlyList<RequiredMember> all = RequiredMembers.All;
            Check($"registry has members ({all.Count})", all.Count > 300);
            Check("registry entries are distinct", all.Select(m => m.Kind + "|" + m.Id + "|" + m.IsStatic).Distinct().Count() == all.Count);
            Check("registry covers every rule and condition property", InspectorRules.All.All(r => all.Any(m => m.Kind == "ruleProperty" && m.Type == r.Component && m.Name == r.Property)));

            string pluginDir = FindUp(Path.Combine("src", "PummelPartyMcp.Plugin"));
            if (pluginDir == null)
            {
                Check("plugin sources located for the registry scan", false);
                return;
            }
            // Every by-name reflection lookup in the plugin sources must be in the registry (so self_test and the drift test cannot miss one).
            HashSet<string> registered = new HashSet<string>(all.Select(m => m.Name), StringComparer.Ordinal);
            Regex typed = new Regex(@"(?:RequireField|FindField|GetStaticField)\(\s*typeof\(([\w.]+)\)\s*,\s*""(\w+)""");
            Regex direct = new Regex(@"typeof\(([\w.]+)\)\s*\.\s*Get(?:Field|Method|Property)\(\s*""(\w+)""");
            Regex byName = new Regex(@"GameReflection\.GetField\([^,]+,\s*""(\w+)""");
            List<string> missingFromRegistry = new List<string>();
            foreach (string file in Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            {
                string text = File.ReadAllText(file);
                string rel = file.Substring(pluginDir.Length + 1);
                foreach (Match m in typed.Matches(text).Cast<Match>().Concat(direct.Matches(text).Cast<Match>()))
                {
                    if (!registered.Contains(m.Groups[2].Value))
                    {
                        missingFromRegistry.Add($"{rel}: {m.Groups[1].Value}.{m.Groups[2].Value}");
                    }
                }
                foreach (Match m in byName.Matches(text))
                {
                    if (!registered.Contains(m.Groups[1].Value))
                    {
                        missingFromRegistry.Add($"{rel}: {m.Groups[1].Value}");
                    }
                }
            }
            // GameReflection itself and PublishBlockTargets (own drift test) use dynamic names and are excluded by the patterns above.
            Check("every by-name member lookup in the plugin sources is registered in RequiredMembers" + (missingFromRegistry.Count > 0 ? ": " + string.Join("; ", missingFromRegistry.Distinct()) : ""), missingFromRegistry.Count == 0);

            string docs = FindUp(Path.Combine("docs", "plugin-api.md"));
            if (docs == null)
            {
                Check("docs/plugin-api.md located", false);
                return;
            }
            string text2 = File.ReadAllText(docs).Replace("\r\n", "\n");
            int b = text2.IndexOf(InspectorRules.DocsBegin, StringComparison.Ordinal);
            int e = text2.IndexOf(InspectorRules.DocsEnd, StringComparison.Ordinal);
            Check("docs/plugin-api.md has the generated inspector-rules block", b >= 0 && e > b);
            if (b >= 0 && e > b)
            {
                string block = text2.Substring(b, e + InspectorRules.DocsEnd.Length - b);
                Check("the inspector-rules block in docs/plugin-api.md equals InspectorRules.ToMarkdown() (regenerate it when the table changes)", block == InspectorRules.ToMarkdown());
            }
            Check("docs name the new error code and tool", text2.Contains("GAME_VERSION_UNTESTED") && text2.Contains("self_test") && text2.Contains("AcceptedGameVersion"));
        }
    }
}
