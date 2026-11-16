using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ModSystem.Logic;

namespace PummelPartyMcp.Hooks
{
    /// <summary>
    /// Recording-only Harmony POSTFIXES for the playtest event feed: ModLogicTrigger.RunActionsLocal (a trigger ran its action
    /// list) and every declared override of ModAction.Execute (an action ran). They never change arguments, results or game
    /// state, return at once unless a playtest is in PlayMode (<see cref="Collecting"/>), and swallow every exception (a postfix
    /// that throws would otherwise propagate into the game's action runner). Each target is patched separately: a target that
    /// cannot be patched only reduces what the feed records; the diff-based events (health, score, deaths, rounds) do not depend
    /// on these hooks. Own Harmony id; same precedent as <see cref="SaveWatch"/>. Installed when the Mod Editor opens and removed
    /// when it closes (decision P2), so no postfix runs in matches outside the editor.
    /// </summary>
    internal static class PlaytestHooks
    {
        public const string HarmonyId = "com.pummelpartymcp.plugin.playtesthooks";

        /// <summary>Set by the per-frame watch: true only while the editor is in PlayMode.</summary>
        public static volatile bool Collecting;

        public static int TriggerHooks { get; private set; }
        public static int ActionHooks { get; private set; }

        /// <summary>True when at least one hook is installed (logic events can appear in the feed).</summary>
        public static bool LogicEvents => TriggerHooks + ActionHooks > 0;

        /// <summary>Patches the targets; returns the problems (empty list = everything installed).</summary>
        public static List<string> Install(Harmony harmony)
        {
            List<string> problems = new List<string>();
            TriggerHooks = 0; // counted per editor session: a second install must not double them
            ActionHooks = 0;
            if (harmony == null)
            {
                problems.Add("no Harmony instance");
                return problems;
            }
            try
            {
                MethodInfo trigger = typeof(ModLogicTrigger).GetMethod("RunActionsLocal", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(ModEvent) }, null);
                MethodInfo triggerPostfix = typeof(PlaytestHooks).GetMethod(nameof(TriggerPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                if (trigger == null)
                {
                    problems.Add("ModLogicTrigger.RunActionsLocal(ModEvent) not found");
                }
                else if (TryPatch(harmony, trigger, triggerPostfix, problems))
                {
                    TriggerHooks++;
                }

                MethodInfo actionPostfix = typeof(PlaytestHooks).GetMethod(nameof(ActionPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                int found = 0;
                foreach (MethodInfo m in ActionExecuteMethods(typeof(ModAction).Assembly))
                {
                    found++;
                    if (TryPatch(harmony, m, actionPostfix, problems))
                    {
                        ActionHooks++;
                    }
                }
                if (found == 0)
                {
                    problems.Add("no ModAction.Execute(ModEvent) found");
                }
            }
            catch (Exception e)
            {
                problems.Add(e.GetType().Name + ": " + e.Message);
            }
            return problems;
        }

        /// <summary>Stops collecting and removes the postfixes (the Mod Editor closed). Never throws.</summary>
        public static PatchRemoval.Result Uninstall()
        {
            Collecting = false;
            TriggerHooks = 0;
            ActionHooks = 0;
            return PatchRemoval.Remove(HarmonyId);
        }

        /// <summary>ModAction.Execute(ModEvent) and every override declared by a subclass (resolved by reflection; the drift test counts them).</summary>
        internal static List<MethodInfo> ActionExecuteMethods(Assembly game)
        {
            List<MethodInfo> list = new List<MethodInfo>();
            Type baseType = game.GetType("ModSystem.Logic.ModAction", false);
            Type eventType = game.GetType("ModSystem.Logic.ModEvent", false);
            if (baseType == null || eventType == null)
            {
                return list;
            }
            Type[] types;
            try
            {
                types = game.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = Array.FindAll(e.Types, t => t != null); // a type that fails to load is not an action
            }
            foreach (Type t in types)
            {
                if (t != baseType && !t.IsSubclassOf(baseType))
                {
                    continue;
                }
                MethodInfo m = t.GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { eventType }, null);
                if (m != null && !m.IsAbstract)
                {
                    list.Add(m);
                }
            }
            return list;
        }

        private static bool TryPatch(Harmony harmony, MethodBase target, MethodInfo postfix, List<string> problems)
        {
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                return true;
            }
            catch (Exception e)
            {
                problems.Add(target.DeclaringType?.Name + "." + target.Name + ": " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static void TriggerPostfix(ModLogicTrigger __instance, ModEvent __0)
        {
            if (!Collecting)
            {
                return;
            }
            try
            {
                Game.PlaytestLogicRecorder.OnTrigger(__instance, __0);
            }
            catch
            {
                // never throw into the game's action runner
            }
        }

        private static void ActionPostfix(ModAction __instance, ModEvent __0)
        {
            if (!Collecting)
            {
                return;
            }
            try
            {
                Game.PlaytestLogicRecorder.OnAction(__instance, __0);
            }
            catch
            {
                // never throw into the game's action runner
            }
        }
    }
}
