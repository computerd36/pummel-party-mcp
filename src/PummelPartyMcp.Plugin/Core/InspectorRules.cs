using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PummelPartyMcp.Core
{
    // =====================================================================================================
    // Inspector visibility rules (audit of the editor's inspectors, plugin 0.7.0).
    //
    // The editor draws a component either with the generic ModEditorGui.DrawValue loop over its reflected properties
    // (ModHideInInspector, headers and multiline strings are the only attributes it honours) or, for the 21 types with
    // [ModCustomInspector], with a hand-written ModInspector that decides which properties are drawn, when, with which
    // choices and limits. set_property / set_properties apply the same rules, so the plugin cannot produce a value the
    // editor UI cannot produce. Pure data and logic (no game or Unity types): values reach the rules in a normalized form
    // (see IRuleContext). Property names are verified against the real game assembly by PatchTests/GameDriftTest.
    // =====================================================================================================

    /// <summary>
    /// What the rule engine knows about a component's property values. Values are normalized by the game layer:
    /// bool; double (any number); string; string[] (names of the set enum flags, or the one enum value name; the mode
    /// name of a MinMaxCurve); a reference or an array of references is a bool (not null / holds a non-null element);
    /// <see cref="InspectorRules.Unknown"/> when the value cannot be read (the rule then allows the write).
    /// </summary>
    public interface IRuleContext
    {
        /// <summary>Value of the property after the call's assignments (assigned value, else the component's current one).</summary>
        object Get(string property);

        /// <summary>State the inspector asks the component object for that is not a property (null = cannot tell).</summary>
        bool? Derived(string name);
    }

    public sealed class RuleRef
    {
        public string Property;

        /// <summary>Enum value names the condition compares against (null for bool/number conditions).</summary>
        public string[] Names;
    }

    /// <summary>A "shown while ..." condition. Three-valued: null = cannot be evaluated (the write is allowed).</summary>
    public abstract class RuleCondition
    {
        public abstract bool? Evaluate(IRuleContext c);

        /// <summary>Text for "shown while &lt;this&gt;".</summary>
        public abstract string Describe();

        /// <summary>Text for the negated condition.</summary>
        public abstract string DescribeNot();

        public abstract void CollectRefs(List<RuleRef> into);

        public RuleCondition Not() => new Cond.NotCondition(this);
    }

    public static class Cond
    {
        /// <summary>The property's enum value (or flag set, or curve mode) contains one of <paramref name="names"/>.</summary>
        public static RuleCondition Is(string property, params string[] names) => new IsCondition(property, names);

        /// <summary>Bool property is true.</summary>
        public static RuleCondition On(string property) => new TruthCondition(property, property + " is on", property + " is off");

        /// <summary>Reference/array property holds at least one non-null reference.</summary>
        public static RuleCondition Has(string property, string yes, string no) => new TruthCondition(property, yes, no);

        public static RuleCondition NumEq(string property, double value) => new NumCondition(property, value, false);

        public static RuleCondition NumGt(string property, double value) => new NumCondition(property, value, true);

        public static RuleCondition All(params RuleCondition[] parts) => new Group(true, parts);

        public static RuleCondition Any(params RuleCondition[] parts) => new Group(false, parts);

        /// <summary>A state the inspector reads from the component object itself (resolved by the game layer).</summary>
        public static RuleCondition Derived(string name, string yes, string no) => new DerivedCondition(name, yes, no);

        internal static string NameList(string[] names)
        {
            if (names.Length == 1)
            {
                return names[0];
            }
            return string.Join(", ", names, 0, names.Length - 1) + " or " + names[names.Length - 1];
        }

        private sealed class IsCondition : RuleCondition
        {
            private readonly string m_property;
            private readonly string[] m_names;

            public IsCondition(string property, string[] names)
            {
                m_property = property;
                m_names = names;
            }

            public override bool? Evaluate(IRuleContext c)
            {
                object v = c.Get(m_property);
                if (v is string[] set)
                {
                    return set.Any(s => m_names.Contains(s, StringComparer.Ordinal));
                }
                if (v is string one)
                {
                    return m_names.Contains(one, StringComparer.Ordinal);
                }
                return null;
            }

            public override string Describe() => $"{m_property} is {NameList(m_names)}";

            public override string DescribeNot() => $"{m_property} is not {NameList(m_names)}";

            public override void CollectRefs(List<RuleRef> into) => into.Add(new RuleRef { Property = m_property, Names = m_names });
        }

        private sealed class TruthCondition : RuleCondition
        {
            private readonly string m_property;
            private readonly string m_yes;
            private readonly string m_no;

            public TruthCondition(string property, string yes, string no)
            {
                m_property = property;
                m_yes = yes;
                m_no = no;
            }

            public override bool? Evaluate(IRuleContext c) => c.Get(m_property) is bool b ? b : (bool?)null;

            public override string Describe() => m_yes;

            public override string DescribeNot() => m_no;

            public override void CollectRefs(List<RuleRef> into) => into.Add(new RuleRef { Property = m_property });
        }

        private sealed class NumCondition : RuleCondition
        {
            private readonly string m_property;
            private readonly double m_value;
            private readonly bool m_greater;

            public NumCondition(string property, double value, bool greater)
            {
                m_property = property;
                m_value = value;
                m_greater = greater;
            }

            public override bool? Evaluate(IRuleContext c)
            {
                if (!(c.Get(m_property) is double d))
                {
                    return null;
                }
                return m_greater ? d > m_value : d == m_value;
            }

            private string V => m_value.ToString("0.###", CultureInfo.InvariantCulture);

            public override string Describe() => m_greater ? $"{m_property} is greater than {V}" : $"{m_property} is {V}";

            public override string DescribeNot() => m_greater ? $"{m_property} is at most {V}" : $"{m_property} is not {V}";

            public override void CollectRefs(List<RuleRef> into) => into.Add(new RuleRef { Property = m_property });
        }

        private sealed class DerivedCondition : RuleCondition
        {
            private readonly string m_name;
            private readonly string m_yes;
            private readonly string m_no;

            public DerivedCondition(string name, string yes, string no)
            {
                m_name = name;
                m_yes = yes;
                m_no = no;
            }

            public override bool? Evaluate(IRuleContext c) => c.Derived(m_name);

            public override string Describe() => m_yes;

            public override string DescribeNot() => m_no;

            public override void CollectRefs(List<RuleRef> into) { }
        }

        private sealed class Group : RuleCondition
        {
            private readonly bool m_and;
            private readonly RuleCondition[] m_parts;

            public Group(bool and, RuleCondition[] parts)
            {
                m_and = and;
                m_parts = parts;
            }

            public override bool? Evaluate(IRuleContext c)
            {
                bool unknown = false;
                foreach (RuleCondition p in m_parts)
                {
                    bool? r = p.Evaluate(c);
                    if (r == null)
                    {
                        unknown = true;
                    }
                    else if (r.Value != m_and)
                    {
                        return r.Value; // false in an AND, true in an OR
                    }
                }
                return unknown ? (bool?)null : m_and;
            }

            public override string Describe() => string.Join(m_and ? " and " : " or ", m_parts.Select(p => p.Describe()).ToArray());

            public override string DescribeNot() => "not (" + Describe() + ")";

            public override void CollectRefs(List<RuleRef> into)
            {
                foreach (RuleCondition p in m_parts)
                {
                    p.CollectRefs(into);
                }
            }
        }

        public sealed class NotCondition : RuleCondition
        {
            private readonly RuleCondition m_inner;

            public NotCondition(RuleCondition inner)
            {
                m_inner = inner;
            }

            public override bool? Evaluate(IRuleContext c)
            {
                bool? r = m_inner.Evaluate(c);
                return r.HasValue ? !r.Value : (bool?)null;
            }

            public override string Describe() => m_inner.DescribeNot();

            public override string DescribeNot() => m_inner.Describe();

            public override void CollectRefs(List<RuleRef> into) => m_inner.CollectRefs(into);
        }
    }

    /// <summary>What the inspector does with one property of a component with a custom inspector.</summary>
    public sealed class PropertyRule
    {
        public string Component;
        public string Property;

        /// <summary>The custom inspector never draws the property (or draws a different one in its place).</summary>
        public bool NeverDrawn;

        /// <summary>Drawn only while the condition holds (null = always drawn).</summary>
        public RuleCondition Shown;

        /// <summary>Enum value names the inspector's dropdown omits.</summary>
        public string[] ExcludedValues;

        /// <summary>Enum value names the inspector offers (all others cannot be chosen).</summary>
        public string[] AllowedValues;

        /// <summary>Flags enum: the only single flags the inspector can toggle.</summary>
        public string[] AllowedFlags;

        /// <summary>Slider / Mathf.Clamp limits the inspector re-applies while drawing.</summary>
        public double? Min;

        public double? Max;

        public string Note;

        public bool HasRestriction => ExcludedValues != null || AllowedValues != null || AllowedFlags != null;
    }

    public static class InspectorRules
    {
        /// <summary>Value the context returns for a property it cannot read (rules then allow the write).</summary>
        public static readonly object Unknown = new object();

        /// <summary>The 21 components with [ModCustomInspector] in game version 1.14.1k (ModAINavigationInspector exists but is attached to nothing).</summary>
        public static readonly string[] CustomInspectorComponents =
        {
            "ModAudioSource", "ModBoardSpace", "ModBoardSpaceEvent", "ModDestructible", "ModFireWeapon", "ModItem", "ModLight",
            "ModLogic", "ModMeshCollider", "ModMover", "ModNavMeshAgent", "ModNote", "ModParticleSystem", "ModPlayerSpawn",
            "ModPrefabInstance", "ModProp", "ModSimpleAnimator", "ModSpawner", "ModText", "ModTrigger", "ModWater",
        };

        /// <summary>
        /// ModReflectionType names the generic inspector (ModEditorGui.DrawValue) draws no editable control for: no case at
        /// all (red type text) or a case without any input. No generic component of 1.14.1k has such a property today.
        /// </summary>
        public static readonly string[] GenericNotDrawnTypes =
        {
            "UNKNOWN", "CHAR", "SHORT", "USHORT", "LONG", "ULONG", "MINMAXCURVE", "BURST", "SERIALIZABLEGUID",
            "BOARDNODECONNECTION", "SIMPLEANIMATIONLAYER", "SIMPLEANIMATION", "VOID",
        };

        public static bool HasCustomInspector(string component) => Array.IndexOf(CustomInspectorComponents, component) >= 0;

        // ------------------------------------------------------------------ audit snapshot (drift guard)

        /// <summary>
        /// Every reflected (non-[ModIgnore]) property of each custom-inspector component in 1.14.1k, as audited. The offline
        /// drift test compares this with the game assembly: a game update that adds or renames a property fails it until the
        /// inspector has been re-read.
        /// </summary>
        public static readonly Dictionary<string, string> AuditedProperties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ModItem"] = "ItemType WeaponInfo ItemPrefab ItemPrefabGUID ItemHoldAnimation ItemBeaconColor CustomItemBeaconColor CanBeDropped DisappearWhenDropped AutoDropAfterTime DropAfterTime UniqueDropPrefab DropOnDeath DroppedItemPrefab DroppedItemPrefabGUID PickupSoundType UniquePickup ItemPickupPriority WeaponType Cooldown HasLimitedUses DropAfterUses OnPickupTrigger OnDropTrigger OnUseTrigger OnWhileHeldTrigger SecondsBetweenHeldAction HeldPositionOffset HeldRotationOffset ItemSlot",
            ["ModDestructible"] = "OnHitActions",
            ["ModAudioSource"] = "audioSourceType audioClip playOnAwake loop volume pitch stereoPanning spatialBlend useDopplerEffect rolloffMode minDistance maxDistance",
            ["ModBoardSpace"] = "baseNodeType nodeEventType nodeEventAction hasInteraction nodeConnections CustomGoalSpawn BlockGoalSpawn SpawnRotation SpawnDistance ConsumeStepOnPass StopRocket DisabledOnStart ModCustomBoardSpace",
            ["ModBoardSpaceEvent"] = "OnEnterTrigger OnPassTrigger",
            ["ModLight"] = "type color range intensity spotAngle shadows",
            ["ModLogic"] = "Triggers",
            ["ModMeshCollider"] = "convex mesh",
            ["ModMover"] = "DirectionType Direction Speed RotationAxisType RotationAxis RotationSpeed",
            ["ModNavMeshAgent"] = "agentTypeID baseOffset speed angularSpeed acceleration stoppingDistance autoBraking radius height quality priority autoTraverseOffMeshLink autoRepath areaMask",
            ["ModNote"] = "Text",
            ["ModParticleSystem"] = "duration looping prewarm startDelay startLifetime startSpeed startSize3D startSize startSizeX startSizeY startSizeZ startRotation3D startRotationX startRotationY startRotationZ flipRotation startColor gravityModifier simulationSpace simulationSpeed useUnscaledTime scalingMode playOnAwake emitterVelocityMode maxParticles autoRandomSeed randomSeed stopAction cullingMode ringBufferMode emissionEnabled rateOverTime rateOverDistance emissionBursts shapeEnabled shapeType angle radius radiusThickness boxThickness arc arcMode arcSpread arcSpeed length donutRadius position rotation scale alignToDirection randomizeDirection sphericalDirectionAmount randomPositionAmount velocityOverLifeTimeEnabled x y z space orbitalX orbitalY orbitalZ orbitalOffsetX orbitalOffsetY orbitalOffsetZ radial speedModifier limitVelocityOverLifetimeEnabled velocityOverLifeTimeSpace separateAxes speed speedX speedY speedZ dampen drag multiplyBySize multiplyByVelocity inheritVelocityEnabled inheritVelocityMode multiplier lifetimeByEmitterSpeedEnabled lifetimeByEmitterSpeedMultiplier lifetimeByEmitterSpeedRange forceOverLifetimeEnabled forceOverLifetimeX forceOverLifetimeY forceOverLifetimeZ forceOverLifetimeSpace forceOverLifetimeRandomize colorOverLifetimeEnabled colorOverLifetimeColor colorBySpeedEnabled colorBySpeedColor Prop Alignment colorBySpeedRange sizeOverLifetimeEnabled sizeOverLifetimeSeparateAxes sizeOverLifetimeSize sizeOverLifetimeX sizeOverLifetimeY sizeOverLifetimeZ sizeBySpeedEnabled sizeBySpeedSeparateAxes sizeBySpeedSize sizeBySpeedSizeX sizeBySpeedSizeY sizeBySpeedSizeZ sizeBySpeedRange rotationOverLifetimeEnabled rotationOverLifetimeSeparateAxes rotationOverLifetimeX rotationOverLifetimeY rotationOverLifetimeZ rotationBySpeedEnabled rotationBySpeedSeparateAxes rotationBySpeedX rotationBySpeedY rotationBySpeedZ rotationBySpeedRange externalForcesEnabled externalForcesMultiplier externalForcesInfluenceFilter externalForcesLayerMask noiseEnabled noiseSeparateAxes noiseStrength noiseStrengthX noiseStrengthY noiseStrengthZ noiseFrequency noiseScrollSpeed noiseDamping noiseOctaves noiseOctaveMultiplier noiseOctaveScale noiseQuality noiseRemapEnabled noiseRemapCurve noiseRemapCurveX noiseRemapCurveY noiseRemapCurveZ noisePositionAmount noiseRotationAmount noiseSizeAmount textureSheetAnimationEnabled animationMode numTilesX numTilesY sheetAnimation timeMode frameOverTime startFrame cycleCount uvChannelMask rowMode rowIndex speedRange fps rendererEnabled renderMode cameraVelocityScale velocityScale lengthScale freeformStretching rotateWithStretchDirection normalDirection material sortMode sortingFudge minParticleSize maxParticleSize flip allowRoll pivot maskInteraction shadowCastingMode shadowBias motionVectorGenerationMode",
            ["ModPlayerSpawn"] = "SharedSpawn SpawnUsageType AllowedPlayers SpawnShape PlayerCountMask SpawnDistribution LineLength Radius Advanced",
            ["ModPrefabInstance"] = "PrefabReference PrefabGUID",
            ["ModProp"] = "prop tintColor collisionType shadowCastingMode customMaterials",
            ["ModSimpleAnimator"] = "UseLocalPosition UseLocalRotation UseLateUpdate TimeOffset Animations",
            ["ModSpawner"] = "Prefabs PrefabGUIDs Interval1 Delay Type LocationType SpawnPoints SpawnCount",
            ["ModText"] = "Text Font Color FontSize HorizontalAlignment Size FontStyles Outline OutlineThickness OutlineColor Shadow ShadowColor ShadowOffsetX ShadowOffsetY ShadowSoftness",
            ["ModTrigger"] = "TriggerShape Size Center Radius Height TriggerOnHit TriggerOnEnter TriggerOnExit TriggerOnStay OnHitActions OnEnterActions OnExitActions OnStayActions StayTriggerInterval DisableAfterTriggered OneUsePerPlayer",
            ["ModWater"] = "Width Height CellSize DeepColor ShallowColor FrothColor WaveHeight WaveSpeed",
            ["ModFireWeapon"] = "FireTime BeamCooldown WeaponInfo WeaponHitTrigger",
        };

        // ------------------------------------------------------------------ the rule table

        private static readonly List<PropertyRule> s_rules = BuildRules();
        private static readonly Dictionary<string, PropertyRule> s_byKey = s_rules.ToDictionary(r => r.Component + "." + r.Property, StringComparer.Ordinal);

        public static IReadOnlyList<PropertyRule> All => s_rules;

        public static PropertyRule Find(string component, string property) =>
            s_byKey.TryGetValue(component + "." + property, out PropertyRule r) ? r : null;

        public static IEnumerable<PropertyRule> ForComponent(string component) => s_rules.Where(r => r.Component == component);

        public static bool TryGetClamp(string component, string property, out double? min, out double? max)
        {
            PropertyRule r = Find(component, property);
            if (r != null && (r.Min.HasValue || r.Max.HasValue))
            {
                min = r.Min;
                max = r.Max;
                return true;
            }
            min = max = null;
            return false;
        }

        private static List<PropertyRule> BuildRules()
        {
            List<PropertyRule> list = new List<PropertyRule>();
            Dictionary<string, PropertyRule> seen = new Dictionary<string, PropertyRule>(StringComparer.Ordinal);

            PropertyRule Get(string c, string p)
            {
                string key = c + "." + p;
                if (!seen.TryGetValue(key, out PropertyRule r))
                {
                    r = new PropertyRule { Component = c, Property = p };
                    seen[key] = r;
                    list.Add(r);
                }
                return r;
            }
            void Never(string c, string note, params string[] props)
            {
                foreach (string p in props)
                {
                    PropertyRule r = Get(c, p);
                    r.NeverDrawn = true;
                    r.Note = note;
                }
            }
            void Show(string c, RuleCondition when, params string[] props)
            {
                foreach (string p in props)
                {
                    Get(c, p).Shown = when;
                }
            }
            void Clamp(string c, string p, double? min, double? max, string note = null)
            {
                PropertyRule r = Get(c, p);
                r.Min = min;
                r.Max = max;
                if (note != null)
                {
                    r.Note = note;
                }
            }
            void Exclude(string c, string p, params string[] values) => Get(c, p).ExcludedValues = values;
            void Allow(string c, string p, params string[] values) => Get(c, p).AllowedValues = values;
            void Note(string c, string p, string note) => Get(c, p).Note = note;

            // ---- ModAudioSource (ModAudioSourceInspector)
            Clamp("ModAudioSource", "volume", 0, 1);
            Clamp("ModAudioSource", "spatialBlend", 0, 1);
            Clamp("ModAudioSource", "stereoPanning", -1, 1);
            Show("ModAudioSource", Cond.NumEq("spatialBlend", 1).Not(), "stereoPanning");
            Show("ModAudioSource", Cond.NumEq("spatialBlend", 0).Not(), "rolloffMode", "minDistance", "maxDistance", "useDopplerEffect");
            Exclude("ModAudioSource", "rolloffMode", "Custom");

            // ---- ModBoardSpace (ModBoardSpaceInspector; Board mods only)
            Never("ModBoardSpace", "never drawn", "nodeEventType", "nodeEventAction", "hasInteraction");
            Exclude("ModBoardSpace", "baseNodeType", "MapEvent", "Trophy", "Weapon");
            Show("ModBoardSpace", Cond.Is("baseNodeType", "Custom"), "ModCustomBoardSpace", "ConsumeStepOnPass", "StopRocket");
            Show("ModBoardSpace", Cond.Is("baseNodeType", "Start", "Pathing").Not(), "DisabledOnStart");
            Show("ModBoardSpace", Cond.On("CustomGoalSpawn"), "SpawnRotation", "SpawnDistance");
            Clamp("ModBoardSpace", "SpawnRotation", 0, 360);
            Clamp("ModBoardSpace", "SpawnDistance", 0, 4);
            Note("ModBoardSpace", "CustomGoalSpawn", "drawn only while the space can support a goal spawn (depends on the board graph: not at the end of a teleport, not an intersection, not Pathing/Graveyard or a custom space that cannot be landed on); not checked by the plugin");
            Note("ModBoardSpace", "BlockGoalSpawn", "drawn only while the space can support a goal spawn (board graph); not checked by the plugin");

            // ---- ModFireWeapon
            Clamp("ModFireWeapon", "FireTime", 0.1, null);
            Show("ModFireWeapon", Cond.Derived("WeaponHitTypeIsBeam", "the weapon's damage type is Beam", "the weapon's damage type is not Beam"), "BeamCooldown");

            // ---- ModItem (ModItemInspector; Minigame mods only)
            RuleCondition notPickup = Cond.Is("ItemType", "Pickup").Not();
            RuleCondition useable = Cond.Is("ItemType", "Carried", "Weapon");
            Never("ModItem", "never drawn (the inspector draws the prefab reference instead)", "ItemPrefabGUID", "DroppedItemPrefabGUID");
            Show("ModItem", notPickup, "ItemSlot", "UniqueDropPrefab", "UniquePickup", "ItemPickupPriority", "HeldPositionOffset",
                "HeldRotationOffset", "SecondsBetweenHeldAction", "CanBeDropped", "AutoDropAfterTime", "DropOnDeath");
            Show("ModItem", Cond.Is("ItemType", "Worn", "Pickup").Not(), "ItemHoldAnimation");
            Show("ModItem", Cond.All(notPickup, Cond.On("UniqueDropPrefab")), "DroppedItemPrefab");
            Show("ModItem", Cond.Is("ItemBeaconColor", "Custom"), "CustomItemBeaconColor");
            Show("ModItem", Cond.Is("ItemType", "Weapon"), "WeaponType");
            Show("ModItem", useable, "Cooldown", "HasLimitedUses");
            Show("ModItem", Cond.All(useable, Cond.On("HasLimitedUses")), "DropAfterUses");
            Show("ModItem", Cond.All(notPickup, Cond.On("AutoDropAfterTime")), "DropAfterTime");
            Show("ModItem", Cond.All(notPickup, Cond.Any(Cond.On("CanBeDropped"), Cond.On("AutoDropAfterTime"), Cond.On("DropOnDeath"))), "DisappearWhenDropped");
            Clamp("ModItem", "SecondsBetweenHeldAction", 0.1, null);
            Clamp("ModItem", "Cooldown", 0.1, null);
            Note("ModItem", "UniquePickup", "for Pickup items the inspector resets UniquePickup to false and ItemPickupPriority to 0 every frame");

            // ---- ModLight (ModLightInspector)
            Show("ModLight", Cond.Is("type", "Directional").Not(), "range");
            Show("ModLight", Cond.Is("type", "Spot"), "spotAngle");
            Clamp("ModLight", "spotAngle", 0, 180);

            // ---- ModMeshCollider
            Show("ModMeshCollider", Cond.Derived("NoLinkedRenderer", "no mesh renderer on the object provides the mesh", "a mesh renderer on the object provides the mesh"), "mesh");

            // ---- ModMover
            Show("ModMover", Cond.Is("DirectionType", "Custom"), "Direction");
            Show("ModMover", Cond.Is("RotationAxisType", "Custom"), "RotationAxis");

            // ---- ModNavMeshAgent (not addable; its inspector has copy-paste bugs)
            Never("ModNavMeshAgent", "never drawn (the inspector draws other properties under these labels)", "speed", "quality", "autoTraverseOffMeshLink");
            Note("ModNavMeshAgent", "agentTypeID", "uncertain: the 'Agent Type' combo discards its result and the 'Quality' dropdown writes agentTypeID, so the UI only produces 0..4 (ObstacleAvoidanceType values); not enforced");

            // ---- ModParticleSystem (ModParticleSystemInspector)
            Never("ModParticleSystem", "never drawn", "startSize", "arcSpeed", "allowRoll", "shadowBias", "animationMode", "fps");
            Show("ModParticleSystem", Cond.On("startSize3D"), "startSizeY", "startSizeZ");
            Show("ModParticleSystem", Cond.On("startRotation3D"), "startRotationX", "startRotationY");
            Show("ModParticleSystem", Cond.On("autoRandomSeed").Not(), "randomSeed");
            Exclude("ModParticleSystem", "simulationSpace", "Custom");
            Allow("ModParticleSystem", "shapeType", "Sphere", "Hemisphere", "Box", "BoxShell", "BoxEdge", "Cone", "ConeVolume", "Donut");
            RuleCondition notBox = Cond.Is("shapeType", "Box", "BoxShell", "BoxEdge").Not();
            Show("ModParticleSystem", Cond.Is("shapeType", "Cone", "ConeVolume"), "angle", "length");
            Show("ModParticleSystem", notBox, "radius", "radiusThickness", "arc", "arcMode", "arcSpread");
            Show("ModParticleSystem", Cond.Is("shapeType", "Donut"), "donutRadius");
            Show("ModParticleSystem", Cond.Is("shapeType", "BoxShell", "BoxEdge"), "boxThickness");
            Clamp("ModParticleSystem", "radiusThickness", 0, 1);
            Clamp("ModParticleSystem", "arc", 0, 360);
            Clamp("ModParticleSystem", "arcSpread", 0, 1);
            Show("ModParticleSystem", Cond.On("separateAxes").Not(), "speed");
            Show("ModParticleSystem", Cond.On("separateAxes"), "speedX", "speedY", "speedZ", "velocityOverLifeTimeSpace");
            Show("ModParticleSystem", Cond.Is("forceOverLifetimeX", "TwoConstants"), "forceOverLifetimeRandomize");
            Show("ModParticleSystem", Cond.On("sizeOverLifetimeSeparateAxes").Not(), "sizeOverLifetimeSize");
            Show("ModParticleSystem", Cond.On("sizeOverLifetimeSeparateAxes"), "sizeOverLifetimeX", "sizeOverLifetimeY", "sizeOverLifetimeZ");
            Show("ModParticleSystem", Cond.On("sizeBySpeedSeparateAxes").Not(), "sizeBySpeedSize");
            Show("ModParticleSystem", Cond.On("sizeBySpeedSeparateAxes"), "sizeBySpeedSizeX", "sizeBySpeedSizeY", "sizeBySpeedSizeZ");
            Show("ModParticleSystem", Cond.On("rotationOverLifetimeSeparateAxes"), "rotationOverLifetimeX", "rotationOverLifetimeY");
            Show("ModParticleSystem", Cond.On("rotationBySpeedSeparateAxes"), "rotationBySpeedY", "rotationBySpeedZ");
            Show("ModParticleSystem", Cond.On("noiseSeparateAxes").Not(), "noiseStrength");
            Show("ModParticleSystem", Cond.On("noiseSeparateAxes"), "noiseStrengthX", "noiseStrengthY", "noiseStrengthZ");
            Show("ModParticleSystem", Cond.NumGt("noiseOctaves", 1), "noiseOctaveMultiplier", "noiseOctaveScale");
            Show("ModParticleSystem", Cond.All(Cond.On("noiseRemapEnabled"), Cond.On("noiseSeparateAxes").Not()), "noiseRemapCurve");
            Show("ModParticleSystem", Cond.All(Cond.On("noiseRemapEnabled"), Cond.On("noiseSeparateAxes")), "noiseRemapCurveX", "noiseRemapCurveY", "noiseRemapCurveZ");
            Note("ModParticleSystem", "noiseRemapCurveY", "uncertain: the inspector's three-axis widget is wired to noiseRemapCurveX for all three axes, so the UI only produces Y and Z equal to X; not enforced");
            Show("ModParticleSystem", Cond.Is("rowMode", "Custom"), "rowIndex");
            Show("ModParticleSystem", Cond.Is("timeMode", "Lifetime"), "frameOverTime", "cycleCount");
            Show("ModParticleSystem", Cond.Is("timeMode", "Speed"), "speedRange");
            Show("ModParticleSystem", Cond.Is("renderMode", "Mesh"), "Prop");
            Show("ModParticleSystem", Cond.Is("renderMode", "Stretch"), "cameraVelocityScale", "velocityScale", "lengthScale", "freeformStretching");
            Show("ModParticleSystem", Cond.All(Cond.Is("renderMode", "Stretch"), Cond.On("freeformStretching")), "rotateWithStretchDirection");

            // ---- ModPlayerSpawn
            Show("ModPlayerSpawn", Cond.Is("SpawnShape", "Line"), "LineLength");
            Show("ModPlayerSpawn", Cond.Is("SpawnShape", "Circle"), "Radius");
            Show("ModPlayerSpawn", Cond.Is("SpawnShape", "Line", "Circle"), "SpawnDistribution");
            Show("ModPlayerSpawn", Cond.Is("SpawnUsageType", "InitialSpawn"), "SharedSpawn");
            Show("ModPlayerSpawn", Cond.On("Advanced"), "AllowedPlayers", "PlayerCountMask");

            // ---- ModPrefabInstance
            Never("ModPrefabInstance", "never drawn (the inspector draws PrefabReference)", "PrefabGUID");

            // ---- ModProp
            Show("ModProp", Cond.Has("customMaterials", "customMaterials holds at least one material", "customMaterials holds no material").Not(), "tintColor");

            // ---- ModSimpleAnimator
            Never("ModSimpleAnimator", "never drawn (the inspector asks for a 'UseBaseRotation' property that does not exist)", "UseLateUpdate");

            // ---- ModSpawner
            Clamp("ModSpawner", "SpawnCount", 0, 20);
            Never("ModSpawner", "never drawn (the inspector draws Prefabs)", "PrefabGUIDs");
            Show("ModSpawner", Cond.Is("Type", "AlwaysSpawn"), "Interval1");
            Show("ModSpawner", Cond.Is("Type", "SpawnOnDestruction"), "Delay");
            Show("ModSpawner", Cond.Is("LocationType", "OnTargets"), "SpawnPoints");

            // ---- ModText
            Exclude("ModText", "Font", "COUNT");
            Allow("ModText", "HorizontalAlignment", "Left", "Center", "Right", "Justified");
            Get("ModText", "FontStyles").AllowedFlags = new[] { "Bold", "Italic", "Underline", "Strikethrough" };
            Show("ModText", Cond.On("Outline"), "OutlineColor", "OutlineThickness");
            Show("ModText", Cond.On("Shadow"), "ShadowColor", "ShadowOffsetX", "ShadowOffsetY", "ShadowSoftness");
            Clamp("ModText", "OutlineThickness", 0, 1);
            Clamp("ModText", "ShadowOffsetX", -1, 1);
            Clamp("ModText", "ShadowOffsetY", -1, 1);
            Clamp("ModText", "ShadowSoftness", 0, 1);

            // ---- ModTrigger (ModTriggerInspector)
            RuleCondition standardTrigger = Cond.All(Cond.On("TriggerOnHit").Not(), Cond.Is("TriggerShape", "CustomCollider").Not());
            Show("ModTrigger", Cond.Is("TriggerShape", "CustomCollider").Not(), "Center");
            Show("ModTrigger", Cond.Is("TriggerShape", "Box"), "Size");
            Show("ModTrigger", Cond.Is("TriggerShape", "Sphere", "Capsule"), "Radius");
            Show("ModTrigger", Cond.Is("TriggerShape", "Capsule"), "Height");
            Show("ModTrigger", standardTrigger, "TriggerOnEnter", "TriggerOnExit", "TriggerOnStay");
            Show("ModTrigger", Cond.All(standardTrigger, Cond.On("TriggerOnStay")), "StayTriggerInterval");
            Clamp("ModTrigger", "StayTriggerInterval", 0.1, null);

            // ---- ModWater
            Clamp("ModWater", "CellSize", 0.1, 10);
            Clamp("ModWater", "WaveHeight", 0, 1);
            Clamp("ModWater", "WaveSpeed", 0, 10);

            return list;
        }

        // ------------------------------------------------------------------ evaluation

        /// <summary>Context for one set_properties call: the call's values win over the component's current ones.</summary>
        public sealed class AfterCallContext : IRuleContext
        {
            private readonly IDictionary<string, object> m_pending;
            private readonly Func<string, object> m_live;
            private readonly Func<string, bool?> m_derived;

            public AfterCallContext(IDictionary<string, object> pending, Func<string, object> live, Func<string, bool?> derived)
            {
                m_pending = pending;
                m_live = live;
                m_derived = derived;
            }

            public object Get(string property)
            {
                if (m_pending != null && m_pending.TryGetValue(property, out object v))
                {
                    return v;
                }
                try
                {
                    return m_live == null ? Unknown : (m_live(property) ?? Unknown);
                }
                catch (Exception)
                {
                    return Unknown;
                }
            }

            public bool? Derived(string name)
            {
                try
                {
                    return m_derived?.Invoke(name);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Refusal text for a property that the component's custom inspector never draws; null = drawn (or no rule).
        /// </summary>
        public static string NeverDrawnRefusal(string component, string uiName, string property)
        {
            PropertyRule r = Find(component, property);
            return r == null || !r.NeverDrawn ? null : NeverDrawnRefusal(r, uiName);
        }

        private static string NeverDrawnRefusal(PropertyRule r, string uiName) =>
            $"the editor's inspector does not offer '{r.Property}' on {uiName} ({r.Note}), so the editor UI cannot set it";

        /// <summary>Refusal text for a property the generic inspector draws no control for; null = drawn.</summary>
        public static string GenericNotDrawnRefusal(string reflectionTypeName, string typeName, string property)
        {
            return Array.IndexOf(GenericNotDrawnTypes, reflectionTypeName) >= 0
                ? $"the editor's generic inspector draws no control for '{property}' (type {typeName}), so the editor UI cannot set it"
                : null;
        }

        /// <summary>
        /// Checks one assigned property against the component's rules (conditions evaluated on <paramref name="ctx"/>, which
        /// must already include the whole call's values) and the choice restrictions on the normalized value. Null = allowed.
        /// </summary>
        public static string Check(string component, string uiName, string property, object normalizedValue, IRuleContext ctx) =>
            CheckRule(Find(component, property), uiName, normalizedValue, ctx);

        /// <summary>Same as <see cref="Check"/> for an explicit rule (the engine is testable with fake rule data).</summary>
        public static string CheckRule(PropertyRule r, string uiName, object normalizedValue, IRuleContext ctx)
        {
            if (r == null)
            {
                return null;
            }
            string property = r.Property;
            if (r.NeverDrawn)
            {
                return NeverDrawnRefusal(r, uiName);
            }
            if (r.Shown != null && r.Shown.Evaluate(ctx) == false)
            {
                string state = DescribeState(r.Shown, ctx);
                return $"'{property}' is only shown by the editor's inspector while {r.Shown.Describe()}{state}; " +
                       "set the controlling property first (or in the same set_properties call)";
            }
            string[] names = normalizedValue as string[] ?? (normalizedValue is string s ? new[] { s } : null);
            if (names != null && names.Length > 0)
            {
                if (r.ExcludedValues != null)
                {
                    foreach (string n in names)
                    {
                        if (Array.IndexOf(r.ExcludedValues, n) >= 0)
                        {
                            return $"'{property}': the editor's inspector does not offer the value {n} (not offered: {string.Join(", ", r.ExcludedValues)})";
                        }
                    }
                }
                if (r.AllowedValues != null)
                {
                    foreach (string n in names)
                    {
                        if (Array.IndexOf(r.AllowedValues, n) < 0)
                        {
                            return $"'{property}': the editor's inspector cannot choose {n}; it offers only {Cond.NameList(r.AllowedValues)}";
                        }
                    }
                }
                if (r.AllowedFlags != null)
                {
                    foreach (string n in names)
                    {
                        if (Array.IndexOf(r.AllowedFlags, n) < 0)
                        {
                            return $"'{property}': the editor's inspector cannot toggle the flag {n}; it offers only {Cond.NameList(r.AllowedFlags)}";
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>" (type is Point after this call)": the current state of what the condition looks at.</summary>
        private static string DescribeState(RuleCondition cond, IRuleContext ctx)
        {
            List<RuleRef> refs = new List<RuleRef>();
            cond.CollectRefs(refs);
            List<string> parts = new List<string>();
            foreach (string name in refs.Select(x => x.Property).Distinct())
            {
                object v = ctx.Get(name);
                string text = v is string[] set ? (set.Length == 0 ? "none" : string.Join(", ", set))
                    : v is bool b ? (b ? "on" : "off")
                    : v is double d ? d.ToString("0.###", CultureInfo.InvariantCulture)
                    : v is string str ? str : null;
                if (text != null)
                {
                    parts.Add($"{name} is {text}");
                }
            }
            return parts.Count == 0 ? "" : " (" + string.Join(", ", parts.ToArray()) + " after this call)";
        }

        // ------------------------------------------------------------------ reference data for tests and docs

        /// <summary>Components that carry at least one rule, in table order.</summary>
        public static IEnumerable<string> RuleComponents => s_rules.Select(r => r.Component).Distinct();

        /// <summary>One-line English summary of a rule for get_object ("shownWhen") and the docs.</summary>
        public static string Summary(PropertyRule r)
        {
            List<string> parts = new List<string>();
            if (r.NeverDrawn)
            {
                parts.Add("never shown");
            }
            if (r.Shown != null)
            {
                parts.Add("shown while " + r.Shown.Describe());
            }
            if (r.ExcludedValues != null)
            {
                parts.Add("not offered: " + string.Join(", ", r.ExcludedValues));
            }
            if (r.AllowedValues != null)
            {
                parts.Add("only " + string.Join(", ", r.AllowedValues));
            }
            if (r.AllowedFlags != null)
            {
                parts.Add("flags only " + string.Join(", ", r.AllowedFlags));
            }
            if (r.Min.HasValue || r.Max.HasValue)
            {
                string lo = r.Min.HasValue ? r.Min.Value.ToString("0.###", CultureInfo.InvariantCulture) : null;
                string hi = r.Max.HasValue ? r.Max.Value.ToString("0.###", CultureInfo.InvariantCulture) : null;
                parts.Add(lo != null && hi != null ? $"clamped {lo}..{hi}" : lo != null ? $"minimum {lo}" : $"maximum {hi}");
            }
            if (r.Note != null && !r.NeverDrawn)
            {
                parts.Add("note: " + r.Note);
            }
            return string.Join("; ", parts.ToArray());
        }

        public const string DocsBegin = "<!-- inspector-rules:begin (generated from Core/InspectorRules.cs; a test keeps it in sync) -->";
        public const string DocsEnd = "<!-- inspector-rules:end -->";

        /// <summary>The markdown block in docs/plugin-api.md (a test compares it with the file).</summary>
        public static string ToMarkdown()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(DocsBegin).Append('\n');
            foreach (string component in CustomInspectorComponents)
            {
                List<PropertyRule> rules = ForComponent(component).ToList();
                sb.Append('\n').Append("**").Append(component).Append("**");
                if (rules.Count == 0)
                {
                    sb.Append(": every settable property is always drawn without restrictions.\n");
                    continue;
                }
                sb.Append('\n').Append('\n');
                sb.Append("| Property | Rule |\n|---|---|\n");
                foreach (PropertyRule r in rules)
                {
                    sb.Append("| `").Append(r.Property).Append("` | ").Append(Summary(r).Replace("|", "\\|")).Append(" |\n");
                }
                sb.Append("\nAll other properties of ").Append(component).Append(" are always drawn.\n");
            }
            sb.Append('\n').Append(DocsEnd);
            return sb.ToString();
        }
    }
}
