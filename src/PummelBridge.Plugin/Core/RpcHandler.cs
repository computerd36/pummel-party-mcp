using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PummelBridge.Core
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
        private readonly IBridgeLog m_log;
        private readonly TimeSpan m_mainThreadTimeout;

        public RpcHandler(ToolRegistry registry, IMainThreadDispatcher dispatcher, IBridgeLog log, TimeSpan mainThreadTimeout)
        {
            m_registry = registry;
            m_dispatcher = dispatcher;
            m_log = log ?? NullBridgeLog.Instance;
            m_mainThreadTimeout = mainThreadTimeout;
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
                return Ok(Error(ErrorCodes.UnknownMethod, $"unknown method '{Truncate(method, 100)}'"));
            }

            m_log.Debug($"rpc call: {method}");
            JObject envelope;
            try
            {
                object result = tool.Invoke(new ToolContext(method, parameters, m_dispatcher, m_mainThreadTimeout));
                JToken resultToken = result as JToken ?? (result == null ? JValue.CreateNull() : JToken.FromObject(result));
                envelope = new JObject { ["ok"] = true, ["result"] = resultToken };
            }
            catch (Exception raw)
            {
                Exception e = Unwrap(raw);
                if (e is RpcException rpc)
                {
                    if (rpc.Code == ErrorCodes.Internal || rpc.Code == ErrorCodes.Timeout)
                    {
                        m_log.Error($"rpc {method} failed: {rpc.Code}: {rpc.Message}");
                    }
                    else if (rpc.Code == ErrorCodes.GuardNotTestMod || rpc.Code == ErrorCodes.WritesDisabled || rpc.Code == ErrorCodes.PublishBlockIncomplete)
                    {
                        m_log.Info($"rpc {method} refused: {rpc.Code}: {rpc.Message}");
                    }
                    else
                    {
                        m_log.Debug($"rpc {method}: {rpc.Code}: {rpc.Message}");
                    }
                    envelope = Error(rpc.Code, rpc.Message);
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

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
