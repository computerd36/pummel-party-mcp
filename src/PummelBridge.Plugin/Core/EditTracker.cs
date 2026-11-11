using System.Collections.Generic;

namespace PummelBridge.Core
{
    /// <summary>State of one editor undo stack, read without mutating it.</summary>
    public struct UndoStackState
    {
        public int Count;

        /// <summary>Identity of the top event (the boxed event object stored in the stack); null when empty.</summary>
        public object Top;

        public UndoStackState(int count, object top)
        {
            Count = count;
            Top = top;
        }

        public bool SameAs(UndoStackState other) => Count == other.Count && ReferenceEquals(Top, other.Top);
    }

    /// <summary>All undo stacks of the open mod: the scene hierarchy manager plus one per prefab that has one.</summary>
    public sealed class UndoSignature
    {
        public UndoStackState Hierarchy;

        /// <summary>Key = prefab object (reference identity).</summary>
        public readonly Dictionary<object, UndoStackState> Prefabs = new Dictionary<object, UndoStackState>(ReferenceKeyComparer.Instance);

        /// <summary>Empties the signature so one instance can be refilled every frame without allocating.</summary>
        public void Clear()
        {
            Hierarchy = default(UndoStackState);
            Prefabs.Clear();
        }
    }

    /// <summary>
    /// The bridge's own "unsaved edits" view of the open mod (main thread only; pure, no game types).
    /// <para>The editor's dirty flag is set by every undo push, including the initial snapshot that the editor pushes on
    /// the first frame after any load, so it is true for an untouched mod. This tracker instead latches "dirty" when,
    /// since the clean point (load or save), any undo stack changed in a way other than the editor's initial snapshot
    /// push (0 -> 1 entries) or an undo of that initial snapshot (1 -> 0), or when a bridge write tool reports an edit.
    /// It is fed once per frame (<see cref="Observe"/>), so an edit followed by an undo between two observations of
    /// different frames is still caught.</para>
    /// <para>If the tracker could not see the mod fresh (first observed with more than one entry on some stack), it has no
    /// baseline and <see cref="HasUnsavedEdits"/> returns null: callers fall back to the editor's flag.</para>
    /// </summary>
    public sealed class EditTracker
    {
        private object m_mod;
        private bool m_pendingBaseline;
        private bool m_haveBaseline;
        private bool m_dirty;
        private UndoStackState m_lastHierarchy;
        private readonly Dictionary<object, UndoStackState> m_lastPrefabs = new Dictionary<object, UndoStackState>(ReferenceKeyComparer.Instance);

        /// <summary>Why the tracker became dirty (first reason since the clean point), for diagnostics.</summary>
        public string DirtyReason { get; private set; }

        public object CurrentMod => m_mod;

        public bool HasBaseline => m_haveBaseline;

        /// <summary>
        /// Feed the current editor state. <paramref name="mod"/> = the open mod (reference identity, null when none);
        /// <paramref name="loaded"/> = loaded and not loading; <paramref name="signature"/> may be null when no mod is open
        /// or the stacks cannot be read (then nothing is concluded this frame).
        /// </summary>
        public void Observe(object mod, bool loaded, UndoSignature signature)
        {
            if (!ReferenceEquals(mod, m_mod))
            {
                m_mod = mod;
                m_haveBaseline = false;
                m_pendingBaseline = mod != null;
                m_dirty = false;
                DirtyReason = null;
                m_lastPrefabs.Clear();
                m_lastHierarchy = default(UndoStackState);
            }
            if (mod == null || signature == null)
            {
                return;
            }
            if (m_pendingBaseline)
            {
                if (!loaded)
                {
                    return; // still loading: the stacks are cleared by the load, nothing to compare yet
                }
                m_pendingBaseline = false;
                if (!IsFresh(signature))
                {
                    m_haveBaseline = false; // opened before we could watch it: unknown, fall back to the editor's flag
                    return;
                }
                m_haveBaseline = true;
                m_dirty = false;
                DirtyReason = null;
                Remember(signature);
                return;
            }
            if (!m_haveBaseline)
            {
                return;
            }

            if (Changed(m_lastHierarchy, signature.Hierarchy))
            {
                MarkDirty("scene undo stack changed");
            }
            foreach (KeyValuePair<object, UndoStackState> kv in signature.Prefabs)
            {
                if (m_lastPrefabs.TryGetValue(kv.Key, out UndoStackState last))
                {
                    if (Changed(last, kv.Value))
                    {
                        MarkDirty("prefab undo stack changed");
                    }
                }
                else if (kv.Value.Count > 1)
                {
                    MarkDirty("prefab undo stack changed"); // a new manager normally appears with 0 or 1 (initial) entries
                }
            }
            foreach (object key in m_lastPrefabs.Keys)
            {
                if (!signature.Prefabs.ContainsKey(key))
                {
                    MarkDirty("prefab undo stack removed"); // managers are only dropped on mod change; be conservative
                    break;
                }
            }
            Remember(signature);
        }

        /// <summary>A bridge write tool modified the open mod.</summary>
        public void MarkBridgeEdit(object mod, string what)
        {
            if (mod != null && ReferenceEquals(mod, m_mod))
            {
                MarkDirty("bridge: " + what);
            }
        }

        /// <summary>Like <see cref="MarkBridgeEdit"/>, but only when the edit actually changed something (a no-op leaves the tracker clean).</summary>
        public void MarkBridgeEditIfChanged(object mod, string what, bool changed)
        {
            if (changed)
            {
                MarkBridgeEdit(mod, what);
            }
        }

        /// <summary>
        /// The mod was saved (editor's SaveCurrentMod: scene, assets and prefabs are written). Callers observe first, so
        /// changes made before the save in the same frame are processed, then clear. A save establishes a baseline even if
        /// the tracker had none.
        /// </summary>
        public void MarkSaved(object mod, UndoSignature signature)
        {
            if (mod == null)
            {
                return;
            }
            if (!ReferenceEquals(mod, m_mod))
            {
                m_mod = mod;
                m_lastPrefabs.Clear();
            }
            m_pendingBaseline = false;
            m_dirty = false;
            DirtyReason = null;
            if (signature != null)
            {
                m_haveBaseline = true;
                Remember(signature);
            }
        }

        /// <summary>Null when <paramref name="mod"/> is not the observed mod or no baseline exists (caller falls back).</summary>
        public bool? HasUnsavedEdits(object mod)
        {
            if (mod == null || !ReferenceEquals(mod, m_mod) || !m_haveBaseline)
            {
                return null;
            }
            return m_dirty;
        }

        private void MarkDirty(string reason)
        {
            if (!m_dirty)
            {
                m_dirty = true;
                DirtyReason = reason;
            }
        }

        /// <summary>Right after a load every stack is empty or holds only the editor's initial snapshot.</summary>
        private static bool IsFresh(UndoSignature s)
        {
            if (s.Hierarchy.Count > 1)
            {
                return false;
            }
            foreach (UndoStackState p in s.Prefabs.Values)
            {
                if (p.Count > 1)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Content-neutral transitions: the initial snapshot push onto an empty stack (DoUndoSerialize's empty-stack branch)
        /// and an undo that pops that single entry again.
        /// </summary>
        public static bool Changed(UndoStackState last, UndoStackState cur)
        {
            if (last.SameAs(cur))
            {
                return false;
            }
            if (last.Count == 0 && cur.Count == 1)
            {
                return false;
            }
            if (last.Count == 1 && cur.Count == 0)
            {
                return false;
            }
            return true;
        }

        private void Remember(UndoSignature s)
        {
            m_lastHierarchy = s.Hierarchy;
            m_lastPrefabs.Clear();
            foreach (KeyValuePair<object, UndoStackState> kv in s.Prefabs)
            {
                m_lastPrefabs[kv.Key] = kv.Value;
            }
        }
    }

    /// <summary>Reference-identity comparer for dictionary keys (Unity objects override Equals/==).</summary>
    public sealed class ReferenceKeyComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceKeyComparer Instance = new ReferenceKeyComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
