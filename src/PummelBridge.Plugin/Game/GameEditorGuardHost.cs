using System.Globalization;
using PummelBridge.Core;
using UnityEngine;

namespace PummelBridge.Game
{
    /// <summary>Game side of the write guard. Main thread only.</summary>
    internal sealed class GameEditorGuardHost : IEditorGuardHost
    {
        private readonly IBridgeLog m_log;

        public GameEditorGuardHost(IBridgeLog log)
        {
            m_log = log ?? NullBridgeLog.Instance;
        }

        public EditorSnapshot Capture()
        {
            EditorSnapshot snap = new EditorSnapshot();
            EditorSystem ed = EditorAccess.EditorOrNull();
            if (ed == null)
            {
                return snap;
            }
            snap.EditorOpen = true;
            snap.IsLoading = ed.IsLoading;
            EditorState state = ed.State;
            snap.IsEditing = state == EditorState.Editing;
            snap.StateName = state.ToString();
            PummelMod mod = ed.CurrentMod;
            if (mod != null)
            {
                snap.ModOpen = true;
                snap.ModLoaded = mod.IsLoaded;
                snap.ModName = EditorAccess.ModName(mod);
                snap.FolderName = EditorAccess.ModFolderName(mod);
            }
            return snap;
        }

        public void EnforceDefaultWorkshopId()
        {
            EditorSystem ed = EditorAccess.EditorOrNull();
            PummelMod mod = ed?.CurrentMod;
            if (mod != null)
            {
                EnforceDefaultWorkshopId(mod, m_log);
            }
        }

        /// <summary>
        /// Resets workshopItem.publishedFileId to ModWorkshopItem.DefaultWorkshopID (256) if it differs. Callers must
        /// have verified that <paramref name="mod"/> is a test mod. Returns true if it was changed.
        /// This is the only field of the workshop item the bridge ever writes.
        /// </summary>
        public static bool EnforceDefaultWorkshopId(PummelMod mod, IBridgeLog log)
        {
            ModWorkshopItem item = mod.workshopItem;
            if (item == null || item.publishedFileId == ModWorkshopItem.DefaultWorkshopID)
            {
                return false;
            }
            ulong old = item.publishedFileId;
            item.publishedFileId = ModWorkshopItem.DefaultWorkshopID;
            string msg = $"Test mod '{EditorAccess.ModFolderName(mod)}' carried Workshop item id {old.ToString(CultureInfo.InvariantCulture)} " +
                         $"(copied from a published mod?). Reset to {ModWorkshopItem.DefaultWorkshopID} in memory; the next save writes {ModWorkshopItem.DefaultWorkshopID}.";
            log.Warning(msg);
            Debug.LogWarning("[PummelBridge] " + msg);
            return true;
        }
    }
}
