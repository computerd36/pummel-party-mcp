using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;
using UnityEngine;

namespace PummelPartyMcp.Tools
{
    /// <summary>
    /// list_mod_settings, get_mod_settings (read); set_mod_settings, capture_preview_image (write, OpenMod). The rules are
    /// Core/SettingsRules.cs, the engine Core/SettingsEngine.cs, the game access Game/SettingsAccess.cs.
    /// </summary>
    internal static class SettingsTools
    {
        public static readonly TimeSpan SetTimeout = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan CaptureWait = TimeSpan.FromSeconds(12);

        private sealed class SetArgs
        {
            public string Section;
            public JObject Values;
        }

        public static void Register(ToolRegistry registry, EditServices services)
        {
            registry.Register(ToolDefinition.Read("list_mod_settings", ctx =>
            {
                string section = ctx.GetString("section");
                return ctx.OnMainThread(() => List(section));
            }));
            registry.Register(ToolDefinition.Read("get_mod_settings", ctx =>
            {
                string section = ctx.GetString("section");
                return ctx.OnMainThread(() => Get(section));
            }));
            registry.Register(ToolDefinition.Write("set_mod_settings", WriteGuard.OpenMod(), ParseSet, inv => Set(inv, services), SetTimeout));
            registry.Register(ToolDefinition.Write("capture_preview_image", WriteGuard.OpenMod(), null, inv => Capture(inv, services), CaptureTimeout));
        }

        // ------------------------------------------------------------------ shared

        private static string AllSectionNames() =>
            string.Join(", ", SettingsRules.All.Select(s => s.Name).Distinct().ToArray());

        private static SetArgs ParseSet(ToolContext ctx)
        {
            string section = ctx.GetString("section", required: true);
            if (!SettingsRules.All.Any(s => string.Equals(s.Name, section, StringComparison.OrdinalIgnoreCase)))
            {
                throw RpcException.InvalidParams($"unknown section '{section}'; sections: {AllSectionNames()} (list_mod_settings shows the ones of the open mod)");
            }
            JToken v = ctx.Params["values"];
            if (!(v is JObject values) || values.Count == 0)
            {
                throw RpcException.InvalidParams("'values' must be an object {field: value, ...} with at least one field");
            }
            if (values.Count > SettingsEngine.MaxFields)
            {
                throw RpcException.InvalidParams($"'values' has more than {SettingsEngine.MaxFields} fields");
            }
            return new SetArgs { Section = section, Values = values };
        }

        private sealed class OpenSettings
        {
            public EditorSystem Ed;
            public PummelMod Mod;
            public string ModType;
            public SettingsHost Host;
            public List<SettingsSection> Sections;
        }

        private static OpenSettings Open(string tool, string sectionFilter)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            PlaytestAccess.RequireEditMode(ed, tool); // during a playtest the running mod is loaded from the saved files, not the editor's objects
            string modType = SettingsHost.ModTypeName(mod);
            List<SettingsSection> all = modType == null ? new List<SettingsSection>() : SettingsRules.ForModType(modType).ToList();
            if (sectionFilter != null)
            {
                SettingsSection hit = all.FirstOrDefault(s => string.Equals(s.Name, sectionFilter, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                {
                    throw RpcException.InvalidParams(modType == null
                        ? $"{mod.ModType} mods have no settings sections in this plugin version"
                        : $"'{sectionFilter}' is not a settings section of a {modType} mod; sections: {string.Join(", ", all.Select(s => s.Name).ToArray())}");
                }
                all = new List<SettingsSection> { hit };
            }
            return new OpenSettings { Ed = ed, Mod = mod, ModType = modType, Host = new SettingsHost(ed, mod), Sections = all };
        }

        private static string UnsupportedNote(PummelMod mod)
        {
            return mod.ModType == PummelModType.BoardSpace
                ? "BoardSpace mods have no settings window (the editor shows one explanatory text); nothing to list"
                : $"{mod.ModType} mods are not covered by the settings tools in this plugin version (use get_open_mod to read what exists)";
        }

        // ------------------------------------------------------------------ list_mod_settings

        private static JObject List(string sectionFilter)
        {
            OpenSettings s = Open("list_mod_settings", sectionFilter);
            JObject result = new JObject
            {
                ["mod"] = EditorAccess.ModName(s.Mod),
                ["modType"] = s.Mod.ModType.ToString(),
            };
            if (s.ModType == null)
            {
                result["sections"] = new JArray();
                result["note"] = UnsupportedNote(s.Mod);
                return result;
            }
            JArray sections = new JArray();
            foreach (SettingsSection sec in s.Sections)
            {
                JObject j = SettingsEngine.SectionListJson(sec, s.Host);
                ModSettingField preset = sec.Fields.FirstOrDefault(f => f.Kind == SettingKind.Preset);
                if (preset != null)
                {
                    IList<string> unusable = s.Host.UnusablePresets(preset);
                    if (unusable.Count > 0)
                    {
                        j["unusablePresets"] = new JArray(unusable.ToArray());
                        j["unusablePresetsNote"] = "listed by the editor but its preset file's internal name differs from the file name, so the editor cannot select it; refused here";
                    }
                    if (sec.Name == "screenEffects")
                    {
                        AddEffectsWarning(j, s.Host);
                    }
                }
                sections.Add(j);
            }
            result["sections"] = sections;
            result["note"] = "Values below are the open mod's current ones, in the shapes set_mod_settings accepts. Settings are saved by save_mod; they are not undoable.";
            return result;
        }

        private static void AddEffectsWarning(JObject sectionJson, SettingsHost host)
        {
            ModSettingField preset = SettingsRules.Find("Minigame", "screenEffects").Find("preset");
            string current = host.Current(preset) as string;
            int count = host.CustomEffectCount();
            if (current != SettingsRules.Custom && count > 0)
            {
                sectionJson["saveWarning"] = $"the mod has {count} custom screen effect(s) in memory, but preset '{current}' is selected: save_mod now would write the preset's effects over PostProcessing.data and the custom effects would be gone after reopening the mod. Select \"Custom\" before save_mod to keep them.";
            }
        }

        // ------------------------------------------------------------------ get_mod_settings

        private static JObject Get(string sectionFilter)
        {
            OpenSettings s = Open("get_mod_settings", sectionFilter);
            JObject result = new JObject
            {
                ["mod"] = EditorAccess.ModName(s.Mod),
                ["modType"] = s.Mod.ModType.ToString(),
            };
            if (s.ModType == null)
            {
                result["sections"] = new JObject();
                result["note"] = UnsupportedNote(s.Mod);
                return result;
            }
            JObject sections = new JObject();
            foreach (SettingsSection sec in s.Sections)
            {
                JObject j = new JObject
                {
                    ["values"] = SettingsEngine.SectionValuesJson(sec, s.Host),
                    ["readOnly"] = new JArray(sec.Fields.Where(f => !f.Writable && !f.OmitFromGet).Select(f => f.Name).ToArray()),
                };
                if (sec.Name == "screenEffects")
                {
                    AddEffectsWarning(j, s.Host);
                }
                sections[sec.Name] = j;
            }
            result["sections"] = sections;
            result["note"] = "values use exactly the shapes set_mod_settings accepts (conditional fields are refused there while their condition is false; list_mod_settings shows the conditions). Settings are saved by save_mod.";
            return result;
        }

        // ------------------------------------------------------------------ set_mod_settings

        private static void Set(WriteInvocation inv, EditServices services)
        {
            SetArgs args = inv.ArgsAs<SetArgs>();
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            EditContext.RequireEditReady(ed);
            string modType = SettingsHost.ModTypeName(mod);
            if (modType == null)
            {
                throw RpcException.InvalidParams(UnsupportedNote(mod));
            }
            SettingsSection section = SettingsRules.Find(modType, args.Section);
            if (section == null)
            {
                throw RpcException.InvalidParams($"'{args.Section}' is not a settings section of a {modType} mod; sections: {string.Join(", ", SettingsRules.ForModType(modType).Select(s => s.Name).ToArray())}");
            }

            SettingsHost host = new SettingsHost(ed, mod);
            SettingsPlan plan = SettingsEngine.Plan(section, args.Values, host); // validates everything; nothing has changed yet

            JObject previous = new JObject();
            foreach (SettingsAssignment a in plan.Assignments)
            {
                object cur = host.Current(a.Field);
                previous[a.Field.Name] = cur == null ? JValue.CreateNull() : SettingsEngine.ToJson(a.Field, cur);
            }
            List<string> warnings = Warnings(section, plan, host);

            inv.Commit(TimeSpan.FromSeconds(0.5)); // from here the caller always gets this call's own result
            services.Observe();
            List<string> applied = new List<string>();
            Exception failure = null;
            try
            {
                host.Apply(plan, applied);
            }
            catch (Exception e)
            {
                failure = e;
            }

            SettingsHost after = new SettingsHost(ed, mod);
            bool changed = false;
            foreach (SettingsAssignment a in plan.Assignments.Where(x => applied.Contains(x.Field.Name)))
            {
                if (!SettingsEngine.ValuesEqual(a.Field, host.Current(a.Field), after.Current(a.Field)))
                {
                    changed = true;
                }
            }
            services.MarkEditIfChanged(mod, "set_mod_settings", changed || applied.Count > 0 && failure != null);
            if (failure != null)
            {
                throw new RpcException(failure is RpcException re ? re.Code : ErrorCodes.Internal,
                    $"{(failure is RpcException ? failure.Message : failure.GetType().Name + ": " + failure.Message)}; fields already applied: {(applied.Count == 0 ? "none" : string.Join(", ", applied.ToArray()))} " +
                    "(settings have no undo; previous values: " + previous.ToString(Newtonsoft.Json.Formatting.None) + ")");
            }

            JObject appliedJson = new JObject();
            foreach (SettingsAssignment a in plan.Assignments)
            {
                object now = after.Current(a.Field);
                JObject entry = new JObject
                {
                    ["type"] = SettingsEngine.TypeText(a.Field),
                    ["value"] = now == null ? JValue.CreateNull() : SettingsEngine.ToJson(a.Field, now),
                };
                if (a.Implied)
                {
                    entry["implied"] = true;
                }
                appliedJson[a.Field.Name] = entry;
            }
            JObject result = new JObject
            {
                ["section"] = section.Name,
                ["applied"] = appliedJson,
                ["previous"] = previous,
                ["clamped"] = new JArray(plan.Clamped.Select(c => (JToken)new JObject
                {
                    ["field"] = c.Field,
                    ["requested"] = c.Requested,
                    ["applied"] = c.Applied,
                    ["min"] = c.Min.HasValue ? new JValue(c.Min.Value) : JValue.CreateNull(),
                    ["max"] = c.Max.HasValue ? new JValue(c.Max.Value) : JValue.CreateNull(),
                    ["reason"] = c.Reason,
                }).ToArray()),
                ["implied"] = new JArray(plan.Implied.Select(a => (JToken)new JObject
                {
                    ["field"] = a.Field.Name,
                    ["value"] = SettingsEngine.ToJson(a.Field, a.Value),
                    ["why"] = a.Why,
                }).ToArray()),
                ["changed"] = changed,
                ["undoable"] = false,
                ["note"] = $"mod settings have no undo in the editor: use 'previous' to revert by hand with set_mod_settings. The change is in memory until save_mod, which writes {section.SavedTo}.",
            };
            if (warnings.Count > 0)
            {
                result["warnings"] = new JArray(warnings.ToArray());
            }
            AddValidation(result, ed, modType, section);
            inv.Complete(result);
        }

        private static List<string> Warnings(SettingsSection section, SettingsPlan plan, SettingsHost host)
        {
            List<string> w = new List<string>();
            ModSettingField preset = section.Fields.FirstOrDefault(f => f.Kind == SettingKind.Preset);
            SettingsAssignment pa = preset == null ? null : plan.Assignments.FirstOrDefault(a => a.Field == preset);
            if (pa == null)
            {
                return w;
            }
            string newPreset = (string)pa.Value;
            string current = host.Current(preset) as string;
            if (section.Name == "screenEffects" && newPreset != SettingsRules.Custom && current == SettingsRules.Custom && host.CustomEffectCount() > 0)
            {
                w.Add($"the mod has {host.CustomEffectCount()} custom screen effect(s): they stay in memory, but saving while preset '{newPreset}' is selected writes the preset's effects over PostProcessing.data, " +
                      "so the custom effects are lost when the mod is reopened. Select \"Custom\" again before save_mod to keep them.");
            }
            if (section.Name == "lighting" && newPreset != SettingsRules.Custom)
            {
                w.Add("the custom lighting values are kept in memory and saved to Lighting.json even while a preset is selected; select \"Custom\" to see and edit them.");
            }
            return w;
        }

        private static void AddValidation(JObject result, EditorSystem ed, string modType, SettingsSection section)
        {
            try
            {
                PlaytestAccess.ValidationRun run = PlaytestAccess.Validate(ed);
                result["validation"] = new JArray(ValidationReport.Ordered(run.Issues)
                    .Where(i => SettingsRules.IsRelevantValidatorMessage(modType, section.Name, i.Title))
                    .Select(i => (JToken)new JObject { ["severity"] = ValidationReport.SeverityName(i.Severity), ["message"] = i.Title }).ToArray());
            }
            catch (Exception e)
            {
                result["validation"] = JValue.CreateNull();
                result["validationError"] = "the validator could not run: " + e.Message;
            }
        }

        // ------------------------------------------------------------------ capture_preview_image

        private static bool Flag(EditorSystem ed, string name) =>
            GameReflection.RequireField(typeof(EditorSystem), name).GetValue(ed) is bool b && b;

        private static void Capture(WriteInvocation inv, EditServices services)
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            EditContext.RequireEditReady(ed);
            string modType = SettingsHost.ModTypeName(mod);
            if (modType == null)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"{mod.ModType} mods have no preview image setting in this plugin version");
            }
            if (ed.CurrentPrefab != null)
            {
                throw new RpcException(ErrorCodes.InvalidState, $"a prefab ('{ed.CurrentPrefab.name}') is being edited; the screenshot would show the prefab view. Return to the scene first");
            }
            if (Flag(ed, "m_willTakePlaymodeScreenshot") || Flag(ed, "m_isTakingScreenShot"))
            {
                throw new RpcException(ErrorCodes.InvalidState, "the editor is already taking a screenshot; wait a moment and retry");
            }
            string rootFull = FullPathWithSeparator(mod.m_modRootDirectory);
            if (rootFull == null)
            {
                throw RpcException.Internal("the mod folder is not known");
            }
            HashSet<string> before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModAsset a in mod.Assets.assets)
            {
                if (a != null)
                {
                    before.Add(a.guid.ToString());
                }
            }

            inv.Commit(TimeSpan.FromSeconds(4));
            Stopwatch sw = Stopwatch.StartNew();
            int frames = 0;
            ed.TakeScreenGrab(); // the toolbar camera button / F10: renders the scene view at the end of the next frame
            services.Poller.Add(() =>
            {
                try
                {
                    frames++;
                    EditorSystem now = EditorAccess.EditorOrNull();
                    if (now == null || !ReferenceEquals(now.CurrentMod, mod))
                    {
                        inv.Fail(new RpcException(ErrorCodes.InvalidState, "the open mod changed while the screenshot was being taken; check list_assets for a Screenshot asset"));
                        return true;
                    }
                    ModAssetExternal created = FindCreated(mod, before);
                    bool busy = Flag(now, "m_willTakePlaymodeScreenshot") || Flag(now, "m_isTakingScreenShot");
                    if (created != null && !busy && frames >= 2)
                    {
                        inv.Complete(Finish(now, mod, services, created, rootFull));
                        return true;
                    }
                    if (sw.Elapsed >= CaptureWait)
                    {
                        inv.Fail(new RpcException(ErrorCodes.Timeout,
                            created == null
                                ? "the editor did not produce a screenshot asset in time; nothing was assigned (check list_assets for a Screenshot<N> texture and get_log)"
                                : "the screenshot is still being written; it was not assigned (check list_assets for the Screenshot<N> texture)"));
                        return true;
                    }
                    return false;
                }
                catch (Exception e)
                {
                    inv.Fail(e);
                    return true;
                }
            });
        }

        private static ModAssetExternal FindCreated(PummelMod mod, HashSet<string> before)
        {
            ModAssetExternal found = null;
            foreach (ModAsset a in mod.Assets.assets)
            {
                if (a is ModAssetExternal ext && !before.Contains(a.guid.ToString()) && a.name != null && a.name.StartsWith("Screenshot", StringComparison.Ordinal)
                    && ValueConverter.LocationName(a) == "external")
                {
                    found = ext;
                }
            }
            return found;
        }

        private static JObject Finish(EditorSystem ed, PummelMod mod, EditServices services, ModAssetExternal created, string rootFull)
        {
            string file = created.filePath;
            string full = null;
            try
            {
                full = string.IsNullOrEmpty(file) ? null : Path.GetFullPath(file);
            }
            catch (Exception)
            {
                full = null;
            }
            if (full == null || !full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw RpcException.Internal("the editor's screenshot asset is not inside the mod folder; it was not assigned");
            }
            Texture2D texture = created.unityObjectAddressable as Texture2D;
            if (texture == null)
            {
                throw RpcException.Internal($"the screenshot asset '{created.name}' could not be loaded as a texture; it was not assigned (the file exists: {created.fileName})");
            }
            SettingsHost host = new SettingsHost(ed, mod);
            string target = host.AssignPreview(texture);
            services.MarkEdit(mod, "capture_preview_image");
            bool exists = File.Exists(full);
            long size = exists ? new FileInfo(full).Length : 0;
            ValueConverter conv = new ValueConverter(mod, summarizeTriggers: false);
            return new JObject
            {
                ["asset"] = new JObject
                {
                    ["guid"] = created.guid.ToString(),
                    ["name"] = created.name,
                    ["assetType"] = ValueConverter.AssetTypeName(created),
                    ["location"] = ValueConverter.LocationName(created),
                    ["relativePath"] = full.Substring(rootFull.Length).Replace('\\', '/'),
                    ["fileExists"] = exists,
                    ["sizeBytes"] = size,
                    ["width"] = texture.width,
                    ["height"] = texture.height,
                },
                ["assignedTo"] = target,
                ["undoable"] = false,
                ["note"] = "the editor wrote the JPEG and its .pmeta into the mod's Assets/Textures folder right now (they stay there even if you do not save); " +
                           "the assignment as the preview image is in memory until save_mod. " +
                           "Data/Preview.jpg (the mod list thumbnail) is not touched; the Workshop item is not touched.",
            };
        }

        private static string FullPathWithSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
