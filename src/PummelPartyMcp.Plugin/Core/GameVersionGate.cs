using System;
using System.Collections.Generic;
using System.Linq;

namespace PummelPartyMcp.Core
{
    /// <summary>Result of comparing the running game version with the tested list and the author's acceptance.</summary>
    public sealed class GameVersionStatus
    {
        /// <summary>Application.version as read at startup; null/blank when it could not be read.</summary>
        public string Running;

        public string[] Tested;

        /// <summary>The version string is usable (not null, not blank).</summary>
        public bool Readable;

        /// <summary>The running version is in the tested list (and the debug override does not force it untested).</summary>
        public bool IsTested;

        /// <summary>[Safety] AcceptedGameVersion (trimmed) equals the running version exactly.</summary>
        public bool Accepted;

        /// <summary>[Safety] ForceGameVersionUntested is on: the tested list is ignored (only ever stricter).</summary>
        public bool ForcedUntested;

        public bool WritesAllowed => IsTested || Accepted;
    }

    /// <summary>
    /// Phase 3 game-version gate. The plugin reaches deep into editor internals, so a version it has not been tested with
    /// leaves read tools working but refuses every write tool (GAME_VERSION_UNTESTED) until the author accepts that exact
    /// version in the config. Pure; the guard calls <see cref="WriteGuardEvaluator"/> with a provider of the live status.
    /// </summary>
    public static class GameVersionGate
    {
        /// <summary>Game versions the plugin has been verified against in-game (docs/plugin-api.md).</summary>
        public static readonly string[] TestedVersions = { "1.14.1k" };

        /// <summary>
        /// Comparison is ordinal and exact: case-sensitive, no trimming of the running version. The accepted config value
        /// has its surrounding whitespace removed (a hand-edited .cfg line), nothing else.
        /// </summary>
        public static GameVersionStatus Evaluate(string running, IEnumerable<string> tested, string acceptedConfig, bool forceUntested = false)
        {
            string[] list = (tested ?? new string[0]).ToArray();
            bool readable = !string.IsNullOrWhiteSpace(running);
            string accepted = acceptedConfig?.Trim();
            return new GameVersionStatus
            {
                Running = running,
                Tested = list,
                Readable = readable,
                ForcedUntested = forceUntested,
                IsTested = readable && !forceUntested && list.Contains(running, StringComparer.Ordinal),
                Accepted = readable && !string.IsNullOrEmpty(accepted) && string.Equals(running, accepted, StringComparison.Ordinal),
            };
        }

        /// <summary>A status that always allows writes (unit tests of other guard steps).</summary>
        public static GameVersionStatus AlwaysTested() => Evaluate(TestedVersions[0], TestedVersions, null);

        public const string CfgName = @"BepInEx\config\com.pummelpartymcp.plugin.cfg";

        /// <summary>The refusal for an untested version; null when writes are allowed.</summary>
        public static RpcException Refusal(GameVersionStatus s)
        {
            if (s == null)
            {
                return new RpcException(ErrorCodes.GameVersionUntested, "the game version could not be determined (no status); write tools are refused (fail closed)");
            }
            if (s.WritesAllowed)
            {
                return null;
            }
            string tested = s.Tested == null || s.Tested.Length == 0 ? "none" : string.Join(", ", s.Tested);
            if (!s.Readable)
            {
                return new RpcException(ErrorCodes.GameVersionUntested,
                    $"the running game version could not be read, so it cannot be compared with the tested versions ({tested}); write tools are refused. " +
                    "Read tools still work. See the BepInEx log; this cannot be accepted in the config until the version is readable.");
            }
            string why = s.ForcedUntested
                ? $"the running game version '{s.Running}' is treated as untested because [Safety] ForceGameVersionUntested = true"
                : $"the running game version '{s.Running}' has not been tested with this plugin (tested versions: {tested})";
            return new RpcException(ErrorCodes.GameVersionUntested,
                why + "; write tools are refused, read tools still work. Run the self_test tool; if it reports ok, accept this exact version by setting " +
                $"[Safety] AcceptedGameVersion = {s.Running} in {CfgName} (the plugin re-reads the file on the next call; no restart needed). " +
                "Acceptance applies only to this exact version string.");
        }
    }
}
