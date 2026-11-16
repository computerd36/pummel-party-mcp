using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PummelPartyMcp.Core
{
    /// <summary>One game member the plugin looks up by name (reflection, Harmony target or rule table) and cannot check at compile time.</summary>
    public sealed class RequiredMember
    {
        /// <summary>"field", "property", "method" or "enumValue".</summary>
        public string Kind;

        /// <summary>Full name of the declaring type in Assembly-CSharp (e.g. "ModSystem.Logic.ModLogicTrigger").</summary>
        public string Type;

        public string Name;

        /// <summary>Field/property: expected System.Type.ToString() of its type (null = not checked). enumValue: the property whose enum must define Name.</summary>
        public string Extra;

        public bool IsStatic;

        /// <summary>Assembly of <see cref="Type"/> when it is not Assembly-CSharp (e.g. Unity.Postprocessing.Runtime).</summary>
        public string Assembly;

        /// <summary>Method: parameter type names (System.Type.ToString()). enumType: the expected "Name=Value" pairs. settingsRange: min, max.</summary>
        public string[] Params;

        /// <summary>Where the plugin uses it.</summary>
        public string UsedBy;

        public string Id => Kind == "enumValue" ? $"{Type}.{Extra} = {Name}" : Name == null ? Type : $"{Type}.{Name}";
    }

    /// <summary>
    /// The central registry of "members the plugin needs": the one list behind the runtime self_test tool and the offline
    /// drift test (PatchTests/GameDriftTest), so the two cannot diverge. Pure reflection by name: works on a loaded or a
    /// reflection-only Assembly-CSharp. A test also scans the plugin sources for by-name lookups missing here.
    /// </summary>
    public static class RequiredMembers
    {
        private static readonly List<RequiredMember> s_all = Build();

        public static IReadOnlyList<RequiredMember> All => s_all;

        private static List<RequiredMember> Build()
        {
            List<RequiredMember> l = new List<RequiredMember>();
            void F(string type, string name, string fieldType, string use, bool isStatic = false) =>
                l.Add(new RequiredMember { Kind = "field", Type = type, Name = name, Extra = fieldType, IsStatic = isStatic, UsedBy = use });
            void P(string type, string name, string use, bool isStatic = false) =>
                l.Add(new RequiredMember { Kind = "property", Type = type, Name = name, IsStatic = isStatic, UsedBy = use });
            void M(string type, string name, string use, params string[] parameters) =>
                l.Add(new RequiredMember { Kind = "method", Type = type, Name = name, Params = parameters, UsedBy = use });

            // editor state, undo, playtest control
            F("EditorSystem", "m_currentPrefab", null, "EditorAccess");
            F("EditorSystem", "m_heirarchyUndoManager", "EditorUndo", "EditorAccess, UndoAccess");
            F("EditorSystem", "m_undoManagerList", null, "EditorAccess, UndoAccess");
            F("EditorSystem", "m_toolbarWidget", "ToolbarWidget", "PlaytestAccess");
            F("EditorUndo", "m_undoEventStack", null, "UndoAccess");
            F("ToolbarWidget", "m_playerCountChoice", "System.Int32", "PlaytestAccess");
            F("ToolbarWidget", "m_aiOnly", "System.Boolean", "PlaytestAccess");
            F("ToolbarWidget", "m_backgroundModLoadTimer", "System.Single", "PlaytestAccess");
            F("ToolbarWidget", "m_playerCount", "System.String[]", "PlaytestAccess");
            F("ModValidationSystem", "m_validators", "System.Collections.Generic.List`1[ModValidationCallbackReceiver]", "PlaytestAccess (validate_mod)", true);
            M("EditorSystem", "SaveCurrentMod", "Plugin save watch", "System.Boolean");
            P("WorkshopController", "CurrentBackgroundLoadMod", "PlaytestAccess", true);
            P("WorkshopController", "CurrentActiveMod", "PlaytestAccess", true);

            // playtest state and event feed
            F("MinigameController", "round", "System.Int32", "PlaytestStateReader");
            F("MinigameController", "round_begin_time", "System.Single", "PlaytestStateReader");
            F("SimpleModPlayer", "m_lastDeathTime", "System.Single", "PlaytestStateReader");
            F("SimpleModPlayer", "m_filoHeldItemStack", "System.Collections.Generic.List`1[ModItem]", "PlaytestStateReader");
            F("ModSystem.Logic.ModLogicTrigger", "m_owner", "ModSystem.Logic.IEventReceiver", "PlaytestLogicRecorder");
            M("ModSystem.Logic.ModLogicTrigger", "RunActionsLocal", "PlaytestHooks", "ModSystem.Logic.ModEvent");

            // editor camera (screenshot camera) and audio state (get_playtest_state)
            F("EditorCamera", "m_targetPosition", "UnityEngine.Vector3", "EditorCameraAccess");
            F("EditorCamera", "m_targetVelocity", "UnityEngine.Vector3", "EditorCameraAccess");
            F("EditorCamera", "m_zRotation", "System.Single", "EditorCameraAccess (pitch)");
            F("EditorCamera", "m_yRotation", "System.Single", "EditorCameraAccess (yaw)");
            F("AudioSystem", "audioSystemObj", "AudioSystemObj", "PlaytestStateReader (audio)", true);
            F("AudioSystem", "masterVolume", "System.Single", "PlaytestStateReader (audio)", true);
            F("AudioSystem", "musicVolume", "System.Single", "PlaytestStateReader (audio)", true);
            F("AudioSystem", "effectsVolume", "System.Single", "PlaytestStateReader (audio)", true);
            F("AudioSystemObj", "currentMusic", "TempAudioSource", "PlaytestStateReader (audio)");
            F("AudioSystemObj", "audioSources", "System.Collections.Generic.List`1[TempAudioSource]", "PlaytestStateReader (audio)");

            // mod metadata, assets, components
            F("PummelMod", "m_metaData", null, "EditorAccess, get_open_mod, write guard");
            F("ModItem", "m_itemPummelPrefab", null, "ComponentReader");
            F("ModItem", "m_itemPummelPrefabGuid", null, "ComponentReader");
            F("ModItem", "m_droppedItemPummelPrefab", null, "ComponentReader");
            F("ModItem", "m_droppedItemPummelPrefabGuid", null, "ComponentReader");
            F("ModPrefabInstance", "m_prefabReference", null, "ComponentReader");
            F("ModPrefabInstance", "m_prefabReferenceGuid", null, "ComponentReader");
            F("ModSpawner", "m_prefabGUIDs", null, "ComponentReader");
            F("ModMeshRenderer", "m_tempMats", null, "ComponentReader");
            F("ModLineRenderer", "tempMats", null, "ComponentReader");
            F("ModSkinnedMeshRenderer", "tempMats", null, "ComponentReader");
            F("ModParticleSystem", "m_particleSystemRenderer", null, "ComponentReader, set_property");
            M("ModMeshCollider", "HasLinkedRenderer", "inspector rules");
            F("Console", "entriesClean", null, "get_log (game console)", true);
            F("Console", "entryTypes", null, "get_log (game console)", true);
            F("Console", "stackTraces", null, "get_log (game console)", true);

            // actions
            F("ModSystem.Logic.ModAction", "m_version", "System.Int16", "ValueConverter");
            F("ModActionMeta", "m_uiName", null, "ActionModel");
            F("ModSystem.Logic.BoardPopupAction", "m_choiceCount", "System.Int32", "ActionModel");
            F("ModSystem.Logic.BoardPopupAction", "m_choices", null, "ActionModel");
            M("ModSystem.Logic.BoardPopupAction", "HasACostlessChoice", "ActionParams");
            M("ModSystem.Logic.TargetAction", "GetHideFlags", "ActionModel");
            M("ModSystem.Logic.TargetAction", "GetAcceptedTargetType", "ActionModel");
            F("ModSystem.Logic.SetPlayerVisualAction", "m_verticalLookTargetChildIndex", "System.Int32", "ActionParams");
            F("ModSystem.Logic.SetPlayerVisualAction", "m_movementRotationTargetChildIndex", "System.Int32", "ActionParams");
            // ModAssetReference<T>: read by the converter (get_object, get_action_graph, reference scans of remove_asset), written by set_action
            F("ModSystem.Logic.ModAssetReference`1", "m_asset", null, "ValueConverter (references; remove_asset reference scan)");
            F("ModSystem.Logic.ModAssetReference`1", "m_assetGUID", "SerializableGuid", "ValueConverter (references; remove_asset reference scan)");
            F("ModSystem.Logic.ModAssetReference`1", "m_curAssetGUID", "SerializableGuid", "ValueConverter (references; remove_asset reference scan)");
            P("ModSystem.Logic.ModAssetReference`1", "Asset", "ActionParams (set_action / set_trigger reference parameters)");
            foreach (string trigger in new[] { "weaponHitTrigger", "tickTrigger", "onDeathTrigger", "onRespawnTrigger", "onFirstSpawnTrigger", "onRoundEndTrigger" })
            {
                F("SimpleModPlayerSettings", trigger, null, "ActionModel (player-settings triggers)");
            }

            // inspector rules: every property a rule names, and every enum value its conditions and choices name
            foreach (PropertyRule r in InspectorRules.All)
            {
                l.Add(new RequiredMember { Kind = "ruleProperty", Type = r.Component, Name = r.Property, UsedBy = "inspector rules" });
                List<RuleRef> refs = new List<RuleRef>();
                r.Shown?.CollectRefs(refs);
                foreach (RuleRef rf in refs)
                {
                    l.Add(new RequiredMember { Kind = "ruleProperty", Type = r.Component, Name = rf.Property, UsedBy = "inspector rules (condition of " + r.Property + ")" });
                    foreach (string n in rf.Names ?? new string[0])
                    {
                        l.Add(new RequiredMember { Kind = "enumValue", Type = r.Component, Extra = rf.Property, Name = n, UsedBy = "inspector rules (condition of " + r.Property + ")" });
                    }
                }
                foreach (string n in (r.ExcludedValues ?? new string[0]).Concat(r.AllowedValues ?? new string[0]).Concat(r.AllowedFlags ?? new string[0]))
                {
                    l.Add(new RequiredMember { Kind = "enumValue", Type = r.Component, Extra = r.Property, Name = n, UsedBy = "inspector rules (choices of " + r.Property + ")" });
                }
            }
            foreach (KeyValuePair<string, string> kv in InspectorRules.AuditedProperties)
            {
                foreach (string name in kv.Value.Split(' '))
                {
                    l.Add(new RequiredMember { Kind = "ruleProperty", Type = kv.Key, Name = name, UsedBy = "inspector audit snapshot" });
                }
            }

            AddSettings(l);
            AddAssets(l);

            // distinct, order preserved
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            return l.Where(m => seen.Add(m.Kind + "|" + m.Id + "|" + m.IsStatic)).ToList();
        }

        /// <summary>The mod settings tools (plugin 0.8.0): editor entry points plus every member, enum and limit of Core/SettingsRules.cs.</summary>
        private static void AddSettings(List<RequiredMember> l)
        {
            const string pp = "UnityEngine.Rendering.PostProcessing.";
            void F(string type, string name, string fieldType, string use, string assembly = null) =>
                l.Add(new RequiredMember { Kind = "field", Type = type, Name = name, Extra = fieldType, Assembly = assembly, UsedBy = use });
            void M(string type, string name, string use, string assembly, params string[] parameters) =>
                l.Add(new RequiredMember { Kind = "method", Type = type, Name = name, Params = parameters, Assembly = assembly, UsedBy = use });
            void P(string type, string name, string use) =>
                l.Add(new RequiredMember { Kind = "property", Type = type, Name = name, UsedBy = use });
            const string use1 = "settings tools";

            // editor entry points
            M("MinigameModSettings", "GenerateMinigameControls", use1, null);
            M("ModSettings", "SetupLightingWidget", use1 + " (preset list)", null);
            M("ModSettings", "SetupPostProcessingWidget", use1 + " (preset list)", null);
            F("ModSettings", "m_lightingWidgetLogic", "LightingWidgetLogic", use1 + " (preset list)");
            F("ModSettings", "m_postProcessWidgetLogic", "PostProcessWidgetLogic", use1 + " (preset list)");
            F("LightingWidgetLogic", "m_presetNames", "System.String[]", use1 + " (preset list)");
            F("LightingWidgetLogic", "m_lightingPresets", "System.Collections.Generic.List`1[ModLightingSettings]", use1 + " (preset objects)");
            F("PostProcessWidgetLogic", "m_presetNames", "System.String[]", use1 + " (preset list)");
            F("PostProcessWidgetLogic", "m_postProcessPresets", "System.Collections.Generic.List`1[ModPostProcessSettings]", use1 + " (preset objects)");
            M("ModLightingSettings", "ApplyLightingChangesToRenderSettings", use1 + " (scene refresh)", null, "System.Boolean");
            M("PummelMod", "GetSelectedLightingPreset", use1, null);
            M("PummelMod", "SetSelectedLightingPreset", use1, null, "System.String");
            M("PummelMod", "GetSelectedPostProcessingPreset", use1, null);
            M("PummelMod", "SetSelectedPostProcessingPreset", use1, null, "System.String");
            F("PummelMod", "lighting", "ModLightingSettings", use1);
            F("PummelMod", "CustomLighting", "ModLightingSettings", use1);
            F("PummelMod", "postProcess", "ModPostProcessSettings", use1);
            F("PummelMod", "CustomPostProcess", "ModPostProcessSettings", use1);
            M("ModPostProcessSettings", "GetPostProcessProfile", use1, null);
            F("EditorSystem", "m_postProcessVolume", "UnityEngine.Rendering.PostProcessing.PostProcessVolume", use1);
            F("EditorSystem", "m_sceneViewCamera", "UnityEngine.Camera", use1);
            F("EditorSystem", "m_willTakePlaymodeScreenshot", "System.Boolean", "capture_preview_image");
            F("EditorSystem", "m_isTakingScreenShot", "System.Boolean", "capture_preview_image");
            M("EditorSystem", "TakeScreenGrab", "capture_preview_image", null);
            M("EditorSystem", "OpenMod", "capture_preview_image (mod-switch guard prefix)", null, "System.String");
            M("EditorSystem", "CreateNewMod", "capture_preview_image (mod-switch guard prefix)", null, "System.String", "PummelModType", "System.String");
            M("NewModPopup", "CreateNewMod", "capture_preview_image (mod-switch guard prefix: the New Mod dialog's action)", null, "System.Collections.Generic.List`1[NewModPopup+NewModOption]");
            M("MainMenuWidget", "DeleteModResult", "capture_preview_image (mod-switch guard prefix: Delete Mod)", null, "System.Int32");
            M("LoadModPopup", "DeleteModResult", "capture_preview_image (mod-switch guard prefix: Delete Mod in the Open dialog)", null, "System.Int32");
            M("EditorSystem", "SetCurrentMod", "capture_preview_image (mod-switch guard prefix: Close Mod = SetCurrentMod(null))", null, "PummelMod");
            M("EditorSystem", "OnDisable", "capture_preview_image (mod-switch guard: editor shutdown ends the hold)", null);
            P("MinigameMod", "MinigameSettings", use1);
            M("MinigameMod", "GetMinigameDefinition", use1, null);
            M("BoardMod", "GetMapDetails", use1, null);
            F("MinigameModSettings", "SimpleMinigameSettings", "SimpleModControllerSettings", use1);
            F("MinigameModSettings", "SimpleModPlayerSettings", "SimpleModPlayerSettings", use1);
            F("BoardModSettings", "CameraSettings", "BoardModCameraSettings", use1);
            F("BoardModSettings", "CustomSpaceSettings", "BoardModCustomSpaceSettings", use1);
            F("MinigameDefinition", "screenshotTextureGuid", "SerializableGuid", use1 + " (preview image)");
            F("MapDetails", "previewImageGuid", "SerializableGuid", use1 + " (preview image)");
            F(pp + "PostProcessProfile", "settings", "System.Collections.Generic.List`1[UnityEngine.Rendering.PostProcessing.PostProcessEffectSettings]", use1, "Unity.Postprocessing.Runtime");
            F(pp + "PostProcessEffectSettings", "enabled", pp + "BoolParameter", use1, "Unity.Postprocessing.Runtime");
            M(pp + "PostProcessProfile", "AddSettings", use1, "Unity.Postprocessing.Runtime", "System.Type");
            M(pp + "PostProcessProfile", "RemoveSettings", use1, "Unity.Postprocessing.Runtime", "System.Type");
            F(pp + "ParameterOverride`1", "value", null, use1 + " (effect parameters)", "Unity.Postprocessing.Runtime");
            F(pp + "ParameterOverride`1", "overrideState", "System.Boolean", use1 + " (effect parameters)", "Unity.Postprocessing.Runtime");
            l.Add(new RequiredMember { Kind = "type", Type = pp + "PostProcessLayer", Assembly = "Unity.Postprocessing.Runtime", UsedBy = use1 + " (effect layer refresh)" });
            foreach (string[] forced in SettingsRules.ForcedEffectValues)
            {
                l.Add(new RequiredMember
                {
                    Kind = "settingsMember", Type = pp + forced[0], Name = forced[1], Assembly = "Unity.Postprocessing.Runtime", UsedBy = use1 + " (forced effect value)",
                });
            }
            l.Add(new RequiredMember
            {
                Kind = "enumType", Type = pp + "GradingMode", Assembly = "Unity.Postprocessing.Runtime", Params = new[] { "LowDefinitionRange=0", "HighDefinitionRange=1", "External=2" },
                UsedBy = use1 + " (forced effect value)",
            });
            l.Add(new RequiredMember
            {
                Kind = "enumType", Type = pp + "VignetteMode", Assembly = "Unity.Postprocessing.Runtime", Params = new[] { "Classic=0", "Masked=1" },
                UsedBy = use1 + " (forced effect value)",
            });

            // the rule table
            foreach (SettingsSection s in SettingsRules.All)
            {
                foreach (ModSettingField f in s.Fields)
                {
                    string use = $"settings rules ({s.ModType}/{s.Name}.{f.Name})";
                    if (f.Member != null)
                    {
                        l.Add(new RequiredMember { Kind = "settingsMember", Type = f.GameType, Name = f.Member, Extra = SettingsRules.ExpectedMemberType(f), Assembly = f.Assembly, UsedBy = use });
                    }
                    else if (f.IsEffect)
                    {
                        l.Add(new RequiredMember { Kind = "type", Type = f.GameType, Assembly = f.Assembly, UsedBy = use });
                    }
                    if (f.EnumType != null && f.EnumNames != null && f.EnumValues != null && f.Kind != SettingKind.NamedInt)
                    {
                        l.Add(new RequiredMember
                        {
                            Kind = "enumType", Type = f.EnumType, Assembly = f.EnumAssembly,
                            Params = f.EnumNames.Select((n, i) => n + "=" + f.EnumValues[i]).ToArray(), UsedBy = use,
                        });
                    }
                    if (f.RangeFromAttribute)
                    {
                        l.Add(new RequiredMember
                        {
                            Kind = "settingsRange", Type = f.GameType, Name = f.Member, Assembly = f.Assembly,
                            Params = new[] { SettingsRules.FormatNumber(f.Min.Value), SettingsRules.FormatNumber(f.Max.Value) }, UsedBy = use,
                        });
                    }
                }
                foreach (string hidden in s.HiddenNames)
                {
                    l.Add(new RequiredMember { Kind = "settingsMember", Type = s.HiddenType, Name = hidden, UsedBy = $"settings rules ({s.ModType}/{s.Name} not shown: {hidden})" });
                }
            }
        }

        /// <summary>The asset tools (plugin 0.8.0): every editor/game member import_asset, set_import_settings, materials and remove_asset call.</summary>
        private static void AddAssets(List<RequiredMember> l)
        {
            const string use = "asset tools";
            void F(string type, string name, string fieldType, string u = use, bool isStatic = false) =>
                l.Add(new RequiredMember { Kind = "field", Type = type, Name = name, Extra = fieldType, IsStatic = isStatic, UsedBy = u });
            void P(string type, string name, string u = use) =>
                l.Add(new RequiredMember { Kind = "property", Type = type, Name = name, UsedBy = u });
            void M(string type, string name, string u, params string[] parameters) =>
                l.Add(new RequiredMember { Kind = "method", Type = type, Name = name, Params = parameters, UsedBy = u });
            void E(string type, string assembly, params string[] pairs) =>
                l.Add(new RequiredMember { Kind = "enumType", Type = type, Assembly = assembly, Params = pairs, UsedBy = use + " (import settings / materials)" });

            // import (the plugin copies the file itself and registers it with ImportFile; AddAsset(copy:true) is never called)
            M("PummelMod", "ImportFile", "import_asset (registers the copied file)", "System.IO.FileInfo", "System.Boolean");
            M("PummelMod", "RemoveAsset", "remove_asset, import cleanup", "ModAsset");
            P("PummelMod", "FullAssetsFolder", "asset tools (folder re-check)");
            P("PummelMod", "FullMaterialsFolder", "create_material (folder re-check)");
            P("PummelMod", "FullTexturesFolder", "capture_preview_image (folder re-check and link pre-check of Screenshot<N>.jpg)");
            F("PummelMod", "workshopItem", "ModWorkshopItem", "remove_asset / get_asset references (Workshop thumbnail, read only)");
            F("ModWorkshopItem", "previewImageGuid", "SerializableGuid", "reference scan (Workshop thumbnail, read only)");
            F("ModWorkshopItem", "previewFiles", "System.Collections.Generic.List`1[WorkshopPreviewFile]", "reference scan (Workshop preview files, read only)");
            F("WorkshopPreviewFile", "guid", "SerializableGuid", "reference scan (Workshop preview files, read only)");
            M("ItemMod", "GetItemDetails", "reference scan (the item's prefabs and icon)");
            F("ItemDetails", "prefabGuid", "SerializableGuid", "reference scan (item prefab)");
            F("ItemDetails", "recievePrefabGuid", "SerializableGuid", "reference scan (item receive prefab)");
            F("ItemDetails", "heldPrefabGuid", "SerializableGuid", "reference scan (item held prefab)");
            F("ItemDetails", "iconTexture", "UnityEngine.Texture2D", "reference scan (item icon)");
            F("SimpleModPlayerSettings", "hitTrigger", "ModSystem.Logic.HitTrigger", "reference scan (the hidden player hit trigger's actions)");
            M("PummelMod", "GetModPath", "asset tools (the mod's absolute folder)");
            M("PummelMod", "GetPreviewImageGuid", "remove_asset (preview image is never removed)");
            F("ModAssetExternal", "filePath", "System.String");
            M("ModAssetExternal", "Save", use + " (import settings, materials)");
            M("ModAssetExternal", "Rename", "create_material (name)", "System.String");
            P("PummelProp", "MeshAsset");
            P("PummelProp", "PropInstance", "set_import_settings (detects a failed OBJ re-import)");
            F("Prop", "mesh", "UnityEngine.Mesh", "get_asset (the prop's current mesh; the nested MeshAsset is stale after a re-import)");
            F("Prop", "mats", "UnityEngine.Material[]", "set_import_settings (a good OBJ re-import always sets a new array)");
            P("PummelMesh", "RecalculateNormals", "set_import_settings");
            M("PummelTexture", "GetCompression", "get_asset, set_import_settings");
            M("PummelTexture", "GetFilterMode", "get_asset, set_import_settings");
            M("PummelTexture", "GetWrapMode", "get_asset, set_import_settings");
            F("PummelTexture", "m_compression", "PummelTextureCompression", "set_import_settings (inspector field)");
            F("PummelTexture", "m_filter", "UnityEngine.FilterMode", "set_import_settings (inspector field)");
            F("PummelTexture", "m_wrapMode", "UnityEngine.TextureWrapMode", "set_import_settings (inspector field)");
            E("PummelTextureCompression", null, "Normal=0", "HighQuality=1", "None=2");
            E("UnityEngine.FilterMode", "UnityEngine.CoreModule", "Point=0", "Bilinear=1", "Trilinear=2");
            E("UnityEngine.TextureWrapMode", "UnityEngine.CoreModule", "Repeat=0", "Clamp=1", "Mirror=2", "MirrorOnce=3");
            M("EditorSystem", "ReimportAsset", "set_import_settings (the inspector's Apply)", "ModAssetExternal");
            M("EditorSystem", "CreateNewMaterial", "create_material (writes Assets/Materials/<name>.pmat and .pmeta)");
            M("EditorSystem", "GetUniqueAssetName", "create_material (the name the editor will choose)", "System.String");
            F("EditorSystem", "defaultShaderGuid", "SerializableGuid", "create_material (the editor's default shader)");
            M("EditorSystem", "GetSelectionManager", use);
            M("EditorSystem", "GetAssetWithGuid", use, "SerializableGuid");
            M("EditorSelectionManager", "IsSelected", use, "ModAssetExternal");
            M("EditorSelectionManager", "IsSelectedAssetDirty", use);
            M("EditorSelectionManager", "SerializeSelectedObject", use + " (clean baseline after a change)");
            M("EditorSelectionManager", "DeselectSelectedAsset", "remove_asset", "System.Boolean");
            F("EditorPopupManager", "CurrentPopup", "EditorPopup", "asset tools (dialog check)", true);
            M("PummelMaterial", "SetShader", "create_material, set_material", "UnityEngine.Shader");
            P("PummelMaterial", "UnityMaterial");
            M("StandardShaderInspector", "UpdateKeywords", "set_material (what the inspector runs after each change)", "PummelMaterial");
            M("DefaultShaderInspector", "UpdateKeywords", "set_material", "PummelMaterial");
            E("MaterialRenderingMode", null, "Opaque=0", "Cutout=1", "Fade=2", "Transparent=3");
            AddPrefabs(l);
        }

        /// <summary>The prefab tools (plugin 0.8.0): create_prefab, open_prefab, close_prefab, the save_mod protection, remove_asset on prefabs.</summary>
        private static void AddPrefabs(List<RequiredMember> l)
        {
            const string use = "prefab tools";
            void F(string type, string name, string fieldType, string u = use, bool isStatic = false) =>
                l.Add(new RequiredMember { Kind = "field", Type = type, Name = name, Extra = fieldType, IsStatic = isStatic, UsedBy = u });
            void P(string type, string name, string u = use) =>
                l.Add(new RequiredMember { Kind = "property", Type = type, Name = name, UsedBy = u });
            void M(string type, string name, string u, params string[] parameters) =>
                l.Add(new RequiredMember { Kind = "method", Type = type, Name = name, Params = parameters, UsedBy = u });

            M("EditorSystem", "CreateNewPrefab", "create_prefab (writes Assets/Prefabs/<name>.pfab and .pmeta)", "ModTransform");
            P("PummelMod", "FullPrefabsFolder", "create_prefab (the folder CreateNewPrefab writes into must be <mod>\\Assets\\Prefabs)");
            P("EditorSystem", "CurrentPrefab", "open_prefab / close_prefab (the setter saves the prefab it leaves)");
            F("EditorSystem", "m_prefabsRoot", "UnityEngine.Transform", "prefab tools (create cleanup, hierarchy scans)");
            M("EditorSelectionManager", "DeselectAllObjectsAndGizmos", "close_prefab (the Return to Hierarchy sequence)");
            M("ModPrefabInstance", "ForceUpdate", "close_prefab (instance refresh, as Return to Hierarchy)", "PummelPrefab");
            P("ModPrefabInstance", "PrefabReference", "close_prefab / get_asset (instances of a prefab)");
            F("PummelPrefab", "isEnabled", "System.Boolean");
            P("PummelPrefab", "PrefabObject");
            M("PummelPrefab", "Save", "the call every prefab write goes through (dry run, verify, restore around it)");
            M("ModHierarchySerializer", "SaveTransform", "dry run of a .pfab (writes to a MemoryStream)", "System.IO.BinaryWriter", "UnityEngine.Transform", "System.Boolean", "System.Boolean");
            F("ModHierarchySerializer", "Identifier", "System.String", "the .pfab header (PMH)", true);
            F("ModHierarchySerializer", "MajorVersion", "System.UInt16", "the .pfab header version", true);
            F("ModHierarchySerializer", "MinorVersion", "System.UInt16", "the .pfab header version", true);
            P("ModAssetExternal", "HideAndDontSave", "save_mod protection (the prefabs PummelMod.Save rewrites)");
            F("ModPrefab", "targetPrefab", "PummelPrefab", "create_prefab cleanup");
        }

        // ------------------------------------------------------------------ type lookup across assemblies

        private static Assembly FindAssembly(Assembly game, string name)
        {
#if NETFRAMEWORK
            if (game.ReflectionOnly)
            {
                foreach (Assembly a in AppDomain.CurrentDomain.ReflectionOnlyGetAssemblies())
                {
                    if (a.GetName().Name == name)
                    {
                        return a;
                    }
                }
                foreach (AssemblyName an in game.GetReferencedAssemblies())
                {
                    if (an.Name == name)
                    {
                        return Assembly.ReflectionOnlyLoad(an.FullName);
                    }
                }
                return null;
            }
#endif
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == name)
                {
                    return a;
                }
            }
            foreach (AssemblyName an in game.GetReferencedAssemblies())
            {
                if (an.Name == name)
                {
                    return Assembly.Load(an);
                }
            }
            return null;
        }

        /// <summary>Type of a registered member: in Assembly-CSharp, or in the named assembly.</summary>
        public static Type ResolveType(Assembly game, string typeName, string assemblyName)
        {
            if (assemblyName == null)
            {
                return game.GetType(typeName, false);
            }
            Assembly a = FindAssembly(game, assemblyName);
            return a?.GetType(typeName, false);
        }

        private static string Pairs(Type enumType)
        {
            return string.Join(", ", enumType.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name + "=" + Convert.ToInt64(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        }

        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>Null if the member exists as registered, otherwise a short description of what is wrong.</summary>
        public static string Resolve(Assembly game, RequiredMember m)
        {
            Type t = ResolveType(game, m.Type, m.Assembly);
            if (t == null)
            {
                return "type " + m.Type + " not found";
            }
            switch (m.Kind)
            {
                case "field":
                {
                    for (Type c = t; c != null; c = c.BaseType)
                    {
                        FieldInfo f = c.GetField(m.Name, Declared);
                        if (f != null)
                        {
                            if (m.Extra != null && f.FieldType.ToString() != m.Extra)
                            {
                                return $"field type is {f.FieldType}, expected {m.Extra}";
                            }
                            return f.IsStatic == m.IsStatic ? null : (m.IsStatic ? "field is not static" : "field is static");
                        }
                    }
                    return "field not found";
                }
                case "property":
                {
                    for (Type c = t; c != null; c = c.BaseType)
                    {
                        PropertyInfo p = c.GetProperty(m.Name, Declared);
                        if (p != null)
                        {
                            MethodInfo g = p.GetGetMethod(true);
                            return g != null && g.IsStatic == m.IsStatic ? null : "property static-ness differs";
                        }
                    }
                    return "property not found";
                }
                case "method":
                {
                    for (Type c = t; c != null; c = c.BaseType)
                    {
                        foreach (MethodInfo mi in c.GetMethods(Declared).Where(x => x.Name == m.Name))
                        {
                            string[] ps = mi.GetParameters().Select(x => x.ParameterType.ToString()).ToArray();
                            if (ps.SequenceEqual(m.Params ?? new string[0]))
                            {
                                return null;
                            }
                        }
                    }
                    return "method not found with parameters (" + string.Join(", ", m.Params ?? new string[0]) + ")";
                }
                case "type":
                    return null;
                case "settingsMember":
                {
                    // a public field or property of the type or a base type (settings fields are plain data members)
                    for (Type c = t; c != null; c = c.BaseType)
                    {
                        FieldInfo f = c.GetField(m.Name, Declared);
                        Type found = f?.FieldType;
                        if (found == null)
                        {
                            found = c.GetProperty(m.Name, Declared)?.PropertyType;
                        }
                        if (found != null)
                        {
                            return m.Extra == null || found.ToString() == m.Extra ? null : $"member type is {found}, expected {m.Extra}";
                        }
                    }
                    return "field or property not found";
                }
                case "enumType":
                {
                    if (!t.IsEnum)
                    {
                        return t.FullName + " is not an enum";
                    }
                    string actual = Pairs(t);
                    string expected = string.Join(", ", m.Params ?? new string[0]);
                    HashSet<string> a = new HashSet<string>(actual.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                    HashSet<string> e = new HashSet<string>(expected.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                    return a.SetEquals(e) ? null : $"enum values are [{actual}], expected [{expected}]";
                }
                case "settingsRange":
                {
                    for (Type c = t; c != null; c = c.BaseType)
                    {
                        FieldInfo f = c.GetField(m.Name, Declared);
                        if (f == null)
                        {
                            continue;
                        }
                        foreach (CustomAttributeData ad in CustomAttributeData.GetCustomAttributes(f))
                        {
                            if (ad.AttributeType.Name == "RangeAttribute" && ad.ConstructorArguments.Count >= 2)
                            {
                                double min = Convert.ToDouble(ad.ConstructorArguments[0].Value, System.Globalization.CultureInfo.InvariantCulture);
                                double max = Convert.ToDouble(ad.ConstructorArguments[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                                double emin = double.Parse(m.Params[0], System.Globalization.CultureInfo.InvariantCulture);
                                double emax = double.Parse(m.Params[1], System.Globalization.CultureInfo.InvariantCulture);
                                return Math.Abs(min - emin) < 1e-4 && Math.Abs(max - emax) < 1e-4 ? null : $"Range attribute is {min}..{max}, the rules document {emin}..{emax}";
                            }
                        }
                        return "member has no Range attribute (the rules document one)";
                    }
                    return "field not found";
                }
                case "ruleProperty":
                    return FindRuleProperty(t, m.Name) != null ? null : "property not found";
                case "enumValue":
                {
                    PropertyInfo p = FindRuleProperty(t, m.Extra);
                    if (p == null)
                    {
                        return "property " + m.Extra + " not found";
                    }
                    Type pt = p.PropertyType;
                    if (!pt.IsEnum)
                    {
                        // MinMaxCurve: its condition names are UnityEngine.ParticleSystemCurveMode values.
                        pt = pt.Name == "MinMaxCurve" ? pt.Assembly.GetType("UnityEngine.ParticleSystemCurveMode", false) : null;
                        if (pt == null)
                        {
                            return "property " + m.Extra + " is not an enum";
                        }
                    }
                    return pt.GetFields(BindingFlags.Public | BindingFlags.Static).Any(f => f.Name == m.Name) ? null : "enum " + pt.Name + " has no value " + m.Name;
                }
                default:
                    return "unknown member kind " + m.Kind;
            }
        }

        /// <summary>Public instance property as ModReflectionSystem collects them: declared on the type, else on ModBehaviour.</summary>
        private static PropertyInfo FindRuleProperty(Type t, string name)
        {
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            PropertyInfo p = t.GetProperty(name, f);
            if (p != null)
            {
                return p;
            }
            for (Type c = t.BaseType; c != null; c = c.BaseType)
            {
                if (c.Name == "ModBehaviour")
                {
                    return c.GetProperty(name, f);
                }
            }
            return null;
        }

        /// <summary>Problems of every registered member against the assembly (empty = all present).</summary>
        public static List<string> ResolveAll(Assembly game)
        {
            List<string> problems = new List<string>();
            foreach (RequiredMember m in s_all)
            {
                string why;
                try
                {
                    why = Resolve(game, m);
                }
                catch (Exception e)
                {
                    why = e.GetType().Name + ": " + e.Message;
                }
                if (why != null)
                {
                    problems.Add($"{m.Id}: {why} (used by {m.UsedBy})");
                }
            }
            return problems;
        }
    }
}
