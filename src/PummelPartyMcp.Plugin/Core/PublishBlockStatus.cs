using System.Collections.Generic;

namespace PummelPartyMcp.Core
{
    /// <summary>Outcome of installing the Workshop publish blocklist (immutable; Safety/PublishBlockMonitor produces a new, incomplete one if a prefix is later removed).</summary>
    public sealed class PublishBlockStatus
    {
        public static readonly PublishBlockStatus NotApplied = new PublishBlockStatus(false, false, 0, new string[0]);

        /// <summary>At least one blocking patch is installed.</summary>
        public bool Active { get; }

        /// <summary>Every required target was found and patched and no patch threw. Write tools need this.</summary>
        public bool Complete { get; }

        public int PatchedCount { get; }

        /// <summary>Required targets that are missing or whose patch failed.</summary>
        public IReadOnlyList<string> Missing { get; }

        public PublishBlockStatus(bool active, bool complete, int patchedCount, IReadOnlyList<string> missing)
        {
            Active = active;
            Complete = complete;
            PatchedCount = patchedCount;
            Missing = missing ?? new string[0];
        }
    }
}
