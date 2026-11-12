using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>Mod settings (plugin 0.8.0): rule table integrity, the validation/normalisation engine on fake data, docs, registry, batch and guard declarations.</summary>
    internal static class SettingsTests
    {
        private static Action<string, bool> s_check;

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            TableIntegrity();
            EngineRules();
            EngineParsing();
            Presets();
            ScreenEffects();
            Implied();
            RoundTrip();
            Shapes();
            BatchAndGuards();
            DocsAndRegistry();
        }

        private static void Check(string name, bool ok) => s_check(name, ok);

        // ------------------------------------------------------------------ fake game

        private sealed class FakeState : ISettingsState
        {
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);
            public readonly Dictionary<string, bool?> DerivedValues = new Dictionary<string, bool?>();
            public List<string> LightingPresets = new List<string> { "Day", "Night", "Custom" };
            public List<string> EffectPresets = new List<string> { "Default", "Noir", "Custom" };
            public readonly HashSet<string> Assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> BadAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Func<ModSettingField, double[]> Limits = f => null;

            public object Current(ModSettingField f)
            {
                if (Values.TryGetValue(f.Name, out object v))
                {
                    return v;
                }
                if (f.IsEffect)
                {
                    return false;
                }
                if (f.IsParam)
                {
                    return null;
                }
                if (f.HasDefault && f.Default != null)
                {
                    return f.Kind == SettingKind.Asset ? new AssetValue { Guid = (string)f.Default } : f.Default;
                }
                return null;
            }

            public bool? Derived(string name) => DerivedValues.TryGetValue(name, out bool? b) ? b : null;

            public IList<string> PresetNames(ModSettingField f) => f.Target == "lightingPreset" ? LightingPresets : EffectPresets;

            public string CheckAsset(ModSettingField f, string guid)
            {
                if (BadAssets.TryGetValue(guid, out string why))
                {
                    return why;
                }
                return Assets.Contains(guid) ? null : "no asset with that guid";
            }

            public bool TryGetLimits(ModSettingField f, out double min, out double max)
            {
                double[] l = Limits(f);
                min = l?[0] ?? 0;
                max = l?[1] ?? 10;
                return l != null;
            }
        }

        private static SettingsSection Sec(string modType, string name) => SettingsRules.Find(modType, name);

        private static JObject J(string json) => JObject.Parse(json);

        private static string Refusal(Func<object> f)
        {
            try
            {
                f();
                return null;
            }
            catch (RpcException e)
            {
                return e.Code == ErrorCodes.InvalidParams ? e.Message : "WRONG CODE " + e.Code + ": " + e.Message;
            }
        }

        private static SettingsPlan Plan(string modType, string section, string json, FakeState st = null) =>
            SettingsEngine.Plan(Sec(modType, section), J(json), st ?? new FakeState());

        private static object Value(SettingsPlan p, string field) => p.Assignments.First(a => a.Field.Name == field).Value;

        // ------------------------------------------------------------------ table

        private static void TableIntegrity()
        {
            Console.WriteLine("-- unit: settings rule table");
            IReadOnlyList<SettingsSection> all = SettingsRules.All;
            Check("sections exist for Minigame and Board",
                SettingsRules.ForModType("Minigame").Select(s => s.Name).SequenceEqual(new[] { "details", "minigameSettings", "playerSettings", "music", "lighting", "screenEffects" })
                && SettingsRules.ForModType("Board").Select(s => s.Name).SequenceEqual(new[] { "details", "customSpaces", "boardSize", "camera", "music", "lighting", "screenEffects" }));
            Check("section names are unique per mod type", all.GroupBy(s => s.ModType + "/" + s.Name).All(g => g.Count() == 1));
            foreach (SettingsSection s in all)
            {
                string at = s.ModType + "/" + s.Name;
                Check($"{at}: field names are unique (case-insensitive)", s.Fields.GroupBy(f => f.Name.ToLowerInvariant()).All(g => g.Count() == 1));
                Check($"{at}: every condition names fields of the same section", s.Fields.All(f =>
                {
                    List<RuleRef> refs = new List<RuleRef>();
                    f.Shown?.CollectRefs(refs);
                    return refs.All(r => s.Find(r.Property) != null);
                }));
                Check($"{at}: every enum-like condition value is a defined name of its field", s.Fields.All(f =>
                {
                    List<RuleRef> refs = new List<RuleRef>();
                    f.Shown?.CollectRefs(refs);
                    return refs.All(r => r.Names == null || (s.Find(r.Property).EnumNames != null && r.Names.All(n => s.Find(r.Property).EnumNames.Contains(n)))
                        || s.Find(r.Property).Kind == SettingKind.Preset);
                }));
                Check($"{at}: choices are defined names, limits are ordered, enums have equal name/value counts", s.Fields.All(f =>
                    (f.Choices == null || (f.EnumNames != null && f.Choices.All(c => f.EnumNames.Contains(c))))
                    && (!f.Min.HasValue || !f.Max.HasValue || f.Min <= f.Max)
                    && (f.EnumNames == null || (f.EnumValues != null && f.EnumNames.Length == f.EnumValues.Length))));
                Check($"{at}: writable fields have a game member (or are presets/effect flags)", s.Fields.All(f => !f.Writable || f.Member != null || f.Kind == SettingKind.Preset || f.IsEffect));
                Check($"{at}: read-only fields say why", s.Fields.All(f => f.Writable || !string.IsNullOrEmpty(f.ReadOnlyReason)));
                Check($"{at}: implied/guard rules name fields of the section", s.Implied.All(i => s.Find(i.TriggerField) != null && s.Find(i.Field) != null)
                    && s.Guards.All(g => s.Find(g.Field) != null));
            }
            ModSettingField rounds = Sec("Minigame", "minigameSettings").Find("NumberOfRounds");
            Check("NumberOfRounds: int, 1..20, default 1", rounds.Kind == SettingKind.Int && rounds.Min == 1 && rounds.Max == 20 && (double)rounds.Default == 1);
            Check("the minigame player settings table has the 32 control fields of the editor + the player type (6 triggers are read-only)",
                Sec("Minigame", "playerSettings").Fields.Count(f => f.Writable) == 33 && Sec("Minigame", "playerSettings").Fields.Count(f => !f.Writable) == 6);
            Check("minigame settings: 8 writable controls, SpawnType is read-only", Sec("Minigame", "minigameSettings").Fields.Count(f => f.Writable) == 8
                && !Sec("Minigame", "minigameSettings").Find("SpawnType").Writable);
            Check("no table field writes the Workshop item (no WorkshopItem/Meta/publish members)", all.SelectMany(s => s.Fields)
                .All(f => !(f.GameType ?? "").Contains("Workshop") && !(f.Member ?? "").ToLowerInvariant().Contains("publish") && !(f.Member ?? "").ToLowerInvariant().Contains("workshop")));
            Check("fields count: lighting " + Sec("Minigame", "lighting").Fields.Count + ", screenEffects " + Sec("Minigame", "screenEffects").Fields.Count,
                Sec("Minigame", "lighting").Fields.Count == 33 && Sec("Minigame", "screenEffects").Fields.Count == 65);
            Check("lighting and screenEffects are the same for both mod types", Sec("Board", "lighting").Fields.Count == Sec("Minigame", "lighting").Fields.Count
                && Sec("Board", "screenEffects").Fields.Count == Sec("Minigame", "screenEffects").Fields.Count);
            Check("custom spaces are read-only with a reason", !Sec("Board", "customSpaces").Fields[0].Writable && Sec("Board", "customSpaces").Fields[0].ReadOnlyReason.Contains("index"));
        }

        // ------------------------------------------------------------------ engine rules

        private static void EngineRules()
        {
            Console.WriteLine("-- unit: settings engine (rules)");
            FakeState st = new FakeState();
            Check("a plain field is accepted and normalised", Plan("Minigame", "minigameSettings", "{\"RoundDuration\": 90}", st).Assignments.Count == 1
                && (double)Value(Plan("Minigame", "minigameSettings", "{\"RoundDuration\": 90}", st), "RoundDuration") == 90);

            string r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"Nope\": 1}"));
            Check("unknown field -> INVALID_PARAMS naming it and the section", r != null && r.Contains("'Nope'") && r.Contains("minigameSettings") && r.Contains("RoundDuration"));
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"coyoteTime\": 1}"));
            Check("a never-shown member is refused as 'no control in the editor'", r != null && r.Contains("no control") && r.Contains("coyoteTime"));
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"SpawnType\": \"PortalAbove\"}"));
            Check("a read-only field is refused with its reason", r != null && r.Contains("read-only") && r.Contains("ignores"));
            r = Refusal(() => Plan("Minigame", "details", "{\"name\": \"x\"}"));
            Check("the mod name is read-only (renaming is a separate editor action)", r != null && r.Contains("read-only") && r.Contains("renam"));
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"weaponHitTrigger\": {}}"));
            Check("event triggers point at the action tools", r != null && r.Contains("add_action"));
            r = Refusal(() => Plan("Board", "customSpaces", "{\"CustomSpacePrefabs\": []}"));
            Check("custom spaces refuse writes", r != null && r.Contains("read-only"));
            Check("field names match case-insensitively", Plan("Minigame", "minigameSettings", "{\"roundduration\": 90}").Assignments[0].Field.Name == "RoundDuration");
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"RoundDuration\": 90, \"roundduration\": 80}"));
            Check("the same field twice is refused", r != null && r.Contains("twice"));
            r = Refusal(() => SettingsEngine.Plan(Sec("Minigame", "minigameSettings"), new JObject(), st));
            Check("empty values are refused", r != null && r.Contains("at least one"));

            // conditional fields
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"PointsToWin\": 7}"));
            Check("conditional false (default EndConditions = Timer): refused naming the condition", r != null && r.Contains("PointsToWin") && r.Contains("EndConditions is ObtainPoints"));
            Check("conditional satisfied in the same call", Plan("Minigame", "minigameSettings", "{\"PointsToWin\": 7, \"EndConditions\": \"Timer, ObtainPoints\"}").Assignments.Count == 2);
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"PointsToWin\": 7, \"EndConditions\": \"Timer\"}"));
            Check("the call's own controlling value can switch the condition off", r != null && r.Contains("only shown"));
            st.Values["EndConditions"] = new[] { "ObtainPoints" };
            Check("conditional true by current state", Plan("Minigame", "minigameSettings", "{\"PointsToWin\": 7}", st).Assignments.Count == 1);
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"RoundDuration\": 70}", st));
            Check("round duration needs the Timer end condition (state: ObtainPoints)", r != null && r.Contains("Timer") && r.Contains("EndConditions is ObtainPoints"));
            FakeState th = new FakeState();
            th.Values["ChosenPlayerType"] = "ThirdPerson";
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"topDownCameraDistance\": 20}", th));
            Check("camera fields are hidden for ThirdPerson", r != null && r.Contains("ChosenPlayerType is TopDown or TwinStick"));
            Check("camera fields work for TopDown (default) and via the same call", Plan("Minigame", "playerSettings", "{\"topDownCameraDistance\": 20}").Assignments.Count == 1
                && Plan("Minigame", "playerSettings", "{\"topDownCameraDistance\": 20, \"ChosenPlayerType\": \"TwinStick\"}", th).Assignments.Count >= 2);
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"jumpSpeed\": 9, \"canJump\": false}"));
            Check("jumpSpeed together with canJump false is refused", r != null && r.Contains("canJump"));
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"punchStunDuration\": 2, \"canPunch\": true}"));
            Check("punchStunDuration needs canPunch and punchStunsTarget (nested condition)", r != null && r.Contains("punchStunsTarget"));
            Check("nested condition satisfied by the call", Plan("Minigame", "playerSettings", "{\"punchStunDuration\": 2, \"canPunch\": true, \"punchStunsTarget\": true}").Assignments.Count == 3);
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"health\": 50}"));
            Check("health needs useHealth", r != null && r.Contains("useHealth"));
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"topDownCamerafollowAllAreaRect\": {\"x\":0,\"y\":0,\"width\":5,\"height\":5}}"));
            Check("follow area needs FollowAll", r != null && r.Contains("FollowAll"));

            // choices
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"nameTagType\": \"ScreenSpace\"}"));
            Check("restricted choices: nameTagType ScreenSpace is not offered", r != null && r.Contains("does not offer") && r.Contains("WorldSpaceScreenSize"));
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"nameTagType\": \"Disabled\"}"));
            Check("restricted choices: Disabled is not offered (use showNameTags false)", r != null && r.Contains("does not offer"));
            Check("an offered choice works (case-insensitive) and numbers work", Value(Plan("Minigame", "playerSettings", "{\"nameTagType\": \"worldspaceworldsize\"}"), "nameTagType").Equals("WorldSpaceWorldSize")
                && Value(Plan("Minigame", "playerSettings", "{\"nameTagType\": 2}"), "nameTagType").Equals("WorldSpaceWorldSize"));
            r = Refusal(() => Plan("Minigame", "lighting", "{\"preset\": \"Custom\", \"ambientMode\": 2}"));
            Check("undefined enum value (ambientMode 2) is refused", r != null && r.Contains("not a defined"));
            r = Refusal(() => Plan("Minigame", "lighting", "{\"preset\": \"Custom\", \"shadowCascades\": 3}"));
            Check("undefined shadowCascades 3 is refused", r != null && r.Contains("not a defined"));
            Check("shadowCascades accepts 0, 2, 4 and names", Value(Plan("Minigame", "lighting", "{\"preset\": \"Custom\", \"shadowCascades\": 2}"), "shadowCascades").Equals("Two")
                && Value(Plan("Minigame", "lighting", "{\"preset\": \"Custom\", \"shadowCascades\": \"Zero\"}"), "shadowCascades").Equals("Zero"));
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"PlacementCondition\": \"Banana\"}"));
            Check("unknown enum name is refused naming the field", r != null && r.Contains("PlacementCondition") && r.Contains("MostPoints"));

            // clamps
            SettingsPlan c = Plan("Minigame", "minigameSettings", "{\"NumberOfRounds\": 0}");
            Check("NumberOfRounds 0 is clamped to 1 and reported (the validator never sees 0)", (double)Value(c, "NumberOfRounds") == 1 && c.Clamped.Count == 1 && c.Clamped[0].Field == "NumberOfRounds"
                && (int)c.Clamped[0].Requested == 0 && (int)c.Clamped[0].Applied == 1 && c.Clamped[0].Min == 1 && c.Clamped[0].Max == 20);
            c = Plan("Minigame", "minigameSettings", "{\"NumberOfRounds\": 99}");
            Check("NumberOfRounds 99 -> 20", (double)Value(c, "NumberOfRounds") == 20 && c.Clamped.Count == 1);
            c = Plan("Minigame", "minigameSettings", "{\"RoundDuration\": 1}");
            Check("RoundDuration 1 -> 5", (double)Value(c, "RoundDuration") == 5 && c.Clamped.Count == 1);
            c = Plan("Minigame", "minigameSettings", "{\"RoundDuration\": 60}");
            Check("a value in range is not reported", c.Clamped.Count == 0);
            c = Plan("Minigame", "playerSettings", "{\"playerGravity\": -500, \"jumpCount\": 0, \"health\": 5000, \"useHealth\": true}");
            Check("several clamps in one call: gravity -100, jumpCount 1, health 1000", c.Clamped.Count == 3 && (double)Value(c, "playerGravity") == -100 && (double)Value(c, "jumpCount") == 1 && (double)Value(c, "health") == 1000);
            c = Plan("Minigame", "lighting", "{\"preset\": \"Custom\", \"shadowDistance\": 1000}");
            Check("shadowDistance 1000 -> 250", (double)Value(c, "shadowDistance") == 250 && c.Clamped.Count == 1);
            c = Plan("Minigame", "music", "{\"MusicVolume\": 3}", new FakeState());
            Check("volume 3 -> 1", (double)Value(c, "MusicVolume") == 1 && c.Clamped.Count == 1);
            c = Plan("Minigame", "playerSettings", "{\"ChosenPlayerType\": \"TopDown\", \"topDownCameraMode\": \"FollowAll\", \"topDownCamerafollowAllAreaRect\": {\"x\": -3000, \"y\": 0, \"width\": 5000, \"height\": 10}}");
            double[] rect = (double[])Value(c, "topDownCamerafollowAllAreaRect");
            Check("follow rect: width clamped to 2000, centre kept inside (x=-1000, y=0)", rect[2] == 2000 && rect[3] == 10 && rect[0] == -1000 && rect[1] == 0 && c.Clamped.Any(x => x.Field == "topDownCamerafollowAllAreaRect"));
            FakeState circle = new FakeState();
            circle.Values["topDownCameraFollowAllType"] = "Circle";
            c = Plan("Minigame", "playerSettings", "{\"topDownCameraMode\": \"FollowAll\", \"topDownCamerafollowAllAreaRect\": {\"x\": 0, \"y\": 0, \"width\": 10, \"height\": 4}}", circle);
            rect = (double[])Value(c, "topDownCamerafollowAllAreaRect");
            Check("follow rect for Circle: height := width, centre kept (centre 5,2 -> x 0, y -3)", rect[2] == 10 && rect[3] == 10 && Math.Abs(rect[0]) < 1e-6 && Math.Abs(rect[1] - -3) < 1e-6);
            FakeState custom = new FakeState();
            custom.Values["UseCustomBoardSize"] = true;
            c = Plan("Board", "boardSize", "{\"mapExtents\": {\"x\": 0, \"y\": 0, \"width\": 40, \"height\": 40}}", custom);
            Check("board extents inside the limits are untouched", c.Clamped.Count == 0);
            r = Refusal(() => Plan("Board", "boardSize", "{\"mapExtents\": {\"x\": 0, \"y\": 0, \"width\": 40, \"height\": 40}}"));
            Check("board extents need UseCustomBoardSize (default off)", r != null && r.Contains("UseCustomBoardSize"));
            Check("board extents together with UseCustomBoardSize: true work", Plan("Board", "boardSize", "{\"UseCustomBoardSize\": true, \"mapExtents\": {\"x\": 0, \"y\": 0, \"width\": 40, \"height\": 40}}").Assignments.Count == 2);
            c = Plan("Board", "details", "{\"defaultGoalChestCount\": 12}");
            Check("trophy chests 12 -> 8", (double)Value(c, "defaultGoalChestCount") == 8);

            // the Follow camera guard
            FakeState follow = new FakeState();
            follow.DerivedValues["hasPlayerSpawn"] = false;
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"topDownCameraMode\": \"Follow\"}", follow));
            Check("camera mode Follow without a Player Spawn is refused with the reason", r != null && r.Contains("Player Spawn") && r.Contains("throws"));
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"topDownCameraMode\": \"follow\"}", follow));
            Check("... also in another letter case", r != null && r.Contains("Player Spawn"));
            follow.DerivedValues["hasPlayerSpawn"] = true;
            Check("camera mode Follow with a Player Spawn is accepted", Plan("Minigame", "playerSettings", "{\"topDownCameraMode\": \"Follow\"}", follow).Assignments.Count == 1);
            follow.DerivedValues["hasPlayerSpawn"] = null;
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"topDownCameraMode\": \"Follow\"}", follow));
            Check("an unanswerable spawn question fails closed", r != null);
            follow.DerivedValues["hasPlayerSpawn"] = false;
            Check("Static and FollowAll never need a spawn", Plan("Minigame", "playerSettings", "{\"topDownCameraMode\": \"Static\"}", follow).Assignments.Count == 1
                && Plan("Minigame", "playerSettings", "{\"topDownCameraMode\": \"FollowAll\"}", follow).Assignments.Count == 1);
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"ChosenPlayerType\": \"ThirdPerson\", \"topDownCameraMode\": \"Follow\"}", follow));
            Check("Follow together with ThirdPerson is refused (camera fields hidden)", r != null && r.Contains("only shown"));
        }

        // ------------------------------------------------------------------ parsing

        private static void EngineParsing()
        {
            Console.WriteLine("-- unit: settings engine (value shapes)");
            string r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"NumberOfRounds\": \"three\"}"));
            Check("wrong type (string for int) names the field", r != null && r.Contains("NumberOfRounds"));
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"NumberOfRounds\": 2.5}"));
            Check("2.5 for an int is refused", r != null);
            Check("2.0 for an int is accepted", (double)Value(Plan("Minigame", "minigameSettings", "{\"NumberOfRounds\": 2.0}"), "NumberOfRounds") == 2);
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"ShowPlayerScore\": 1}"));
            Check("1 for a bool is refused", r != null && r.Contains("ShowPlayerScore"));
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"RoundDuration\": \"60\"}"));
            Check("string for a float is refused", r != null);
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"movementSpeed\": null}"));
            Check("null for a float is refused", r != null);
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"topDownCameraPositionOffset\": {\"x\": 1}}"));
            Check("incomplete vector is refused", r != null && r.Contains("topDownCameraPositionOffset"));
            Check("vector accepts {x,y,z} and [x,y,z]", ((double[])Value(Plan("Minigame", "playerSettings", "{\"topDownCameraPositionOffset\": [1,2,3]}"), "topDownCameraPositionOffset")).SequenceEqual(new double[] { 1, 2, 3 }));
            r = Refusal(() => Plan("Minigame", "details", "{\"description\": 5}"));
            Check("number for a string is refused", r != null && r.Contains("description"));
            r = Refusal(() => Plan("Minigame", "details", "{\"description\": \"" + new string('x', 20000) + "\"}"));
            Check("a 20000-character description is refused (limit 16384)", r != null);
            Check("a multi-line description is fine", Value(Plan("Minigame", "details", "{\"description\": \"a\\nb\"}"), "description").Equals("a\nb"));

            // flags
            string[] flags(string json) => (string[])Value(Plan("Minigame", "minigameSettings", json), "EndConditions");
            Check("flags: comma string", flags("{\"EndConditions\": \"ObtainPoints, Timer\"}").SequenceEqual(new[] { "Timer", "ObtainPoints" }));
            Check("flags: array", flags("{\"EndConditions\": [\"RemainingPlayersAlive\"]}").SequenceEqual(new[] { "RemainingPlayersAlive" }));
            Check("flags: integer", flags("{\"EndConditions\": 12}").SequenceEqual(new[] { "RemainingPlayersAlive", "FinishMinigame" }));
            Check("flags: \"\", \"None\", [] and 0 all mean no end condition", flags("{\"EndConditions\": \"\"}").Length == 0 && flags("{\"EndConditions\": \"None\"}").Length == 0
                && flags("{\"EndConditions\": []}").Length == 0 && flags("{\"EndConditions\": 0}").Length == 0);
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"EndConditions\": \"Timer, Banana\"}"));
            Check("flags: unknown name refused", r != null && r.Contains("Banana"));
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"EndConditions\": 16}"));
            Check("flags: undefined bit refused", r != null);
            r = Refusal(() => Plan("Minigame", "minigameSettings", "{\"EndConditions\": \"None\", \"PointsToWin\": 3}"));
            Check("no end condition hides PointsToWin / RoundDuration / MinPlayersAlive", r != null && r.Contains("only shown"));

            // assets
            FakeState st = new FakeState();
            string good = "11111111-1111-1111-1111-111111111111";
            string bad = "22222222-2222-2222-2222-222222222222";
            st.Assets.Add(good);
            st.BadAssets[bad] = "'Boom' is a Mesh, expected AudioClip";
            Check("asset: a valid guid is accepted (string and {guid})", ((AssetValue)Value(Plan("Minigame", "music", "{\"Music\": \"" + good + "\"}", st), "Music")).Guid == good
                && ((AssetValue)Value(Plan("Minigame", "music", "{\"Music\": {\"guid\": \"" + good + "\"}}", st), "Music")).Guid == good);
            Check("asset: null clears (music: none)", ((AssetValue)Value(Plan("Minigame", "music", "{\"Music\": null}", st), "Music")).Guid == null);
            r = Refusal(() => Plan("Minigame", "music", "{\"Music\": \"" + bad + "\"}", st));
            Check("asset of the wrong type is refused with the game's message", r != null && r.Contains("Mesh") && r.Contains("Music"));
            r = Refusal(() => Plan("Minigame", "music", "{\"Music\": \"33333333-3333-3333-3333-333333333333\"}", st));
            Check("asset: a guid that is no asset is refused", r != null && r.Contains("no asset"));
            r = Refusal(() => Plan("Minigame", "music", "{\"Music\": \"not-a-guid\"}", st));
            Check("asset: a non-guid string is refused", r != null && r.Contains("GUID"));
            Check("preview image accepts a texture guid and null", Plan("Minigame", "details", "{\"previewImage\": \"" + good + "\"}", st).Assignments.Count == 1 && Plan("Minigame", "details", "{\"previewImage\": null}", st).Assignments.Count == 1);

            // all-or-nothing: one bad value among good ones fails the whole call before anything is applied
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"movementSpeed\": 5, \"stepOffset\": 1, \"canJump\": \"yes\"}"));
            Check("all-or-nothing: one wrong type fails the call (plan is pure, nothing to apply)", r != null && r.Contains("canJump"));
            r = Refusal(() => Plan("Minigame", "playerSettings", "{\"movementSpeed\": 5, \"health\": 50}"));
            Check("all-or-nothing: one hidden conditional fails the call", r != null && r.Contains("health"));
        }

        // ------------------------------------------------------------------ presets

        private static void Presets()
        {
            Console.WriteLine("-- unit: settings engine (presets)");
            FakeState st = new FakeState();
            Check("a listed preset is accepted", Value(Plan("Minigame", "lighting", "{\"preset\": \"Night\"}", st), "preset").Equals("Night"));
            Check("preset names match case-insensitively and are normalised to the list's spelling", Value(Plan("Minigame", "lighting", "{\"preset\": \"night\"}", st), "preset").Equals("Night"));
            string r = Refusal(() => Plan("Minigame", "lighting", "{\"preset\": \"Sunset\"}", st));
            Check("an unknown preset is refused and the choices are listed", r != null && r.Contains("Sunset") && r.Contains("Day") && r.Contains("Custom"));
            r = Refusal(() => Plan("Minigame", "screenEffects", "{\"preset\": \"Vaporwave\"}", st));
            Check("unknown screen-effects preset refused (the editor would throw)", r != null && r.Contains("Noir"));
            r = Refusal(() => Plan("Minigame", "lighting", "{\"preset\": 3}", st));
            Check("a number is no preset name", r != null);
            st.LightingPresets = null;
            r = SettingsErrorCode(() => Plan("Minigame", "lighting", "{\"preset\": \"Night\"}", st));
            Check("an unreadable preset list is INTERNAL, not a guess", r == ErrorCodes.Internal);
            st.LightingPresets = new List<string> { "Day", "Custom" };

            // custom values need the Custom preset (UI parity)
            r = Refusal(() => Plan("Minigame", "lighting", "{\"topColor\": {\"r\":1,\"g\":0,\"b\":0}}", st));
            Check("custom lighting values are refused while a preset is selected (default preset 'Default')", r != null && r.Contains("preset is Custom"));
            Check("custom lighting values are accepted in the same call as preset Custom", Plan("Minigame", "lighting", "{\"preset\": \"Custom\", \"topColor\": {\"r\":1,\"g\":0,\"b\":0}}", st).Assignments.Count == 2);
            st.Values["preset"] = "Custom";
            Check("custom lighting values are accepted while Custom is selected", Plan("Minigame", "lighting", "{\"topColor\": {\"r\":1,\"g\":0,\"b\":0,\"a\":0.5}}", st).Assignments.Count == 1);
            r = Refusal(() => Plan("Minigame", "lighting", "{\"preset\": \"Day\", \"stars\": true}", st));
            Check("switching to a preset and editing custom values in one call is refused", r != null && r.Contains("preset is Custom") && r.Contains("preset is Day"));
            double[] col = (double[])Value(Plan("Minigame", "lighting", "{\"topColor\": {\"r\":1,\"g\":0.5,\"b\":0}}", st), "topColor");
            Check("a color without alpha defaults to a = 1", col.Length == 4 && col[3] == 1);
            r = Refusal(() => Plan("Minigame", "lighting", "{\"fogColor\": {\"r\":1,\"g\":0.5,\"b\":0}}", st));
            Check("fog colour needs fog on", r != null && r.Contains("fog is on"));
            r = Refusal(() => Plan("Minigame", "lighting", "{\"fog\": true, \"fogDensity\": 0.1}", st));
            Check("fog density needs a non-linear fog mode (default Linear)", r != null && r.Contains("fogMode"));
            Check("fog density with fogMode Exponential works", Plan("Minigame", "lighting", "{\"fog\": true, \"fogMode\": \"Exponential\", \"fogDensity\": 0.1}", st).Assignments.Count == 3);
            r = Refusal(() => Plan("Minigame", "lighting", "{\"cloudSpeed1\": 1}", st));
            Check("cloud settings need cloud shadows", r != null && r.Contains("cloudShadows"));
            r = Refusal(() => Plan("Minigame", "lighting", "{\"globalLightShadows\": \"None\", \"cloudShadows\": true}", st));
            Check("cloud shadows need sun shadows (the UI shows a hint instead)", r != null && r.Contains("globalLightShadows is not None"));
            Check("ambient gradient colours need Gradient", Refusal(() => Plan("Minigame", "lighting", "{\"ambientEquatorColor\": {\"r\":0,\"g\":0,\"b\":0}}", st)) != null
                && Plan("Minigame", "lighting", "{\"ambientMode\": \"Gradient\", \"ambientEquatorColor\": {\"r\":0,\"g\":0,\"b\":0}}", st).Assignments.Count == 2);
            Check("ambientSkyColor is also used by the Color mode", Plan("Minigame", "lighting", "{\"ambientMode\": \"Color\", \"ambientSkyColor\": {\"r\":0,\"g\":0,\"b\":0}}", st).Assignments.Count == 2);
        }

        private static string SettingsErrorCode(Func<object> f)
        {
            try
            {
                f();
                return null;
            }
            catch (RpcException e)
            {
                return e.Code;
            }
        }

        // ------------------------------------------------------------------ screen effects

        private static void ScreenEffects()
        {
            Console.WriteLine("-- unit: settings engine (screen effects)");
            FakeState st = new FakeState();
            st.Values["preset"] = "Custom";
            string r = Refusal(() => Plan("Minigame", "screenEffects", "{\"vignette.intensity\": 0.5}", st));
            Check("a parameter of an absent effect is refused (add the effect first)", r != null && r.Contains("vignette is on"));
            SettingsPlan p = Plan("Minigame", "screenEffects", "{\"vignette\": true, \"vignette.intensity\": 0.5}", st);
            ParamValue pv = (ParamValue)Value(p, "vignette.intensity");
            Check("adding an effect and setting its parameter in one call works; a bare value switches the override on", p.Assignments.Count == 2 && (double)pv.Value == 0.5 && pv.Override == true);
            st.Values["vignette"] = true;
            pv = (ParamValue)Value(Plan("Minigame", "screenEffects", "{\"vignette.intensity\": {\"override\": false}}", st), "vignette.intensity");
            Check("{override:false} switches the override off and keeps the value", pv.Override == false && pv.Value == null);
            pv = (ParamValue)Value(Plan("Minigame", "screenEffects", "{\"vignette.intensity\": {\"value\": 0.4}}", st), "vignette.intensity");
            Check("{value} alone switches the override on", pv.Override == true && Math.Abs((double)pv.Value - 0.4) < 1e-6);
            pv = (ParamValue)Value(Plan("Minigame", "screenEffects", "{\"vignette.intensity\": {\"value\": 0.4, \"override\": false}}", st), "vignette.intensity");
            Check("{value, override:false} keeps the explicit flag", pv.Override == false && Math.Abs((double)pv.Value - 0.4) < 1e-6);
            Check("parameter errors name the field", Refusal(() => Plan("Minigame", "screenEffects", "{\"vignette.intensity\": null}", st))?.Contains("vignette.intensity") == true
                && Refusal(() => Plan("Minigame", "screenEffects", "{\"vignette.intensity\": {\"value\": \"x\"}}", st))?.Contains("vignette.intensity.value") == true
                && Refusal(() => Plan("Minigame", "screenEffects", "{\"vignette.intensity\": {\"foo\": 1, \"value\": 1}}", st)) != null
                && Refusal(() => Plan("Minigame", "screenEffects", "{\"vignette.intensity\": {\"override\": 1}}", st)) != null);

            // slider limits: from the game's Range attribute at run time, else the documented range; free fields never clamp
            SettingsPlan c = Plan("Minigame", "screenEffects", "{\"vignette.intensity\": 5}", st);
            Check("slider parameter is clamped to the documented range without a runtime answer", c.Clamped.Count == 1 && ((ParamValue)Value(c, "vignette.intensity")).Value.Equals(1.0));
            st.Limits = f => f.Name == "vignette.intensity" ? new[] { 0.0, 2.0 } : null;
            c = Plan("Minigame", "screenEffects", "{\"vignette.intensity\": 5}", st);
            Check("the runtime RangeAttribute limits win over the documented ones", c.Clamped.Count == 1 && ((ParamValue)Value(c, "vignette.intensity")).Value.Equals(2.0));
            st.Values["bloom"] = true;
            c = Plan("Minigame", "screenEffects", "{\"bloom.BloomStrength\": 500}", st);
            Check("a free-input parameter (no slider) is not clamped", c.Clamped.Count == 0 && ((ParamValue)Value(c, "bloom.BloomStrength")).Value.Equals(500.0));
            c = Plan("Minigame", "screenEffects", "{\"bloom.BloomThreshold\": 9}", st);
            Check("bloom threshold slider: 0..2", ((ParamValue)Value(c, "bloom.BloomThreshold")).Value.Equals(2.0));
            c = Plan("Minigame", "screenEffects", "{\"bloom.BloomUpsampleFactor\": 9}", st);
            Check("bloom upsample factor is free input in the UI (even though the field has a range)", c.Clamped.Count == 0);

            // conditions between parameters
            st.Values["ambientOcclusion"] = true;
            st.Values["ambientOcclusion.mode"] = new ParamValue { Value = "ScalableAmbientObscurance", Override = true };
            Check("AO radius is offered in Scalable mode, thickness is not", Plan("Minigame", "screenEffects", "{\"ambientOcclusion.radius\": 2}", st).Assignments.Count == 1
                && Refusal(() => Plan("Minigame", "screenEffects", "{\"ambientOcclusion.thicknessModifier\": 2}", st))?.Contains("ambientOcclusion.mode") == true);
            Check("AO thickness in the same call as mode MultiScaleVolumetricObscurance", Plan("Minigame", "screenEffects",
                "{\"ambientOcclusion.mode\": \"MultiScaleVolumetricObscurance\", \"ambientOcclusion.thicknessModifier\": 2}", st).Assignments.Count == 2);

            // presets switch the editors off
            st.Values["preset"] = "Noir";
            r = Refusal(() => Plan("Minigame", "screenEffects", "{\"vignette.intensity\": 0.3}", st));
            Check("custom effects cannot be edited while a preset is selected", r != null && r.Contains("preset is Custom"));
            r = Refusal(() => Plan("Minigame", "screenEffects", "{\"vignette\": true}", st));
            Check("... nor added", r != null && r.Contains("preset is Custom"));
            Check("vignette center is a vector2 param, colorFilter a color param",
                Sec("Minigame", "screenEffects").Find("vignette.center").Kind == SettingKind.Vector2 && Sec("Minigame", "screenEffects").Find("colorGrading.colorFilter").Kind == SettingKind.Color);
            Check("every effect has a presence flag, an enabled flag and parameters", SettingsRules.EffectTypeNames.All(t =>
            {
                string f = SettingsRules.EffectFieldName(t);
                ModSettingField present = Sec("Minigame", "screenEffects").Find(f);
                return present != null && present.IsEffect && Sec("Minigame", "screenEffects").Find(f + ".enabled") != null
                    && Sec("Minigame", "screenEffects").Fields.Count(x => x.IsParam && x.Name.StartsWith(f + ".", StringComparison.Ordinal)) >= 1;
            }));
            Check("slider limits documented for every slider parameter are inside the attribute-style ranges", Sec("Minigame", "screenEffects").Fields.Where(f => f.RangeFromAttribute).All(f => f.Min.HasValue && f.Max.HasValue));
            Check("the effect types are the 8 the Add Effect menu offers", SettingsRules.EffectTypeNames.OrderBy(x => x).SequenceEqual(new[]
                { "AmbientOcclusion", "ChromaticAberration", "ColorGrading", "ComputeBloom", "DepthOfField", "Grain", "LensDistortion", "Vignette" }));
        }

        // ------------------------------------------------------------------ implied writes

        private static void Implied()
        {
            Console.WriteLine("-- unit: settings engine (implied writes)");
            FakeState st = new FakeState();
            st.Values["topDownCameraMode"] = "FollowAll";
            SettingsPlan p = Plan("Minigame", "playerSettings", "{\"ChosenPlayerType\": \"ThirdPerson\"}", st);
            Check("ThirdPerson forces the camera mode to Static (implied, reported)", p.Implied.Count() == 1 && Value(p, "topDownCameraMode").Equals("Static") && p.Implied.First().Why.Contains("Static"));
            p = Plan("Minigame", "playerSettings", "{\"ChosenPlayerType\": \"ThirdPerson\"}");
            Check("... not when the mode already is Static", !p.Implied.Any());
            p = Plan("Minigame", "playerSettings", "{\"ChosenPlayerType\": \"TwinStick\"}", st);
            Check("TwinStick implies nothing", !p.Implied.Any());

            FakeState cam = new FakeState();
            cam.Values["CameraMode"] = "Perspective";
            p = Plan("Board", "camera", "{\"CameraMode\": \"Orthographic\"}", cam);
            Check("board camera: changing the mode resets the distance to 40 (Orthographic)", p.Implied.Count() == 1 && (double)Value(p, "CameraDistance") == 40);
            p = Plan("Board", "camera", "{\"CameraMode\": \"Perspective\"}", cam);
            Check("... same mode: nothing implied", !p.Implied.Any());
            cam.Values["CameraMode"] = "Orthographic";
            cam.Values["CameraDistance"] = 40.0;
            p = Plan("Board", "camera", "{\"CameraMode\": \"Perspective\"}", cam);
            Check("... back to Perspective: distance 13", (double)Value(p, "CameraDistance") == 13);
            p = Plan("Board", "camera", "{\"CameraMode\": \"Perspective\", \"CameraDistance\": 9}", cam);
            Check("an explicit distance in the same call wins over the reset", (double)Value(p, "CameraDistance") == 9 && !p.Implied.Any());
            string r = Refusal(() => Plan("Board", "camera", "{\"CameraMode\": \"Orthographic\", \"CameraDistance\": 9}", cam));
            Check("the distance cannot be set in Orthographic mode (no control)", r != null && r.Contains("CameraMode is Perspective"));
            r = Refusal(() => Plan("Board", "camera", "{\"CameraSize\": 6}", new FakeState()));
            Check("view size needs Orthographic", r != null && r.Contains("Orthographic"));
            p = Plan("Board", "camera", "{\"CameraViewAngle\": 5}");
            Check("elevation angle clamped to 14..90", (double)Value(p, "CameraViewAngle") == 14 && p.Clamped.Count == 1);
        }

        // ------------------------------------------------------------------ round trip

        private static void RoundTrip()
        {
            Console.WriteLine("-- unit: settings round trip (get shape -> set input)");
            int fields = 0;
            List<string> failures = new List<string>();
            foreach (SettingsSection s in SettingsRules.All)
            {
                FakeState st = new FakeState();
                st.Assets.Add("13dbf0ac-803d-49d1-997f-0a559bd43061");
                st.Assets.Add("90abde3b-7f29-43ee-80c9-40ec418d5993");
                st.Assets.Add("3ac2bff4-9bc6-406e-8e6f-b725dc69e08a");
                st.Assets.Add("05f5b5c8-5190-49d7-ad7a-f154db4e4f29");
                foreach (ModSettingField f in s.Fields.Where(x => x.Writable))
                {
                    object normalized;
                    if (f.Kind == SettingKind.Preset)
                    {
                        normalized = SettingsRules.Custom;
                    }
                    else if (f.IsEffect)
                    {
                        normalized = true;
                    }
                    else if (f.IsParam)
                    {
                        object inner = SampleValue(f);
                        normalized = new ParamValue { Value = inner, Override = true };
                    }
                    else
                    {
                        normalized = SampleValue(f);
                    }
                    JToken shape = SettingsEngine.ToJson(f, normalized);
                    object parsed;
                    try
                    {
                        parsed = SettingsEngine.ParseValue(f, shape, "values." + f.Name, st);
                    }
                    catch (RpcException e)
                    {
                        failures.Add(s.ModType + "/" + s.Name + "." + f.Name + ": " + e.Message);
                        continue;
                    }
                    fields++;
                    bool equal = SettingsEngine.ValuesEqual(f, parsed, normalized) || f.Kind == SettingKind.Asset;
                    if (!equal)
                    {
                        failures.Add(s.ModType + "/" + s.Name + "." + f.Name + ": parsed value differs");
                    }
                }
            }
            Check($"every writable field's get shape parses as set input and gives the same value ({fields} fields; failures: {string.Join(" | ", failures.Take(5).ToArray())})", failures.Count == 0 && fields > 250);

            // defaults documented in the table are valid values of their own field
            List<string> badDefaults = new List<string>();
            foreach (SettingsSection s in SettingsRules.All)
            {
                foreach (ModSettingField f in s.Fields.Where(x => x.Writable && x.HasDefault && !x.IsParam))
                {
                    FakeState st = new FakeState();
                    st.Assets.Add((f.Default as string) ?? "x");
                    st.LightingPresets = new List<string> { "Default", "Custom" };
                    st.EffectPresets = new List<string> { "Default", "Custom" };
                    try
                    {
                        SettingsEngine.ParseValue(f, SettingsEngine.ToJson(f, f.Kind == SettingKind.Asset ? new AssetValue { Guid = (string)f.Default } : f.Default), "d", st);
                    }
                    catch (RpcException e)
                    {
                        badDefaults.Add(s.Name + "." + f.Name + ": " + e.Message);
                    }
                    if (f.Min.HasValue && f.Max.HasValue && f.Default is double d && (d < f.Min.Value || d > f.Max.Value))
                    {
                        badDefaults.Add(s.Name + "." + f.Name + ": default outside its limits");
                    }
                }
            }
            Check("table defaults are valid and inside their limits" + (badDefaults.Count > 0 ? ": " + string.Join(" | ", badDefaults.Take(5).ToArray()) : ""), badDefaults.Count == 0);
        }

        private static object SampleValue(ModSettingField f)
        {
            switch (f.Kind)
            {
                case SettingKind.Bool: return true;
                case SettingKind.Int: return f.Min.HasValue ? Math.Ceiling(f.Min.Value) : 3.0;
                case SettingKind.Float: return f.Min.HasValue ? (double)(float)f.Min.Value : 0.25;
                case SettingKind.String: return "sample";
                case SettingKind.Enum:
                case SettingKind.NamedInt: return f.OfferedNames[0];
                case SettingKind.Flags: return new[] { f.EnumNames[0], f.EnumNames[f.EnumNames.Length - 1] };
                case SettingKind.Vector2: return new[] { 0.25, -0.5 };
                case SettingKind.Vector3: return new[] { 1.0, 2.0, 3.0 };
                case SettingKind.Rect: return new[] { -5.0, -5.0, 10.0, 10.0 };
                case SettingKind.Color: return new[] { 0.25, 0.5, 0.75, 1.0 };
                case SettingKind.Asset: return new AssetValue { Guid = (string)f.Default ?? "13dbf0ac-803d-49d1-997f-0a559bd43061" };
                default: return null;
            }
        }

        // ------------------------------------------------------------------ JSON shapes

        private static void Shapes()
        {
            Console.WriteLine("-- unit: settings JSON (list / get)");
            FakeState st = new FakeState();
            st.Values["EndConditions"] = new[] { "Timer", "ObtainPoints" };
            st.Values["RoundDuration"] = 90.0;
            JObject list = SettingsEngine.SectionListJson(Sec("Minigame", "minigameSettings"), st);
            JObject pts = (JObject)((JArray)list["fields"]).First(x => (string)x["name"] == "PointsToWin");
            Check("list: field has name, type, shape, default, value, shown, shownWhen, limits, writable",
                (string)pts["type"] == "int" && pts["shape"] != null && (int)pts["default"] == 5 && pts["shownWhen"] != null && (bool)pts["shown"] && (int)pts["limits"]["min"] == 1 && (int)pts["limits"]["max"] == 1000000 && (bool)pts["writable"]);
            JObject dur = (JObject)((JArray)list["fields"]).First(x => (string)x["name"] == "RoundDuration");
            Check("list: current value is the game's (90) and shown", (double)dur["value"] == 90 && (bool)dur["shown"]);
            JObject mp = (JObject)((JArray)list["fields"]).First(x => (string)x["name"] == "MinPlayersAlive");
            Check("list: a conditional field that is not shown says shown:false", (bool)mp["shown"] == false);
            JObject end = (JObject)((JArray)list["fields"]).First(x => (string)x["name"] == "EndConditions");
            Check("list: flags show choices and the current value as a comma string", ((JArray)end["choices"]).Count == 4 && (string)end["value"] == "Timer, ObtainPoints");
            JObject spawn = (JObject)((JArray)list["fields"]).First(x => (string)x["name"] == "SpawnType");
            Check("list: read-only fields say writable false with a reason", (bool)spawn["writable"] == false && ((string)spawn["reason"]).Length > 5);
            JObject nt = (JObject)((JArray)SettingsEngine.SectionListJson(Sec("Minigame", "playerSettings"), st)["fields"]).First(x => (string)x["name"] == "nameTagType");
            Check("list: restricted choices list only the offered values", ((JArray)nt["choices"]).Count == 3 && !((JArray)nt["choices"]).Any(x => (string)x == "Disabled"));
            JObject lighting = SettingsEngine.SectionListJson(Sec("Minigame", "lighting"), st);
            Check("list: preset sections carry the preset names", ((JArray)lighting["presets"]).Select(x => (string)x).SequenceEqual(new[] { "Day", "Night", "Custom" }));
            JObject music = SettingsEngine.SectionListJson(Sec("Minigame", "music"), st);
            JObject mus = (JObject)((JArray)music["fields"]).First(x => (string)x["name"] == "Music");
            Check("list: asset pickers say which asset types and locations", (string)mus["assets"]["types"][0] == "AudioClip" && ((string)mus["assets"]["locations"]).Contains("internal") && (bool)mus["assets"]["nullAllowed"]);
            JObject get = SettingsEngine.SectionValuesJson(Sec("Minigame", "minigameSettings"), st);
            Check("get: values by field name; SpawnType is omitted", (double)get["RoundDuration"] == 90 && get["SpawnType"] == null && (string)get["EndConditions"] == "Timer, ObtainPoints");
            JObject getEffects = SettingsEngine.SectionValuesJson(Sec("Minigame", "screenEffects"), st);
            Check("get: parameters of absent effects are omitted, presence flags are not", getEffects["vignette"] != null && !(bool)getEffects["vignette"] && getEffects["vignette.intensity"] == null);
            st.Values["vignette"] = true;
            st.Values["vignette.intensity"] = new ParamValue { Value = 0.4, Override = true };
            getEffects = SettingsEngine.SectionValuesJson(Sec("Minigame", "screenEffects"), st);
            Check("get: a parameter is {value, override}", (double)getEffects["vignette.intensity"]["value"] > 0.39 && (bool)getEffects["vignette.intensity"]["override"]);
            Check("clamp report shape: {field, requested, applied, min, max}", Plan("Minigame", "minigameSettings", "{\"NumberOfRounds\": 0}").Clamped[0].Reason != null);
        }

        // ------------------------------------------------------------------ batch, guards, unsaved edits

        private static void BatchAndGuards()
        {
            Console.WriteLine("-- unit: settings tools in batch / guard / tracker");
            Check("run_batch allows set_mod_settings, get_mod_settings and list_mod_settings", BatchRules.Refusal("set_mod_settings", true) == null
                && BatchRules.Refusal("get_mod_settings", true) == null && BatchRules.Refusal("list_mod_settings", true) == null);
            string why = BatchRules.Refusal("capture_preview_image", true);
            Check("run_batch refuses capture_preview_image with the reason", why != null && why.Contains("several frames"));
            Check("run_batch's refusal text names set_mod_settings among the allowed tools", BatchRules.Refusal("ping", true).Contains("set_mod_settings"));

            string plugin = FindPluginDir();
            if (plugin == null)
            {
                Check("plugin sources located", false);
                return;
            }
            string src = File.ReadAllText(Path.Combine(plugin, "Tools", "SettingsTools.cs"));
            Check("set_mod_settings and capture_preview_image are write tools behind the OpenMod guard",
                src.Contains("ToolDefinition.Write(\"set_mod_settings\", WriteGuard.OpenMod()") && src.Contains("ToolDefinition.Write(\"capture_preview_image\", WriteGuard.OpenMod()"));
            Check("list_mod_settings and get_mod_settings are read tools", src.Contains("ToolDefinition.Read(\"list_mod_settings\"") && src.Contains("ToolDefinition.Read(\"get_mod_settings\""));
            Check("neither guard allows play mode (WriteGuard.OpenMod() without allowPlayMode)", !src.Contains("AllowPlayMode") && !src.Contains("OpenMod(true)") && !src.Contains("OpenMod(allowPlayMode"));
            Check("no Workshop item member is touched by the settings code", !File.ReadAllText(Path.Combine(plugin, "Game", "SettingsAccess.cs")).Contains("workshopItem")
                && !src.Contains("workshopItem") && !src.Contains("WorkshopItem"));
            Check("the settings code calls no disk-writing editor handler (profile export/delete, rename, icon)", !src.Contains("SaveLightingSettings") && !src.Contains("RenameMod")
                && !File.ReadAllText(Path.Combine(plugin, "Game", "SettingsAccess.cs")).Contains("RenameMod") && !File.ReadAllText(Path.Combine(plugin, "Game", "SettingsAccess.cs")).Contains("File.Delete"));
            Check("the only disk-writing editor call is TakeScreenGrab (capture_preview_image)", src.Split(new[] { "TakeScreenGrab" }, StringSplitOptions.None).Length == 2);

            // the tracker marks a settings edit as unsaved (the editor's own dirty flag is not set by settings)
            EditTracker tracker = new EditTracker();
            object mod = new object();
            UndoSignature sig = new UndoSignature();
            tracker.Observe(mod, true, sig);
            Check("fresh mod: no unsaved edits", tracker.HasUnsavedEdits(mod) == false);
            tracker.MarkPluginEditIfChanged(mod, "set_mod_settings", false);
            Check("a settings no-op does not mark the mod", tracker.HasUnsavedEdits(mod) == false);
            tracker.MarkPluginEditIfChanged(mod, "set_mod_settings", true);
            Check("a settings change marks the mod as having unsaved edits", tracker.HasUnsavedEdits(mod) == true && tracker.DirtyReason.Contains("set_mod_settings"));
            tracker.MarkSaved(mod, sig);
            Check("save_mod clears it again", tracker.HasUnsavedEdits(mod) == false);

            // validator messages
            Check("rounds validator message belongs to minigameSettings, description/preview to details",
                SettingsRules.IsRelevantValidatorMessage("Minigame", "minigameSettings", "Number of rounds must be greater than zero")
                && SettingsRules.IsRelevantValidatorMessage("Minigame", "details", "Missing minigame preview image.")
                && !SettingsRules.IsRelevantValidatorMessage("Minigame", "details", "Number of rounds must be greater than zero")
                && SettingsRules.IsRelevantValidatorMessage("Board", "music", "You have music set but the volume is very low.")
                && !SettingsRules.IsRelevantValidatorMessage("Minigame", "music", "You have music set but the volume is very low."));
        }

        private static string FindPluginDir()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string p = Path.Combine(dir, "src", "PummelPartyMcp.Plugin");
                if (Directory.Exists(p))
                {
                    return p;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static string FindUp(string relative)
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string p = Path.Combine(dir, relative);
                if (File.Exists(p))
                {
                    return p;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        // ------------------------------------------------------------------ docs and registry

        private static void DocsAndRegistry()
        {
            Console.WriteLine("-- unit: settings docs and registry");
            string docs = FindUp(Path.Combine("docs", "plugin-api.md"));
            if (docs == null)
            {
                Check("docs/plugin-api.md located", false);
                return;
            }
            string text = File.ReadAllText(docs).Replace("\r\n", "\n");
            int b = text.IndexOf(SettingsRules.DocsBegin, StringComparison.Ordinal);
            int e = text.IndexOf(SettingsRules.DocsEnd, StringComparison.Ordinal);
            Check("docs/plugin-api.md has the generated settings-rules block", b >= 0 && e > b);
            if (b >= 0 && e > b)
            {
                string block = text.Substring(b, e + SettingsRules.DocsEnd.Length - b);
                Check("the settings-rules block in docs/plugin-api.md equals SettingsRules.ToMarkdown() (regenerate it when the table changes)", block == SettingsRules.ToMarkdown());
            }
            Check("docs describe the four tools and the capability", text.Contains("list_mod_settings") && text.Contains("get_mod_settings") && text.Contains("set_mod_settings")
                && text.Contains("capture_preview_image") && text.Contains("\"settings\""));

            IReadOnlyList<RequiredMember> all = RequiredMembers.All;
            foreach (SettingsSection s in SettingsRules.All)
            {
                foreach (ModSettingField f in s.Fields.Where(x => x.Member != null))
                {
                    if (!all.Any(m => m.Kind == "settingsMember" && m.Type == f.GameType && m.Name == f.Member))
                    {
                        Check($"registry covers {s.Name}.{f.Name} ({f.GameType}.{f.Member})", false);
                    }
                }
            }
            Check("registry covers every settings field with a game member",
                SettingsRules.All.SelectMany(s => s.Fields).Where(f => f.Member != null).All(f => all.Any(m => m.Kind == "settingsMember" && m.Type == f.GameType && m.Name == f.Member)));
            Check("registry checks every enum a settings field uses", SettingsRules.All.SelectMany(s => s.Fields).Where(f => f.EnumType != null && f.Kind != SettingKind.NamedInt && f.EnumNames != null)
                .All(f => all.Any(m => m.Kind == "enumType" && m.Type == f.EnumType)));
            Check("registry checks every documented slider range", SettingsRules.All.SelectMany(s => s.Fields).Where(f => f.RangeFromAttribute).All(f => all.Any(m => m.Kind == "settingsRange" && m.Type == f.GameType && m.Name == f.Member)));
            Check("registry names the editor entry points the settings tools use", new[] { "SetupLightingWidget", "SetupPostProcessingWidget", "ApplyLightingChangesToRenderSettings", "TakeScreenGrab", "GenerateMinigameControls", "m_presetNames", "m_postProcessVolume" }
                .All(n => all.Any(m => m.Name == n)));
        }
    }
}
