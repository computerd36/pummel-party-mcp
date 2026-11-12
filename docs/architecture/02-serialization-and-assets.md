# 02 - Serialization, load/save paths, assets, cache (code-side reference)

Source: static reading of decompiled `Assembly-CSharp` only. **[C]** confirmed from code, **[I]** inferred, **[?]** unverified. Evidence = `File.cs:line` (root folder unless a subfolder is given). Format details live in `docs/mod-format.md`; this doc covers call chains, write/delete behaviour and safety.

## 1. Safety summary (read this first)

| Question | Answer |
|---|---|
| Is saving atomic (temp file + rename)? | **No.** Every file is written in place: JSON via `File.WriteAllText` (`ModDataReaderWriter.cs:9-13`), binary via `File.Open(..., FileMode.Create)` which truncates then writes (`:579-587`, `PummelPrefab.cs:73`), `.pmeta` via `File.WriteAllBytes` (`ModAssetExternal.cs:58`). No `File.Replace`, no `.tmp`, no `.bak` anywhere in the code base (grep over all `*.cs`) [C]. |
| Backups made by the game? | **None.** No `.bak`, no copy-before-overwrite, no versioned save. [C] |
| Partial-save risk | `PummelMod.Save()` (`PummelMod.cs:567-592`) has no try/catch and writes in sequence: `Meta.json`, `WorkshopItem.json`, `Lighting.json`, `PostProcessing.data`, `MainScene.scene`, mod-specific (`MinigameDefinitionData.json`/`MapDetails.json` + `ModSettings.json`), then every external asset's `Save()` (`.pmeta` each; `.pfab`, `.pmat` stub, `.pgf` bodies). An exception mid-way leaves earlier files new and later files old. The scene and post-process binaries are serialized into a `MemoryStream` first and only then written (`ModDataReaderWriter.cs:569-587`, `:78-192`), so a serialization exception (e.g. "not implemented" type) aborts **before** truncating that file. `.pfab` is serialized straight into the opened file stream (`PummelPrefab.cs:73-76`): an exception there leaves a truncated prefab. [C] |
| Does a normal save rewrite all files or only changed ones? | **All of them, unconditionally** (no dirty tracking per file). `IsModDirty` only drives the "unsaved changes" popup (`EditorSystem.cs:407`). Also rewrites `Preview.jpg` (`EditorSystem.SaveCurrentMod`, `:808-821`; `SavePreviewScreenshot`, `:1363-1368`) and bumps `Meta.json.LastModified`. [C] |
| Does saving delete files/folders? | **No.** `PummelMod.Save` and every `ModAsset.Save` only create/overwrite. No unused-asset cleanup, no orphan `.pmeta` cleanup. [C] |
| What can delete user data? | (a) `PummelMod.RemoveAsset` -> `DeleteFile` (asset file + `.pmeta`), user action in asset browser (`PummelMod.cs:827-865`); (b) `EditorSystem.ReimportAsset` when the source file is missing calls `RemoveAsset` (removes the orphan `.pmeta`) (`EditorSystem.cs:908-933`); (c) `PummelMod.DeleteFailedAsset` after a failed import-with-copy (`:941-951`); (d) whole-mod delete: `Directory.Delete(<WorkshopMods>\<name>, recursive)` in `LoadModPopup.DeleteModResult` (`LoadModPopup.cs:210-235`) and `MainMenuWidget.DeleteModResult` (`MainMenuWidget.cs:285-305`), both behind a yes/no popup; (e) texture/mod cache deletions (section 5); (f) lighting custom preset delete (`LightingWidgetLogic.cs:270`, a different folder). None run automatically on save/load of an editor mod. [C] |
| Edge: copy-collision delete | `PummelMod.AddAsset(path, folder, copy:true)` tries 100 candidate names `<stem>(i)`; if all 100 exist, the last candidate (an existing file) is used, `File.Copy` throws and the catch calls `DeleteFailedAsset(text)` on that **existing** file + its `.pmeta` (`:912-927`). Requires 100 duplicates; avoid by unique names. [C] |
| Does a failed load overwrite files? | The load failure path itself writes nothing: `PostLoadCleanup` shows a popup and calls `SetCurrentMod(null)` (`PummelMod.cs:471-490`), so a later Ctrl+S has no current mod. No autosave timer exists; `SaveCurrentMod` callers: Ctrl+S (`EditorSystem.cs:1676-1678`), `AT_SaveMod`, main-menu Save (`MainMenuWidget.cs:98`), "save changes" popup (`ModUnsavedChangesPopup.cs:24`), new empty mod (`EditorSystem.cs:889` delayed save), `FinishTemplateSetup`, Workshop-create callback (`WorkshopModInstaller.cs:1042`). [C] |
| **But loading itself writes** | (1) Missing `<asset>.pmeta` -> new sidecar written (`PummelAssetImporter.TryCreateOrLoadMetaAsset`, `PummelAssetImporter.cs:106-148`); (2) **corrupt** `.pmeta` -> sidecar overwritten with a fresh one with a NEW GUID (`:127-131`), silently breaking every reference to that asset; (3) opening creates missing folders `Assets\`, `Data\` etc. (`PummelMod.ReadAssetFileInfos`, `:998-1004`; `CreateFolders` only for empty/new mods); (4) the PNG/JPG importer creates `PummelModCache\<publishedFileId>\` even in the editor (section 5). [C] |
| Silent default-on-corrupt (then overwritten at next save) | `Lighting.json`, `ModSettings.json`, `MapDetails.json`, `MinigameDefinitionData.json`, `PostProcessing.data` (unknown version): a corrupt or missing file loads as defaults with load result `Success`; the next save writes the defaults over the file. A dangling asset GUID in the scene loads as null (error logged), saved back as null. Treat any "Success" load as possibly lossy. [C] |

## 2. Load path (editor, "open mod folder" -> live scene)

```
UI (LoadModPopup / main menu / plugin)
 EditorSystem.OpenMod(path)                           EditorSystem.cs:863
  PummelMod.GetType(path)                             PummelMod.cs:1216      reads Meta.json ModType -> PummelMod.ModTypes[]
  PummelMod.CreatePummelModObject(path, Editor, type) PummelMod.cs:203       Activator.CreateInstance(type, path, loadType)
     ctor: Meta name := folder name, defaults, workshopItem stub, MinigameMod/BoardMod ctor sets ModType + definition objects
  EditorSystem.LoadModIntoEditor(pm)                  EditorSystem.cs:869    progress popup; SetCurrentMod(pm) closes previous mod
   pm.LoadModAsync()                                  PummelMod.cs:364       NEW THREAD -> LoadModInternal
    LoadModInternal(): stages run in order, abort at first lastResult != Success  (:391-459)
     0 Setup (nothing)
     1 LoadWorkshopItemDetails   WorkshopItem.json      missing -> WorkshopFileMissing (abort)
     2 LoadModMetaData           Meta.json              missing -> MetaDataFileMissing; parse fail -> Failed (abort)
     3 RefreshImportedFiles      Assets\** (section 3)   exception -> ImportFilesException (abort)
     4 LoadLightingSettings      Lighting.json (+ preset file); always Success
     5 LoadPostProceessing       PostProcessing.data (+ preset); always Success
     6 LoadScene                 MainScene.scene (main thread)   missing -> SceneFileMissing; exception -> SceneException
     7 LoadModSpecificData       ModSettings.json + MinigameDefinitionData.json / MapDetails.json (main thread); always Success
     8 RenderItemPreviews        asset thumbnails (atlas)
    if ModVersion < VERSION: UpdateModVersion()  (in memory only)            :451-454
    PostLoadCleanup (main thread)                                             :471-490
       Success: apply lighting, ModBehaviour.OnFinishedLoad on hierarchy + prefab roots (:511-524), IsLoaded=true
       failure: popup ShowLoadFailedPopup + SetCurrentMod(null)
```
Any exception inside a stage is caught -> `Failed` (`:441-445`). Failures on stages 1-3, 6 abort the load; stages 4, 5, 7 can never fail (defaults). [C]

### 2.1 Scene load detail (`PummelMod.LoadSceneInternal`, `:658-678`)

`File.OpenRead(MainScene.scene)` -> `ModHierarchySerializer.LoadTransform(br, root, ignoreRoot:true)` (`ModHierarchySerializer.cs:183-196`):
1. `LoadVersion`: read string; if it is not `"PMH"` the stream rewinds to 0 and is treated as version 0.0 (legacy: no tag field) (`:167-181`).
2. `LoadHierarchy`: recreate GameObjects, name/active/layer/(tag), parent (`:198-227`).
3. `LoadComponents`: for each object read the component table, `GetOrAddComponent` each type, set guid/enabled, `SetupComponents()` for newly added ones; unknown type name -> `[ModPreviouslySerializedAs]` lookup -> else exception (whole load fails); removes extra components not in file (`:229-292`).
4. `ModBehaviourMonitor.UpdateBehaviours()` registers all behaviours as runtime `ModAsset`s so GUID references can resolve (`:190-193`).
5. `LoadComponentValues`: per component `ModReflectionTypes.ReadTypes`, unknown property names skipped by recorded byte length; then `OnAfterDeserialize()` (`:307-317`).
Prefab files go through the same functions split over `PummelPrefabImporter` (`LoadHierarchy/LoadComponents/LoadValues`, `PummelPrefabImporter.cs:92-146`), with `IsLoadingPrefabs=true`. [C]

### 2.2 Error handling matrix

| Condition | Result | Writes? |
|---|---|---|
| `Meta.json` missing / unparsable | load aborts, popup, mod closed | no |
| `WorkshopItem.json` missing / unparsable | same | no |
| `MainScene.scene` missing | `SceneFileMissing`, abort | no |
| Scene truncated / garbage / unknown component type / exception in a property setter | `SceneException`, abort (`lastException` kept for popup) | no |
| Scene property value type changed incompatibly | stream desynchronises -> garbage values or exception [I: no re-sync] | no |
| Asset GUID not found | `null` + console error, load continues | next save persists the null |
| Asset file unreadable (OBJ invalid, image fails) | importer returns empty/null, asset simply absent; later references -> dangling | no (a sidecar may have been created) |
| `.pmeta` corrupt | recreated with new GUID | **yes (overwrite)** |
| Settings/lighting/postprocess corrupt | defaults, Success | next save overwrites |
Note `LoadObjectFromJson` returns `Failed` with a default object on parse failure (`ModDataReaderWriter.cs:20-47`); callers decide. `WaitForFile` is an empty stub (no locking/retry). [C]

### 2.3 Runtime (in-game) load differs

`WorkshopController` loads mods with `loadType = IngameRuntime/IngameTest/BackgroundInitialize` using `RefreshImportedFiles` in two passes (everything but `.pfab`, then `.pfab`) and `LoadModSpecificData` (`WorkshopController.cs:429-451, 585, 699`). It uses the same file readers and therefore the same "write missing `.pmeta`" behaviour, now against Steam's install folder. Not needed for the plugin (editor-focused).

## 3. Save path in detail

```
Ctrl+S / menu / popup / plugin -> EditorSystem.SaveCurrentMod(delayed)      EditorSystem.cs:808
  CurrentMod.Save()  (or DelayedSave: coroutine waits for IsLoaded then Save)   PummelMod.cs:567 / 1181-1197
    SaveMetaData            Data\Meta.json         WriteAllText; LastModified = now        :689-697
    SaveWorkshopItemDetails Data\WorkshopItem.json WriteAllText                            :594-601
    if UsesLighting     SaveLightingSettings(Data, "Lighting.json", CustomLighting)       ModDataReaderWriter.cs:53
    if UsesPostProcessing SavePostProcessSettings(Data, "PostProcessing.data", postProcess):78
    if UsesHierarchy    SaveScene(Data, "MainScene.scene", hierarchyRoot)                  :569
        ModHierarchySerializer.SaveTransform -> MemoryStream -> WriteByteDataToFile (FileMode.Create)
    SaveModSpecificData  (MinigameMod: MinigameDefinitionData.json + ModSettings.json; BoardMod: MapDetails.json + ModSettings.json)
    foreach asset in Assets.assets where ModAssetExternal && !HideAndDontSave: asset.Save()
        base: <file>.pmeta (WriteAllBytes)  ; PummelPrefab: .pfab body then .pmeta ; PummelMaterial: ".pmat" stub + .pmeta (with shader data)
        PummelVisualScript: regenerate types, .pgf JSON, then .pmeta
  m_availableModManager.UpdateLoadedModLastModifiedTime
  SavePreviewScreenshot -> Data\Preview.jpg (WriteAllBytes, 640x360 JPEG)
  undo manager marks mod clean
```
Properties: every write creates the parent directory if missing (`SaveMetaData`, `WriteByteDataToFile`, `PummelVisualScript.Save`). The set of files written is "all known assets in memory": an asset file that was deleted outside the editor while the mod was open gets its `.pmeta` rewritten (the sidecar is recreated even though the source is gone) [I]. Files in `Assets\` that were never imported (unknown ext) are not touched. Save never moves/renames files. [C]

Asset-level saves that happen outside `Save()`: new prefab (`EditorSystem.CreateNewPrefab`, `:2411-2423`) and new material (`CreateNewMaterial`, `:2425-2441`) write their files immediately; `ReimportAsset` saves the sidecar first (`:908-915`); asset inspector edits are applied on reimport; `RenameMod` saves meta+workshop item+definition at once.

## 4. Programmatic entry points

| Method signature | What it does | Writes / deletes on disk? | Evidence |
|---|---|---|---|
| `EditorSystem.OpenMod(string path)` | build `PummelMod` for the folder (type from Meta.json) and run async load | No explicit writes; load may write missing/corrupt `.pmeta`, create `Assets\` | `EditorSystem.cs:863`; `PummelMod.cs:364` |
| `EditorSystem.OpenModWithPopupPrompt(string)` | same but asks to save if dirty (modal popup: avoid from a plugin) | as above | `EditorSystem.cs:848` |
| `EditorSystem.CreateNewMod(string path, PummelModType, string templatePath = null)` | empty mod (folders + defaults + delayed save) or copy template folder then load + `RenameMod(folderName)` | Writes: whole new tree; template copy (`DirectoryCopy`, no overwrite) incl. `.pmeta` (GUIDs identical to template) | `EditorSystem.cs:823-846`; `ZP.Utility/DirectoryUtility.cs` |
| `EditorSystem.SaveCurrentMod(bool delayed = false)` | full save of current mod (section 3) | Overwrites all mod files + `Preview.jpg`; no deletes | `EditorSystem.cs:808` |
| `PummelMod.Save()` | the save itself; no screenshot | overwrite in place, no atomicity | `PummelMod.cs:567` |
| `EditorSystem.RenameMod(string modPath, string newName)` | UI-level rename; checks target folder absent | `Directory.Move` folder; rewrites Meta/WorkshopItem/definition json; loaded mod also reimports | `EditorSystem.cs:1172-1215` |
| `PummelMod.RenameMod(string)` / `RenameUnloadedMod(string folder, string name)` | metadata-only rename after the folder move | overwrites `Meta.json`, `WorkshopItem.json`, `MinigameDefinitionData.json`/`MapDetails.json` | `PummelMod.cs:300-309, 254-298`; `MinigameMod.cs:~32`; `BoardMod.cs:~42` |
| `EditorSystem.SetCurrentMod(PummelMod)` / `PummelMod.Close()` | switch/close; destroys scene objects and assets in memory | none | `EditorSystem.cs:772`; `PummelMod.cs:708-745` |
| `EditorSystem.ExitModEditor(bool ignoreUnsavedChanges = false)` | leave editor; may prompt | none by itself | `EditorSystem.cs:1530` |
| `PummelMod.AddAsset(string filepath, string folder, bool copy = true)` | import a file: copy into `Assets\<folder>\`, write `.pmeta`, decode | creates folder, copies file, writes sidecar; deletes the copy+sidecar on failure; name-collision edge case (section 1) | `PummelMod.cs:898-939` |
| `PummelMod.ImportFile(FileInfo, bool forceNewGuid=false)` | import a file already inside the mod | writes `.pmeta` if missing | `PummelMod.cs:1056-1087` |
| `PummelMod.AddDuplicatedAsset(string filepath)` | duplicate asset file+sidecar with `(n)` name, new GUID | copies 2 files | `PummelMod.cs:873-896` |
| `PummelMod.RefreshImportedFiles(Func<FileInfo,bool> filter = null)` | rescan `Assets\**` and import everything | writes missing/corrupt `.pmeta` | `PummelMod.cs:953-996` |
| `PummelMod.RemoveAsset(ModAsset)` | remove an asset from the mod | **deletes asset file + `.pmeta`** | `PummelMod.cs:827-865` |
| `ModAssetExternal.Rename(string)` | rename asset file + sidecar | `File.Move` x2 | `ModAssetExternal.cs:70-110` |
| `ModAssetExternal.Save()` / `SaveMetaFile()` | write sidecar (subclasses write bodies) | overwrite | `ModAssetExternal.cs:50-68` |
| `EditorSystem.CreateNewPrefab(ModTransform t = null)` / `CreateNewMaterial()` | new `.pfab` / `.pmat` + sidecar in `Assets\Prefabs|Materials` | creates files immediately | `EditorSystem.cs:2411-2441` |
| `EditorSystem.ReimportAsset(ModAssetExternal)` | save sidecar, re-import; if source missing -> `RemoveAsset` | writes; may delete orphan `.pmeta` | `EditorSystem.cs:908-933` |
| `ModDataReaderWriter.SaveObjectToJson<T>(string path, T)` / `LoadObjectFromJson<T>(...)` | JsonUtility file I/O helpers | overwrite in place | `ModDataReaderWriter.cs:9-47` |
| `ModDataReaderWriter.SaveScene(string path, string fileName, Transform root)` | write scene binary | overwrite in place | `:569` |
| `ModHierarchySerializer.SaveTransform(BinaryWriter, Transform, bool ignoreRoot=false, bool ignoreChildren=false)` / `LoadTransform(BinaryReader, Transform, ...)` | (de)serialize any hierarchy to any stream; usable for in-memory dry-runs (e.g. round-trip tests) | none (stream only) | `ModHierarchySerializer.cs:79, 183` |
| `ModReflectionTypes.WriteTypes/ReadTypes`, `ModReflectionSystem.GetType(Type)` | property-level serializer; `ModReflectedType.properties` is the exact list of serialized members per component | none | `ModReflectionTypes.cs:245, 476`; `ModReflectionSystem.cs:48` |
| `WorkshopController.Instance.GetAssetWithGuid(SerializableGuid)` / `GetAssetWithObject(object)` | GUID <-> object resolution (global list, then current mod) | none | `WorkshopController.cs:777-811` |
| `ModCacheSystem.DeleteAllModCache()` / `DeleteCacheFolderForMod(ulong)` / `SweepAndDeleteUnusedCacheFiles()` | cache management | **deletes** under `PummelModCache` only | `ModCacheSystem.cs:94-228` |
| `EditorSystem.SavePreviewScreenshot()` | render scene camera to `Data\Preview.jpg` | overwrite | `EditorSystem.cs:1363` |
| (UI-only, not for the plugin) `LoadModPopup.DeleteModResult`, `MainMenuWidget.DeleteModResult` | delete a mod folder | **`Directory.Delete(recursive)`** on `<WorkshopMods>\<name>` | `LoadModPopup.cs:210-235`; `MainMenuWidget.cs:285-305` |
All of the above must run on the Unity main thread (the loader itself uses `GameManager.RunOnMainThread`). `LoadModAsync` spawns its own worker thread and marshals Unity calls back (`PummelMod.cs:364-377`); a plugin must wait for `PummelMod.OnFinishedLoading` / `IsLoaded` before touching the scene [C].

## 5. `PummelModCache` and `ModCacheSystem` [C]

- Content: **only a texture cache** for PNG/JPG assets of a mod, per mod id: `<persistentDataPath>\PummelModCache\<publishedFileId>\<textureName>.ptraw` (raw `GetRawTextureData`) and `<textureName>.ptinfo` (JSON: `TextureDataVersion`, `LastTextureFileSizeBytes`, `LastModifiedDateTime`, `GraphicsFormat`, `TextureFormat`, `Width`, `Height`, `MipCount`, `IsSRGBFormat`) (`PummelImageImporter.cs:11-36, 214-255, 282-305`; folder `ModCacheSystem.cs:18-37`). Mesh/audio/other assets are not cached.
- Write condition: `m_useCache = (EditorSystem.Instance == null) && Settings.UseModCache` (`PummelImageImporter.cs:61`; setting default on, `Settings.cs:750`). `EditorSystem.Instance` is null outside the mod editor scene, so cache files are written when a mod is **loaded in the game proper** (downloaded Workshop mods, and local mods when launched/tested in-game [I]) and not while editing. Written asynchronously with `File.WriteAll*Async` after compression (`:282-305`).
- Reuse: valid when stored source file size and last-write time (UTC "O") equal the current source's; otherwise `DeleteCacheForTexture` removes both cache files and re-decodes (`:177-221`).
- Keying: **`publishedFileId`**. Local, never-published mods all use `256` (`ModWorkshopItem.DefaultWorkshopID`), so they share one folder `PummelModCache\256\` keyed by bare texture file name (name collisions across different local mods would alias; stale entries are detected only by size+timestamp) [I].
- Editor side effect: `GetPathStringsForFile` runs unconditionally and `ModCacheSystem.GetModTextureCacheFolderPath` does `Directory.CreateDirectory(PummelModCache\<id>)` even in the editor (`PummelImageImporter.cs:92, 138-168`; `ModCacheSystem.cs:26-35`), so an editor load can create (empty) cache folders. [C]
- Sweep (`SweepAndDeleteUnusedCacheFiles`, called from `WorkshopModInstaller.cs:315`, background thread): for each numeric subfolder: not subscribed and id >= 1024 and any file unused for 14 days -> delete the whole folder; ids < 1024 (incl. 256) are never swept by age; subscribed mods -> delete cache files whose texture no longer exists in the mod's `Assets\Textures`. `DeleteAllModCache` is a Settings button (`SettingsButton.cs:98`); `DeleteCacheFolderForMod` runs on unsubscribe (`WorkshopModInstaller.cs:836`). None of these touch `WorkshopMods`.
- Therefore: the cache is derived data, never read when editing, safe to ignore; it relates to downloaded mods **and** to local mods played in-game; deleting it only costs re-decode time.

## 6. Asset system (code view)

- Registry: `ModAssetList` ScriptableObject = list of `ModAsset` kept **sorted by guid**, binary-searched (`ModAssetList.cs:43-46, 108-121, 181-221`). Two layers: global (built-in, loaded from the game; `WorkshopController.Assets`) and per-mod `PummelMod.Assets`; scene behaviours add runtime entries (`ModBehaviourMonitor.cs`).
- `ModAsset` (`ModAsset.cs:8-110`): `guid`, `name`, `tags`, `assetLocation`, `assetFolder`, `assetTypeString` (internal types only), plus transient preview/atlas state. `SetupVariables()` derives internal/external/runtime flags from `assetLocation` and lowercases tags.
- Internal assets: Addressables key = guid string; loaded on first access (`ModAsset.GetAddressable`, `:282-316`), released by `Unload`.
- External assets: `ModAssetExternal` subclasses + `.pmeta`. Importer dispatch by extension (`PummelAssetImporter.cs:77-114`). Import of a model/texture/audio source writes only the sidecar; output objects are in-memory (hidden `HideFlags.HideAndDontSave`).
- Prefab files load with `PummelPrefabImporter`: sidecar -> `PummelPrefab`, deserialize hierarchy under the prefab root, activate only when in runtime or being edited (`:92-146`). A prefab instance in a scene regenerates its children from the prefab at load (`ModPrefabInstance.UpdatePrefab`).
- Assets are keyed by GUID, never by path. Renaming an asset file through the editor keeps the GUID; external renames outside the editor without moving the `.pmeta` produce a new asset (new GUID) and break references. GUID identity of built-ins is defined by the game's addressables catalogue, not in code [?].

## 7. Reflection / attribute system (code view)

`ModReflectionSystem.Initialize` scans `Assembly-CSharp` once for `[ModSerialized]` types and builds `ModReflectedType` (name, runtime Unity type, ui metadata, `savePriority`, `supportsChildren`, `properties[]`) (`ModReflectionSystem.cs:21-28, 140-257`). `ModReflectedType.properties` = `GetReflectedProperties(type)`: public instance properties declared on the type (+ `ModBehaviour`'s `guid`), excluding overrides, `[ModIgnore]`, and types without a reflection mapping (these log "Cannot Save/Load") (`:74-112`). `[ModHideInInspector]` and `[ModHeader]` only set UI flags in `ReflectedProperty.Initialize`. Renamed **types** are mapped by `[ModPreviouslySerializedAs]`. Renamed or removed **properties** are silently skipped on load via size-prefixed property records. The type name stored in the file is `Type.ToString()` of the component class (not its Unity runtime type), e.g. `ModTransform`.
For a plugin: `ModReflectionSystem.GetType(typeof(X)).properties` yields exactly the settable surface that persists; mutating via those property setters (not private fields) reproduces editor behaviour including side effects (`SettingPropertiesFromLoad` distinguishes load-time sets).

## 8. Folder naming (code view)

Detailed in `mod-format.md` section 6. Key code: `NewModPopup.CreateNewMod` (`Path.Combine(EditorSystem.ModFolder, m_modName)`), `EditorPopup.IsValidFilenameInput/IsValidModnameInput` (`EditorPopup.cs:51-76`), `EditorSystem.RenameMod` (`:1172-1215`), `PummelMod` ctor (Name := `Path.GetFileName(path)`, `:160-170`). `MCP_xyz` -> folder `MCP_xyz`, Meta Name `MCP_xyz`, workshop title set from `GetModName()` at creation (`PummelMod.cs:187`). The check for duplicate names is on Meta Name (case-insensitive), while the on-disk collision check happens only in rename (`Directory.Exists`) and implicitly in template copy (no-overwrite copy throws).

## 9. Open questions / unverified

1. **Exact JSON shape** of `SerializableGuid` inside JsonUtility output (assumed `{"serializedGuid": "..."}`) and of `ModAssetReference<T>` (`m_asset` object field) and `.pmeta` byte[] (`m_shaderData`) - needs a real file or a runtime `JsonUtility.ToJson` probe through the plugin.
2. Whether `JsonUtility.FromJson` throws or silently defaults on partially invalid JSON in each file (Unity-native behaviour; only syntax errors reliably throw) - affects "corrupt" classification.
3. Whether an exception in `LoadPostProceessing`/`LoadLightingSettings` main-thread delegates (truncated `PostProcessing.data`) propagates to the load result or is swallowed by `GameManager.RunOnMainThread` (not read).
4. `ReflectedValue.SetValue/GetValue` (FastMember) exception handling when a property setter throws during load: assumed to propagate and fail the scene load.
5. Item (`ItemMod`) and BoardSpace (`BoardSpaceMod`) settings/definition formats (`ItemDetails.json`, `SimpleMod*Settings`) were not expanded; only minigame and board were studied.
6. `Color32`, `Gradient`, `AnimationCurve`, `MinMaxCurve` binary layouts are given by name only (`Extensions.cs:644-748, 857-900`); not needed for most edits.
7. Whether in-game "Test/Play" from the editor loads the local mod with `EditorSystem.Instance == null` (thus writing `PummelModCache\256`) - [I] from `ModEditorEmptyScene`/`WorkshopMinigame_Scene` scene switching (`EditorSystem.OnEditorPlay`, `SceneManager_sceneLoaded`); the observed `PummelModCache\<workshopId>` folder suggests at least Workshop mods do.
8. Unknown-component-type behaviour: code path says exception (whole scene fails); not confirmed at runtime. A plugin must only emit type names that exist in `ModReflectionSystem`.
9. The built-in asset GUID catalogue (internal prop/material/audio/texture/shader GUIDs) is in Addressables bundles/`ModAssetList` asset data, not in decompiled code; the plugin must enumerate it at runtime via `WorkshopController.Instance.Assets.assets`.
10. Concurrency: `LoadModAsync` runs stages on a worker thread while the editor UI keeps running; behaviour of calling `Save` during load is unprotected (`DelayedSaveRoutine` waits for `IsLoaded`, direct `Save()` does not) [C for absence of lock].
11. `ModSettings.json` for Board mods references assets via `ModAssetReference<PummelPrefab>`; round-trip when the referenced prefab is missing was not traced.
12. Whether the on-disk folder names for Workshop-downloaded/internal mods (`InbuiltMods`, Steam content folder) matter for the plugin: not studied (read-only there).

## Corrections and additions (Phase 4, asset import and removal built)

Recorded when the asset tools were built (plugin 0.8.0); details and evidence in `07-asset-import.md` (which has its own corrections section):

- The standing rule "the plugin never imports or removes assets" no longer holds. The plugin now copies files into the sandbox mod itself (never through `PummelMod.AddAsset(copy: true)`, which can delete the source or an existing `(99)` duplicate) and registers them with `PummelMod.ImportFile`; `remove_asset` replicates `PummelMod.RemoveAsset` after a path, class and reference check. The boundaries are in `docs/plugin-api.md` (Assets).
- The "100-duplicates" hazard noted above is avoided by construction: the plugin picks the free name (file and `.pmeta` both absent, at most 20 numbered duplicates, `FileMode.CreateNew`) and never lets the editor's loop run.
- A re-import (`EditorSystem.ReimportAsset`) of a texture creates a new `Texture2D` and the asset list swaps it into the registered asset; the old object is neither destroyed nor re-pointed. Components, action parameters and the preview image that hold the old object by identity then fail the `GetAssetWithObject` lookup and are saved as none (confirmed from `PummelImageImporter` and `ModAssetList.AddAsset`). Props keep their registered `Prop` object (only the mesh is replaced).
