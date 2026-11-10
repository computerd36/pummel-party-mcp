using Newtonsoft.Json.Linq;
using PummelBridge.Core;
using PummelBridge.Game;
using UnityEngine.SceneManagement;

namespace PummelBridge.Tools
{
    /// <summary>ping, get_status, get_open_mod.</summary>
    internal static class StatusTools
    {
        public static void Register(ToolRegistry registry, BridgeRuntimeInfo info)
        {
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
                ["readOnlyBuild"] = true,
            }));

            registry.Register(ToolDefinition.Read("get_status", ctx => ctx.OnMainThread(GetStatus)));
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
                ["undoStackLength"] = null,
                ["prefabContext"] = null,
                ["playMode"] = false,
                ["activeScene"] = SceneManager.GetActiveScene().name,
                ["readOnlyBuild"] = true,
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

            PummelMod mod = ed.CurrentMod;
            if (mod != null)
            {
                result["modOpen"] = true;
                result["modName"] = EditorAccess.ModName(mod);
                result["modType"] = mod.ModType.ToString();
                result["modLoaded"] = mod.IsLoaded;
                bool? dirty = EditorAccess.IsDirty(ed);
                result["isDirty"] = dirty.HasValue ? new JValue(dirty.Value) : JValue.CreateNull();
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

            object meta = GameReflection.RequireField(typeof(PummelMod), "m_metaData").GetValue(mod);
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
