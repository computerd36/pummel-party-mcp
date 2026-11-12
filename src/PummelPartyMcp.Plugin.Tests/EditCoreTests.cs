using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>Phase 2 increment 2: value parsing, edit tracking, write queue, edit pipeline, hierarchy/component rules.</summary>
    internal static class EditCoreTests
    {
        private static Action<string, bool> s_check;

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            ValueParsingTests();
            EditTrackerTests();
            WriteQueueTests();
            RegistryQueueTests();
            EditPipelineTests();
            CommitPipelineTests();
            CommitDispatchTests();
            ReferenceRuleTests();
            ReparentTests();
            ComponentRuleTests();
            KindAndClampTests();
        }

        private static void Check(string name, bool ok) => s_check(name, ok);

        private static string Code(Action a)
        {
            try
            {
                a();
                return null;
            }
            catch (RpcException e)
            {
                return e.Code;
            }
            catch (Exception e)
            {
                return "EXC:" + e.GetType().Name;
            }
        }

        private static string Msg(Action a)
        {
            try
            {
                a();
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        // ================================================================== value parsing

        private static PValue P(string json, ValueTypeDesc d) => ValueParser.Parse(json == null ? null : JToken.Parse(json), d, "prop");

        private static bool Bad(string json, ValueTypeDesc d) => Code(() => P(json, d)) == ErrorCodes.InvalidParams;

        private static void ValueParsingTests()
        {
            Console.WriteLine("-- unit: JSON -> value parsing");
            ValueTypeDesc b = ValueTypeDesc.Simple(ValueKind.Bool, "Boolean");
            Check("bool true", ((PBool)P("true", b)).Value);
            Check("bool rejects 1 / \"true\" / null", Bad("1", b) && Bad("\"true\"", b) && Bad("null", b));

            ValueTypeDesc i32 = ValueTypeDesc.Integer("Int32", int.MinValue, int.MaxValue);
            ValueTypeDesc u8 = ValueTypeDesc.Integer("Byte", 0, 255);
            ValueTypeDesc u64 = ValueTypeDesc.Integer("UInt64", 0, ulong.MaxValue);
            Check("int 42", ((PInteger)P("42", i32)).Value == 42);
            Check("int accepts integral float 3.0", ((PInteger)P("3.0", i32)).Value == 3);
            Check("int rejects 3.5 / string / bool", Bad("3.5", i32) && Bad("\"3\"", i32) && Bad("true", i32));
            Check("int range: 2147483648 refused", Bad("2147483648", i32));
            Check("byte range: 256 / -1 refused, 255 ok", Bad("256", u8) && Bad("-1", u8) && ((PInteger)P("255", u8)).Value == 255);
            Check("ulong max accepted", ((PInteger)P("18446744073709551615", u64)).Value == ulong.MaxValue);
            Check("out-of-range message names the type", Msg(() => P("300", u8)).Contains("Byte"));

            ValueTypeDesc f = ValueTypeDesc.Simple(ValueKind.Float, "Single");
            ValueTypeDesc d = ValueTypeDesc.Simple(ValueKind.Double, "Double");
            Check("float 1.5 and integer 2", ((PNumber)P("1.5", f)).Value == 1.5 && ((PNumber)P("2", f)).Value == 2);
            Check("float rejects strings incl. \"NaN\"", Bad("\"1.5\"", f) && Bad("\"NaN\"", f) && Bad("null", f));
            Check("float rejects > float.MaxValue, double accepts it", Bad("1e39", f) && ((PNumber)P("1e39", d)).Value == 1e39);

            ValueTypeDesc s = ValueTypeDesc.Simple(ValueKind.String, "String");
            Check("string ok incl. empty", ((PString)P("\"abc\"", s)).Value == "abc" && ((PString)P("\"\"", s)).Value == "");
            Check("string rejects number/null", Bad("5", s) && Bad("null", s));
            Check("string length limit", Bad("\"" + new string('x', ValueParser.MaxStringLength + 1) + "\"", s));

            ValueTypeDesc e = ValueTypeDesc.Enum("InternalLight", new[] { "Directional", "Point", "Spot" }, new long[] { 0, 1, 2 }, false);
            Check("enum by name, case-insensitive", ((PEnum)P("\"spot\"", e)).Value == 2 && ((PEnum)P("\"Point\"", e)).Value == 1);
            Check("enum by number", ((PEnum)P("1", e)).Value == 1);
            Check("enum undefined number / unknown name refused", Bad("7", e) && Bad("\"Area\"", e));
            Check("enum message lists names", Msg(() => P("\"Area\"", e)).Contains("Directional, Point, Spot"));
            Check("non-flags enum rejects 'A, B'", Bad("\"Point, Spot\"", e));
            ValueTypeDesc fl = ValueTypeDesc.Enum("Locations", new[] { "None", "OnSelf", "OnTargets", "All" }, new long[] { 0, 1, 2, 3 }, true);
            Check("flags by names 'OnSelf, OnTargets'", ((PEnum)P("\"OnSelf, OnTargets\"", fl)).Value == 3);
            Check("flags by number 2 and 0", ((PEnum)P("2", fl)).Value == 2 && ((PEnum)P("0", fl)).Value == 0);
            Check("flags undefined bit refused", Bad("4", fl));

            ValueTypeDesc v3 = ValueTypeDesc.Simple(ValueKind.Vector3, "Vector3");
            float[] vv = ((PFloats)P("{\"x\":1,\"y\":2.5,\"z\":-3}", v3)).Values;
            Check("vector3 object", vv.SequenceEqual(new[] { 1f, 2.5f, -3f }));
            Check("vector3 array form", ((PFloats)P("[1,2,3]", v3)).Values.SequenceEqual(new[] { 1f, 2f, 3f }));
            Check("vector3 missing z / extra key / wrong array length refused",
                Bad("{\"x\":1,\"y\":2}", v3) && Bad("{\"x\":1,\"y\":2,\"z\":3,\"w\":4}", v3) && Bad("[1,2]", v3) && Bad("{\"X\":1,\"y\":2,\"z\":3}", v3));
            Check("vector3 component must be a number", Bad("{\"x\":\"1\",\"y\":2,\"z\":3}", v3));
            ValueTypeDesc q = ValueTypeDesc.Simple(ValueKind.Quaternion, "Quaternion");
            Check("quaternion needs w", Bad("{\"x\":0,\"y\":0,\"z\":0}", q) && ((PFloats)P("{\"x\":0,\"y\":0,\"z\":0,\"w\":1}", q)).Values[3] == 1f);
            ValueTypeDesc v2 = ValueTypeDesc.Simple(ValueKind.Vector2, "Vector2");
            Check("vector2", ((PFloats)P("{\"x\":1,\"y\":2}", v2)).Values.Length == 2 && Bad("{\"x\":1,\"y\":2,\"z\":3}", v2));

            ValueTypeDesc c = ValueTypeDesc.Simple(ValueKind.Color, "Color");
            Check("color with alpha default 1", ((PFloats)P("{\"r\":1,\"g\":0.5,\"b\":0}", c)).Values.SequenceEqual(new[] { 1f, 0.5f, 0f, 1f }));
            Check("color missing g refused", Bad("{\"r\":1,\"b\":0}", c));
            ValueTypeDesc c32 = ValueTypeDesc.Simple(ValueKind.Color32, "Color32");
            Check("color32 ints, a defaults 255", ((PBytes)P("{\"r\":255,\"g\":0,\"b\":10}", c32)).Values.SequenceEqual(new byte[] { 255, 0, 10, 255 }));
            Check("color32 range", Bad("{\"r\":256,\"g\":0,\"b\":0}", c32));
            ValueTypeDesc rect = ValueTypeDesc.Simple(ValueKind.Rect, "Rect");
            Check("rect", ((PFloats)P("{\"x\":0,\"y\":0,\"width\":1,\"height\":2}", rect)).Values[3] == 2f && Bad("{\"x\":0,\"y\":0,\"w\":1,\"h\":2}", rect));
            ValueTypeDesc lm = ValueTypeDesc.Simple(ValueKind.LayerMask, "LayerMask");
            Check("layermask int", ((PInteger)P("-1", lm)).Value == -1 && Bad("1.5", lm));

            ValueTypeDesc r = ValueTypeDesc.Reference("Prop");
            const string g = "1D2E3F40-1111-2222-3333-444455556666";
            Check("reference: bare guid normalised", ((PRef)P("\"" + g + "\"", r)).Guid == g.ToLowerInvariant());
            Check("reference: {guid} object with extra keys (get_object shape)",
                ((PRef)P("{\"guid\":\"" + g + "\",\"name\":\"Cube\",\"assetType\":\"Prop\",\"location\":\"internal\"}", r)).Guid == g.ToLowerInvariant());
            Check("reference: null clears", ((PRef)P("null", r)).Guid == null && ((PRef)P("{\"guid\":null}", r)).Guid == null);
            Check("reference: unregistered get_object ref refused", Bad("{\"guid\":null,\"name\":\"x\",\"registered\":false}", r));
            Check("reference: malformed guid / number / object without guid refused", Bad("\"nope\"", r) && Bad("5", r) && Bad("{\"name\":\"Cube\"}", r));

            ValueTypeDesc arr = ValueTypeDesc.ArrayOf(ValueTypeDesc.Reference("Material"));
            PArray pa = (PArray)P("[null, \"" + g + "\"]", arr);
            Check("array of references", pa.Items.Count == 2 && ((PRef)pa.Items[1]).Guid != null);
            Check("array element error names the index", Msg(() => P("[\"" + g + "\", 5]", arr)).Contains("prop[1]"));
            Check("array rejects null / object / truncation marker", Bad("null", arr) && Bad("{}", arr) && Bad("[{\"truncatedItems\":3}]", arr));
            Check("array length limit", Bad("[" + string.Join(",", Enumerable.Repeat("null", ValueParser.MaxArrayLength + 1)) + "]", arr));
            ValueTypeDesc v3arr = ValueTypeDesc.ArrayOf(v3);
            Check("array of vector3", ((PArray)P("[{\"x\":0,\"y\":0,\"z\":0},[1,2,3]]", v3arr)).Items.Count == 2);

            ValueTypeDesc unsupported = ValueTypeDesc.Unsupported("ModLogicTrigger", "use the action tools");
            Check("unsupported type refused with its reason", Msg(() => P("{}", unsupported)).Contains("use the action tools"));
            Check("array of unsupported element refused", Bad("[]", ValueTypeDesc.ArrayOf(unsupported)));

            ValueTypeDesc curve = ValueTypeDesc.Simple(ValueKind.AnimationCurve, "AnimationCurve");
            PCurve pc = (PCurve)P("{\"keys\":[{\"time\":0,\"value\":0},{\"time\":1,\"value\":2,\"inTangent\":0.5,\"outTangent\":0}],\"preWrapMode\":\"clampforever\",\"postWrapMode\":\"Loop\"}", curve);
            Check("curve keys + wrap modes", pc.Keys.Count == 2 && pc.Keys[1].InTangent == 0.5f && pc.PreWrapMode == "ClampForever" && pc.PostWrapMode == "Loop");
            Check("curve bad wrap / missing value / unknown key refused",
                Bad("{\"keys\":[],\"preWrapMode\":\"Bounce\"}", curve) && Bad("{\"keys\":[{\"time\":0}]}", curve) && Bad("{\"keys\":[],\"x\":1}", curve));

            ValueTypeDesc mmc = ValueTypeDesc.Simple(ValueKind.MinMaxCurve, "MinMaxCurve");
            Check("minmaxcurve bare number = Constant", ((PMinMaxCurve)P("2.5", mmc)).Mode == "Constant" && ((PMinMaxCurve)P("2.5", mmc)).Constant == 2.5f);
            PMinMaxCurve two = (PMinMaxCurve)P("{\"mode\":\"TwoConstants\",\"constantMin\":1,\"constantMax\":3}", mmc);
            Check("minmaxcurve TwoConstants (get_object shape)", two.ConstantMin == 1f && two.ConstantMax == 3f);
            Check("minmaxcurve Curve mode", ((PMinMaxCurve)P("{\"mode\":\"Curve\",\"curveMultiplier\":2,\"curve\":{\"keys\":[{\"time\":0,\"value\":1}]}}", mmc)).Curve.Keys.Count == 1);
            Check("minmaxcurve missing fields / bad mode refused",
                Bad("{\"mode\":\"TwoConstants\",\"constantMin\":1}", mmc) && Bad("{\"mode\":\"Random\"}", mmc) && Bad("{\"constant\":1}", mmc));

            ValueTypeDesc grad = ValueTypeDesc.Simple(ValueKind.Gradient, "Gradient");
            PGradient pg = (PGradient)P("{\"mode\":\"Fixed\",\"colorKeys\":[{\"color\":{\"r\":1,\"g\":0,\"b\":0},\"time\":0}],\"alphaKeys\":[{\"alpha\":1,\"time\":1}]}", grad);
            Check("gradient", pg.Mode == "Fixed" && pg.ColorKeys[0][3] == 1f && pg.AlphaKeys[0][1] == 1f);
            Check("gradient key limits and time range",
                Bad("{\"colorKeys\":[],\"alphaKeys\":[{\"alpha\":1,\"time\":0}]}", grad) &&
                Bad("{\"colorKeys\":[{\"color\":{\"r\":1,\"g\":0,\"b\":0},\"time\":2}],\"alphaKeys\":[{\"alpha\":1,\"time\":0}]}", grad));
            ValueTypeDesc mmg = ValueTypeDesc.Simple(ValueKind.MinMaxGradient, "MinMaxGradient");
            Check("minmaxgradient bare color", ((PMinMaxGradient)P("{\"r\":0,\"g\":1,\"b\":0}", mmg)).Mode == "Color");
            Check("minmaxgradient TwoColors", ((PMinMaxGradient)P("{\"mode\":\"TwoColors\",\"colorMin\":{\"r\":0,\"g\":0,\"b\":0},\"colorMax\":{\"r\":1,\"g\":1,\"b\":1,\"a\":0.5}}", mmg)).ColorMax[3] == 0.5f);
            Check("minmaxgradient missing gradient refused", Bad("{\"mode\":\"Gradient\"}", mmg));
            Check("mismatch message names expected type", Msg(() => P("\"x\"", v3)).Contains("expected Vector3"));
        }

        // ================================================================== edit tracker

        private static UndoSignature Sig(int count, object top, params KeyValuePair<object, UndoStackState>[] prefabs)
        {
            UndoSignature s = new UndoSignature { Hierarchy = new UndoStackState(count, top) };
            foreach (KeyValuePair<object, UndoStackState> p in prefabs)
            {
                s.Prefabs[p.Key] = p.Value;
            }
            return s;
        }

        private static void EditTrackerTests()
        {
            Console.WriteLine("-- unit: edit tracker");
            object mod = new object(), e1 = new object(), e2 = new object(), e3 = new object();
            EditTracker t = new EditTracker();
            t.Observe(mod, false, Sig(0, null));
            Check("loading: no baseline yet -> null", t.HasUnsavedEdits(mod) == null);
            t.Observe(mod, true, Sig(0, null));
            Check("load complete, empty stack -> clean", t.HasUnsavedEdits(mod) == false);
            t.Observe(mod, true, Sig(1, e1));
            Check("initial snapshot push (0->1) is not an edit", t.HasUnsavedEdits(mod) == false);
            t.Observe(mod, true, Sig(1, e1));
            Check("no change -> still clean", t.HasUnsavedEdits(mod) == false);
            t.Observe(mod, true, Sig(2, e2));
            Check("push after the initial one -> dirty", t.HasUnsavedEdits(mod) == true && t.DirtyReason != null);
            t.Observe(mod, true, Sig(1, e1));
            Check("undo back to the initial state stays dirty (latched)", t.HasUnsavedEdits(mod) == true);
            t.MarkSaved(mod, Sig(1, e1));
            Check("save -> clean", t.HasUnsavedEdits(mod) == false);
            t.Observe(mod, true, Sig(1, e3));
            Check("same count, different top (undo+push in one frame) -> dirty", t.HasUnsavedEdits(mod) == true);
            t.MarkSaved(mod, Sig(1, e3));
            t.Observe(mod, true, Sig(0, null));
            t.Observe(mod, true, Sig(1, e1));
            Check("undo of the initial snapshot and re-push (1->0->1) is neutral", t.HasUnsavedEdits(mod) == false);
            t.MarkPluginEditIfChanged(mod, "set_property", false);
            Check("no-op plugin edit (changed: false) leaves the tracker clean", t.HasUnsavedEdits(mod) == false);
            t.MarkPluginEditIfChanged(mod, "set_property", true);
            Check("plugin edit that changed something -> dirty", t.HasUnsavedEdits(mod) == true);
            t.MarkSaved(mod, Sig(1, e1));
            t.MarkPluginEdit(mod, "set_property");
            Check("plugin edit -> dirty", t.HasUnsavedEdits(mod) == true && t.DirtyReason.Contains("plugin"));
            t.MarkPluginEdit(new object(), "x");
            object prefab = new object(), p1 = new object(), p2 = new object();
            t.MarkSaved(mod, Sig(1, e1));
            t.Observe(mod, true, Sig(1, e1, new KeyValuePair<object, UndoStackState>(prefab, new UndoStackState(0, null))));
            t.Observe(mod, true, Sig(1, e1, new KeyValuePair<object, UndoStackState>(prefab, new UndoStackState(1, p1))));
            Check("new prefab manager with its initial snapshot -> clean", t.HasUnsavedEdits(mod) == false);
            t.Observe(mod, true, Sig(1, e1, new KeyValuePair<object, UndoStackState>(prefab, new UndoStackState(2, p2))));
            Check("prefab stack push -> dirty", t.HasUnsavedEdits(mod) == true);

            // The plugin refills one scratch signature every frame: the tracker must not keep a reference to it.
            object smod = new object(), s1 = new object(), s2 = new object(), sp = new object(), sp1 = new object();
            EditTracker st = new EditTracker();
            UndoSignature scratch = new UndoSignature();
            Action<int, object, object, int, object> fill = (count, top, prefab, pcount, ptop) =>
            {
                scratch.Clear();
                scratch.Hierarchy = new UndoStackState(count, top);
                if (prefab != null)
                {
                    scratch.Prefabs[prefab] = new UndoStackState(pcount, ptop);
                }
            };
            fill(0, null, null, 0, null); st.Observe(smod, true, scratch);
            fill(1, s1, sp, 1, sp1); st.Observe(smod, true, scratch);
            fill(1, s1, sp, 1, sp1); st.Observe(smod, true, scratch);
            Check("refilled scratch signature, unchanged stacks -> clean", st.HasUnsavedEdits(smod) == false);
            fill(1, s2, sp, 1, sp1); st.Observe(smod, true, scratch);
            Check("refilled scratch signature, hierarchy top changed -> dirty", st.HasUnsavedEdits(smod) == true);
            scratch.Clear();
            Check("Clear() empties the signature", scratch.Hierarchy.Count == 0 && scratch.Hierarchy.Top == null && scratch.Prefabs.Count == 0);

            object mod2 = new object();
            t.Observe(mod2, true, Sig(0, null));
            Check("other mod: previous state dropped, fresh baseline", t.HasUnsavedEdits(mod2) == false && t.HasUnsavedEdits(mod) == null);
            object mod3 = new object();
            t.Observe(mod3, true, Sig(5, e1));
            Check("mod first seen with a long stack -> no baseline (fallback)", t.HasUnsavedEdits(mod3) == null && !t.HasBaseline);
            t.Observe(mod3, true, Sig(6, e2));
            Check("without baseline nothing is concluded", t.HasUnsavedEdits(mod3) == null);
            t.MarkSaved(mod3, Sig(6, e2));
            Check("a save establishes a baseline", t.HasUnsavedEdits(mod3) == false);
            t.Observe(null, false, null);
            Check("mod closed -> null", t.HasUnsavedEdits(mod3) == null);
            object mod4 = new object();
            t.Observe(mod4, true, null);
            t.Observe(mod4, true, Sig(1, e1));
            Check("unreadable signature frame is skipped, then baseline", t.HasUnsavedEdits(mod4) == false);
            Check("Changed(): 2->1 is a change, 0->1 and 1->0 are not",
                EditTracker.Changed(new UndoStackState(2, e2), new UndoStackState(1, e1)) &&
                !EditTracker.Changed(new UndoStackState(0, null), new UndoStackState(1, e1)) &&
                !EditTracker.Changed(new UndoStackState(1, e1), new UndoStackState(0, null)));
        }

        // ================================================================== write queue

        private sealed class FakeCompletion : IWorkCompletion
        {
            public bool Cancelled;
            public bool Committed;
            public object Result;
            public Exception Error;
            public int Completions;
            /// <summary>Time left until the deadline; unlimited when null.</summary>
            public Func<TimeSpan> Left;
            public bool IsCancelled => Cancelled;
            public bool TryCommit() { if (Cancelled) return false; Committed = true; return true; }
            public TimeSpan Remaining => Left?.Invoke() ?? TimeSpan.MaxValue;
            public void SetResult(object result) { Result = result; Completions++; }
            public void SetException(Exception error) { Error = error; Completions++; }
        }

        private static void WriteQueueTests()
        {
            Console.WriteLine("-- unit: write queue");
            TimeSpan now = TimeSpan.Zero;
            WriteQueue q = new WriteQueue(null, () => now);
            List<string> order = new List<string>();
            IWorkCompletion heldA = null;
            FakeCompletion a = new FakeCompletion(), b = new FakeCompletion(), c = new FakeCompletion();
            q.Enqueue("A", a, comp => { order.Add("A"); heldA = comp; });
            Check("first write starts immediately", order.SequenceEqual(new[] { "A" }) && q.IsBusy && q.ActiveName == "A");
            q.Enqueue("B", b, comp => { order.Add("B"); comp.SetResult("b"); });
            q.Enqueue("C", c, comp => { order.Add("C"); });
            Check("later writes wait while one is active", order.Count == 1 && q.PendingCount == 2);
            heldA.SetResult("a");
            Check("completion forwards and starts the next in FIFO order (B completes sync, then C)",
                (string)a.Result == "a" && (string)b.Result == "b" && order.SequenceEqual(new[] { "A", "B", "C" }) && q.ActiveName == "C");
            heldA.SetResult("again");
            Check("double completion ignored", a.Completions == 1);

            WriteQueue q2 = new WriteQueue(null, () => now);
            FakeCompletion x = new FakeCompletion(), y = new FakeCompletion(), z = new FakeCompletion();
            IWorkCompletion heldX = null;
            int yRuns = 0;
            q2.Enqueue("X", x, comp => heldX = comp);
            q2.Enqueue("Y", y, comp => yRuns++);
            q2.Enqueue("Z", z, comp => comp.SetException(new RpcException(ErrorCodes.InvalidState, "z")));
            y.Cancelled = true; // caller timed out while queued
            heldX.SetResult(1);
            Check("cancelled queued write is dropped without running", yRuns == 0 && y.Completions == 0);
            Check("next write after a dropped one runs; its error is forwarded", (z.Error as RpcException)?.Code == ErrorCodes.InvalidState && !q2.IsBusy);

            WriteQueue q3 = new WriteQueue(null, () => now);
            FakeCompletion t1 = new FakeCompletion(), t2 = new FakeCompletion();
            q3.Enqueue("T1", t1, comp => throw new InvalidOperationException("boom"));
            Check("synchronous throw completes with the exception and releases", t1.Error is InvalidOperationException && !q3.IsBusy);
            int t2Runs = 0;
            q3.Enqueue("T2", t2, comp => { t2Runs++; comp.SetResult(2); });
            Check("queue usable after a throwing body", t2Runs == 1 && (int)t2.Result == 2);

            WriteQueue q4 = new WriteQueue(null, () => now);
            FakeCompletion s1 = new FakeCompletion(), s2 = new FakeCompletion();
            int s2Runs = 0;
            q4.Enqueue("stuck", s1, comp => { });
            q4.Enqueue("next", s2, comp => { s2Runs++; comp.SetResult(0); });
            q4.Pump();
            Check("active body not cancelled is never force-released", q4.ActiveName == "stuck" && s2Runs == 0);
            s1.Cancelled = true;
            q4.Pump(); // first sight of the cancellation
            now += TimeSpan.FromSeconds(5);
            q4.Pump();
            Check("cancelled active body is kept during the grace period", q4.ActiveName == "stuck" && s2Runs == 0);
            now += WriteQueue.StuckGrace;
            q4.Pump();
            Check("cancelled active body is force-released after the grace period; next runs", s2Runs == 1 && !q4.IsBusy);

            WriteQueue q5 = new WriteQueue(null, () => now);
            FakeCompletion r1 = new FakeCompletion(), r2 = new FakeCompletion();
            List<string> nested = new List<string>();
            q5.Enqueue("outer", r1, comp =>
            {
                nested.Add("outer-start");
                q5.Enqueue("inner", r2, c2 => { nested.Add("inner"); c2.SetResult(null); });
                nested.Add("outer-end");
                comp.SetResult(null);
            });
            Check("enqueue from inside an active body waits until it completes", nested.SequenceEqual(new[] { "outer-start", "outer-end", "inner" }));
        }

        /// <summary>Two write tools through the registry: the second waits for the first's deferred completion.</summary>
        private static void RegistryQueueTests()
        {
            Console.WriteLine("-- unit: registry serialises write tools");
            ManualDispatcher d = new ManualDispatcher();
            FakeHost host = new FakeHost();
            ToolRegistry reg = new ToolRegistry(new WriteSafety(() => true, () => true, host));
            List<string> events = new List<string>();
            WriteInvocation pending = null;
            reg.Register(ToolDefinition.Write("slow", WriteGuard.OpenMod(), null, inv => { events.Add("slow-start"); pending = inv; }));
            reg.Register(ToolDefinition.Write("fast", WriteGuard.OpenMod(), null, inv => { events.Add("fast"); inv.Complete("ok"); }));
            reg.TryGet("slow", out RegisteredTool slow);
            reg.TryGet("fast", out RegisteredTool fast);
            object slowResult = null, fastResult = null;
            Thread t1 = new Thread(() => slowResult = slow.Invoke(new ToolContext("slow", new JObject(), d, TimeSpan.FromSeconds(5))));
            Thread t2 = new Thread(() => fastResult = fast.Invoke(new ToolContext("fast", new JObject(), d, TimeSpan.FromSeconds(5))));
            t1.Start();
            d.WaitForQueued(1);
            d.RunAll();
            t2.Start();
            d.WaitForQueued(1);
            d.RunAll();
            Check("second write does not start while the first is pending", events.SequenceEqual(new[] { "slow-start" }));
            host.Captures = 0;
            d.RunOnMain(() => pending.Complete("done"));
            t1.Join(2000);
            t2.Join(2000);
            Check("first completes, then the second runs with a fresh guard evaluation",
                (string)slowResult == "done" && (string)fastResult == "ok" && events.SequenceEqual(new[] { "slow-start", "fast" }) && host.Captures == 1);
        }

        private sealed class FakeHost : IEditorGuardHost
        {
            public int Captures;
            public EditorSnapshot Capture()
            {
                Captures++;
                return new EditorSnapshot
                {
                    EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "PB_TEST_A", FolderName = "PB_TEST_A",
                    IsEditing = true, StateName = "Editing",
                };
            }
            public void EnforceDefaultWorkshopId() { }
        }

        /// <summary>Queues work like MainThreadDispatcher; the test thread acts as the main thread.</summary>
        private sealed class ManualDispatcher : IMainThreadDispatcher
        {
            private readonly MainThreadDispatcher m_inner = new MainThreadDispatcher();
            public object Invoke(Action<IWorkCompletion> start, TimeSpan timeout) => m_inner.Invoke(start, timeout);
            public void WaitForQueued(int n)
            {
                SpinWait.SpinUntil(() => m_inner.PendingCount >= n, 2000);
            }
            public void RunAll() => m_inner.Drain(100, 1000);
            public void RunOnMain(Action a) => a();
        }

        // ================================================================== edit pipeline

        private sealed class FakeEditHost : IEditHost
        {
            public bool Selected;
            public bool SelectAccepted = true;
            public bool Baseline;
            public object Token = new object();
            public bool Mouse;
            public RpcException Invalid;
            public int Selects;
            public RpcException Validate() => Invalid;
            public bool TargetIsSoleSelection() => Selected;
            public void SelectTarget() { Selects++; if (SelectAccepted) Selected = true; }
            public bool BaselineCoversTarget() => Baseline;
            public object BaselineToken() => Token;
            public bool MouseHeld() => Mouse;
        }

        private static void EditPipelineTests()
        {
            Console.WriteLine("-- unit: edit pipeline (select -> baseline -> edit -> push)");
            TimeSpan now = TimeSpan.Zero;
            Func<TimeSpan> clock = () => now;

            // Already selected and snapshotted: applies at once; then waits for the push.
            FakeEditHost h = new FakeEditHost { Selected = true, Baseline = true };
            FakeCompletion c = new FakeCompletion();
            int applied = 0;
            EditPipeline p = new EditPipeline(h, c, true, () => { applied++; return new EditOutcome { Changed = true, Then = PostEditWait.AwaitPush, BuildResult = r => r }; }, clock);
            bool done = p.Start();
            Check("sole selection + baseline: edit applied in the same call, no reselect", applied == 1 && h.Selects == 0 && !done);
            Check("waits for the push", !p.Tick() && c.Completions == 0);
            h.Token = new object();
            Check("push observed -> completes undoable", p.Tick() && ((EditReport)c.Result).UndoRecorded && ((EditReport)c.Result).Changed);

            // Not selected: selects, waits for the baseline, then applies.
            h = new FakeEditHost();
            c = new FakeCompletion();
            applied = 0;
            p = new EditPipeline(h, c, true, () => { applied++; return new EditOutcome { Changed = true, Then = PostEditWait.AwaitPush, BuildResult = r => r }; }, clock);
            p.Start();
            Check("selects the target, does not apply before the baseline", h.Selects == 1 && applied == 0);
            p.Tick();
            Check("still waiting without baseline", applied == 0);
            h.Baseline = true;
            p.Tick();
            Check("applies once the baseline covers the target", applied == 1);
            now += EditPipeline.DefaultPushTimeout;
            p.Tick();
            Check("no push within the bound -> completes with undoPending", c.Completions == 1 && ((EditReport)c.Result).UndoPending && !((EditReport)c.Result).UndoRecorded);

            // Mouse held: baseline never comes -> INVALID_STATE, edit never applied.
            now = TimeSpan.Zero;
            h = new FakeEditHost { Mouse = true };
            c = new FakeCompletion();
            applied = 0;
            p = new EditPipeline(h, c, true, () => { applied++; return new EditOutcome { Changed = true }; }, clock);
            p.Start();
            now += EditPipeline.DefaultBaselineTimeout;
            p.Tick();
            Check("mouse held: INVALID_STATE after the bound, nothing applied", (c.Error as RpcException)?.Code == ErrorCodes.InvalidState && applied == 0);

            now = TimeSpan.Zero;
            h = new FakeEditHost();
            c = new FakeCompletion();
            p = new EditPipeline(h, c, true, () => { applied++; return new EditOutcome { Changed = true }; }, clock);
            p.Start();
            now += EditPipeline.DefaultBaselineTimeout;
            p.Tick();
            Check("no baseline without mouse: TIMEOUT", (c.Error as RpcException)?.Code == ErrorCodes.Timeout);

            // Mod switched while waiting -> error, edit never applied.
            now = TimeSpan.Zero;
            h = new FakeEditHost();
            c = new FakeCompletion();
            applied = 0;
            p = new EditPipeline(h, c, true, () => { applied++; return new EditOutcome { Changed = true }; }, clock);
            p.Start();
            h.Invalid = new RpcException(ErrorCodes.InvalidState, "the open mod changed");
            h.Baseline = true;
            p.Tick();
            Check("mod change while waiting: fails, stale object untouched", (c.Error as RpcException)?.Code == ErrorCodes.InvalidState && applied == 0);

            // Invalid after the edit while waiting for the push.
            h = new FakeEditHost { Selected = true, Baseline = true };
            c = new FakeCompletion();
            p = new EditPipeline(h, c, true, () => new EditOutcome { Changed = true, Then = PostEditWait.AwaitPush }, clock);
            p.Start();
            h.Invalid = new RpcException(ErrorCodes.InvalidState, "left edit mode");
            p.Tick();
            Check("invalid after the edit: error says the edit was applied", c.Error?.Message.Contains("applied") == true);

            // Unchanged edit completes at once.
            h = new FakeEditHost { Selected = true, Baseline = true };
            c = new FakeCompletion();
            p = new EditPipeline(h, c, true, () => new EditOutcome { Changed = false, Then = PostEditWait.AwaitPush, BuildResult = r => r }, clock);
            Check("unchanged edit completes immediately, not undoable", p.Start() && !((EditReport)c.Result).Changed && !((EditReport)c.Result).UndoRecorded);

            // Explicit push (structural edit).
            h = new FakeEditHost { Selected = true, Baseline = true };
            c = new FakeCompletion();
            p = new EditPipeline(h, c, true, () => new EditOutcome { Changed = true, Then = PostEditWait.None, BuildResult = r => r }, clock);
            Check("explicit undo event: completes immediately as recorded", p.Start() && ((EditReport)c.Result).UndoRecorded);

            // Selection not accepted (locked).
            h = new FakeEditHost { SelectAccepted = false };
            c = new FakeCompletion();
            p = new EditPipeline(h, c, true, () => new EditOutcome(), clock);
            Check("selection refused by the editor -> INVALID_STATE", p.Start() && (c.Error as RpcException)?.Code == ErrorCodes.InvalidState);

            // User changes selection while waiting: reselect, bounded.
            h = new FakeEditHost();
            c = new FakeCompletion();
            p = new EditPipeline(h, c, true, () => new EditOutcome { Changed = true }, clock);
            p.Start();
            int i;
            for (i = 0; i < 10 && c.Completions == 0; i++)
            {
                h.Selected = false;
                p.Tick();
            }
            Check("selection keeps changing: reselects a few times, then INVALID_STATE", h.Selects > 1 && (c.Error as RpcException)?.Code == ErrorCodes.InvalidState);

            // Creation: apply first, then wait for the baseline of the new object.
            now = TimeSpan.Zero;
            h = new FakeEditHost();
            c = new FakeCompletion();
            p = new EditPipeline(h, c, false, () => new EditOutcome { Changed = true, Then = PostEditWait.AwaitBaseline, BuildResult = r => r }, clock);
            Check("create: no selection step, waits for the new object's baseline", !p.Start() && h.Selects == 0);
            h.Baseline = true;
            Check("create: completes once snapshotted (not undoable)", p.Tick() && !((EditReport)c.Result).UndoRecorded && !((EditReport)c.Result).BaselineTimedOut);

            // Apply throws: error forwarded.
            h = new FakeEditHost { Selected = true, Baseline = true };
            c = new FakeCompletion();
            p = new EditPipeline(h, c, true, () => throw new RpcException(ErrorCodes.InvalidParams, "bad"), clock);
            Check("edit throwing -> error forwarded", p.Start() && (c.Error as RpcException)?.Code == ErrorCodes.InvalidParams);

            // Caller gave up.
            h = new FakeEditHost();
            c = new FakeCompletion();
            p = new EditPipeline(h, c, true, () => new EditOutcome { Changed = true }, clock);
            p.Start();
            c.Cancelled = true;
            Check("cancelled call stops the pipeline and still completes (queue moves on)", p.Tick() && c.Completions == 1);
        }

        // ================================================================== commit point (M1)

        /// <summary>Pipeline + fake completion with a fake clock: the commit point and the deadline rules.</summary>
        private static void CommitPipelineTests()
        {
            Console.WriteLine("-- unit: edit pipeline commit point and deadline");
            TimeSpan now = TimeSpan.Zero;
            Func<TimeSpan> clock = () => now;
            TimeSpan deadline = TimeSpan.FromSeconds(15);
            Func<TimeSpan> left = () => deadline - now;

            // Timeout before commit: cancelled while waiting for the baseline -> TIMEOUT, edit never runs later.
            FakeEditHost h = new FakeEditHost();
            FakeCompletion c = new FakeCompletion { Left = left };
            int applied = 0;
            EditPipeline p = new EditPipeline(h, c, true, () => { applied++; return new EditOutcome { Changed = true, Then = PostEditWait.AwaitPush, BuildResult = r => r }; }, clock);
            p.Start();
            c.Cancelled = true;
            h.Baseline = true; // the baseline arrives in the same frame the caller gave up
            p.Tick();
            p.Tick();
            p.Tick();
            Check("pre-commit timeout: TIMEOUT 'nothing was changed', edit never applied, not committed",
                applied == 0 && !c.Committed && !p.IsCommitted && (c.Error as RpcException)?.Code == ErrorCodes.Timeout
                && c.Error.Message.Contains("nothing was changed") && c.Completions == 1);

            // Commit refused (caller gave up between the last check and the edit): TryCommit decides atomically.
            h = new FakeEditHost { Selected = true, Baseline = true };
            c = new FakeCompletion { Left = left };
            applied = 0;
            p = new EditPipeline(h, c, true, () => { applied++; return new EditOutcome { Changed = true }; }, clock);
            c.Cancelled = true;
            Check("cancelled before start: nothing selected, nothing applied", p.Start() && applied == 0 && h.Selects == 0 && (c.Error as RpcException)?.Code == ErrorCodes.Timeout);

            // Deadline reached after commit: the post-edit wait ends at the deadline with the normal (success) result.
            now = TimeSpan.Zero;
            h = new FakeEditHost { Selected = true, Baseline = true };
            c = new FakeCompletion { Left = left };
            applied = 0;
            p = new EditPipeline(h, c, true, () => { applied++; return new EditOutcome { Changed = true, Then = PostEditWait.AwaitPush, BuildResult = r => r }; }, clock);
            p.PushTimeout = TimeSpan.FromSeconds(10);
            now = deadline - TimeSpan.FromSeconds(11); // 11 s left >= reserve (10.5 s): applies
            p.Start();
            Check("post-commit: applied once and committed", applied == 1 && c.Committed && p.IsCommitted && c.Completions == 0);
            c.Cancelled = true; // a (buggy) late cancel flag must not turn an applied edit into a TIMEOUT
            now = deadline + TimeSpan.FromMilliseconds(1);
            p.Tick();
            p.Tick();
            EditReport rep = c.Result as EditReport;
            Check("post-commit: deadline reached -> success result with undoPending, applied exactly once",
                c.Completions == 1 && c.Error == null && rep != null && rep.Changed && rep.UndoPending && applied == 1);

            // Not enough time left to start (waited in the write queue): fails before selecting anything.
            now = TimeSpan.Zero;
            h = new FakeEditHost();
            c = new FakeCompletion { Left = () => TimeSpan.FromSeconds(2) };
            applied = 0;
            p = new EditPipeline(h, c, true, () => { applied++; return new EditOutcome { Changed = true }; }, clock);
            Check("queued too long: TIMEOUT at start, nothing selected or applied",
                p.Start() && applied == 0 && h.Selects == 0 && (c.Error as RpcException)?.Code == ErrorCodes.Timeout && c.Error.Message.Contains("could not start"));

            // Creation (no select step) also refuses to apply without its reserve.
            c = new FakeCompletion { Left = () => TimeSpan.FromSeconds(1) };
            p = new EditPipeline(new FakeEditHost(), c, false, () => { applied++; return new EditOutcome { Changed = true, Then = PostEditWait.AwaitBaseline }; }, clock);
            Check("create with too little time: not created", p.Start() && applied == 0 && (c.Error as RpcException)?.Code == ErrorCodes.Timeout);

            // The deadline approaches while waiting for the baseline: refuse to apply (the reply could not be sent in time).
            now = TimeSpan.Zero;
            h = new FakeEditHost();
            c = new FakeCompletion { Left = left };
            applied = 0;
            p = new EditPipeline(h, c, true, () => { applied++; return new EditOutcome { Changed = true }; }, clock);
            p.Start();
            now = deadline - TimeSpan.FromSeconds(3); // < 3.5 s reserve
            h.Baseline = true;
            p.Tick();
            Check("reserve reached while waiting for the baseline: TIMEOUT, nothing applied (object only selected)",
                applied == 0 && (c.Error as RpcException)?.Code == ErrorCodes.Timeout && c.Error.Message.Contains("nothing was changed"));

            // A reply-building failure after the edit says the edit was applied.
            h = new FakeEditHost { Selected = true, Baseline = true };
            c = new FakeCompletion();
            p = new EditPipeline(h, c, true, () => new EditOutcome { Changed = true, Then = PostEditWait.None, BuildResult = r => throw new InvalidOperationException("boom") }, clock);
            p.Start();
            Check("post-commit reply failure: INTERNAL 'the edit was applied'", (c.Error as RpcException)?.Code == ErrorCodes.Internal && c.Error.Message.Contains("applied"));
        }

        /// <summary>Real dispatcher + write queue + registry with a thread acting as the HTTP caller.</summary>
        private static void CommitDispatchTests()
        {
            Console.WriteLine("-- unit: dispatcher commit semantics (real dispatcher, write queue, registry)");
            FakeHost guardHost = new FakeHost();

            // 1. Timeout before commit: the caller gets TIMEOUT; the edit never runs, also when the baseline comes later.
            {
                MainThreadDispatcher d = new MainThreadDispatcher(TimeSpan.FromSeconds(3));
                ToolRegistry reg = new ToolRegistry(new WriteSafety(() => true, () => true, guardHost));
                FakeEditHost eh = new FakeEditHost { Selected = true, Baseline = false };
                EditPipeline pipe = null;
                int applied = 0;
                reg.Register(ToolDefinition.Write("edit", WriteGuard.OpenMod(), null, inv =>
                {
                    pipe = new EditPipeline(eh, inv.Completion, true, () => { applied++; return new EditOutcome { Changed = true, Then = PostEditWait.None, BuildResult = r => "ok" }; })
                    {
                        PushTimeout = TimeSpan.FromMilliseconds(50),
                        CommitMargin = TimeSpan.Zero,
                        BaselineTimeout = TimeSpan.FromSeconds(30),
                    };
                    pipe.Start();
                }, TimeSpan.FromMilliseconds(1300)));
                string code = null, message = null;
                Thread caller = Call(reg, "edit", d, (r, e) => { code = (e as RpcException)?.Code; message = e?.Message; });
                SpinWait.SpinUntil(() => d.PendingCount >= 1, 2000);
                d.Drain(10, 100);
                while (caller.IsAlive)
                {
                    pipe?.Tick(); // waits for a baseline that does not come
                    Thread.Sleep(10);
                }
                eh.Baseline = true; // too late
                for (int i = 0; i < 5; i++)
                {
                    pipe.Tick();
                    reg.WriteQueue.Pump();
                }
                Check("dispatch: pre-commit TIMEOUT says safe to retry; edit never ran later", code == ErrorCodes.Timeout && message.Contains("safe to retry") && applied == 0 && pipe.IsDone);
                Check("dispatch: the write queue moved on", !reg.WriteQueue.IsBusy);
            }

            // 2. Deadline reached after commit: the caller waits (bounded) and gets the success result; applied once.
            {
                MainThreadDispatcher d = new MainThreadDispatcher(TimeSpan.FromSeconds(3));
                ToolRegistry reg = new ToolRegistry(new WriteSafety(() => true, () => true, guardHost));
                FakeEditHost eh = new FakeEditHost { Selected = true, Baseline = true };
                EditPipeline pipe = null;
                int applied = 0;
                reg.Register(ToolDefinition.Write("edit", WriteGuard.OpenMod(), null, inv =>
                {
                    pipe = new EditPipeline(eh, inv.Completion, true, () => { applied++; return new EditOutcome { Changed = true, Then = PostEditWait.AwaitPush, BuildResult = r => r }; })
                    {
                        PushTimeout = TimeSpan.FromMilliseconds(300),
                        CommitMargin = TimeSpan.Zero,
                    };
                    pipe.Start(); // applies at once (selected + baseline), then waits for the push
                }, TimeSpan.FromMilliseconds(1200)));
                object result = null;
                Exception error = null;
                Stopwatch sw = Stopwatch.StartNew();
                Thread caller = Call(reg, "edit", d, (r, e) => { result = r; error = e; });
                SpinWait.SpinUntil(() => d.PendingCount >= 1, 2000);
                d.Drain(10, 100);
                bool appliedBeforeDeadline = applied == 1 && pipe.IsCommitted;
                Thread.Sleep(1700); // "frame stall" past the caller's deadline; the push never comes
                bool stillWaiting = caller.IsAlive;
                pipe.Tick();
                caller.Join(5000);
                EditReport rep = result as EditReport;
                Check("dispatch: committed call is not cancelled at its deadline (caller still waiting)", appliedBeforeDeadline && stillWaiting);
                Check($"dispatch: deadline after commit -> success result (undoPending), applied exactly once ({sw.Elapsed.TotalSeconds:0.0} s)",
                    error == null && rep != null && rep.Changed && rep.UndoPending && applied == 1 && sw.Elapsed.TotalMilliseconds > 1200);
            }

            // 3. Queued call that cannot start in time: fails before touching anything; the body never runs.
            {
                MainThreadDispatcher d = new MainThreadDispatcher(TimeSpan.FromSeconds(3));
                ToolRegistry reg = new ToolRegistry(new WriteSafety(() => true, () => true, guardHost));
                WriteInvocation held = null;
                int secondRuns = 0;
                reg.Register(ToolDefinition.Write("slow", WriteGuard.OpenMod(), null, inv => held = inv, TimeSpan.FromSeconds(10)));
                reg.Register(ToolDefinition.Write("second", WriteGuard.OpenMod(), null, inv => { secondRuns++; inv.Complete("ran"); }, TimeSpan.FromMilliseconds(1600)));
                object slowResult = null;
                string secondCode = null, secondMessage = null;
                Thread t1 = Call(reg, "slow", d, (r, e) => slowResult = r);
                SpinWait.SpinUntil(() => d.PendingCount >= 1, 2000);
                d.Drain(10, 100);
                Thread t2 = Call(reg, "second", d, (r, e) => { secondCode = (e as RpcException)?.Code; secondMessage = e?.Message; });
                SpinWait.SpinUntil(() => d.PendingCount >= 1, 2000);
                d.Drain(10, 100); // second joins the write queue behind slow
                Thread.Sleep(800); // second now has < 1 s (WriteQueue.MinStartBudget) left
                held.Complete("slow done");
                t1.Join(2000);
                t2.Join(3000);
                Check("dispatch: queued call without enough time left -> TIMEOUT 'not started', body never ran",
                    secondCode == ErrorCodes.Timeout && secondMessage.Contains("write queue") && secondRuns == 0 && (string)slowResult == "slow done");

                // A body's own commit point refuses when its budget does not fit (e.g. a create/open load).
                int sideEffects = 0;
                reg.Register(ToolDefinition.Write("needs_time", WriteGuard.OpenMod(), null, inv =>
                {
                    inv.Commit(TimeSpan.FromSeconds(5));
                    sideEffects++;
                    inv.Complete("done");
                }, TimeSpan.FromSeconds(2)));
                string code = null;
                Thread t3 = Call(reg, "needs_time", d, (r, e) => code = (e as RpcException)?.Code);
                SpinWait.SpinUntil(() => d.PendingCount >= 1, 2000);
                d.Drain(10, 100);
                t3.Join(3000);
                Check("dispatch: WriteInvocation.Commit with too little time left -> TIMEOUT, no side effect", code == ErrorCodes.Timeout && sideEffects == 0);
            }
        }

        private static Thread Call(ToolRegistry reg, string name, IMainThreadDispatcher d, Action<object, Exception> done)
        {
            reg.TryGet(name, out RegisteredTool tool);
            Thread t = new Thread(() =>
            {
                try
                {
                    done(tool.Invoke(new ToolContext(name, new JObject(), d, TimeSpan.FromSeconds(10))), null);
                }
                catch (Exception e)
                {
                    done(null, e);
                }
            }) { IsBackground = true };
            t.Start();
            return t;
        }

        // ================================================================== reference rules (M2)

        private static RefComponent RC(string type, string guid, bool matches) => new RefComponent { TypeName = type, Guid = guid, Matches = matches };

        private static void ReferenceRuleTests()
        {
            Console.WriteLine("-- unit: reference rules (object guid vs component guid)");
            const string obj = "0b6d0000-0000-0000-0000-000000000001", tr = "0b6d0000-0000-0000-0000-00000000000a";
            const string audio = "0b6d0000-0000-0000-0000-00000000000b", audio2 = "0b6d0000-0000-0000-0000-00000000000c";
            Func<IList<RefComponent>> speaker = () => new List<RefComponent> { RC("ModTransform", tr, false), RC("ModAudioSource", audio, true) };
            int calls = 0;
            Func<IList<RefComponent>> counted = () => { calls++; return speaker(); };

            RefDecision d = ReferenceRules.Decide(obj, true, obj, "Speaker", ExpectedRefKind.Other, false, speaker, "AudioSource", "targets[0]");
            Check("object guid (registered ModTransform asset), AudioSource expected -> the object's AudioSource component",
                d.Source == RefSource.ObjectComponent && d.ComponentIndex == 1);
            Check("object guid compared case-insensitively",
                ReferenceRules.Decide(obj, true, obj.ToUpperInvariant(), "Speaker", ExpectedRefKind.Other, false, speaker, "AudioSource", "p").Source == RefSource.ObjectComponent);
            d = ReferenceRules.Decide(audio, true, null, null, ExpectedRefKind.Other, false, counted, "AudioSource", "targets[0]");
            Check("component guid -> its registered asset, components not searched", d.Source == RefSource.Asset && calls == 0);
            d = ReferenceRules.Decide(obj, true, obj, "Speaker", ExpectedRefKind.Transform, true, counted, "Transform", "m_transformToMatch");
            Check("object guid, Transform expected -> the object's own asset (Transform)", d.Source == RefSource.Asset && calls == 0);
            d = ReferenceRules.Decide(obj, true, obj, "Speaker", ExpectedRefKind.GameObject, false, counted, "GameObject", "p");
            Check("object guid, GameObject expected -> the object's own asset (its GameObject)", d.Source == RefSource.Asset && calls == 0);
            d = ReferenceRules.Decide(obj, true, obj, "Speaker", ExpectedRefKind.Other, true, counted, "Component", "targets[0]");
            Check("object guid, Component expected (Transform fits) -> the Transform, as before", d.Source == RefSource.Asset && calls == 0);
            d = ReferenceRules.Decide(obj, false, null, "Speaker", ExpectedRefKind.Other, false, speaker, "AudioSource", "p");
            Check("unregistered object guid -> component search", d.Source == RefSource.ObjectComponent && d.ComponentIndex == 1);

            string missing = Msg(() => ReferenceRules.Decide(obj, true, obj, "Cube", ExpectedRefKind.Other, false,
                () => new List<RefComponent> { RC("ModTransform", tr, false), RC("ModProp", audio2, false) }, "AudioSource", "targets[0]"));
            Check("object without the component -> INVALID_PARAMS naming the type and listing components with guids",
                Code(() => ReferenceRules.Decide(obj, true, obj, "Cube", ExpectedRefKind.Other, false, () => new List<RefComponent> { RC("ModTransform", tr, false) }, "AudioSource", "p")) == ErrorCodes.InvalidParams
                && missing.Contains("targets[0]") && missing.Contains("no component of type AudioSource") && missing.Contains("ModTransform " + tr) && missing.Contains("ModProp " + audio2));
            string ambiguous = Msg(() => ReferenceRules.Decide(obj, true, obj, "Speaker", ExpectedRefKind.Other, false,
                () => new List<RefComponent> { RC("ModTransform", tr, false), RC("ModAudioSource", audio, true), RC("ModAudioSource", audio2, true) }, "AudioSource", "p"));
            Check("several matching components -> refused, both guids listed", ambiguous != null && ambiguous.Contains(audio) && ambiguous.Contains(audio2) && ambiguous.Contains("2 components"));
            Check("neither asset nor object -> INVALID_PARAMS 'no asset or object'",
                Msg(() => ReferenceRules.Decide(obj, false, null, null, ExpectedRefKind.Other, false, speaker, "AudioSource", "p"))?.Contains("no asset or object") == true);
            Check("asset whose value is another object's Transform (guid differs) -> plain asset path",
                ReferenceRules.Decide(obj, true, tr, null, ExpectedRefKind.Other, false, counted, "AudioSource", "p").Source == RefSource.Asset && calls == 0);
        }

        // ================================================================== reparent rules

        private sealed class Node
        {
            public string Name;
            public Node Parent;
            public bool Hides;
            public bool PrefabRoot;
            public object Context;
            public Node(string name, Node parent, object ctx) { Name = name; Parent = parent; Context = ctx; }
        }

        private static string Reparent(Node target, Node parent) =>
            ReparentRules.Check(target, parent, n => n.Parent, n => n.Hides, n => n.PrefabRoot, n => n.Context, n => n.Name);

        private static void ReparentTests()
        {
            Console.WriteLine("-- unit: reparent rules (fake tree)");
            object scene = "scene", prefabCtx = "prefab";
            Node root = new Node("SceneRoot", null, scene);
            Node a = new Node("A", root, scene);
            Node b = new Node("B", a, scene);
            Node c = new Node("C", b, scene);
            Node d = new Node("D", root, scene);
            Node inst = new Node("Instance", root, scene) { Hides = true };
            Node proot = new Node("PrefabRoot", null, prefabCtx) { PrefabRoot = true };
            Node pchild = new Node("PChild", proot, prefabCtx);
            Check("move to a sibling subtree ok", Reparent(c, d) == null);
            Check("move to top level (context root) ok", Reparent(c, root) == null);
            Check("own parent ok (no-op handled by the tool)", Reparent(b, a) == null);
            Check("onto itself refused", Reparent(a, a) != null);
            Check("into own child refused (cycle)", Reparent(a, b)?.Contains("cycle") == true);
            Check("into own grandchild refused (cycle)", Reparent(a, c)?.Contains("cycle") == true);
            Check("into an object that hides children refused", Reparent(d, inst)?.Contains("does not support child") == true);
            Check("cross-context refused", Reparent(d, pchild)?.Contains("different context") == true);
            Check("prefab root cannot be moved", Reparent(proot, pchild)?.Contains("root") == true);
            Check("IsSelfOrDescendant", ReparentRules.IsSelfOrDescendant(c, a, n => n.Parent) && !ReparentRules.IsSelfOrDescendant(a, c, n => n.Parent));
        }

        // ================================================================== component rules

        private static ComponentTypeInfo T(string name, string ui, bool addable = true, int flags = 0xF, bool kids = true, bool single = true, string baseType = "ModBehaviour", bool baseSingle = false) =>
            new ComponentTypeInfo { Name = name, FullName = name, UiName = ui, Addable = addable, ModTypeFlags = flags, SupportsChildren = kids, DisallowMultiple = single, BaseTypeFullName = baseType, BaseClassDisallowMultiple = baseSingle };

        private static ObjectFacts Obj(bool children, params ComponentTypeInfo[] mods)
        {
            ObjectFacts f = new ObjectFacts { HasChildren = children };
            f.Components.Add(new ComponentFact { FullName = "UnityEngine.Transform", BaseTypeFullName = "UnityEngine.Component" });
            f.Components.Add(new ComponentFact { FullName = "ModTransform", BaseTypeFullName = "ModBehaviour", Reflected = T("ModTransform", "Transform", addable: false) });
            foreach (ComponentTypeInfo m in mods)
            {
                f.Components.Add(new ComponentFact { FullName = m.FullName, BaseTypeFullName = m.BaseTypeFullName, Reflected = m });
                f.IsPrefabInstance |= m.Name == "ModPrefabInstance";
            }
            return f;
        }

        private static void ComponentRuleTests()
        {
            Console.WriteLine("-- unit: component rules (replica of PerformChecksForComponentAddability)");
            ComponentTypeInfo light = T("ModLight", "Light");
            ComponentTypeInfo box = T("ModBoxCollider", "Box Collider", single: false);
            ComponentTypeInfo trigger = T("ModTrigger", "Trigger", flags: ComponentRules.FlagMinigame);
            ComponentTypeInfo logic = T("ModLogic", "Logic", flags: ComponentRules.FlagMinigame | ComponentRules.FlagMap);
            ComponentTypeInfo rigid = T("ModRigidbody", "RigidBody", flags: 0);
            ComponentTypeInfo camera = T("ModCamera", "Camera", addable: false);
            ComponentTypeInfo text = T("ModText", "Text", kids: false, baseType: "ModRenderer", baseSingle: true);
            ComponentTypeInfo item = T("ModItem", "Item", kids: false, flags: ComponentRules.FlagMinigame);
            ComponentTypeInfo prop = T("ModProp", "Prop", baseType: "ModRenderer", baseSingle: true);
            ComponentTypeInfo mesh = T("ModMeshRenderer", "Mesh Renderer", baseType: "ModRenderer", baseSingle: true);
            ComponentTypeInfo inst = T("ModPrefabInstance", "Prefab Instance", kids: false);
            ComponentTypeInfo space = T("ModBoardSpace", "Board Space", kids: false, flags: ComponentRules.FlagMap);
            ComponentTypeInfo spaceEvent = T("ModBoardSpaceEvent", "Board Space Event", flags: ComponentRules.FlagMap);
            ComponentTypeInfo mover = T("ModMover", "Mover", single: false);
            ComponentTypeInfo anim = T("ModSimpleAnimator", "Simple Animator");

            Check("light on empty object in a minigame: addable", ComponentRules.WhyNotAddable(light, Obj(false), "Minigame", false) == null);
            Check("second light refused (single)", ComponentRules.WhyNotAddable(light, Obj(false, light), "Minigame", false)?.Contains("single Light") == true);
            Check("second box collider allowed (multi)", ComponentRules.WhyNotAddable(box, Obj(false, box), "Minigame", false) == null);
            Check("non-addable type refused", ComponentRules.WhyNotAddable(camera, Obj(false), "Minigame", false)?.Contains("not offered") == true);
            Check("mod-type flags: Trigger only in minigames", ComponentRules.WhyNotAddable(trigger, Obj(false), "Board", false)?.Contains("Minigame") == true
                && ComponentRules.WhyNotAddable(trigger, Obj(false), "Minigame", false) == null);
            Check("Logic in Board ok, in Item mods not", ComponentRules.WhyNotAddable(logic, Obj(false), "Board", false) == null
                && ComponentRules.WhyNotAddable(logic, Obj(false), "Item", false) != null);
            Check("flags None: never offered", ComponentRules.WhyNotAddable(rigid, Obj(false), "Minigame", false)?.Contains("any mod type") == true);
            Check("!supportsChildren needs no children", ComponentRules.WhyNotAddable(text, Obj(true), "Minigame", false)?.Contains("no child gameobjects") == true);
            Check("two different !supportsChildren types refused", ComponentRules.WhyNotAddable(item, Obj(false, text), "Minigame", false)?.Contains("containing a Text") == true);
            Check("renderer base class: Prop + Mesh Renderer refused", ComponentRules.WhyNotAddable(mesh, Obj(false, prop), "Minigame", false)?.Contains("ModRenderer base") == true);
            Check("prefab instance only on a bare object", ComponentRules.WhyNotAddable(inst, Obj(false), "Minigame", false) == null
                && ComponentRules.WhyNotAddable(inst, Obj(false, light), "Minigame", false)?.Contains("no other components") == true);
            Check("prefab instance object: no components can be added", ComponentRules.WhyNotAddable(box, Obj(false, inst), "Minigame", false)?.Contains("prefab instance") == true);
            Check("prefab instance / board space refused inside a prefab", ComponentRules.WhyNotAddable(inst, Obj(false), "Minigame", true)?.Contains("inside a prefab") == true
                && ComponentRules.WhyNotAddable(space, Obj(false), "Board", true)?.Contains("inside a prefab") == true);
            Check("board space event only inside a prefab", ComponentRules.WhyNotAddable(spaceEvent, Obj(false), "Board", false)?.Contains("inside of a prefab") == true
                && ComponentRules.WhyNotAddable(spaceEvent, Obj(false), "Board", true) == null);
            Check("mover vs simple animator exclusive (both ways)", ComponentRules.WhyNotAddable(mover, Obj(false, anim), "Minigame", false)?.Contains("Simple Animator") == true
                && ComponentRules.WhyNotAddable(anim, Obj(false, mover), "Minigame", false)?.Contains("Mover") == true);
            Check("no object: context rules only", ComponentRules.WhyNotAddable(light, null, "Minigame", false) == null
                && ComponentRules.WhyNotAddable(spaceEvent, null, "Board", false) != null);
            Check("multiple reasons joined by newline", ComponentRules.PerformChecks(text, Obj(true, item), false).reason.Contains("\n"));
            Check("remove: non-addable (Transform) refused", ComponentRules.WhyNotRemovable(T("ModTransform", "Transform", addable: false), false) != null);
            Check("remove: addable ok", ComponentRules.WhyNotRemovable(light, false) == null);
            Check("remove on prefab instance: only Prefab Instance", ComponentRules.WhyNotRemovable(inst, true) == null && ComponentRules.WhyNotRemovable(light, true) != null);
            Check("CanUseItem mapping", ComponentRules.CanUseItem("Board", ComponentRules.FlagMap) && !ComponentRules.CanUseItem("Board", ComponentRules.FlagMinigame)
                && ComponentRules.CanUseItem("Item", ComponentRules.FlagItem) && ComponentRules.CanUseItem("BoardSpace", ComponentRules.FlagBoardSpace));
        }

        // ================================================================== kinds + clamps

        private static void KindAndClampTests()
        {
            Console.WriteLine("-- unit: creation kinds and inspector clamps");
            Check("19 creation kinds, unique", ObjectKinds.All.Length == 19 && ObjectKinds.All.Select(k => k.Kind).Distinct().Count() == 19);
            Check("kind lookup case-insensitive", ObjectKinds.Find("PointLight")?.ObjectType == "PointLight" && ObjectKinds.Find("weaponsource")?.ObjectType == "FireWeapon");
            ObjectKindInfo trig = ObjectKinds.Find("trigger"), space = ObjectKinds.Find("boardSpace"), inst = ObjectKinds.Find("prefabInstance"), cube = ObjectKinds.Find("cube");
            Check("minigame object kinds only in minigames", ObjectKinds.WhyUnavailable(trig, "Board", true, false, false, "p") != null
                && ObjectKinds.WhyUnavailable(trig, "Minigame", true, false, false, "p") == null);
            Check("board space only in boards and not in prefab edit", ObjectKinds.WhyUnavailable(space, "Board", true, false, false, "p") == null
                && ObjectKinds.WhyUnavailable(space, "Board", true, true, false, "p") != null && ObjectKinds.WhyUnavailable(space, "Minigame", true, false, false, "p") != null);
            Check("prefab instance disabled in prefab edit", ObjectKinds.WhyUnavailable(inst, "Minigame", true, true, false, "p") != null);
            Check("parent that hides children refuses every kind", ObjectKinds.WhyUnavailable(cube, "Minigame", true, false, true, "Inst")?.Contains("Inst") == true);
            Check("no hierarchy (item mod) without prefab refused; with prefab ok", ObjectKinds.WhyUnavailable(cube, "Item", false, false, false, "p") != null
                && ObjectKinds.WhyUnavailable(cube, "Item", false, true, false, "p") == null);

            PNumber spot = new PNumber { Value = 200 };
            Check("spotAngle clamped to 180", InspectorClamps.Apply("ModLight", "spotAngle", spot) && spot.Value == 180);
            PNumber cd = new PNumber { Value = 0.01 };
            Check("cooldown min 0.1", InspectorClamps.Apply("ModItem", "Cooldown", cd) && Math.Abs(cd.Value - 0.1) < 1e-9);
            PInteger sc = new PInteger { Value = 50 };
            Check("spawn count clamped to 20 (int)", InspectorClamps.Apply("ModSpawner", "SpawnCount", sc) && sc.Value == 20);
            PNumber ok = new PNumber { Value = 0.5 };
            Check("in-range value unchanged; unknown property untouched", !InspectorClamps.Apply("ModAudioSource", "volume", ok) && ok.Value == 0.5
                && !InspectorClamps.Apply("ModLight", "intensity", new PNumber { Value = 99 }));
        }
    }
}
