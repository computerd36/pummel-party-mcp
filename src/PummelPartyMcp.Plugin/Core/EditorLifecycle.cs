using System;

namespace PummelPartyMcp.Core
{
    /// <summary>Where the plugin is in its editor-scoped lifecycle (decision P2).</summary>
    public enum LifecycleState
    {
        /// <summary>No patch, no server, no log listener, no per-frame work. Only the scene-loaded callback exists.</summary>
        Dormant,

        /// <summary>The Mod Editor is open and everything started.</summary>
        Active,

        /// <summary>The Mod Editor is open but arming failed (publish block incomplete, token or port problem): the tools are off.</summary>
        Failed,

        /// <summary>The Mod Editor is gone: new calls are refused, running work gets a short grace, then everything is removed.</summary>
        Disarming,
    }

    /// <summary>What the host must do after an event.</summary>
    public enum LifecycleAction
    {
        None,

        /// <summary>Install the publish block, start the server and the hooks now (synchronously, main thread), then call <see cref="EditorLifecycle.Armed"/>.</summary>
        Arm,

        /// <summary>Stop the listener and refuse queued work; keep pumping running work for the grace period.</summary>
        BeginDisarm,

        /// <summary>Fail what is left, remove every patch, go dormant, then call <see cref="EditorLifecycle.Disarmed"/>.</summary>
        FinishDisarm,
    }

    /// <summary>
    /// The plugin's lifecycle as a pure state machine (decision P2: active only while the Mod Editor is open). Unity-free; the
    /// plugin feeds it the scene loads and, while armed, one call per frame. Main thread only.
    /// <para>
    /// Arm: a scene load while Dormant whose name is the editor scene, or any scene load while the editor object exists (fallback
    /// if the scene is renamed). Stay armed while the editor object exists: scene loads during a playtest, or the rare exits where
    /// the editor object survives into the main menu, change nothing. Disarm: the editor object is gone after it was seen, or,
    /// if it was never seen, at a later scene load that is not the editor scene while no editor object exists (fail closed: no
    /// time-out, so an editor that comes up late still finds the publish block in place). Finish the disarm when the running
    /// work is done, or after <see cref="DisarmGrace"/> and at least <see cref="MinGraceFrames"/> frames.
    /// </para>
    /// </summary>
    public sealed class EditorLifecycle
    {
        /// <summary>The game's scene name of the Mod Editor (GameManager.OpenScene("ModEditorScene")).</summary>
        public const string EditorSceneName = "ModEditorScene";

        public static readonly TimeSpan DisarmGrace = TimeSpan.FromSeconds(2);

        /// <summary>The grace ends by time only after this many frames, so one long frame cannot cut it short.</summary>
        public const int MinGraceFrames = 10;

        private bool m_seenEditor;
        private TimeSpan m_disarmStartedAt;
        private int m_graceFrames;
        private bool m_pendingRearm;

        public LifecycleState State { get; private set; } = LifecycleState.Dormant;

        /// <summary>UTC time of the last arm (null before the first).</summary>
        public DateTime? ArmedUtc { get; private set; }

        /// <summary>Editor sessions armed since the game started.</summary>
        public int SessionsThisRun { get; private set; }

        /// <summary>Why the last disarm started (log text), or null.</summary>
        public string DisarmReason { get; private set; }

        /// <summary>True between <see cref="LifecycleAction.Arm"/> and <see cref="Armed"/> (the host is arming).</summary>
        public bool Arming { get; private set; }

        /// <summary>The editor object was seen in this session (before that, only a non-editor scene load ends the session).</summary>
        public bool SeenEditor => m_seenEditor;

        /// <summary>The state as get_status shows it.</summary>
        public string StateName => State.ToString().ToLowerInvariant();

        public static bool IsEditorScene(string sceneName) => string.Equals(sceneName, EditorSceneName, StringComparison.Ordinal);

        /// <summary>A scene finished loading (SceneManager.sceneLoaded).</summary>
        public LifecycleAction OnSceneLoaded(string sceneName, bool editorExists, TimeSpan now)
        {
            bool editor = editorExists || IsEditorScene(sceneName);
            switch (State)
            {
                case LifecycleState.Dormant:
                    if (!editor || Arming)
                    {
                        return LifecycleAction.None;
                    }
                    Arming = true;
                    return LifecycleAction.Arm;
                case LifecycleState.Disarming:
                    if (editor)
                    {
                        m_pendingRearm = true; // arm again right after the disarm finished
                    }
                    return LifecycleAction.None;
                default:
                    if (editorExists)
                    {
                        m_seenEditor = true;
                        return LifecycleAction.None; // playtest scene loads and the like: stay armed
                    }
                    if (!m_seenEditor && !IsEditorScene(sceneName))
                    {
                        // the editor never came up and another scene replaced its scene: this session is over
                        return StartDisarm($"the Mod Editor never appeared and scene {sceneName} loaded", now);
                    }
                    return LifecycleAction.None; // a seen editor that is gone is noticed by the next frame
            }
        }

        /// <summary>The host finished arming. <paramref name="toolsOn"/> false = Failed (the tools stay off for this editor session).</summary>
        public void Armed(bool toolsOn, bool editorExists, TimeSpan now, DateTime utcNow)
        {
            Arming = false;
            State = toolsOn ? LifecycleState.Active : LifecycleState.Failed;
            m_seenEditor = editorExists;
            m_graceFrames = 0;
            m_pendingRearm = false;
            DisarmReason = null;
            ArmedUtc = utcNow;
            SessionsThisRun++;
        }

        /// <summary>
        /// Once per frame while armed or disarming. <paramref name="workIdle"/>: no call is queued or running (only read while
        /// Disarming).
        /// </summary>
        public LifecycleAction OnFrame(bool editorExists, TimeSpan now, bool workIdle)
        {
            switch (State)
            {
                case LifecycleState.Active:
                case LifecycleState.Failed:
                    if (editorExists)
                    {
                        m_seenEditor = true;
                        return LifecycleAction.None;
                    }
                    if (m_seenEditor)
                    {
                        return StartDisarm("the Mod Editor was closed", now);
                    }
                    return LifecycleAction.None; // never seen: wait (fail closed); a later non-editor scene load ends it
                case LifecycleState.Disarming:
                    if (editorExists)
                    {
                        m_pendingRearm = true; // a new editor came up during the grace
                    }
                    m_graceFrames++;
                    if (workIdle || (now - m_disarmStartedAt >= DisarmGrace && m_graceFrames >= MinGraceFrames))
                    {
                        return LifecycleAction.FinishDisarm;
                    }
                    return LifecycleAction.None;
                default:
                    return LifecycleAction.None;
            }
        }

        /// <summary>
        /// The host removed everything. Returns <see cref="LifecycleAction.Arm"/> when an editor came up meanwhile (or exists now):
        /// the host then arms again at once.
        /// </summary>
        public LifecycleAction Disarmed(bool editorExists)
        {
            bool rearm = m_pendingRearm || editorExists;
            State = LifecycleState.Dormant;
            m_pendingRearm = false;
            m_seenEditor = false;
            m_graceFrames = 0;
            if (rearm)
            {
                Arming = true;
                return LifecycleAction.Arm;
            }
            return LifecycleAction.None;
        }

        /// <summary>Arming threw before it could report: the host treats the session as Failed (tools off, disarm when the editor goes).</summary>
        public void ArmAborted(bool editorExists, TimeSpan now, DateTime utcNow) => Armed(false, editorExists, now, utcNow);

        private LifecycleAction StartDisarm(string reason, TimeSpan now)
        {
            State = LifecycleState.Disarming;
            DisarmReason = reason;
            m_disarmStartedAt = now;
            m_graceFrames = 0;
            return LifecycleAction.BeginDisarm;
        }
    }

    /// <summary>Which tools the server of the current editor session may run.</summary>
    public enum ToolAccess
    {
        /// <summary>The server is up but arming has not finished (main thread busy in the scene load).</summary>
        Starting,

        Enabled,

        /// <summary>The Workshop publish block could not be installed completely at editor open (decision P2, Q2 option b).</summary>
        BlockIncomplete,

        /// <summary>The editor could not be read at editor open (EditorSystem.Instance unreadable: game update?): diagnostics only.</summary>
        EditorUnreadable,

        /// <summary>The Mod Editor was closed; the server is stopping.</summary>
        Stopping,
    }

    /// <summary>
    /// The per-session tool gate in front of the registry (RpcHandler). With an incomplete publish block only the diagnostic tools
    /// answer, so the agent can tell the human why; no editor or mod tool runs in that editor session. Pure.
    /// </summary>
    public static class SessionGate
    {
        /// <summary>Tools that answer in every state: they read status and logs only and change nothing.</summary>
        public static readonly string[] DiagnosticTools = { "ping", "get_status", "get_log", "self_test" };

        public static bool IsDiagnostic(string method) => Array.IndexOf(DiagnosticTools, method) >= 0;

        /// <summary>Null when <paramref name="method"/> may run; otherwise the error to return (nothing ran).</summary>
        public static RpcException Check(ToolAccess access, string method, string missing)
        {
            if (access == ToolAccess.Enabled || IsDiagnostic(method))
            {
                return null;
            }
            switch (access)
            {
                case ToolAccess.BlockIncomplete:
                    return new RpcException(ErrorCodes.PublishBlockIncomplete,
                        "the Workshop publish block could not be installed completely when the Mod Editor opened" +
                        (string.IsNullOrEmpty(missing) ? "" : " (missing: " + missing + ")") +
                        "; every tool except " + string.Join(", ", DiagnosticTools) + " is off until the Mod Editor is closed. Nothing was run. " +
                        "See get_status.publishBlock and the BepInEx log.");
                case ToolAccess.EditorUnreadable:
                    return new RpcException(ErrorCodes.Internal,
                        "the plugin cannot read the Mod Editor (EditorSystem.Instance; game version changed?); every tool except " +
                        string.Join(", ", DiagnosticTools) + " is off until the Mod Editor is closed. Nothing was run. The Workshop publish block is installed. " +
                        "See self_test and the BepInEx log.");
                case ToolAccess.Stopping:
                    return EditorClosed();
                default:
                    return new RpcException(ErrorCodes.EditorNotOpen,
                        "the Mod Editor is still opening (the plugin is starting); the call was not run and nothing was changed. Retry in a second.");
            }
        }

        /// <summary>A call that never ran because the Mod Editor closed.</summary>
        public static RpcException EditorClosed() =>
            new RpcException(ErrorCodes.EditorNotOpen, EditorClosedMessage);

        public const string EditorClosedMessage = "the Mod Editor was closed; the call was not run and nothing was changed.";

        /// <summary>
        /// A call that was still running when the Mod Editor closed (its own result never came): its own code, because unlike
        /// EDITOR_NOT_OPEN it may have changed something.
        /// </summary>
        public static RpcException EditorClosedWhileRunning() =>
            new RpcException(ErrorCodes.EditorClosedDuringCall,
                "the Mod Editor was closed while the call was running; a write may have partly applied (a read tool changed nothing). " +
                "Open the Mod Editor and check get_status / get_object before retrying; do not retry blindly.");
    }
}
