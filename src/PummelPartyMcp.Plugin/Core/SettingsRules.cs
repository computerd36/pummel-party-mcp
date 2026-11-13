using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PummelPartyMcp.Core
{
    // =====================================================================================================
    // Mod settings rule table (plugin 0.8.0; audit of the editor's Mod Settings window, docs/architecture/06-mod-settings.md).
    //
    // The settings window ("Minigame Settings" / "Board Settings" entry in the Hierarchy) clamps and hides fields only in its
    // draw code, and only while the group is expanded. The data classes have no limits of their own, so the plugin applies the
    // same limits itself: per section and field the table says the type, default, when the control is shown, which choices the
    // UI offers, the limits it applies, and whether the plugin may write it at all. Pure data (no game or Unity types, no JSON
    // library): the engine is SettingsEngine.cs, the game access Game/SettingsAccess.cs. Every game member named here is
    // registered in RequiredMembers and checked by the offline drift test and self_test.
    // =====================================================================================================

    public enum SettingKind
    {
        Bool,
        Int,
        Float,
        String,
        Enum,
        Flags,

        /// <summary>An int field with a closed set of named values the UI offers (names are the plugin's, not a game enum).</summary>
        NamedInt,
        Vector2,
        Vector3,
        Rect,
        Color,

        /// <summary>Reference to one asset (null = none).</summary>
        Asset,
        AssetList,

        /// <summary>The name of a lighting / screen-effects preset (the list is read from the editor at run time).</summary>
        Preset,

        /// <summary>
        /// An action trigger (ModLogicTrigger subclass) held by the settings, e.g. the player "Event Triggers"; read-only here (edited
        /// with the action tools) and scanned for asset/object references by FindReferences.
        /// </summary>
        Trigger,
    }

    /// <summary>Extra normalisation the UI applies to a field after the widget.</summary>
    public enum SettingSpecial
    {
        None,

        /// <summary>Rect edited as centre + size: width/height clamp 0..2000, centre within +-(1000 - half size).</summary>
        RectCentered,

        /// <summary>As <see cref="RectCentered"/>, and for the Circle follow shape the height is set equal to the width.</summary>
        RectCenteredCircleAware,
    }

    public sealed class ModSettingField
    {
        /// <summary>Name the agent uses (usually the game member's name; screen-effect fields are "effect.parameter").</summary>
        public string Name;

        /// <summary>The UI label, for the agent's orientation.</summary>
        public string Label;

        public SettingKind Kind;

        /// <summary>Screen-effect parameter: the value shape is {value, override} (a bare value means value + override on).</summary>
        public bool IsParam;

        /// <summary>Screen effect presence flag (true = the effect exists in the custom profile).</summary>
        public bool IsEffect;

        /// <summary>Logical object the member lives on (resolved by Game/SettingsAccess.cs).</summary>
        public string Target;

        public string GameType;
        public string Member;

        /// <summary>Assembly of <see cref="GameType"/> when it is not Assembly-CSharp.</summary>
        public string Assembly;

        /// <summary>Expected System.Type.ToString() of the member (drift test); null = not checked.</summary>
        public string MemberType;

        // enum / flags / named int
        public string EnumType;
        public string EnumAssembly;
        public string[] EnumNames;
        public long[] EnumValues;

        /// <summary>The names the UI dropdown offers (null = all defined values).</summary>
        public string[] Choices;

        public object Default;
        public bool HasDefault;

        /// <summary>Drawn only while the condition holds (null = always drawn). Conditions name fields of the same section.</summary>
        public RuleCondition Shown;

        public double? Min;
        public double? Max;

        /// <summary>Screen-effect slider: the limits are the game's RangeAttribute of the member (Min/Max are the documented values; 0..10 without the attribute).</summary>
        public bool RangeFromAttribute;

        public SettingSpecial Special;

        /// <summary>Asset pickers: the asset types the UI offers and the locations ("internal, external").</summary>
        public string[] AssetTypes;
        public string AssetLocations;

        public bool Writable = true;
        public string ReadOnlyReason;

        /// <summary>The editor has no control for this field (it keeps the file/default value).</summary>
        public bool NeverShown;

        /// <summary>Not returned by get_mod_settings (hidden fields, trigger lists).</summary>
        public bool OmitFromGet;

        public string Note;

        /// <summary>No direct game member to check by type (preset and effect-presence fields, event triggers, lists).</summary>
        public bool NoTypeCheck;

        public bool HasLimits => Min.HasValue || Max.HasValue;
        public string[] OfferedNames => Choices ?? EnumNames;
    }

    /// <summary>Forced/implied write: what the UI does as a side effect of another control.</summary>
    public sealed class ImpliedWrite
    {
        /// <summary>The call assigns this field...</summary>
        public string TriggerField;

        /// <summary>...and (when not null) its value is one of these names...</summary>
        public string[] TriggerValues;

        /// <summary>...and (OnChangeOnly) differs from the current value.</summary>
        public bool OnChangeOnly;

        /// <summary>Field written as a consequence, with the value per trigger value name (or one value for all).</summary>
        public string Field;
        public Dictionary<string, object> ValueByTrigger;
        public object Value;

        /// <summary>The call's own value for <see cref="Field"/> wins.</summary>
        public bool ExplicitWins;

        public string Why;
    }

    /// <summary>A value combination the plugin refuses because the game misbehaves with it.</summary>
    public sealed class SettingGuard
    {
        public string Field;
        public string Value;

        /// <summary>Must hold (after the call) for the value to be allowed; Derived conditions are answered by the game layer.</summary>
        public RuleCondition Require;

        public string Message;
    }

    public sealed class SettingsSection
    {
        public string Name;

        /// <summary>"Minigame" or "Board".</summary>
        public string ModType;

        /// <summary>The settings window's group title.</summary>
        public string Title;

        /// <summary>Which file(s) save_mod writes this section to.</summary>
        public string SavedTo;

        public string Note;
        public List<ModSettingField> Fields = new List<ModSettingField>();

        /// <summary>Game members of the same objects that have no control in the editor (refused as "not shown").</summary>
        public string[] HiddenNames = new string[0];

        public string HiddenType;
        public string HiddenTarget;
        public List<ImpliedWrite> Implied = new List<ImpliedWrite>();
        public List<SettingGuard> Guards = new List<SettingGuard>();

        /// <summary>The section has a preset field (lighting, screenEffects).</summary>
        public bool HasPresets => Fields.Any(f => f.Kind == SettingKind.Preset);

        public ModSettingField Find(string name) =>
            Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal)) ??
            Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static class SettingsRules
    {
        public const string DocsBegin = "<!-- settings-rules:begin (generated from Core/SettingsRules.cs; a test keeps it in sync) -->";
        public const string DocsEnd = "<!-- settings-rules:end -->";

        public const string Custom = "Custom";

        private static readonly List<SettingsSection> s_all;

        static SettingsRules()
        {
            s_all = Build(); // after every other static initialiser (the effect and enum tables)
        }

        public static IReadOnlyList<SettingsSection> All => s_all;

        public static IEnumerable<SettingsSection> ForModType(string modType) => s_all.Where(s => s.ModType == modType);

        public static SettingsSection Find(string modType, string name) =>
            s_all.FirstOrDefault(s => s.ModType == modType && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Validator messages (ModMinigameValidator / ModBoardValidator) that concern settings, by section.</summary>
        public static readonly string[][] ValidatorMessages =
        {
            new[] { "Minigame", "details", "Empty minigame description." },
            new[] { "Minigame", "details", "Missing minigame preview image." },
            new[] { "Minigame", "minigameSettings", "Number of rounds must be greater than zero" },
            new[] { "Minigame", "playerSettings", "Respawns are enabled with Last Man Standing end condition." },
            new[] { "Minigame", "minigameSettings", "Respawns are enabled with Last Man Standing end condition." },
            new[] { "Board", "details", "Empty board description." },
            new[] { "Board", "details", "Missing board preview image." },
            new[] { "Board", "music", "You have music set but the volume is very low." },
            new[] { "Board", "music", "You have ambient sound set but the volume is very low." },
        };

        /// <summary>Settings-related validator messages for one section (substring match on the validator's title).</summary>
        public static bool IsRelevantValidatorMessage(string modType, string section, string title)
        {
            if (title == null)
            {
                return false;
            }
            return ValidatorMessages.Any(v => v[0] == modType && v[1] == section && title.IndexOf(v[2], StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ------------------------------------------------------------------ the table

        private sealed class Builder
        {
            public readonly SettingsSection Section;

            public Builder(SettingsSection s)
            {
                Section = s;
            }

            public ModSettingField Add(string name, string label, SettingKind kind, string target, string gameType, string member, object def = null, bool hasDef = true)
            {
                ModSettingField f = new ModSettingField
                {
                    Name = name, Label = label, Kind = kind, Target = target, GameType = gameType, Member = member ?? name, Default = def, HasDefault = hasDef && def != null,
                };
                Section.Fields.Add(f);
                return f;
            }
        }

        private static readonly string[] s_endNames = { "Timer", "ObtainPoints", "RemainingPlayersAlive", "FinishMinigame" };
        private static readonly long[] s_endValues = { 1, 2, 4, 8 };

        private static string[] Names(params string[] n) => n;

        private static List<SettingsSection> Build()
        {
            List<SettingsSection> list = new List<SettingsSection>();
            list.Add(MinigameDetails());
            list.Add(MinigameSettings());
            list.Add(PlayerSettings());
            list.Add(MinigameMusic());
            list.Add(Lighting("Minigame"));
            list.Add(ScreenEffects("Minigame"));
            list.Add(BoardDetails());
            list.Add(CustomSpaces());
            list.Add(BoardSize());
            list.Add(BoardCamera());
            list.Add(BoardMusic());
            list.Add(Lighting("Board"));
            list.Add(ScreenEffects("Board"));
            return list;
        }

        private static Builder Start(string name, string modType, string title, string savedTo, string note)
        {
            return new Builder(new SettingsSection { Name = name, ModType = modType, Title = title, SavedTo = savedTo, Note = note });
        }

        private static ModSettingField Num(Builder b, string name, string label, SettingKind kind, string target, string type, double def, double? min, double? max, string note = null, string member = null)
        {
            ModSettingField f = b.Add(name, label, kind, target, type, member, def);
            f.Min = min;
            f.Max = max;
            f.Note = note;
            return f;
        }

        private static ModSettingField Flag(Builder b, string name, string label, string target, string type, bool def)
        {
            return b.Add(name, label, SettingKind.Bool, target, type, null, def);
        }

        private static ModSettingField EnumField(Builder b, string name, string label, string target, string type, string enumType, string[] names, long[] values, string def, string[] choices = null, string enumAssembly = null)
        {
            ModSettingField f = b.Add(name, label, SettingKind.Enum, target, type, null, def);
            f.EnumType = enumType;
            f.EnumAssembly = enumAssembly;
            f.EnumNames = names;
            f.EnumValues = values;
            f.Choices = choices;
            return f;
        }

        private static long[] Seq(int n)
        {
            long[] v = new long[n];
            for (int i = 0; i < n; i++)
            {
                v[i] = i;
            }
            return v;
        }

        private static ModSettingField Asset(Builder b, string name, string label, string target, string type, string member, string assetType, string locations, string defGuid, string note = null)
        {
            ModSettingField f = b.Add(name, label, SettingKind.Asset, target, type, member, defGuid);
            f.AssetTypes = new[] { assetType };
            f.AssetLocations = locations;
            f.Note = note;
            return f;
        }

        private static ModSettingField ReadOnly(ModSettingField f, string reason)
        {
            f.Writable = false;
            f.ReadOnlyReason = reason;
            return f;
        }

        // ---- minigame

        private static SettingsSection MinigameDetails()
        {
            Builder b = Start("details", "Minigame", "Minigame Details", "Data/MinigameDefinitionData.json (written by save_mod)",
                "The minigame's own description and presentation image (loading screen, ruleset, lobby). NOT the Workshop item: its title, description, tags, visibility and thumbnail are never touched. Use capture_preview_image to create a preview image from the scene.");
            ModSettingField name = b.Add("name", "Name", SettingKind.String, "definition", "MinigameDefinition", "minigameName");
            ReadOnly(name, "the name is the mod's name; renaming a mod is a separate editor action (Rename dialog: renames the folder and rewrites Meta.json and the Workshop item) that the plugin never performs");
            ModSettingField desc = b.Add("description", "Description", SettingKind.String, "definition", "MinigameDefinition", "description", "A minigame mod.");
            desc.Note = "multiline text, no length limit in the editor (the plugin accepts up to 16384 characters)";
            Asset(b, "previewImage", "Preview Image", "definition", "MinigameDefinition", "screenshotTexture", "Texture2D", "internal, external", null,
                "null = none (the validator warns: Missing minigame preview image). A guid from list_assets (type Texture2D) or the asset capture_preview_image created.");
            b.Section.HiddenType = "MinigameDefinition";
            b.Section.HiddenTarget = "definition";
            b.Section.HiddenNames = new[] { "minigameNameToken", "descriptionToken", "minPlayers", "maxPlayers", "inputHelp" };
            return b.Section;
        }

        private static SettingsSection MinigameSettings()
        {
            Builder b = Start("minigameSettings", "Minigame", "Minigame Settings", "Data/ModSettings.json (written by save_mod)",
                "How the minigame is played. The player type and all player options are in section playerSettings.");
            const string T = "controller";
            const string C = "SimpleModControllerSettings";
            Num(b, "NumberOfRounds", "Rounds", SettingKind.Int, T, C, 1, 1, 20, "the validator requires at least 1; the UI clamps to 1..20");
            ModSettingField end = b.Add("EndConditions", "End Conditions", SettingKind.Flags, T, C, null, new[] { "Timer" });
            end.EnumType = "SimpleMinigameEndCondition";
            end.EnumNames = s_endNames;
            end.EnumValues = s_endValues;
            end.Note = "any combination; \"\" or \"None\" or 0 = no end condition (the UI's \"Nothing\")";
            ModSettingField pts = Num(b, "PointsToWin", "Points to win", SettingKind.Int, T, C, 5, 1, 1000000);
            pts.Shown = Cond.Is("EndConditions", "ObtainPoints");
            ModSettingField dur = Num(b, "RoundDuration", "Round Duration (s)", SettingKind.Float, T, C, 60, 5, 1200);
            dur.Shown = Cond.Is("EndConditions", "Timer");
            ModSettingField alive = Num(b, "MinPlayersAlive", "Players Alive", SettingKind.Int, T, C, 1, 1, 7);
            alive.Shown = Cond.Is("EndConditions", "RemainingPlayersAlive");
            EnumField(b, "PlacementCondition", "Placement Condition", T, C, "SimpleMinigamePlacementCondition",
                Names("MostPoints", "LeastPoints", "FinishOrder", "LastManStanding"), Seq(4), "MostPoints");
            Flag(b, "ShowPlayerScore", "Show Score in UI", T, C, true);
            ModSettingField hide = Flag(b, "OnlyShowCombinedTeamScoreInTeams", "(Teams) Hide Player Scores", T, C, false);
            hide.Shown = Cond.On("ShowPlayerScore");
            ModSettingField spawn = b.Add("SpawnType", "(no control)", SettingKind.String, T, C, null, null, false);
            spawn.NeverShown = true;
            spawn.OmitFromGet = true;
            spawn.MemberType = "PlayerSpawnType";
            ReadOnly(spawn, "the editor has no control for it and the game ignores the value (it always uses PortalAbove)");
            return b.Section;
        }

        private static SettingsSection PlayerSettings()
        {
            Builder b = Start("playerSettings", "Minigame", "Player Settings", "Data/ModSettings.json (written by save_mod)",
                "The player type and every foldout of the Player Settings group. The six event triggers (weaponHitTrigger, tickTrigger, onDeathTrigger, onRespawnTrigger, onFirstSpawnTrigger, onRoundEndTrigger) are edited with the action tools (get_action_graph / add_action ... with scope \"playerSettings\"), not here. Changing the player type, canJump or canPunch also regenerates the controls help shown on the loading screen (the editor does so at save too).");
            const string C = "SimpleModControllerSettings";
            const string P = "SimpleModPlayerSettings";
            ModSettingField type = EnumField(b, "ChosenPlayerType", "Player Type (+ Twinstick Controls)", "controller", C, "SimpleModPlayerType",
                Names("ThirdPerson", "TopDown", "TwinStick"), Seq(3), "TopDown");
            type.Note = "the UI offers ThirdPerson/TopDown in a combo and TwinStick as a checkbox under Movement (only while TopDown); all three are accepted";
            RuleCondition topDown = Cond.Is("ChosenPlayerType", "TopDown", "TwinStick");

            // Movement
            Num(b, "movementSpeed", "Movement Speed", SettingKind.Float, "player", P, 6, 0, 1000);
            Num(b, "movementAcceleration", "Acceleration", SettingKind.Float, "player", P, 50, 0, 1000);
            Num(b, "movementDecelaration", "Deceleration", SettingKind.Float, "player", P, 50, 0, 10000, "the game's member name is spelled movementDecelaration");
            Num(b, "midAirMovementAcceleration", "Mid-air Acceleration", SettingKind.Float, "player", P, 8, 0, 1000);
            Num(b, "playerGravity", "Gravity Scale x 25", SettingKind.Float, "player", P, 25, -100, 100, "the UI shows playerGravity / 25 (\"Gravity Scale\", slider -4..4); this is the stored value");
            Num(b, "stepOffset", "Max Step Height", SettingKind.Float, "player", P, 0.5, 0, 1.75);
            Flag(b, "canJump", "Can Jump", "player", P, true);
            ModSettingField js = Num(b, "jumpSpeed", "Jump Speed", SettingKind.Float, "player", P, 7, 0, 1000);
            js.Shown = Cond.On("canJump");
            ModSettingField jc = Num(b, "jumpCount", "Mid-air Jumps + 1", SettingKind.Int, "player", P, 1, 1, 1001, "the UI shows jumpCount - 1 as \"Mid-air Jumps\" (0..1000); this is the stored value");
            jc.Shown = Cond.On("canJump");

            // Camera (TopDown / TwinStick only)
            ModSettingField mode = EnumField(b, "topDownCameraMode", "Camera Mode", "player", P, "SimpleModPlayerTopDownCameraMode",
                Names("Static", "Follow", "FollowAll"), Seq(3), "Static");
            mode.Shown = topDown;
            mode.Note = "Follow needs a Player Spawn object in the scene (the editor throws on every draw otherwise); with ThirdPerson the editor forces Static";
            ModSettingField dist = Num(b, "topDownCameraDistance", "Camera Distance", SettingKind.Float, "player", P, 10, 1, 40);
            dist.Shown = topDown;
            ModSettingField angle = Num(b, "topDownCameraViewAngle", "View Angle", SettingKind.Float, "player", P, 35, 0, 90);
            angle.Shown = topDown;
            ModSettingField off = b.Add("topDownCameraPositionOffset", "Position Offset", SettingKind.Vector3, "player", P, null, new[] { 0.0, 0.0, 0.0 });
            off.Shown = topDown;
            RuleCondition followAll = Cond.All(topDown, Cond.Is("topDownCameraMode", "FollowAll"));
            ModSettingField shape = EnumField(b, "topDownCameraFollowAllType", "Follow Area Shape", "player", P, "CameraFollowType", Names("Circle", "Square"), Seq(2), "Square");
            shape.Shown = followAll;
            ModSettingField rect = b.Add("topDownCamerafollowAllAreaRect", "Follow area (size + centre)", SettingKind.Rect, "player", P, null, new[] { -5.0, -5.0, 10.0, 10.0 });
            rect.Shown = followAll;
            rect.Special = SettingSpecial.RectCenteredCircleAware;
            rect.Note = "the member's name has a lower-case f: topDownCamerafollowAllAreaRect";
            ModSettingField mx = Num(b, "maxXMoveDist", "Max X Move Distance", SettingKind.Float, "player", P, 3, null, null);
            mx.Shown = Cond.All(followAll, Cond.Is("topDownCameraFollowAllType", "Square"));
            ModSettingField mz = Num(b, "maxZMoveDist", "Max Z Move Distance", SettingKind.Float, "player", P, 3, null, null);
            mz.Shown = mx.Shown;
            ModSettingField sm = Num(b, "sphereMaxMoveDist", "Max Move Distance (circle)", SettingKind.Float, "player", P, 3, null, null);
            sm.Shown = Cond.All(followAll, Cond.Is("topDownCameraFollowAllType", "Circle"));

            // Punching
            Flag(b, "canPunch", "Can Punch", "player", P, false);
            ModSettingField pr = Num(b, "punchRechargeSpeed", "Cooldown (s)", SettingKind.Float, "player", P, 0.75, 0.1, 1000);
            pr.Shown = Cond.On("canPunch");
            ModSettingField pd = Num(b, "punchDamage", "Damage", SettingKind.Int, "player", P, 1, -1000, 1000);
            pd.Shown = Cond.On("canPunch");
            ModSettingField ps = Flag(b, "punchStunsTarget", "Punch Stuns Target", "player", P, false);
            ps.Shown = Cond.On("canPunch");
            ModSettingField pst = Num(b, "punchStunDuration", "Punch Stun Duration (s)", SettingKind.Float, "player", P, 1, 0.1, 1000);
            pst.Shown = Cond.All(Cond.On("canPunch"), Cond.On("punchStunsTarget"));

            // Stuns
            Num(b, "stunImmunityLength", "Immunity duration", SettingKind.Float, "player", P, 1, 0, 1000);

            // Respawning
            Flag(b, "respawnEnabled", "Can Respawn", "player", P, true);
            ModSettingField rt = Num(b, "respawnTime", "Respawn Time (s)", SettingKind.Float, "player", P, 3, 0, 1000);
            rt.Shown = Cond.On("respawnEnabled");
            ModSettingField ri = Num(b, "respawnInvulnerableTime", "Invulnerable Time (s)", SettingKind.Float, "player", P, 1, 0, 1000);
            ri.Shown = Cond.On("respawnEnabled");

            // Health
            Flag(b, "useHealth", "Use Health", "player", P, false);
            ModSettingField shb = Flag(b, "showHealthBar", "Show Health Bar", "player", P, false);
            shb.Shown = Cond.On("useHealth");
            ModSettingField hp = Num(b, "health", "Health Points", SettingKind.Int, "player", P, 100, 1, 1000);
            hp.Shown = Cond.On("useHealth");

            // Name tags
            Flag(b, "showNameTags", "Show Name Tags", "player", P, true);
            ModSettingField nt = EnumField(b, "nameTagType", "Name Tag Type", "player", P, "UIPlayerNameTagType",
                Names("ScreenSpace", "WorldSpaceScreenSize", "WorldSpaceWorldSize", "WorldSpaceWorldSizeOccluded", "Disabled"), Seq(5), "WorldSpaceWorldSizeOccluded",
                Names("WorldSpaceScreenSize", "WorldSpaceWorldSize", "WorldSpaceWorldSizeOccluded"));
            nt.Shown = Cond.On("showNameTags");
            nt.Note = "UI labels: WorldSpaceScreenSize = \"Screen Space\", WorldSpaceWorldSize = \"World Space\", WorldSpaceWorldSizeOccluded = \"True World Space\"; \"no name tags\" is showNameTags = false";

            // Event triggers: read-only pointers
            foreach (string trig in new[] { "weaponHitTrigger", "tickTrigger", "onDeathTrigger", "onRespawnTrigger", "onFirstSpawnTrigger", "onRoundEndTrigger" })
            {
                ModSettingField t = b.Add(trig, "Event Triggers", SettingKind.Trigger, "player", P, null, null, false);
                t.OmitFromGet = true;
                ReadOnly(t, "an event trigger: use get_action_graph with scope \"playerSettings\" and add_action / set_action / remove_action / move_action");
                t.NoTypeCheck = true;
            }
            b.Section.HiddenType = P;
            b.Section.HiddenTarget = "player";
            b.Section.HiddenNames = new[]
            {
                "coyoteTime", "shootDistance", "shootRechargeSpeed", "shotSpreadVertical", "shotSpreadHorizontal", "bulletsPerShot", "firstShotCentred",
                "firingPattern", "respawnAnimationType", "hitTrigger",
            };

            b.Section.Implied.Add(new ImpliedWrite
            {
                TriggerField = "ChosenPlayerType", TriggerValues = new[] { "ThirdPerson" }, Field = "topDownCameraMode", Value = "Static", ExplicitWins = false,
                Why = "the editor forces topDownCameraMode to Static for the ThirdPerson player type",
            });
            b.Section.Guards.Add(new SettingGuard
            {
                Field = "topDownCameraMode", Value = "Follow", Require = Cond.Derived("hasPlayerSpawn", "the scene has a Player Spawn object", "the scene has no Player Spawn object"),
                Message = "camera mode Follow needs a Player Spawn object in the scene: the editor's camera preview throws an exception on every draw without one. Create one first (create_object kind playerSpawn) or use Static / FollowAll",
            });
            return b.Section;
        }

        private static SettingsSection MinigameMusic()
        {
            Builder b = Start("music", "Minigame", "Music & Ambient Sound", "Data/ModSettings.json (written by save_mod)",
                "Background track and ambience of the minigame. Clips are assets: a built-in one (list_assets includeBuiltIn, type AudioClip) or one imported into the mod.");
            const string C = "SimpleModControllerSettings";
            Asset(b, "Music", "Music", "controller", C, "Music", "AudioClip", "internal, external", "13dbf0ac-803d-49d1-997f-0a559bd43061", "null = no music; a game property (stores the clip's asset guid)").MemberType = "UnityEngine.AudioClip";
            Num(b, "MusicVolume", "Music Volume", SettingKind.Float, "controller", C, 0.3, 0, 1);
            Asset(b, "Ambience", "Ambience", "controller", C, "Ambience", "AudioClip", "internal, external", "90abde3b-7f29-43ee-80c9-40ec418d5993", "null = no ambience").MemberType = "UnityEngine.AudioClip";
            Num(b, "AmbienceVolume", "Ambience Volume", SettingKind.Float, "controller", C, 0.5, 0, 1);
            return b.Section;
        }

        // ---- board

        private static SettingsSection BoardDetails()
        {
            Builder b = Start("details", "Board", "Board Details", "Data/MapDetails.json (written by save_mod)",
                "The board's own description and presentation image (lobby and in game). NOT the Workshop item: its title, description, tags, visibility and thumbnail are never touched. Use capture_preview_image to create a preview image from the scene.");
            ModSettingField name = b.Add("name", "Name", SettingKind.String, "mapDetails", "MapDetails", "mapName");
            ReadOnly(name, "the name is the mod's name; renaming a mod is a separate editor action that the plugin never performs");
            b.Add("description", "Description", SettingKind.String, "mapDetails", "MapDetails", "description", "A board mod.");
            Asset(b, "previewImage", "Preview Image", "mapDetails", "MapDetails", "previewImage", "Texture2D", "internal, external", null,
                "null = none (the validator warns: Missing board preview image)");
            Num(b, "defaultGoalChestCount", "Trophy Chests", SettingKind.Int, "mapDetails", "MapDetails", 1, 1, 8, "players' rulesets can override it");
            Flag(b, "supportsWeaponCache", "Weapons Cache", "mapDetails", "MapDetails", true);
            Flag(b, "useCustomChestShuffleLocation", "Custom Chest Shuffle Position", "mapDetails", "MapDetails", false);
            ModSettingField pos = b.Add("chestShuffleLocation", "Chest Shuffle Position", SettingKind.Vector3, "mapDetails", "MapDetails", null, new[] { 0.0, 0.0, 0.0 });
            pos.Note = "world space; the editor lets you edit it even while the checkbox is off (it only greys it out), so it has no condition here either";
            b.Section.HiddenType = "MapDetails";
            b.Section.HiddenTarget = "mapDetails";
            b.Section.HiddenNames = new[] { "sceneName", "overrideSpacePrefabs", "overrideSpace", "spacePrefabOverrides" };
            return b.Section;
        }

        private static SettingsSection CustomSpaces()
        {
            Builder b = Start("customSpaces", "Board", "Custom Spaces", "Data/ModSettings.json (written by save_mod)",
                "The list of custom board space prefabs. Board spaces store an INDEX into this list, so adding, removing or reordering entries re-points existing spaces; the editor also rebuilds the spaces through an event that only its own window raises. Read-only in 0.8.0.");
            ModSettingField f = b.Add("CustomSpacePrefabs", "Custom Spaces", SettingKind.AssetList, "customSpaces", "BoardModCustomSpaceSettings", "CustomSpacePrefabReferences", null, false);
            f.AssetTypes = new[] { "PummelPrefab" };
            f.AssetLocations = "external";
            f.NoTypeCheck = true;
            ReadOnly(f, "index-based list whose change event is raised only by the editor's own settings window; the plugin does not write it (edit it in the editor)");
            return b.Section;
        }

        private static SettingsSection BoardSize()
        {
            Builder b = Start("boardSize", "Board", "Board Size", "Data/ModSettings.json (UseCustomBoardSize) and Data/MapDetails.json (mapExtents) (written by save_mod)",
                "Without a custom size the editor recomputes the extents from the board nodes at every save (bounding box plus a margin), so writing them is only meaningful with UseCustomBoardSize on.");
            Flag(b, "UseCustomBoardSize", "Use Custom Board Size", "boardSettings", "BoardModSettings", false);
            ModSettingField r = b.Add("mapExtents", "Left/Right size, Forward/Backward size, Map Centre", SettingKind.Rect, "mapDetails", "MapDetails", null, new[] { -20.0, -20.0, 40.0, 40.0 });
            r.Shown = Cond.On("UseCustomBoardSize");
            r.Special = SettingSpecial.RectCentered;
            r.Note = "{x, y, width, height} with x/y the corner (the UI edits size and centre); the default shown is a new mod's";
            return b.Section;
        }

        private static SettingsSection BoardCamera()
        {
            Builder b = Start("camera", "Board", "Camera Settings", "Data/ModSettings.json (written by save_mod)",
                "How the camera views the board. Changing CameraMode also resets CameraDistance (13 for Perspective, 40 for Orthographic), as the editor does.");
            const string T = "camera";
            const string C = "BoardModCameraSettings";
            Num(b, "CameraViewAngle", "Elevation Angle", SettingKind.Float, T, C, 37.5, 14, 90);
            ModSettingField mode = b.Add("CameraMode", "Camera Mode", SettingKind.NamedInt, T, C, null, "Perspective");
            mode.EnumNames = Names("Perspective", "Orthographic");
            mode.EnumValues = new long[] { 0, 1 };
            ModSettingField fov = Num(b, "CameraFov", "Field of View", SettingKind.Float, T, C, 60, 30, 90);
            fov.Shown = Cond.Is("CameraMode", "Perspective");
            ModSettingField d = Num(b, "CameraDistance", "Distance", SettingKind.Float, T, C, 13, 6, 20);
            d.Shown = Cond.Is("CameraMode", "Perspective");
            ModSettingField size = Num(b, "CameraSize", "View Size", SettingKind.Float, T, C, 5, 4, 10);
            size.Shown = Cond.Is("CameraMode", "Orthographic");
            b.Section.Implied.Add(new ImpliedWrite
            {
                TriggerField = "CameraMode", OnChangeOnly = true, Field = "CameraDistance", ExplicitWins = true,
                ValueByTrigger = new Dictionary<string, object> { ["Perspective"] = 13.0, ["Orthographic"] = 40.0 },
                Why = "changing the camera mode resets the distance (13 Perspective, 40 Orthographic)",
            });
            return b.Section;
        }

        private static SettingsSection BoardMusic()
        {
            Builder b = Start("music", "Board", "Music & Ambient Sound", "Data/MapDetails.json (written by save_mod)",
                "Music and ambient clips of the board (assets, built-in or imported). The validator warns when a clip is set and its volume is very low.");
            const string T = "mapDetails";
            const string C = "MapDetails";
            Asset(b, "Music", "Music Clip", T, C, "music", "AudioClip", "internal, external", "3ac2bff4-9bc6-406e-8e6f-b725dc69e08a", "null = no music");
            Num(b, "MusicVolume", "Music Volume", SettingKind.Float, T, C, 0.3, 0, 1, null, "musicVol");
            Asset(b, "Ambience", "Ambient Clip", T, C, "ambience", "AudioClip", "internal, external", "05f5b5c8-5190-49d7-ad7a-f154db4e4f29", "null = no ambience");
            Num(b, "AmbienceVolume", "Ambience Volume", SettingKind.Float, T, C, 0.5, 0, 1, null, "ambienceVol");
            return b.Section;
        }

        // ---- lighting

        private const string Lt = "ModLightingSettings";

        private static readonly double[] s_white = { 1, 1, 1, 1 };

        private static double[] Rgb255(int r, int g, int bl) => new[] { r / 255.0, g / 255.0, bl / 255.0, 1.0 };

        private static ModSettingField LColor(Builder b, string name, string label, double[] def)
        {
            ModSettingField f = b.Add(name, label, SettingKind.Color, "lighting", Lt, null, def);
            f.Note = null;
            return f;
        }

        private static SettingsSection Lighting(string modType)
        {
            Builder b = Start("lighting", modType, "Lighting", "Data/Meta.json (selected preset) and Data/Lighting.json (the custom values, always) (written by save_mod)",
                "Pick a preset by name, or \"Custom\" and edit the custom values. Custom values live in the mod's own custom lighting object and survive a preset switch (Lighting.json always holds them); the preset objects themselves are never modified. After a change the plugin applies the lighting to the editor scene once, so a screenshot shows it. Presets are listed by list_mod_settings (the built-in files of the game).");
            ModSettingField preset = b.Add("preset", "Lighting Preset", SettingKind.Preset, "lightingPreset", "PummelMod", "GetSelectedLightingPreset", "Default");
            preset.Member = null;
            preset.NoTypeCheck = true;
            preset.Note = "one of the names list_mod_settings shows (the last entry is \"Custom\"); anything else is refused (the editor would silently fall back to default lighting)";
            RuleCondition custom = Cond.Is("preset", Custom);
            ModSettingField stars = b.Add("stars", "Stars", SettingKind.Bool, "lighting", Lt, null, false);
            stars.Shown = custom;
            ModSettingField top = LColor(b, "topColor", "Top Color", Rgb255(97, 171, 255));
            top.Shown = custom;
            LColor(b, "horizonColor", "Horizon Color", Rgb255(92, 167, 225)).Shown = custom;
            LColor(b, "groundColor", "Ground Color", Rgb255(41, 64, 82)).Shown = custom;
            LColor(b, "haloColor", "Halo Color", Rgb255(181, 171, 145)).Shown = custom;
            Num(b, "haloExponent", "Halo Exponent", SettingKind.Float, "lighting", Lt, 6, 0, 32).Shown = custom;
            Num(b, "haloStrength", "Halo Strength", SettingKind.Float, "lighting", Lt, 1, 0.1, 4).Shown = custom;

            b.Add("globalLightEnabled", "Use Global Light", SettingKind.Bool, "lighting", Lt, null, true).Shown = custom;
            RuleCondition sun = Cond.All(custom, Cond.On("globalLightEnabled"));
            b.Add("globalLightRotation", "Sunlight Direction (Euler angles)", SettingKind.Vector3, "lighting", Lt, null, new[] { 130.0, 25.0, 0.0 }).Shown = sun;
            LColor(b, "globalLightColor", "Sunlight Color", new[] { 1.0, 0.95686275, 0.8392157, 1.0 }).Shown = sun;
            Num(b, "globalLightIntensity", "Sun Intensity", SettingKind.Float, "lighting", Lt, 0.6, null, null).Shown = sun;
            Num(b, "globalShadowItensity", "Shadow Strength", SettingKind.Float, "lighting", Lt, 1, 0, 1, "the game's member name is spelled globalShadowItensity").Shown = sun;
            EnumField(b, "globalLightShadows", "Sun Shadow Mode", "lighting", Lt, "UnityEngine.LightShadows", Names("None", "Hard", "Soft"), new long[] { 0, 1, 2 }, "Soft",
                null, "UnityEngine.CoreModule").Shown = sun;

            ModSettingField am = EnumField(b, "ambientMode", "Ambient Source", "lighting", Lt, "PummelAmbientMode", Names("Skybox", "Gradient", "Color"), new long[] { 0, 1, 3 }, "Skybox");
            am.Shown = custom;
            Num(b, "ambientIntensity", "Ambient Intensity", SettingKind.Float, "lighting", Lt, 0.7, null, null).Shown = Cond.All(custom, Cond.Is("ambientMode", "Skybox"));
            ModSettingField asky = LColor(b, "ambientSkyColor", "Ambient Sky Color / Ambient Color", Rgb255(54, 58, 66));
            asky.Shown = Cond.All(custom, Cond.Is("ambientMode", "Gradient", "Color"));
            asky.Note = "labelled \"Ambient Color\" in Color mode";
            RuleCondition grad = Cond.All(custom, Cond.Is("ambientMode", "Gradient"));
            LColor(b, "ambientEquatorColor", "Ambient Equator Color", Rgb255(29, 32, 34)).Shown = grad;
            LColor(b, "ambientGroundColor", "Ambient Ground Color", Rgb255(12, 11, 9)).Shown = grad;

            RuleCondition shadowsOn = Cond.All(custom, Cond.Is("globalLightShadows", "None").Not());
            b.Add("cloudShadows", "Use Cloud Shadows", SettingKind.Bool, "lighting", Lt, null, false).Shown = shadowsOn;
            RuleCondition clouds = Cond.All(shadowsOn, Cond.On("cloudShadows"));
            Num(b, "cloudSpeed1", "Primary Cloud Speed", SettingKind.Float, "lighting", Lt, 0.12, null, null).Shown = clouds;
            Num(b, "cloudSpeed2", "Secondary Cloud Speed", SettingKind.Float, "lighting", Lt, 0.1375, null, null).Shown = clouds;
            Num(b, "cloudIntensity", "Cloud Intensity", SettingKind.Float, "lighting", Lt, 0.7, 0, 1).Shown = clouds;
            Num(b, "cloudStrength", "Cloud Sparseness", SettingKind.Float, "lighting", Lt, 1, 0, 5).Shown = clouds;
            Num(b, "cloudScale", "Cloud Size", SettingKind.Float, "lighting", Lt, 200, null, null).Shown = clouds;

            b.Add("fog", "Use Fog", SettingKind.Bool, "lighting", Lt, null, false).Shown = custom;
            RuleCondition fogOn = Cond.All(custom, Cond.On("fog"));
            LColor(b, "fogColor", "Fog Color", new[] { 0.5, 0.5, 0.5, 1.0 }).Shown = fogOn;
            EnumField(b, "fogMode", "Fog Mode", "lighting", Lt, "UnityEngine.FogMode", Names("Linear", "Exponential", "ExponentialSquared"), new long[] { 1, 2, 3 }, "Linear",
                null, "UnityEngine.CoreModule").Shown = fogOn;
            RuleCondition fogLinear = Cond.All(fogOn, Cond.Is("fogMode", "Linear"));
            Num(b, "fogEndDistance", "Fog End Distance", SettingKind.Float, "lighting", Lt, 300, null, null).Shown = fogLinear;
            Num(b, "fogStartDistance", "Fog Start Distance", SettingKind.Float, "lighting", Lt, 0, null, null).Shown = fogLinear;
            Num(b, "fogDensity", "Fog Density", SettingKind.Float, "lighting", Lt, 0.01, null, null).Shown = Cond.All(fogOn, Cond.Is("fogMode", "Linear").Not());

            Num(b, "shadowDistance", "Shadow Distance", SettingKind.Float, "lighting", Lt, 200, 5, 250).Shown = custom;
            ModSettingField sc = b.Add("shadowCascades", "Shadow Cascades", SettingKind.NamedInt, "lighting", Lt, null, "Four");
            sc.EnumNames = Names("Zero", "Two", "Four");
            sc.EnumValues = new long[] { 0, 2, 4 };
            sc.Shown = custom;
            sc.Note = "stored as the numbers 0, 2 or 4";

            b.Section.HiddenType = Lt;
            b.Section.HiddenTarget = "lighting";
            b.Section.HiddenNames = new[] { "version", "presetName", "shadowCascade4Split", "shadowProjection", "shadowCascade2Split", "defaultReflectionMode", "reflectionIntensity" };
            return b.Section;
        }

        // ---- screen effects

        private const string PpNs = "UnityEngine.Rendering.PostProcessing.";
        private const string PpAsm = "Unity.Postprocessing.Runtime";

        private enum PK
        {
            Float,
            Bool,
            Color,
            Vec2,
            Enum,
        }

        private sealed class EffectDef
        {
            public string Field;
            public string Type;
            public string Label;
        }

        private static readonly EffectDef[] s_effects =
        {
            new EffectDef { Field = "ambientOcclusion", Type = "AmbientOcclusion", Label = "Ambient Occlusion" },
            new EffectDef { Field = "bloom", Type = "ComputeBloom", Label = "Bloom" },
            new EffectDef { Field = "chromaticAberration", Type = "ChromaticAberration", Label = "Chromatic Aberration" },
            new EffectDef { Field = "colorGrading", Type = "ColorGrading", Label = "Color Grading" },
            new EffectDef { Field = "depthOfField", Type = "DepthOfField", Label = "Depth of Field" },
            new EffectDef { Field = "grain", Type = "Grain", Label = "Grain" },
            new EffectDef { Field = "lensDistortion", Type = "LensDistortion", Label = "Lens Distortion" },
            new EffectDef { Field = "vignette", Type = "Vignette", Label = "Vignette" },
        };

        /// <summary>Enum parameters of the effects: member name -> (enum type, names, values).</summary>
        public static readonly string[] EffectEnumTypes = { "AmbientOcclusionMode", "KernelSize", "GradingMode", "VignetteMode" };

        private static SettingsSection ScreenEffects(string modType)
        {
            Builder b = Start("screenEffects", modType, "Screen Effects", "Data/Meta.json (selected preset) and Data/PostProcessing.data (the ACTIVE effects: the preset's values while a preset is selected) (written by save_mod)",
                "Pick a preset by name, or \"Custom\" and add / remove effects and edit their parameters. Each parameter is {value, override}: the editor ignores a value whose override is off, so a bare value in set_mod_settings sets the value AND switches the override on; {override: false} switches it off. " +
                "SAVE WARNING: PostProcessing.data is written from the active effects, so saving while a built-in preset is selected overwrites the custom effects on disk with the preset's values (they survive in memory until the mod is reopened; select Custom again before save_mod to keep them). " +
                "The effects are visible in the scene view only while the toolbar's Post Processing button is on; the plugin refreshes the effect layer after a change. The legacy Bloom effect (only present in some presets) cannot be added and is not covered.");
            ModSettingField preset = b.Add("preset", "Screen Effects Preset", SettingKind.Preset, "effectsPreset", "PummelMod", "GetSelectedPostProcessingPreset", "Default");
            preset.Member = null;
            preset.NoTypeCheck = true;
            preset.Note = "one of the names list_mod_settings shows (the last entry is \"Custom\"); anything else is refused (the editor would throw)";
            RuleCondition custom = Cond.Is("preset", Custom);

            foreach (EffectDef e in s_effects)
            {
                string type = PpNs + e.Type;
                ModSettingField present = b.Add(e.Field, e.Label + " (effect present)", SettingKind.Bool, "effect:" + e.Type, type, null, false);
                present.IsEffect = true;
                present.Assembly = PpAsm;
                present.Member = null;
                present.NoTypeCheck = true;
                present.Shown = custom;
                present.Note = "true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost)";
                RuleCondition on = Cond.All(custom, Cond.On(e.Field));

                void Enabled()
                {
                    ModSettingField en = b.Add(e.Field + ".enabled", e.Label + " header checkbox", SettingKind.Bool, "effect:" + e.Type, type, "enabled", true);
                    en.Assembly = PpAsm;
                    en.Shown = on;
                    en.Note = "the effect's on/off switch";
                    en.MemberType = PpNs + "BoolParameter";
                    en.IsParam = false;
                    en.Special = SettingSpecial.None;
                }

                void P(string member, string label, PK kind, bool slider, double min, double max, RuleCondition extra = null, string enumType = null, string[] enumNames = null, long[] enumValues = null)
                {
                    SettingKind sk = kind == PK.Float ? SettingKind.Float : kind == PK.Bool ? SettingKind.Bool : kind == PK.Color ? SettingKind.Color : kind == PK.Vec2 ? SettingKind.Vector2 : SettingKind.Enum;
                    ModSettingField f = b.Add(e.Field + "." + member, e.Label + " " + label, sk, "effect:" + e.Type, type, member, null, false);
                    f.Assembly = PpAsm;
                    f.IsParam = true;
                    f.Shown = extra == null ? on : Cond.All(on, extra);
                    string pt = kind == PK.Float ? "FloatParameter" : kind == PK.Bool ? "BoolParameter" : kind == PK.Color ? "ColorParameter" : kind == PK.Vec2 ? "Vector2Parameter" : enumType + "Parameter";
                    f.MemberType = PpNs + pt;
                    if (kind == PK.Float && slider)
                    {
                        f.Min = min;
                        f.Max = max;
                        f.RangeFromAttribute = true;
                    }
                    if (kind == PK.Enum)
                    {
                        f.EnumType = PpNs + enumType;
                        f.EnumAssembly = PpAsm;
                        f.EnumNames = enumNames;
                        f.EnumValues = enumValues;
                    }
                }

                Enabled();
                switch (e.Type)
                {
                    case "AmbientOcclusion":
                        P("mode", "mode", PK.Enum, false, 0, 0, null, "AmbientOcclusionMode", Names("ScalableAmbientObscurance", "MultiScaleVolumetricObscurance"), new long[] { 0, 1 });
                        P("intensity", "intensity", PK.Float, true, 0, 4);
                        P("radius", "radius", PK.Float, false, 0, 0, Cond.Is(e.Field + ".mode", "ScalableAmbientObscurance"));
                        P("thicknessModifier", "thickness modifier", PK.Float, true, 1, 10, Cond.Is(e.Field + ".mode", "ScalableAmbientObscurance").Not());
                        P("color", "color", PK.Color, false, 0, 0);
                        P("ambientOnly", "ambient only", PK.Bool, false, 0, 0);
                        break;
                    case "ComputeBloom":
                        P("HighQualityBloom", "high quality", PK.Bool, false, 0, 0);
                        P("BloomStrength", "intensity", PK.Float, false, 0, 0);
                        P("BloomThreshold", "threshold", PK.Float, true, 0, 2);
                        P("BloomUpsampleFactor", "upsample factor", PK.Float, false, 0, 0);
                        P("color", "color", PK.Color, false, 0, 0);
                        break;
                    case "ChromaticAberration":
                        P("intensity", "intensity", PK.Float, false, 0, 0);
                        break;
                    case "ColorGrading":
                        P("temperature", "temperature", PK.Float, true, -100, 100);
                        P("tint", "tint", PK.Float, true, -100, 100);
                        P("colorFilter", "color filter", PK.Color, false, 0, 0);
                        P("hueShift", "hue shift", PK.Float, true, -180, 180);
                        P("saturation", "saturation", PK.Float, true, -100, 100);
                        P("brightness", "brightness", PK.Float, true, -100, 100);
                        P("contrast", "contrast", PK.Float, true, -100, 100);
                        foreach (string o in new[] { "Red", "Green", "Blue" })
                        {
                            foreach (string i in new[] { "Red", "Green", "Blue" })
                            {
                                P("mixer" + o + "Out" + i + "In", "channel mixer " + o.ToLowerInvariant() + " out, " + i.ToLowerInvariant() + " in", PK.Float, true, -200, 200);
                            }
                        }
                        break;
                    case "DepthOfField":
                        P("focusDistance", "focus distance", PK.Float, false, 0, 0);
                        P("aperture", "aperture", PK.Float, true, 0.05, 32);
                        P("focalLength", "focal length", PK.Float, true, 1, 300);
                        P("kernelSize", "max blur size", PK.Enum, false, 0, 0, null, "KernelSize", Names("Small", "Medium", "Large", "VeryLarge"), Seq(4));
                        break;
                    case "Grain":
                        P("colored", "colored", PK.Bool, false, 0, 0);
                        P("intensity", "intensity", PK.Float, true, 0, 1);
                        P("size", "size", PK.Float, true, 0.3, 3);
                        P("lumContrib", "luminance contribution", PK.Float, true, 0, 1);
                        break;
                    case "LensDistortion":
                        P("intensity", "intensity", PK.Float, true, -100, 100);
                        P("intensityX", "X multiplier", PK.Float, true, 0, 1);
                        P("intensityY", "Y multiplier", PK.Float, true, 0, 1);
                        P("centerX", "center X", PK.Float, true, -1, 1);
                        P("centerY", "center Y", PK.Float, true, -1, 1);
                        P("scale", "scale", PK.Float, true, 0.01, 5);
                        break;
                    case "Vignette":
                        P("color", "color", PK.Color, false, 0, 0);
                        P("center", "center", PK.Vec2, false, 0, 0);
                        P("intensity", "intensity", PK.Float, true, 0, 1);
                        P("smoothness", "smoothness", PK.Float, true, 0.01, 1);
                        P("roundness", "roundness", PK.Float, true, 0, 1);
                        P("rounded", "rounded", PK.Bool, false, 0, 0);
                        break;
                }
            }
            return b.Section;
        }

        /// <summary>Effect type names (game class names) in the order of the table.</summary>
        public static IEnumerable<string> EffectTypeNames => s_effects.Select(e => e.Type);

        public static string EffectFieldName(string effectType) => s_effects.First(e => e.Type == effectType).Field;

        /// <summary>Forced values the editor writes while an effect's header is open (applied when the effect is added or written).</summary>
        public static readonly string[][] ForcedEffectValues =
        {
            // effect type, member, enum type, enum value, also forces the override flag
            new[] { "ColorGrading", "gradingMode", PpNs + "GradingMode", "LowDefinitionRange", "override" },
            new[] { "Vignette", "mode", PpNs + "VignetteMode", "Classic", "" },
        };

        public static string PostProcessingNamespace => PpNs;
        public static string PostProcessingAssembly => PpAsm;

        // ------------------------------------------------------------------ type names for drift tests and docs

        /// <summary>System.Type.ToString() the member is expected to have (null = not checked).</summary>
        public static string ExpectedMemberType(ModSettingField f)
        {
            if (f.NoTypeCheck)
            {
                return null;
            }
            if (f.MemberType != null)
            {
                return f.MemberType;
            }
            switch (f.Kind)
            {
                case SettingKind.Bool: return "System.Boolean";
                case SettingKind.Int:
                case SettingKind.NamedInt: return "System.Int32";
                case SettingKind.Float: return "System.Single";
                case SettingKind.String: return "System.String";
                case SettingKind.Enum:
                case SettingKind.Flags: return f.EnumType;
                case SettingKind.Vector2: return "UnityEngine.Vector2";
                case SettingKind.Vector3: return "UnityEngine.Vector3";
                case SettingKind.Rect: return "UnityEngine.Rect";
                case SettingKind.Color: return "UnityEngine.Color";
                case SettingKind.Asset: return "UnityEngine." + f.AssetTypes[0];
                default: return null;
            }
        }

        // ------------------------------------------------------------------ docs

        public static string TypeName(ModSettingField f)
        {
            string t;
            switch (f.Kind)
            {
                case SettingKind.Bool: t = "bool"; break;
                case SettingKind.Int: t = "int"; break;
                case SettingKind.Float: t = "float"; break;
                case SettingKind.String: t = "string"; break;
                case SettingKind.Enum: t = "enum"; break;
                case SettingKind.Flags: t = "flags"; break;
                case SettingKind.NamedInt: t = "choice"; break;
                case SettingKind.Vector2: t = "vector2"; break;
                case SettingKind.Vector3: t = "vector3"; break;
                case SettingKind.Rect: t = "rect"; break;
                case SettingKind.Color: t = "color"; break;
                case SettingKind.Asset: t = "assetRef"; break;
                case SettingKind.AssetList: t = "assetRef[]"; break;
                case SettingKind.Trigger: t = "trigger"; break;
                default: t = "preset"; break;
            }
            return f.IsParam ? "param(" + t + ")" : t;
        }

        public static string FormatNumber(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

        public static string FormatDefault(ModSettingField f)
        {
            if (!f.HasDefault || f.Default == null)
            {
                return f.IsParam ? "(game default)" : "none";
            }
            object d = f.Default;
            if (d is bool b)
            {
                return b ? "true" : "false";
            }
            if (d is double n)
            {
                return FormatNumber(n);
            }
            if (d is string s)
            {
                return s;
            }
            if (d is string[] names)
            {
                return string.Join(", ", names);
            }
            if (d is double[] a)
            {
                string[] keys = f.Kind == SettingKind.Rect ? new[] { "x", "y", "width", "height" }
                    : f.Kind == SettingKind.Color ? new[] { "r", "g", "b", "a" }
                    : new[] { "x", "y", "z" };
                return "{" + string.Join(", ", a.Select((v, i) => keys[i] + ": " + FormatNumber(v)).ToArray()) + "}";
            }
            return d.ToString();
        }

        /// <summary>One-line English rule summary (shown when, choices, limits, notes) for list_mod_settings and the docs.</summary>
        public static string Summary(ModSettingField f)
        {
            List<string> parts = new List<string>();
            if (!f.Writable)
            {
                parts.Add("read-only: " + f.ReadOnlyReason);
            }
            if (f.Shown != null)
            {
                parts.Add("shown while " + f.Shown.Describe());
            }
            if (f.Kind == SettingKind.Enum || f.Kind == SettingKind.NamedInt || f.Kind == SettingKind.Flags)
            {
                if (f.Writable && f.OfferedNames != null)
                {
                    parts.Add((f.Kind == SettingKind.Flags ? "flags: " : "choices: ") + string.Join(", ", f.OfferedNames));
                }
            }
            if (f.Kind == SettingKind.Preset)
            {
                parts.Add("choices: the preset names of list_mod_settings (built-in files plus \"Custom\")");
            }
            if (f.Kind == SettingKind.Asset || f.Kind == SettingKind.AssetList)
            {
                parts.Add("asset type " + string.Join("/", f.AssetTypes) + ", " + f.AssetLocations + (f.Kind == SettingKind.Asset ? ", null allowed" : ""));
            }
            if (f.HasLimits)
            {
                string lo = f.Min.HasValue ? FormatNumber(f.Min.Value) : null;
                string hi = f.Max.HasValue ? FormatNumber(f.Max.Value) : null;
                string text = lo != null && hi != null ? $"clamped {lo}..{hi}" : lo != null ? $"minimum {lo}" : $"maximum {hi}";
                if (f.RangeFromAttribute)
                {
                    text += " (slider limits read from the game's Range attribute)";
                }
                parts.Add(text);
            }
            else if (f.IsParam && f.Kind == SettingKind.Float)
            {
                parts.Add("no limit (free input)");
            }
            if (f.Special == SettingSpecial.RectCentered)
            {
                parts.Add("width and height clamped 0..2000, centre kept within +-(1000 - half size)");
            }
            else if (f.Special == SettingSpecial.RectCenteredCircleAware)
            {
                parts.Add("width and height clamped 0..2000, centre kept within +-(1000 - half size); for the Circle shape the height is set to the width");
            }
            if (f.Note != null)
            {
                parts.Add(f.Note);
            }
            return string.Join("; ", parts.ToArray());
        }

        public static string ToMarkdown()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(DocsBegin).Append('\n');
            foreach (SettingsSection s in s_all)
            {
                sb.Append('\n').Append("**").Append(s.ModType).Append(" / ").Append(s.Name).Append("** (").Append(s.Title).Append(")\n\n");
                sb.Append("Saved to: ").Append(s.SavedTo).Append(". ").Append(s.Note).Append("\n\n");
                sb.Append("| Field | Type | Default | Rule |\n|---|---|---|---|\n");
                foreach (ModSettingField f in s.Fields)
                {
                    sb.Append("| `").Append(f.Name).Append("` | ").Append(TypeName(f)).Append(" | ").Append(FormatDefault(f).Replace("|", "\\|")).Append(" | ")
                      .Append(Summary(f).Replace("|", "\\|")).Append(" |\n");
                }
                foreach (ImpliedWrite im in s.Implied)
                {
                    sb.Append("\nImplied: ").Append(im.Why).Append(".\n");
                }
                foreach (SettingGuard g in s.Guards)
                {
                    sb.Append("\nRefused: `").Append(g.Field).Append("` = ").Append(g.Value).Append(" unless ").Append(g.Require.Describe()).Append(".\n");
                }
                if (s.HiddenNames.Length > 0)
                {
                    sb.Append("\nNot shown by the editor (refused): ").Append(string.Join(", ", s.HiddenNames)).Append(".\n");
                }
            }
            sb.Append('\n').Append("**Settings-related validator messages**\n\n| Mod | Section | Message |\n|---|---|---|\n");
            foreach (string[] v in ValidatorMessages)
            {
                sb.Append("| ").Append(v[0]).Append(" | ").Append(v[1]).Append(" | ").Append(v[2]).Append(" |\n");
            }
            sb.Append('\n').Append(DocsEnd);
            return sb.ToString();
        }
    }
}
