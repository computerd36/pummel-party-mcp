using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using PummelBridge.Core;

namespace PummelBridge.Tests
{
    /// <summary>Test-mod name rule, write guard ordering, registry enforcement, fail-closed switches, frame poller.</summary>
    internal static class GuardTests
    {
        private static Action<string, bool> s_check;

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            NameRule();
            Registration();
            OpenModGuardOrder();
            LifecycleGuard();
            ThroughRegistry();
            Poller();
        }

        private static void Check(string name, bool ok) => s_check(name, ok);

        // ------------------------------------------------------------------ name rule

        private static void NameRule()
        {
            Console.WriteLine("-- unit: test-mod name rule");
            string[] valid =
            {
                "PB_TEST_x", "PB_TEST_Sandbox", "PB_TEST_My Arena-2", "PB_TEST__", "PB_TEST_-", "PB_TEST_0",
                "PB_TEST_" + new string('a', TestModName.MaxLength - 8),
            };
            foreach (string v in valid)
            {
                Check($"valid: '{Show(v)}'", TestModName.IsValid(v) && TestModName.Validate(v) == null);
            }
            string[] invalid =
            {
                null, "", "PB_TEST_", "PB_TEST", "PB_TEST ", "pb_test_x", "Pb_Test_x", "PB_test_x", "pB_TEST_x",
                " PB_TEST_x", "PB_TEST_x ", "PB_TEST_x\t", "\tPB_TEST_x", "PB_TEST_x\n", "PB_TEST_ ",
                "XPB_TEST_x", "My Real Mod", "My PB_TEST_x",
                "PB_TEST_a/b", "PB_TEST_a\\b", "PB_TEST_..", "PB_TEST_../x", "PB_TEST_..\\x", "PB_TEST_a.b", "PB_TEST_x.",
                "PB_TEST_C:", "PB_TEST_a:b", "PB_TEST_*", "PB_TEST_?", "PB_TEST_\"", "PB_TEST_<x>", "PB_TEST_a|b", "PB_TEST_a;b",
                "PB_TEST_\0", "PB_TEST_a\u0001",
                "PB_TEST_é", "PB_TEST_ü", "PB_TEST_ẞ", "PB_TEST_\u00A0x", "PB_TEST_x\u200B", "PB_TEST_\uFF41",   // non-ASCII, nbsp, zero-width, fullwidth a
                "PB\u2010TEST_x", "PB_ТЕST_x", "ΡB_TEST_x", "PB＿TEST_x",   // hyphen look-alike, Cyrillic Т/Е, Greek Rho, fullwidth low line
                "PB_TEST_" + new string('a', TestModName.MaxLength - 7),   // 65 chars
                "PB_TEST_" + new string('a', 300),
            };
            foreach (string v in invalid)
            {
                Check($"invalid: '{Show(v)}'", !TestModName.IsValid(v) && TestModName.Validate(v) != null);
            }
            Check("length 64 is the maximum", ("PB_TEST_" + new string('a', 56)).Length == 64 && TestModName.IsValid("PB_TEST_" + new string('a', 56)));
            Check("prefix constant is exactly PB_TEST_", TestModName.Prefix == "PB_TEST_");
        }

        private static string Show(string s) => s == null ? "<null>" : (s.Length > 40 ? s.Substring(0, 40) + "..." : s)
            .Replace("\t", "\\t").Replace("\n", "\\n").Replace("\0", "\\0");

        // ------------------------------------------------------------------ registration

        private static void Registration()
        {
            Console.WriteLine("-- unit: write tool registration");
            Check("ToolDefinition has no public constructor",
                typeof(ToolDefinition).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0);
            Check("WriteGuard has no public constructor",
                typeof(WriteGuard).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0);
            Check("ToolDefinition.Write without guard throws",
                Throws<ArgumentNullException>(() => ToolDefinition.Write("save_mod", null, null, inv => inv.Complete(null))));
            Check("ToolDefinition.Write without body throws",
                Throws<ArgumentNullException>(() => ToolDefinition.Write("save_mod", WriteGuard.OpenMod(), null, null)));
            Check("WriteGuard.Lifecycle without target-name accessor throws",
                Throws<ArgumentNullException>(() => WriteGuard.Lifecycle(null)));

            ToolRegistry readOnly = new ToolRegistry();
            Check("registry without WriteSafety refuses write tools",
                Throws<InvalidOperationException>(() => readOnly.Register(ToolDefinition.Write("save_mod", WriteGuard.OpenMod(), null, inv => inv.Complete(null))))
                && !readOnly.TryGet("save_mod", out _));
            readOnly.Register(ToolDefinition.Read("ping", _ => 1));
            Check("registry without WriteSafety accepts read tools", readOnly.TryGet("ping", out RegisteredTool p) && !p.Definition.IsWrite);

            FakeHost host = new FakeHost();
            ToolRegistry reg = new ToolRegistry(new WriteSafety(() => true, () => true, host));
            reg.Register(ToolDefinition.Write("save_mod", WriteGuard.OpenMod(), null, inv => inv.Complete(null)));
            Check("registry with WriteSafety registers write tool, definition keeps its guard",
                reg.TryGet("save_mod", out RegisteredTool w) && w.Definition.IsWrite && w.Definition.Guard.Kind == WriteGuardKind.OpenMod
                && w.Definition.ReadHandler == null);
            Check("duplicate names refused", Throws<InvalidOperationException>(() => reg.Register(ToolDefinition.Read("save_mod", _ => 1))));
            Check("WriteSafety requires all providers",
                Throws<ArgumentNullException>(() => new WriteSafety(null, () => true, host))
                && Throws<ArgumentNullException>(() => new WriteSafety(() => true, null, host))
                && Throws<ArgumentNullException>(() => new WriteSafety(() => true, () => true, null)));
        }

        // ------------------------------------------------------------------ guard order (OpenMod)

        private static EditorSnapshot GoodSnapshot() => new EditorSnapshot
        {
            EditorOpen = true, IsLoading = false, ModOpen = true, ModLoaded = true,
            ModName = "PB_TEST_Arena", FolderName = "PB_TEST_Arena", IsEditing = true, StateName = "Editing",
        };

        private static string Code(bool writes, bool block, WriteGuard guard, object args, EditorSnapshot snap)
        {
            RpcException e = WriteGuardEvaluator.Evaluate(new WriteSafety(() => writes, () => block, new FakeHost()), guard, args, snap);
            return e?.Code;
        }

        private static void OpenModGuardOrder()
        {
            Console.WriteLine("-- unit: OpenMod guard order");
            WriteGuard g = WriteGuard.OpenMod();
            Check("all good -> allowed", Code(true, true, g, null, GoodSnapshot()) == null);

            // Each check in order: break everything from step N on and expect step N's code.
            EditorSnapshot closed = new EditorSnapshot { EditorOpen = false };
            Check("WRITES_DISABLED first (even with block incomplete and editor closed)", Code(false, false, g, null, closed) == ErrorCodes.WritesDisabled);
            Check("PUBLISH_BLOCK_INCOMPLETE second (editor closed)", Code(true, false, g, null, closed) == ErrorCodes.PublishBlockIncomplete);
            Check("PUBLISH_BLOCK_INCOMPLETE even with a valid test mod open", Code(true, false, g, null, GoodSnapshot()) == ErrorCodes.PublishBlockIncomplete);
            Check("EDITOR_NOT_OPEN", Code(true, true, g, null, closed) == ErrorCodes.EditorNotOpen);
            Check("EDITOR_NOT_OPEN for null snapshot", Code(true, true, g, null, null) == ErrorCodes.EditorNotOpen);

            EditorSnapshot loading = GoodSnapshot();
            loading.IsLoading = true; loading.ModOpen = false; loading.IsEditing = false;
            Check("BUSY_LOADING while loading (before NO_MOD_OPEN / INVALID_STATE)", Code(true, true, g, null, loading) == ErrorCodes.BusyLoading);

            EditorSnapshot noMod = new EditorSnapshot { EditorOpen = true, IsEditing = false };
            Check("NO_MOD_OPEN", Code(true, true, g, null, noMod) == ErrorCodes.NoModOpen);

            EditorSnapshot notLoaded = GoodSnapshot();
            notLoaded.ModLoaded = false; notLoaded.ModName = "My Real Mod";
            Check("BUSY_LOADING when mod not loaded (before name check)", Code(true, true, g, null, notLoaded) == ErrorCodes.BusyLoading);

            EditorSnapshot real = GoodSnapshot();
            real.ModName = "My Real Mod"; real.FolderName = "My Real Mod"; real.IsEditing = false; real.StateName = "PlayMode";
            RpcException e = WriteGuardEvaluator.Evaluate(new WriteSafety(() => true, () => true, new FakeHost()), g, null, real);
            Check("GUARD_NOT_TEST_MOD for a real mod (before INVALID_STATE), message names it",
                e?.Code == ErrorCodes.GuardNotTestMod && e.Message.Contains("My Real Mod"));

            EditorSnapshot metaOnly = GoodSnapshot();
            metaOnly.FolderName = "My Real Mod";
            Check("GUARD_NOT_TEST_MOD when only the Meta name is a test name", Code(true, true, g, null, metaOnly) == ErrorCodes.GuardNotTestMod);
            EditorSnapshot folderOnly = GoodSnapshot();
            folderOnly.ModName = "My Real Mod";
            Check("GUARD_NOT_TEST_MOD when only the folder is a test name", Code(true, true, g, null, folderOnly) == ErrorCodes.GuardNotTestMod);
            EditorSnapshot lower = GoodSnapshot();
            lower.ModName = lower.FolderName = "pb_test_arena";
            Check("GUARD_NOT_TEST_MOD for lower-case prefix", Code(true, true, g, null, lower) == ErrorCodes.GuardNotTestMod);
            EditorSnapshot nullNames = GoodSnapshot();
            nullNames.ModName = null; nullNames.FolderName = null;
            Check("GUARD_NOT_TEST_MOD when names are unreadable", Code(true, true, g, null, nullNames) == ErrorCodes.GuardNotTestMod);
            EditorSnapshot missingMember = GoodSnapshot();
            missingMember.ModName = null; missingMember.ModNameError = "member PummelMod.m_metaData not found - game version changed?";
            RpcException me = WriteGuardEvaluator.Evaluate(new WriteSafety(() => true, () => true, new FakeHost()), g, null, missingMember);
            Check("unreadable name because of a missing game member: refused (fail closed) with a clear message",
                me != null && me.Code == ErrorCodes.Internal && me.Message.Contains("game version changed"));
            Check("same snapshot through the Lifecycle guard is refused too",
                Code(true, true, WriteGuard.Lifecycle(a => "PB_TEST_x"), null, missingMember) != null);

            EditorSnapshot play = GoodSnapshot();
            play.IsEditing = false; play.StateName = "PlayMode";
            Check("INVALID_STATE in play mode", Code(true, true, g, null, play) == ErrorCodes.InvalidState);
            Check("AllowPlayMode tool passes in play mode", Code(true, true, WriteGuard.OpenMod(allowPlayMode: true), null, play) == null);

            Check("throwing WritesEnabled provider fails closed",
                WriteGuardEvaluator.Evaluate(new WriteSafety(() => throw new Exception("x"), () => true, new FakeHost()), g, null, GoodSnapshot())?.Code == ErrorCodes.WritesDisabled);
            Check("throwing PublishBlockComplete provider fails closed",
                WriteGuardEvaluator.Evaluate(new WriteSafety(() => true, () => throw new Exception("x"), new FakeHost()), g, null, GoodSnapshot())?.Code == ErrorCodes.PublishBlockIncomplete);
        }

        // ------------------------------------------------------------------ lifecycle guard

        private static void LifecycleGuard()
        {
            Console.WriteLine("-- unit: Lifecycle guard");
            WriteGuard g = WriteGuard.Lifecycle(a => (string)a);
            EditorSnapshot none = new EditorSnapshot { EditorOpen = true, IsEditing = true, StateName = "Editing" };
            Check("valid target, no mod open -> allowed", Code(true, true, g, "PB_TEST_New", none) == null);
            Check("valid target, test mod open -> allowed", Code(true, true, g, "PB_TEST_New", GoodSnapshot()) == null);
            Check("invalid target -> GUARD_NOT_TEST_MOD", Code(true, true, g, "My Real Mod", none) == ErrorCodes.GuardNotTestMod);
            Check("lower-case target -> GUARD_NOT_TEST_MOD", Code(true, true, g, "pb_test_x", none) == ErrorCodes.GuardNotTestMod);
            Check("path-like target -> GUARD_NOT_TEST_MOD", Code(true, true, g, "PB_TEST_../My Real Mod", none) == ErrorCodes.GuardNotTestMod);
            Check("null target -> GUARD_NOT_TEST_MOD", Code(true, true, g, null, none) == ErrorCodes.GuardNotTestMod);

            EditorSnapshot realOpen = GoodSnapshot();
            realOpen.ModName = realOpen.FolderName = "Arena Deluxe - Winter";
            RpcException e = WriteGuardEvaluator.Evaluate(new WriteSafety(() => true, () => true, new FakeHost()), g, "PB_TEST_New", realOpen);
            Check("non-test mod open -> GUARD_NOT_TEST_MOD naming it and saying 'close ... by hand'",
                e?.Code == ErrorCodes.GuardNotTestMod && e.Message.Contains("Arena Deluxe - Winter") && e.Message.Contains("by hand"));
            EditorSnapshot realNotLoaded = GoodSnapshot();
            realNotLoaded.ModName = realNotLoaded.FolderName = "Arena Deluxe - Winter";
            realNotLoaded.ModLoaded = false;
            Check("non-test mod open but not loaded -> still GUARD_NOT_TEST_MOD", Code(true, true, g, "PB_TEST_New", realNotLoaded) == ErrorCodes.GuardNotTestMod);
            Check("WRITES_DISABLED before target check", Code(false, true, g, "My Real Mod", realOpen) == ErrorCodes.WritesDisabled);
            Check("PUBLISH_BLOCK_INCOMPLETE before target check", Code(true, false, g, "My Real Mod", realOpen) == ErrorCodes.PublishBlockIncomplete);
            EditorSnapshot loading = GoodSnapshot();
            loading.IsLoading = true;
            Check("BUSY_LOADING while loading", Code(true, true, g, "PB_TEST_New", loading) == ErrorCodes.BusyLoading);
            EditorSnapshot play = GoodSnapshot();
            play.IsEditing = false; play.StateName = "PlayMode";
            Check("INVALID_STATE in play mode", Code(true, true, g, "PB_TEST_New", play) == ErrorCodes.InvalidState);
            Check("EDITOR_NOT_OPEN", Code(true, true, g, "PB_TEST_New", new EditorSnapshot()) == ErrorCodes.EditorNotOpen);
            WriteGuard throwing = WriteGuard.Lifecycle(a => throw new Exception("bad"));
            Check("throwing target accessor -> refused", Code(true, true, throwing, null, none) != null);
        }

        // ------------------------------------------------------------------ through the registry

        private static void ThroughRegistry()
        {
            Console.WriteLine("-- unit: guarded invocation through the registry");
            FakeHost host = new FakeHost { Snapshot = GoodSnapshot() };
            bool writes = true, block = true;
            int bodyRuns = 0;
            ToolRegistry reg = new ToolRegistry(new WriteSafety(() => writes, () => block, host));
            reg.Register(ToolDefinition.Write("save_mod", WriteGuard.OpenMod(), null, inv => { bodyRuns++; inv.Complete(new JObject { ["saved"] = true }); }));
            reg.Register(ToolDefinition.Write("open_test_mod", WriteGuard.Lifecycle(a => (string)a), ctx => ctx.GetString("name", true),
                inv => { bodyRuns++; inv.Complete(new JObject { ["name"] = (string)inv.Args }); }));
            InlineDispatcher d = new InlineDispatcher();

            object Call(string method, JObject ps)
            {
                reg.TryGet(method, out RegisteredTool t);
                return t.Invoke(new ToolContext(method, ps, d, TimeSpan.FromSeconds(2)));
            }
            string CallCode(string method, JObject ps)
            {
                try { Call(method, ps); return null; }
                catch (RpcException e) { return e.Code; }
            }

            Check("allowed write runs body on the 'main thread' and returns its result",
                ((JObject)Call("save_mod", new JObject()))?["saved"]?.Value<bool>() == true && bodyRuns == 1 && d.Invocations == 1);
            Check("id hygiene runs when the guard passes for an open test mod", host.HygieneCalls == 1);

            writes = false;
            Check("WRITES_DISABLED through registry, body not run", CallCode("save_mod", new JObject()) == ErrorCodes.WritesDisabled && bodyRuns == 1);
            writes = true; block = false;
            Check("PUBLISH_BLOCK_INCOMPLETE through registry, body not run", CallCode("save_mod", new JObject()) == ErrorCodes.PublishBlockIncomplete && bodyRuns == 1);
            Check("PUBLISH_BLOCK_INCOMPLETE also for lifecycle tools", CallCode("open_test_mod", new JObject { ["name"] = "PB_TEST_X" }) == ErrorCodes.PublishBlockIncomplete && bodyRuns == 1);
            block = true;

            host.Snapshot = GoodSnapshot();
            host.Snapshot.ModName = host.Snapshot.FolderName = "My Real Mod";
            int hygieneBefore = host.HygieneCalls;
            Check("non-test mod open: save refused, body not run, no hygiene",
                CallCode("save_mod", new JObject()) == ErrorCodes.GuardNotTestMod && bodyRuns == 1 && host.HygieneCalls == hygieneBefore);
            Check("non-test mod open: lifecycle refused", CallCode("open_test_mod", new JObject { ["name"] = "PB_TEST_X" }) == ErrorCodes.GuardNotTestMod && bodyRuns == 1);
            host.Snapshot = new EditorSnapshot { EditorOpen = true, IsEditing = true, StateName = "Editing" };
            Check("lifecycle with no mod open runs, no hygiene", ((JObject)Call("open_test_mod", new JObject { ["name"] = "PB_TEST_X" }))?["name"]?.ToString() == "PB_TEST_X"
                && bodyRuns == 2 && host.HygieneCalls == hygieneBefore);
            Check("lifecycle param type error -> INVALID_PARAMS before the main thread", CallCode("open_test_mod", new JObject { ["name"] = 5 }) == ErrorCodes.InvalidParams
                && d.Invocations == 7);
            Check("lifecycle with non-test target refused", CallCode("open_test_mod", new JObject { ["name"] = "My Real Mod" }) == ErrorCodes.GuardNotTestMod && bodyRuns == 2);
            Check("snapshot captured on the dispatcher thread (same unit as the body)", host.CapturedOnDispatcherOnly);
        }

        // ------------------------------------------------------------------ poller

        private static void Poller()
        {
            Console.WriteLine("-- unit: frame poller");
            FramePoller p = new FramePoller(NullBridgeLog.Instance);
            int a = 0, b = 0;
            p.Add(() => ++a >= 3);
            p.Add(() => { b++; throw new Exception("x"); });
            p.Tick();
            Check("poller: thrower removed after first tick", p.Count == 1 && b == 1);
            p.Tick(); p.Tick(); p.Tick();
            Check("poller: callback removed when it returns true", p.Count == 0 && a == 3);
        }

        // ------------------------------------------------------------------ fakes

        private static bool Throws<T>(Action a) where T : Exception
        {
            try { a(); return false; }
            catch (T) { return true; }
            catch { return false; }
        }

        private sealed class FakeHost : IEditorGuardHost
        {
            public EditorSnapshot Snapshot = new EditorSnapshot();
            public int HygieneCalls;
            public bool CapturedOnDispatcherOnly = true;

            public EditorSnapshot Capture()
            {
                if (!InlineDispatcher.OnDispatcher)
                {
                    CapturedOnDispatcherOnly = false;
                }
                return Snapshot;
            }

            public void EnforceDefaultWorkshopId() => HygieneCalls++;
        }

        /// <summary>Runs the work synchronously on the calling thread (flagged as the "main thread").</summary>
        private sealed class InlineDispatcher : IMainThreadDispatcher
        {
            [ThreadStatic] public static bool OnDispatcher;
            public int Invocations;

            public object Invoke(Action<IWorkCompletion> start, TimeSpan timeout)
            {
                Invocations++;
                Completion c = new Completion();
                OnDispatcher = true;
                try
                {
                    start(c);
                }
                catch (Exception e)
                {
                    c.SetException(e);
                }
                finally
                {
                    OnDispatcher = false;
                }
                if (!c.Done.Wait(timeout))
                {
                    throw new RpcException(ErrorCodes.Timeout, "timeout");
                }
                if (c.Error != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(c.Error).Throw();
                }
                return c.Result;
            }

            private sealed class Completion : IWorkCompletion
            {
                public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
                public object Result;
                public Exception Error;
                public bool IsCancelled => false;
                public bool TryCommit() => true;
                public TimeSpan Remaining => TimeSpan.MaxValue;
                public void SetResult(object result) { Result = result; Done.Set(); }
                public void SetException(Exception error) { Error = error; Done.Set(); }
            }
        }
    }
}
