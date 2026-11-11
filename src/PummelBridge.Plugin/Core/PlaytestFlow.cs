using System;
using System.Diagnostics;

namespace PummelBridge.Core
{
    /// <summary>EditorSystem.State, plus Closed when the editor is not open.</summary>
    public enum PlayState
    {
        Closed,
        Editing,
        EnteringPlayMode,
        PlayMode,
        ExitingPlayMode,
    }

    /// <summary>Game side of start_playtest / stop_playtest (main thread). Implemented in Game/PlaytestHost.cs; faked in tests.</summary>
    public interface IPlaytestHost
    {
        PlayState State { get; }

        /// <summary>Null while the editor and the open mod are the ones the call started with; otherwise the error.</summary>
        RpcException CheckSameMod();

        /// <summary>start: applies the toolbar choices and calls SetEditorState(EnteringPlayMode) (what the Play button does).</summary>
        void EnterPlayMode();

        /// <summary>stop: what the Stop button does (Time.timeScale = 1, disconnectUserEvent = LeftGame, NetSystem.OnDisconnect(DisconnectReasonLeft)).</summary>
        void PressStop();

        /// <summary>stop: the editor's own Editing transition has run (hierarchy root active again).</summary>
        bool EditSceneRestored();
    }

    /// <summary>
    /// Multi-frame start/stop of a playtest with commit-point semantics. Start: commit (needs <see cref="StartBudget"/>
    /// left), <see cref="IPlaytestHost.EnterPlayMode"/>, wait until PlayMode. Stop: Editing = already stopped (no change);
    /// ExitingPlayMode = wait for Editing (no change); EnteringPlayMode = wait (pre-commit, bounded) until PlayMode, as the
    /// Stop button is disabled until then; PlayMode = commit (needs <see cref="StopBudget"/>), PressStop, wait until
    /// Editing and the edit scene is restored. Pre-commit failures say "nothing was changed"; post-commit ones say what was
    /// done. Post-commit waits end at their limit or 1 s before the caller's deadline. Always completes the completion.
    /// </summary>
    public sealed class PlaytestFlow
    {
        public static readonly TimeSpan DefaultStartBudget = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan DefaultStopBudget = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan DefaultReplyMargin = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan DefaultEnterWaitBeforeStop = TimeSpan.FromSeconds(15);

        private enum Phase
        {
            AwaitPlay,          // start, committed
            AwaitPlayBeforeStop, // stop, not committed
            AwaitEditing,       // stop (committed, or waiting for someone else's exit)
            Done,
        }

        public sealed class Outcome
        {
            public bool AlreadyStopped;
            public bool AlreadyStopping;
            public bool StopPressed;
            public PlayState FinalState;
            public TimeSpan Elapsed;
        }

        private readonly IPlaytestHost m_host;
        private readonly IWorkCompletion m_completion;
        private readonly Func<TimeSpan> m_clock;
        private readonly bool m_isStart;
        private readonly Func<Outcome, object> m_buildResult;
        private readonly TimeSpan m_t0;

        private Phase m_phase;
        private TimeSpan m_phaseStart;
        private bool m_committed;
        private bool m_alreadyStopping;

        public TimeSpan StartBudget = DefaultStartBudget;
        public TimeSpan StopBudget = DefaultStopBudget;
        public TimeSpan ReplyMargin = DefaultReplyMargin;
        public TimeSpan EnterWaitBeforeStop = DefaultEnterWaitBeforeStop;

        private PlaytestFlow(IPlaytestHost host, IWorkCompletion completion, bool isStart, Func<Outcome, object> buildResult, Func<TimeSpan> clock)
        {
            m_host = host ?? throw new ArgumentNullException(nameof(host));
            m_completion = completion ?? throw new ArgumentNullException(nameof(completion));
            m_buildResult = buildResult ?? throw new ArgumentNullException(nameof(buildResult));
            m_isStart = isStart;
            if (clock == null)
            {
                Stopwatch sw = Stopwatch.StartNew();
                clock = () => sw.Elapsed;
            }
            m_clock = clock;
            m_t0 = clock();
        }

        public static PlaytestFlow ForStart(IPlaytestHost host, IWorkCompletion completion, Func<Outcome, object> buildResult, Func<TimeSpan> clock = null) =>
            new PlaytestFlow(host, completion, true, buildResult, clock);

        public static PlaytestFlow ForStop(IPlaytestHost host, IWorkCompletion completion, Func<Outcome, object> buildResult, Func<TimeSpan> clock = null) =>
            new PlaytestFlow(host, completion, false, buildResult, clock);

        public bool IsDone => m_phase == Phase.Done;

        public bool IsCommitted => m_committed;

        /// <summary>Runs as far as possible now. True when finished.</summary>
        public bool Start()
        {
            try
            {
                if (m_completion.IsCancelled)
                {
                    return Fail(Cancelled());
                }
                return m_isStart ? BeginStart() : BeginStop();
            }
            catch (Exception e)
            {
                return Fail(Wrap(e));
            }
        }

        /// <summary>Frame poller callback. True when finished.</summary>
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
                return Fail(Wrap(e));
            }
        }

        private bool BeginStart()
        {
            Commit(StartBudget, "start the playtest");
            m_host.EnterPlayMode();
            PlayState s = m_host.State;
            if (s == PlayState.Editing)
            {
                return Fail(new RpcException(ErrorCodes.Internal,
                    "the playtest could not start: the editor saved the mod, failed to load its playtest copy and stayed in edit mode " +
                    "(the editor shows its own error popup; see get_log)"));
            }
            if (s == PlayState.PlayMode)
            {
                return Complete(PlayState.PlayMode);
            }
            if (s != PlayState.EnteringPlayMode)
            {
                return Fail(new RpcException(ErrorCodes.Internal, $"the playtest start left the editor in state {s}; the mod was saved; check get_status"));
            }
            Enter(Phase.AwaitPlay);
            return Step();
        }

        private bool BeginStop()
        {
            PlayState s = m_host.State;
            switch (s)
            {
                case PlayState.Editing:
                    return Complete(PlayState.Editing, alreadyStopped: true);
                case PlayState.ExitingPlayMode:
                    m_alreadyStopping = true;
                    Enter(Phase.AwaitEditing);
                    return Step();
                case PlayState.EnteringPlayMode:
                    Enter(Phase.AwaitPlayBeforeStop);
                    return Step();
                case PlayState.PlayMode:
                    return PressStop();
                default:
                    return Fail(new RpcException(ErrorCodes.EditorNotOpen, "the mod editor is not open"));
            }
        }

        private bool PressStop()
        {
            Commit(StopBudget, "stop the playtest");
            m_host.PressStop();
            Enter(Phase.AwaitEditing);
            return Step();
        }

        private bool Step()
        {
            if (!m_committed && m_completion.IsCancelled)
            {
                return Fail(Cancelled());
            }
            RpcException same = m_host.CheckSameMod();
            if (same != null)
            {
                return Fail(m_committed ? new RpcException(same.Code, Applied() + same.Message) : same);
            }
            PlayState s = m_host.State;
            switch (m_phase)
            {
                case Phase.AwaitPlay:
                    if (s == PlayState.PlayMode)
                    {
                        return Complete(s);
                    }
                    if (s == PlayState.Editing || s == PlayState.ExitingPlayMode)
                    {
                        return Fail(new RpcException(ErrorCodes.InvalidState,
                            $"the mod was saved and the playtest started, but it returned to edit mode (state {s}) before play mode was reached " +
                            "(a load error or the playtest was stopped by hand); see get_log"));
                    }
                    if (WaitOver(m_completion.Remaining - ReplyMargin))
                    {
                        return Fail(new RpcException(ErrorCodes.Timeout,
                            $"the mod was saved and the playtest is starting, but play mode was not reached within {Elapsed().TotalSeconds:0} s (state {s}); " +
                            "it may still finish: check get_status, then use stop_playtest; do not retry start_playtest blindly"));
                    }
                    return false;

                case Phase.AwaitPlayBeforeStop:
                    if (s == PlayState.PlayMode)
                    {
                        return PressStop();
                    }
                    if (s == PlayState.Editing)
                    {
                        return Complete(s, alreadyStopped: true);
                    }
                    if (s == PlayState.ExitingPlayMode)
                    {
                        m_alreadyStopping = true;
                        Enter(Phase.AwaitEditing);
                        return false;
                    }
                    // Pre-commit wait: at most EnterWaitBeforeStop, and only while StopBudget would still be left for the stop itself.
                    TimeSpan budget = EnterWaitBeforeStop - (m_clock() - m_phaseStart);
                    TimeSpan byDeadline = m_completion.Remaining - StopBudget;
                    if (byDeadline < budget)
                    {
                        budget = byDeadline;
                    }
                    if (WaitOver(budget))
                    {
                        return Fail(new RpcException(ErrorCodes.InvalidState,
                            $"the playtest is still starting (state {s} after {Elapsed().TotalSeconds:0} s); the editor's Stop button is disabled until play mode " +
                            "is reached; nothing was changed, retry stop_playtest later"));
                    }
                    return false;

                case Phase.AwaitEditing:
                    if (s == PlayState.Editing && m_host.EditSceneRestored())
                    {
                        return Complete(s, alreadyStopping: m_alreadyStopping, stopPressed: m_committed);
                    }
                    if (WaitOver(m_completion.Remaining - ReplyMargin))
                    {
                        return Fail(new RpcException(ErrorCodes.Timeout, m_committed
                            ? $"the Stop sequence was sent, but the editor did not return to edit mode within {Elapsed().TotalSeconds:0} s (state {s}); check get_status"
                            : $"the playtest is ending (state {s}) but the editor did not return to edit mode within {Elapsed().TotalSeconds:0} s; nothing was changed by this call; check get_status"));
                    }
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>A wait is over when the budget still available for it is used up.</summary>
        private static bool WaitOver(TimeSpan remainingBudget) => remainingBudget <= TimeSpan.Zero;

        private void Commit(TimeSpan needed, string what)
        {
            TimeSpan left = m_completion.Remaining;
            if (left < needed)
            {
                throw new RpcException(ErrorCodes.Timeout,
                    $"not enough of the call's time limit left to {what} ({Math.Max(0, left.TotalSeconds):0.#} s left, {needed.TotalSeconds:0} s needed; " +
                    "it waited behind other write calls); nothing was changed (safe to retry)");
            }
            if (!m_completion.TryCommit())
            {
                throw Cancelled();
            }
            m_committed = true;
        }

        private RpcException Cancelled() => new RpcException(ErrorCodes.Timeout, "the call timed out before it changed anything; nothing was changed (safe to retry)");

        private string Applied() => m_isStart
            ? "the playtest start was requested (Play: toolbar choices applied, then the editor saves the mod and enters play mode), but then: "
            : "the Stop sequence was sent, but then: ";

        /// <summary>Exceptions thrown by the host or by Commit; after the commit point the message says what was already done.</summary>
        private Exception Wrap(Exception e)
        {
            if (!m_committed)
            {
                return e;
            }
            string what = e is RpcException rpc ? rpc.Message : $"{e.GetType().Name}: {e.Message}";
            string code = e is RpcException rc ? rc.Code : ErrorCodes.Internal;
            return new RpcException(code, Applied() + what + $" (editor state now {SafeState()})");
        }

        private string SafeState()
        {
            try
            {
                return m_host.State.ToString();
            }
            catch
            {
                return "unknown";
            }
        }

        private void Enter(Phase p)
        {
            m_phase = p;
            m_phaseStart = m_clock();
        }

        private TimeSpan Elapsed() => m_clock() - m_t0;

        private bool Complete(PlayState final, bool alreadyStopped = false, bool alreadyStopping = false, bool stopPressed = false)
        {
            object result;
            try
            {
                result = m_buildResult(new Outcome
                {
                    AlreadyStopped = alreadyStopped,
                    AlreadyStopping = alreadyStopping,
                    StopPressed = stopPressed,
                    FinalState = final,
                    Elapsed = Elapsed(),
                });
            }
            catch (Exception e)
            {
                return Fail(Wrap(e));
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
