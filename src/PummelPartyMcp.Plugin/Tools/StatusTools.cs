using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;
using PummelPartyMcp.Game;
using UnityEngine.SceneManagement;

namespace PummelPartyMcp.Tools
{
    /// <summary>ping, get_status, get_open_mod.</summary>
    internal static class StatusTools
    {
        private static EditServices s_services;

        public static readonly string[] Capabilities = { "read", "write", "edit", "actions", "playtest", "batch", "playtestState", "selfTest", "gameVersionGate" };

        /// <param name="publishBlock">Live publish-block status (re-verifies the required prefixes on every call).</param>
        public static void Register(ToolRegistry registry, PluginRuntimeInfo info, Func<bool> writesEnabled, Func<PublishBlockStatus> publishBlock,
            Func<GameVersionStatus> gameVersionPeek, Func<GameVersionStatus> gameVersionMain, EditServices services)
        {
            s_services = services;
            // Answered on the HTTP thread from values cached at startup: works with the editor closed and even
            // while the main thread is busy (e.g. during a scene load).
            registry.Register(ToolDefinition.Read("ping", ctx => new JObject
            {
                ["pong"] = true,
                ["pluginVersion"] = PluginInfo.Version,
                ["gameVersion"] = info.GameVersion,
                ["unityVersion"] = info.UnityVersion,
                ["apiVersion"] = PluginInfo.ApiVersion,
                ["sessionId"] = info.SessionId,
                ["capabilities"] = new JArray(Capabilities),
                // HTTP thread: the last loaded config values (the guard and get_status reload the file on the main thread).
                ["gameVersionCheck"] = SelfTestTool.VersionJson(gameVersionPeek()),
            }));

            registry.Register(ToolDefinition.Read("get_status", ctx => ctx.OnMainThread(() =>
            {
                JObject status = GetStatus();
                status["capabilities"] = new JArray(Capabilities);
                status["writesEnabled"] = writesEnabled();
                status["gameVersionCheck"] = SelfTestTool.VersionJson(gameVersionMain());
                PublishBlockStatus block = publishBlock();
                status["publishBlock"] = new JObject
                {
                    ["active"] = block.Active,
                    ["complete"] = block.Complete,
                    ["patchedCount"] = block.PatchedCount,
                    ["missing"] = new JArray(block.Missing.ToArray()),
                };
                return status;
            })));
            registry.Register(ToolDefinition.Read("get_open_mod", ctx => ctx.OnMainThread(GetOpenMod)));
        }

        private static JObject GetStatus()
        {
            JObject result = new JObject
            {
                ["editorOpen"] = false,
                ["editorState"] = null,
                ["isLoading"] = false,
                ["modOpen"] = false,
                ["modName"] = null,
                ["modType"] = null,
                ["modLoaded"] = false,
                ["isDirty"] = null,
                ["hasUnsavedEdits"] = null,
                ["unsavedEditsSource"] = null,
                ["undoStackLength"] = null,
                ["prefabContext"] = null,
                ["playMode"] = false,
                ["activeScene"] = SceneManager.GetActiveScene().name,
                ["playtest"] = null,
            };

            EditorSystem ed = EditorAccess.EditorOrNull();
            if (ed == null)
            {
                return result;
            }
            EditorState state = ed.State;
            result["editorOpen"] = true;
            result["editorState"] = state.ToString();
            result["isLoading"] = ed.IsLoading;
            result["playMode"] = state != EditorState.Editing;
            result["playtest"] = PlaytestTools.StatusJson(ed, s_services);

            PummelMod mod = ed.CurrentMod;
            if (mod != null)
            {
                result["modOpen"] = true;
                result["modName"] = EditorAccess.ModName(mod);
                result["modType"] = mod.ModType.ToString();
                result["modLoaded"] = mod.IsLoaded;
                bool? dirty = EditorAccess.IsDirty(ed);
                result["isDirty"] = dirty.HasValue ? new JValue(dirty.Value) : JValue.CreateNull();
                if (s_services != null)
                {
                    bool? unsaved = s_services.HasUnsavedEdits(ed);
                    result["hasUnsavedEdits"] = unsaved.HasValue ? new JValue(unsaved.Value) : JValue.CreateNull();
                    result["unsavedEditsSource"] = s_services.Source(ed);
                }
                int? undo = EditorAccess.UndoStackLength(ed);
                result["undoStackLength"] = undo.HasValue ? new JValue(undo.Value) : JValue.CreateNull();
            }

            PummelPrefab prefab = ed.CurrentPrefab;
            if (prefab != null)
            {
                result["prefabContext"] = new JObject { ["name"] = prefab.name, ["guid"] = prefab.guid.ToString() };
            }
            return result;
        }

        private static JObject GetOpenMod()
        {
            EditorSystem ed = EditorAccess.RequireEditor();
            PummelMod mod = EditorAccess.RequireLoadedMod(ed);
            PlaytestAccess.RequireEditMode(ed, "get_open_mod"); // settings hold asset references (preview image, music)

            object meta =GameReflection.RequireField(typeof(PummelMod), "m_metaData").GetValue(mod);
            ValueConverter conv = new ValueConverter(mod, summarizeTriggers: false);

            ModSettings settings = mod.GetModSettings();
            JObject result = new JObject
            {
                ["name"] = EditorAccess.ModName(mod),
                ["folderName"] = EditorAccess.ModFolderName(mod),
                ["type"] = mod.ModType.ToString(),
                ["className"] = mod.GetType().Name,
                ["usesHierarchy"] = mod.UsesHierarchy,
                ["usesLighting"] = mod.UsesLighting,
                ["usesPostProcessing"] = mod.UsesPostProcessing,
                ["metadata"] = meta == null ? JValue.CreateNull() : conv.SerializedFields(meta, 0, null),
                ["settingsClass"] = settings?.GetType().Name,
                ["settings"] = settings == null ? JValue.CreateNull() : conv.SerializedFields(settings, 0, null),
                ["lightingPreset"] = meta == null ? null : mod.GetSelectedLightingPreset(),
                ["postProcessingPreset"] = meta == null ? null : mod.GetSelectedPostProcessingPreset(),
            };

            // Only these two fields of the workshop item are exposed (string id: may exceed 2^53).
            ModWorkshopItem item = mod.workshopItem;
            if (item != null)
            {
                ulong id = item.publishedFileId;
                result["publishedFileId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result["isPublished"] = id != ModWorkshopItem.DefaultWorkshopID;
            }
            else
            {
                result["publishedFileId"] = null;
                result["isPublished"] = false;
            }
            return result;
        }
    }
}
