using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>Pre-release review fixes: create_object placement (A), screenshot capture queue (B), asset-reference rendering (C/D),
    /// log-safe names (H1), reserved device names (H4), write-queue visibility (H5).</summary>
    internal static class PrereleaseFixTests
    {
        private static Action<string, bool> s_check;

        private static void Check(string name, bool ok) => s_check(name, ok);

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            Console.WriteLine("-- unit: pre-release review fixes");
            Creation();
            Captures();
            AssetRefs();
            SafeNames();
            ReservedNames();
            WriteQueueVisibility();
        }

        // ------------------------------------------------------------------ A

        private static void Creation()
        {
            string why = CreationRules.Decide(true, true, true, "HierarchyRoot", out CreatePlacement p);
            Check("create: top level of an active root is direct", why == null && p == CreatePlacement.Direct);
            why = CreationRules.Decide(false, true, true, "HierarchyRoot", out p);
            Check("create: an active parent is direct (the normal case is unchanged)", why == null && p == CreatePlacement.Direct);
            why = CreationRules.Decide(false, true, false, "HierarchyRoot", out p);
            Check("create: an inactive parent builds under the creation root, then moves", why == null && p == CreatePlacement.ViaCreationRoot);
            why = CreationRules.Decide(true, false, false, "MyPrefab", out p);
            Check("create: an inactive creation root is refused before anything exists (names it, says how to fix)",
                why != null && why.Contains("'MyPrefab'") && why.Contains("set_object") && why.Contains("Nothing was created"));
            why = CreationRules.Decide(false, false, false, "MyPrefab", out p);
            Check("create: an inactive creation root is refused for a nested parent too", why != null);

            Check("create: first empty object is 'GameObject'", CreationRules.EmptyObjectName(new[] { "Floor", "GameObject" }) == "GameObject");
            Check("create: second is 'GameObject (1)'", CreationRules.EmptyObjectName(new[] { "GameObject", "GameObject" }) == "GameObject (1)");
            Check("create: counts 'GameObject (n)' names like the editor", CreationRules.EmptyObjectName(new[] { "GameObject", "GameObject (1)", "GameObject (7)", "GameObjectX", "GameObject" }) == "GameObject (3)");
            Check("create: ignores lookalikes ('GameObject (1', 'My GameObject')", CreationRules.EmptyObjectName(new[] { "GameObject (1", "My GameObject", "GameObject" }) == "GameObject");

            object a = new object(), b = new object(), c = new object();
            List<object> added = CreationRules.NewItems(new[] { a, b, a }, new[] { a, b, c, c, null });
            Check("create: the backstop finds exactly the objects this call added (deduplicated)", added.Count == 1 && ReferenceEquals(added[0], c));
            Check("create: nothing added -> nothing to remove", CreationRules.NewItems(new[] { a }, new[] { a }).Count == 0);
        }

        // ------------------------------------------------------------------ B

        private sealed class Fake : IWorkCompletion
        {
            public bool Cancelled;
            public bool Committed;
            public object Result;
            public Exception Error;
            public int Completions;
            public bool IsCancelled => Cancelled;
            public bool TryCommit() { if (Cancelled) return false; Committed = true; return true; }
            public TimeSpan Remaining => TimeSpan.MaxValue;
            public void SetResult(object result) { if (Completions++ == 0) Result = result; }
            public void SetException(Exception error) { if (Completions++ == 0) Error = error; }
        }

        private sealed class Log : IPluginLog
        {
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warning(string message) => Warnings.Add(message);
            public void Error(string message) => Errors.Add(message);
        }

        private static void Captures()
        {
            TimeSpan now = TimeSpan.Zero;
            List<string> started = new List<string>();
            string camera = "start";
            Dictionary<string, string> captured = new Dictionary<string, string>();
            List<CaptureQueue.Entry> running = new List<CaptureQueue.Entry>();
            Log log = new Log();
            CaptureQueue q = null;
            q = new CaptureQueue(e =>
            {
                string name = (string)e.State;
                if (name.StartsWith("throw", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("camera refused");
                }
                if (name.StartsWith("cam", StringComparison.Ordinal))
                {
                    if (!e.Completion.TryCommit())
                    {
                        return false; // gave up: nothing moved
                    }
                    camera = name; // the camera moves only when the entry's turn comes
                }
                if (name.StartsWith("sync", StringComparison.Ordinal))
                {
                    e.Completion.SetResult("sync");
                    q.Release(e); // released from inside start (a coroutine that ends at once)
                    started.Add(name);
                    return true;
                }
                started.Add(name);
                running.Add(e);
                return true;
            }, log, TimeSpan.FromSeconds(5), () => now);

            void Finish(CaptureQueue.Entry e)
            {
                captured[(string)e.State] = camera; // what the capture sees
                e.Completion.SetResult(camera);
                running.Remove(e);
                q.Release(e);
            }

            Fake fa = new Fake(), fb = new Fake();
            CaptureQueue.Entry ea = new CaptureQueue.Entry(fa, "camA"), eb = new CaptureQueue.Entry(fb, "camB");
            q.Enqueue(ea);
            q.Enqueue(eb);
            Check("capture: the second call waits; its camera is not applied while the first captures", started.SequenceEqual(new[] { "camA" }) && camera == "camA" && q.WaitingCount == 1);
            Finish(ea);
            Check("capture: the first call captures its own view (the original bug: it captured the second call's)", captured["camA"] == "camA" && (string)fa.Result == "camA");
            Check("capture: releasing starts the next one, which then moves the camera", started.SequenceEqual(new[] { "camA", "camB" }) && camera == "camB");
            Finish(eb);
            Check("capture: idle afterwards", q.Idle && (string)fb.Result == "camB");

            // cancelled while waiting: skipped, its camera never applied
            Fake f1 = new Fake(), f2 = new Fake(), f3 = new Fake();
            CaptureQueue.Entry e1 = new CaptureQueue.Entry(f1, "camOne"), e2 = new CaptureQueue.Entry(f2, "camTwo"), e3 = new CaptureQueue.Entry(f3, "plain");
            q.Enqueue(e1);
            q.Enqueue(e2);
            q.Enqueue(e3);
            f2.Cancelled = true;
            Finish(e1);
            Check("capture: a call whose caller gave up is skipped and moves nothing", !started.Contains("camTwo") && camera == "camOne" && started.Last() == "plain" && f2.Completions == 0);
            Finish(e3);

            // a start that throws fails only its own call; the queue goes on
            Fake ft = new Fake(), fn = new Fake();
            q.Enqueue(new CaptureQueue.Entry(ft, "throwX"));
            q.Enqueue(new CaptureQueue.Entry(fn, "plain2"));
            Check("capture: a throwing start fails its call and the next one starts", ft.Error is InvalidOperationException && started.Last() == "plain2");
            Finish(running.Single());

            // released synchronously inside start
            Fake fs = new Fake(), fafter = new Fake();
            q.Enqueue(new CaptureQueue.Entry(fs, "sync1"));
            q.Enqueue(new CaptureQueue.Entry(fafter, "plain3"));
            Check("capture: a capture that releases inside start does not stall the queue", (string)fs.Result == "sync" && started.Last() == "plain3");
            Finish(running.Single());

            // liveness: a capture that never finishes (coroutine host destroyed, frames never come) is taken back after the bound
            Fake stuck = new Fake(), behind = new Fake();
            CaptureQueue.Entry es = new CaptureQueue.Entry(stuck, "camStuck");
            q.Enqueue(es);
            q.Enqueue(new CaptureQueue.Entry(behind, "camBehind"));
            for (int i = 0; i < 30; i++)
            {
                now += TimeSpan.FromSeconds(0.1);
                q.FrameTick();
            }
            Check("capture: many frames but under the time bound keep the slot", q.Busy && !started.Contains("camBehind"));
            now += TimeSpan.FromSeconds(2.5);
            q.FrameTick();
            Check("capture: after the bound the slot is taken back, the stuck call fails with TIMEOUT, the next call runs",
                stuck.Error is RpcException re && re.Code == ErrorCodes.Timeout && started.Last() == "camBehind" && log.Warnings.Count == 1);
            CaptureQueue.Entry eBehind = running.Last();
            running.Remove(es);
            q.Release(es); // the stuck coroutine's late release
            Check("capture: a late release of the taken-back call does not free the running call's slot", q.Busy && running.Contains(eBehind));
            Finish(eBehind);
            Check("capture: the late result of the stuck call is ignored (first completion wins)", stuck.Completions == 1 && q.Idle);

            // review point 4: one very long frame (a stall over the time bound) must not reclaim a capture that finishes in that frame
            Fake stuck2 = new Fake(), behind2 = new Fake();
            CaptureQueue q2 = null;
            List<string> started2 = new List<string>();
            q2 = new CaptureQueue(e => { started2.Add((string)e.State); return true; }, log, TimeSpan.FromSeconds(5), () => now);
            q2.Enqueue(new CaptureQueue.Entry(stuck2, "a"));
            q2.Enqueue(new CaptureQueue.Entry(behind2, "b"));
            now += TimeSpan.FromSeconds(8);
            q2.Pump(); // e.g. another call arrives during the stall
            q2.FrameTick(); // the stalled frame ends: the capture would finish in it
            Check("capture: after a single stalled frame (8 s) the slot is kept", q2.Busy && started2.Count == 1 && stuck2.Completions == 0);
            for (int i = 0; i < CaptureQueue.MinFramesBeforeReclaim - 2; i++)
            {
                q2.FrameTick();
            }
            Check("capture: still kept one frame before the frame minimum", started2.Count == 1);
            q2.FrameTick();
            Check("capture: reclaimed once both the time bound and the frame minimum are reached", started2.Count == 2 && stuck2.Error is RpcException);
        }

        // ------------------------------------------------------------------ C / D

        private static void AssetRefs()
        {
            const string real = "11111111-2222-3333-4444-555555555555";
            const string other = "aaaaaaaa-2222-3333-4444-555555555555";
            AssetRefDecision d = AssetRefRules.Decide(true, real, true, true, true, null, null);
            Check("asset ref: the registered instance renders as that asset", d.Shape == AssetRefShape.Asset);
            d = AssetRefRules.Decide(true, "99999999-0000-0000-0000-000000000000", false, false, true, null, null);
            Check("asset ref: a default instance ('DefaultObject', unregistered guid) with empty guid fields is null, not a fake asset", d.Shape == AssetRefShape.Null);
            d = AssetRefRules.Decide(true, "99999999-0000-0000-0000-000000000000", false, false, true, real, null);
            Check("asset ref: an unregistered instance falls back to the reference's guid (cur first)", d.Shape == AssetRefShape.ByGuid && d.Guid == real);
            d = AssetRefRules.Decide(true, "99999999-0000-0000-0000-000000000000", false, false, true, null, other);
            Check("asset ref: then the stored guid", d.Shape == AssetRefShape.ByGuid && d.Guid == other);
            d = AssetRefRules.Decide(true, real, false, true, true, null, null);
            Check("asset ref: a copy of a registered asset (same guid, other instance) still renders by its guid: a reference scan never misses it",
                d.Shape == AssetRefShape.ByGuid && d.Guid == real);
            d = AssetRefRules.Decide(false, null, false, false, true, null, real);
            Check("asset ref: no instance -> the guid fields (unchanged behaviour)", d.Shape == AssetRefShape.ByGuid && d.Guid == real);
            d = AssetRefRules.Decide(false, null, false, false, true, null, null);
            Check("asset ref: nothing -> null", d.Shape == AssetRefShape.Null);
            d = AssetRefRules.Decide(true, "99999999-0000-0000-0000-000000000000", false, false, false);
            Check("asset ref: a bare asset field with an unregistered guid -> {guid, missing} (its guid is all there is)", d.Shape == AssetRefShape.ByGuid && d.Guid == "99999999-0000-0000-0000-000000000000");
            d = AssetRefRules.Decide(true, null, false, false, false);
            Check("asset ref: a bare asset field with an empty guid -> null", d.Shape == AssetRefShape.Null);

            bool incomplete = false;
            List<string> found = new List<string>();
            ReferenceScan.FindGuid(JObject.Parse("{'Prefab': {'type': 'X', 'error': 'member ModAssetReference`1.m_asset not found'}}"), real, string.Empty, found, ref incomplete);
            Check("reference scan: a value that could not be read ({error}) makes the scan incomplete (remove_asset then refuses without force)", incomplete && found.Count == 0);
        }

        // ------------------------------------------------------------------ H1

        private static void SafeNames()
        {
            Check("safe text: plain names are unchanged", SafeText.Name("MCP_Arena 2") == "MCP_Arena 2");
            string forged = SafeText.Name("x\r\n[Error  : PummelPartyMcp] forged\t\u007f");
            Check("safe text: CR, LF, TAB and DEL are escaped (no forged log lines)", forged == "x\\u000d\\u000a[Error  : PummelPartyMcp] forged\\u0009\\u007f");
            string longName = SafeText.Name(new string('a', 500));
            Check("safe text: names are capped at 80 characters", longName.Length == 83 && longName.EndsWith("...", StringComparison.Ordinal));
            Check("safe text: null", SafeText.Name(null) == "null" && SafeText.Escape(null) == null);
            Check("safe text: Escape keeps the length of clean text", SafeText.Escape(new string('b', 300)).Length == 300);

            WriteGuard g = WriteGuard.Lifecycle(a => (string)a);
            EditorSnapshot none = new EditorSnapshot { EditorOpen = true, IsEditing = true, StateName = "Editing" };
            string evil = "Real\r\n[Info   : PummelPartyMcp] rpc save_mod ok" + new string('x', 5000);
            RpcException e = WriteGuardEvaluator.Evaluate(new WriteSafety(() => true, () => true, () => GameVersionGate.AlwaysTested(), new NoHost()), g, evil, none);
            Check("lifecycle refusal: the caller's name is escaped and capped in the message (and so in the log line)",
                e?.Code == ErrorCodes.GuardNotTestMod && !e.Message.Any(c => c < 0x20 || c == 0x7F) && e.Message.Contains("Real\\u000d\\u000a") && e.Message.Length < 250);

            ToolRegistry reg = new ToolRegistry();
            reg.Register(ToolDefinition.Read("refuse", ctx => throw new RpcException(ErrorCodes.GuardNotTestMod, "'a\nb' refused")));
            List<string> infos = new List<string>();
            RpcHandler h = new RpcHandler(reg, null, new InfoLog(infos), TimeSpan.FromSeconds(1));
            RpcResponse r = h.Handle("{\"method\":\"refuse\",\"params\":{}}");
            Check("rpc log: a refusal's message is logged with control characters escaped (the reply keeps the text)",
                infos.Count == 1 && infos[0].Contains("'a\\u000ab'") && !infos[0].Contains("\n") && r.Json.Contains("a\\nb"));
            RpcResponse u = h.Handle("{\"method\":\"" + "x\\r\\n" + new string('y', 300) + "\",\"params\":{}}");
            JObject uj = JObject.Parse(u.Json);
            string um = (string)uj["error"]["message"];
            Check("unknown method: the echoed name is escaped and capped", (string)uj["error"]["code"] == ErrorCodes.UnknownMethod && !um.Any(c => c < 0x20) && um.Length < 120);
        }

        private sealed class NoHost : IEditorGuardHost
        {
            public EditorSnapshot Capture() => new EditorSnapshot();
            public void EnforceDefaultWorkshopId() { }
        }

        private sealed class InfoLog : IPluginLog
        {
            private readonly List<string> m_infos;
            public InfoLog(List<string> infos) { m_infos = infos; }
            public void Debug(string message) { }
            public void Info(string message) => m_infos.Add(message);
            public void Warning(string message) { }
            public void Error(string message) { }
        }

        // ------------------------------------------------------------------ H4

        private static void ReservedNames()
        {
            Check("reserved: COM0 and LPT0", AssetImportRules.IsReservedDeviceName("COM0") && AssetImportRules.IsReservedDeviceName("lpt0.png"));
            Check("reserved: COM¹ COM² COM³ LPT¹ LPT² LPT³ (superscript digits)",
                new[] { "COM¹", "com².wav", "COM³", "LPT¹", "lpt².jpg", "LPT³ " }.All(AssetImportRules.IsReservedDeviceName));
            Check("reserved: still COM1 / NUL / CON.txt; not COM10, COMX, Console", AssetImportRules.IsReservedDeviceName("COM1") && AssetImportRules.IsReservedDeviceName("nul")
                && AssetImportRules.IsReservedDeviceName("CON.txt") && !AssetImportRules.IsReservedDeviceName("COM10") && !AssetImportRules.IsReservedDeviceName("COMX")
                && !AssetImportRules.IsReservedDeviceName("Console") && !AssetImportRules.IsReservedDeviceName("COM⁴"));
        }

        // ------------------------------------------------------------------ H5

        private static void WriteQueueVisibility()
        {
            TimeSpan now = TimeSpan.Zero;
            Log log = new Log();
            WriteQueue q = new WriteQueue(log, () => now);
            Check("activeWrite: null while idle", q.ActiveName == null && !q.ActiveAge.HasValue);
            TimeSpan deadline = TimeSpan.FromSeconds(15);
            DeadlineCompletion c = new DeadlineCompletion(() => deadline - now);
            IWorkCompletion held = null;
            q.Enqueue("set_property", c, comp => { held = comp; comp.TryCommit(); });
            now += TimeSpan.FromSeconds(3);
            Check("activeWrite: tool name and age", q.ActiveName == "set_property" && Math.Abs(q.ActiveAge.Value.TotalSeconds - 3) < 1e-9);
            now = deadline + WriteQueue.OverdueAfterDeadline - TimeSpan.FromSeconds(0.5);
            q.Pump();
            Check("activeWrite: no error before deadline + commit grace + stuck grace", log.Errors.Count == 0);
            now += TimeSpan.FromSeconds(1);
            q.Pump();
            q.Pump();
            Check("activeWrite: one error once it is older than that (logged once, nothing released)",
                log.Errors.Count == 1 && log.Errors[0].Contains("set_property") && q.IsBusy);
            held.SetResult("done");
            Check("activeWrite: null again after it completed", q.ActiveName == null && !q.ActiveAge.HasValue);
        }

        private sealed class DeadlineCompletion : IWorkCompletion
        {
            private readonly Func<TimeSpan> m_left;
            private bool m_committed;
            public DeadlineCompletion(Func<TimeSpan> left) { m_left = left; }
            public bool IsCancelled => false;
            public bool TryCommit() { m_committed = true; return true; }
            public TimeSpan Remaining => m_left();
            public void SetResult(object result) { }
            public void SetException(Exception error) { }
            public bool Committed => m_committed;
        }
    }
}
