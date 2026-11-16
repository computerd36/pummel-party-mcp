using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Hooks
{
    /// <summary>
    /// Review G: capture_preview_image calls EditorSystem.TakeScreenGrab(); the game's coroutine writes Screenshot&lt;N&gt;.jpg (and
    /// registers it) a few frames later into whatever mod is open at that moment. If the user opened another mod by hand inside that
    /// window, the file would land in that mod, which may be a real one; if the user closed the mod, the coroutine throws on the null
    /// mod and leaves the editor's "taking a screenshot" flag set (and gizmo icons / grid hidden) for the rest of the session. These
    /// Harmony PREFIXES refuse opening, creating, closing and deleting a mod (the original is skipped, one warning is logged) ONLY
    /// while a plugin-started capture is pending, and only for at most <see cref="MaxHold"/> after <see cref="Arm"/> even if nobody
    /// calls <see cref="Disarm"/>, so the editor can never get stuck. At all other times they are pure pass-throughs.
    /// <para>
    /// Targets (the caller resolves them; all are required, see <see cref="Install"/>):
    /// <list type="bullet">
    /// <item>refused as a whole: EditorSystem.OpenMod(string) (every Open path: Open Mod dialog, Recent Mods, Published Mods, all via
    /// OpenModWithPopupPrompt; nothing runs before it), NewModPopup.CreateNewMod(List) (the New Mod dialog's action: it calls
    /// EditorSystem.CreateNewMod and then always adds a mod-list entry, so it is refused as a whole), EditorSystem.CreateNewMod(string,
    /// PummelModType, string) (backstop for any other caller; it copies the template before LoadModIntoEditor, so that common callee is
    /// not used), MainMenuWidget.DeleteModResult(int) and LoadModPopup.DeleteModResult(int) (the two Delete Mod confirmations: they
    /// close the open mod and then delete its folder, so they are refused as a whole);</item>
    /// <item>EditorSystem.SetCurrentMod(PummelMod): refused only for null (Close Mod has no method of its own: the File menu calls
    /// SetCurrentMod(null) inline). Refusing skips the whole method, so no state is half-changed. Non-null calls (LoadModIntoEditor,
    /// which has already changed state when it gets there) always pass;</item>
    /// <item>EditorSystem.OnDisable: ends the hold, so the editor shutting down (exit to the main menu destroys it; its OnDisable calls
    /// SetCurrentMod(null)) is never refused. The screenshot coroutine runs on the editor object and stops with it.</item>
    /// </list>
    /// </para>
    /// No game types are referenced here, so the patch is unit-tested against stand-ins (PatchTests).
    /// </summary>
    public static class ModSwitchGuard
    {
        public const string HarmonyId = "com.pummelpartymcp.plugin.modswitchguard";

        public const string RefusalMessage =
            "Opening, creating, closing or deleting a mod was refused for a moment: capture_preview_image is writing a screenshot into the open mod. Try again in a second.";

        /// <summary>Hard upper bound of one hold, whatever happens to the capture.</summary>
        public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(8);

        private static readonly object s_lock = new object();
        private static long s_holdUntilMs; // 0 = not armed
        private static bool s_warned;
        private static int s_refused;

        /// <summary>Milliseconds clock (tests replace it).</summary>
        public static Func<long> ClockMs = DefaultClock();

        public static IPluginLog Log = NullPluginLog.Instance;

        /// <summary>Optional second sink for the refusal line (the plugin passes Unity's Debug.LogWarning, so the game console shows it).</summary>
        public static Action<string> ExtraSink;

        /// <summary>Every prefix is in place (capture_preview_image refuses to run otherwise).</summary>
        public static bool Installed { get; private set; }

        /// <summary>Calls refused so far (diagnostics, tests).</summary>
        public static int RefusedCount { get { lock (s_lock) { return s_refused; } } }

        public static bool IsHolding
        {
            get
            {
                lock (s_lock)
                {
                    return s_holdUntilMs != 0 && ClockMs() < s_holdUntilMs;
                }
            }
        }

        private static Func<long> DefaultClock()
        {
            Stopwatch sw = Stopwatch.StartNew();
            return () => sw.ElapsedMilliseconds;
        }

        /// <summary>
        /// Installs the prefixes: <paramref name="refuse"/> (refused as a whole while holding), <paramref name="close"/>
        /// (SetCurrentMod: refused only for a null argument) and <paramref name="release"/> (OnDisable: ends the hold). Every target is
        /// required. Returns null on success, otherwise the problems (then <see cref="Installed"/> is false).
        /// </summary>
        public static string Install(Harmony harmony, IList<MethodBase> refuse, MethodBase close, MethodBase release)
        {
            Installed = false;
            if (harmony == null || refuse == null || refuse.Count == 0)
            {
                return "no targets";
            }
            List<string> problems = new List<string>();
            void Patch(MethodBase target, string prefixName, string what)
            {
                if (target == null)
                {
                    problems.Add(what + " was not found");
                    return;
                }
                try
                {
                    MethodInfo prefix = typeof(ModSwitchGuard).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                }
                catch (Exception e)
                {
                    problems.Add(target.Name + ": " + e.GetType().Name + ": " + e.Message);
                }
            }
            for (int i = 0; i < refuse.Count; i++)
            {
                Patch(refuse[i], nameof(Prefix), "guard target #" + (i + 1));
            }
            Patch(close, nameof(ClosePrefix), "the close-mod target (SetCurrentMod)");
            Patch(release, nameof(ReleasePrefix), "the editor shutdown target (OnDisable)");
            if (problems.Count > 0)
            {
                return string.Join("; ", problems.ToArray());
            }
            Installed = true;
            return null;
        }

        /// <summary>Starts a hold (main thread, right before TakeScreenGrab). Ends at <see cref="Disarm"/> or after <see cref="MaxHold"/>.</summary>
        public static void Arm()
        {
            lock (s_lock)
            {
                s_holdUntilMs = Math.Max(1, ClockMs() + (long)MaxHold.TotalMilliseconds);
                s_warned = false;
            }
        }

        public static void Disarm()
        {
            lock (s_lock)
            {
                s_holdUntilMs = 0;
            }
        }

        /// <summary>SetCurrentMod: only closing (null) is refused while holding; switching to a mod passes (its open was already guarded).</summary>
        private static bool ClosePrefix(object[] __args)
        {
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] != null)
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }
            return Prefix();
        }

        /// <summary>EditorSystem.OnDisable: the editor is shutting down (its coroutine stops with it): end the hold so nothing it does is refused.</summary>
        private static void ReleasePrefix()
        {
            try
            {
                Disarm();
            }
            catch
            {
                // never throw into the game
            }
        }

        /// <summary>True = run the original. Never throws into the game.</summary>
        private static bool Prefix()
        {
            try
            {
                bool warn;
                lock (s_lock)
                {
                    if (s_holdUntilMs == 0)
                    {
                        return true;
                    }
                    if (ClockMs() >= s_holdUntilMs)
                    {
                        s_holdUntilMs = 0; // expired: never hold longer than MaxHold
                        return true;
                    }
                    s_refused++;
                    warn = !s_warned;
                    s_warned = true;
                }
                if (warn)
                {
                    Log.Warning(RefusalMessage);
                    try
                    {
                        ExtraSink?.Invoke("[PummelPartyMcp] " + RefusalMessage);
                    }
                    catch
                    {
                        // the sink must not turn the refusal into an exception in the game
                    }
                }
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
