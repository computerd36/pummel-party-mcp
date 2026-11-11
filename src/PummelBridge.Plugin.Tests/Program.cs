using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;

namespace PummelBridge.Tests
{
    /// <summary>
    /// Starts the real BridgeHttpServer + RpcHandler + MainThreadDispatcher on a free loopback port with a fake
    /// "main thread" (a pump thread that can be paused) and checks the HTTP/auth/dispatch contract with raw
    /// sockets, so every header (Host, Origin, Content-Length, ...) is under test control.
    /// </summary>
    internal static class Program
    {
        private static int s_passed;
        private static int s_failed;
        private static readonly List<string> s_failures = new List<string>();

        private static string s_token;
        private static int s_port;
        private static volatile bool s_pump = true;
        private static int s_mainThreadExecutions;
        private static int s_mainThreadId;

        private static int Main()
        {
            Console.WriteLine("PummelBridge core test harness");
            UnitTests();
            GuardTests.Run((name, ok) => Check(name, ok));
            EditCoreTests.Run((name, ok) => Check(name, ok));
            AssetListingTests();
            ServerTests();
            Console.WriteLine();
            Console.WriteLine($"RESULT: {s_passed} passed, {s_failed} failed");
            foreach (string f in s_failures)
            {
                Console.WriteLine("  FAILED: " + f);
            }
            return s_failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ unit tests

        private static void UnitTests()
        {
            Console.WriteLine("-- unit: token, registry, ring buffer");
            string dir = Path.Combine(Path.GetTempPath(), "pb-test-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "PummelBridge.token");
            try
            {
                string t1 = TokenAuth.LoadOrCreate(path, out bool created1);
                Check("token created on first start (64 lowercase hex)", created1 && TokenAuth.IsWellFormed(t1) && File.ReadAllText(path) == t1);
                string t2 = TokenAuth.LoadOrCreate(path, out bool created2);
                Check("token reused on later start", !created2 && t2 == t1);
                File.WriteAllText(path, "garbage");
                string t3 = TokenAuth.LoadOrCreate(path, out bool created3);
                Check("malformed token file is regenerated", created3 && t3 != t1 && TokenAuth.IsWellFormed(t3));
                Check("two generated tokens differ", TokenAuth.Generate() != TokenAuth.Generate());
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            string tok = TokenAuth.Generate();
            Check("FixedTimeEquals: equal", TokenAuth.FixedTimeEquals(tok, tok));
            Check("FixedTimeEquals: different/shorter/longer/null", !TokenAuth.FixedTimeEquals(tok, tok.Substring(0, 63) + (tok[63] == 'a' ? 'b' : 'a'))
                && !TokenAuth.FixedTimeEquals(tok, tok.Substring(0, 10)) && !TokenAuth.FixedTimeEquals(tok, tok + "0") && !TokenAuth.FixedTimeEquals(tok, null));

            LogRingBuffer ring = new LogRingBuffer(5);
            for (int i = 1; i <= 8; i++)
            {
                ring.Add("info", "m" + i, null);
            }
            LogQueryResult q1 = ring.Query(200, null);
            Check("ring keeps newest N (ids 4..8)", q1.Entries.Select(e => e.Id).SequenceEqual(new long[] { 4, 5, 6, 7, 8 }) && q1.NextId == 8);
            LogQueryResult q2 = ring.Query(2, 5);
            Check("ring sinceId returns oldest-first after id", q2.Entries.Select(e => e.Id).SequenceEqual(new long[] { 6, 7 }) && q2.NextId == 7 && !q2.Dropped);
            LogQueryResult q3 = ring.Query(10, 1);
            Check("ring reports dropped entries", q3.Dropped && q3.Entries.First().Id == 4);
            LogQueryResult q4 = ring.Query(10, 8);
            Check("ring sinceId at head returns nothing", q4.Entries.Count == 0 && q4.NextId == 8);
        }

        // ------------------------------------------------------------------ asset listing

        private static AssetEntry Asset(string guid, string name, string type, string cls = null)
        {
            return new AssetEntry
            {
                Guid = guid, Name = name, Type = type, TypeAliases = new[] { cls ?? "ModAsset", "" },
                Row = new JObject { ["guid"] = guid, ["name"] = name, ["type"] = type },
            };
        }

        private static bool Throws(Action a)
        {
            try { a(); return false; }
            catch (RpcException e) { return e.Code == ErrorCodes.InvalidParams; }
        }

        private static void AssetListingTests()
        {
            Console.WriteLine("-- unit: asset listing");
            List<AssetEntry> all = new List<AssetEntry>
            {
                Asset("g3", "Wooden Crate", "Prop"),
                Asset("g1", "wooden barrel", "Prop"),
                Asset("g2", "Wooden Crate", "Prop"),
                Asset("g4", "Stone", "Material"),
                Asset("g5", "Crate Texture", "Texture2D", "PummelTexture"),
                Asset("g6", "Boom", "AudioClip"),
            };
            Func<JObject, string> names = r => string.Join(",", r["assets"].Select(a => (string)a["guid"]));

            JObject r0 = AssetListing.Query(all, null, null, 100, 0);
            Check("order: type, then name, then guid", names(r0) == "g6,g4,g1,g2,g3,g5");
            Check("result shape", (int)r0["total"] == 6 && (int)r0["returned"] == 6 && (int)r0["limit"] == 100
                && (int)r0["offset"] == 0 && !(bool)r0["hasMore"]);
            Check("typeCounts", (int)r0["typeCounts"]["Prop"] == 3 && (int)r0["typeCounts"]["Material"] == 1 && ((JObject)r0["typeCounts"]).Count == 4);

            JObject rs = AssetListing.Query(all, null, "  CRATE wood ", 100, 0);
            Check("search: all terms, case-insensitive", names(rs) == "g2,g3" && (int)rs["total"] == 2);
            Check("search: no match -> empty", (int)AssetListing.Query(all, null, "zzz", 10, 0)["total"] == 0);
            Check("blank search ignored", (int)AssetListing.Query(all, null, "   ", 10, 0)["total"] == 6);

            JObject rt = AssetListing.Query(all, "prop", "crate", 100, 0);
            Check("type filter case-insensitive; typeCounts ignore type filter",
                names(rt) == "g2,g3" && (int)rt["total"] == 2 && (int)rt["typeCounts"]["Texture2D"] == 1 && (int)rt["typeCounts"]["Prop"] == 2);
            Check("type filter matches class-name alias", names(AssetListing.Query(all, "pummeltexture", null, 10, 0)) == "g5");
            Check("type filter is exact, not substring", (int)AssetListing.Query(all, "Pro", null, 10, 0)["total"] == 0);

            JObject p1 = AssetListing.Query(all, null, null, 2, 0);
            Check("paging first page hasMore", names(p1) == "g6,g4" && (bool)p1["hasMore"] && (int)p1["returned"] == 2 && (int)p1["total"] == 6);
            JObject p3 = AssetListing.Query(all, null, null, 2, 4);
            Check("paging last page", names(p3) == "g3,g5" && !(bool)p3["hasMore"]);
            JObject p4 = AssetListing.Query(all, null, null, 4, 4);
            Check("short last page returned", (int)p4["returned"] == 2 && !(bool)p4["hasMore"]);
            JObject pe = AssetListing.Query(all, null, null, 2, 6);
            Check("offset == total -> empty, no more", (int)pe["returned"] == 0 && !(bool)pe["hasMore"] && (int)pe["total"] == 6);
            JObject pf = AssetListing.Query(all, null, null, 2, 100);
            Check("offset beyond total -> empty", (int)pf["returned"] == 0 && !(bool)pf["hasMore"]);
            Check("limit 0 / 501 / offset -1 -> INVALID_PARAMS",
                Throws(() => AssetListing.Query(all, null, null, 0, 0)) && Throws(() => AssetListing.Query(all, null, null, 501, 0))
                && Throws(() => AssetListing.Query(all, null, null, 10, -1)));
            Check("limit 500 accepted", (int)AssetListing.Query(all, null, null, 500, 0)["limit"] == 500);

            ToolContext bad = new ToolContext("list_assets", new JObject { ["limit"] = 501 }, null, TimeSpan.FromSeconds(1));
            Check("ctx limit 501 -> INVALID_PARAMS", Throws(() => bad.GetInt("limit", 100, 1, AssetListing.MaxLimit)));
            ToolContext bad2 = new ToolContext("list_assets", new JObject { ["offset"] = -1 }, null, TimeSpan.FromSeconds(1));
            Check("ctx offset -1 -> INVALID_PARAMS", Throws(() => bad2.GetInt("offset", 0, 0, int.MaxValue)));
        }

        // ------------------------------------------------------------------ server tests

        private static void ServerTests()
        {
            s_token = TokenAuth.Generate();
            MainThreadDispatcher dispatcher = new MainThreadDispatcher();
            Thread pump = new Thread(() =>
            {
                s_mainThreadId = Environment.CurrentManagedThreadId;
                while (true)
                {
                    if (s_pump)
                    {
                        dispatcher.Drain(16, 8);
                    }
                    Thread.Sleep(5);
                }
            }) { IsBackground = true, Name = "fake-main-thread" };
            pump.Start();

            ToolRegistry registry = new ToolRegistry();
            registry.Register(ToolDefinition.Read("ping", ctx => new JObject { ["pong"] = true, ["apiVersion"] = 1 }));
            registry.Register(ToolDefinition.Read("echo_main", ctx =>
            {
                int n = ctx.GetInt("n", 0, 0, 1000);
                return ctx.OnMainThread(() =>
                {
                    Interlocked.Increment(ref s_mainThreadExecutions);
                    return new JObject { ["n"] = n, ["onMainThread"] = Environment.CurrentManagedThreadId == s_mainThreadId };
                });
            }));
            registry.Register(ToolDefinition.Read("throws", ctx => ctx.OnMainThread<object>(() => throw new InvalidOperationException("boom"))));
            registry.Register(ToolDefinition.Read("not_found", ctx => throw RpcException.NotFound("nothing here")));

            RpcHandler handler = new RpcHandler(registry, dispatcher, NullBridgeLog.Instance, TimeSpan.FromSeconds(10));
            BridgeHttpServer server = new BridgeHttpServer(new BridgeHttpServerOptions { Port = 0, Token = s_token }, handler.Handle, NullBridgeLog.Instance);
            server.Start();
            s_port = server.Port;
            Console.WriteLine($"-- server on 127.0.0.1:{s_port}");

            string ping = "{\"method\":\"ping\",\"params\":{}}";

            // auth / origin / host
            Resp r = Send("POST", "/rpc", ping, token: null);
            Check("401 without token", r.Status == 401 && r.Code == "UNAUTHORIZED", r);
            r = Send("POST", "/rpc", ping, token: new string('0', 64));
            Check("401 with wrong token (same length)", r.Status == 401, r);
            r = Send("POST", "/rpc", ping, token: "abc");
            Check("401 with wrong token (short)", r.Status == 401, r);
            r = Send("POST", "/rpc", ping, extraHeaders: "Origin: http://evil.example\r\n");
            Check("403 with Origin header (valid token)", r.Status == 403, r);
            r = Send("POST", "/rpc", ping, extraHeaders: "Origin: null\r\n");
            Check("403 with Origin: null", r.Status == 403, r);
            r = Send("POST", "/rpc", ping, host: "evil.example:" + s_port);
            Check("403 with foreign Host", r.Status == 403, r);
            r = Send("POST", "/rpc", ping, host: "127.0.0.1:" + (s_port == 65535 ? 1 : s_port + 1));
            Check("403 with Host on another port", r.Status == 403, r);
            r = Send("POST", "/rpc", ping, host: "127.0.0.1");
            Check("403 with Host without port", r.Status == 403, r);
            r = Send("POST", "/rpc", ping, host: "");
            Check("403 with missing Host", r.Status == 403, r);
            r = Send("POST", "/rpc", ping, host: "localhost:" + s_port);
            Check("200 with Host localhost:<port>", r.Status == 200 && r.Ok == true, r);
            r = Send("POST", "/rpc", ping, extraHeaders: "Host: 127.0.0.1:" + s_port + "\r\n");
            Check("400 with duplicate Host header", r.Status == 400, r);

            // routing
            r = Send("GET", "/rpc", null);
            Check("405 for GET /rpc", r.Status == 405 && r.RawHead.Contains("Allow: POST"), r);
            r = Send("PUT", "/rpc", ping);
            Check("405 for PUT /rpc", r.Status == 405, r);
            r = Send("POST", "/other", ping);
            Check("404 for POST /other", r.Status == 404, r);
            r = Send("GET", "/", null);
            Check("404 for GET /", r.Status == 404, r);
            r = Send("POST", "/other", ping, token: null);
            Check("401 (not 404) for unknown path without token", r.Status == 401, r);
            r = Send("POST", "/rpc", ping, contentType: "text/plain");
            Check("415 for text/plain", r.Status == 415, r);
            r = Send("POST", "/rpc", ping, contentType: "application/json; charset=utf-8");
            Check("200 for application/json; charset=utf-8", r.Status == 200 && r.Ok == true, r);

            // body limits
            r = SendRaw(BuildHead("POST", "/rpc", "application/json", 1024 * 1024 + 1, s_token, "127.0.0.1:" + s_port, null), new byte[100]);
            Check("413 when Content-Length > 1 MB", r.Status == 413, r);
            string big = "{\"method\":\"ping\",\"params\":{\"pad\":\"" + new string('x', 1024 * 1024 + 10) + "\"}}";
            r = Send("POST", "/rpc", big);
            Check("413 when a >1 MB body is actually sent", r.Status == 413, r);
            string exact = "{\"method\":\"ping\",\"params\":{}}";
            exact += new string(' ', 1024 * 1024 - Encoding.UTF8.GetByteCount(exact));
            r = Send("POST", "/rpc", exact);
            Check("200 for a body of exactly 1 MB", r.Status == 200 && r.Ok == true, r);
            r = SendRaw(Encoding.ASCII.GetBytes("POST /rpc HTTP/1.1\r\nHost: 127.0.0.1:" + s_port + "\r\n" + TokenAuth.HeaderName + ": " + s_token + "\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n"), Encoding.ASCII.GetBytes("0\r\n\r\n"));
            Check("411 for chunked body", r.Status == 411, r);
            r = Send("POST", "/rpc", ping, extraHeaders: "X-Pad: " + new string('a', 20000) + "\r\n");
            Check("431 for oversized headers", r.Status == 431, r);

            // envelope
            r = Send("POST", "/rpc", "{not json");
            Check("400 INVALID_PARAMS for malformed JSON", r.Status == 400 && r.Code == ErrorCodes.InvalidParams, r);
            r = Send("POST", "/rpc", "[1,2]");
            Check("400 for non-object body", r.Status == 400, r);
            r = Send("POST", "/rpc", "{\"params\":{}}");
            Check("400 for missing method", r.Status == 400, r);
            r = Send("POST", "/rpc", "{\"method\":\"ping\",\"params\":[1]}");
            Check("400 for non-object params", r.Status == 400, r);
            r = Send("POST", "/rpc", "{\"method\":\"does_not_exist\",\"params\":{}}");
            Check("200 UNKNOWN_METHOD", r.Status == 200 && r.Ok == false && r.Code == ErrorCodes.UnknownMethod, r);
            r = Send("POST", "/rpc", ping);
            Check("ping round-trip", r.Status == 200 && r.Ok == true && (bool?)r.Json?["result"]?["pong"] == true, r);
            r = Send("POST", "/rpc", "{\"method\":\"ping\"}");
            Check("ping without params", r.Status == 200 && r.Ok == true, r);
            r = Send("POST", "/rpc", "{\"method\":\"echo_main\",\"params\":{\"n\":7}}");
            Check("main-thread dispatch round-trip", r.Status == 200 && r.Ok == true && (int?)r.Json?["result"]?["n"] == 7
                && (bool?)r.Json?["result"]?["onMainThread"] == true, r);
            r = Send("POST", "/rpc", "{\"method\":\"echo_main\",\"params\":{\"n\":\"seven\"}}");
            Check("INVALID_PARAMS for wrong param type", r.Status == 200 && r.Code == ErrorCodes.InvalidParams, r);
            r = Send("POST", "/rpc", "{\"method\":\"throws\",\"params\":{}}");
            Check("tool exception -> INTERNAL with message", r.Status == 200 && r.Code == ErrorCodes.Internal && r.Message.Contains("boom"), r);
            r = Send("POST", "/rpc", "{\"method\":\"not_found\",\"params\":{}}");
            Check("RpcException code passes through (NOT_FOUND)", r.Status == 200 && r.Code == ErrorCodes.NotFound, r);

            // concurrency
            Resp[] parallel = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8)
                .Select(i => Send("POST", "/rpc", "{\"method\":\"echo_main\",\"params\":{\"n\":" + i + "}}")).ToArray();
            Check("8 concurrent main-thread calls succeed", parallel.All(p => p.Status == 200 && p.Ok == true));

            // timeout: main thread never services the call
            s_pump = false;
            int before = Volatile.Read(ref s_mainThreadExecutions);
            Stopwatch sw = Stopwatch.StartNew();
            r = Send("POST", "/rpc", "{\"method\":\"echo_main\",\"params\":{\"n\":1}}", readTimeoutMs: 30000);
            sw.Stop();
            Check($"TIMEOUT when the dispatcher never services the call (took {sw.Elapsed.TotalSeconds:0.0} s)",
                r.Status == 200 && r.Code == ErrorCodes.Timeout && sw.Elapsed.TotalSeconds >= 9.5 && sw.Elapsed.TotalSeconds < 15, r);
            Resp duringStall = Send("POST", "/rpc", ping);
            Check("ping still answers while the main thread is stalled", duringStall.Status == 200 && duringStall.Ok == true, duringStall);
            s_pump = true;
            Thread.Sleep(300);
            Check("timed-out work item is discarded, not run later", Volatile.Read(ref s_mainThreadExecutions) == before);
            r = Send("POST", "/rpc", "{\"method\":\"echo_main\",\"params\":{\"n\":2}}");
            Check("dispatch works again after a timeout", r.Status == 200 && r.Ok == true, r);

            server.Stop();
            bool refusedAfterStop;
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    c.Connect("127.0.0.1", s_port);
                }
                refusedAfterStop = false;
            }
            catch (SocketException)
            {
                refusedAfterStop = true;
            }
            Check("listener closed after Stop()", refusedAfterStop);

            dispatcher.Shutdown();
            bool shutdownRejects = false;
            try
            {
                dispatcher.Invoke(c => c.SetResult(1), TimeSpan.FromSeconds(1));
            }
            catch (RpcException e) when (e.Code == ErrorCodes.Internal)
            {
                shutdownRejects = true;
            }
            Check("dispatcher rejects work after Shutdown()", shutdownRejects);
        }

        // ------------------------------------------------------------------ helpers

        private sealed class Resp
        {
            public int Status;
            public string RawHead = "";
            public string Body = "";
            public JObject Json;
            public bool? Ok => Json?["ok"]?.Type == JTokenType.Boolean ? (bool)Json["ok"] : (bool?)null;
            public string Code => (string)Json?["error"]?["code"];
            public string Message => (string)Json?["error"]?["message"] ?? "";
            public override string ToString() => $"HTTP {Status} {Truncate(Body, 200)}";
        }

        private static Resp Send(string method, string path, string body, string token = "__default__", string host = null,
            string contentType = "application/json", string extraHeaders = null, int readTimeoutMs = 15000)
        {
            if (token == "__default__")
            {
                token = s_token;
            }
            byte[] bodyBytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            byte[] head = BuildHead(method, path, contentType, body == null ? -1 : bodyBytes.Length, token, host ?? "127.0.0.1:" + s_port, extraHeaders);
            return SendRaw(head, bodyBytes, readTimeoutMs);
        }

        private static byte[] BuildHead(string method, string path, string contentType, long contentLength, string token, string host, string extraHeaders)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            if (host.Length > 0)
            {
                sb.Append("Host: ").Append(host).Append("\r\n");
            }
            if (token != null)
            {
                sb.Append(TokenAuth.HeaderName).Append(": ").Append(token).Append("\r\n");
            }
            if (contentType != null && contentLength >= 0)
            {
                sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            }
            if (contentLength >= 0)
            {
                sb.Append("Content-Length: ").Append(contentLength).Append("\r\n");
            }
            sb.Append(extraHeaders ?? "");
            sb.Append("\r\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static Resp SendRaw(byte[] head, byte[] body, int readTimeoutMs = 15000)
        {
            Resp resp = new Resp();
            using (TcpClient client = new TcpClient())
            {
                client.Connect("127.0.0.1", s_port);
                client.ReceiveTimeout = readTimeoutMs;
                client.SendTimeout = 15000;
                NetworkStream stream = client.GetStream();
                try
                {
                    stream.Write(head, 0, head.Length);
                    if (body != null && body.Length > 0)
                    {
                        stream.Write(body, 0, body.Length);
                    }
                }
                catch (IOException)
                {
                    // server may reject early and stop reading; still read its response below
                }
                MemoryStream ms = new MemoryStream();
                byte[] buf = new byte[65536];
                try
                {
                    int n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                    }
                }
                catch (IOException)
                {
                }
                string text = Encoding.UTF8.GetString(ms.ToArray());
                int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (split < 0)
                {
                    resp.Body = text;
                    return resp;
                }
                resp.RawHead = text.Substring(0, split);
                resp.Body = text.Substring(split + 4);
                string[] statusLine = resp.RawHead.Split("\r\n")[0].Split(' ');
                if (statusLine.Length >= 2)
                {
                    int.TryParse(statusLine[1], out resp.Status);
                }
                try
                {
                    resp.Json = JObject.Parse(resp.Body);
                }
                catch
                {
                    resp.Json = null;
                }
            }
            return resp;
        }

        private static void Check(string name, bool ok, Resp detail = null)
        {
            if (ok)
            {
                s_passed++;
                Console.WriteLine("  PASS  " + name);
            }
            else
            {
                s_failed++;
                string msg = name + (detail != null ? " -> " + detail : "");
                s_failures.Add(msg);
                Console.WriteLine("  FAIL  " + msg);
            }
        }

        private static string Truncate(string s, int max) => s == null ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");
    }
}
