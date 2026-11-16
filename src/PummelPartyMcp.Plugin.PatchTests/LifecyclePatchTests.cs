using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PummelPartyMcp.Core;
using PummelPartyMcp.Hooks;
using PummelPartyMcp.Safety;

namespace PummelPartyMcp.PatchTests
{
    /// <summary>Stand-ins for the editor-scoped patch cycles (decision P2): patched at editor open, removed at editor close.</summary>
    public static class CycleTargets
    {
        public static int Runs;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Publish() { Runs++; return 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Delete(ulong id) { Runs++; return true; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Submit() { Runs++; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Optional() { Runs++; return 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Shared() { Runs++; return 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Measured() { Runs++; return 7; }
    }

    /// <summary>Like ModLogicTrigger.RunActionsLocal / ModAction.Execute: instance methods that get a record-only postfix.</summary>
    public sealed class FakeLogic
    {
        public int Runs;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(int e) { Runs++; }
    }

    public static class CountingPostfix
    {
        public static int Calls;

        public static void Postfix() => Calls++;
    }

    public static class ForeignPrefix
    {
        public static int Calls;

        public static void Prefix() => Calls++;
    }

    /// <summary>Design tests P1-P6 against real HarmonyX on stand-ins (.NET Framework CLR, not Unity's Mono).</summary>
    internal static class LifecyclePatchTests
    {
        private static Action<string, bool> Check;

        public static void Run(Action<string, bool> check)
        {
            Check = check;
            Console.WriteLine("-- editor-scoped patches (P2): apply, remove, re-apply");
            PublishBlocker.Configure(NullPluginLog.Instance, null);
            Type t = typeof(CycleTargets);
            MethodInfo publish = t.GetMethod("Publish"), delete = t.GetMethod("Delete"), submit = t.GetMethod("Submit"), optional = t.GetMethod("Optional");
            List<BlockTarget> targets = new List<BlockTarget> { T(publish, true), T(delete, true), T(submit, true), T(optional, false) };
            string id = PublishBlocker.HarmonyId;

            Check("PatchRemoval: nothing patched -> nothing removed, clean", PatchRemoval.Remove(id) is PatchRemoval.Result r0 && r0.Removed == 0 && r0.Clean);

            // P1
            CycleTargets.Runs = 0;
            PublishBlockStatus st = PublishBlocker.Apply(new Harmony(id), targets);
            Check("P1 apply: complete, originals blocked", st.Complete && CycleTargets.Publish() == 0 && !CycleTargets.Delete(1) && CycleTargets.Runs == 0);
            PatchRemoval.Result removed = PublishBlocker.Remove();
            Check("P1 Remove: every patched method restored, none left", removed.Removed == 4 && removed.Clean && PatchRemoval.PatchedBy(id).Count == 0);
            Check("P1 Remove: no prefix with our id in the patch info", targets.All(x => !PublishBlocker.HasBlockingPrefix(x.Method, id)));
            CycleTargets.Submit();
            Check("P1 after Remove the stand-in originals run again", CycleTargets.Publish() == 1 && CycleTargets.Delete(1) && CycleTargets.Runs == 3);

            // P2 + P3
            PublishBlockMonitor old = null;
            bool cycles = true, between = true, once = true;
            for (int i = 0; i < 10; i++)
            {
                PublishBlockStatus s = PublishBlocker.Apply(new Harmony(id), targets);
                PublishBlockMonitor m = new PublishBlockMonitor(s, targets, id, NullPluginLog.Instance);
                cycles &= s.Complete && s.PatchedCount == 4 && m.IsComplete();
                once &= targets.All(x => Harmony.GetPatchInfo(x.Method).Prefixes.Count(p => p.owner == id) == 1);
                CycleTargets.Runs = 0;
                cycles &= CycleTargets.Publish() == 0 && CycleTargets.Runs == 0;
                if (i == 4)
                {
                    old = m;
                }
                PatchRemoval.Result r = PublishBlocker.Remove();
                cycles &= r.Removed == 4 && r.Clean;
                between &= CycleTargets.Publish() == 1 && CycleTargets.Runs == 1;
            }
            Check("P2 ten apply/remove cycles: each apply complete with a complete monitor, each remove clean", cycles);
            Check("P2 the originals run between cycles", between);
            Check("P2 each cycle has exactly one prefix of ours per target (no duplicates)", once);
            Check("P3 an old monitor is sticky-incomplete after its patches were removed", old != null && !old.IsComplete());
            PublishBlockStatus again = PublishBlocker.Apply(new Harmony(id), targets);
            PublishBlockMonitor fresh = new PublishBlockMonitor(again, targets, id, NullPluginLog.Instance);
            Check("P3 a new monitor after re-apply is complete although the old one stays incomplete", fresh.IsComplete() && !old.IsComplete());

            // removal touches only our id
            MethodInfo shared = t.GetMethod("Shared");
            PublishBlocker.Apply(new Harmony(id), new[] { T(shared, true) });
            new Harmony("someone.else").Patch(shared, prefix: new HarmonyMethod(typeof(ForeignPrefix).GetMethod("Prefix")));
            PatchRemoval.Result mine = PublishBlocker.Remove();
            ForeignPrefix.Calls = 0;
            CycleTargets.Runs = 0;
            int sharedResult = CycleTargets.Shared();
            Check("Remove: another plugin's patch on the same method stays, and the original runs again",
                mine.Clean && sharedResult == 1 && CycleTargets.Runs == 1 && ForeignPrefix.Calls == 1 && Harmony.GetPatchInfo(shared).Owners.Contains("someone.else"));
            Harmony.UnpatchID("someone.else");
            Check("PatchRemoval: an empty id is refused, not 'every id'", !PatchRemoval.Remove("").Clean);

            HooksCycle();
            Informational();
        }

        /// <summary>P4: save watch and mod-switch guard installed, removed and installed again: the hook runs once per call.</summary>
        private static void HooksCycle()
        {
            MethodInfo save = typeof(FakeEditor).GetMethod("SaveCurrentMod", new[] { typeof(bool) });
            PatchRemoval.Remove("pummelpartymcp.patchtests.sw"); // the earlier SaveWatch test's postfix (another id) would double the count
            int observed = 0;
            bool ok = true;
            FakeEditor editor = new FakeEditor();
            for (int i = 0; i < 3; i++)
            {
                ok &= SaveWatch.Install(new Harmony(SaveWatch.HarmonyId), save) == null && SaveWatch.Installed;
                SaveWatch.OnSaved = () => observed++;
                int before = observed;
                editor.SaveCurrentMod();
                ok &= observed == before + 1;
                PatchRemoval.Result r = SaveWatch.Uninstall();
                ok &= r.Removed == 1 && r.Clean && !SaveWatch.Installed && SaveWatch.OnSaved == null;
                editor.SaveCurrentMod();
                ok &= observed == before + 1;
            }
            Check("P4 save watch install/uninstall x3: the postfix runs exactly once per save while installed, never after uninstall", ok && editor.Saves == 6);

            MethodInfo open = typeof(FakeModEditor).GetMethod("OpenMod");
            MethodInfo setMod = typeof(FakeModEditor).GetMethod("SetCurrentMod");
            MethodInfo onDisable = typeof(FakeModEditor).GetMethod("OnDisable");
            long now = 0;
            ModSwitchGuard.ClockMs = () => now;
            FakeModEditor ed = new FakeModEditor();
            bool guard = true;
            for (int i = 0; i < 3; i++)
            {
                guard &= ModSwitchGuard.Install(new Harmony(ModSwitchGuard.HarmonyId), new MethodBase[] { open }, setMod, onDisable) == null && ModSwitchGuard.Installed;
                guard &= Harmony.GetPatchInfo(open).Prefixes.Count(p => p.owner == ModSwitchGuard.HarmonyId) == 1;
                ModSwitchGuard.Arm();
                int opens = ed.Opens;
                ed.OpenMod("x");
                guard &= ed.Opens == opens;
                PatchRemoval.Result r = ModSwitchGuard.Uninstall();
                guard &= r.Removed == 3 && r.Clean && !ModSwitchGuard.Installed && !ModSwitchGuard.IsHolding;
                ed.OpenMod("y");
                guard &= ed.Opens == opens + 1;
            }
            Check("P4 mod-switch guard install/uninstall x3: one prefix per session, the hold ends at uninstall, pass-through after it", guard);

            MethodInfo exec = typeof(FakeLogic).GetMethod("Execute");
            const string hookId = "pummelpartymcp.patchtests.hooks";
            FakeLogic logic = new FakeLogic();
            bool post = true;
            for (int i = 0; i < 3; i++)
            {
                new Harmony(hookId).Patch(exec, postfix: new HarmonyMethod(typeof(CountingPostfix).GetMethod("Postfix")));
                CountingPostfix.Calls = 0;
                logic.Execute(1);
                post &= CountingPostfix.Calls == 1;
                post &= PatchRemoval.Remove(hookId).Clean;
                logic.Execute(2);
                post &= CountingPostfix.Calls == 1;
            }
            Check("P4 record-only postfix (playtest-hook-like) patch/remove x3: runs once per call, not at all after removal", post && logic.Runs == 6);
        }

        /// <summary>P5 and P6: printed only (the .NET Framework CLR is not Unity's Mono).</summary>
        private static void Informational()
        {
            MethodInfo m = typeof(CycleTargets).GetMethod("Measured");
            CycleTargets.Measured();
            IntPtr before = m.MethodHandle.GetFunctionPointer();
            const string id = "pummelpartymcp.patchtests.p5";
            new Harmony(id).Patch(m, prefix: new HarmonyMethod(typeof(ForeignPrefix).GetMethod("Prefix")));
            IntPtr patched = m.MethodHandle.GetFunctionPointer();
            new Harmony(id).UnpatchSelf();
            IntPtr after = m.MethodHandle.GetFunctionPointer();
            bool listed = Harmony.GetAllPatchedMethods().Contains(m);
            ForeignPrefix.Calls = 0;
            int value = CycleTargets.Measured();
            Console.WriteLine($"  INFO  P5 HarmonyX after UnpatchSelf: function pointer {(after == before ? "equals the original" : "differs from the original")}, " +
                              $"while patched {(patched == before ? "also equal (the detour is written into the code, so this pointer cannot show whether it stays)" : "different")}; " +
                              $"still listed by GetAllPatchedMethods: {listed}; original behaviour: {value == 7 && ForeignPrefix.Calls == 0}");
            Check("P5 after UnpatchSelf the original behaviour is back (no prefix runs)", value == 7 && ForeignPrefix.Calls == 0);

            // P6: patch and remove the stand-ins a number of times as a rough cost per patched method
            List<BlockTarget> targets = new[] { "Publish", "Delete", "Submit", "Optional", "Shared", "Measured" }
                .Select(n => T(typeof(CycleTargets).GetMethod(n), true)).ToList();
            const string pid = "pummelpartymcp.patchtests.p6";
            const int rounds = 12;
            Stopwatch apply = new Stopwatch(), remove = new Stopwatch();
            for (int i = 0; i < rounds; i++)
            {
                apply.Start();
                PublishBlocker.Apply(new Harmony(pid), targets);
                apply.Stop();
                remove.Start();
                PatchRemoval.Remove(pid);
                remove.Stop();
            }
            int n = rounds * targets.Count;
            double perApply = apply.Elapsed.TotalMilliseconds / n, perRemove = remove.Elapsed.TotalMilliseconds / n;
            Console.WriteLine($"  INFO  P6 per patched method: apply {perApply:0.00} ms, remove {perRemove:0.00} ms; " +
                              $"for 69 methods about {perApply * 69:0} ms + {perRemove * 69:0} ms (this CLR; the game's Mono may differ)");
        }

        private static BlockTarget T(MethodBase m, bool required) =>
            new BlockTarget { Name = PublishBlocker.Describe(m), Layer = "test", Required = required, Method = m };
    }
}
