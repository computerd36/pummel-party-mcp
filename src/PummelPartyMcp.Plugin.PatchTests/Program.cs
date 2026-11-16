using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PummelPartyMcp.Core;
using PummelPartyMcp.Hooks;
using PummelPartyMcp.Safety;

namespace PummelPartyMcp.PatchTests
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

    /// <summary>Like EditorSystem.OpenMod(string) / CreateNewMod(string, PummelModType, string): record their runs.</summary>
    public sealed class FakeModEditor
    {
        public int Opens;
        public int Creates;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OpenMod(string path) { Opens++; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CreateNewMod(string path, int modType, string templatePath = null) { Creates++; }

        public object CurrentMod = "MCP_Mod";
        public int SetCalls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Spare() { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetCurrentMod(object newMod) { SetCalls++; CurrentMod = newMod; }

        /// <summary>Like EditorSystem.OnDisable: closes the mod as the editor shuts down.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnDisable() { SetCurrentMod(null); }
    }

    /// <summary>Like NewModPopup.CreateNewMod(List): creates through the editor, then ALWAYS adds a mod-list entry.</summary>
    public sealed class FakeNewModPopup
    {
        public readonly List<string> ModList = new List<string>();
        public FakeModEditor Editor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateNewMod(List<int> options) { Editor.CreateNewMod("p", 1); ModList.Add("new"); }

        public void ClickOk() => CreateNewMod(new List<int>());
    }

    /// <summary>Like MainMenuWidget/LoadModPopup.DeleteModResult(int): closes the open mod, then deletes its folder.</summary>
    public sealed class FakeDeleteDialog
    {
        public FakeModEditor Editor;
        public int Deleted;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DeleteModResult(int result) { Editor.SetCurrentMod(null); Deleted++; }

        public void Confirm() => DeleteModResult(0);
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
            Console.WriteLine("pummel-party-mcp publish-block patch test (Harmony on " + Environment.Version + ")");
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
            Harmony harmony = new Harmony("pummelpartymcp.patchtests");
            PublishBlockStatus st = PublishBlocker.Apply(harmony, targets);
            Check("all targets patched, block complete", st.Active && st.Complete && st.PatchedCount == 8 && st.Missing.Count == 0);
            Check("prefix registered with our Harmony id at Priority.Last",
                Harmony.GetPatchInfo(createItem).Prefixes.Any(p => p.owner == "pummelpartymcp.patchtests" && p.priority == Priority.Last));

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
            Check("Unity sink got the [PummelPartyMcp] line", unity.Any(u => u.StartsWith("[PummelPartyMcp] " + PublishBlocker.UserMessage) && u.Contains(createName)));

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
            PublishBlockStatus missing = PublishBlocker.Apply(new Harmony("pummelpartymcp.patchtests.2"), new[]
            {
                new BlockTarget { Name = "Game.Gone()", Layer = "game", Required = true, Problem = "method not found" },
                new BlockTarget { Name = "Opt.Gone()", Layer = "SteamUGC", Required = false, Problem = "not found" },
                T(typeof(FakeSteamUGC).GetMethod("NotPatched"), true),
            });
            Check("missing required target -> incomplete, named in Missing, Error logged",
                !missing.Complete && missing.Active && missing.PatchedCount == 1 && missing.Missing.Count == 1 && missing.Missing[0].StartsWith("Game.Gone()")
                && log2.Errors.Any(e => e.Contains("Game.Gone()")));
            Check("missing optional target -> warning only", log2.Warnings.Any(w => w.Contains("Opt.Gone()")) && !missing.Missing.Any(m => m.StartsWith("Opt.Gone")));

            PublishBlockStatus abstractFail = PublishBlocker.Apply(new Harmony("pummelpartymcp.patchtests.3"), new[]
            {
                T(typeof(IDisposable).GetMethod("Dispose"), false), // abstract interface method: cannot be patched
            });
            Check("a patch that throws (even optional) -> incomplete, reported", !abstractFail.Complete && abstractFail.Missing.Count == 1 && abstractFail.Missing[0].Contains("patch failed"));
            Check("nothing patched -> not complete", !PublishBlocker.Apply(new Harmony("pummelpartymcp.patchtests.4"), new BlockTarget[0]).Complete);

            // ---- live publish-block check (PublishBlockMonitor) ----
            MonitorTests();

            // ---- offline drift test against the real game assemblies (reflection only, no patch applied) ----
            GameDriftTest.Run(Check);

            // ---- SaveWatch (postfix on SaveCurrentMod that only records) ----
            MethodInfo save = typeof(FakeEditor).GetMethod("SaveCurrentMod", new[] { typeof(bool) });
            Check("SaveWatch.Install with null target reports an error", SaveWatch.Install(new Harmony("pummelpartymcp.patchtests.sw0"), null) != null && !SaveWatch.Installed);
            Check("SaveWatch installs", SaveWatch.Install(new Harmony("pummelpartymcp.patchtests.sw"), save) == null && SaveWatch.Installed);
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

            ModSwitchGuardTests();

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
            const string id = "pummelpartymcp.patchtests.monitor";
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

        /// <summary>Review G: the prefix that refuses opening/creating a mod only while capture_preview_image waits for the editor's screenshot.</summary>
        private static void ModSwitchGuardTests()
        {
            Console.WriteLine("-- mod-switch guard (capture_preview_image)");
            ListLog log = new ListLog();
            List<string> sink = new List<string>();
            long now = 1000;
            ModSwitchGuard.ClockMs = () => now;
            ModSwitchGuard.Log = log;
            ModSwitchGuard.ExtraSink = sink.Add;
            const BindingFlags priv = BindingFlags.Instance | BindingFlags.NonPublic;
            MethodInfo open = typeof(FakeModEditor).GetMethod("OpenMod");
            MethodInfo create = typeof(FakeModEditor).GetMethod("CreateNewMod");
            MethodInfo setMod = typeof(FakeModEditor).GetMethod("SetCurrentMod");
            MethodInfo onDisable = typeof(FakeModEditor).GetMethod("OnDisable");
            MethodInfo newModDialog = typeof(FakeNewModPopup).GetMethod("CreateNewMod", priv);
            MethodInfo deleteDialog = typeof(FakeDeleteDialog).GetMethod("DeleteModResult", priv);

            Check("guard: no targets -> not installed", ModSwitchGuard.Install(new Harmony("pummelpartymcp.patchtests.msg0"), new MethodBase[0], setMod, onDisable) != null && !ModSwitchGuard.Installed);
            Check("guard: a missing refuse target -> not installed (capture_preview_image then refuses)",
                ModSwitchGuard.Install(new Harmony("pummelpartymcp.patchtests.msg1"), new MethodBase[] { null }, null, null) != null && !ModSwitchGuard.Installed);
            Check("guard: a missing close or shutdown target -> not installed",
                ModSwitchGuard.Install(new Harmony("pummelpartymcp.patchtests.msg2"), new MethodBase[] { typeof(FakeModEditor).GetMethod("Spare") }, null, onDisable) != null && !ModSwitchGuard.Installed);
            Check("guard: installs on every target", ModSwitchGuard.Install(new Harmony("pummelpartymcp.patchtests.msg"),
                new MethodBase[] { open, newModDialog, create, deleteDialog }, setMod, onDisable) == null && ModSwitchGuard.Installed);

            FakeModEditor ed = new FakeModEditor();
            ed.OpenMod("a");
            ed.CreateNewMod("b", 1);
            Check("guard: pure pass-through while not armed", ed.Opens == 1 && ed.Creates == 1 && !ModSwitchGuard.IsHolding && log.Warnings.Count == 0);

            ModSwitchGuard.Arm();
            int refusedBefore = ModSwitchGuard.RefusedCount;
            ed.OpenMod("c");
            ed.CreateNewMod("d", 1, "template");
            ed.OpenMod("e");
            Check("guard: armed -> opening and creating a mod are refused (originals not run)", ed.Opens == 1 && ed.Creates == 1 && ModSwitchGuard.RefusedCount == refusedBefore + 3);
            Check("guard: one warning per hold (not per refused call), also to the extra sink",
                log.Warnings.Count == 1 && log.Warnings[0] == ModSwitchGuard.RefusalMessage && sink.Count == 1 && sink[0].Contains(ModSwitchGuard.RefusalMessage));

            ModSwitchGuard.Disarm();
            ed.OpenMod("f");
            Check("guard: disarmed -> pass-through again", ed.Opens == 2 && !ModSwitchGuard.IsHolding);

            ModSwitchGuard.Arm();
            now += (long)ModSwitchGuard.MaxHold.TotalMilliseconds - 1;
            ed.OpenMod("g");
            Check("guard: still refused just before the hard bound", ed.Opens == 2 && ModSwitchGuard.IsHolding);
            now += 1;
            ed.OpenMod("h");
            ed.CreateNewMod("i", 0);
            Check("guard: the hold ends by itself after MaxHold even without Disarm (the editor never gets stuck)", ed.Opens == 3 && ed.Creates == 2 && !ModSwitchGuard.IsHolding);
            Check("guard: the hold is bounded by a few seconds", ModSwitchGuard.MaxHold > TimeSpan.Zero && ModSwitchGuard.MaxHold <= TimeSpan.FromSeconds(10));

            int warningsBefore = log.Warnings.Count;
            ModSwitchGuard.Arm();
            ed.OpenMod("j");
            ed.OpenMod("j2");
            Check("guard: a new hold warns again (once)", ed.Opens == 3 && log.Warnings.Count == warningsBefore + 1);
            ModSwitchGuard.ExtraSink = s => throw new Exception("sink bug");
            ModSwitchGuard.Arm();
            bool threw = false;
            try { ed.OpenMod("k"); } catch (Exception) { threw = true; }
            Check("guard: a throwing sink never propagates into the game (call still refused)", !threw && ed.Opens == 3);
            ModSwitchGuard.ExtraSink = null;

            // review follow-up 1: the New Mod dialog's action is refused as a whole (no mod-list entry without a mod)
            FakeNewModPopup dialog = new FakeNewModPopup { Editor = ed };
            int createsBefore = ed.Creates;
            dialog.ClickOk();
            Check("guard: New Mod dialog refused as a whole while holding: no mod created and no mod-list entry", ed.Creates == createsBefore && dialog.ModList.Count == 0);
            // review follow-up 2: Close Mod (SetCurrentMod(null)) and Delete Mod (close, then delete the folder) are refused while holding
            ed.SetCurrentMod(null);
            Check("guard: closing the mod (SetCurrentMod(null)) is refused while holding: the mod stays open, nothing in SetCurrentMod ran",
                Equals(ed.CurrentMod, "MCP_Mod") && ed.SetCalls == 0);
            ed.SetCurrentMod("Other");
            Check("guard: SetCurrentMod with a mod always passes (the load already changed state when it gets there)", Equals(ed.CurrentMod, "Other") && ed.SetCalls == 1);
            ed.CurrentMod = "MCP_Mod";
            FakeDeleteDialog delete = new FakeDeleteDialog { Editor = ed };
            delete.Confirm();
            Check("guard: Delete Mod refused as a whole while holding (the open mod is not closed, no folder deleted)", delete.Deleted == 0 && Equals(ed.CurrentMod, "MCP_Mod"));
            ed.OnDisable();
            Check("guard: the editor shutting down (OnDisable) ends the hold, so its SetCurrentMod(null) runs", ed.CurrentMod == null && !ModSwitchGuard.IsHolding);
            ed.CurrentMod = "MCP_Mod";
            dialog.ClickOk();
            delete.Confirm();
            ed.SetCurrentMod(null);
            Check("guard: not holding -> New Mod, Delete Mod and Close Mod run unchanged", dialog.ModList.Count == 1 && ed.Creates == createsBefore + 1 && delete.Deleted == 1 && ed.CurrentMod == null);
            ModSwitchGuard.Disarm();
            ModSwitchGuard.Log = NullPluginLog.Instance;
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

        private sealed class ListLog : IPluginLog
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
