using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using PummelBridge.Game;
using UnityEngine;

namespace PummelBridge.Tools
{
    /// <summary>get_hierarchy.</summary>
    internal static class HierarchyTools
    {
        public const int MaxNodes = 10000;
        public const int MaxDepthLimit = 64;

        public static void Register(ToolRegistry registry)
        {
            registry.Register(ToolDefinition.Read("get_hierarchy", ctx =>
            {
                string rootGuid = ctx.GetString("rootGuid");
                if (rootGuid != null)
                {
                    rootGuid = EditorAccess.NormalizeGuid(rootGuid, "rootGuid");
                }
                int maxDepth = ctx.GetInt("maxDepth", MaxDepthLimit, 0, MaxDepthLimit);
                bool includeInactive = ctx.GetBool("includeInactive", true);
                return ctx.OnMainThread(() => GetHierarchy(rootGuid, maxDepth, includeInactive));
            }));
        }

        private sealed class WalkState
        {
            public int Nodes;
            public bool Truncated;
            public bool IncludeInactive;
            public int MaxDepth;
        }

        private static JObject GetHierarchy(string rootGuid, int maxDepth, bool includeInactive)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            EditorAccess.RequireLoadedMod(ed);
            WalkState state = new WalkState { IncludeInactive = includeInactive, MaxDepth = maxDepth };

            if (rootGuid != null)
            {
                ModTransform mt = EditorAccess.FindByGuid(ed, rootGuid, out _);
                if (mt == null)
                {
                    throw RpcException.NotFound($"no object with guid {rootGuid}");
                }
                JObject node = Node(mt.transform, mt, 0, state);
                node["truncated"] = state.Truncated;
                node["nodeCount"] = state.Nodes;
                return node;
            }

            // Synthetic root: the editor's context root (not a mod object itself).
            Transform root = EditorAccess.ContextRoot(ed);
            if (root == null)
            {
                throw RpcException.Internal("editor hierarchy root is missing");
            }
            PummelPrefab prefab = ed.CurrentPrefab;
            JArray children = Children(root, 0, state, out int count);
            JObject result = new JObject
            {
                ["guid"] = null,
                ["name"] = root.name,
                ["context"] = prefab == null ? "scene" : "prefab",
                ["prefab"] = prefab == null ? null : new JObject { ["name"] = prefab.name, ["guid"] = prefab.guid.ToString() },
                ["active"] = root.gameObject.activeSelf,
                ["components"] = new JArray(),
                ["childCount"] = count,
                ["children"] = children,
                ["truncated"] = state.Truncated,
                ["nodeCount"] = state.Nodes,
            };
            return result;
        }

        /// <summary>A get_hierarchy node (with its whole subtree up to <paramref name="maxDepth"/>) for one object. Main thread.</summary>
        internal static JObject NodeFor(ModTransform mt, int maxDepth = MaxDepthLimit)
        {
            WalkState state = new WalkState { IncludeInactive = true, MaxDepth = maxDepth };
            return Node(mt.transform, mt, 0, state);
        }

        private static JObject Node(Transform t, ModTransform mt, int depth, WalkState state)
        {
            state.Nodes++;
            JArray components = new JArray();
            foreach (ModBehaviour mb in EditorAccess.ModComponents(t.gameObject))
            {
                if (mb is ModTransform)
                {
                    continue; // every mod object has exactly one
                }
                components.Add(ModReflectionSystem.GetType(mb.GetType())?.name ?? mb.GetType().Name);
            }

            JObject node = new JObject
            {
                ["guid"] = EditorAccess.GuidOf(mt),
                ["name"] = t.name,
                ["active"] = t.gameObject.activeSelf,
                ["components"] = components,
            };

            if (EditorAccess.HidesChildren(t))
            {
                // Prefab instances, board spaces, items and texts: children are generated/locked; the editor
                // shows none and the scene file stores none.
                node["childCount"] = 0;
                if (t.childCount > 0)
                {
                    node["childrenHidden"] = true;
                }
                node["children"] = new JArray();
                return node;
            }

            if (depth >= state.MaxDepth)
            {
                node["childCount"] = CountChildren(t, state);
                node["children"] = null;
                node["depthLimited"] = true;
                return node;
            }

            JArray children = Children(t, depth + 1, state, out int count);
            node["childCount"] = count;
            node["children"] = children;
            return node;
        }

        private static JArray Children(Transform parent, int depth, WalkState state, out int count)
        {
            JArray arr = new JArray();
            count = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                ModTransform mt = child.GetComponent<ModTransform>();
                if (mt == null || (!state.IncludeInactive && !child.gameObject.activeSelf))
                {
                    continue; // not a mod object (editor helper) or filtered out
                }
                count++;
                if (state.Nodes >= MaxNodes)
                {
                    state.Truncated = true;
                    continue;
                }
                arr.Add(Node(child, mt, depth, state));
            }
            return arr;
        }

        private static int CountChildren(Transform parent, WalkState state)
        {
            int n = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.GetComponent<ModTransform>() != null && (state.IncludeInactive || child.gameObject.activeSelf))
                {
                    n++;
                }
            }
            return n;
        }
    }
}
