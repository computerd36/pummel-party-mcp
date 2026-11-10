using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PummelBridge.Core;
using UnityEngine;

namespace PummelBridge.Game
{
    /// <summary>Read-only access to the editor singleton. All members must be called on the Unity main thread.</summary>
    internal static class EditorAccess
    {
        public static EditorSystem EditorOrNull()
        {
            EditorSystem ed = EditorSystem.Instance;
            return ed == null ? null : ed; // Unity null check (destroyed objects compare equal to null)
        }

        public static EditorSystem RequireEditor()
        {
            EditorSystem ed = EditorOrNull();
            if (ed == null)
            {
                throw new RpcException(ErrorCodes.EditorNotOpen, "the mod editor is not open");
            }
            return ed;
        }

        /// <summary>Editor open, not loading, a mod open and fully loaded.</summary>
        public static PummelMod RequireLoadedMod(EditorSystem ed)
        {
            if (ed.IsLoading)
            {
                throw new RpcException(ErrorCodes.BusyLoading, "the editor is loading a mod");
            }
            PummelMod mod = ed.CurrentMod;
            if (mod == null)
            {
                throw new RpcException(ErrorCodes.NoModOpen, "no mod is open in the editor");
            }
            if (!mod.IsLoaded)
            {
                throw new RpcException(ErrorCodes.BusyLoading, "the open mod has not finished loading");
            }
            return mod;
        }

        public static string ModName(PummelMod mod)
        {
            // GetModName() dereferences m_metaData without a null check; read the field defensively.
            object meta = GameReflection.FindField(typeof(PummelMod), "m_metaData")?.GetValue(mod);
            return (meta as ModMetaData)?.Name;
        }

        public static string ModFolderName(PummelMod mod)
        {
            string root = mod.m_modRootDirectory;
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }
            return Path.GetFileName(root.TrimEnd('\\', '/'));
        }

        /// <summary>
        /// Dirty flag of the current undo context, read from fields. EditorSystem.IsModDirty is not called because
        /// GetUndoManager() lazily creates and stores a new EditorUndo when a prefab context has none yet.
        /// Null if the editor internals changed.
        /// </summary>
        public static bool? IsDirty(EditorSystem ed)
        {
            try
            {
                object prefab = GameReflection.RequireField(typeof(EditorSystem), "m_currentPrefab").GetValue(ed);
                EditorUndo undo;
                if (prefab == null)
                {
                    undo = GameReflection.RequireField(typeof(EditorSystem), "m_heirarchyUndoManager").GetValue(ed) as EditorUndo;
                }
                else
                {
                    IDictionary map = GameReflection.RequireField(typeof(EditorSystem), "m_undoManagerList").GetValue(ed) as IDictionary;
                    undo = map != null && map.Contains(prefab) ? map[prefab] as EditorUndo : null;
                    if (undo == null)
                    {
                        return false; // no undo manager yet for this prefab => nothing pushed => not dirty
                    }
                }
                return undo?.IsModDirty();
            }
            catch (RpcException)
            {
                return null;
            }
        }

        public static int? UndoStackLength(EditorSystem ed)
        {
            try
            {
                object prefab = GameReflection.RequireField(typeof(EditorSystem), "m_currentPrefab").GetValue(ed);
                if (prefab != null)
                {
                    IDictionary map = GameReflection.RequireField(typeof(EditorSystem), "m_undoManagerList").GetValue(ed) as IDictionary;
                    return map != null && map.Contains(prefab) ? (map[prefab] as EditorUndo)?.UndoStackLength() : 0;
                }
                return (GameReflection.RequireField(typeof(EditorSystem), "m_heirarchyUndoManager").GetValue(ed) as EditorUndo)?.UndoStackLength();
            }
            catch (RpcException)
            {
                return null;
            }
        }

        /// <summary>Root of the current editing context (scene hierarchy, or prefab root while editing a prefab).</summary>
        public static Transform ContextRoot(EditorSystem ed) => ed.CurrentPrefab == null ? ed.m_hierarchyRoot : ed.m_prefabsRoot;

        public static Transform OtherRoot(EditorSystem ed) => ed.CurrentPrefab == null ? ed.m_prefabsRoot : ed.m_hierarchyRoot;

        public static string GuidOf(ModBehaviour behaviour) => behaviour.guid.ToString();

        /// <summary>Same rule the hierarchy widget and the scene serializer use (EditorSystem.ShouldHideChildGameObjects).</summary>
        public static bool HidesChildren(Transform t) => EditorSystem.ShouldHideChildGameObjects(t);

        public static string NormalizeGuid(string text, string paramName)
        {
            if (!Guid.TryParse(text, out Guid g))
            {
                throw RpcException.InvalidParams($"'{paramName}' is not a valid GUID");
            }
            return g.ToString();
        }

        /// <summary>
        /// Finds a mod object by its ModTransform guid among editor-visible objects (children of objects that hide
        /// their children, e.g. prefab instances, are skipped), searching the current context root first.
        /// </summary>
        public static ModTransform FindByGuid(EditorSystem ed, string normalizedGuid, out Transform foundUnderRoot)
        {
            foreach (Transform root in new[] { ContextRoot(ed), OtherRoot(ed) })
            {
                if (root == null)
                {
                    continue;
                }
                ModTransform hit = FindUnder(root, normalizedGuid);
                if (hit != null)
                {
                    foundUnderRoot = root;
                    return hit;
                }
            }
            foundUnderRoot = null;
            return null;
        }

        private static ModTransform FindUnder(Transform root, string guid)
        {
            Stack<Transform> stack = new Stack<Transform>();
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                stack.Push(root.GetChild(i));
            }
            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                ModTransform mt = t.GetComponent<ModTransform>();
                if (mt != null && GuidOf(mt) == guid)
                {
                    return mt;
                }
                if (HidesChildren(t))
                {
                    continue;
                }
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    stack.Push(t.GetChild(i));
                }
            }
            return null;
        }

        public static string PathOf(Transform t, Transform root)
        {
            List<string> parts = new List<string>();
            for (Transform cur = t; cur != null && cur != root; cur = cur.parent)
            {
                parts.Add(cur.name);
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        /// <summary>Mod components of an object (ModBehaviour subclasses registered in ModReflectionSystem), in component order.</summary>
        public static List<ModBehaviour> ModComponents(GameObject go)
        {
            List<ModBehaviour> result = new List<ModBehaviour>();
            foreach (ModBehaviour mb in go.GetComponents<ModBehaviour>())
            {
                if (mb != null && ModReflectionSystem.GetType(mb.GetType()) != null)
                {
                    result.Add(mb);
                }
            }
            return result;
        }
    }
}
