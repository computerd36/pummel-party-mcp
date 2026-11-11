using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PummelBridge.Core;
using PummelBridge.Hooks;
using PummelBridge.Safety;

namespace PummelBridge.PatchTests
{
    // ---- stand-ins shaped like the real targets ----

    /// <summary>Like Steamworks.UGCUpdateHandle_t: Invalid is ulong.MaxValue, so default(T) would NOT be invalid.</summary>
    public struct FakeUpdateHandle
    {
        public ulong Value;
        public static readonly FakeUpdateHandle Invalid = new FakeUpdateHandle { Value = ulong.MaxValue };
    }

    /// <summary>Like Steamworks.SteamAPICall_t: Invalid is 0.</summary>
    public struct FakeApiCall
    {
        public ulong Value;
        public static readonly FakeApiCall Invalid = new FakeApiCall { Value = 0 };
    }

    public struct PlainStruct
    {
        public int A;
    }

    public static class FakeSteamUGC
    {
        public static int OriginalRuns;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static FakeApiCall CreateItem(uint app, int type) { OriginalRuns++; return new FakeApiCall { Value = 42 }; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static FakeUpdateHandle StartItemUpdate(uint app, ulong id) { OriginalRuns++; return new FakeUpdateHandle { Value = 7 }; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool SetItemTitle(FakeUpdateHandle h, string title) { OriginalRuns++; return true; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Count() { OriginalRuns++; return 5; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Name() { OriginalRuns++; return "x"; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static PlainStruct Plain() { OriginalRuns++; return new PlainStruct { A = 9 }; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool GetQueryResult(out int details) { OriginalRuns++; details = 3; return true; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool NotPatched() { OriginalRuns++; return true; }
    }

    /// <summary>Like EditorSystem.SaveCurrentMod(bool): instance method, records its own runs and arguments.</summary>
    public sealed class FakeEditor
    {
        public int Saves;
        public bool LastDelayed;
        public bool Throw;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SaveCurrentMod(bool delayed = false)
        {
            if (Throw)
            {
                throw new InvalidOperationException("save failed");
            }
            Saves++;
            LastDelayed = delayed;
        }
    }

    /// <summary>Stand-ins for the live publish-block check (patched, then unpatched by "another plugin").</summary>
    public static class MonitoredTargets
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Publish() => 1;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Delete() => 1;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Optional() => 1;
    }

    public sealed class FakePopup
    {
        public int Saves;
        public bool Uploading;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PublishMod(object mod) { Saves++; Uploading = true; }

        public void ClickPublish() => PublishMod(null);
    }

    internal static class Program
    {
        private static int s_passed, s_failed;
        private static readonly List<string> s_failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("PummelBridge publish-block patch test (Harmony on " + Environment.Version + ")");
            ListLog log = new ListLog();
            List<string> unity = new List<string>();
            long now = 0;
            PublishBlocker.ClockMs = () => now;
            PublishBlocker.Configure(log, unity.Add);

            Type s = typeof(FakeSteamUGC);
            MethodInfo createItem = s.GetMethod("CreateItem");
            List<BlockTarget> targets = new List<BlockTarget>
            {
                T(createItem, true),
                T(s.GetMethod("StartItemUpdate"), true),
                T(s.GetMethod("SetItemTitle"), false),
                T(s.GetMethod("Count"), false),
                T(s.GetMethod("Name"), false),
                T(s.GetMethod("Plain"), false),
                T(s.GetMethod("GetQueryResult"), false),
                T(typeof(FakePopup).GetMethod("PublishMod", BindingFlags.Instance | BindingFlags.NonPublic), true),
                T(createItem, true), // duplicate entry: patched once
            };
            Harmony harmony = new Harmony("pummelbridge.patchtests");
            PublishBlockStatus st = PublishBlocker.Apply(harmony, targets);
            Check("all targets patched, block complete", st.Active && st.Complete && st.PatchedCount == 8 && st.Missing.Count == 0);
            Check("prefix registered with our Harmony id at Priority.Last",
                Harmony.GetPatchInfo(createItem).Prefixes.Any(p => p.owner == "pummelbridge.patchtests" && p.priority == Priority.Last));

            FakeSteamUGC.OriginalRuns = 0;
            Check("SteamAPICall-like result: original skipped, Invalid (0) returned", FakeSteamUGC.CreateItem(1, 0).Value == 0);
            Check("UGCUpdateHandle-like result: Invalid (ulong.MaxValue) returned, not default(T)", FakeSteamUGC.StartItemUpdate(1, 2).Value == ulong.MaxValue);
            Check("bool result: false", FakeSteamUGC.SetItemTitle(default, "t") == false);
            Check("int result: 0", FakeSteamUGC.Count() == 0);
            Check("reference result: null", FakeSteamUGC.Name() == null);
            Check("struct without Invalid: default", FakeSteamUGC.Plain().A == 0);
            int details = -1;
            Check("out parameter method: false, original skipped", FakeSteamUGC.GetQueryResult(out details) == false);
            Check("no original ran for any patched method", FakeSteamUGC.OriginalRuns == 0);
            Check("unpatched method still runs", FakeSteamUGC.NotPatched() && FakeSteamUGC.OriginalRuns == 1);

            FakePopup popup = new FakePopup();
            popup.ClickPublish();
            Check("private instance void method (WorkshopPopup.PublishMod-like) skipped: no save, not uploading", popup.Saves == 0 && !popup.Uploading);

            string createName = PublishBlocker.Describe(createItem);
            Check("Describe format", createName == "FakeSteamUGC.CreateItem(UInt32, Int32)");
            int warningsBefore = log.Warnings.Count(w => w.Contains(createName));
            Check("first blocked call logged at Warning with method name and user message",
                warningsBefore == 1 && log.Warnings.First(w => w.Contains(createName)).Contains(PublishBlocker.UserMessage));
            Check("Unity sink got the [PummelBridge] line", unity.Any(u => u.StartsWith("[PummelBridge] " + PublishBlocker.UserMessage) && u.Contains(createName)));

            // rate limit: 1000 calls in the same 10 s window -> no further log lines
            for (int i = 0; i < 1000; i++)
            {
                FakeSteamUGC.CreateItem(1, 0);
            }
            Check("per-frame caller does not flood the log (1000 calls, still 1 line)", log.Warnings.Count(w => w.Contains(createName)) == 1);
            Check("blocked calls are counted", PublishBlocker.BlockedCallCount(createName) == 1001);
            now += 10_000;
            FakeSteamUGC.CreateItem(1, 0);
            List<string> lines = log.Warnings.Where(w => w.Contains(createName)).ToList();
            Check("after the interval one more line, reporting the suppressed count", lines.Count == 2 && lines[1].Contains("1000 further calls"));
            Check("other methods are rate-limited independently", log.Warnings.Count(w => w.Contains("StartItemUpdate")) == 1);

            // fail closed
            ListLog log2 = new ListLog();
            PublishBlocker.Configure(log2, null);
            PublishBlockStatus missing = PublishBlocker.Apply(new Harmony("pummelbridge.patchtests.2"), new[]
            {
                new BlockTarget { Name = "Game.Gone()", Layer = "game", Required = true, Problem = "method not found" },
                new BlockTarget { Name = "Opt.Gone()", Layer = "SteamUGC", Required = false, Problem = "not found" },
                T(typeof(FakeSteamUGC).GetMethod("NotPatched"), true),
            });
            Check("missing required target -> incomplete, named in Missing, Error logged",
                !missing.Complete && missing.Active && missing.PatchedCount == 1 && missing.Missing.Count == 1 && missing.Missing[0].StartsWith("Game.Gone()")
                && log2.Errors.Any(e => e.Contains("Game.Gone()")));
            Check("missing optional target -> warning only", log2.Warnings.Any(w => w.Contains("Opt.Gone()")) && !missing.Missing.Any(m => m.StartsWith("Opt.Gone")));

            PublishBlockStatus abstractFail = PublishBlocker.Apply(new Harmony("pummelbridge.patchtests.3"), new[]
            {
                T(typeof(IDisposable).GetMethod("Dispose"), false), // abstract interface method: cannot be patched
            });
            Check("a patch that throws (even optional) -> incomplete, reported", !abstractFail.Complete && abstractFail.Missing.Count == 1 && abstractFail.Missing[0].Contains("patch failed"));
            Check("nothing patched -> not complete", !PublishBlocker.Apply(new Harmony("pummelbridge.patchtests.4"), new BlockTarget[0]).Complete);

            // ---- live publish-block check (PublishBlockMonitor) ----
            MonitorTests();

            // ---- offline drift test against the real game assemblies (reflection only, no patch applied) ----
            GameDriftTest.Run(Check);

            // ---- SaveWatch (postfix on SaveCurrentMod that only records) ----
            MethodInfo save = typeof(FakeEditor).GetMethod("SaveCurrentMod", new[] { typeof(bool) });
            Check("SaveWatch.Install with null target reports an error", SaveWatch.Install(new Harmony("pummelbridge.patchtests.sw0"), null) != null && !SaveWatch.Installed);
            Check("SaveWatch installs", SaveWatch.Install(new Harmony("pummelbridge.patchtests.sw"), save) == null && SaveWatch.Installed);
            int observed = 0, savesSeenByCallback = -1;
            FakeEditor editor = new FakeEditor();
            SaveWatch.OnSaved = () => { observed++; savesSeenByCallback = editor.Saves; };
            int before = SaveWatch.SaveCount;
            editor.SaveCurrentMod(true);
            Check("original runs unchanged (argument passed through)", editor.Saves == 1 && editor.LastDelayed);
            Check("postfix runs after the original and records the save", observed == 1 && savesSeenByCallback == 1 && SaveWatch.SaveCount == before + 1);
            SaveWatch.OnSaved = () => throw new Exception("callback bug");
            editor.SaveCurrentMod();
            Check("a throwing callback never propagates into the save", editor.Saves == 2 && SaveWatch.SaveCount == before + 2);
            SaveWatch.OnSaved = () => observed++;
            editor.Throw = true;
            bool threw = false;
            try { editor.SaveCurrentMod(); } catch (InvalidOperationException) { threw = true; }
            Check("failed save: exception propagates unchanged, postfix not run", threw && observed == 1 && SaveWatch.SaveCount == before + 2);
            SaveWatch.OnSaved = null;

            Console.WriteLine();
            Console.WriteLine($"RESULT: {s_passed} passed, {s_failed} failed");
            foreach (string f in s_failures)
            {
                Console.WriteLine("  FAILED: " + f);
            }
            return s_failed == 0 ? 0 : 1;
        }

        private static void MonitorTests()
        {
            ListLog log = new ListLog();
            PublishBlocker.Configure(log, null);
            Type m = typeof(MonitoredTargets);
            MethodInfo publish = m.GetMethod("Publish"), delete = m.GetMethod("Delete"), optional = m.GetMethod("Optional");
            List<BlockTarget> targets = new List<BlockTarget> { T(publish, true), T(delete, true), T(optional, false) };
            const string id = "pummelbridge.patchtests.monitor";
            Harmony h = new Harmony(id);
            PublishBlockStatus installed = PublishBlocker.Apply(h, targets);
            PublishBlockMonitor monitor = new PublishBlockMonitor(installed, targets, id, log);
            Check("monitor: watches the required targets only", monitor.RequiredCount == 2);
            Check("monitor: complete while every required prefix is in place", installed.Complete && monitor.IsComplete() && monitor.Current().Missing.Count == 0);
            Check("monitor: only this plugin's Harmony id counts",
                PublishBlocker.HasBlockingPrefix(publish, id) && !PublishBlocker.HasBlockingPrefix(publish, "someone.else"));

            h.Unpatch(optional, HarmonyPatchType.Prefix, id);
            Check("monitor: unpatching an optional target keeps it complete", monitor.IsComplete());

            h.Unpatch(delete, HarmonyPatchType.Prefix, id);
            Check("monitor: unpatched original runs again (stand-in sanity)", MonitoredTargets.Delete() == 1);
            PublishBlockStatus lost = monitor.Current();
            string deleteName = PublishBlocker.Describe(delete);
            Check("monitor: required prefix removed after startup -> incomplete, named in missing",
                !lost.Complete && lost.Missing.Count == 1 && lost.Missing[0].StartsWith(deleteName) && lost.Missing[0].Contains("removed after startup"));
            Check("monitor: Error logged naming the target", log.Errors.Count(e => e.Contains(deleteName)) == 1);
            monitor.Current();
            monitor.IsComplete();
            Check("monitor: the Error is logged once, not per check", log.Errors.Count(e => e.Contains(deleteName)) == 1);

            h.Patch(delete, prefix: new HarmonyMethod(typeof(PublishBlocker).GetMethod(nameof(PublishBlocker.PrefixWithResult))));
            Check("monitor: stays incomplete (sticky) even if the prefix comes back", !monitor.IsComplete());

            Harmony.UnpatchID(id); // what another plugin can do to our id (or Harmony.UnpatchAll() to every id)
            PublishBlockStatus all = monitor.Current();
            Check("monitor: UnpatchID by another plugin -> every required target listed, one more Error for the new one",
                !all.Complete && all.Missing.Count == 2 && log.Errors.Count(e => e.Contains(PublishBlocker.Describe(publish))) == 1);

            PublishBlockStatus failed = new PublishBlockStatus(true, false, 1, new[] { "Game.Gone(): method not found" });
            PublishBlockMonitor startupFailed = new PublishBlockMonitor(failed, targets, id, log);
            Check("monitor: an incomplete install stays incomplete with its own missing list",
                !startupFailed.IsComplete() && startupFailed.Current().Missing[0].StartsWith("Game.Gone()"));
        }

        private static BlockTarget T(MethodBase m, bool required) =>
            new BlockTarget { Name = PublishBlocker.Describe(m), Layer = "test", Required = required, Method = m };

        private static void Check(string name, bool ok)
        {
            if (ok)
            {
                s_passed++;
                Console.WriteLine("  PASS  " + name);
            }
            else
            {
                s_failed++;
                s_failures.Add(name);
                Console.WriteLine("  FAIL  " + name);
            }
        }

        private sealed class ListLog : IBridgeLog
        {
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warning(string message) { lock (Warnings) Warnings.Add(message); }
            public void Error(string message) { lock (Errors) Errors.Add(message); }
        }
    }
}
