using System;
using Newtonsoft.Json.Linq;

namespace PummelBridge.Core
{
    /// <summary>
    /// run_batch (write, guard OpenMod): runs existing open-mod write tools and read tools in order as one write-queue
    /// turn, each step with its own guard evaluation, its own time limit and (for edits) its own undo step. Unity-free:
    /// steps run through the tool registry, so the whole tool is unit-tested with fake tools.
    /// </summary>
    public static class BatchTool
    {
        /// <summary>Dispatcher limit of the whole batch (queueing + all steps). The commit grace (4 s) comes on top.</summary>
        public static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(120);

        /// <param name="poller">Ticks the batch (and the steps' own multi-frame work) once per frame.</param>
        /// <param name="clock">Test hook; null = real time.</param>
        public static void Register(ToolRegistry registry, FramePoller poller, Func<TimeSpan> clock = null)
        {
            registry.Register(ToolDefinition.Write("run_batch", WriteGuard.OpenMod(),
                ctx => BatchPlan.Parse(ctx.Params, name => Lookup(registry, ctx, name)),
                inv =>
                {
                    BatchRunner runner = new BatchRunner(inv.ArgsAs<BatchPlan>(), inv.Completion, new RegistryStepExecutor(registry), clock);
                    if (!runner.Start())
                    {
                        poller.Add(runner.Tick);
                    }
                },
                BatchTimeout));
        }

        private static BatchToolInfo Lookup(ToolRegistry registry, ToolContext outer, string name)
        {
            if (!registry.TryGet(name, out RegisteredTool t))
            {
                return null;
            }
            ToolDefinition d = t.Definition;
            TimeSpan timeout = d.Timeout ?? outer.MainThreadTimeout;
            BatchToolInfo info = new BatchToolInfo { IsWrite = d.IsWrite, Timeout = timeout };
            if (d.IsWrite)
            {
                info.Parse = p => ToolRegistry.ParseWriteArgs(d, new ToolContext(name, p, InlineDispatcher.Instance, timeout));
            }
            return info;
        }

        /// <summary>Starts steps through the registry: read handlers inline, write bodies behind their own guard.</summary>
        internal sealed class RegistryStepExecutor : IBatchStepExecutor
        {
            private readonly ToolRegistry m_registry;

            public RegistryStepExecutor(ToolRegistry registry)
            {
                m_registry = registry;
            }

            public void Start(BatchStep step, JObject resolvedParams, IWorkCompletion completion)
            {
                if (!m_registry.TryGet(step.Tool, out RegisteredTool t))
                {
                    completion.SetException(RpcException.Internal($"tool '{step.Tool}' is no longer registered"));
                    return;
                }
                ToolDefinition d = t.Definition;
                ToolContext ctx = new ToolContext(step.Tool, resolvedParams ?? step.Params, InlineDispatcher.Instance, step.Timeout);
                if (!d.IsWrite)
                {
                    // Read handlers normally run on an HTTP thread and hop to the main thread with ctx.OnMainThread; here we
                    // already are on the main thread, so the inline dispatcher runs that work synchronously.
                    completion.SetResult(d.ReadHandler(ctx));
                    return;
                }
                // Steps without references were parsed up front from the same (unescaped) params.
                object args = step.PreParsed ? step.PreParsedArgs : ToolRegistry.ParseWriteArgs(d, ctx);
                m_registry.StartGuarded(d, ctx, args, completion);
            }
        }
    }

    /// <summary>
    /// Dispatcher for code that already runs on the main thread (batch steps): runs the work at once and requires it to
    /// complete synchronously. Work that would need later frames (e.g. end-of-frame capture) fails with INTERNAL.
    /// </summary>
    public sealed class InlineDispatcher : IMainThreadDispatcher
    {
        public static readonly InlineDispatcher Instance = new InlineDispatcher();

        public object Invoke(Action<IWorkCompletion> start, TimeSpan timeout)
        {
            SyncCompletion c = new SyncCompletion();
            start(c);
            if (!c.Done)
            {
                throw RpcException.Internal("this tool needs more than one frame and cannot run inside run_batch");
            }
            if (c.Error != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(c.Error).Throw();
            }
            return c.Result;
        }

        private sealed class SyncCompletion : IWorkCompletion
        {
            public bool Done;
            public object Result;
            public Exception Error;
            public bool IsCancelled => false;
            public bool TryCommit() => !Done;
            public TimeSpan Remaining => TimeSpan.MaxValue;
            public void SetResult(object result)
            {
                if (!Done)
                {
                    Result = result;
                    Done = true;
                }
            }
            public void SetException(Exception error)
            {
                if (!Done)
                {
                    Error = error;
                    Done = true;
                }
            }
        }
    }
}
