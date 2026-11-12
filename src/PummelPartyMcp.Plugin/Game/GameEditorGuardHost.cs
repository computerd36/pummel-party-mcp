using System.Globalization;
using PummelPartyMcp.Core;
using UnityEngine;

namespace PummelPartyMcp.Game
{
    /// <summary>Game side of the write guard. Main thread only.</summary>
    internal sealed class GameEditorGuardHost : IEditorGuardHost
    {
        private readonly IPluginLog m_log;

        public GameEditorGuardHost(IPluginLog log)
        {
            m_log = log ?? NullPluginLog.Instance;
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
                if (snap.ModName == null && GameReflection.FindField(typeof(PummelMod), "m_metaData") == null)
                {
                    snap.ModNameError = GameReflection.MissingMessage(typeof(PummelMod), "m_metaData");
                }
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
        /// This is the only field of the workshop item the plugin ever writes.
        /// </summary>
        public static bool EnforceDefaultWorkshopId(PummelMod mod, IPluginLog log)
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
            Debug.LogWarning("[PummelPartyMcp] " + msg);
            return true;
        }
    }
}
