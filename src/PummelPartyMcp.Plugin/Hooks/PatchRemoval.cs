using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace PummelPartyMcp.Hooks
{
    /// <summary>
    /// Removes every patch one Harmony id owns and checks that none is left (decision P2: the plugin's patches exist only while
    /// the Mod Editor is open). Unpatches method by method, so one failure does not stop the rest. Other ids are never touched.
    /// HarmonyX "unpatches" by re-patching with the remaining patches: a method may keep a generated copy of its original code
    /// after our last patch is gone, so behaviour is the original one but the machine code may not be. No game types are
    /// referenced here, so the PatchTests cover it on stand-ins.
    /// </summary>
    public static class PatchRemoval
    {
        public sealed class Result
        {
            /// <summary>Methods that carried a patch of the id before the removal.</summary>
            public int Removed;

            /// <summary>Methods that still carry a patch of the id afterwards (display names); empty when clean.</summary>
            public List<string> Left = new List<string>();

            /// <summary>Unpatch errors (display names and messages); empty when clean.</summary>
            public List<string> Errors = new List<string>();

            public bool Clean => Left.Count == 0 && Errors.Count == 0;
        }

        /// <summary>Methods that carry at least one patch (prefix, postfix, transpiler, finalizer) owned by <paramref name="harmonyId"/>.</summary>
        public static List<MethodBase> PatchedBy(string harmonyId)
        {
            List<MethodBase> list = new List<MethodBase>();
            foreach (MethodBase m in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info = Harmony.GetPatchInfo(m);
                if (info != null && info.Owners.Contains(harmonyId))
                {
                    list.Add(m);
                }
            }
            return list;
        }

        /// <summary>Removes every patch of <paramref name="harmonyId"/>; never throws.</summary>
        public static Result Remove(string harmonyId)
        {
            Result r = new Result();
            if (string.IsNullOrEmpty(harmonyId))
            {
                r.Errors.Add("no Harmony id");
                return r;
            }
            List<MethodBase> before;
            try
            {
                before = PatchedBy(harmonyId);
            }
            catch (Exception e)
            {
                r.Errors.Add("patch list: " + e.GetType().Name + ": " + e.Message);
                return r;
            }
            r.Removed = before.Count;
            Harmony harmony = new Harmony(harmonyId);
            foreach (MethodBase m in before)
            {
                try
                {
                    harmony.Unpatch(m, HarmonyPatchType.All, harmonyId);
                }
                catch (Exception e)
                {
                    r.Errors.Add(Describe(m) + ": " + e.GetType().Name + ": " + e.Message);
                }
            }
            try
            {
                r.Left = PatchedBy(harmonyId).Select(Describe).ToList();
            }
            catch (Exception e)
            {
                r.Errors.Add("patch check: " + e.GetType().Name + ": " + e.Message);
            }
            r.Removed -= Math.Min(r.Removed, r.Left.Count);
            return r;
        }

        private static string Describe(MethodBase m) => m == null ? "?" : m.DeclaringType?.Name + "." + m.Name;
    }
}
