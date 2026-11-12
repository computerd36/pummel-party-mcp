# 01 - Editor core and mod lifecycle

Reverse-engineering recon for pummel-party-mcp. Source: decompiled `Assembly-CSharp` (read-only), Pummel Party / Unity 2021.3 / Mono.
No game code is reproduced here; only class/member names, signatures and one-line paraphrases.

Legend: **[C]** confirmed by reading code. **[I]** inferred (reasoned from code, not directly observed). **[U]** unverified / needs a runtime check.
File references are `File.cs:line` relative to the `Assembly-CSharp` root. Line numbers come from this decompiled copy and may drift between game builds.

## TL;DR for plugin authors

- Everything hangs off one MonoBehaviour singleton: `EditorSystem.Instance` (null when the editor is not open). The open mod is `EditorSystem.Instance.CurrentMod` (a `PummelMod` subclass).
- Direct, non-popup entry points exist for open / create / save / rename / exit / playtest. None need ImGui input, but several push ImGui popups (loading bar, error boxes) as a side effect.
- **There is no timer-based autosave, no save on focus loss, quit, close or exit.** There ARE implicit saves: on entering playtest, on empty-mod creation, whenever `CurrentPrefab` changes away from a prefab, and on creating prefab/material/script assets. See section 7.
- The "dirty" flag is NOT on the mod. It lives in `EditorUndo` (one per hierarchy plus one per open prefab) and is only set by scene-graph undo snapshots. It is probably true right after a load. See section 6.
- UI is Dear ImGui through a native plugin (`ModEditorPlugin_Release`), drawn once per frame from a `WaitForEndOfFrame` coroutine, not from `Update()`.

---

## 1. Entering / exiting the editor; "is the editor open?"

### Enter (main menu to editor) [C]
1. Main-menu button event `MainMenuButtonEventType.GoModEditorScene` (`MainMenuButtonEventType.cs:57`) is handled in `UIController.cs:1373`: stops menu music, calls `GameManager.OpenScene("ModEditorScene")`.
   - The button itself is wired in the main-menu scene (Unity serialized event), not in code. `MainMenuSceneController.cs` only picks the 3D background scene (`MainMenuBackground.ModEditorUpdate` maps to the "TitlePage" background, `MainMenuSceneController.cs:104`); it has no editor entry logic.
   - `AT_OpenModEditor` calls `GameManager.MainMenuUIController.DoButtonEvent(MainMenuButtonEventType.GoModEditorScene)`. That is the programmatic entry; valid only while `GameManager.MainMenuUIController != null` and `EditorSystem.Instance == null`.
2. `GameManager.OpenScene(string)` (`GameManager.cs:2485`) forwards to `GameManagerObj.OpenScene` (`GameManagerObj.cs:148`), which instantiates a `DisconnectTransition` prefab and calls `Setup(name)`; its coroutine (`DisconnectTransition.cs:38-78`) loads the scene with `Addressables.LoadSceneAsync`. Asynchronous: the scene is not active when the call returns.
3. Scene `ModEditorScene` contains the `EditorSystem` object (the many public Camera/Transform/Texture fields in `EditorSystem.cs:34-160` are Unity-serialized references). [I: scene contents are not in the decompiled C#.]
4. `EditorSystem.Awake` (`:1296`): `ImGui.CreateImGuiContext()`, `m_instance = this`, `DontDestroyOnLoad` on itself and on the scene-view camera, play-mode UI camera, hierarchy root, prefabs root, audio listener, post-process volume, gizmo manager, reflection probe.
5. `EditorSystem.Start` (`:1347`) calls `Init()` (`:1089`): builds widgets (`ToolbarWidget`, `MainMenuWidget`, `InspectorWidget`, `SceneViewWidget`, `HierarchyWidget`, `LogWidget`, `FileBrowserWidget`, `ActionEditorWindow`), loads the ImGui layout from `<persistentDataPath>/LastUsedLayout.ini` (or `StreamingAssets/ModEditorLayouts/Default.ini`), sets `ModFolder`, scans mods (section 4), subscribes `SceneManager.sceneLoaded`, optionally queues the Welcome popup (PlayerPrefs `HasOptedOutOfModEditorWelcome`), resets `GameManager.CurMap` / `CurrentMinigameDef`.
6. `OnEnable` (`:1352`) subscribes selection events and starts the `CallPluginAtEndOfFrames` coroutine (the ImGui draw loop, section 9).
7. `SetupWindow` coroutine (`:1273`): window title "ModEditor", forced windowed, OS window maximized via `Win32Wrapper.Instance.ShowWindow(ShowWindowCommand.SW_MAXIMIZE)`.

### Exit (editor to main menu) [C]
- `EditorSystem.ExitModEditor(bool ignoreUnsavedChanges = false)` (`:1530`): if a mod is open AND `IsModDirty` AND not ignoring, pushes `ModUnsavedChangesPopup` whose continue callback is `RunExitModEditorLogic`; otherwise calls `RunExitModEditorLogic()` directly.
- `RunExitModEditorLogic()` (`:1545`, public): starts a thread that reinstalls Workshop mods recorded via `AddModToReinstallOnExit` (only real `publishedFileId`, i.e. not 256), `Destroy(gameObject)`, `PummelPreviewRenderer.ClearPreviewRendererData()`, then `GameManager.OpenScene("MainMenu")`. **Does not save.**
- `EditorSystem.OnDestroy` (`:2625`): writes the ImGui layout `.ini`, destroys child roots/cameras, `m_instance = null`, calls `SetCurrentMod(null)` (closes the mod in memory, no mod write), destroys the ImGui context, restores window title "Pummel Party".
- Exit triggers: File > "Exit to Main Menu" (`MainMenuWidget.cs:143`); `Esc` with a Yes/No popup when no popup is open and `State == Editing` (`EditorSystem.cs:1662`); `AT_CloseModEditor` (`ExitModEditor(ignoreUnsavedChanges: true)`, then loops `EditorPopupManager.CloseTopPopup()` until the active scene is "MainMenu").
- `OnApplicationQuit` (`:1281`) is an empty body. Quitting the game with the editor open runs no save.

### Scenes / objects while open [C unless noted]
- Scene names in code: `ModEditorScene` (entry), `ModEditorEmptyScene` (returned to after playtest, `EditorSystem.cs:1850`), `MainMenu`. Playtest scenes: `WorkshopMinigame_Scene` (minigame), `WorkshopMap_Scene` (board), `PirateMap_Scene` (item), chosen in each mod class's `OnEditorPlay`. `GameManagerObj.cs:73` treats `MainMenu`/`ModEditorEmptyScene`/`ModEditorScene` as menu-like (sets `GameManager.MainMenuLoaded`, calls `GameManager.Reset()`).
- Editor roots are `DontDestroyOnLoad`, so the edited hierarchy survives the playtest scene switch. `SceneManager_sceneLoaded` (`:1249`) sets `State = Editing` on `ModEditorEmptyScene` and `PlayMode` on `PirateMap_Scene`; board/minigame set PlayMode from `GameBoardController.cs:1215` and `:4068`.
- Singletons / statics: `EditorSystem.Instance`, `EditorSystem.ModFolder`, `EditorPopupManager.CurrentPopup`, `WorkshopController.Instance` (editor `Assets` property forwards to it, `EditorSystem.cs:391`), `ModBehaviourMonitor.Instance` (its `active` flag follows "a mod is open"), `DebugDraw.Instance`, `Win32Wrapper.Instance`.
- While active, `EditorSystem.SetActive` (`:1009`) sets `GameManager.DEBUGGING` and `GameManager.QUICKMODE` to `!active`.

### Detecting "editor open right now?" [C]
- Primary: `EditorSystem.Instance != null` (set in `Awake`, nulled in `OnDestroy`). The game uses exactly this idiom everywhere (`ToolbarWidget.cs:246`, `AT_NewMod.CanRun`).
- "Editor open with a usable mod": `Instance != null && Instance.CurrentMod != null && Instance.CurrentMod.IsLoaded && !Instance.IsLoading && Instance.State == EditorState.Editing` (the `AT_*.CanRun()` guards, plus `IsLoaded`).
- Secondary: active scene `ModEditorScene` / `ModEditorEmptyScene`; OS window title starting with "ModEditor" [I: weak, set by `SetWindowText`].
- `EditorState` (`EditorState.cs`): `Editing, EnteringPlayMode, PlayMode, ExitingPlayMode`; exposed as `EditorSystem.State` (`:421`). Default is `Editing` (enum 0), so there is no explicit initial transition.

---

## 2. In-memory mod document model

### Static access [C]
`EditorSystem.Instance.CurrentMod` (`EditorSystem.cs:435`, getter over private `m_currentMod`); null when none. `WorkshopController.CurrentActiveMod` (`WorkshopController.cs:46`) returns the playtest mod if set, else falls back to `EditorSystem.Instance.CurrentMod`. Prefer `EditorSystem.CurrentMod` for editing.

### Class hierarchy [C]
`abstract class PummelMod` (`PummelMod.cs:13`) with four subclasses; `PummelMod.ModTypes[]` (index = `PummelModType` int) = `ItemMod`, `BoardMod`, `BoardSpaceMod`, `MinigameMod`. Instantiated reflectively by `PummelMod.CreatePummelModObject(string path, ModLoadType loadType, Type type)` (`:203`). `PummelMod.GetType(string path)` (`:1216`) reads `Data/Meta.json` and maps `ModMetaData.ModType`; falls back to `MinigameMod` when Meta.json is missing or unreadable.

Enums: `PummelModType` (`PummelModType.cs`): `Item=0, Board=1, BoardSpace=2, Minigame=3` (these numbers are what Meta.json stores). `ModLoadType`: `Editor, IngameRuntime, IngameTest, BackgroundInitialize`.

### What a `PummelMod` holds [C]
| Member | Meaning |
|---|---|
| `m_modRootDirectory` (public field) / `GetModPath()` | Mod folder; `GetModPath()` returns `Path.GetFullPath` of it. |
| `protected ModMetaData m_metaData` | Serializable: `MetaDataVersion`, `ModVersion`, `ModCreatedVersion`, `Name`, `ModType`, `LastModified` (ISO string), `SelectedLightingPreset`, `SelectedPostProcessingPreset`, `IsUnlocked`. Accessors: `GetModName()`, `GetVersion()`, `GetDateTimeLastModified()`, `IsUnlockedForEditing()`, `GetSelected*Preset()`. |
| `ModWorkshopItem workshopItem` | title, description, tags, visibility, preview files, `publishedFileId` (256 = `ModWorkshopItem.DefaultWorkshopID` = never published). Saved to `Data/WorkshopItem.json`. |
| `ModAssetList Assets` | All assets: `PummelPrefab` (.pfab), `PummelMaterial` (.pmat), `PummelVisualScript` (.pgf), textures, audio, models. Helpers: `Assets.assets`, `Assets.AssetCount`, `Assets.GetAllAssetsOfType<T>()`, `PummelMod.GetAllVisualScripts()`, `AddAsset(path, folder, copy)`, `RemoveAsset`. |
| `lighting` / `CustomLighting` (`ModLightingSettings`), `postProcess` / `CustomPostProcess` (`ModPostProcessSettings`) | Lighting and screen effects. |
| `abstract ModSettings GetModSettings()` | Per-type settings (`MinigameModSettings`, `BoardModSettings`, `BoardSpaceModSettings`, `ItemModSettings`); base `ModSettings` has a `Version` byte and ImGui widget helpers. Saved to `Data/ModSettings.json`. |
| `lastResult` (`ModLoadResult`), `lastException`, `IsLoaded`, `loadType`, `IsEditor`, `IsRunTime`, `IsBackgroundInit` | Load status / mode. |
| Folder helpers | `FullDataFolder`, `FullAssetsFolder`, `FullTexturesFolder`, `FullMaterialsFolder`, `FullVisualScriptFolder`, `FullPrefabsFolder`, `ModSettingsFile`. |

### Root objects (scene graph) [C]
The mod's scene is NOT held by `PummelMod`; it lives under editor-owned transforms: `EditorSystem.Instance.m_hierarchyRoot` (main scene objects, public field) and `m_prefabsRoot` (prefab objects). `PummelMod.GetPrefabRoot()` returns `m_prefabsRoot` in editor mode. While editing a prefab, `EditorSystem.CurrentPrefab` (`PummelPrefab`) is non-null (`IsEditingPrefab`) and the setter swaps which root is active. Selection: `GetSelectionManager()`. Data lives in `ModTransform` / `ModBehaviour` components (covered by other recon docs).

### Per-type differences [C]
| Type | Class | `FolderName` | `SceneRootName` | UsesHierarchy | UsesLighting / PostProcessing | Type-specific data | Playtest scene |
|---|---|---|---|---|---|---|---|
| Minigame | `MinigameMod` | "Minigames" | "MinigameRoot" | yes | yes / yes | `MinigameDefinition` (`Data/MinigameDefinitionData.json`); `MinigameModSettings` (`SimpleMinigameSettings`, `SimpleModPlayerSettings`, custom controller/player prefabs) | `WorkshopMinigame_Scene` |
| Board | `BoardMod` | "Boards" | "BoardWorld" | yes | yes / yes | `MapDetails` (`Data/MapDetails.json`; `mapExtents` recomputed from `BoardNode`s on save unless `UseCustomBoardSize`); `BoardModSettings` (`CameraSettings`, `CustomSpaceSettings`, `UseCustomBoardSize`) | `WorkshopMap_Scene` |
| BoardSpace | `BoardSpaceMod` | "BoardSpaces" | "" | **no** | yes / yes (base defaults) | only `BoardSpaceModSettings` | none (`OnEditorPlay` not overridden) |
| Item | `ItemMod` | "Items" | "" | **no** | **no** / **no** | `ItemDetails` (`Data/ItemDetails.json`); `ItemModSettings` | `PirateMap_Scene` |

Observations [C]:
- `ItemMod.LoadModSpecificData` always replaces `m_modSettings` with a fresh `ItemModSettings`, even after reading `ModSettings.json` successfully. Item settings from that file do not survive a load; the real item data is `ItemDetails.json`.
- `MinigameMod.FinishTemplateSetup` renames template scripts/prefabs "ModPlayer"/"ModController" to `<ModName>Player` / `<ModName>Controller`. Nothing in this assembly calls any `FinishTemplateSetup` [U: dead code, or called by reflection]. Template creation instead renames through `CreateNewMod`'s load-finished hook (section 3).
- The Workshop publish menu item is enabled only for Board and Minigame mods (`MainMenuWidget.cs`, `enabled` local). Publishing is out of scope for the plugin.

---

## 3. New / Open / Close / Rename

### The `AT_*` classes [C]
`AT_*` = **A**utomated **T**est steps implementing `IAutomatedTest` (`Run()` coroutine, `CanRun()`, `GetName()`, `Reset()`), run by `AutomatedTestSystem` (File > "Automated Tests", `AutomatedTestPopup`). `AutomatedTestSystem.IsEnabled` is true only in the Unity Editor or a `Debug.isDebugBuild`; in a release build the menu entry is hidden.
They are smoke/fuzz steps with RANDOM targets (random name from `AutomatedTestSystem.GetRandomName()`, random template, random existing mod) and no parameters, so they are NOT usable as programmatic entry points. Their value is as evidence that the public `EditorSystem` methods below are meant to be called directly:

| Class | What it calls |
|---|---|
| `AT_NewMod` | `EditorPopup.IsValidModnameInput`, `EditorSystem.Instance.CreateNewMod(path, type, templatePath)`, then `GetAvailableModManager().AddAvailableMod(...)`; waits on `IsLoading`. Templates read from `StreamingAssets/WorkshopTemplates/*/*` (each with `Data/Meta.json`). |
| `AT_OpenMod` | `EditorSystem.Instance.OpenMod(fullPath)` on a random available mod; waits on `IsLoading`. |
| `AT_SaveMod` | `EditorSystem.Instance.SaveCurrentMod()` |
| `AT_RenameMod` | `EditorSystem.Instance.RenameMod(CurrentMod.GetModPath(), randomName)` |
| `AT_OpenModEditor` | `MainMenuUIController.DoButtonEvent(GoModEditorScene)` |
| `AT_CloseModEditor` | `ExitModEditor(ignoreUnsavedChanges: true)` + `CloseTopPopup()` loop |
| `AT_AddGameObjects`, `AT_AddRandomProp`, `AT_SelectAsset` | scene-editing helpers (out of scope; `EditorSystem.AddGameObject(ObjectType, Transform)` is the relevant call) |

### New mod [C]
- UI: File > New Mod (`MainMenuWidget.cs:45`) opens `NewModPopup("New Mod")`. It lists built-in templates (`StreamingAssets/WorkshopTemplates`), unlocked/subscribed Workshop templates (Steam UGC), and two "Empty" options (minigame, board). On OK (`NewModPopup.cs` ~150-190): `CloseAllPopups()`, `ModUnsavedChangesPopup` first if the current mod is dirty, then private `CreateNewMod` which does `Path.Combine(EditorSystem.ModFolder, name)` -> `EditorSystem.CreateNewMod(...)` -> `GetAvailableModManager().AddAvailableMod(new EditorAvailableMod(name, name, type, DateTime.Now))`.
- Name validation lives only in the UI: `EditorPopup.IsValidModnameInput(string)` (`EditorPopup.cs:66`) = `IsValidFilenameInput(name)` AND not `DoesModWithNameAlreadyExist` (case-insensitive vs. cached Meta.json names). **`CreateNewMod` itself neither validates the name nor checks that the folder already exists**; the template branch would merge-copy into an existing folder.
- `EditorSystem.CreateNewMod(string path, PummelModType modType, string templatePath = null)` (`:823`):
  - Template branch: `DirectoryUtility.DirectoryCopy(templatePath, path, copySubDirs: true, skipping ".meta")` (disk write: full template copy), builds the mod via `GetType(path)`, `LoadModIntoEditor(pm)` (async), and on load finish sets `workshopItem.publishedFileId = 256` and, on success, `pm.RenameMod(folderName)` (rewrites Meta.json, WorkshopItem.json, details; re-imports assets).
  - Empty branch: builds `PummelMod.ModTypes[(int)modType]`, `LoadModIntoEditor(pm, isEmpty: true)`: `pm.LoadEmptyMod()` creates the folder tree (`""`, `Data/`, `Assets/`, `Assets/Textures/`, `Assets/Materials/`, `Assets/Prefabs/`) and `SaveCurrentMod(delayed: true)` performs a full save a few frames later.
  - UI coupling: none required, but a `LoadingBarPopup` is pushed. The caller must also call `AddAvailableMod(...)` to keep the in-memory list in sync (popup and `AT_NewMod` both do). No unsaved-changes check: the previous mod is closed and its unsaved edits are discarded.

### Open mod [C]
- UI: File > Open Mod -> `LoadModPopup("Open Mod")` (`LoadModPopup.cs`; does its own fresh scan of `ModFolder`: Meta.json, WorkshopItem.json, Preview.jpg) -> private `LoadModIntoEditor(index)` (`:76`) -> `EditorSystem.OpenModWithPopupPrompt(path)`. File > Recent Mods also uses `OpenModWithPopupPrompt`.
- `EditorSystem.OpenModWithPopupPrompt(string modPath)` (`:848`): if `CurrentMod != null && IsModDirty`, pushes `ModUnsavedChangesPopup` (Save / Don't Save / Cancel; "Save" calls `SaveCurrentMod()` then continues); otherwise opens directly. **UI-coupled when dirty.**
- `EditorSystem.OpenMod(string path)` (`:863`): `CreatePummelModObject(path, ModLoadType.Editor, GetType(path))` then `LoadModIntoEditor(pm)`. `path` is an absolute mod folder. Directly callable; silently discards unsaved edits of the previous mod.
- `EditorSystem.LoadModIntoEditor(PummelMod pm, bool isEmpty = false)` (`:869`): `m_isLoading = true`, clears undo stacks and selection, pushes `LoadingBarPopup`, `SetCurrentMod(pm)` (closes old mod, sets title, `ModBehaviourMonitor.active`, fires `OnBeforeLoadNewMod`, clears undo), then `pm.LoadModAsync()`. `LoadModAsync` starts a **background thread** running `LoadModInternal`; stages: workshop item, meta, assets (`RefreshImportedFiles`), lighting, post-processing, scene (`LoadScene(m_hierarchyRoot)` only if `UsesHierarchy`), mod-specific data (marshalled to the main thread), asset preview rendering. `PostLoadCleanup` then runs on the main thread -> `Cleanup()` sets `IsLoaded` and fires `OnFinishedLoading(lastResult)` -> `m_isLoading = false`.
- Completion polling: `EditorSystem.IsLoading`, then `CurrentMod`, `CurrentMod.IsLoaded`, `CurrentMod.lastResult` / `lastException`. On failure `PostLoadCleanup` shows `ShowLoadFailedPopup` and calls `SetCurrentMod(null)`, so `CurrentMod` becomes null. `ModLoadResult` (`ModLoadResult.cs`): `Success, Failed, ModMissing, ...Exception, ...FileMissing, ScriptCompilationFailure, MissingMinigameController`, etc.
- **Disk side effects of OPEN** [C]: `RefreshImportedFiles` -> `PummelAssetImporter.TryCreateOrLoadMetaAsset` (`PummelAssetImporter.cs` ~118-128) writes a `.pmeta` next to any asset file that lacks one. `UpdateModVersion` only changes in-memory `ModVersion`. Opening does not rewrite Meta.json or the scene.
- If `Data/Meta.json` is missing, `GetType` logs an error and defaults to `MinigameMod`.

### Close mod [C]
- No dedicated method. File > Close Mod (`MainMenuWidget.cs` ~79-93): if dirty, `ModUnsavedChangesPopup` with continue = `EditorSystem.Instance.SetCurrentMod(null)`; else `SetCurrentMod(null)` directly.
- `EditorSystem.SetCurrentMod(PummelMod newMod)` (`:772`): `CurrentPrefab = null` (the setter saves the prefab being left, see section 7), `m_currentMod.Close()` (destroys children of hierarchy/prefab roots, releases assets, GC), updates title, `ModBehaviourMonitor.active`, fires `OnBeforeLoadNewMod(newMod)` (also with null), clears all undo stacks. **No write of the mod itself.**

### Rename mod [C]
- UI: File > Rename Mod (`MainMenuWidget.cs:102`) -> `RenameModPopup(title, modPath)` -> OK -> `EditorSystem.RenameMod(modPath, newName)`.
- `EditorSystem.RenameMod(string modPath, string newName)` (`:1172`): sets `CurrentPrefab = null` if a mod is open; if the target folder already exists it pushes a Yes/No popup and does nothing else; otherwise **`Directory.Move(old, new)`**, then for the current mod `CurrentMod.RenameMod(newName)` (updates `m_metaData.Name`, `m_modRootDirectory`, `workshopItem.title`; rewrites Meta.json + WorkshopItem.json; `RefreshImportedFiles()`; board/minigame also rewrite MapDetails.json / MinigameDefinitionData.json), or for any other mod the static `PummelMod.RenameUnloadedMod(newFolder, newName)` (`PummelMod.cs:254`; rewrites Meta.json, WorkshopItem.json, map/minigame details). Finally `GetAvailableModManager().ReadListOfAvailableMods(ModFolder)`. Exceptions produce popups.
- Rename of an open mod is not a full save (scene/assets are not rewritten) but does bump `LastModified`.
- Folder name == mod name, so rename moves the folder. **Implication for the `PB_TEST_` guard: validate both the source and the NEW name (otherwise a sandbox mod could be renamed out of the sandbox, or something renamed into it).** `RenameMod` does no name validation of its own.

### Delete mod (for completeness) [C]
Exists only inside UI handlers (`MainMenuWidget.DeleteModResult` `:283`, `LoadModPopup.DeleteModResult` `:211`): `SetCurrentMod(null)` if it is the open mod, then recursive `Directory.Delete` of the mod folder. There is no callable `EditorSystem.DeleteMod`. The plugin should not implement it.

---

## 4. Where the list of local mods comes from

- Root: `EditorSystem.ModFolder = Path.Combine(Application.persistentDataPath, "WorkshopMods")` (static string, assigned in `Init`, `EditorSystem.cs:1140`); created if missing. On this machine `persistentDataPath` is `...\AppData\LocalLow\Rebuilt Games\Pummel Party\` per `docs/paths.md` [I: Unity's company/product convention; not derivable from the decompiled C#].
- Scan: `EditorAvailableModManager.ReadListOfAvailableMods(string modFolder)`: `Directory.GetDirectories(modFolder)` (top level only); a folder is accepted only if `Data/Meta.json` exists and deserializes (`ModDataReaderWriter.LoadObjectFromJson<ModMetaData>`). Entry = `EditorAvailableMod(ModName = Meta.Name, ModFolderName = the full directory path returned by GetDirectories, ModType = Meta.ModType, ModLastModified = parsed LastModified)`. Despite its name, `ModFolderName` holds a full path after a scan, but a bare folder name for entries added via `AddAvailableMod` from the popup/`AT_NewMod`; both work because `Path.Combine(ModFolder, absolutePath)` returns the absolute path.
- Refresh timing: only at `Init` and at the end of `RenameMod`. NOT refreshed when folders appear/disappear externally. `LoadModPopup` re-scans on open. A plugin `list_mods` should either call `ReadListOfAvailableMods(ModFolder)` (read-only) or do its own `Directory.GetDirectories` + Meta.json read.
- API: `GetAvailableMods()`, `GetRecentMods()` (8 most recent via `LastEditedModSorter`), `GetAvailableModCount()`, `DoesModWithNameAlreadyExist(name)`, `AddAvailableMod(EditorAvailableMod)`, `UpdateLoadedModLastModifiedTime(PummelMod)`.
- Other locations: built-in templates `Application.streamingAssetsPath/WorkshopTemplates/<group>/<template>/` (read-only); custom lighting profiles `persistentDataPath/ModEditorCustomLightingProfiles/` (written only by an explicit button, `LightingWidgetLogic.cs:230`); ImGui layout `persistentDataPath/LastUsedLayout.ini` (written at editor `Init`, via `InitImGuiLayout` `:1222`, and in `OnDestroy`).

On-disk mod layout [C, from folder constants and the Save methods]:
```
<ModFolder>/<ModName>/
  Data/Meta.json            (ModMetaData)
  Data/WorkshopItem.json    (ModWorkshopItem)
  Data/ModSettings.json     (per-type ModSettings subclass)
  Data/Lighting.json, Data/PostProcessing.data   (if UsesLighting / UsesPostProcessing)
  Data/MainScene.scene      (if UsesHierarchy; binary)
  Data/MapDetails.json | MinigameDefinitionData.json | ItemDetails.json   (per type)
  Data/Preview.jpg          (rewritten on every SaveCurrentMod)
  Assets/{Textures,Materials,Prefabs,VisualScripts,...}   (+ <asset>.pmeta beside each asset)
```

---

## 5. Save: call chain

Triggers [C]:
- **Ctrl+S**: polled in `EditorSystem.Update` (`:1676`): left Ctrl held + `S` pressed -> `SaveCurrentMod()`. `Update` returns before this line while in `PlayMode` (section 8).
- File > Save Mod (`MainMenuWidget.cs:94-98`); refused with a log message while state is Entering/Playing/Exiting.
- `ModUnsavedChangesPopup` "Save" button.
- Programmatic: `AT_SaveMod`.

Chain:
```
EditorSystem.SaveCurrentMod(bool delayed = false)                 EditorSystem.cs:808
  delayed ? CurrentMod.DelayedSave() : CurrentMod.Save()
  EditorAvailableModManager.UpdateLoadedModLastModifiedTime(CurrentMod)    (in-memory list only)
  EditorSystem.SavePreviewScreenshot()     -> Data/Preview.jpg (640x360 JPG rendered from the scene-view camera)
  GetUndoManager().SetModAsNotDirty()
PummelMod.Save()  (virtual, returns bool)                         PummelMod.cs:567
  SaveMetaData()             -> Data/Meta.json (stamps LastModified = now)
  SaveWorkshopItemDetails()  -> Data/WorkshopItem.json
  if UsesLighting:        Data/Lighting.json (CustomLighting)
  if UsesPostProcessing:  Data/PostProcessing.data
  if UsesHierarchy:       Data/MainScene.scene (ModDataReaderWriter.SaveScene from m_hierarchyRoot)
  SaveModSpecificData()  (virtual)
     Minigame:   MinigameDefinitionData.json + ModSettings.json  (also GenerateMinigameControls())
     Board:      MapDetails.json (recomputes mapExtents) + ModSettings.json
     BoardSpace: ModSettings.json
     Item:       ItemDetails.json + ModSettings.json
  for each ModAssetExternal in Assets (unless HideAndDontSave): asset.Save()
     ModAssetExternal.Save -> <asset>.pmeta
     PummelPrefab.Save -> .pfab (binary) + pmeta;  PummelVisualScript.Save -> .pgf (JSON) + pmeta;  PummelMaterial.Save -> .pmat + pmeta
```
- `delayed: true` -> `PummelMod.DelayedSave()` (`:1181`): `GameManager.RunOnMainThread` + `GameManager.DoCoroutine(DelayedSaveRoutine())`, which waits a frame and until `IsLoaded`, then `Save()`. Used for new empty mods. The screenshot and `SetModAsNotDirty` still run immediately in the same call.
- `SaveCurrentMod` has **no guards** (no state, null or `IsLoaded` check). The plugin must check `CurrentMod != null && CurrentMod.IsLoaded && !IsLoading && State == Editing`. Rationale: `SavePreviewScreenshot` renders `m_sceneViewCamera`, which is deactivated in play mode, and `Save()` would serialize a half-loaded hierarchy [I].
- Dirty-scope gotcha [C code / I consequence]: `SetModAsNotDirty` clears only the CURRENT undo context (`GetUndoManager()` returns the per-prefab manager while `CurrentPrefab != null`). Saving while a prefab is open leaves the hierarchy manager dirty. Set `CurrentPrefab = null` first (which itself saves that prefab).
- Assets also write themselves on creation: `CreateNewPrefab` (`:2411`), `CreateNewMaterial` (`:2425`), `CreateNewVisualScript` (`:2445`; the `PummelVisualScript` constructor also saves), `ReimportAsset` (`:908`). These are independent of the dirty flag.

---

## 6. Dirty-state tracking

- Public: `EditorSystem.IsModDirty` (`EditorSystem.cs:407`) => `GetUndoManager().IsModDirty()`.
- Storage: private bool `EditorUndo.m_isModDirty` (`EditorUndo.cs:14`). `EditorUndo` instances: `m_heirarchyUndoManager` (main hierarchy) plus one per `PummelPrefab` in `m_undoManagerList`, created lazily by `GetUndoManager()` (`EditorSystem.cs:729`) according to `CurrentPrefab`.
- **Set** (only place): `EditorUndo.PushUndoEventToStack` (`EditorUndo.cs:58`). Callers: `EditorSystem.DoUndoSerialize` (`:1793`, run from `Update` whenever mouse button 0 is up and the snapshot differs), `Duplicate` (`:2033`), `Delete` (`:2067`), `HierarchyWidget` reparent/move (`HierarchyWidget.cs:531/546/666/689`).
- **Cleared**: `EditorUndo.SetModAsNotDirty()` (only from `SaveCurrentMod`, `EditorSystem.cs:820`) and `EditorUndo.ClearUndoStack()` (`EditorUndo.cs:159`; called from `LoadModIntoEditor` and `SetCurrentMod`).
- Consumers: `ToolbarWidget.cs:246` ("| Unsaved Changes" text), `MainMenuWidget.cs:81` (Close Mod), `NewModPopup.cs:161`, `OpenModWithPopupPrompt` (`EditorSystem.cs:850`), `ExitModEditor` (`:1532`).
- No `IsDirty` on `PummelMod` or `ModAsset`, and no change event [C].
- **Not tracked** [I, from the call-site list]: edits to `ModSettings`, lighting/post-processing, workshop item fields, and asset-only edits (materials, visual scripts, textures) never call `PushUndoEventToStack`. The flag means "a scene-graph/prefab-graph undo event was pushed since the last save/load".
- **Probably true right after any load** [I, needs a runtime check]: `Update` calls `DoUndoSerialize` every frame when the mouse is not held; with an empty stack (fresh load clears it) it pushes the initial snapshot, which sets the flag. So an untouched, freshly opened mod likely reads `IsModDirty == true` after the first frame. Consequences: the plugin must not treat `IsModDirty` as proof the user edited something, and `OpenModWithPopupPrompt` on a just-opened mod will probably show the unsaved-changes popup (`OpenMod` / `CreateNewMod` never do).
- For a reliable signal the plugin should keep its own "mutated since save" flag around its write tools, and/or use `GetUndoManager().UndoStackLength()` (`EditorUndo.cs:170`).

---

## 7. Autosave audit (safety-critical)

Searched the whole `Assembly-CSharp` tree for: `SaveCurrentMod`, `DelayedSave`, `.Save(`, `AutoSave`/`autosave`, `InvokeRepeating`, `OnApplicationFocus`, `OnApplicationPause`, `OnApplicationQuit`, `ModDataReaderWriter.Save*`, `SaveMetaData`, `SaveScene`, `WriteAll*`, and read every `Update` / `LateUpdate` / coroutine in `EditorSystem`.

**Result [C]: no periodic autosave, no focus-loss save, no save on application quit, no save on editor exit, no save on mod close/switch.**
- `EditorSystem.OnApplicationQuit` is empty (`:1281`). `OnApplicationFocus` exists only in `BoardCamera`, `GameManagerObj`, `GameUIController` and the Rewired input module (unrelated to mod saving).
- `InvokeRepeating` appears only in `DestroyWhenOrphaned.cs` and `ModDestroyedObjectGarbageCollector.cs`; neither references saving or files.
- The editor's own timers are the reflection-probe refresh (render only) and the undo-snapshot debounce (in-memory).

**Saves that occur without the user pressing Save [C]:**

| # | Trigger | Location | What is written |
|---|---|---|---|
| 1 | **Entering playtest** (`SetEditorState(EnteringPlayMode)`) | `EditorSystem.cs:1875` | `CurrentMod.Save()`: full mod save (all files in section 5) but NOT the preview screenshot, and the dirty flag is not cleared. This is how the playtest (a second mod instance loaded from disk) sees current edits. |
| 2 | New EMPTY mod | `EditorSystem.cs:889` via `LoadModIntoEditor(isEmpty: true)` | `SaveCurrentMod(delayed: true)`: folder tree + full save + preview screenshot. |
| 3 | New TEMPLATE mod | `EditorSystem.cs:823` and its hook | template `DirectoryCopy`, then on load `pm.RenameMod(...)` (Meta/WorkshopItem/details rewrite, `.pmeta` creation). |
| 4 | **Leaving a prefab-editing context** | `EditorSystem.CurrentPrefab` setter (`:462`) | `m_currentPrefab.Save()` (.pfab + .pmeta) whenever `CurrentPrefab` changes away from a non-null prefab. Also reached via `SetCurrentMod`, `RenameMod`, Validate, Publish menu, Play button. So `OpenMod`, `CreateNewMod`, `SetCurrentMod(null)`, `RenameMod`, and `SetEditorState(EnteringPlayMode)` while a prefab is being edited will write that prefab. The setter returns early when the value is unchanged. |
| 5 | Creating prefab / material / visual script | `EditorSystem.cs:2411/2425/2445`; `PummelVisualScript` ctor | new asset file + .pmeta written immediately. |
| 6 | Importing assets / screenshots | `PummelMod.AddAsset` (`:898`) copies the file into `Assets/<folder>` and writes .pmeta; F10 / toolbar Screenshot writes `Assets/Textures/ScreenshotN.jpg` (`TakePlayModeScreenshot`, `EditorSystem.cs:1437`) | files in the mod folder |
| 7 | Opening any mod | `PummelAssetImporter.TryCreateOrLoadMetaAsset` | creates missing `.pmeta` files |
| 8 | Rename | `EditorSystem.RenameMod` | folder move + Meta/WorkshopItem/details rewrite |
| 9 | Unsaved-changes popup "Save" button | `ModUnsavedChangesPopup.cs` | `SaveCurrentMod()` then continue (user-chosen) |
| 10 | Workshop publish | `WorkshopPopup.PublishMod` (`WorkshopPopup.cs:460`) calls `pm.Save()`; `WorkshopModInstaller.OnCreateItem` (`:1042`) calls `DelayedSave()` | **Never implement or call (project hard rule 5).** |
| 11 | Editor init/destroy | `ImGui.SaveIniSettingsToDisk` | layout `.ini` in persistentDataPath (not in a mod folder) |
| 12 | PlayerPrefs | `CurrentUIZoomPercent` setter, player-count combo | Unity PlayerPrefs, not mod data |
| 13 | Exit-time reinstall thread | `RunExitModEditorLogic` -> `WorkshopModInstaller.ReinstallMod(id)` | only for published mods whose playtest ran (`AddModToReinstallOnExit` when `publishedFileId != 256`); PB_TEST_ mods are unpublished so unaffected [I] |

Implications for "plugin writes only to PB_TEST_ mods":
- #1 and #4 are the dangerous implicit ones. **Starting a playtest saves the open mod, and leaving a prefab saves that prefab.** If a protected mod is open, a plugin-triggered playtest WILL overwrite its files (at minimum Meta.json `LastModified`). Playtest and every write tool must verify the folder name of `CurrentMod.GetModPath()` starts with `PB_TEST_`.
- Opening a protected mod is not strictly read-only (#7 can create `.pmeta`), and any later implicit #1/#4 would write to it. Policy suggestion (not from code): the plugin should refuse to open non-`PB_TEST_` mods, or treat them as read-only and never call `SetEditorState(EnteringPlayMode)` or change `CurrentPrefab` while one is open.
- After `RenameMod` the folder leaf changes; always re-derive identity from `GetModPath()`, not from a cached name.

---

## 8. Playtest (play mode)

### Start [C]
- UI: Play button in `ToolbarWidget` (`ToolbarWidget.cs` ~130-150). Disabled when no mod, when state is not Editing and the game is not paused, or `IsDoingBackgroundModLoad()` (Workshop reinstall in progress). The click handler does `CurrentPrefab = null` (saves prefab), `ValidateCurrentMod()`; if valid (or already in PlayMode) and state is Editing -> `SetEditorState(EditorState.EnteringPlayMode)`; if already playing it just resumes `Time.timeScale = 1`; if invalid it shows `ModValidationPopup(result, runAnywayButton: true)`, whose "run anyway" also calls `SetEditorState(EnteringPlayMode)` (`ModValidationPopup.cs:74`).
- Programmatic: `EditorSystem.SetEditorState(EditorState newState)` (`:1838`, public; returns early if unchanged). For `EnteringPlayMode` it: sets `m_curState` first, sets `GameManager.partyGameMode = BoardGame`, nulls `GameManager.CurMap` for minigames, calls **`CurrentMod.Save()`**, then, if `WorkshopController.CurrentBackgroundLoadMod == null`, calls `WorkshopController.Instance.AddModInBackground(modPath)` (loads a second `PummelMod` with `ModLoadType.BackgroundInitialize`: workshop item, meta, mod-specific data, preview; registers it with `GameManager` via `RuntimeAddToGameManager`). On `Success`: `FocusSceneView()`, `CurrentMod.OnEditorPlay()` (opens the type's playtest scene), `SetActive(false)` (stashes active flags, deactivates editor camera/roots/post-process/gizmos). On failure: reverts `m_curState = Editing` and shows `ShowLoadFailedPopup`.
- **Stuck-state hazard [C]:** if `CurrentBackgroundLoadMod != null` on entry, nothing happens but `m_curState` stays `EnteringPlayMode`. Check it is null first. `SetEditorState` does not run `ValidateCurrentMod()`; validation exists only in the toolbar handler.
- `BoardSpaceMod` does not override `OnEditorPlay`, so a playtest request would hide the editor without opening a scene [I; see open questions]. Do not start playtests for BoardSpace mods.
- Transitions: `EnteringPlayMode` -> the playtest scene loads and `SceneManager_sceneLoaded` / `GameBoardController` call `SetEditorState(PlayMode)`; `PlayMode` enables `m_playModeUICamera` and resizes the scene view. Pause = `Time.timeScale = 0`, Play again = `1`.
- Per frame in PlayMode, `EditorSystem.Update` returns early after `UpdatePlayMode()` (retargets all game cameras to the scene-view render-texture rect; moves canvases onto `m_playModeUICamera`). **Widget `Update`, Ctrl+S, the Esc handler, undo snapshots and all hotkeys are skipped.** ImGui `Draw()` still runs.

### Stop [C]
- UI: Stop button (`ToolbarWidget.cs` ~177-185): `Time.timeScale = 1`, `GameManager.disconnectUserEvent = LeftGame`, `NetSystem.OnDisconnect(NetworkDisconnectReason.DisconnectReasonLeft)`, which reaches `GameNetworkManager.cs:143` -> `EditorSystem.SetEditorState(ExitingPlayMode)` when the editor has a mod. A finished board/minigame does the same from `GameBoardController.cs:2295`.
- `ExitingPlayMode` handler: `FocusSceneView()`, `GameManager.OpenScene("ModEditorEmptyScene")`, `WorkshopController.Instance.RemoveMod(CurrentMod)` (drops the runtime copy), records the published ID for reinstall-on-exit if not 256. When the empty scene loads, `State` becomes `Editing` -> `SetActive(true)`: restores stashed active flags, shows a load-failed popup if the background copy failed, `CurrentMod.LoadLightingSettings()`, `ModBehaviourMonitor.UpdateAllBehaviours` on both roots.
- Programmatic stop: `SetEditorState(ExitingPlayMode)` is public, but the clean path replicates the Stop button (timescale + disconnect). [U: calling it directly skips the network disconnect teardown.]

### What is saved / restored [C]
- Saved: one full `CurrentMod.Save()` at entry (no dirty-flag change, no preview screenshot).
- Restored: the editor hierarchy is NOT reloaded from disk after the playtest; it persists as deactivated `DontDestroyOnLoad` objects. Undo stacks are untouched (the playtest does not change the dirty flag). The playtest itself runs from a second mod instance loaded from the just-saved files.
- Nothing is written after entry unless something triggers the editor screenshot (F10 -> `Assets/Textures/ScreenshotN.jpg`).

---

## 9. Threading, loop, UI framework

- **Dear ImGui** through a native plugin: the `ImGui` static class P/Invokes `ModEditorPlugin_Release` (`ImGui.cs:17,29,...`). Input is fed manually from `UnityEngine.Input` (`EditorSystem.Draw` sets mouse/viewport; `PollKeyInput` forwards `Input.inputString` and key states). Not uGUI. Rendering goes through `GL.IssuePluginEvent(ImGui.GetRenderEventFunc(), 12345)`.
- **Draw loop**: coroutine `CallPluginAtEndOfFrames` (`EditorSystem.cs:2085`): `yield return new WaitForEndOfFrame()` -> `Draw()` (`:2106`) -> optional `TakePlayModeScreenshot` -> `ReleaseAllTextureShaderResourceViews()`. `Draw()` skips frames where `Time.unscaledDeltaTime` is ~0, calls `ImGui.NewFrame()`, then `widget.Draw()` for every open `EditorWidget`, then `EditorPopupManager.Draw()`, then `ImGui.Render()`. **All ImGui click handlers (menu items, popup buttons) therefore run at end-of-frame inside this coroutine, not in `Update`.**
- **Per-frame logic**: `EditorSystem.Update()` (`:1572`): reflection probe, F10 flag, Ctrl+/- UI zoom, early return in PlayMode, caches mouse/modifier state in public bools (`mouseButtonDown`, `leftControlDown`, `deleteClicked`, ...), `CurrentMod.OnPostImport()` (renders item previews), applies pending widget add/remove lists, grid draw, `widget.Update()` for each widget, `EditorPopupManager.Update()` (empty body), undo snapshot (`DoUndoSerialize`), Esc / Ctrl+Z / Ctrl+S, gizmo hotkeys, hierarchy arrow navigation, Ctrl+D/C/V/Delete, `PollKeyInput()`. `LateUpdate` calls `widget.LateUpdate()`.
- **Threads**: mod loading runs on a worker thread (`PummelMod.LoadModAsync` / `LoadModInternal`) and marshals Unity calls through `GameManager.RunOnMainThread(Action)` (`GameManager.cs:823`: runs inline on the main thread, otherwise enqueues to `UnityMainThreadDispatcher` and `Wait()`s). The exit-time Workshop reinstall also runs on a thread.
- **Rules for a plugin calling from `Update()`** [C/I]:
  1. Main thread only. An HTTP listener thread must queue work and run it from `Update()`.
  2. Never call `ImGui.*` yourself outside the game's draw coroutine. `EditorPopupManager.AddPopup` is safe (just a stack push drawn later).
  3. After `OpenMod` / `CreateNewMod`, poll `IsLoading` across frames (then `CurrentMod`, `IsLoaded`, `lastResult`). Never block the main thread waiting: load stages call back to the main thread and would deadlock.
  4. Do not call lifecycle methods while `IsLoading` or when `State != Editing`.
  5. Check `EditorPopupManager.CurrentPopup == null`; pending popups (load failed, rename conflict, unsaved changes) are how errors surface and they block user UI. Dismiss with static `CloseTopPopup()` / `CloseAllPopups()`.
  6. ImGui-click-driven flows need real rendered frames (zero-delta frames are skipped).
  7. Re-check `EditorSystem.Instance != null` before every call (it is nulled in `OnDestroy`).

---

## 10. Keyboard shortcuts / command dispatch

- **No command system exists** [C]. `Command.cs` is an empty class. `ShowWindowCommand.cs` is the Win32 `ShowWindow` enum (`SW_HIDE`, `SW_MAXIMIZE`, `SW_MINIMIZE`, `SW_RESTORE`, `SW_FORCEMINIMIZE`) used only by `Win32Wrapper`. `InputActions.cs` is a static list of Rewired action ids for in-game input (`UISubmit`, `PauseGame`, ...), not used by the editor.
- Shortcuts are hard-coded polling of `UnityEngine.Input` in `EditorSystem.Update` (left Ctrl only): Ctrl+S save (`:1676`), Ctrl+Z undo (`:1672`), Esc exit prompt (`:1662`; only when no popup open and `State == Editing`), F10 screenshot (`:1575`), Ctrl+= / Ctrl+- UI zoom, Q/W/E/R gizmo operations, Z pivot/center, X local/world (only when scene view/hierarchy/toolbar is focused and the inspector is not), Ctrl+D duplicate, Ctrl+C / Ctrl+V, Delete, arrow keys in the hierarchy. Menu items in `MainMenuWidget` mirror them with "CTRL+S" style hints.
- Synthetic key injection is possible but fragile; calling the `EditorSystem` methods directly is the intended plugin path.

---

## Programmatic entry points

`EditorSystem` below means `EditorSystem.Instance`; all calls main-thread only. "UI-coupled" = completes only through ImGui interaction or may block on a popup.

| Method signature | What it does | Writes to disk? | UI-coupled? | Evidence |
|---|---|---|---|---|
| `static EditorSystem Instance { get; }` | Singleton; null when the editor is closed | no | no | `EditorSystem.cs:383` |
| `PummelMod CurrentMod { get; }` | Open mod (null if none) | no | no | `EditorSystem.cs:435` |
| `bool IsLoading { get; }` | True from `LoadModIntoEditor` until load finishes or fails | no | no | `EditorSystem.cs:405` |
| `EditorState State { get; }` | Editing / EnteringPlayMode / PlayMode / ExitingPlayMode | no | no | `EditorSystem.cs:421`, `EditorState.cs` |
| `bool IsModDirty { get; }` | Dirty flag of the CURRENT undo context only | no | no | `EditorSystem.cs:407`, `EditorUndo.cs:48` |
| `PummelPrefab CurrentPrefab { get; set; }` | Prefab-edit context; set null to return to the main scene | **yes** (saves the prefab being left) | no | `EditorSystem.cs:440-496` |
| `static string ModFolder` | `<persistentDataPath>/WorkshopMods` | no | no | `EditorSystem.cs:201,1140` |
| `EditorAvailableModManager GetAvailableModManager()` | Cached mod list: `GetAvailableMods()`, `DoesModWithNameAlreadyExist(name)`, `ReadListOfAvailableMods(folder)`, `AddAvailableMod(...)` | no (reads Meta.json) | no | `EditorSystem.cs:758`, `EditorAvailableModManager.cs` |
| `void OpenMod(string path)` | Close current mod (no save), build `PummelMod` from folder, async load | maybe: creates missing `.pmeta`; prefab save if `CurrentPrefab` set | LoadingBar popup; errors via popup | `EditorSystem.cs:863`, `PummelMod.cs:364,391` |
| `void OpenModWithPopupPrompt(string modPath)` | As `OpenMod`, but shows unsaved-changes popup first if `IsModDirty` | same (+ full save if user clicks "Save") | **yes** when dirty | `EditorSystem.cs:848`, `ModUnsavedChangesPopup.cs` |
| `void CreateNewMod(string path, PummelModType modType, string templatePath = null)` | New mod: empty = folder tree + delayed full save; template = copy + rename on load | **yes** (creates folder and files) | LoadingBar popup; no name/exists validation | `EditorSystem.cs:823` |
| `void SaveCurrentMod(bool delayed = false)` | Full mod save + preview screenshot + clears current dirty flag | **yes** (all mod files, section 5) | no | `EditorSystem.cs:808`, `PummelMod.cs:567` |
| `void RenameMod(string modPath, string newName)` | Move folder + rewrite meta/workshop/details; refresh list | **yes** (folder move) | popups on conflict/error | `EditorSystem.cs:1172` |
| `static ModLoadResult PummelMod.RenameUnloadedMod(string newFolderPath, string newName)` | Rewrites Meta/WorkshopItem/details of an on-disk mod after its folder was moved | yes | no | `PummelMod.cs:254` |
| `void SetCurrentMod(PummelMod newMod)` | Closes current mod in memory; `SetCurrentMod(null)` = "Close Mod" | no (but `CurrentPrefab = null` saves an open prefab) | no | `EditorSystem.cs:772` |
| `void ExitModEditor(bool ignoreUnsavedChanges = false)` | Leave to MainMenu; popup if dirty and not ignoring | no (never saves) | **yes** when dirty and flag false | `EditorSystem.cs:1530` |
| `void RunExitModEditorLogic()` | Unconditional exit to MainMenu (destroys editor object) | no (published mods: reinstall thread) | no | `EditorSystem.cs:1545` |
| `void SetEditorState(EditorState newState)` | `EnteringPlayMode` starts a playtest (saves mod first); `ExitingPlayMode` returns to editing | **yes** on `EnteringPlayMode` | no (validation bypassed; hazards in section 8) | `EditorSystem.cs:1838-1897` |
| `ModValidationResult ValidateCurrentMod()` | Runs mod validation (the toolbar does this before playtest) | no | no | `EditorSystem.cs:763` |
| `GameManager.OpenScene("ModEditorScene")` or `GameManager.MainMenuUIController.DoButtonEvent(MainMenuButtonEventType.GoModEditorScene)` | Enter the editor from the main menu | no | async scene transition; needs the main menu | `UIController.cs:1373`, `GameManager.cs:2485` |
| `static EditorPopupManager.CloseTopPopup()` / `CloseAllPopups()` / `CurrentPopup` | Dismiss / inspect modal popups | no | no | `EditorPopupManager.cs` |
| `static bool EditorPopup.IsValidModnameInput(string)` | Filename-safe and not already in the list | no | no | `EditorPopup.cs:66` |
| `Action<PummelMod> OnBeforeLoadNewMod`; `PummelMod.OnFinishedLoading` (`Action<ModLoadResult>`); `OnAddAsset` / `OnRemoveAsset` | Observer hooks for load completion and asset changes (combine delegates) | no | no | `EditorSystem.cs:305-309`, `PummelMod.cs:75` |
| `bool PummelMod.Save()` (virtual) | Raw full save (what `SaveCurrentMod` calls) without screenshot / dirty clear / list update | **yes** | no | `PummelMod.cs:567` |
| `AT_*` classes | Random fuzz steps, not parameterized | n/a | n/a | do not reuse; `AutomatedTestSystem.IsEnabled` is false in release builds |
| `WorkshopPopup.PublishMod`, `WorkshopModInstaller.PublishItem` | Workshop upload | yes | yes | **FORBIDDEN (project hard rule 5)** |

---

## Open questions / unverified

1. Is `IsModDirty` really true right after a fresh load? (Section 6 reasoning: the first `DoUndoSerialize` pushes an initial snapshot onto the empty stack.) Runtime check: read the flag a few frames after `OpenMod` with no edits.
2. `FinishTemplateSetup` (all four mod classes) has no caller in this assembly. Does template creation work without it (renaming "ModPlayer"/"ModController" scripts for minigame templates)? Called by reflection or from another assembly?
3. `SetEditorState(ExitingPlayMode)` called directly versus the Stop button's `NetSystem.OnDisconnect` path: does the direct call leak the playtest session/network state?
4. What happens when Play is used on a BoardSpace mod (`OnEditorPlay` is not overridden; `SceneManager_sceneLoaded` only maps `PirateMap_Scene`)? Possible stuck/hidden editor.
5. Does Ctrl+S in `Update` fire while an ImGui text field has focus? No `WantTextInput` gate was seen. Only relevant for synthetic key injection.
6. Contents of scenes `ModEditorScene` / `ModEditorEmptyScene` (is another `EditorSystem` present in the empty scene; does `Awake` re-run?). Not visible from the C#.
7. Whether asset importers or the `ModBehaviour` load lifecycle write to the mod folder beyond `.pmeta` creation. Only `PummelAssetImporter.TryCreateOrLoadMetaAsset` was found; `ModDataReaderWriter.LoadScene` / `LoadGameObjectFromGUID` were not read for write side effects.
8. `ModEditorGui.cs` (2102 lines) was only grepped: it is a static helper library of ImGui widgets with no lifecycle or save calls found. How inspector edits flow into `ModBehaviour` fields (and whether they touch the undo/dirty system) belongs to another recon topic.
9. `persistentDataPath` was taken from `docs/paths.md` (earlier read-only discovery), not derived from decompiled code.
10. Does `SaveCurrentMod()` with `CurrentPrefab != null` write a consistent `MainScene.scene`? The hierarchy root is deactivated while a prefab is edited and `SaveScene` was not read for active-state handling. Safest is to clear `CurrentPrefab` before saving.
11. `AutomatedTestSystem` behavior in release builds: `IsEnabled` is false, but whether tests can still be constructed and run outside the popup was not traced.
12. Workshop-reinstall thread (`WorkshopModInstaller.ReinstallMod`) was not read; the claim that it touches only published mods is inferred from the `publishedFileId != 256` guard.
