using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using PummelBridge.Game;
using UnityEngine;

namespace PummelBridge.Tools
{
    /// <summary>list_component_types (read); add_component, remove_component, set_property, set_properties (write, OpenMod).</summary>
    internal static class ComponentTools
    {
        public const int MaxPropertiesPerCall = 64;

        private sealed class AddArgs
        {
            public string Guid;
            public string Type;
        }

        private sealed class ComponentArgs
        {
            public string Guid;
            public string Component;
        }

        private sealed class SetArgs
        {
            public string Guid;
            public string Component;
            public List<KeyValuePair<string, JToken>> Values = new List<KeyValuePair<string, JToken>>();
        }

        private sealed class PreparedValue
        {
            public string Name;
            public PropertyInfo Setter;
            public object Value;
            public bool InspectorClamped;
        }

        public static void Register(ToolRegistry registry, EditServices services)
        {
            registry.Register(ToolDefinition.Read("list_component_types", ctx =>
            {
                string guid = EditCommon.Guid(ctx, "guid", false);
                return ctx.OnMainThread(() => ListTypes(guid));
            }));

            registry.Register(ToolDefinition.Write("add_component", WriteGuard.OpenMod(),
                ctx => new AddArgs { Guid = EditCommon.Guid(ctx, "guid", true), Type = ctx.GetString("type", required: true) },
                inv => Add(inv, services), EditCommon.EditTimeout));

            registry.Register(ToolDefinition.Write("remove_component", WriteGuard.OpenMod(),
                ctx => new ComponentArgs { Guid = EditCommon.Guid(ctx, "guid", true), Component = ctx.GetString("component", required: true) },
                inv => Remove(inv, services), EditCommon.EditTimeout));

            registry.Register(ToolDefinition.Write("set_property", WriteGuard.OpenMod(), ctx =>
            {
                SetArgs a = new SetArgs { Guid = EditCommon.Guid(ctx, "guid", true), Component = ctx.GetString("component", required: true) };
                string property = ctx.GetString("property", required: true);
                if (!ctx.Params.ContainsKey("value"))
                {
                    throw RpcException.InvalidParams("'value' is required (use null to clear a reference)");
                }
                a.Values.Add(new KeyValuePair<string, JToken>(property, ctx.Params["value"]));
                return a;
            }, inv => Set(inv, services), EditCommon.EditTimeout));

            registry.Register(ToolDefinition.Write("set_properties", WriteGuard.OpenMod(), ctx =>
            {
                SetArgs a = new SetArgs { Guid = EditCommon.Guid(ctx, "guid", true), Component = ctx.GetString("component", required: true) };
                if (!(ctx.Params["values"] is JObject values) || values.Count == 0)
                {
                    throw RpcException.InvalidParams("'values' must be a non-empty object {propertyName: value, ...}");
                }
                if (values.Count > MaxPropertiesPerCall)
                {
                    throw RpcException.InvalidParams($"at most {MaxPropertiesPerCall} properties per call");
                }
                foreach (JProperty p in values.Properties())
                {
                    a.Values.Add(new KeyValuePair<string, JToken>(p.Name, p.Value));
                }
                return a;
            }, inv => Set(inv, services), EditCommon.EditTimeout));
        }

        // ------------------------------------------------------------------ registry facts

        internal static ComponentTypeInfo Info(ModReflectedType t) => new ComponentTypeInfo
        {
            Name = t.name,
            FullName = t.systemType.ToString(),
            UiName = t.uiName,
            Folder = t.folder,
            BaseTypeFullName = t.systemType.BaseType?.ToString(),
            Addable = t.addable,
            ModTypeFlags = (int)t.modTypeFlags,
            SupportsChildren = t.supportsChildren,
            DisallowMultiple = t.disallowMultipleComponent,
            BaseClassDisallowMultiple = t.baseClassDisallowMultipleComponents,
        };

        /// <summary>What SetupComponentAddabilityCheck([go]) collects, without touching its static sets.</summary>
        internal static ObjectFacts Facts(GameObject go)
        {
            ObjectFacts f = new ObjectFacts { HasChildren = go.transform.childCount > 0, IsPrefabInstance = go.GetComponent<ModPrefabInstance>() != null };
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    continue; // missing script
                }
                Type t = c.GetType();
                ModReflectedType rt = ModReflectionSystem.GetType(t);
                f.Components.Add(new ComponentFact
                {
                    FullName = t.ToString(),
                    BaseTypeFullName = t.BaseType?.ToString(),
                    Reflected = rt != null ? Info(rt) : null,
                });
            }
            return f;
        }

        private static ModReflectedType FindType(string query)
        {
            ModReflectedType byUi = null;
            int uiMatches = 0;
            for (int i = 0; i < ModReflectionSystem.TypeCount; i++)
            {
                ModReflectedType t = ModReflectionSystem.GetType(i);
                if (string.Equals(t.name, query, StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
                if (string.Equals(t.uiName, query, StringComparison.OrdinalIgnoreCase))
                {
                    byUi = t;
                    uiMatches++;
                }
            }
            return uiMatches == 1 ? byUi : null;
        }

        /// <summary>Component on the object by component guid, class name or UI name (unique).</summary>
        private static ModBehaviour FindComponent(GameObject go, string query)
        {
            List<ModBehaviour> comps = EditorAccess.ModComponents(go);
            if (Guid.TryParse(query, out Guid g))
            {
                string key = g.ToString();
                ModBehaviour byGuid = comps.FirstOrDefault(c => EditorAccess.GuidOf(c) == key);
                if (byGuid == null)
                {
                    throw RpcException.NotFound($"'{go.name}' has no component with guid {key}");
                }
                return byGuid;
            }
            List<ModBehaviour> matches = comps.Where(c =>
            {
                ModReflectedType rt = ModReflectionSystem.GetType(c.GetType());
                return string.Equals(rt.name, query, StringComparison.OrdinalIgnoreCase) || string.Equals(rt.uiName, query, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            if (matches.Count == 0)
            {
                throw RpcException.NotFound($"'{go.name}' has no {query} component");
            }
            if (matches.Count > 1)
            {
                throw RpcException.InvalidParams($"'{go.name}' has {matches.Count} {query} components; pass the component guid instead ({string.Join(", ", matches.Select(EditorAccess.GuidOf).ToArray())})");
            }
            return matches[0];
        }

        // ------------------------------------------------------------------ list_component_types

        private static JArray ListTypes(string guid)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            bool editingPrefab = ed.CurrentPrefab != null;
            string modType = mod.ModType.ToString();
            ObjectFacts facts = null;
            string objectProblem = null;
            if (guid != null)
            {
                ModTransform mt = EditorAccess.FindByGuid(ed, guid, out _);
                if (mt == null)
                {
                    throw RpcException.NotFound($"no object with guid {guid}");
                }
                facts = Facts(mt.gameObject);
                if (!EditContext.IsEditable(ed, mt.transform))
                {
                    objectProblem = $"'{mt.name}' is not in the current editing context";
                }
            }
            JArray rows = new JArray();
            for (int i = 0; i < ModReflectionSystem.TypeCount; i++)
            {
                ModReflectedType t = ModReflectionSystem.GetType(i);
                ComponentTypeInfo info = Info(t);
                string why = ComponentRules.WhyNotAddable(info, facts, modType, editingPrefab) ?? objectProblem;
                JObject row = new JObject
                {
                    ["type"] = t.name,
                    ["uiName"] = t.uiName,
                    ["category"] = t.folder,
                    ["addable"] = why == null,
                };
                if (why != null)
                {
                    row["reason"] = why;
                }
                row["allowsMultiple"] = !t.disallowMultipleComponent;
                row["supportsChildren"] = t.supportsChildren;
                row["modTypes"] = new JArray(ComponentRules.ModTypeNames((int)t.modTypeFlags));
                rows.Add(row);
            }
            return rows;
        }

        // ------------------------------------------------------------------ add_component

        private static void Add(WriteInvocation inv, EditServices services)
        {
            AddArgs a = inv.ArgsAs<AddArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            ModTransform target = EditContext.RequireEditable(ed, a.Guid);
            ModReflectedType rt = FindType(a.Type)
                ?? throw RpcException.InvalidParams($"unknown component type '{a.Type}' (class name like ModLight or UI name like Light; see list_component_types)");
            ComponentTypeInfo info = Info(rt);
            string modType = mod.ModType.ToString();
            bool editingPrefab = ed.CurrentPrefab != null;
            string why = ComponentRules.WhyNotAddable(info, Facts(target.gameObject), modType, editingPrefab);
            if (why != null)
            {
                throw RpcException.InvalidParams(why);
            }
            EditHost host = new EditHost(ed, target.gameObject);
            EditCommon.Run(services, inv, host, selectFirst: true, apply: () =>
            {
                GameObject go = target.gameObject;
                string again = ComponentRules.WhyNotAddable(info, Facts(go), modType, editingPrefab);
                if (again != null)
                {
                    throw RpcException.InvalidParams(again); // state changed while waiting; nothing was changed
                }
                // Cross-check with the editor's own rule code. Its scratch sets are static and shared with the Add Component
                // popup / component context menu, so only while no editor dialog is open.
                EditorPopup popup = EditorPopupManager.CurrentPopup;
                if (popup != null)
                {
                    throw new RpcException(ErrorCodes.InvalidState, $"an editor dialog was opened ('{popup.GetTitle()}'); nothing was changed");
                }
                ModEditorComponentAdderRemover.SetupComponentAddabilityCheck(new List<GameObject> { go });
                (bool blocked, string reason) = ModEditorComponentAdderRemover.PerformChecksForComponentAddability(rt);
                if (blocked)
                {
                    services.Log.Warning($"add_component: the editor's rules block {rt.name} on '{go.name}' but the bridge's replica did not: {reason}");
                    throw RpcException.InvalidParams(string.IsNullOrEmpty(reason) ? $"{rt.uiName} cannot be added to '{go.name}'" : reason);
                }
                services.MarkEdit(mod, "add_component");
                Component added = ModEditorComponentAdderRemover.AddComponentToGameObject(go, rt.systemType); // what the Add Component popup calls
                if (added == null || !(added is ModBehaviour mb))
                {
                    throw RpcException.Internal($"Unity did not add {rt.name} to '{go.name}' (see get_log)");
                }
                return new EditOutcome
                {
                    Changed = true,
                    Then = PostEditWait.AwaitPush,
                    BuildResult = report =>
                    {
                        JObject r = new JObject
                        {
                            ["guid"] = a.Guid,
                            ["component"] = mb != null ? ComponentReader.Read(mb, rt, new ValueConverter(mod, summarizeTriggers: true)) : null,
                        };
                        return EditCommon.WithEditFields(r, ed, report, EditCommon.Undoable(report));
                    },
                };
            });
        }

        // ------------------------------------------------------------------ remove_component

        private static void Remove(WriteInvocation inv, EditServices services)
        {
            ComponentArgs a = inv.ArgsAs<ComponentArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            ModTransform target = EditContext.RequireEditable(ed, a.Guid);
            ModBehaviour comp = FindComponent(target.gameObject, a.Component);
            ModReflectedType rt = ModReflectionSystem.GetType(comp.GetType());
            string why = ComponentRules.WhyNotRemovable(Info(rt), target.GetComponent<ModPrefabInstance>() != null);
            if (why != null)
            {
                throw RpcException.InvalidParams(why);
            }
            JObject removed = new JObject { ["type"] = rt.name, ["uiName"] = rt.uiName, ["guid"] = EditorAccess.GuidOf(comp) };
            EditHost host = new EditHost(ed, target.gameObject);
            EditCommon.Run(services, inv, host, selectFirst: true, apply: () =>
            {
                if (comp == null)
                {
                    throw RpcException.NotFound("the component was removed meanwhile");
                }
                int children = rt.supportsChildren ? 0 : target.transform.childCount;
                services.MarkEdit(mod, "remove_component");
                // Inspector close button: destroys generated children of !supportsChildren types, then the component.
                ModEditorComponentAdderRemover.RemoveComponentFromGameObject(comp);
                return new EditOutcome
                {
                    Changed = true, // Object.Destroy takes effect at the end of the frame; the editor's next snapshot sees it
                    Then = PostEditWait.AwaitPush,
                    BuildResult = report =>
                    {
                        JObject r = new JObject { ["guid"] = a.Guid, ["removed"] = removed, ["childrenDestroyed"] = children };
                        if (children > 0)
                        {
                            r["childrenNote"] = "generated child objects of this component type were destroyed; undo restores the component but not those children (they are regenerated where the component does that itself, e.g. Prefab Instance)";
                        }
                        return EditCommon.WithEditFields(r, ed, report, EditCommon.Undoable(report));
                    },
                };
            });
        }

        // ------------------------------------------------------------------ set_property / set_properties

        private static ReflectedProperty FindProperty(ModReflectedType rt, string name)
        {
            if (rt.propertyMap.TryGetValue(name, out ReflectedProperty exact))
            {
                return exact;
            }
            ReflectedProperty[] ci = (rt.properties ?? new ReflectedProperty[0]).Where(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            return ci.Length == 1 ? ci[0] : null;
        }

        private static void Set(WriteInvocation inv, EditServices services)
        {
            SetArgs a = inv.ArgsAs<SetArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            ModTransform target = EditContext.RequireEditable(ed, a.Guid);
            ModBehaviour comp = FindComponent(target.gameObject, a.Component);
            ModReflectedType rt = ModReflectionSystem.GetType(comp.GetType());
            if (target.GetComponent<ModPrefabInstance>() != null && !(comp is ModTransform) && !(comp is ModPrefabInstance))
            {
                throw RpcException.InvalidParams($"the inspector shows only Transform and Prefab Instance on a prefab instance; edit the prefab to change {rt.uiName}");
            }

            // Validate and build every value before anything is selected or changed (all-or-nothing).
            ReferenceResolver refs = new ReferenceResolver(ed, comp);
            List<PreparedValue> prepared = new List<PreparedValue>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JToken> kv in a.Values)
            {
                ReflectedProperty p = FindProperty(rt, kv.Key)
                    ?? throw RpcException.InvalidParams($"{rt.name} has no property '{kv.Key}' (see get_object for the property names)");
                if (!seen.Add(p.name))
                {
                    throw RpcException.InvalidParams($"property '{p.name}' is given twice");
                }
                string refusal = Refusal(rt, p);
                if (refusal != null)
                {
                    throw RpcException.InvalidParams($"'{p.name}': {refusal}");
                }
                ValueTypeDesc desc = ValueBuilder.Describe(p.systemType);
                PValue parsed = ValueParser.Parse(kv.Value, desc, p.name);
                bool clamped = InspectorClamps.Apply(rt.name, p.name, parsed);
                object value = ValueBuilder.Build(parsed, p.systemType, refs, p.name);
                PropertyInfo setter = EditCommon.Setter(comp.GetType(), p.name)
                    ?? throw RpcException.InvalidParams($"'{p.name}' has no public setter");
                prepared.Add(new PreparedValue { Name = p.name, Setter = setter, Value = value, InspectorClamped = clamped });
            }

            EditHost host = new EditHost(ed, target.gameObject);
            EditCommon.Run(services, inv, host, selectFirst: true, apply: () =>
            {
                if (comp == null)
                {
                    throw RpcException.NotFound("the component was removed meanwhile");
                }
                if (comp is ModParticleSystem)
                {
                    foreach (PreparedValue pv in prepared)
                    {
                        if (ComponentReader.ParticleSetterNeedsRenderer(pv.Name) && GameReflection.GetField(comp, "m_particleSystemRenderer") == null)
                        {
                            throw new RpcException(ErrorCodes.InvalidState, $"'{pv.Name}': the particle system renderer is not initialised yet; nothing was changed");
                        }
                    }
                }
                services.Observe();
                List<string> done = new List<string>();
                foreach (PreparedValue pv in prepared)
                {
                    try
                    {
                        EditCommon.InvokeSetter(pv.Setter, comp, pv.Value, rt.name + "." + pv.Name);
                    }
                    catch (RpcException e)
                    {
                        services.MarkEditIfChanged(mod, "set_property", done.Count > 0); // earlier setters of this call took effect
                        throw new RpcException(e.Code, done.Count == 0 ? e.Message
                            : $"{e.Message}; already set in this call: {string.Join(", ", done.ToArray())} (one undo reverts them)");
                    }
                    done.Add(pv.Name);
                }
                bool changed = EditCommon.SnapshotChanged(host);
                services.MarkEditIfChanged(mod, "set_property", changed);
                return new EditOutcome
                {
                    Changed = changed,
                    Then = PostEditWait.AwaitPush,
                    BuildResult = report =>
                    {
                        JObject props = comp != null
                            ? (JObject)ComponentReader.Read(comp, rt, new ValueConverter(mod, summarizeTriggers: true))["properties"]
                            : new JObject();
                        JObject applied = new JObject();
                        JArray clampedNames = new JArray();
                        foreach (PreparedValue pv in prepared)
                        {
                            applied[pv.Name] = props[pv.Name];
                            if (pv.InspectorClamped)
                            {
                                clampedNames.Add(pv.Name);
                            }
                        }
                        JObject r = new JObject
                        {
                            ["guid"] = a.Guid,
                            ["component"] = new JObject { ["type"] = rt.name, ["guid"] = EditorAccess.GuidOf(comp) },
                            ["applied"] = applied,
                            ["inspectorClamped"] = clampedNames,
                        };
                        return EditCommon.WithEditFields(r, ed, report, EditCommon.Undoable(report));
                    },
                };
            });
        }

        /// <summary>Property-level refusals (beyond the value type check). Null = writable.</summary>
        private static string Refusal(ModReflectedType rt, ReflectedProperty p)
        {
            if (p.name == "guid")
            {
                return "the guid is assigned by the editor";
            }
            if (p.hideInInspector)
            {
                return "the property is hidden in the inspector ([ModHideInInspector]); the editor does not let users edit it";
            }
            if (p.reflectionType == ModReflectionType.MODLOGICTRIGGER || p.reflectionType == ModReflectionType.MODACTION)
            {
                return ValueBuilder.TriggerMessage;
            }
            if (rt.name == "ModScriptReference" && p.name == "scriptValues")
            {
                return "script values are binary data; they cannot be set by the bridge";
            }
            return ValueBuilder.Describe(p.systemType).WhyUnsupported();
        }
    }
}
