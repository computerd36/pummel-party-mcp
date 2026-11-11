using PummelBridge.Core;
using UnityEngine;

namespace PummelBridge.Game
{
    /// <summary>
    /// Where edits may happen: the current editing context. Scene: objects below EditorSystem.m_hierarchyRoot.
    /// Prefab edit: the open prefab's PrefabObject and everything below it. Children of objects that hide their children
    /// (prefab instances, board spaces, items, texts) are never addressable. Main thread only.
    /// </summary>
    internal static class EditContext
    {
        /// <summary>The transform new top-level objects are created under (what DoGameObjectMenu uses as default root).</summary>
        public static Transform CreationRoot(EditorSystem ed)
        {
            PummelPrefab prefab = ed.CurrentPrefab;
            if (prefab != null)
            {
                GameObject po = prefab.PrefabObject;
                return po != null ? po.transform : null;
            }
            return ed.m_hierarchyRoot;
        }

        /// <summary>True if <paramref name="t"/> is an object of the current context (the prefab object itself counts).</summary>
        public static bool IsEditable(EditorSystem ed, Transform t)
        {
            if (t == null)
            {
                return false;
            }
            Transform root = CreationRoot(ed);
            if (root == null)
            {
                return false;
            }
            bool scene = ed.CurrentPrefab == null;
            if (!scene && t == root)
            {
                return true;
            }
            for (Transform cur = t.parent; cur != null; cur = cur.parent)
            {
                if (cur == root)
                {
                    return true;
                }
                if (EditorAccess.HidesChildren(cur))
                {
                    return false; // generated/locked child of a prefab instance etc.
                }
            }
            return false;
        }

        /// <summary>Context key used to compare two objects (scene root or prefab object); null if neither.</summary>
        public static Transform ContextOf(EditorSystem ed, Transform t)
        {
            for (Transform cur = t; cur != null; cur = cur.parent)
            {
                if (cur == ed.m_hierarchyRoot)
                {
                    return cur;
                }
                if (cur.parent == ed.m_prefabsRoot && cur.GetComponent<ModPrefab>() != null)
                {
                    return cur;
                }
            }
            return null;
        }

        /// <summary>The open prefab's root object (cannot be moved or deleted).</summary>
        public static bool IsPrefabRootObject(EditorSystem ed, Transform t) =>
            t != null && t.GetComponent<ModPrefab>() != null && t.parent == ed.m_prefabsRoot;

        /// <summary>Resolves an object for editing: NOT_FOUND / INVALID_STATE when it is outside the current context.</summary>
        public static ModTransform RequireEditable(EditorSystem ed, string guid, string what = "object")
        {
            ModTransform mt = EditorAccess.FindByGuid(ed, guid, out Transform root);
            if (mt == null)
            {
                throw RpcException.NotFound($"no {what} with guid {guid}");
            }
            if (!IsEditable(ed, mt.transform))
            {
                PummelPrefab prefab = ed.CurrentPrefab;
                Transform ctx = ContextOf(ed, mt.transform);
                if (prefab != null && ctx == ed.m_hierarchyRoot)
                {
                    throw new RpcException(ErrorCodes.InvalidState,
                        $"'{mt.name}' is a scene object, but the prefab '{prefab.name}' is being edited; return to the scene hierarchy first");
                }
                string prefabName = ctx != null ? ctx.GetComponent<ModPrefab>()?.targetPrefab?.name ?? ctx.name : null;
                throw new RpcException(ErrorCodes.InvalidState, prefabName != null
                    ? $"'{mt.name}' belongs to the prefab '{prefabName}'; open that prefab in the editor to edit it"
                    : $"'{mt.name}' is not in the current editing context");
            }
            return mt;
        }

        /// <summary>
        /// Preconditions shared by all object edits (in addition to the write guard): no editor dialog open (the user
        /// could not edit either, and the Add Component popup shares static state), no asset with unapplied import
        /// settings selected (selecting an object would pop up a dialog), no create/open still loading.
        /// </summary>
        public static void RequireEditReady(EditorSystem ed)
        {
            Tools.ModTools.RefuseIfLifecyclePendingPublic();
            EditorPopup popup = EditorPopupManager.CurrentPopup;
            if (popup != null)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"an editor dialog is open ('{popup.GetTitle()}'); close it first");
            }
            EditorSelectionManager sel = ed.GetSelectionManager();
            if (sel.IsAnAssetSelected() && sel.IsSelectedAssetDirty())
            {
                throw new RpcException(ErrorCodes.InvalidState,
                    $"the selected asset '{sel.GetSelectedAsset().name}' has unapplied import settings; apply or revert them in the inspector first");
            }
        }
    }

    /// <summary>IEditHost over the live editor for one edit. Captures the editor/mod/context at creation.</summary>
    internal sealed class EditHost : IEditHost
    {
        private readonly EditorSystem m_ed;
        private readonly PummelMod m_mod;
        private readonly PummelPrefab m_prefab;

        public GameObject Target;

        public EditHost(EditorSystem ed, GameObject target)
        {
            m_ed = ed;
            m_mod = ed.CurrentMod;
            m_prefab = ed.CurrentPrefab;
            Target = target;
        }

        public EditorSystem Editor => m_ed;
        public PummelMod Mod => m_mod;

        public RpcException Validate()
        {
            EditorSystem ed = EditorAccess.EditorOrNull();
            if (ed == null || !ReferenceEquals(ed, m_ed))
            {
                return new RpcException(ErrorCodes.EditorNotOpen, "the mod editor was closed");
            }
            if (!ReferenceEquals(ed.CurrentMod, m_mod) || m_mod == null)
            {
                return new RpcException(ErrorCodes.InvalidState, "the open mod changed (closed, switched or reloaded)");
            }
            if (ed.IsLoading || !m_mod.IsLoaded)
            {
                return new RpcException(ErrorCodes.BusyLoading, "the editor started loading a mod");
            }
            if (ed.State != EditorState.Editing)
            {
                return new RpcException(ErrorCodes.InvalidState, $"the editor left edit mode (state {ed.State})");
            }
            if (!ReferenceEquals(ed.CurrentPrefab, m_prefab))
            {
                return new RpcException(ErrorCodes.InvalidState, "the editing context changed (a prefab was opened or closed)");
            }
            if (Target != null || !ReferenceEquals(Target, null))
            {
                if (Target == null)
                {
                    return new RpcException(ErrorCodes.NotFound, "the object was destroyed");
                }
                if (!EditContext.IsEditable(ed, Target.transform))
                {
                    return new RpcException(ErrorCodes.NotFound, "the object was deleted or moved out of the editing context");
                }
            }
            return null;
        }

        public bool TargetIsSoleSelection()
        {
            EditorSelectionManager sel = m_ed.GetSelectionManager();
            return sel.GetSelectedObjectCount() == 1 && sel.GetFirstSelected() == Target;
        }

        public void SelectTarget() => m_ed.GetSelectionManager().SetSelectedObject(Target);

        public bool BaselineCoversTarget() => UndoAccess.BaselineCovers(UndoAccess.CurrentForWrite(m_ed), Target);

        public object BaselineToken() => UndoAccess.BaselineToken(UndoAccess.CurrentForWrite(m_ed));

        public bool MouseHeld() => UndoAccess.MouseHeld();

        public int UndoStackLength() => UndoAccess.CurrentForWrite(m_ed).UndoStackLength();

        public byte[] BaselineData() => UndoAccess.CurrentForWrite(m_ed).lastGameobjectUndoEvent.data;
    }
}
