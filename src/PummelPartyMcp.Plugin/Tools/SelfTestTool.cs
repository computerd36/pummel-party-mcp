using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Safety;

namespace PummelPartyMcp.Tools
{
    /// <summary>
    /// self_test (read): after a game update, re-resolves every member the plugin looks up by name (the central registry
    /// Core/RequiredMembers.cs, the same list the offline drift test uses), re-checks the publish block and the inspector audit
    /// snapshot. Changes nothing, runs no game code beyond type/member metadata lookups and the pure Harmony patch-info reads of
    /// the publish block monitor.
    /// </summary>
    internal static class SelfTestTool
    {
        public static void Register(ToolRegistry registry, PluginRuntimeInfo info, Func<GameVersionStatus> gameVersionMain, Func<PublishBlockStatus> publishBlock)
        {
            registry.Register(ToolDefinition.Read("self_test", ctx => ctx.OnMainThread(() => Run(info, gameVersionMain(), publishBlock()))));
        }

        internal static JObject VersionJson(GameVersionStatus v)
        {
            JObject o = new JObject
            {
                ["running"] = v.Running,
                ["tested"] = new JArray(v.Tested),
                ["accepted"] = v.Accepted,
                ["writesAllowed"] = v.WritesAllowed,
            };
            if (!v.Readable)
            {
                o["readable"] = false;
            }
            if (v.ForcedUntested)
            {
                o["forcedUntested"] = true;
            }
            return o;
        }

        private static JObject Run(PluginRuntimeInfo info, GameVersionStatus version, PublishBlockStatus block)
        {
            Assembly game = typeof(EditorSystem).Assembly;
            List<string> missing = RequiredMembers.ResolveAll(game);
            List<string> notes = new List<string>();

            // Publish block: live prefix check (the monitor's status) plus: could every target still be resolved by name?
            JObject pb = new JObject
            {
                ["active"] = block.Active,
                ["complete"] = block.Complete,
                ["patchedCount"] = block.PatchedCount,
                ["missing"] = new JArray(block.Missing.ToArray()),
            };
            List<string> unresolved = new List<string>();
            try
            {
                List<BlockTarget> targets = PublishBlockTargets.Build(game);
                unresolved = targets.Where(t => t.Required && t.Method == null).Select(t => t.Name + (t.Problem != null ? " (" + t.Problem + ")" : "")).ToList();
                pb["targetsChecked"] = targets.Count;
            }
            catch (Exception e)
            {
                unresolved.Add("target list could not be built: " + e.GetType().Name + ": " + e.Message);
            }
            pb["unresolvedTargets"] = new JArray(unresolved.ToArray());

            // Inspector audit: the custom-inspector components and their properties must be the ones that were read.
            List<string> unaudited = new List<string>();
            List<string> inspectors = new List<string>();
            for (int i = 0; i < ModReflectionSystem.TypeCount; i++)
            {
                ModReflectedType rt = ModReflectionSystem.GetType(i);
                if (!rt.hasInspector)
                {
                    continue;
                }
                inspectors.Add(rt.name);
                if (!InspectorRules.AuditedProperties.TryGetValue(rt.name, out string audited))
                {
                    unaudited.Add(rt.name + " (new custom inspector, not audited)");
                    continue;
                }
                HashSet<string> known = new HashSet<string>(audited.Split(' '), StringComparer.Ordinal);
                foreach (ReflectedProperty p in rt.properties ?? new ReflectedProperty[0])
                {
                    if (p.name != "guid" && !known.Contains(p.name))
                    {
                        unaudited.Add(rt.name + "." + p.name);
                    }
                }
            }
            foreach (string expected in InspectorRules.CustomInspectorComponents)
            {
                if (!inspectors.Contains(expected))
                {
                    unaudited.Add(expected + " (audited custom inspector no longer present)");
                }
            }
            JObject audit = new JObject
            {
                ["customInspectorComponents"] = inspectors.Count,
                ["unauditedProperties"] = new JArray(unaudited.ToArray()),
            };

            bool ok = missing.Count == 0 && block.Complete && unresolved.Count == 0 && unaudited.Count == 0 && version.Readable;
            if (!version.Readable)
            {
                notes.Add("the game version could not be read; write tools stay refused.");
            }
            else if (!version.IsTested && !version.Accepted)
            {
                notes.Add($"game version {version.Running} is not in the tested list ({string.Join(", ", version.Tested)}): write tools are refused until " +
                          $"[Safety] AcceptedGameVersion = {version.Running} is set in {GameVersionGate.CfgName}." +
                          (ok ? " This self-test is clean, so accepting is reasonable; it does not exercise the editor itself, so verify with a quick edit on a MCP_ mod afterwards." : ""));
            }
            if (!ok)
            {
                notes.Add("do not accept the version while missingMembers, publishBlock or inspectorAudit report problems: the affected tools may misbehave. Report them with the game version.");
            }
            notes.Add("self_test resolves members by name only (existence, type, signature); it cannot tell whether a member still behaves the same.");

            return new JObject
            {
                ["ok"] = ok,
                ["gameVersion"] = info.GameVersion,
                ["gameVersionCheck"] = VersionJson(version),
                ["membersChecked"] = RequiredMembers.All.Count,
                ["missingMembers"] = new JArray(missing.ToArray()),
                ["publishBlock"] = pb,
                ["inspectorAudit"] = audit,
                ["notes"] = new JArray(notes.ToArray()),
            };
        }
    }
}
