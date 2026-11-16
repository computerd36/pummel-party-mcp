using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PummelPartyMcp.Core
{
    public sealed class RpcHttpServerOptions
    {
        /// <summary>TCP port on 127.0.0.1. 0 = pick a free port (tests).</summary>
        public int Port;
        public string Token;
        public int MaxBodyBytes = 1024 * 1024;
        public int MaxHeaderBytes = 16 * 1024;
        public int MaxConcurrentRequests = 16;

        /// <summary>
        /// Connections that found no free request slot and are answered with 503 at the same time. Beyond this the accept thread closes
        /// the connection at once, so a flood of local connections cannot occupy the thread pool (each 503 write and drain can take ~3 s).
        /// </summary>
        public int MaxBusyResponses = 4;
        /// <summary>
        /// Absolute limit for receiving the complete request (headers and body) after the connection was accepted. Not a
        /// per-read timeout: a client that sends one byte every few seconds is cut off at this deadline, so slow
        /// connections cannot hold the concurrent-request slots. Also the send timeout for responses.
        /// </summary>
        public TimeSpan IoTimeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Minimal HTTP/1.1 server for exactly one endpoint (POST /rpc), bound to 127.0.0.1 only.
    /// One request per connection (Connection: close). Implemented on TcpListener instead of HttpListener:
    /// Mono's HttpListener matches the request Host header against the prefix host (so "Host: localhost:port"
    /// would be rejected before our own checks run) and on Windows/.NET http.sys needs a URL ACL for
    /// 127.0.0.1 prefixes. A raw socket gives identical, fully controlled behaviour in the game and in tests.
    /// Request handling order: loopback peer, header parse, Origin, Host, token, path, verb, content type, body.
    /// </summary>
    public sealed class RpcHttpServer : IDisposable
    {
        public const string RpcPath = "/rpc";

        private readonly RpcHttpServerOptions m_options;
        private readonly Func<string, RpcResponse> m_handler;
        private readonly IPluginLog m_log;
        private readonly SemaphoreSlim m_slots;
        private readonly SemaphoreSlim m_busySlots;
        private TcpListener m_listener;
        private Thread m_acceptThread;
        private volatile bool m_stopping;

        public int Port { get; private set; }

        public RpcHttpServer(RpcHttpServerOptions options, Func<string, RpcResponse> handler, IPluginLog log)
        {
            m_options = options ?? throw new ArgumentNullException(nameof(options));
            m_handler = handler ?? throw new ArgumentNullException(nameof(handler));
            m_log = log ?? NullPluginLog.Instance;
            if (!TokenAuth.IsWellFormed(options.Token))
            {
                throw new ArgumentException("token is missing or malformed", nameof(options));
            }
            m_slots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRequests));
            m_busySlots = new SemaphoreSlim(Math.Max(0, options.MaxBusyResponses));
        }

        public void Start()
        {
            m_listener = new TcpListener(IPAddress.Loopback, m_options.Port);
            try
            {
                m_listener.Server.ExclusiveAddressUse = true; // no other process may share the port
            }
            catch (Exception e)
            {
                m_log.Warning("could not set ExclusiveAddressUse: " + e.Message);
            }
            m_listener.Start();
            Port = ((IPEndPoint)m_listener.LocalEndpoint).Port;
            m_acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "PummelPartyMcp.Http" };
            m_acceptThread.Start();
        }

        public void Stop()
        {
            if (m_stopping)
            {
                return;
            }
            m_stopping = true;
            try
            {
                m_listener?.Stop();
            }
            catch (Exception e)
            {
                m_log.Warning("error stopping listener: " + e.Message);
            }
            if (m_acceptThread != null && m_acceptThread != Thread.CurrentThread)
            {
                m_acceptThread.Join(2000);
            }
        }

        public void Dispose() => Stop();

        private void AcceptLoop()
        {
            while (!m_stopping)
            {
                TcpClient client;
                try
                {
                    client = m_listener.AcceptTcpClient();
                }
                catch (Exception e) when (m_stopping || e is ObjectDisposedException)
                {
                    break;
                }
                catch (Exception e)
                {
                    m_log.Warning("accept failed: " + e.Message);
                    Thread.Sleep(50);
                    continue;
                }

                bool gotSlot = m_slots.Wait(0);
                bool gotBusySlot = !gotSlot && m_busySlots.Wait(0);
                if (!gotSlot && !gotBusySlot)
                {
                    // Neither a request slot nor a 503 slot: close on the accept thread, no thread-pool work for this connection.
                    try { client.Close(); } catch { /* ignore */ }
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (gotSlot)
                        {
                            HandleConnection(client);
                        }
                        else
                        {
                            client.SendTimeout = 2000;
                            WriteResponse(client.GetStream(), 503, "SERVER_BUSY", "too many concurrent requests", null);
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.Debug("connection error: " + e.Message);
                    }
                    finally
                    {
                        if (gotSlot)
                        {
                            m_slots.Release();
                        }
                        GracefulClose(client);
                        if (gotBusySlot)
                        {
                            m_busySlots.Release(); // after the drain: bounds the thread-pool time spent on busy connections
                        }
                    }
                });
            }
        }

        /// <summary>
        /// Half-close and drain briefly so an early rejection (401/413/...) is not turned into a TCP reset
        /// by unread request bytes still in the receive buffer.
        /// </summary>
        private static void GracefulClose(TcpClient client)
        {
            try
            {
                Socket s = client.Client;
                s.Shutdown(SocketShutdown.Send);
                s.ReceiveTimeout = 500;
                byte[] sink = new byte[8192];
                int total = 0;
                DateTime deadline = DateTime.UtcNow.AddSeconds(1);
                while (total < 4 * 1024 * 1024 && DateTime.UtcNow < deadline)
                {
                    int n = s.Receive(sink);
                    if (n <= 0)
                    {
                        break;
                    }
                    total += n;
                }
            }
            catch
            {
                // peer closed or timed out
            }
            try { client.Close(); } catch { /* ignore */ }
        }

        private void HandleConnection(TcpClient client)
        {
            int timeoutMs = (int)m_options.IoTimeout.TotalMilliseconds;
            Stopwatch requestClock = Stopwatch.StartNew(); // absolute deadline for the whole request, see ReadWithDeadline
            client.ReceiveTimeout = timeoutMs;
            client.SendTimeout = timeoutMs;
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();

            IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote == null || !IPAddress.IsLoopback(remote.Address))
            {
                WriteResponse(stream, 403, "FORBIDDEN", "non-loopback peer", null);
                return;
            }

            RequestHead head;
            byte[] leftover;
            int parseStatus;
            string parseError;
            try
            {
                parseStatus = ReadHead(stream, requestClock, out head, out leftover, out parseError);
            }
            catch (IOException)
            {
                WriteResponse(stream, 408, "REQUEST_TIMEOUT", $"timed out ({m_options.IoTimeout.TotalSeconds:0.#} s total) reading request headers", null);
                return;
            }
            if (parseStatus != 0)
            {
                WriteResponse(stream, parseStatus, parseStatus == 431 ? "HEADERS_TOO_LARGE" : "BAD_REQUEST", parseError, null);
                return;
            }

            // 1. Browser requests always carry Origin (CORS / CSRF protection): reject any Origin, even "null".
            if (head.Headers.ContainsKey("Origin"))
            {
                WriteResponse(stream, 403, "FORBIDDEN", "requests with an Origin header are not accepted", null);
                return;
            }

            // 2. DNS-rebinding protection: Host must name the loopback endpoint explicitly.
            if (!head.Headers.TryGetValue("Host", out string host) || !IsAllowedHost(host))
            {
                WriteResponse(stream, 403, "FORBIDDEN", "Host header must be 127.0.0.1:<port> or localhost:<port>", null);
                return;
            }

            // 3. Shared secret.
            head.Headers.TryGetValue(TokenAuth.HeaderName, out string token);
            if (!TokenAuth.FixedTimeEquals(m_options.Token, token))
            {
                WriteResponse(stream, 401, "UNAUTHORIZED", "missing or invalid " + TokenAuth.HeaderName + " header", null);
                return;
            }

            string path = head.Target;
            int q = path.IndexOf('?');
            if (q >= 0)
            {
                path = path.Substring(0, q);
            }
            if (!string.Equals(path, RpcPath, StringComparison.Ordinal))
            {
                WriteResponse(stream, 404, "NOT_FOUND", "only POST /rpc exists", null);
                return;
            }
            if (!string.Equals(head.Method, "POST", StringComparison.Ordinal))
            {
                WriteResponse(stream, 405, "METHOD_NOT_ALLOWED", "only POST is allowed", "Allow: POST\r\n");
                return;
            }

            head.Headers.TryGetValue("Content-Type", out string contentType);
            if (!IsJsonContentType(contentType))
            {
                WriteResponse(stream, 415, "UNSUPPORTED_MEDIA_TYPE", "Content-Type must be application/json", null);
                return;
            }

            if (head.Headers.ContainsKey("Transfer-Encoding"))
            {
                WriteResponse(stream, 411, "LENGTH_REQUIRED", "chunked bodies are not supported; send Content-Length", null);
                return;
            }
            if (!head.Headers.TryGetValue("Content-Length", out string lengthText) ||
                !long.TryParse(lengthText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long length))
            {
                WriteResponse(stream, 411, "LENGTH_REQUIRED", "a valid Content-Length header is required", null);
                return;
            }
            if (length > m_options.MaxBodyBytes)
            {
                WriteResponse(stream, 413, "PAYLOAD_TOO_LARGE", $"request body exceeds {m_options.MaxBodyBytes} bytes", null);
                return;
            }

            if (head.Headers.TryGetValue("Expect", out string expect) &&
                string.Equals(expect.Trim(), "100-continue", StringComparison.OrdinalIgnoreCase))
            {
                byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                stream.Write(cont, 0, cont.Length);
            }

            byte[] body = new byte[length];
            int have = Math.Min(leftover.Length, (int)length);
            Buffer.BlockCopy(leftover, 0, body, 0, have);
            try
            {
                while (have < length)
                {
                    int n = ReadWithDeadline(stream, requestClock, body, have, (int)length - have);
                    if (n <= 0)
                    {
                        WriteResponse(stream, 400, "BAD_REQUEST", "connection closed before the full body was received", null);
                        return;
                    }
                    have += n;
                }
            }
            catch (IOException)
            {
                WriteResponse(stream, 408, "REQUEST_TIMEOUT", $"timed out ({m_options.IoTimeout.TotalSeconds:0.#} s total) reading request body", null);
                return;
            }

            string json;
            try
            {
                json = new UTF8Encoding(false, true).GetString(body);
            }
            catch (ArgumentException)
            {
                WriteResponse(stream, 400, "INVALID_PARAMS", "request body is not valid UTF-8", null);
                return;
            }

            RpcResponse response = m_handler(json);
            WriteRaw(stream, response.StatusCode, response.Json, null);
        }

        private bool IsAllowedHost(string host)
        {
            string h = host.Trim();
            string port = ":" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Equals(h, "127.0.0.1" + port, StringComparison.OrdinalIgnoreCase)
                || string.Equals(h, "localhost" + port, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsJsonContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType))
            {
                return false;
            }
            string mediaType = contentType.Split(';')[0].Trim();
            return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- parsing ----------------

        private sealed class RequestHead
        {
            public string Method;
            public string Target;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Returns 0 on success, otherwise an HTTP status (400/431).</summary>
        /// <summary>
        /// One read bounded by what is left of the request's total budget (<see cref="RpcHttpServerOptions.IoTimeout"/>
        /// since accept). Throws IOException when the budget is used up or the read times out.
        /// </summary>
        private int ReadWithDeadline(NetworkStream stream, Stopwatch clock, byte[] buffer, int offset, int count)
        {
            double left = m_options.IoTimeout.TotalMilliseconds - clock.Elapsed.TotalMilliseconds;
            if (left <= 0)
            {
                throw new IOException("request deadline exceeded");
            }
            stream.ReadTimeout = (int)Math.Max(1, Math.Ceiling(left));
            return stream.Read(buffer, offset, count);
        }

        private int ReadHead(NetworkStream stream, Stopwatch clock, out RequestHead head, out byte[] leftover, out string error)
        {
            head = null;
            leftover = new byte[0];
            error = null;
            byte[] buffer = new byte[m_options.MaxHeaderBytes];
            int filled = 0;
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                if (filled == buffer.Length)
                {
                    error = "request headers too large";
                    return 431;
                }
                int n = ReadWithDeadline(stream, clock, buffer, filled, buffer.Length - filled);
                if (n <= 0)
                {
                    error = "connection closed before headers were complete";
                    return 400;
                }
                int searchFrom = Math.Max(0, filled - 3);
                filled += n;
                for (int i = searchFrom; i + 3 < filled; i++)
                {
                    if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    {
                        headerEnd = i;
                        break;
                    }
                }
            }

            int bodyStart = headerEnd + 4;
            leftover = new byte[filled - bodyStart];
            Buffer.BlockCopy(buffer, bodyStart, leftover, 0, leftover.Length);

            string text;
            try
            {
                text = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            }
            catch (Exception)
            {
                error = "invalid header encoding";
                return 400;
            }
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal) ||
                requestLine[0].Length == 0 || requestLine[1].Length == 0)
            {
                error = "malformed request line";
                return 400;
            }
            head = new RequestHead { Method = requestLine[0], Target = requestLine[1] };
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    continue;
                }
                if (line[0] == ' ' || line[0] == '\t')
                {
                    error = "obsolete header folding is not supported";
                    return 400;
                }
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    error = "malformed header line";
                    return 400;
                }
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (head.Headers.TryGetValue(name, out string existing))
                {
                    bool mustBeUnique = name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(TokenAuth.HeaderName, StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase);
                    if (mustBeUnique)
                    {
                        error = "duplicate " + name + " header";
                        return 400;
                    }
                    head.Headers[name] = existing + ", " + value;
                }
                else
                {
                    head.Headers[name] = value;
                }
            }
            return 0;
        }

        // ---------------- responses ----------------

        private static void WriteResponse(Stream stream, int status, string code, string message, string extraHeaders)
        {
            string json = new JObject
            {
                ["ok"] = false,
                ["error"] = new JObject { ["code"] = code, ["message"] = message ?? string.Empty },
            }.ToString(Formatting.None);
            WriteRaw(stream, status, json, extraHeaders);
        }

        private static void WriteRaw(Stream stream, int status, string json, string extraHeaders)
        {
            byte[] body = new UTF8Encoding(false).GetBytes(json);
            string head = "HTTP/1.1 " + status + " " + ReasonPhrase(status) + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Cache-Control: no-store\r\n"
                + "X-Content-Type-Options: nosniff\r\n"
                + "Connection: close\r\n"
                + (extraHeaders ?? string.Empty)
                + "\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            try
            {
                stream.Write(headBytes, 0, headBytes.Length);
                stream.Write(body, 0, body.Length);
                stream.Flush();
            }
            catch (IOException)
            {
                // client went away
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 415: return "Unsupported Media Type";
                case 431: return "Request Header Fields Too Large";
                case 503: return "Service Unavailable";
                default: return "Error";
            }
        }
    }
}
