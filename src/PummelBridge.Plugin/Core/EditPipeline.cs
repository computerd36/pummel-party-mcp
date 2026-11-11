using System;
using System.Diagnostics;

namespace PummelBridge.Core
{
    /// <summary>Game side of an undoable edit (main thread). Implemented in Game/EditHost.cs; faked in tests.</summary>
    public interface IEditHost
    {
        /// <summary>Null while the edit may continue; otherwise the error (mod switched/unloaded, play mode, target deleted ...).</summary>
        RpcException Validate();

        /// <summary>The target object is the only selected object.</summary>
        bool TargetIsSoleSelection();

        /// <summary>Makes the target the sole selection the way a hierarchy click does.</summary>
        void SelectTarget();

        /// <summary>The editor's undo baseline (EditorUndo.lastGameobjectUndoEvent) is a snapshot of exactly [target].</summary>
        bool BaselineCoversTarget();

        /// <summary>Identity of the current baseline snapshot; it changes whenever the editor pushes/re-baselines.</summary>
        object BaselineToken();

        /// <summary>Left mouse button held in the game window (the editor defers undo snapshots meanwhile).</summary>
        bool MouseHeld();
    }

    public enum PostEditWait
    {
        /// <summary>Complete right after the edit (no change, or an explicit undo event was pushed by the edit itself).</summary>
        None,

        /// <summary>Wait until the editor's snapshot loop records the pre-edit state as an undo step.</summary>
        AwaitPush,

        /// <summary>Wait until the editor took a baseline snapshot of the (new) target (creation).</summary>
        AwaitBaseline,
    }

    /// <summary>What an edit did. <see cref="BuildResult"/> runs when the pipeline completes (after the post-edit wait).</summary>
    public sealed class EditOutcome
    {
        public bool Changed;
        public PostEditWait Then;
        public Func<EditReport, object> BuildResult;
    }

    public sealed class EditReport
    {
        /// <summary>The edit changed the object's serialized state.</summary>
        public bool Changed;

        /// <summary>The pre-edit state is on the undo stack (pushed by the snapshot loop or explicitly).</summary>
        public bool UndoRecorded;

        /// <summary>Changed, baseline was pre-edit, but the editor had not pushed yet when the wait ended (mouse held).</summary>
        public bool UndoPending;

        /// <summary>AwaitBaseline ended without the editor snapshotting the new target.</summary>
        public bool BaselineTimedOut;
    }

    /// <summary>
    /// Runs one undoable edit like a user would do it (EditorSystem.Update snapshots the selected objects every frame
    /// while the left mouse button is up and, with one selected object, 0.1 s after the last mouse hold; an edit on an
    /// object is undoable only if a snapshot of it was taken before the edit):
    /// <list type="number">
    /// <item>select the target if it is not already the sole selection;</item>
    /// <item>wait (frame by frame, bounded) until the editor's baseline is a snapshot of [target];</item>
    /// <item>apply the edit in one frame (several property writes = one undo step);</item>
    /// <item>optionally wait until the editor pushed the pre-edit snapshot (or took a baseline of a new object);</item>
    /// <item>complete the RPC.</item>
    /// </list>
    /// Every step re-validates the editor state (mod switch, unload, play mode, deleted target) and fails instead of
    /// touching a stale object. Always completes its completion (also when the caller gave up), so the write queue moves on.
    /// <para>
    /// Timeouts: step 3 is the commit point (<see cref="IWorkCompletion.TryCommit"/>). Before it, a caller timeout cancels
    /// the call and the edit never runs; the pipeline also refuses to start or to apply with less than
    /// <see cref="CommitReserve"/> (post-edit wait + margin) left until the caller's deadline, so a call delayed in the
    /// write queue fails before touching anything. After it, the caller cannot cancel any more: the post-edit wait ends
    /// at <see cref="PushTimeout"/> or at the deadline, whichever comes first, and the normal result is returned
    /// (<c>undoPending</c> when the push was not observed in time).
    /// </para>
    /// </summary>
    public sealed class EditPipeline
    {
        public static readonly TimeSpan DefaultBaselineTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan DefaultPushTimeout = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan DefaultCommitMargin = TimeSpan.FromSeconds(0.5);
        private const int MaxReselects = 5;

        private enum Phase
        {
            AwaitBaseline,
            AwaitPush,
            AwaitPostBaseline,
            Done,
        }

        private readonly IEditHost m_host;
        private readonly IWorkCompletion m_completion;
        private readonly Func<TimeSpan> m_clock;
        private readonly bool m_selectFirst;
        private readonly Func<EditOutcome> m_apply;

        private Phase m_phase;
        private TimeSpan m_phaseStart;
        private int m_reselects;
        private object m_token;
        private EditOutcome m_outcome;
        private bool m_committed;

        public TimeSpan BaselineTimeout = DefaultBaselineTimeout;
        public TimeSpan PushTimeout = DefaultPushTimeout;

        /// <summary>Time to keep between the commit point and the caller's deadline on top of <see cref="PushTimeout"/>.</summary>
        public TimeSpan CommitMargin = DefaultCommitMargin;

        /// <summary>Minimum time left until the caller's deadline to start, and to apply the edit.</summary>
        public TimeSpan CommitReserve => PushTimeout + CommitMargin;

        /// <summary>The edit step ran (or is running): from here on the call always ends with the pipeline's own outcome.</summary>
        public bool IsCommitted => m_committed;

        /// <param name="selectFirst">True for edits of an existing object (select + wait for its baseline before applying);
        /// false for creation/undo-like edits that apply at once.</param>
        public EditPipeline(IEditHost host, IWorkCompletion completion, bool selectFirst, Func<EditOutcome> apply, Func<TimeSpan> clock = null)
        {
            m_host = host ?? throw new ArgumentNullException(nameof(host));
            m_completion = completion ?? throw new ArgumentNullException(nameof(completion));
            m_apply = apply ?? throw new ArgumentNullException(nameof(apply));
            m_selectFirst = selectFirst;
            if (clock == null)
            {
                Stopwatch sw = Stopwatch.StartNew();
                clock = () => sw.Elapsed;
            }
            m_clock = clock;
        }

        public bool IsDone => m_phase == Phase.Done;

        /// <summary>Runs as far as possible right now. Returns true when finished (no ticking needed).</summary>
        public bool Start()
        {
            try
            {
                if (m_completion.IsCancelled)
                {
                    return Fail(CancelledError());
                }
                TimeSpan left = m_completion.Remaining;
                if (left < CommitReserve)
                {
                    return Fail(new RpcException(ErrorCodes.Timeout,
                        $"the call could not start in time ({Math.Max(0, left.TotalSeconds):0.#} s of its time limit left after waiting behind other write calls, " +
                        $"{CommitReserve.TotalSeconds:0.#} s needed); nothing was changed (safe to retry)"));
                }
                RpcException invalid = m_host.Validate();
                if (invalid != null)
                {
                    return Fail(invalid);
                }
                if (!m_selectFirst)
                {
                    return Apply();
                }
                if (!m_host.TargetIsSoleSelection())
                {
                    m_host.SelectTarget();
                    if (!m_host.TargetIsSoleSelection())
                    {
                        return Fail(new RpcException(ErrorCodes.InvalidState,
                            "the editor did not accept the selection (selection is locked, e.g. while a board space or AI node gizmo is being dragged); try again"));
                    }
                }
                Enter(Phase.AwaitBaseline);
                return Step();
            }
            catch (Exception e)
            {
                return Fail(e);
            }
        }

        /// <summary>FramePoller callback: returns true when finished.</summary>
        public bool Tick()
        {
            if (m_phase == Phase.Done)
            {
                return true;
            }
            try
            {
                return Step();
            }
            catch (Exception e)
            {
                return Fail(e);
            }
        }

        private static RpcException CancelledError() =>
            new RpcException(ErrorCodes.Timeout, "the call timed out before the edit was applied; nothing was changed (safe to retry)");

        private bool Step()
        {
            if (!m_committed && m_completion.IsCancelled)
            {
                return Fail(CancelledError());
            }
            switch (m_phase)
            {
                case Phase.AwaitBaseline:
                {
                    RpcException invalid = m_host.Validate();
                    if (invalid != null)
                    {
                        return Fail(invalid);
                    }
                    if (!m_host.TargetIsSoleSelection())
                    {
                        // The user (or a gizmo) changed the selection meanwhile: select again, bounded.
                        if (++m_reselects > MaxReselects)
                        {
                            return Fail(new RpcException(ErrorCodes.InvalidState, "the selection keeps changing in the editor; nothing was changed"));
                        }
                        m_host.SelectTarget();
                        return false;
                    }
                    TimeSpan left = m_completion.Remaining;
                    if (left < CommitReserve)
                    {
                        return Fail(new RpcException(ErrorCodes.Timeout,
                            $"too little of the call's time limit is left to apply the edit and wait for the editor's undo step ({Math.Max(0, left.TotalSeconds):0.#} s); " +
                            "nothing was changed (the object was selected; safe to retry)"));
                    }
                    if (m_host.BaselineCoversTarget())
                    {
                        m_token = m_host.BaselineToken();
                        return Apply();
                    }
                    if (Elapsed() >= BaselineTimeout)
                    {
                        return Fail(m_host.MouseHeld()
                            ? new RpcException(ErrorCodes.InvalidState,
                                "the left mouse button is held in the game window, so the editor does not record undo snapshots; release it and retry (the object was selected, nothing else changed)")
                            : new RpcException(ErrorCodes.Timeout,
                                $"the editor did not take an undo snapshot of the selected object within {BaselineTimeout.TotalSeconds:0.#} s; nothing was changed"));
                    }
                    return false;
                }

                case Phase.AwaitPush:
                {
                    RpcException invalid = m_host.Validate();
                    if (invalid != null)
                    {
                        return Fail(new RpcException(invalid.Code, "the edit was applied, but then: " + invalid.Message));
                    }
                    if (!ReferenceEquals(m_host.BaselineToken(), m_token))
                    {
                        return Complete(new EditReport { Changed = true, UndoRecorded = true });
                    }
                    if (PostEditWaitOver())
                    {
                        // The pre-edit snapshot is the baseline; the editor pushes it when the mouse is released or the
                        // selection changes. The edit stays undoable.
                        return Complete(new EditReport { Changed = true, UndoRecorded = false, UndoPending = true });
                    }
                    return false;
                }

                case Phase.AwaitPostBaseline:
                {
                    RpcException invalid = m_host.Validate();
                    if (invalid != null)
                    {
                        return Fail(new RpcException(invalid.Code, "the edit was applied, but then: " + invalid.Message));
                    }
                    if (m_host.BaselineCoversTarget())
                    {
                        return Complete(new EditReport { Changed = m_outcome.Changed });
                    }
                    if (PostEditWaitOver())
                    {
                        return Complete(new EditReport { Changed = m_outcome.Changed, BaselineTimedOut = true });
                    }
                    return false;
                }

                default:
                    return true;
            }
        }

        /// <summary>Post-commit waits end at <see cref="PushTimeout"/> or at the caller's deadline, whichever comes first.</summary>
        private bool PostEditWaitOver() => Elapsed() >= PushTimeout || m_completion.Remaining <= TimeSpan.Zero;

        private bool Apply()
        {
            // Commit point: atomically either the caller's timeout wins (and the edit never runs) or the edit runs and the
            // caller waits for this pipeline's own result.
            if (!m_completion.TryCommit())
            {
                return Fail(CancelledError());
            }
            m_committed = true;
            m_outcome = m_apply() ?? throw RpcException.Internal("edit returned no outcome");
            if (!m_outcome.Changed && m_outcome.Then != PostEditWait.AwaitBaseline)
            {
                return Complete(new EditReport { Changed = false });
            }
            switch (m_outcome.Then)
            {
                case PostEditWait.AwaitPush:
                    if (m_token == null)
                    {
                        m_token = m_host.BaselineToken();
                    }
                    Enter(Phase.AwaitPush);
                    return Step();
                case PostEditWait.AwaitBaseline:
                    Enter(Phase.AwaitPostBaseline);
                    return Step();
                default:
                    return Complete(new EditReport { Changed = m_outcome.Changed, UndoRecorded = m_outcome.Changed });
            }
        }

        private void Enter(Phase p)
        {
            m_phase = p;
            m_phaseStart = m_clock();
        }

        private TimeSpan Elapsed() => m_clock() - m_phaseStart;

        private bool Complete(EditReport report)
        {
            object result;
            try
            {
                result = m_outcome?.BuildResult != null ? m_outcome.BuildResult(report) : null;
            }
            catch (Exception e)
            {
                return Fail(m_committed && report.Changed
                    ? new RpcException(ErrorCodes.Internal, $"the edit was applied, but building the reply failed: {e.GetType().Name}: {e.Message}")
                    : e);
            }
            m_phase = Phase.Done;
            m_completion.SetResult(result);
            return true;
        }

        private bool Fail(Exception e)
        {
            m_phase = Phase.Done;
            m_completion.SetException(e);
            return true;
        }
    }
}
