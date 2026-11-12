using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PummelPartyMcp.Core
{
    /// <summary>One game member the plugin looks up by name (reflection, Harmony target or rule table) and cannot check at compile time.</summary>
    public sealed class RequiredMember
    {
        /// <summary>"field", "property", "method" or "enumValue".</summary>
        public string Kind;

        /// <summary>Full name of the declaring type in Assembly-CSharp (e.g. "ModSystem.Logic.ModLogicTrigger").</summary>
        public string Type;

        public string Name;

        /// <summary>Field/property: expected System.Type.ToString() of its type (null = not checked). enumValue: the property whose enum must define Name.</summary>
        public string Extra;

        public bool IsStatic;

        /// <summary>Method: parameter type names (System.Type.ToString()).</summary>
        public string[] Params;

        /// <summary>Where the plugin uses it.</summary>
        public string UsedBy;

        public string Id => Kind == "enumValue" ? $"{Type}.{Extra} = {Name}" : $"{Type}.{Name}";
    }

    /// <summary>
    /// The central registry of "members the plugin needs": the one list behind the runtime self_test tool and the offline
    /// drift test (PatchTests/GameDriftTest), so the two cannot diverge. Pure reflection by name: works on a loaded or a
    /// reflection-only Assembly-CSharp. A test also scans the plugin sources for by-name lookups missing here.
    /// </summary>
    public static class RequiredMembers
    {
        private static readonly List<RequiredMember> s_all = Build();

        public static IReadOnlyList<RequiredMember> All => s_all;

        private static List<RequiredMember> Build()
        {
            List<RequiredMember> l = new List<RequiredMember>();
            void F(string type, string name, string fieldType, string use, bool isStatic = false) =>
                l.Add(new RequiredMember { Kind = "field", Type = type, Name = name, Extra = fieldType, IsStatic = isStatic, UsedBy = use });
            void P(string type, string name, string use, bool isStatic = false) =>
                l.Add(new RequiredMember { Kind = "property", Type = type, Name = name, IsStatic = isStatic, UsedBy = use });
            void M(string type, string name, string use, params string[] parameters) =>
                l.Add(new RequiredMember { Kind = "method", Type = type, Name = name, Params = parameters, UsedBy = use });

            // editor state, undo, playtest control
            F("EditorSystem", "m_currentPrefab", null, "EditorAccess");
            F("EditorSystem", "m_heirarchyUndoManager", "EditorUndo", "EditorAccess, UndoAccess");
            F("EditorSystem", "m_undoManagerList", null, "EditorAccess, UndoAccess");
            F("EditorSystem", "m_toolbarWidget", "ToolbarWidget", "PlaytestAccess");
            F("EditorUndo", "m_undoEventStack", null, "UndoAccess");
            F("ToolbarWidget", "m_playerCountChoice", "System.Int32", "PlaytestAccess");
            F("ToolbarWidget", "m_aiOnly", "System.Boolean", "PlaytestAccess");
            F("ToolbarWidget", "m_backgroundModLoadTimer", "System.Single", "PlaytestAccess");
            F("ToolbarWidget", "m_playerCount", "System.String[]", "PlaytestAccess");
            F("ModValidationSystem", "m_validators", "System.Collections.Generic.List`1[ModValidationCallbackReceiver]", "PlaytestAccess (validate_mod)", true);
            M("EditorSystem", "SaveCurrentMod", "Plugin save watch", "System.Boolean");
            P("WorkshopController", "CurrentBackgroundLoadMod", "PlaytestAccess", true);
            P("WorkshopController", "CurrentActiveMod", "PlaytestAccess", true);

            // playtest state and event feed
            F("MinigameController", "round", "System.Int32", "PlaytestStateReader");
            F("MinigameController", "round_begin_time", "System.Single", "PlaytestStateReader");
            F("SimpleModPlayer", "m_lastDeathTime", "System.Single", "PlaytestStateReader");
            F("SimpleModPlayer", "m_filoHeldItemStack", "System.Collections.Generic.List`1[ModItem]", "PlaytestStateReader");
            F("ModSystem.Logic.ModLogicTrigger", "m_owner", "ModSystem.Logic.IEventReceiver", "PlaytestLogicRecorder");
            M("ModSystem.Logic.ModLogicTrigger", "RunActionsLocal", "PlaytestHooks", "ModSystem.Logic.ModEvent");

            // mod metadata, assets, components
            F("PummelMod", "m_metaData", null, "EditorAccess, get_open_mod, write guard");
            F("ModItem", "m_itemPummelPrefab", null, "ComponentReader");
            F("ModItem", "m_itemPummelPrefabGuid", null, "ComponentReader");
            F("ModItem", "m_droppedItemPummelPrefab", null, "ComponentReader");
            F("ModItem", "m_droppedItemPummelPrefabGuid", null, "ComponentReader");
            F("ModPrefabInstance", "m_prefabReference", null, "ComponentReader");
            F("ModPrefabInstance", "m_prefabReferenceGuid", null, "ComponentReader");
            F("ModSpawner", "m_prefabGUIDs", null, "ComponentReader");
            F("ModMeshRenderer", "m_tempMats", null, "ComponentReader");
            F("ModLineRenderer", "tempMats", null, "ComponentReader");
            F("ModSkinnedMeshRenderer", "tempMats", null, "ComponentReader");
            F("ModParticleSystem", "m_particleSystemRenderer", null, "ComponentReader, set_property");
            M("ModMeshCollider", "HasLinkedRenderer", "inspector rules");
            F("Console", "entriesClean", null, "get_log (game console)", true);
            F("Console", "entryTypes", null, "get_log (game console)", true);
            F("Console", "stackTraces", null, "get_log (game console)", true);

            // actions
            F("ModSystem.Logic.ModAction", "m_version", "System.Int16", "ValueConverter");
            F("ModActionMeta", "m_uiName", null, "ActionModel");
            F("ModSystem.Logic.BoardPopupAction", "m_choiceCount", "System.Int32", "ActionModel");
            F("ModSystem.Logic.BoardPopupAction", "m_choices", null, "ActionModel");
            M("ModSystem.Logic.BoardPopupAction", "HasACostlessChoice", "ActionParams");
            M("ModSystem.Logic.TargetAction", "GetHideFlags", "ActionModel");
            M("ModSystem.Logic.TargetAction", "GetAcceptedTargetType", "ActionModel");
            F("ModSystem.Logic.SetPlayerVisualAction", "m_verticalLookTargetChildIndex", "System.Int32", "ActionParams");
            F("ModSystem.Logic.SetPlayerVisualAction", "m_movementRotationTargetChildIndex", "System.Int32", "ActionParams");
            foreach (string trigger in new[] { "weaponHitTrigger", "tickTrigger", "onDeathTrigger", "onRespawnTrigger", "onFirstSpawnTrigger", "onRoundEndTrigger" })
            {
                F("SimpleModPlayerSettings", trigger, null, "ActionModel (player-settings triggers)");
            }

            // inspector rules: every property a rule names, and every enum value its conditions and choices name
            foreach (PropertyRule r in InspectorRules.All)
            {
                l.Add(new RequiredMember { Kind = "ruleProperty", Type = r.Component, Name = r.Property, UsedBy = "inspector rules" });
                List<RuleRef> refs = new List<RuleRef>();
                r.Shown?.CollectRefs(refs);
                foreach (RuleRef rf in refs)
                {
                    l.Add(new RequiredMember { Kind = "ruleProperty", Type = r.Component, Name = rf.Property, UsedBy = "inspector rules (condition of " + r.Property + ")" });
                    foreach (string n in rf.Names ?? new string[0])
                    {
                        l.Add(new RequiredMember { Kind = "enumValue", Type = r.Component, Extra = rf.Property, Name = n, UsedBy = "inspector rules (condition of " + r.Property + ")" });
                    }
                }
                foreach (string n in (r.ExcludedValues ?? new string[0]).Concat(r.AllowedValues ?? new string[0]).Concat(r.AllowedFlags ?? new string[0]))
                {
                    l.Add(new RequiredMember { Kind = "enumValue", Type = r.Component, Extra = r.Property, Name = n, UsedBy = "inspector rules (choices of " + r.Property + ")" });
                }
            }
            foreach (KeyValuePair<string, string> kv in InspectorRules.AuditedProperties)
            {
                foreach (string name in kv.Value.Split(' '))
                {
                    l.Add(new RequiredMember { Kind = "ruleProperty", Type = kv.Key, Name = name, UsedBy = "inspector audit snapshot" });
                }
            }

            // distinct, order preserved
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            return l.Where(m => seen.Add(m.Kind + "|" + m.Id + "|" + m.IsStatic)).ToList();
        }

        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>Null if the member exists as registered, otherwise a short description of what is wrong.</summary>
        public static string Resolve(Assembly game, RequiredMember m)
        {
            Type t = game.GetType(m.Type, false);
            if (t == null)
            {
                return "type " + m.Type + " not found";
            }
            switch (m.Kind)
            {
                case "field":
                {
                    for (Type c = t; c != null; c = c.BaseType)
                    {
                        FieldInfo f = c.GetField(m.Name, Declared);
                        if (f != null)
                        {
                            if (m.Extra != null && f.FieldType.ToString() != m.Extra)
                            {
                                return $"field type is {f.FieldType}, expected {m.Extra}";
                            }
                            return f.IsStatic == m.IsStatic ? null : (m.IsStatic ? "field is not static" : "field is static");
                        }
                    }
                    return "field not found";
                }
                case "property":
                {
                    for (Type c = t; c != null; c = c.BaseType)
                    {
                        PropertyInfo p = c.GetProperty(m.Name, Declared);
                        if (p != null)
                        {
                            MethodInfo g = p.GetGetMethod(true);
                            return g != null && g.IsStatic == m.IsStatic ? null : "property static-ness differs";
                        }
                    }
                    return "property not found";
                }
                case "method":
                {
                    for (Type c = t; c != null; c = c.BaseType)
                    {
                        foreach (MethodInfo mi in c.GetMethods(Declared).Where(x => x.Name == m.Name))
                        {
                            string[] ps = mi.GetParameters().Select(x => x.ParameterType.ToString()).ToArray();
                            if (ps.SequenceEqual(m.Params ?? new string[0]))
                            {
                                return null;
                            }
                        }
                    }
                    return "method not found with parameters (" + string.Join(", ", m.Params ?? new string[0]) + ")";
                }
                case "ruleProperty":
                    return FindRuleProperty(t, m.Name) != null ? null : "property not found";
                case "enumValue":
                {
                    PropertyInfo p = FindRuleProperty(t, m.Extra);
                    if (p == null)
                    {
                        return "property " + m.Extra + " not found";
                    }
                    Type pt = p.PropertyType;
                    if (!pt.IsEnum)
                    {
                        // MinMaxCurve: its condition names are UnityEngine.ParticleSystemCurveMode values.
                        pt = pt.Name == "MinMaxCurve" ? pt.Assembly.GetType("UnityEngine.ParticleSystemCurveMode", false) : null;
                        if (pt == null)
                        {
                            return "property " + m.Extra + " is not an enum";
                        }
                    }
                    return pt.GetFields(BindingFlags.Public | BindingFlags.Static).Any(f => f.Name == m.Name) ? null : "enum " + pt.Name + " has no value " + m.Name;
                }
                default:
                    return "unknown member kind " + m.Kind;
            }
        }

        /// <summary>Public instance property as ModReflectionSystem collects them: declared on the type, else on ModBehaviour.</summary>
        private static PropertyInfo FindRuleProperty(Type t, string name)
        {
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            PropertyInfo p = t.GetProperty(name, f);
            if (p != null)
            {
                return p;
            }
            for (Type c = t.BaseType; c != null; c = c.BaseType)
            {
                if (c.Name == "ModBehaviour")
                {
                    return c.GetProperty(name, f);
                }
            }
            return null;
        }

        /// <summary>Problems of every registered member against the assembly (empty = all present).</summary>
        public static List<string> ResolveAll(Assembly game)
        {
            List<string> problems = new List<string>();
            foreach (RequiredMember m in s_all)
            {
                string why;
                try
                {
                    why = Resolve(game, m);
                }
                catch (Exception e)
                {
                    why = e.GetType().Name + ": " + e.Message;
                }
                if (why != null)
                {
                    problems.Add($"{m.Id}: {why} (used by {m.UsedBy})");
                }
            }
            return problems;
        }
    }
}
