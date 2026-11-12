using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    /// <summary>
    /// One RPC method. Created only through <see cref="Read"/> or <see cref="Write"/>: a write tool cannot exist
    /// without a <see cref="WriteGuard"/>, and its body is reachable only through the registry's guard wrapper.
    /// </summary>
    public sealed class ToolDefinition
    {
        public string Name { get; }

        /// <summary>Write tools mutate the editor/mod. They always run behind the guard layer (WriteGuardEvaluator).</summary>
        public bool IsWrite { get; }

        /// <summary>Read tools: runs on an HTTP worker thread and uses ctx.OnMainThread for game access. Null for write tools.</summary>
        public Func<ToolContext, object> ReadHandler { get; }

        /// <summary>Write tools: guard declaration (never null for write tools).</summary>
        public WriteGuard Guard { get; }

        /// <summary>Write tools: parses/validates params on the HTTP thread (may throw INVALID_PARAMS). May be null.</summary>
        internal Func<ToolContext, object> ParseArgs { get; }

        /// <summary>Write tools: runs on the main thread after the guard passed, in the same unit of work.</summary>
        internal Action<WriteInvocation> Body { get; }

        /// <summary>Write tools: overall time limit (queueing + body + deferred completion). Null = default.</summary>
        public TimeSpan? Timeout { get; }

        private ToolDefinition(string name, bool isWrite, Func<ToolContext, object> readHandler, WriteGuard guard,
            Func<ToolContext, object> parseArgs, Action<WriteInvocation> body, TimeSpan? timeout)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("name required", nameof(name));
            }
            Name = name;
            IsWrite = isWrite;
            ReadHandler = readHandler;
            Guard = guard;
            ParseArgs = parseArgs;
            Body = body;
            Timeout = timeout;
        }

        public static ToolDefinition Read(string name, Func<ToolContext, object> handler) =>
            new ToolDefinition(name, false, handler ?? throw new ArgumentNullException(nameof(handler)), null, null, null, null);

        /// <summary>
        /// Declares a write tool. <paramref name="guard"/> is mandatory. <paramref name="body"/> must complete the
        /// invocation (inv.Complete / inv.Fail), either synchronously or from a later frame.
        /// </summary>
        public static ToolDefinition Write(string name, WriteGuard guard, Func<ToolContext, object> parseArgs,
            Action<WriteInvocation> body, TimeSpan? timeout = null)
        {
            if (guard == null)
            {
                throw new ArgumentNullException(nameof(guard), $"write tool '{name}' must declare a WriteGuard");
            }
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }
            if (timeout.HasValue && timeout.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }
            return new ToolDefinition(name, true, null, guard, parseArgs, body, timeout);
        }
    }

    /// <summary>A tool as the RPC layer sees it: <see cref="Invoke"/> already includes the guard for write tools.</summary>
    public sealed class RegisteredTool
    {
        public ToolDefinition Definition { get; }
        public Func<ToolContext, object> Invoke { get; }

        internal RegisteredTool(ToolDefinition definition, Func<ToolContext, object> invoke)
        {
            Definition = definition;
            Invoke = invoke;
        }
    }

    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, RegisteredTool> m_tools = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        private readonly WriteSafety m_safety;

        /// <param name="safety">Required to register write tools; a registry without it accepts read tools only.</param>
        /// <param name="writeQueue">Serialises write tool bodies on the main thread (one at a time). Created if null.</param>
        public ToolRegistry(WriteSafety safety = null, WriteQueue writeQueue = null)
        {
            m_safety = safety;
            WriteQueue = writeQueue ?? new WriteQueue();
        }

        /// <summary>Main thread only. Pumped by the plugin once per frame (stuck-entry safety net).</summary>
        public WriteQueue WriteQueue { get; }

        public void Register(ToolDefinition tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }
            if (m_tools.ContainsKey(tool.Name))
            {
                throw new InvalidOperationException($"Tool '{tool.Name}' is already registered.");
            }
            Func<ToolContext, object> invoke;
            if (tool.IsWrite)
            {
                if (tool.Guard == null || tool.Body == null)
                {
                    throw new InvalidOperationException($"Refusing to register write tool '{tool.Name}' without a guard.");
                }
                if (m_safety == null)
                {
                    throw new InvalidOperationException($"Refusing to register write tool '{tool.Name}': this registry has no write safety configured.");
                }
                invoke = ctx => InvokeWrite(tool, ctx);
            }
            else
            {
                invoke = tool.ReadHandler ?? throw new InvalidOperationException($"Read tool '{tool.Name}' has no handler.");
            }
            m_tools.Add(tool.Name, new RegisteredTool(tool, invoke));
        }

        public bool TryGet(string name, out RegisteredTool tool) => m_tools.TryGetValue(name, out tool);

        public IEnumerable<string> Names => m_tools.Keys;

        /// <summary>
        /// Params are parsed on the calling (HTTP) thread. On the main thread the call joins the write queue (one write
        /// body at a time, FIFO); when its turn comes, the guard and the start of the body run in one unit of work, so the
        /// editor state cannot change between check and action. The timeout covers queueing and the whole body.
        /// </summary>
        private object InvokeWrite(ToolDefinition tool, ToolContext ctx)
        {
            object args = tool.ParseArgs?.Invoke(ctx);
            TimeSpan timeout = tool.Timeout ?? ctx.MainThreadTimeout;
            return ctx.InvokeOnMainThread(outer => WriteQueue.Enqueue(tool.Name, outer, completion => StartGuarded(tool, ctx, args, completion)), timeout);
        }

        /// <summary>
        /// Main thread: evaluates the tool's guard and, if it passes, applies the Workshop id hygiene and starts the body,
        /// all in one unit of work. Used for every write call (inside its write-queue turn) and for each write step of
        /// run_batch (inside the batch's turn). The body completes <paramref name="completion"/> (now or later).
        /// </summary>
        internal void StartGuarded(ToolDefinition tool, ToolContext ctx, object args, IWorkCompletion completion)
        {
            if (tool == null || !tool.IsWrite || tool.Guard == null || tool.Body == null || m_safety == null)
            {
                completion.SetException(new RpcException(ErrorCodes.Internal, "write tool without guard configuration"));
                return;
            }
            EditorSnapshot snap = m_safety.Host.Capture();
            RpcException denied = WriteGuardEvaluator.Evaluate(m_safety, tool.Guard, args, snap);
            if (denied != null)
            {
                completion.SetException(denied);
                return;
            }
            if (snap.ModOpen && snap.ModLoaded && snap.OpenModIsTestMod)
            {
                m_safety.Host.EnforceDefaultWorkshopId();
            }
            tool.Body(new WriteInvocation(ctx, args, completion, snap, m_safety));
        }

        /// <summary>Parses a write tool's params (no game access; may throw INVALID_PARAMS). Null when the tool has no parser.</summary>
        internal static object ParseWriteArgs(ToolDefinition tool, ToolContext ctx) => tool.ParseArgs?.Invoke(ctx);
    }

    public sealed class ToolContext
    {
        private readonly IMainThreadDispatcher m_dispatcher;

        public string Method { get; }
        public JObject Params { get; }
        public TimeSpan MainThreadTimeout { get; }

        public ToolContext(string method, JObject parameters, IMainThreadDispatcher dispatcher, TimeSpan mainThreadTimeout)
        {
            Method = method;
            Params = parameters ?? new JObject();
            m_dispatcher = dispatcher;
            MainThreadTimeout = mainThreadTimeout;
        }

        /// <summary>Runs synchronous work on the Unity main thread.</summary>
        public T OnMainThread<T>(Func<T> work) => m_dispatcher.Invoke(work, MainThreadTimeout);

        /// <summary>Starts work on the main thread that completes later (e.g. a coroutine).</summary>
        public object OnMainThreadDeferred(Action<IWorkCompletion> start) => m_dispatcher.Invoke(start, MainThreadTimeout);

        internal object InvokeOnMainThread(Action<IWorkCompletion> start, TimeSpan timeout) => m_dispatcher.Invoke(start, timeout);

        // ---- parameter helpers (throw INVALID_PARAMS on type errors) ----

        private JToken Get(string name)
        {
            JToken t = Params[name];
            return t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined ? null : t;
        }

        public string GetString(string name, bool required = false)
        {
            JToken t = Get(name);
            if (t == null)
            {
                if (required)
                {
                    throw RpcException.InvalidParams($"'{name}' is required");
                }
                return null;
            }
            if (t.Type != JTokenType.String)
            {
                throw RpcException.InvalidParams($"'{name}' must be a string");
            }
            return (string)t;
        }

        public bool GetBool(string name, bool defaultValue)
        {
            JToken t = Get(name);
            if (t == null)
            {
                return defaultValue;
            }
            if (t.Type != JTokenType.Boolean)
            {
                throw RpcException.InvalidParams($"'{name}' must be a boolean");
            }
            return (bool)t;
        }

        public long? GetLong(string name, long min, long max)
        {
            JToken t = Get(name);
            if (t == null)
            {
                return null;
            }
            if (t.Type != JTokenType.Integer)
            {
                throw RpcException.InvalidParams($"'{name}' must be an integer");
            }
            long v;
            try
            {
                v = (long)t;
            }
            catch (OverflowException)
            {
                throw RpcException.InvalidParams($"'{name}' is out of range");
            }
            if (v < min || v > max)
            {
                throw RpcException.InvalidParams($"'{name}' must be between {min} and {max}");
            }
            return v;
        }

        public int GetInt(string name, int defaultValue, int min, int max)
        {
            long? v = GetLong(name, min, max);
            return v.HasValue ? (int)v.Value : defaultValue;
        }
    }
}
