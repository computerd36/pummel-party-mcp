using System;
using PummelPartyMcp.Core;
using PummelPartyMcp.Hooks;

namespace PummelPartyMcp.Game
{
    /// <summary>
    /// Shared services of the edit tools: frame poller and the unsaved-edits tracker wired to the live editor.
    /// Main thread only.
    /// </summary>
    internal sealed class EditServices
    {
        public FramePoller Poller { get; }
        public EditTracker Tracker { get; } = new EditTracker();
        public IPluginLog Log { get; }

        /// <summary>Playtest timeline (get_status.playtest), fed by <see cref="Observe"/>.</summary>
        public PlaytestClock Playtest { get; } = new PlaytestClock();

        /// <summary>Playtest event ring + diffing (get_playtest_events / get_playtest_state), fed by <see cref="Observe"/>.</summary>
        public PlaytestFeed Feed { get; }

        private readonly PlaytestWatch m_watch;

        /// <summary>Reused every frame by <see cref="Observe"/> (the tracker copies what it keeps).</summary>
        private readonly UndoSignature m_scratch = new UndoSignature();

        public EditServices(FramePoller poller, IPluginLog log)
        {
            Poller = poller;
            Log = log ?? NullPluginLog.Instance;
            Feed = new PlaytestFeed(PlaytestFeed.DefaultCapacity, PlaytestStateReader.StateName);
            PlaytestLogicRecorder.Feed = Feed;
            m_watch = new PlaytestWatch(Feed, Log);
        }

        /// <summary>Feeds the tracker with the current editor state (once per frame, and before every check).</summary>
        public void Observe()
        {
            try
            {
                EditorSystem ed = EditorAccess.EditorOrNull();
                DateTime now = DateTime.UtcNow;
                PlayState playState = PlaytestAccess.StateOf(ed);
                Playtest.Observe(playState, now);
                m_watch.Frame(ed, playState, now);
                PummelMod mod = ed?.CurrentMod;
                if (ed == null || mod == null)
                {
                    Tracker.Observe(null, false, null);
                    return;
                }
                Tracker.Observe(mod, mod.IsLoaded && !ed.IsLoading, UndoAccess.Fill(ed, m_scratch) ? m_scratch : null);
            }
            catch (Exception e)
            {
                GameReflection.WarnOnce("edittracker.observe", "edit tracker: cannot read the editor's undo state: " + e.Message);
            }
        }

        /// <summary>Called from the SaveCurrentMod postfix (and by save_mod): moves the clean point.</summary>
        public void OnSaved()
        {
            EditorSystem ed = EditorAccess.EditorOrNull();
            PummelMod mod = ed?.CurrentMod;
            if (ed == null || mod == null)
            {
                return;
            }
            Observe();
            Tracker.MarkSaved(mod, UndoAccess.Signature(ed));
        }

        public void MarkEdit(PummelMod mod, string what)
        {
            Observe();
            Tracker.MarkPluginEdit(mod, what);
        }

        /// <summary>Marks a plugin edit only when the edit actually changed something (a no-op must not look like an unsaved edit).</summary>
        public void MarkEditIfChanged(PummelMod mod, string what, bool changed)
        {
            Observe();
            Tracker.MarkPluginEditIfChanged(mod, what, changed);
        }

        /// <summary>
        /// The plugin's view: unsaved edits since load/save; falls back to the editor's dirty flag when the tracker has no
        /// baseline for this mod (or the save postfix is missing, since user saves would then go unnoticed). Null when
        /// neither is readable.
        /// </summary>
        public bool? HasUnsavedEdits(EditorSystem ed)
        {
            PummelMod mod = ed?.CurrentMod;
            if (mod == null)
            {
                return null;
            }
            Observe();
            bool? tracked = SaveWatch.Installed ? Tracker.HasUnsavedEdits(mod) : null;
            return tracked ?? EditorAccess.IsDirty(ed);
        }

        /// <summary>"tracker" when the plugin's own tracking is used, "editorFlag" for the fallback.</summary>
        public string Source(EditorSystem ed)
        {
            PummelMod mod = ed?.CurrentMod;
            return mod != null && SaveWatch.Installed && Tracker.HasUnsavedEdits(mod).HasValue ? "tracker" : "editorFlag";
        }
    }
}
