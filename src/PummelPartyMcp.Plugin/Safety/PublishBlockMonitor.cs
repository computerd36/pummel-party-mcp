using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Safety
{
    /// <summary>
    /// Live view of the publish block. <see cref="Current"/> re-reads Harmony's patch info of every required target and
    /// reports the block incomplete as soon as one of them no longer carries this plugin's prefix (e.g. another plugin
    /// called <c>Harmony.UnpatchAll()</c> or unpatched it after startup). Fail closed and sticky: once incomplete, it stays
    /// incomplete until the game restarts; the plugin does not re-patch (see the decision log). Called before every write
    /// tool (guard step 2) and by get_status; costs one patch-info lookup per required target (about 14), no per-frame work.
    /// Unity-free so the PatchTests harness covers it.
    /// </summary>
    public sealed class PublishBlockMonitor
    {
        private readonly PublishBlockStatus m_installed;
        private readonly List<KeyValuePair<string, MethodBase>> m_required;
        private readonly string m_harmonyId;
        private readonly IPluginLog m_log;
        private readonly object m_lock = new object();
        private readonly HashSet<string> m_reported = new HashSet<string>(StringComparer.Ordinal);
        private PublishBlockStatus m_lost;

        /// <param name="installed">Result of <see cref="PublishBlocker.Apply"/>.</param>
        /// <param name="targets">The same target list that was applied; required targets with a method are watched.</param>
        /// <param name="harmonyId">Harmony id the prefixes were installed with.</param>
        public PublishBlockMonitor(PublishBlockStatus installed, IEnumerable<BlockTarget> targets, string harmonyId, IPluginLog log)
        {
            m_installed = installed ?? PublishBlockStatus.NotApplied;
            m_harmonyId = harmonyId ?? throw new ArgumentNullException(nameof(harmonyId));
            m_log = log ?? NullPluginLog.Instance;
            m_required = new List<KeyValuePair<string, MethodBase>>();
            HashSet<MethodBase> seen = new HashSet<MethodBase>();
            foreach (BlockTarget t in targets ?? Enumerable.Empty<BlockTarget>())
            {
                if (t != null && t.Required && t.Method != null && seen.Add(t.Method))
                {
                    m_required.Add(new KeyValuePair<string, MethodBase>(t.Name ?? PublishBlocker.Describe(t.Method), t.Method));
                }
            }
        }

        /// <summary>Number of required targets watched.</summary>
        public int RequiredCount => m_required.Count;

        public bool IsComplete() => Current().Complete;

        /// <summary>The install result while every required prefix is still in place; otherwise an incomplete status naming the lost targets.</summary>
        public PublishBlockStatus Current()
        {
            if (!m_installed.Complete)
            {
                return m_installed; // already failed closed at startup
            }
            List<string> lost = null;
            foreach (KeyValuePair<string, MethodBase> kv in m_required)
            {
                bool present;
                try
                {
                    present = PublishBlocker.HasBlockingPrefix(kv.Value, m_harmonyId);
                }
                catch
                {
                    present = false; // fail closed
                }
                if (!present)
                {
                    (lost ?? (lost = new List<string>())).Add(kv.Key);
                }
            }
            lock (m_lock)
            {
                if (lost != null)
                {
                    List<string> fresh = lost.Where(n => m_reported.Add(n)).ToList();
                    if (fresh.Count > 0)
                    {
                        m_log.Error($"Publish block: the blocking prefix was removed after startup from {string.Join(", ", fresh.ToArray())} " +
                                    "(another plugin unpatched it?). All write tools are disabled until the game is restarted.");
                    }
                    List<string> missing = m_reported.OrderBy(n => n, StringComparer.Ordinal)
                        .Select(n => $"{n}: blocking prefix removed after startup").ToList();
                    m_lost = new PublishBlockStatus(m_installed.Active, false, m_installed.PatchedCount, missing);
                }
                return m_lost ?? m_installed;
            }
        }
    }
}
