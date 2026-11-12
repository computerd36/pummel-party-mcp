using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    /// <summary>Starts one batch step. Implemented over the tool registry (<see cref="BatchTool"/>); faked in tests.</summary>
    public interface IBatchStepExecutor
    {
        /// <summary>
        /// Starts <paramref name="step"/> with its resolved params. Must complete <paramref name="completion"/> now or from a
        /// later frame (also on errors and when the step's time runs out before its commit point). May throw.
        /// </summary>
        void Start(BatchStep step, JObject resolvedParams, IWorkCompletion completion);
    }

    /// <summary>
    /// Completion handed to one batch step. Behaves towards the step's body like the dispatcher's completion of a direct
    /// call: <see cref="Remaining"/> runs to the step's own deadline (the tool's limit, capped by the batch's deadline
    /// minus a reply margin); before the step commits, the deadline cancels it (TryCommit then returns false). The first
    /// commit of any step also commits the outer batch call, so from then on the batch caller waits for the batch's own result.
    /// </summary>
    public sealed class BatchStepCompletion : IWorkCompletion
    {
        private readonly IWorkCompletion m_outer;
        private readonly Func<TimeSpan> m_clock;
        private readonly TimeSpan m_deadline;
        private bool m_committed;
        private bool m_cancelled;

        public bool Done { get; private set; }
        public object Result { get; private set; }
        public Exception Error { get; private set; }

        /// <summary>Absolute deadline on the runner's clock.</summary>
        public TimeSpan Deadline => m_deadline;

        public BatchStepCompletion(IWorkCompletion outer, Func<TimeSpan> clock, TimeSpan deadline)
        {
            m_outer = outer;
            m_clock = clock;
            m_deadline = deadline;
        }

        public bool Committed => m_committed;

        public bool IsCancelled
        {
            get
            {
                if (m_committed || Done)
                {
                    return false;
                }
                if (!m_cancelled && (Remaining <= TimeSpan.Zero || m_outer.IsCancelled))
                {
                    m_cancelled = true;
                }
                return m_cancelled;
            }
        }

        public TimeSpan Remaining => m_deadline - m_clock();

        public bool TryCommit()
        {
            if (Done)
            {
                return false;
            }
            if (m_committed)
            {
                return true;
            }
            if (IsCancelled)
            {
                return false;
            }
            if (!m_outer.TryCommit())
            {
                m_cancelled = true;
                return false;
            }
            m_committed = true;
            return true;
        }

        public void SetResult(object result)
        {
            if (Done)
            {
                return;
            }
            Result = result;
            Done = true;
        }

        public void SetException(Exception error)
        {
            if (Done)
            {
                return;
            }
            Error = error ?? new Exception("unknown error");
            Done = true;
        }
    }

    /// <summary>
    /// Runs a validated <see cref="BatchPlan"/> step by step on the main thread (inside the batch's write-queue turn).
    /// For each step: stop if the batch caller already gave up before anything was committed; refuse to start (partial
    /// result, stopReason "timeBudget") with less than <see cref="StepStartBudget"/> of the batch's time left; resolve
    /// references against earlier results (an unresolvable one fails the step with INVALID_PARAMS); start the step with
    /// its own <see cref="BatchStepCompletion"/>; wait (frame by frame) for it to finish. stopOnError stops at the first
    /// failed step. There is no rollback. A step that does not finish within its deadline plus <see cref="StepGrace"/>
    /// aborts the batch (stopReason "stepDidNotFinish"). Always completes the outer completion exactly once.
    /// </summary>
    public sealed class BatchRunner
    {
        public static readonly TimeSpan DefaultStepStartBudget = TimeSpan.FromSeconds(4);
        public static readonly TimeSpan DefaultReplyMargin = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan DefaultStepGrace = TimeSpan.FromSeconds(2);

        private readonly BatchPlan m_plan;
        private readonly IWorkCompletion m_outer;
        private readonly IBatchStepExecutor m_exec;
        private readonly Func<TimeSpan> m_clock;
        private readonly TimeSpan m_outerDeadline;
        private readonly Dictionary<string, JToken> m_results = new Dictionary<string, JToken>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_failedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly JArray m_rows = new JArray();

        private int m_next;
        private BatchStep m_current;
        private BatchStepCompletion m_currentCompletion;
        private int m_completed;
        private int m_firstFailed = -1;
        private string m_stopReason;
        private bool m_finished;
        private bool m_anyCommitted;

        public TimeSpan StepStartBudget = DefaultStepStartBudget;
        public TimeSpan ReplyMargin = DefaultReplyMargin;
        public TimeSpan StepGrace = DefaultStepGrace;

        public BatchRunner(BatchPlan plan, IWorkCompletion outer, IBatchStepExecutor exec, Func<TimeSpan> clock = null)
        {
            m_plan = plan ?? throw new ArgumentNullException(nameof(plan));
            m_outer = outer ?? throw new ArgumentNullException(nameof(outer));
            m_exec = exec ?? throw new ArgumentNullException(nameof(exec));
            if (clock == null)
            {
                Stopwatch sw = Stopwatch.StartNew();
                clock = () => sw.Elapsed;
            }
            m_clock = clock;
            TimeSpan left = outer.Remaining;
            // Unknown deadline (TimeSpan.MaxValue): treat as far away without overflowing.
            m_outerDeadline = left > TimeSpan.FromDays(1) ? m_clock() + TimeSpan.FromDays(1) : m_clock() + left;
        }

        public bool IsFinished => m_finished;

        /// <summary>Runs as far as possible now. True when the batch finished (no ticking needed).</summary>
        public bool Start() => Advance();

        /// <summary>Frame poller callback. True when finished.</summary>
        public bool Tick() => Advance();

        private TimeSpan BatchLeft() => m_outerDeadline - ReplyMargin - m_clock();

        private bool Advance()
        {
            if (m_finished)
            {
                return true;
            }
            try
            {
                while (true)
                {
                    if (m_current != null)
                    {
                        if (!m_currentCompletion.Done)
                        {
                            if (m_clock() > m_currentCompletion.Deadline + StepGrace)
                            {
                                string msg = m_currentCompletion.Committed
                                    ? $"the step was applied but did not finish within its time limit (+{StepGrace.TotalSeconds:0} s); the batch was stopped because it may still be running"
                                    : $"the step did not finish within its time limit (+{StepGrace.TotalSeconds:0} s); the batch was stopped";
                                Record(m_current, false, null, new RpcException(ErrorCodes.Timeout, msg));
                                m_current = null;
                                return Finish("stepDidNotFinish");
                            }
                            return false; // wait for the step (next frame)
                        }
                        BatchStep done = m_current;
                        m_current = null;
                        bool ok = m_currentCompletion.Error == null;
                        Record(done, ok, m_currentCompletion.Result, m_currentCompletion.Error);
                        if (!ok && !m_anyCommitted && m_outer.IsCancelled)
                        {
                            return Finish(m_next < m_plan.Steps.Count ? "cancelled" : null);
                        }
                        if (!ok && m_plan.StopOnError)
                        {
                            return Finish(m_next < m_plan.Steps.Count ? "stepFailed" : null);
                        }
                    }

                    if (m_next >= m_plan.Steps.Count)
                    {
                        return Finish(null);
                    }
                    if (!m_anyCommitted && m_outer.IsCancelled)
                    {
                        return Finish("cancelled");
                    }
                    if (BatchLeft() < StepStartBudget)
                    {
                        return Finish("timeBudget");
                    }

                    BatchStep step = m_plan.Steps[m_next++];
                    JObject resolved;
                    try
                    {
                        // Also unescapes "$$" in steps without references.
                        resolved = Placeholders.Resolve(step.Params, ResultOf);
                    }
                    catch (RpcException e)
                    {
                        Record(step, false, null, e);
                        if (m_plan.StopOnError)
                        {
                            return Finish(m_next < m_plan.Steps.Count ? "stepFailed" : null);
                        }
                        continue;
                    }

                    TimeSpan now = m_clock();
                    TimeSpan cap = m_outerDeadline - ReplyMargin;
                    TimeSpan own = now + step.Timeout;
                    BatchStepCompletion sc = new BatchStepCompletion(new CommitTracker(this), m_clock, own < cap ? own : cap);
                    m_current = step;
                    m_currentCompletion = sc;
                    try
                    {
                        m_exec.Start(step, resolved, sc);
                    }
                    catch (Exception e)
                    {
                        sc.SetException(e);
                    }
                    // Loop: a synchronously finished step is recorded at once; otherwise wait for the next frame.
                }
            }
            catch (Exception e)
            {
                // Never leave the outer call hanging (the write queue waits for it).
                if (m_current != null)
                {
                    Record(m_current, false, null, e);
                    m_current = null;
                }
                return Finish("internalError");
            }
        }

        private JToken ResultOf(string id)
        {
            if (m_results.TryGetValue(id, out JToken r))
            {
                return r ?? JValue.CreateNull();
            }
            if (m_failedIds.TryGetValue(id, out string why))
            {
                throw RpcException.InvalidParams($"step '{id}' {why}, so its result cannot be referenced");
            }
            throw RpcException.InvalidParams($"step '{id}' has not run, so its result cannot be referenced");
        }

        private void Record(BatchStep step, bool ok, object result, Exception error)
        {
            JObject row = new JObject { ["index"] = step.Index };
            if (step.Id != null)
            {
                row["id"] = step.Id;
            }
            row["tool"] = step.Tool;
            row["ok"] = ok;
            if (ok)
            {
                JToken r = result as JToken ?? (result == null ? JValue.CreateNull() : JToken.FromObject(result));
                row["result"] = r;
                m_completed++;
                if (step.Id != null)
                {
                    m_results[step.Id] = r;
                }
            }
            else
            {
                RpcException rpc = error as RpcException;
                JObject err = new JObject
                {
                    ["code"] = rpc?.Code ?? ErrorCodes.Internal,
                    ["message"] = rpc != null ? rpc.Message : $"{error?.GetType().Name}: {error?.Message}",
                };
                if (rpc?.ErrorData != null)
                {
                    try
                    {
                        err["data"] = rpc.ErrorData as JToken ?? JToken.FromObject(rpc.ErrorData);
                    }
                    catch
                    {
                        // detail is optional
                    }
                }
                row["error"] = err;
                if (m_firstFailed < 0)
                {
                    m_firstFailed = step.Index;
                }
                if (step.Id != null)
                {
                    m_failedIds[step.Id] = "failed";
                }
            }
            m_rows.Add(row);
        }

        private bool Finish(string stopReason)
        {
            if (m_finished)
            {
                return true;
            }
            m_finished = true;
            m_stopReason = stopReason;
            int total = m_plan.Steps.Count;
            int attempted = m_rows.Count;
            JObject result = new JObject
            {
                ["completed"] = m_completed,
                ["attempted"] = attempted,
                ["total"] = total,
            };
            if (m_firstFailed >= 0)
            {
                result["failedStep"] = m_firstFailed;
            }
            bool stoppedEarly = attempted < total;
            result["stoppedEarly"] = stoppedEarly;
            if (stopReason != null && stoppedEarly)
            {
                result["stopReason"] = stopReason;
            }
            else if (stopReason == "stepDidNotFinish" || stopReason == "internalError")
            {
                result["stopReason"] = stopReason;
            }
            result["results"] = m_rows;
            result["note"] = Note(stopReason, stoppedEarly, attempted, total);
            m_outer.SetResult(result);
            return true;
        }

        private string Note(string stopReason, bool stoppedEarly, int attempted, int total)
        {
            string head;
            switch (stopReason)
            {
                case "stepFailed":
                    head = $"Stopped at the first failed step (steps[{m_firstFailed}], stopOnError); steps[{attempted}] and later were not run.";
                    break;
                case "timeBudget":
                    head = $"Stopped because less than {StepStartBudget.TotalSeconds:0} s of the batch's time limit was left; steps[{attempted}] and later were not started (nothing of them was applied).";
                    break;
                case "stepDidNotFinish":
                    head = $"Stopped because steps[{attempted - 1}] did not finish in time; later steps were not run.";
                    break;
                case "cancelled":
                    head = "Stopped because the caller gave up before any step changed anything.";
                    break;
                case "internalError":
                    head = "Stopped by an internal error.";
                    break;
                default:
                    head = m_firstFailed >= 0
                        ? $"All {total} steps were attempted; {total - m_completed} failed (stopOnError false), the others were applied."
                        : $"All {total} steps succeeded.";
                    break;
            }
            return head + " There is no rollback: every step that succeeded stays applied (object creation is not undoable in the editor; " +
                   "revert with delete_object, or undo one step per call). Nothing was saved unless a save_mod step ran.";
        }

        /// <summary>Outer completion as seen by a step: commit/cancel go to the batch call; results stay with the step.</summary>
        private sealed class CommitTracker : IWorkCompletion
        {
            private readonly BatchRunner m_runner;

            public CommitTracker(BatchRunner runner)
            {
                m_runner = runner;
            }

            public bool IsCancelled => !m_runner.m_anyCommitted && m_runner.m_outer.IsCancelled;

            public bool TryCommit()
            {
                if (m_runner.m_anyCommitted)
                {
                    return true;
                }
                if (!m_runner.m_outer.TryCommit())
                {
                    return false;
                }
                m_runner.m_anyCommitted = true;
                return true;
            }

            public TimeSpan Remaining => m_runner.m_outerDeadline - m_runner.m_clock();

            public void SetResult(object result) { }

            public void SetException(Exception error) { }
        }
    }
}
