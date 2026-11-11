using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PummelBridge.Core;

namespace PummelBridge.Safety
{
    /// <summary>One method to block. <see cref="Method"/> is null when it could not be resolved.</summary>
    public sealed class BlockTarget
    {
        /// <summary>Display name, e.g. "WorkshopPopup.PublishMod(PummelMod)".</summary>
        public string Name;
        /// <summary>"game", "SteamUGC" or "SteamRemoteStorage".</summary>
        public string Layer;
        /// <summary>A missing or unpatchable required target makes the block incomplete (write tools then refuse).</summary>
        public bool Required;
        public MethodBase Method;
        /// <summary>Why <see cref="Method"/> is null.</summary>
        public string Problem;
    }

    /// <summary>
    /// Installs Harmony prefixes that skip the original method and return a safe default. Unity-free and
    /// Steamworks-free (the target list is built elsewhere), so the mechanism can be tested on stand-in methods.
    /// Patches are never removed by the plugin; <see cref="PublishBlockMonitor"/> notices when someone else removes them.
    /// </summary>
    public static class PublishBlocker
    {
        public const string HarmonyId = "com.pummelbridge.plugin.publishblock";

        public const string UserMessage = "Workshop publishing is blocked while the PummelBridge plugin is enabled.";

        public const string HowToPublish =
            "To publish, set [General] PluginEnabled = false in BepInEx\\config\\com.pummelbridge.plugin.cfg and restart the game.";

        /// <summary>A given method is logged at most once per interval (first call immediately).</summary>
        public static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(10);

        private static readonly MethodInfo s_prefixVoid = typeof(PublishBlocker).GetMethod(nameof(PrefixVoid), BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo s_prefixResult = typeof(PublishBlocker).GetMethod(nameof(PrefixWithResult), BindingFlags.Public | BindingFlags.Static);

        private static readonly ConcurrentDictionary<Type, object> s_safeResults = new ConcurrentDictionary<Type, object>();
        private static readonly Dictionary<string, RateState> s_rate = new Dictionary<string, RateState>(StringComparer.Ordinal);
        private static readonly object s_rateLock = new object();

        private static IBridgeLog s_log = NullBridgeLog.Instance;
        private static Action<string> s_extraSink;

        /// <summary>Milliseconds clock; replaceable in tests.</summary>
        internal static Func<long> ClockMs = () => Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

        /// <param name="log">BepInEx log (Warning per blocked call, Error per failed patch).</param>
        /// <param name="extraSink">Second destination for the blocked-call line (the plugin passes Unity's Debug.LogWarning so the editor's Log window shows it).</param>
        public static void Configure(IBridgeLog log, Action<string> extraSink)
        {
            s_log = log ?? NullBridgeLog.Instance;
            s_extraSink = extraSink;
        }

        /// <summary>Patches every resolvable target. Fail closed: any missing required target or any patch failure makes the result incomplete.</summary>
        public static PublishBlockStatus Apply(Harmony harmony, IEnumerable<BlockTarget> targets)
        {
            if (harmony == null)
            {
                throw new ArgumentNullException(nameof(harmony));
            }
            List<string> missing = new List<string>();
            HashSet<MethodBase> seen = new HashSet<MethodBase>();
            int patched = 0;
            bool complete = true;

            foreach (BlockTarget target in targets ?? Enumerable.Empty<BlockTarget>())
            {
                if (target == null)
                {
                    continue;
                }
                string name = target.Name ?? (target.Method != null ? Describe(target.Method) : "?");
                if (target.Method == null)
                {
                    string why = target.Problem ?? "not found";
                    if (target.Required)
                    {
                        complete = false;
                        missing.Add($"{name}: {why}");
                        s_log.Error($"Publish block: required target {name} could not be resolved ({why}). Write tools are disabled.");
                    }
                    else
                    {
                        s_log.Warning($"Publish block: optional target {name} could not be resolved ({why}).");
                    }
                    continue;
                }
                if (!seen.Add(target.Method))
                {
                    continue; // listed twice (e.g. core method also matched by enumeration)
                }
                try
                {
                    PatchOne(harmony, target.Method);
                    patched++;
                    s_log.Debug($"Publish block: patched [{target.Layer}] {name}");
                }
                catch (Exception e)
                {
                    complete = false;
                    Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                    missing.Add($"{name}: patch failed ({inner.GetType().Name}: {inner.Message})");
                    s_log.Error($"Publish block: patching {name} failed: {inner.GetType().Name}: {inner.Message}. Write tools are disabled.");
                }
            }
            return new PublishBlockStatus(patched > 0, complete && patched > 0, patched, missing);
        }

        private static void PatchOne(Harmony harmony, MethodBase method)
        {
            MethodInfo mi = method as MethodInfo;
            bool returnsVoid = mi == null || mi.ReturnType == typeof(void);
            if (!returnsVoid)
            {
                SafeResult(mi.ReturnType); // computed now so the prefix itself can never fail on it
            }
            MethodInfo prefix = returnsVoid ? s_prefixVoid : s_prefixResult;
            HarmonyMethod hm = new HarmonyMethod(prefix) { priority = Priority.Last };
            harmony.Patch(method, prefix: hm);

            if (!HasBlockingPrefix(method, harmony.Id))
            {
                throw new InvalidOperationException("Harmony did not register the prefix");
            }
        }

        /// <summary>The method currently carries one of this blocker's prefixes installed under <paramref name="harmonyId"/> (Harmony patch info).</summary>
        public static bool HasBlockingPrefix(MethodBase method, string harmonyId)
        {
            Patches info = Harmony.GetPatchInfo(method);
            return info != null && info.Prefixes.Any(p => p.owner == harmonyId && (p.PatchMethod == s_prefixVoid || p.PatchMethod == s_prefixResult));
        }

        /// <summary>Prefix for void methods (and constructors): skip the original.</summary>
        public static bool PrefixVoid(MethodBase __originalMethod)
        {
            OnBlocked(__originalMethod);
            return false;
        }

        /// <summary>Prefix for methods with a return value: skip the original and return a safe default.</summary>
        public static bool PrefixWithResult(MethodBase __originalMethod, ref object __result)
        {
            try
            {
                Type rt = (__originalMethod as MethodInfo)?.ReturnType;
                __result = rt == null ? null : SafeResult(rt);
            }
            catch
            {
                // SafeResult was precomputed at patch time; nothing to do
            }
            OnBlocked(__originalMethod);
            return false;
        }

        /// <summary>
        /// Value returned instead of the original: a value type's public static readonly <c>Invalid</c> field of its own
        /// type if it has one (Steam handles: UGCUpdateHandle_t.Invalid is ulong.MaxValue, not 0), else default(T)
        /// (false for bool, 0 for numbers, SteamAPICall_t.Invalid = 0); null for reference types.
        /// </summary>
        public static object SafeResult(Type type)
        {
            return s_safeResults.GetOrAdd(type, t =>
            {
                if (!t.IsValueType || t == typeof(void))
                {
                    return null;
                }
                FieldInfo invalid = t.GetField("Invalid", BindingFlags.Public | BindingFlags.Static);
                if (invalid != null && invalid.FieldType == t && invalid.IsInitOnly)
                {
                    return invalid.GetValue(null);
                }
                return Activator.CreateInstance(t);
            });
        }

        public static string Describe(MethodBase m)
        {
            if (m == null)
            {
                return "?";
            }
            string ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name).ToArray());
            return $"{m.DeclaringType?.Name}.{m.Name}({ps})";
        }

        /// <summary>Total blocked calls of a method since startup (any thread), by <see cref="Describe"/> name.</summary>
        public static long BlockedCallCount(string describedName)
        {
            lock (s_rateLock)
            {
                return s_rate.TryGetValue(describedName, out RateState st) ? st.Total : 0;
            }
        }

        private static void OnBlocked(MethodBase method)
        {
            try
            {
                string name = Describe(method);
                bool log;
                int suppressed;
                long now = ClockMs();
                lock (s_rateLock)
                {
                    if (!s_rate.TryGetValue(name, out RateState st))
                    {
                        st = new RateState();
                        s_rate.Add(name, st);
                    }
                    st.Total++;
                    log = !st.EverLogged || now - st.LastLogMs >= (long)LogInterval.TotalMilliseconds;
                    if (log)
                    {
                        suppressed = st.Suppressed;
                        st.Suppressed = 0;
                        st.LastLogMs = now;
                        st.EverLogged = true;
                    }
                    else
                    {
                        suppressed = 0;
                        st.Suppressed++;
                    }
                }
                if (!log)
                {
                    return;
                }
                string extra = suppressed > 0 ? $" ({suppressed} further calls in the last {LogInterval.TotalSeconds:0} s not logged)" : "";
                string message = $"{UserMessage} Blocked call: {name}{extra}. Nothing was sent to Steam. {HowToPublish}";
                s_log.Warning(message);
                s_extraSink?.Invoke("[PummelBridge] " + message);
            }
            catch
            {
                // a blocked call must never throw into the game
            }
        }

        private sealed class RateState
        {
            public bool EverLogged;
            public long LastLogMs;
            public int Suppressed;
            public long Total;
        }
    }
}
