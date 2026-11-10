using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace PummelBridge.Core
{
    /// <summary>One RPC method. Handlers run on an HTTP worker thread and use ctx.OnMainThread for game access.</summary>
    public sealed class ToolDefinition
    {
        public string Name { get; }

        /// <summary>
        /// Write tools mutate the editor/mod. This build refuses to register them; Phase 2 will add a guard layer
        /// (PB_TEST_ allowlist, publish blocklist) in front of every write tool before this can be enabled.
        /// </summary>
        public bool IsWrite { get; }

        public Func<ToolContext, object> Handler { get; }

        public ToolDefinition(string name, bool isWrite, Func<ToolContext, object> handler)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("name required", nameof(name));
            }
            Name = name;
            IsWrite = isWrite;
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public static ToolDefinition Read(string name, Func<ToolContext, object> handler) => new ToolDefinition(name, false, handler);
    }

    public sealed class ToolRegistry
    {
        /// <summary>Phase 1 is read-only: write tools cannot be registered in this build.</summary>
        public const bool WriteToolsAllowed = false;

        private readonly Dictionary<string, ToolDefinition> m_tools = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);

        public void Register(ToolDefinition tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }
            if (tool.IsWrite && !WriteToolsAllowed)
            {
                throw new InvalidOperationException($"Refusing to register write tool '{tool.Name}': this is a read-only build.");
            }
            if (m_tools.ContainsKey(tool.Name))
            {
                throw new InvalidOperationException($"Tool '{tool.Name}' is already registered.");
            }
            m_tools.Add(tool.Name, tool);
        }

        public bool TryGet(string name, out ToolDefinition tool) => m_tools.TryGetValue(name, out tool);

        public IEnumerable<string> Names => m_tools.Keys;
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
