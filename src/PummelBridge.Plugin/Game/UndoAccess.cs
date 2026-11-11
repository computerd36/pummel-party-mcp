using System.Collections;
using System.Collections.Generic;
using System.IO;
using PummelBridge.Core;
using UnityEngine;

namespace PummelBridge.Game
{
    /// <summary>
    /// Read access to the editor's undo managers (EditorUndo) without mutating them. Main thread only.
    /// Read paths never call EditorSystem.GetUndoManager() (it lazily inserts a manager for an open prefab); write paths
    /// may, since EditorSystem.Update calls it every frame anyway.
    /// </summary>
    internal static class UndoAccess
    {
        public static EditorUndo HierarchyManager(EditorSystem ed) =>
            GameReflection.RequireField(typeof(EditorSystem), "m_heirarchyUndoManager").GetValue(ed) as EditorUndo;

        public static IDictionary PrefabManagers(EditorSystem ed) =>
            GameReflection.RequireField(typeof(EditorSystem), "m_undoManagerList").GetValue(ed) as IDictionary;

        /// <summary>Count and top event identity of EditorUndo.m_undoEventStack (Stack&lt;BaseUndoEvent&gt;; Peek does not mutate).</summary>
        public static UndoStackState StackState(EditorUndo undo)
        {
            if (undo == null)
            {
                return default(UndoStackState);
            }
            Stack<BaseUndoEvent> stack = GameReflection.RequireField(typeof(EditorUndo), "m_undoEventStack").GetValue(undo) as Stack<BaseUndoEvent>;
            if (stack == null || stack.Count == 0)
            {
                return new UndoStackState(0, null);
            }
            return new UndoStackState(stack.Count, stack.Peek());
        }

        /// <summary>Type name of the top event (GameObjectUndoEvent, MoveGameObject, DeleteGameObject, DuplicateGameObject) or null.</summary>
        public static string TopEventType(EditorUndo undo)
        {
            object top = StackState(undo).Top;
            return top?.GetType().Name;
        }

        /// <summary>All stacks of the open mod; null if the editor internals changed.</summary>
        public static UndoSignature Signature(EditorSystem ed)
        {
            try
            {
                UndoSignature sig = new UndoSignature { Hierarchy = StackState(HierarchyManager(ed)) };
                IDictionary map = PrefabManagers(ed);
                if (map != null)
                {
                    foreach (DictionaryEntry e in map)
                    {
                        if (e.Key != null)
                        {
                            sig.Prefabs[e.Key] = StackState(e.Value as EditorUndo);
                        }
                    }
                }
                return sig;
            }
            catch (RpcException)
            {
                return null;
            }
        }

        /// <summary>Undo manager of the current context for write paths (same call EditorSystem.Update makes).</summary>
        public static EditorUndo CurrentForWrite(EditorSystem ed) => ed.GetUndoManager();

        /// <summary>The baseline snapshot (lastGameobjectUndoEvent) covers exactly [go].</summary>
        public static bool BaselineCovers(EditorUndo undo, GameObject go)
        {
            GameObject[] objs = undo?.lastGameobjectUndoEvent.objects;
            return objs != null && objs.Length == 1 && objs[0] == go && go != null;
        }

        /// <summary>Identity of the baseline snapshot data (a new array each time the editor re-baselines).</summary>
        public static object BaselineToken(EditorUndo undo) => undo?.lastGameobjectUndoEvent.data;

        /// <summary>
        /// Serializes one object exactly like the editor's undo snapshot (ModHierarchySerializer.SaveTransform with
        /// ignoreChildren) into a private buffer. Calls the reflected getters, as the editor does every frame for the
        /// selected object; only used on the object being edited, after it has been selected and snapshotted.
        /// </summary>
        public static byte[] Snapshot(GameObject go)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    ModHierarchySerializer.SaveTransform(w, go.transform, ignoreRoot: false, ignoreChildren: true);
                    w.Flush();
                    return ms.ToArray();
                }
            }
        }

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        public static bool MouseHeld() => Input.GetMouseButton(0);
    }
}
