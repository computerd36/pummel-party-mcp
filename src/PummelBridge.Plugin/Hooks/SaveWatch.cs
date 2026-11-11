using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace PummelBridge.Hooks
{
    /// <summary>
    /// Harmony POSTFIX on EditorSystem.SaveCurrentMod(bool) that only records that a save happened (Ctrl+S, File > Save,
    /// the unsaved-changes popup's Save button, or the bridge's save_mod). It never changes arguments, results or game
    /// state and swallows its own exceptions. Used by the edit tracker to move its clean point after user saves.
    /// No game types are referenced here (the target is resolved by the caller), so the patch is unit-tested.
    /// </summary>
    public static class SaveWatch
    {
        public const string HarmonyId = "com.pummelbridge.plugin.savewatch";

        private static int s_saveCount;

        /// <summary>Invoked on the main thread after each completed SaveCurrentMod call.</summary>
        public static Action OnSaved;

        public static bool Installed { get; private set; }

        public static int SaveCount => Volatile.Read(ref s_saveCount);

        /// <summary>Returns null on success, otherwise the error text (the tracker then falls back to the editor's flag).</summary>
        public static string Install(Harmony harmony, MethodBase target)
        {
            if (harmony == null || target == null)
            {
                return "SaveCurrentMod not found";
            }
            try
            {
                MethodInfo postfix = typeof(SaveWatch).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                Installed = true;
                return null;
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
        }

        private static void Postfix()
        {
            Interlocked.Increment(ref s_saveCount);
            try
            {
                OnSaved?.Invoke();
            }
            catch
            {
                // never throw into the game's save path
            }
        }
    }
}
