using System;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    public struct RpcResponse
    {
        public int StatusCode;
        public string Json;

        public RpcResponse(int statusCode, string json)
        {
            StatusCode = statusCode;
            Json = json;
        }
    }

    /// <summary>Parses the {"method","params"} envelope, runs the tool and builds the {"ok",...} envelope.</summary>
    public sealed class RpcHandler
    {
        private static readonly JsonSerializerSettings s_outSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            FloatFormatHandling = FloatFormatHandling.String,
            ReferenceLoopHandling = ReferenceLoopHandling.Error,
        };

        private readonly ToolRegistry m_registry;
        private readonly IMainThreadDispatcher m_dispatcher;
        private readonly IPluginLog m_log;
        private readonly TimeSpan m_mainThreadTimeout;
        private readonly Func<string, RpcException> m_gate;

        /// <param name="gate">
        /// Optional per-call gate (any thread): returns the refusal for a known method, or null to run it. The plugin passes the
        /// editor session's <see cref="SessionGate"/>.
        /// </param>
        public RpcHandler(ToolRegistry registry, IMainThreadDispatcher dispatcher, IPluginLog log, TimeSpan mainThreadTimeout,
            Func<string, RpcException> gate = null)
        {
            m_registry = registry;
            m_dispatcher = dispatcher;
            m_log = log ?? NullPluginLog.Instance;
            m_mainThreadTimeout = mainThreadTimeout;
            m_gate = gate;
        }

        public RpcResponse Handle(string body)
        {
            JObject request;
            try
            {
                using (JsonTextReader reader = new JsonTextReader(new StringReader(body ?? string.Empty)))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.MaxDepth = 64;
                    JToken token = JToken.ReadFrom(reader);
                    if (reader.Read())
                    {
                        return BadRequest("request body has trailing content after the JSON value");
                    }
                    request = token as JObject;
                }
            }
            catch (JsonException e)
            {
                return BadRequest("malformed JSON: " + e.Message);
            }
            if (request == null)
            {
                return BadRequest("request body must be a JSON object");
            }

            JToken methodToken = request["method"];
            if (methodToken == null || methodToken.Type != JTokenType.String || string.IsNullOrEmpty((string)methodToken))
            {
                return BadRequest("'method' must be a non-empty string");
            }
            string method = (string)methodToken;

            JToken paramsToken = request["params"];
            JObject parameters;
            if (paramsToken == null || paramsToken.Type == JTokenType.Null)
            {
                parameters = new JObject();
            }
            else if (paramsToken is JObject obj)
            {
                parameters = obj;
            }
            else
            {
                return BadRequest("'params' must be an object");
            }

            if (!m_registry.TryGet(method, out RegisteredTool tool))
            {
                return Ok(Error(ErrorCodes.UnknownMethod, $"unknown method '{SafeText.Name(method)}'"));
            }

            m_log.Debug($"rpc call: {method}");
            JObject envelope;
            try
            {
                RpcException refused;
                try
                {
                    refused = m_gate?.Invoke(method);
                }
                catch (Exception e)
                {
                    refused = RpcException.Internal("the session gate failed (" + e.GetType().Name + "); the call was not run"); // fail closed
                }
                if (refused != null)
                {
                    throw refused;
                }
                object result = tool.Invoke(new ToolContext(method, parameters, m_dispatcher, m_mainThreadTimeout));
                JToken resultToken = result as JToken ?? (result == null ? JValue.CreateNull() : JToken.FromObject(result));
                envelope = new JObject { ["ok"] = true, ["result"] = resultToken };
            }
            catch (Exception raw)
            {
                Exception e = Unwrap(raw);
                if (e is RpcException rpc)
                {
                    // messages can echo caller input: control characters are escaped in the log line (the reply is unchanged)
                    if (rpc.Code == ErrorCodes.Internal || rpc.Code == ErrorCodes.Timeout || rpc.Code == ErrorCodes.EditorClosedDuringCall)
                    {
                        m_log.Error($"rpc {method} failed: {rpc.Code}: {SafeText.Escape(rpc.Message)}");
                    }
                    else if (rpc.Code == ErrorCodes.GuardNotTestMod || rpc.Code == ErrorCodes.WritesDisabled || rpc.Code == ErrorCodes.PublishBlockIncomplete)
                    {
                        m_log.Info($"rpc {method} refused: {rpc.Code}: {SafeText.Escape(rpc.Message)}");
                    }
                    else
                    {
                        m_log.Debug($"rpc {method}: {rpc.Code}: {SafeText.Escape(rpc.Message)}");
                    }
                    envelope = Error(rpc.Code, rpc.Message);
                    if (rpc.ErrorData != null)
                    {
                        try
                        {
                            ((JObject)envelope["error"])["data"] = rpc.ErrorData as JToken ?? JToken.FromObject(rpc.ErrorData);
                        }
                        catch (Exception de)
                        {
                            m_log.Warning($"rpc {method}: error data could not be serialised: {de.Message}");
                        }
                    }
                }
                else
                {
                    m_log.Error($"rpc {method} threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    envelope = Error(ErrorCodes.Internal, $"{e.GetType().Name}: {e.Message}");
                }
            }

            try
            {
                return new RpcResponse(200, JsonConvert.SerializeObject(envelope, s_outSettings));
            }
            catch (Exception e)
            {
                m_log.Error($"rpc {method}: failed to serialize result: {e}");
                return Ok(Error(ErrorCodes.Internal, "failed to serialize result: " + e.Message));
            }
        }

        public static JObject Error(string code, string message)
        {
            return new JObject
            {
                ["ok"] = false,
                ["error"] = new JObject { ["code"] = code, ["message"] = message ?? string.Empty },
            };
        }

        private static RpcResponse Ok(JObject envelope) => new RpcResponse(200, envelope.ToString(Formatting.None));

        private static RpcResponse BadRequest(string message) =>
            new RpcResponse(400, Error(ErrorCodes.InvalidParams, message).ToString(Formatting.None));

        private static Exception Unwrap(Exception e)
        {
            while ((e is TargetInvocationException || e is AggregateException) && e.InnerException != null)
            {
                e = e.InnerException;
            }
            return e;
        }
    }

    /// <summary>
    /// Caller-supplied text in log lines and error messages: control characters (below 0x20, and DEL) are escaped as \uXXXX so a
    /// caller cannot forge or split log lines, and names are capped so a caller cannot bloat the log.
    /// </summary>
    public static class SafeText
    {
        public const int MaxNameLength = 80;

        /// <summary>A caller-supplied name: escaped and cut to <paramref name="max"/> characters (then "...").</summary>
        public static string Name(string s, int max = MaxNameLength)
        {
            if (s == null)
            {
                return "null";
            }
            return Escape(s.Length <= max ? s : s.Substring(0, max) + "...");
        }

        /// <summary>Escapes control characters only (no length cap).</summary>
        public static string Escape(string s)
        {
            if (s == null)
            {
                return null;
            }
            bool clean = true;
            foreach (char c in s)
            {
                if (c < 0x20 || c == 0x7F)
                {
                    clean = false;
                    break;
                }
            }
            if (clean)
            {
                return s;
            }
            StringBuilder sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                if (c < 0x20 || c == 0x7F)
                {
                    sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
