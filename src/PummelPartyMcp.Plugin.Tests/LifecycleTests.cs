using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>
    /// Decision P2 (the plugin is active only while the Mod Editor is open): the lifecycle state machine, the per-session tool
    /// gate, and the helpers that end an editor session (dispatcher shutdown and abandon, write queue reset, poller and capture
    /// queue clear). Design tests U1-U10.
    /// </summary>
    internal static class LifecycleTests
    {
        private static Action<string, bool> Check;

        public static void Run(Action<string, bool> check)
        {
            Check = check;
            Console.WriteLine("-- unit: editor lifecycle (P2)");
            StateMachine();
            Gate();
            WriteQueueEnd();
            PollerAndCapture();
            DispatcherEnd();
        }

        private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

        private static readonly DateTime Utc = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

        private static void StateMachine()
        {
            EditorLifecycle l = new EditorLifecycle();
            Check("U0 starts dormant, no session yet", l.State == LifecycleState.Dormant && l.SessionsThisRun == 0 && l.ArmedUtc == null && l.StateName == "dormant");

            // U1
            Check("U1 dormant + MainMenu scene (no editor) -> no action",
                l.OnSceneLoaded("MainMenu", false, S(1)) == LifecycleAction.None && l.State == LifecycleState.Dormant);
            Check("U1 dormant + a match scene -> no action", l.OnSceneLoaded("PirateMap_Scene", false, S(1)) == LifecycleAction.None);
            Check("U1 dormant frames do nothing", l.OnFrame(false, S(2), true) == LifecycleAction.None && l.State == LifecycleState.Dormant);
            Check("U1 the scene name compare is exact (case and spelling)", !EditorLifecycle.IsEditorScene("modeditorscene") && !EditorLifecycle.IsEditorScene("ModEditorEmptyScene")
                && !EditorLifecycle.IsEditorScene(null) && EditorLifecycle.IsEditorScene("ModEditorScene"));

            // U2
            Check("U2 dormant + ModEditorScene -> Arm", l.OnSceneLoaded("ModEditorScene", false, S(3)) == LifecycleAction.Arm && l.Arming);
            Check("U2 a second scene load while arming -> no second Arm", l.OnSceneLoaded("ModEditorScene", true, S(3)) == LifecycleAction.None);
            l.Armed(true, false, S(3), Utc);
            Check("U2 armed -> active, one session, armedUtc set", l.State == LifecycleState.Active && !l.Arming && l.SessionsThisRun == 1 && l.ArmedUtc == Utc && l.StateName == "active");
            Check("U2 a later editor scene load while active -> no second Arm", l.OnSceneLoaded("ModEditorScene", true, S(3.1)) == LifecycleAction.None && l.SessionsThisRun == 1);

            // U4: the editor stays alive through playtest scene loads
            Check("U4 frames with the editor present -> no change", l.OnFrame(true, S(4), false) == LifecycleAction.None && l.State == LifecycleState.Active);
            bool playtest = true;
            foreach (string scene in new[] { "PirateMap_Scene", "MinigameScene", "ModEditorEmptyScene", "MainMenu" })
            {
                playtest &= l.OnSceneLoaded(scene, true, S(5)) == LifecycleAction.None && l.OnFrame(true, S(5), false) == LifecycleAction.None;
            }
            Check("U4 playtest scene loads (board, minigame, empty editor scene) with the editor alive -> stays active", playtest && l.State == LifecycleState.Active);
            Check("Q9 the editor object surviving into the main menu keeps the plugin armed", l.OnFrame(true, S(100), false) == LifecycleAction.None && l.State == LifecycleState.Active);

            // U5
            Check("U5 editor gone after it was seen -> BeginDisarm once", l.OnFrame(false, S(101), false) == LifecycleAction.BeginDisarm && l.State == LifecycleState.Disarming
                && l.DisarmReason.Contains("closed"));
            Check("U5 a disarming frame with work still running -> keep pumping", l.OnFrame(false, S(101.1), false) == LifecycleAction.None);
            Check("U5 work idle -> FinishDisarm", l.OnFrame(false, S(101.2), true) == LifecycleAction.FinishDisarm);
            Check("U5 disarmed -> dormant, no re-arm", l.Disarmed(false) == LifecycleAction.None && l.State == LifecycleState.Dormant);
            Check("U5 dormant again: frames and non-editor scenes do nothing", l.OnFrame(false, S(102), true) == LifecycleAction.None
                && l.OnSceneLoaded("MainMenu", false, S(102)) == LifecycleAction.None);

            // grace bound: time AND frames
            EditorLifecycle g = ArmedAt(S(0));
            g.OnFrame(true, S(1), false);
            g.OnFrame(false, S(2), false);
            bool early = true;
            for (int i = 1; i < EditorLifecycle.MinGraceFrames; i++)
            {
                early &= g.OnFrame(false, S(2) + EditorLifecycle.DisarmGrace + S(1), false) == LifecycleAction.None;
            }
            Check("grace: one long frame past the grace time does not finish before the minimum frame count", early);
            Check("grace: past the grace time and the frame count -> FinishDisarm even with work running",
                g.OnFrame(false, S(2) + EditorLifecycle.DisarmGrace + S(1), false) == LifecycleAction.FinishDisarm);
            EditorLifecycle g2 = ArmedAt(S(0));
            g2.OnFrame(true, S(1), false);
            g2.OnFrame(false, S(2), false);
            bool waits = true;
            for (int i = 0; i < 100; i++)
            {
                waits &= g2.OnFrame(false, S(2) + TimeSpan.FromMilliseconds(i * 10), false) == LifecycleAction.None;
            }
            Check("grace: many short frames within the grace time keep waiting for running work", waits);

            // U6 (fail closed: no time-out)
            EditorLifecycle n = ArmedAt(S(10));
            bool waitsForEditor = true;
            foreach (double t in new[] { 10.1, 20, 60, 3600 })
            {
                waitsForEditor &= n.OnFrame(false, S(t), false) == LifecycleAction.None;
            }
            Check("U6 editor never seen: no disarm by time, however long (the block stays for a late editor)", waitsForEditor && n.State == LifecycleState.Active && !n.SeenEditor);
            Check("U6 never seen + the editor scene loads again without an editor -> still armed", n.OnSceneLoaded("ModEditorScene", false, S(3601)) == LifecycleAction.None);
            Check("U6 a late editor (no new scene load) is seen and then a normal close disarms", n.OnFrame(true, S(3602), false) == LifecycleAction.None && n.SeenEditor
                && n.OnFrame(false, S(3603), false) == LifecycleAction.BeginDisarm);
            EditorLifecycle m = ArmedAt(S(0));
            Check("U6 never seen + another scene loads with no editor -> BeginDisarm", m.OnSceneLoaded("MainMenu", false, S(5)) == LifecycleAction.BeginDisarm
                && m.State == LifecycleState.Disarming && m.DisarmReason.Contains("never appeared"));
            EditorLifecycle m2 = ArmedAt(S(0));
            Check("U6 never seen + another scene loads while an editor object exists -> seen, stays armed",
                m2.OnSceneLoaded("MainMenu", true, S(5)) == LifecycleAction.None && m2.State == LifecycleState.Active && m2.SeenEditor);
            EditorLifecycle m3 = ArmedAt(S(0));
            m3.OnFrame(true, S(1), false);
            Check("U6 seen editor + a scene load without it: the scene load does not disarm, the next frame does",
                m3.OnSceneLoaded("MainMenu", false, S(2)) == LifecycleAction.None && m3.OnFrame(false, S(2.1), false) == LifecycleAction.BeginDisarm);
            EditorLifecycle seenAtArm = new EditorLifecycle();
            seenAtArm.OnSceneLoaded("ModEditorScene", true, S(0));
            seenAtArm.Armed(true, true, S(0), Utc);
            Check("U6 editor present at arm counts as seen: gone on the next frame -> BeginDisarm", seenAtArm.OnFrame(false, S(0.1), false) == LifecycleAction.BeginDisarm);
            EditorLifecycle seenByScene = ArmedAt(S(0));
            seenByScene.OnSceneLoaded("ModEditorEmptyScene", true, S(0.5));
            Check("U6 editor seen in a scene load counts as seen", seenByScene.OnFrame(false, S(0.6), false) == LifecycleAction.BeginDisarm);

            // U3
            EditorLifecycle f = new EditorLifecycle();
            Check("U3 dormant + any scene while the editor object exists -> Arm (fallback for a renamed scene)",
                f.OnSceneLoaded("SomeRenamedEditorScene", true, S(0)) == LifecycleAction.Arm);

            // U7
            f.Armed(false, true, S(0), Utc);
            Check("U7 arm failure -> failed (tools off), still counted as a session", f.State == LifecycleState.Failed && f.StateName == "failed" && f.SessionsThisRun == 1);
            Check("U7 failed + editor present -> stays failed", f.OnFrame(true, S(1), false) == LifecycleAction.None && f.State == LifecycleState.Failed);
            Check("U7 failed + editor gone -> BeginDisarm", f.OnFrame(false, S(2), false) == LifecycleAction.BeginDisarm);
            f.OnFrame(false, S(2.1), true);
            Check("U7 ... -> dormant", f.Disarmed(false) == LifecycleAction.None && f.State == LifecycleState.Dormant);
            EditorLifecycle ab = new EditorLifecycle();
            ab.OnSceneLoaded("ModEditorScene", false, S(0));
            ab.ArmAborted(false, S(0), Utc);
            Check("U7 arming that threw -> failed, not stuck in arming", ab.State == LifecycleState.Failed && !ab.Arming);

            // U8
            EditorLifecycle r = ArmedAt(S(0));
            r.OnFrame(true, S(1), false);
            r.OnFrame(false, S(2), false);
            Check("U8 ModEditorScene while disarming -> no arm yet", r.OnSceneLoaded("ModEditorScene", false, S(2.1)) == LifecycleAction.None && r.State == LifecycleState.Disarming);
            r.OnFrame(false, S(2.2), true);
            Check("U8 ... -> Arm right after dormant is reached", r.Disarmed(false) == LifecycleAction.Arm && r.Arming && r.State == LifecycleState.Dormant);
            r.Armed(true, true, S(3), Utc.AddMinutes(1));
            Check("U8 second session: active, counted, new armedUtc", r.State == LifecycleState.Active && r.SessionsThisRun == 2 && r.ArmedUtc == Utc.AddMinutes(1));
            Check("U8 second session: the seen flag starts fresh from the arm", r.OnFrame(false, S(3.1), false) == LifecycleAction.BeginDisarm);
            Check("U8 an editor coming up during the grace -> re-arm after the disarm", r.OnFrame(true, S(3.2), false) == LifecycleAction.None
                && r.OnFrame(false, S(3.3), true) == LifecycleAction.FinishDisarm && r.Disarmed(false) == LifecycleAction.Arm);
            EditorLifecycle e = ArmedAt(S(0));
            e.OnFrame(true, S(1), false);
            e.OnFrame(false, S(2), false);
            e.OnFrame(false, S(2.1), true);
            Check("U8 an editor that exists when the disarm ends -> re-arm", e.Disarmed(true) == LifecycleAction.Arm);

            // T4 offline: ten sessions behave like the first
            EditorLifecycle c = new EditorLifecycle();
            bool cycles = true;
            for (int i = 0; i < 10; i++)
            {
                TimeSpan t = S(i * 100);
                cycles &= c.OnSceneLoaded("ModEditorScene", false, t) == LifecycleAction.Arm;
                c.Armed(true, false, t, Utc);
                cycles &= c.OnFrame(true, t + S(1), false) == LifecycleAction.None;
                cycles &= c.OnFrame(false, t + S(2), false) == LifecycleAction.BeginDisarm;
                cycles &= c.OnFrame(false, t + S(2.1), true) == LifecycleAction.FinishDisarm;
                cycles &= c.Disarmed(false) == LifecycleAction.None && c.State == LifecycleState.Dormant;
                cycles &= c.OnSceneLoaded("MainMenu", false, t + S(3)) == LifecycleAction.None;
            }
            Check("ten editor sessions in a row: each arms once and disarms once", cycles && c.SessionsThisRun == 10);
        }

        private static EditorLifecycle ArmedAt(TimeSpan t)
        {
            EditorLifecycle l = new EditorLifecycle();
            l.OnSceneLoaded(EditorLifecycle.EditorSceneName, false, t);
            l.Armed(true, false, t, Utc);
            return l;
        }

        private static void Gate()
        {
            Check("gate: enabled -> every tool runs", SessionGate.Check(ToolAccess.Enabled, "set_property", null) == null && SessionGate.Check(ToolAccess.Enabled, "run_batch", null) == null);
            bool diag = SessionGate.DiagnosticTools.All(t => SessionGate.Check(ToolAccess.BlockIncomplete, t, "x") == null
                && SessionGate.Check(ToolAccess.Starting, t, null) == null && SessionGate.Check(ToolAccess.Stopping, t, null) == null);
            Check("gate: ping, get_status, get_log, self_test answer in every state", diag && SessionGate.DiagnosticTools.Length == 4);
            string[] others = { "get_open_mod", "get_hierarchy", "list_mods", "set_property", "open_test_mod", "create_test_mod", "save_mod", "run_batch",
                "start_playtest", "screenshot", "get_playtest_events", "import_asset", "capture_preview_image", "undo" };
            Check("gate: block incomplete -> every editor or mod tool refused with PUBLISH_BLOCK_INCOMPLETE",
                others.All(t => SessionGate.Check(ToolAccess.BlockIncomplete, t, "A.B()")?.Code == ErrorCodes.PublishBlockIncomplete));
            RpcException blocked = SessionGate.Check(ToolAccess.BlockIncomplete, "list_mods", "WorkshopPopup.PublishMod(PummelMod): not found");
            Check("gate: the refusal names what is missing and the tools that still answer",
                blocked.Message.Contains("WorkshopPopup.PublishMod") && blocked.Message.Contains("get_status") && blocked.Message.Contains("Nothing was run"));
            Check("gate: starting -> EDITOR_NOT_OPEN, retry", SessionGate.Check(ToolAccess.Starting, "list_mods", null)?.Code == ErrorCodes.EditorNotOpen);
            RpcException closing = SessionGate.Check(ToolAccess.Stopping, "set_property", null);
            Check("gate: stopping -> EDITOR_NOT_OPEN, nothing changed", closing?.Code == ErrorCodes.EditorNotOpen && closing.Message.Contains("nothing was changed"));
            Check("gate: a method name is matched exactly", SessionGate.Check(ToolAccess.BlockIncomplete, "PING", null) != null && SessionGate.Check(ToolAccess.BlockIncomplete, "get_status ", null) != null);
            Check("gate: a call cut off while running has its own code (EDITOR_CLOSED_DURING_CALL), not 'nothing changed'",
                !SessionGate.EditorClosedWhileRunning().Message.Contains("nothing was changed") && SessionGate.EditorClosedWhileRunning().Code == ErrorCodes.EditorClosedDuringCall
                && SessionGate.EditorClosedWhileRunning().Message.Contains("do not retry blindly") && ErrorCodes.EditorClosedDuringCall == "EDITOR_CLOSED_DURING_CALL");
            Check("gate: editor unreadable at arm -> diagnostics only", SessionGate.DiagnosticTools.All(t => SessionGate.Check(ToolAccess.EditorUnreadable, t, null) == null)
                && SessionGate.Check(ToolAccess.EditorUnreadable, "list_mods", null)?.Code == ErrorCodes.Internal
                && SessionGate.Check(ToolAccess.EditorUnreadable, "set_property", null).Message.Contains("publish block is installed"));
            string docs = FindUp(System.IO.Path.Combine("docs", "plugin-api.md"));
            string client = FindUp(System.IO.Path.Combine("src", "PummelPartyMcp.Server", "src", "client.ts"));
            Check("docs and the MCP client know EDITOR_CLOSED_DURING_CALL (documented code, client hint)",
                docs != null && client != null && System.IO.File.ReadAllText(docs).Contains("`EDITOR_CLOSED_DURING_CALL`")
                && System.IO.File.ReadAllText(client).Contains("EDITOR_CLOSED_DURING_CALL:"));

            // the gate in front of the registry (RpcHandler)
            ToolRegistry registry = new ToolRegistry();
            int runs = 0;
            registry.Register(ToolDefinition.Read("ping", ctx => { runs++; return "pong"; }));
            registry.Register(ToolDefinition.Read("list_mods", ctx => { runs++; return "mods"; }));
            ToolAccess access = ToolAccess.BlockIncomplete;
            RpcHandler handler = new RpcHandler(registry, null, null, S(1), m => SessionGate.Check(access, m, "X.Y()"));
            JObject refused = JObject.Parse(handler.Handle("{\"method\":\"list_mods\"}").Json);
            Check("handler gate: a refused tool returns the gate's error and never runs",
                (bool)refused["ok"] == false && (string)refused["error"]["code"] == ErrorCodes.PublishBlockIncomplete && runs == 0);
            JObject ping = JObject.Parse(handler.Handle("{\"method\":\"ping\"}").Json);
            Check("handler gate: a diagnostic tool runs", (bool)ping["ok"] && (string)ping["result"] == "pong" && runs == 1);
            JObject unknown = JObject.Parse(handler.Handle("{\"method\":\"nope\"}").Json);
            Check("handler gate: an unknown method stays UNKNOWN_METHOD", (string)unknown["error"]["code"] == ErrorCodes.UnknownMethod);
            access = ToolAccess.Enabled;
            Check("handler gate: read live per call (enabled -> runs)", (bool)JObject.Parse(handler.Handle("{\"method\":\"list_mods\"}").Json)["ok"] && runs == 2);
            RpcHandler broken = new RpcHandler(registry, null, null, S(1), m => throw new InvalidOperationException("bug"));
            JObject failClosed = JObject.Parse(broken.Handle("{\"method\":\"list_mods\"}").Json);
            Check("handler gate: a throwing gate refuses the call (fail closed)", (bool)failClosed["ok"] == false && runs == 2);
        }

        private sealed class Completion : IWorkCompletion
        {
            public bool Cancelled;
            public object Result;
            public Exception Error;
            public int Completions;
            public bool IsCancelled => Cancelled;
            public bool TryCommit() => !Cancelled;
            public TimeSpan Remaining => TimeSpan.MaxValue;
            public void SetResult(object result) { Result = result; Completions++; }
            public void SetException(Exception error) { Error = error; Completions++; }
        }

        private static void WriteQueueEnd()
        {
            // U9
            WriteQueue q = new WriteQueue();
            IWorkCompletion heldA = null;
            int bRuns = 0, cRuns = 0;
            Completion a = new Completion(), b = new Completion(), c = new Completion(), cancelled = new Completion { Cancelled = true };
            q.Enqueue("A", a, comp => heldA = comp);
            q.Enqueue("B", b, comp => bRuns++);
            q.Enqueue("X", cancelled, comp => bRuns++);
            q.Enqueue("C", c, comp => cRuns++);
            int failed = q.FailPending(SessionGate.EditorClosed);
            Check("U9 FailPending: queued entries get EDITOR_NOT_OPEN and never run", failed == 2 && bRuns == 0 && cRuns == 0
                && (b.Error as RpcException)?.Code == ErrorCodes.EditorNotOpen && (c.Error as RpcException)?.Code == ErrorCodes.EditorNotOpen && q.PendingCount == 0);
            Check("U9 FailPending: a caller that already gave up is not completed again", cancelled.Completions == 0);
            Check("U9 FailPending: each caller gets its own exception object", !ReferenceEquals(b.Error, c.Error));
            Check("U9 FailPending: the active entry is untouched", q.IsBusy && a.Completions == 0);
            heldA.SetResult("done");
            Check("U9 the active entry completes normally afterwards", (string)a.Result == "done" && !q.IsBusy);

            WriteQueue r = new WriteQueue();
            IWorkCompletion heldR = null;
            Completion r1 = new Completion(), r2 = new Completion();
            int r2Runs = 0;
            r.Enqueue("R1", r1, comp => heldR = comp);
            r.Enqueue("R2", r2, comp => r2Runs++);
            int n = r.Reset(SessionGate.EditorClosed, SessionGate.EditorClosedWhileRunning);
            Check("Reset: the waiting entry fails as not run, the active one as running", n == 2 && r2Runs == 0
                && r2.Error.Message.Contains("not run") && r1.Error.Message.Contains("while the call was running"));
            Check("Reset: the queue is free and empty", !r.IsBusy && r.PendingCount == 0);
            heldR.SetResult("late");
            Check("Reset: a late completion of the released body is ignored", r1.Completions == 1 && r1.Result == null);
            Completion next = new Completion();
            r.Enqueue("N", next, comp => comp.SetResult(1));
            Check("Reset: the queue works in the next session", (int)next.Result == 1 && !r.IsBusy);
            Check("Reset: an idle queue fails nothing", new WriteQueue().Reset(SessionGate.EditorClosed, SessionGate.EditorClosedWhileRunning) == 0);
        }

        private static void PollerAndCapture()
        {
            // U10
            FramePoller p = new FramePoller(null);
            int ticks = 0;
            p.Add(() => { ticks++; return false; });
            p.Add(() => { ticks++; return false; });
            p.Tick();
            Check("U10 FramePoller.Clear drops every callback without running it", p.Clear() == 2 && p.Count == 0 && ticks == 2);
            p.Tick();
            Check("U10 nothing runs after Clear", ticks == 2);
            p.Add(() => { ticks++; return true; });
            p.Tick();
            Check("U10 the poller works in the next session", ticks == 3 && p.Count == 0);

            List<string> started = new List<string>();
            CaptureQueue cq = null;
            cq = new CaptureQueue(e => { started.Add((string)e.State); return true; });
            Completion c1 = new Completion(), c2 = new Completion();
            CaptureQueue.Entry e1 = new CaptureQueue.Entry(c1, "one");
            cq.Enqueue(e1);
            cq.Enqueue(new CaptureQueue.Entry(c2, "two"));
            int dropped = cq.FailAll(SessionGate.EditorClosedWhileRunning);
            Check("capture queue FailAll: the running and the waiting capture fail, nothing new starts",
                dropped == 2 && started.SequenceEqual(new[] { "one" }) && c1.Error != null && c2.Error != null && cq.Idle);
            cq.Release(e1);
            Check("capture queue FailAll: a late release of the dropped capture is ignored", cq.Idle && started.Count == 1);
            Completion c3 = new Completion();
            CaptureQueue.Entry e3 = new CaptureQueue.Entry(c3, "three");
            cq.Enqueue(e3);
            Check("capture queue: works in the next session", started.Last() == "three" && cq.Busy);
            Completion c4 = new Completion(), c5 = new Completion();
            cq.Enqueue(new CaptureQueue.Entry(c4, "four"));
            cq.Enqueue(new CaptureQueue.Entry(c5, "five"));
            int waitingFailed = cq.FailWaiting(SessionGate.EditorClosed);
            Check("capture queue FailWaiting (disarm start): waiting captures fail with EDITOR_NOT_OPEN, the running one is untouched",
                waitingFailed == 2 && (c4.Error as RpcException)?.Code == ErrorCodes.EditorNotOpen && (c5.Error as RpcException)?.Code == ErrorCodes.EditorNotOpen
                && c3.Completions == 0 && cq.Busy && cq.WaitingCount == 0);
            cq.Release(e3);
            Check("capture queue FailWaiting: nothing starts after the running capture ends", started.Last() == "three" && cq.Idle);
        }

        private static void DispatcherEnd()
        {
            MainThreadDispatcher d = new MainThreadDispatcher(S(0.2));
            // a deferred call (completes in a later frame) is running when the editor closes
            IWorkCompletion held = null;
            Task<object> running = Task.Run(() => d.Invoke(c => held = c, S(5)));
            Task<object> queued = null;
            bool drained = SpinUntil(() => { d.Drain(16, 8); return held != null; });
            Check("dispatcher: deferred work counts as running", drained && d.RunningCount == 1);
            queued = Task.Run(() => d.Invoke(c => c.SetResult("ran"), S(5)));
            SpinUntil(() => d.PendingCount == 1);
            d.Shutdown(SessionGate.EditorClosed);
            Exception qe = Catch(queued);
            Check("dispatcher Shutdown(error): a queued call fails with that error and never runs", (qe as RpcException)?.Code == ErrorCodes.EditorNotOpen
                && qe.Message.Contains("not run"));
            Exception late = Catch(Task.Run(() => d.Invoke(c => c.SetResult("x"), S(5))));
            Check("dispatcher Shutdown(error): a new call fails at once with the same code", (late as RpcException)?.Code == ErrorCodes.EditorNotOpen);
            Check("dispatcher: Drain after shutdown runs nothing", d.Drain(16, 8) == 0);
            Check("dispatcher: running work still counts after shutdown (grace)", d.RunningCount == 1 && !running.IsCompleted);
            int abandoned = d.AbandonRunning(SessionGate.EditorClosedWhileRunning);
            Exception re = Catch(running);
            Check("dispatcher AbandonRunning: the waiting caller is released with the 'while running' error",
                abandoned == 1 && (re as RpcException)?.Code == ErrorCodes.EditorClosedDuringCall && re.Message.Contains("while the call was running"));
            held.SetResult("too late");
            Check("dispatcher: a late result of abandoned work is ignored", d.RunningCount == 0 && d.AbandonRunning(SessionGate.EditorClosed) == 0);

            MainThreadDispatcher d2 = new MainThreadDispatcher();
            IWorkCompletion held2 = null;
            Task<object> normal = Task.Run(() => d2.Invoke(c => held2 = c, S(5)));
            SpinUntil(() => { d2.Drain(16, 8); return held2 != null; });
            d2.Shutdown(SessionGate.EditorClosed);
            held2.SetResult("own result");
            Check("dispatcher: running work that finishes in the grace returns its own result", normal.Wait(5000) && (string)normal.Result == "own result" && d2.RunningCount == 0);

            MainThreadDispatcher d3 = new MainThreadDispatcher();
            Task<object> sync = Task.Run(() => d3.Invoke(c => c.SetResult(1), S(5)));
            SpinUntil(() => { d3.Drain(16, 8); return sync.IsCompleted; });
            Check("dispatcher: synchronous work is not tracked as running", d3.RunningCount == 0);
            d3.Shutdown();
            Exception legacy = Catch(Task.Run(() => d3.Invoke(c => c.SetResult(1), S(5))));
            Check("dispatcher: Shutdown() without an error keeps the old INTERNAL text", (legacy as RpcException)?.Code == ErrorCodes.Internal);
        }

        private static string FindUp(string relative)
        {
            for (System.IO.DirectoryInfo d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string p = System.IO.Path.Combine(d.FullName, relative);
                if (System.IO.File.Exists(p))
                {
                    return p;
                }
            }
            return null;
        }

        private static bool SpinUntil(Func<bool> condition)
        {
            for (int i = 0; i < 500; i++)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(5);
            }
            return false;
        }

        private static Exception Catch(Task<object> task)
        {
            try
            {
                task.Wait(5000);
                return null;
            }
            catch (AggregateException e)
            {
                return e.InnerException;
            }
        }
    }
}
