using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;
using UnityEngine;

namespace PummelPartyMcp.Tools
{
    /// <summary>
    /// create_object, duplicate_object, delete_object, reparent_object, set_object, set_transform, undo.
    /// All write tools with guard kind OpenMod; bodies run on the main thread through the write queue.
    /// </summary>
    internal static class ObjectEditTools
    {
        private static readonly string[] s_transformProps = { "position", "rotation", "scale" };

        private sealed class CreateArgs
        {
            public ObjectKindInfo Kind;
            public string Name;
            public string ParentGuid;
            public float[] Position, Rotation, Scale;
            public string PropGuid;
            public string PrefabGuid;
        }

        private sealed class GuidArgs
        {
            public string Guid;
        }

        private sealed class ReparentArgs
        {
            public string Guid;
            public string NewParentGuid;
            public int? SiblingIndex;
        }

        private sealed class SetObjectArgs
        {
            public string Guid;
            public string Name;
            public bool? Active;
        }

        private sealed class SetTransformArgs
        {
            public string Guid;
            public float[][] Values = new float[3][]; // position, rotation, scale
        }

        public static void Register(ToolRegistry registry, EditServices services)
        {
            registry.Register(ToolDefinition.Write("create_object", WriteGuard.OpenMod(), ParseCreate, inv => Create(inv, services), EditCommon.EditTimeout));
            registry.Register(ToolDefinition.Write("duplicate_object", WriteGuard.OpenMod(),
                ctx => new GuidArgs { Guid = EditCommon.Guid(ctx, "guid", true) }, inv => Duplicate(inv, services), EditCommon.EditTimeout));
            registry.Register(ToolDefinition.Write("delete_object", WriteGuard.OpenMod(),
                ctx => new GuidArgs { Guid = EditCommon.Guid(ctx, "guid", true) }, inv => Delete(inv, services), EditCommon.EditTimeout));
            registry.Register(ToolDefinition.Write("reparent_object", WriteGuard.OpenMod(), ParseReparent, inv => Reparent(inv, services), EditCommon.EditTimeout));
            registry.Register(ToolDefinition.Write("set_object", WriteGuard.OpenMod(), ctx =>
            {
                SetObjectArgs a = new SetObjectArgs
                {
                    Guid = EditCommon.Guid(ctx, "guid", true),
                    Name = EditCommon.OptObjectName(ctx, "name"),
                    Active = EditCommon.OptBool(ctx, "active"),
                };
                if (a.Name == null && !a.Active.HasValue)
                {
                    throw RpcException.InvalidParams("give 'name' and/or 'active'");
                }
                return a;
            }, inv => SetObject(inv, services), EditCommon.EditTimeout));
            registry.Register(ToolDefinition.Write("set_transform", WriteGuard.OpenMod(), ctx =>
            {
                SetTransformArgs a = new SetTransformArgs { Guid = EditCommon.Guid(ctx, "guid", true) };
                for (int i = 0; i < 3; i++)
                {
                    a.Values[i] = EditCommon.OptVector3(ctx, s_transformProps[i]);
                }
                if (a.Values[0] == null && a.Values[1] == null && a.Values[2] == null)
                {
                    throw RpcException.InvalidParams("give at least one of 'position', 'rotation', 'scale'");
                }
                return a;
            }, inv => SetTransform(inv, services), EditCommon.EditTimeout));
            registry.Register(ToolDefinition.Write("undo", WriteGuard.OpenMod(),
                ctx => ctx.GetInt("steps", 1, 1, 20), inv => Undo(inv, services), EditCommon.EditTimeout));
        }

        // ------------------------------------------------------------------ create_object

        private static object ParseCreate(ToolContext ctx)
        {
            CreateArgs a = new CreateArgs
            {
                Name = EditCommon.OptObjectName(ctx, "name"),
                ParentGuid = EditCommon.Guid(ctx, "parentGuid", false),
                Position = EditCommon.OptVector3(ctx, "position"),
                Rotation = EditCommon.OptVector3(ctx, "rotation"),
                Scale = EditCommon.OptVector3(ctx, "scale"),
                PropGuid = EditCommon.Guid(ctx, "prop", false),
                PrefabGuid = EditCommon.Guid(ctx, "prefab", false),
            };
            string kind = ctx.GetString("kind") ?? (a.PropGuid != null ? "prop" : a.PrefabGuid != null ? "prefabInstance" : "empty");
            a.Kind = ObjectKinds.Find(kind) ?? throw RpcException.InvalidParams($"unknown kind '{kind}'; one of {ObjectKinds.KindList}");
            if (a.PropGuid != null && a.Kind.Kind != "prop")
            {
                throw RpcException.InvalidParams("'prop' is only allowed with kind \"prop\"");
            }
            if (a.PrefabGuid != null && a.Kind.Kind != "prefabInstance")
            {
                throw RpcException.InvalidParams("'prefab' is only allowed with kind \"prefabInstance\"");
            }
            return a;
        }

        private static void Create(WriteInvocation inv, EditServices services)
        {
            CreateArgs a = inv.ArgsAs<CreateArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            bool editingPrefab = ed.CurrentPrefab != null;

            Transform parent = a.ParentGuid == null ? EditContext.CreationRoot(ed) : EditContext.RequireEditable(ed, a.ParentGuid, "parent object").transform;
            if (parent == null)
            {
                throw new RpcException(ErrorCodes.InvalidState, "the editor has no creation root (prefab object missing?)");
            }
            string why = ObjectKinds.WhyUnavailable(a.Kind, mod.ModType.ToString(), mod.UsesHierarchy, editingPrefab, EditorAccess.HidesChildren(parent), parent.name);
            if (why != null)
            {
                throw RpcException.InvalidParams(why);
            }
            ModAsset dropAsset = null;
            if (a.PropGuid != null)
            {
                dropAsset = RequireAsset(ed, a.PropGuid, ModReflectionType.PROP, "prop", "a Prop asset (list_assets type Prop)");
            }
            else if (a.PrefabGuid != null)
            {
                dropAsset = RequireAsset(ed, a.PrefabGuid, ModReflectionType.PUMMELPREFAB, "prefab", "a Prefab asset of this mod (list_assets type Prefab)");
            }
            ObjectType objectType = (ObjectType)Enum.Parse(typeof(ObjectType), a.Kind.ObjectType);

            EditHost host = new EditHost(ed, null);
            EditCommon.Run(services, inv, host, selectFirst: false, apply: () =>
            {
                services.MarkEdit(mod, "create_object");
                GameObject go;
                if (dropAsset != null)
                {
                    // What dropping the asset on a hierarchy item does: AddGameObject(Prop/NewPrefabInstance) + assign the
                    // asset + name, then the drop end resets the preview layer.
                    go = dropAsset.OnSceneDragDropStart(parent);
                    if (go != null)
                    {
                        dropAsset.OnSceneDragDropEnd(go, delivered: true);
                    }
                }
                else
                {
                    go = ed.AddGameObject(objectType, parent); // creates, parents (local values kept) and selects it
                }
                if (go == null)
                {
                    throw RpcException.Internal("the editor did not create an object");
                }
                host.Target = go;
                ModTransform mt = go.GetComponent<ModTransform>();
                if (a.Name != null)
                {
                    go.name = a.Name;
                }
                ApplyTransform(mt, new[] { a.Position, a.Rotation, a.Scale });
                return new EditOutcome
                {
                    Changed = true,
                    Then = PostEditWait.AwaitBaseline,
                    BuildResult = report =>
                    {
                        JObject r = EditCommon.ObjectNode(ed, mod, mt);
                        r["kind"] = a.Kind.Kind;
                        r["selected"] = ed.GetSelectionManager().IsSelected(go);
                        r["undoable"] = false;
                        r["undoStackLength"] = EditCommon.Nullable(EditorAccess.UndoStackLength(ed));
                        if (report.BaselineTimedOut)
                        {
                            r["note"] = "the editor has not taken an undo snapshot of the new object yet (left mouse button held?)";
                        }
                        return r;
                    },
                };
            });
        }

        private static ModAsset RequireAsset(EditorSystem ed, string guid, ModReflectionType type, string param, string expected)
        {
            ModAsset asset = ed.GetAssetWithGuid(new SerializableGuid(System.Guid.Parse(guid)));
            if (asset == null)
            {
                throw RpcException.InvalidParams($"'{param}': no asset with guid {guid}");
            }
            if (asset.ReflectionType != type || (type == ModReflectionType.PUMMELPREFAB && !(asset is PummelPrefab)))
            {
                throw RpcException.InvalidParams($"'{param}': '{asset.name}' is a {ValueConverter.AssetTypeName(asset)}, expected {expected}");
            }
            if (!asset.enabled)
            {
                throw RpcException.InvalidParams($"'{param}': '{asset.name}' is missing/deleted");
            }
            return asset;
        }

        /// <summary>Sets ModTransform.position/rotation/scale through their setters (the inspector's path; they clamp).</summary>
        private static void ApplyTransform(ModTransform mt, float[][] values)
        {
            for (int i = 0; i < 3; i++)
            {
                if (values[i] == null)
                {
                    continue;
                }
                System.Reflection.PropertyInfo pi = EditCommon.Setter(typeof(ModTransform), s_transformProps[i])
                    ?? throw RpcException.Internal(GameReflection.MissingMessage(typeof(ModTransform), s_transformProps[i]));
                EditCommon.InvokeSetter(pi, mt, new Vector3(values[i][0], values[i][1], values[i][2]), "Transform." + s_transformProps[i]);
            }
        }

        // ------------------------------------------------------------------ duplicate_object

        private static void Duplicate(WriteInvocation inv, EditServices services)
        {
            string guid = inv.ArgsAs<GuidArgs>().Guid;
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            ModTransform source = EditContext.RequireEditable(ed, guid);
            EditHost host = new EditHost(ed, source.gameObject);
            EditCommon.Run(services, inv, host, selectFirst: true, apply: () =>
            {
                EditorUndo undo = UndoAccess.CurrentForWrite(ed);
                int before = undo.UndoStackLength();
                services.MarkEdit(mod, "duplicate_object");
                ed.Duplicate(); // Copy() + Paste(): clones next to the selected object, pushes DuplicateGameObject, selects the clones
                if (undo.UndoStackLength() != before + 1 || UndoAccess.TopEventType(undo) != "DuplicateGameObject")
                {
                    throw RpcException.Internal("the editor did not duplicate the object");
                }
                GameObject[] clones = ed.GetSelectionManager().SelectedObjects.ToArray();
                string collision = FindGuidCollision(ed, clones);
                if (collision != null)
                {
                    undo.Undo(); // our DuplicateGameObject is on top: removes the clones again
                    throw RpcException.Internal("the duplicate shares guids with existing objects (" + collision + "); it was undone. Please report this.");
                }
                return new EditOutcome
                {
                    Changed = true,
                    Then = PostEditWait.None,
                    BuildResult = report =>
                    {
                        JArray objects = new JArray();
                        foreach (GameObject c in clones)
                        {
                            ModTransform cmt = c != null ? c.GetComponent<ModTransform>() : null;
                            if (cmt != null)
                            {
                                objects.Add(EditCommon.ObjectNode(ed, mod, cmt));
                            }
                        }
                        // top-level guid = the first (root) duplicate, like create_object's reply
                        JObject r = new JObject { ["sourceGuid"] = guid, ["guid"] = objects.Count > 0 ? objects[0]["guid"] : null, ["objects"] = objects };
                        return EditCommon.WithEditFields(r, ed, report, true);
                    },
                };
            });
        }

        /// <summary>
        /// Every ModBehaviour guid in the clones must be non-empty and unique among all mod objects (scene, prefabs and
        /// soft-deleted objects that undo can restore). Returns a description of the first collision, or null.
        /// </summary>
        private static string FindGuidCollision(EditorSystem ed, GameObject[] clones)
        {
            HashSet<ModBehaviour> cloneBehaviours = new HashSet<ModBehaviour>();
            foreach (GameObject c in clones)
            {
                if (c != null)
                {
                    cloneBehaviours.UnionWith(c.GetComponentsInChildren<ModBehaviour>(true));
                }
            }
            Dictionary<string, ModBehaviour> others = new Dictionary<string, ModBehaviour>();
            List<Transform> roots = new List<Transform> { ed.m_hierarchyRoot, ed.m_prefabsRoot };
            if (EditorUndo.DeletedRoot != null)
            {
                roots.Add(EditorUndo.DeletedRoot.transform);
            }
            foreach (Transform root in roots)
            {
                if (root == null)
                {
                    continue;
                }
                foreach (ModBehaviour mb in root.GetComponentsInChildren<ModBehaviour>(true))
                {
                    if (!cloneBehaviours.Contains(mb))
                    {
                        others[mb.guid.ToString()] = mb;
                    }
                }
            }
            HashSet<string> seen = new HashSet<string>();
            string empty = SerializableGuid.Empty.ToString();
            foreach (ModBehaviour mb in cloneBehaviours)
            {
                string g = mb.guid.ToString();
                if (g == empty)
                {
                    return $"{mb.GetType().Name} on '{mb.name}' has an empty guid";
                }
                if (others.ContainsKey(g) || !seen.Add(g))
                {
                    return $"{mb.GetType().Name} on '{mb.name}' reuses guid {g}";
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ delete_object

        private static void Delete(WriteInvocation inv, EditServices services)
        {
            string guid = inv.ArgsAs<GuidArgs>().Guid;
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            ModTransform target = EditContext.RequireEditable(ed, guid);
            if (!ed.Deleteable(target.gameObject))
            {
                throw RpcException.InvalidParams($"'{target.name}' is the prefab's root object and cannot be deleted");
            }
            string name = target.name;
            EditHost host = new EditHost(ed, target.gameObject);
            EditCommon.Run(services, inv, host, selectFirst: true, apply: () =>
            {
                int count = CountObjects(target.transform);
                EditorUndo undo = UndoAccess.CurrentForWrite(ed);
                int before = undo.UndoStackLength();
                services.MarkEdit(mod, "delete_object");
                ed.Delete(); // soft delete of the selection under EditorUndo's inactive "UndoDeletedObjects" root
                if (undo.UndoStackLength() != before + 1 || UndoAccess.TopEventType(undo) != "DeleteGameObject")
                {
                    throw RpcException.Internal("the editor did not delete the object");
                }
                host.Target = null;
                return new EditOutcome
                {
                    Changed = true,
                    Then = PostEditWait.None,
                    BuildResult = report => EditCommon.WithEditFields(new JObject { ["guid"] = guid, ["name"] = name, ["deleted"] = count }, ed, report, true),
                };
            });
        }

        /// <summary>The object plus its editor-visible descendants (children of hides-children objects are not counted).</summary>
        private static int CountObjects(Transform t)
        {
            int n = t.GetComponent<ModTransform>() != null ? 1 : 0;
            if (EditorAccess.HidesChildren(t))
            {
                return n;
            }
            for (int i = 0; i < t.childCount; i++)
            {
                n += CountObjects(t.GetChild(i));
            }
            return n;
        }

        // ------------------------------------------------------------------ reparent_object

        private static object ParseReparent(ToolContext ctx)
        {
            if (!ctx.Params.ContainsKey("newParentGuid"))
            {
                throw RpcException.InvalidParams("'newParentGuid' is required (null = top level of the scene / prefab)");
            }
            long? index = ctx.GetLong("siblingIndex", 0, 100000);
            return new ReparentArgs
            {
                Guid = EditCommon.Guid(ctx, "guid", true),
                NewParentGuid = EditCommon.Guid(ctx, "newParentGuid", false),
                SiblingIndex = index.HasValue ? (int?)index.Value : null,
            };
        }

        private static void Reparent(WriteInvocation inv, EditServices services)
        {
            ReparentArgs a = inv.ArgsAs<ReparentArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            ModTransform target = EditContext.RequireEditable(ed, a.Guid);
            Transform newParent = a.NewParentGuid == null ? EditContext.CreationRoot(ed) : EditContext.RequireEditable(ed, a.NewParentGuid, "parent object").transform;
            string why = ReparentRules.Check(target.transform, newParent, t => t.parent, EditorAccess.HidesChildren,
                t => EditContext.IsPrefabRootObject(ed, t), t => EditContext.ContextOf(ed, t), t => t.name);
            if (why != null)
            {
                throw RpcException.InvalidParams(why);
            }
            bool sameParent = target.transform.parent == newParent;
            int maxIndex = newParent.childCount - (sameParent ? 1 : 0);
            if (a.SiblingIndex.HasValue && a.SiblingIndex.Value > maxIndex)
            {
                throw RpcException.InvalidParams($"'siblingIndex' must be between 0 and {maxIndex} for this parent");
            }
            EditHost host = new EditHost(ed, target.gameObject);
            EditCommon.Run(services, inv, host, selectFirst: true, apply: () =>
            {
                Transform t = target.transform;
                bool noop = t.parent == newParent && (!a.SiblingIndex.HasValue || a.SiblingIndex.Value == t.GetSiblingIndex());
                if (!noop)
                {
                    // HierarchyWidget drag and drop: push MoveGameObject BEFORE moving, move (world pose kept), re-baseline.
                    services.MarkEdit(mod, "reparent_object");
                    EditorUndo undo = UndoAccess.CurrentForWrite(ed);
                    undo.PushUndoEventToStack(new MoveGameObject(new[] { target.gameObject }));
                    if (t.parent != newParent)
                    {
                        target.SetParent(newParent); // worldPositionStays: true (default), as the widget does
                    }
                    if (a.SiblingIndex.HasValue)
                    {
                        t.SetAsLastSibling();
                        t.SetSiblingIndex(a.SiblingIndex.Value);
                    }
                    undo.lastGameobjectUndoEvent = undo.GetCurUndoEvent();
                }
                return new EditOutcome
                {
                    Changed = !noop,
                    Then = PostEditWait.None,
                    BuildResult = report => EditCommon.WithEditFields(new JObject
                    {
                        ["guid"] = a.Guid,
                        ["parentGuid"] = EditCommon.ParentGuid(ed, t),
                        ["siblingIndex"] = t.GetSiblingIndex(),
                        ["transform"] = EditCommon.TransformJson(mod, t),
                    }, ed, report, report.Changed),
                };
            });
        }

        // ------------------------------------------------------------------ set_object

        private static void SetObject(WriteInvocation inv, EditServices services)
        {
            SetObjectArgs a = inv.ArgsAs<SetObjectArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            ModTransform target = EditContext.RequireEditable(ed, a.Guid);
            EditHost host = new EditHost(ed, target.gameObject);
            EditCommon.Run(services, inv, host, selectFirst: true, apply: () =>
            {
                GameObject go = target.gameObject;
                services.Observe();
                // InspectorWidget: name field writes GameObject.name, the checkbox calls SetActive (for the selection).
                if (a.Name != null)
                {
                    go.name = a.Name;
                }
                if (a.Active.HasValue)
                {
                    go.SetActive(a.Active.Value);
                }
                bool changed = EditCommon.SnapshotChanged(host);
                services.MarkEditIfChanged(mod, "set_object", changed);
                return new EditOutcome
                {
                    Changed = changed,
                    Then = PostEditWait.AwaitPush,
                    BuildResult = report => EditCommon.WithEditFields(new JObject
                    {
                        ["guid"] = a.Guid,
                        ["name"] = go.name,
                        ["active"] = go.activeSelf,
                        ["activeInHierarchy"] = go.activeInHierarchy,
                    }, ed, report, EditCommon.Undoable(report)),
                };
            });
        }

        // ------------------------------------------------------------------ set_transform

        private static void SetTransform(WriteInvocation inv, EditServices services)
        {
            SetTransformArgs a = inv.ArgsAs<SetTransformArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            ModTransform target = EditContext.RequireEditable(ed, a.Guid);
            EditHost host = new EditHost(ed, target.gameObject);
            EditCommon.Run(services, inv, host, selectFirst: true, apply: () =>
            {
                services.Observe();
                ApplyTransform(target, a.Values);
                bool changed = EditCommon.SnapshotChanged(host);
                services.MarkEditIfChanged(mod, "set_transform", changed);
                return new EditOutcome
                {
                    Changed = changed,
                    Then = PostEditWait.AwaitPush,
                    BuildResult = report =>
                    {
                        ValueConverter conv = new ValueConverter(mod, summarizeTriggers: true);
                        Vector3[] applied = { target.position, target.rotation, target.scale };
                        JObject appliedJson = new JObject();
                        JArray clamped = new JArray();
                        for (int i = 0; i < 3; i++)
                        {
                            appliedJson[s_transformProps[i]] = conv.Convert(applied[i]);
                            if (a.Values[i] != null && !Close(applied[i], a.Values[i]))
                            {
                                clamped.Add(s_transformProps[i]);
                            }
                        }
                        return EditCommon.WithEditFields(new JObject
                        {
                            ["guid"] = a.Guid,
                            ["applied"] = appliedJson,
                            ["clamped"] = clamped,
                            ["transform"] = EditCommon.TransformJson(mod, target.transform),
                        }, ed, report, EditCommon.Undoable(report));
                    },
                };
            });
        }

        private static bool Close(Vector3 v, float[] requested)
        {
            for (int i = 0; i < 3; i++)
            {
                float r = requested[i];
                if (Math.Abs(v[i] - r) > 1e-4f * Math.Max(1f, Math.Abs(r)))
                {
                    return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ undo

        /// <summary>
        /// Ctrl+Z in EditorSystem.Update is GetUndoManager().Undo(), once per key press (frame). The tool does the same,
        /// one step per frame, then waits one more frame and reports the stack length (the editor may push an extra
        /// snapshot after undoing a component add/remove, because Undo re-baselines before Unity destroys components).
        /// </summary>
        /// <summary>Time left until the deadline that undo needs to start (one step per frame, up to 20, plus two frames).</summary>
        private static readonly TimeSpan UndoStartBudget = TimeSpan.FromSeconds(3);

        /// <summary>After the first step, no further step is started with less than this left (the reply must still go out).</summary>
        private static readonly TimeSpan UndoStopMargin = TimeSpan.FromSeconds(1);

        private static void Undo(WriteInvocation inv, EditServices services)
        {
            int steps = (int)inv.Args;
            EditorSystem ed = EditorAccess.RequireEditor();
            EditContext.RequireEditReady(ed);
            PummelMod mod = ed.CurrentMod;
            EditHost host = new EditHost(ed, null);
            int requested = steps;
            int undone = 0;
            int lengthAfterLast = -1;
            int waitUntilFrame = -1;
            bool committed = false;
            bool outOfTime = false;
            JArray events = new JArray();

            bool Step()
            {
                if (!committed && inv.Completion.IsCancelled)
                {
                    inv.Fail(new RpcException(ErrorCodes.Timeout, "the call timed out before anything was undone; nothing was changed (safe to retry)"));
                    return true;
                }
                RpcException invalid = host.Validate();
                if (invalid != null)
                {
                    inv.Fail(undone == 0 ? invalid : new RpcException(invalid.Code, $"{undone} step(s) were undone, then: {invalid.Message}"));
                    return true;
                }
                EditorUndo undo = UndoAccess.CurrentForWrite(ed);
                if (waitUntilFrame < 0)
                {
                    if (committed && undone < steps && inv.Completion.Remaining < UndoStopMargin)
                    {
                        // Past the commit point the call always returns its result: stop early and report what was done.
                        outOfTime = true;
                        steps = undone;
                    }
                    if (undone < steps && undo.UndoStackLength() > 0)
                    {
                        if (!committed)
                        {
                            inv.Commit(UndoStartBudget); // throws TIMEOUT (nothing undone) when cancelled or too late
                            committed = true;
                        }
                        string type = UndoAccess.TopEventType(undo);
                        int before = undo.UndoStackLength();
                        services.MarkEdit(mod, "undo");
                        undo.Undo();
                        if (undo.UndoStackLength() < before)
                        {
                            undone++;
                            events.Add(type);
                        }
                        else
                        {
                            steps = undone; // nothing popped: stop
                        }
                        if (undone < steps && undo.UndoStackLength() > 0)
                        {
                            return false; // next step next frame, like repeated Ctrl+Z
                        }
                    }
                    lengthAfterLast = undo.UndoStackLength();
                    waitUntilFrame = Time.frameCount + 2;
                    return false;
                }
                if (Time.frameCount < waitUntilFrame && undone > 0)
                {
                    return false;
                }
                int finalLength = undo.UndoStackLength();
                JObject r = new JObject
                {
                    ["undone"] = undone,
                    ["events"] = events,
                    ["undoStackLength"] = finalLength,
                };
                if (undone > 0 && finalLength > lengthAfterLast)
                {
                    r["extraUndoEntries"] = finalLength - lengthAfterLast;
                    r["note"] = "the editor pushed a new undo snapshot after the undo (it re-baselines before Unity destroys removed components); this is editor behaviour";
                }
                if (undone == 0)
                {
                    r["note"] = "nothing to undo";
                }
                if (outOfTime)
                {
                    string stopped = $"stopped after {undone} of {requested} steps because the call's time limit was reached; call undo again for the rest";
                    r["note"] = r["note"] != null ? (string)r["note"] + "; " + stopped : stopped;
                }
                inv.Complete(r);
                return true;
            }

            if (!Step())
            {
                services.Poller.Add(() =>
                {
                    try
                    {
                        return Step();
                    }
                    catch (Exception e)
                    {
                        inv.Fail(e);
                        return true;
                    }
                });
            }
        }
    }
}
