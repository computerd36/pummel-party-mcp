# Pummel Party mod on-disk format (reference)

Source: static reading of the decompiled `Assembly-CSharp` (game build as decompiled in this repo). Nothing here was verified against a live mod file or a running game.
Legend: **[C]** confirmed from code, **[I]** inferred (from attributes / Unity behaviour, not directly visible in code), **[?]** unverified.
Evidence is `File.cs:line` (root folder unless a path is given). Companion doc (code side, load/save/safety): `docs/architecture/02-serialization-and-assets.md`.

## 0. Overview

| Concern | Mechanism |
|---|---|
| Small settings files | Unity `JsonUtility` (NOT Newtonsoft): public/`[SerializeField]` fields only, pretty-printed, UTF-8 text. `ModDataReaderWriter.SaveObjectToJson` / `LoadObjectFromJson` (`ModDataReaderWriter.cs:9-47`) [C] |
| Scene (`MainScene.scene`) and prefabs (`.pfab`) | Custom binary, .NET `BinaryWriter` primitives, **uncompressed**, header string `"PMH"`. `ModHierarchySerializer` [C] |
| Post processing | Custom binary, `.data`, ushort version up front. `ModDataReaderWriter.cs:78-193, 195-500` [C] |
| Asset sidecars | `<assetfile>.pmeta` = JsonUtility JSON of a `ModAssetExternal` subclass (`ModAssetExternal.cs:50-68`, `GameManager.cs:959`) [C] |
| Visual scripts (graphs) | `.pgf` JSON in `Assets\VisualScripts\` (`PummelVisualScript.cs:373-402`) [C] |
| Action Editor logic | Embedded **inside the scene/prefab binary** as JSON strings per property (not separate files) [C] |
| Compression | Only `Extensions.Compress/Decompress` (GZip, `Extensions.cs:23-50`) exists; no mod-file reader/writer in this topic uses it. Workshop upload packaging is Steam's, out of scope [C for no-use in these paths] |
| Endianness / strings | .NET BinaryWriter: little-endian; strings = 7-bit-encoded length prefix + UTF-8; bool = 1 byte; Vector3 etc. are raw floats (see 2.6) |

Folder naming: `<persistentDataPath>\WorkshopMods\<ModFolderName>\` (`EditorSystem.cs:1140`). Constants: `PummelMod.cs:38-58`.

## 1. Files in a mod folder

```
<ModFolder>\
  Data\
    Meta.json                    ModMetaData        (JSON)
    WorkshopItem.json            ModWorkshopItem    (JSON)
    ModSettings.json             <Type>ModSettings  (JSON, shape depends on mod type)
    MinigameDefinitionData.json  (minigames only)   (JSON)
    MapDetails.json              (boards only)      (JSON)
    ItemDetails.json             (item mods only)   (JSON)   [C: ModDataReaderWriter.cs:523-544]
    Lighting.json                ModLightingSettings(JSON)
    PostProcessing.data          custom binary
    MainScene.scene              custom binary "PMH"
    Preview.jpg                  640x360 JPEG screenshot, rewritten on every editor save
  Assets\
    Audio\ Props\ ...            (any subfolder; enumeration is recursive)
    Textures\ Materials\ Prefabs\ VisualScripts\
      <file>                     source asset (.obj .png .jpg .jpeg .wav .mp3 .ogg .pmat .pfab .pgf)
      <file>.pmeta               sidecar JSON, one per asset file
```

| File | Reader | Writer | Missing | Corrupt |
|---|---|---|---|---|
| `Meta.json` | `PummelMod.LoadModMetaData` (`PummelMod.cs:618`), `EditorAvailableModManager.ReadListOfAvailableMods` (`EditorAvailableModManager.cs:14-24`), `PummelMod.GetType(path)` (`:1216`) | `PummelMod.SaveMetaData` (`:689-697`), `RenameUnloadedMod` (`:254`) | load **fails** `MetaDataFileMissing`; mod omitted from the editor's list | `Failed`; mod omitted from list |
| `WorkshopItem.json` | `LoadWorkshopItemDetails` (`:623`) | `SaveWorkshopItemDetails` (`:594`) | load fails `WorkshopFileMissing` | load fails `Failed` |
| `ModSettings.json` | `MinigameMod/BoardMod/ItemMod/BoardSpaceMod.LoadModSpecificData` | `SaveModSpecificData` | defaults, load **succeeds** | defaults (error logged), load succeeds |
| `MinigameDefinitionData.json` | `ModDataReaderWriter.LoadMinigameDefinition` (`:553`) | `SaveMinigameDefinition` (`:546`) | stays at code defaults, succeeds | error logged, defaults, succeeds |
| `MapDetails.json` | `LoadMapDetails` (`:509`) | `SaveMapDetails` (`:502`) | dummy values ("MapName"...), succeeds | same |
| `Lighting.json` | `PummelMod.LoadLightingSettings` (`:607`) via `ModDataReaderWriter.LoadLightingSettings` (`:65`) | `SaveLightingSettings` (`:53`) | defaults, succeeds | defaults, succeeds |
| `PostProcessing.data` | `ModDataReaderWriter.LoadPostProcessSettings` (`:481`) | `SavePostProcessSettings` (`:78`) | defaults, succeeds | unknown version -> defaults silently; truncated -> EndOfStream exception thrown inside a main-thread callback, effect on load result [?] |
| `MainScene.scene` | `PummelMod.LoadSceneInternal` (`:658`) | `ModDataReaderWriter.SaveScene` (`:569`) | load fails `SceneFileMissing` | load fails `SceneException` |
| `Preview.jpg` | not read by editor load (used by "new from template" list, `NewModPopup.cs:~120`) | `EditorSystem.SavePreviewScreenshot` (`EditorSystem.cs:1363`) | n/a | n/a |
| `<asset>.pmeta` | `PummelAssetImporter.TryCreateOrLoadMetaAsset/LoadMetaAsset` (`PummelAssetImporter.cs:106-148`) | `ModAssetExternal.SaveMetaFile` (`ModAssetExternal.cs:56-68`) | **created** (new GUID) | **overwritten with a fresh meta (new GUID)** (`:127-131`) |

### 1.1 `Meta.json` -- `ModMetaData` (`ModMetaData.cs`) [C]

| Key | Type | Notes |
|---|---|---|
| `MetaDataVersion` | int | default 1. Only touched in `PummelMod.UpdateModVersion` (`:461-469`): if `ModVersion<=0` then `ModCreatedVersion=0`, `MetaDataVersion=1` |
| `ModVersion` | int | editor format version, current `PummelMod.VERSION = 1` (`PummelMod.cs:28`). If lower on load it is bumped **in memory** (`:451-454`); written at next save |
| `ModCreatedVersion` | int | set once at creation (=VERSION). Gates OBJ import handedness fix: `>=1` mirrors X and flips winding (`PummelOBJImporter.cs` `UseLeftHandCorrection`) |
| `Name` | string | mod name; see section 8 |
| `ModType` | int | `PummelModType`: 0 Item, 1 Board, 2 BoardSpace, 3 Minigame (`PummelModType.cs`). Used as an index into `PummelMod.ModTypes` (`:30-36`, `:1223`). Values outside 0..3 throw |
| `LastModified` | string | `DateTime.Now.ToString("O")`, set by every `SaveMetaData` |
| `SelectedLightingPreset` / `SelectedPostProcessingPreset` | string | `"Custom"` (or empty) = use the mod's own `Lighting.json`/`PostProcessing.data`; otherwise the name of a preset file `StreamingAssets\LightingPresets\<name>.json` / `PostProcessPresets\<name>.data` (`PummelMod.cs:612, 638`). Default on new mod: `"Default"` (`:175-176`) |
| `IsUnlocked` | bool | "allowed as template / editable by others" (`SetEditLock`) |

Note: `ModUGCMetaData` (`AuthorName`, `AuthorID`, `IsModUnlocked`) is NOT a file in the mod folder; it is JSON pushed into Steam item metadata at upload (`WorkshopModInstaller.cs:1085-1090`) and read back by the Workshop browser (`WorkshopWindowManager.cs:150`). Irrelevant to local editing. [C]

### 1.2 `WorkshopItem.json` -- `ModWorkshopItem` (`ModWorkshopItem.cs`) [C]

Keys: `Version` (byte), `title`, `description`, `language` ("english"), `metaData` (string), `visibility` (Steam enum as int: 0 public, 1 friends-only, 2 private [I on numbering]), `tags` (string list), `isInternal` (bool; forced false on load unless the path contains `InbuiltMods`, `PummelMod.cs:626-629`), `previewFiles` (list of `{type, path, guid}` = `WorkshopPreviewFile`), `publishedFileId` (ulong; **256 = never published** = `DefaultWorkshopID`), `previewImageGuid` (GUID struct, see 2.8). `changeNote` is `[NonSerialized]`.
New mods get tag `"Minigame"` or `"Board"` automatically (`PummelMod.cs:187-195`). `title` is separate from `Meta.json.Name`; both are rewritten on rename. This doc covers file format only; the upload path is deliberately not described.

### 1.3 `ModSettings.json` [C shape / I exact keys]

JsonUtility dump of the mod-type-specific settings class, base `ModSettings` contributes `Version` (byte).
- Minigame: `MinigameModSettings` -> `SimpleMinigameSettings` (`SimpleModControllerSettings`), `SimpleModPlayerSettings` (`MinigameModSettings.cs:8-28`). Two `PummelPrefab` members are `[NonSerialized]`.
- Board: `BoardModSettings` -> `CameraSettings`, `CustomSpaceSettings` (contains `ModAssetReference<PummelPrefab>[] CustomSpacePrefabReferences`), `UseCustomBoardSize` (`BoardModSettings.cs:6-12`).
- Item / BoardSpace: `ItemModSettings` / `BoardSpaceModSettings` (not expanded here).
Asset references inside settings use `ModAssetReference<T>`: JSON object with a `m_assetGUID` (GUID struct) and a `m_asset` field; GUID is refreshed from the live asset in `OnBeforeSerialize` only when the mod is in the editor (`ModSystem.Logic/ModAssetReference.cs`) [C]. What JsonUtility writes for the object-typed `m_asset` is [?].

### 1.4 `MinigameDefinitionData.json` -- `SerializableMinigameDefinition` [C]

`MinigameName`, `Description`, `MinigameNameToken`, `DescriptionToken`, `ScreenshotTextureGuid` (plain **string**, null/"" when unset), `MinPlayers` (def 2), `MaxPlayers` (def 8), `InputHelp` (nested `InputHelp` object). The screenshot GUID names a texture asset in `Assets\Textures`.

### 1.5 `MapDetails.json` -- `SerializableMapDetails` [C]

`MapName`, `SceneName`, `Description`, `MapExtents` (Rect: x,y,width,height), `PreviewImageGuid` (GUID struct), `OverrideSpacePrefabs` (bool), `OverrideSpace` (bool[13]), `SpacePrefabOverrideGuids` (GUID struct[13]), `MusicGuid`, `MusicVol`, `AmbienceGuid`, `AmbienceVol`, `SupportsWeaponCache`, `UseCustomChestShuffleLocation`, `ChestShuffleLocation` (Vector3), `DefaultGoalChestCount`. `MapExtents` is recomputed from `BoardNode`s on save unless `UseCustomBoardSize` (`BoardMod.cs:~75-84`).

### 1.6 `Lighting.json` -- `ModLightingSettings` (`ModLightingSettings.cs:1-~100`) [C]

Public fields (these are the JSON keys; the PascalCase properties are not serialized): `version`(ushort), `presetName`, `stars`, `topColor`, `horizonColor`, `groundColor`, `haloColor`, `haloExponent`, `haloStrength`, `shadowDistance`, `shadowCascades`, `shadowCascade4Split`(Vector3), `shadowProjection`(enum int), `shadowCascade2Split`, `ambientMode`(enum int), `ambientIntensity`, `ambientSkyColor`, `ambientEquatorColor`, `ambientGroundColor`, `fog`, `fogColor`, `fogMode`(enum int), `fogDensity`, `fogStartDistance`, `fogEndDistance`, `defaultReflectionMode`, `reflectionIntensity`, `cloudShadows`, `cloudSpeed1`, `cloudSpeed2`, `cloudStrength`, `cloudScale`, `cloudIntensity`, `globalLightEnabled`, `globalLightRotation`(Vector3 euler), `globalLightColor`, `globalLightIntensity`, `globalShadowItensity` (sic), `globalLightShadows`(enum int). Colors are `{r,g,b,a}` floats (Unity JSON convention) [I]. Custom user presets also live outside the mod: `<persistentDataPath>\ModEditorCustomLightingProfiles\*.json` (`LightingWidgetLogic.cs:48`).

### 1.7 `PostProcessing.data` (binary) [C]

```
ushort  version                 (writer always emits ModPostProcessSettings.VERSION = 1)
string  presetName
-- then, in this fixed order, per effect: bool present; if present: bool enabled + effect fields --
AmbientOcclusion, Bloom, ComputeBloom (v1 only), ChromaticAberration, ColorGrading,
DepthOfField, Grain, LensDistortion, Vignette
```
Field values are written with `WriteParam` helpers (float/bool/Color/Vector2 parameters, `Extensions.cs:772-797`). Version 0 = same without ComputeBloom. Any other version value: file is ignored, defaults returned (`ModDataReaderWriter.cs:489-497`). There is no length framing: a reader/writer mismatch desynchronises everything after it. Editing this file by hand is not advisable; the agent should drive it through the editor UI/API instead.

## 2. `MainScene.scene` (and `.pfab`) binary format [C]

Writer: `ModHierarchySerializer.SaveTransform` (`ModHierarchySerializer.cs:79-87`). `SaveScene` calls it with `ignoreRoot=true` on the editor hierarchy root (`ModDataReaderWriter.cs:569-577`); `.pfab` files call it on the prefab's root object with `ignoreRoot=false` (`PummelPrefab.cs:73-76`). Three sequential sections in a single stream, no section lengths:

```
[A] Header   : string "PMH" ; ushort major(=1) ; ushort minor(=0)
[B] Hierarchy: pre-order depth-first node list (see 2.1)
[C] Components table: one record per GameObject, in the same pre-order (see 2.2)
[D] Values   : one record per component, in the order the components were listed in [C] (see 2.3)
```

### 2.1 Hierarchy section [B] (`SaveHierarchy`, `:89-108`; `LoadHierarchy`, `:198-227`)

```
node(non-root) := string name ; bool activeSelf ; int32 layer ; string tag      (tag: only when version > 0.0)
children       := ushort childCount ; childCount x node
scene file     := children            (root itself is NOT written: ignoreRoot)
prefab file    := node(root) + its children
```
- Parent/child is expressed purely by nesting. No parent ids or indices exist. Max 65535 children per node (ushort).
- Children of a GameObject carrying a component whose `ModSerialized.supportsChildren == false` (e.g. `ModPrefabInstance`) are not written (`EditorSystem.ShouldHideChildGameObjects`, `EditorSystem.cs:971-983`): the instantiated prefab body is regenerated at load (`ModPrefabInstance.UpdatePrefab`).
- Loader recreates every GameObject (`new GameObject`) and parents it; there is no matching against existing objects when loading a scene.

### 2.2 Components table [C] (`SaveComponents`, `:110-155`; `LoadComponents`, `:229-292`)

For each GameObject in the [B] order:
```
string  gameObjectGuid          (= the ModTransform component's guid; ignored by the loader: read and discarded, :233)
int32   componentCount
repeat componentCount:
    string  typeName            (System.Type.ToString() of the ModBehaviour subclass, e.g. "ModTransform", "ModProp", "ModLogic")
    string  componentGuid       (ModBehaviour.guid)
    bool    enabled
```
- Only `ModBehaviour` subclasses that are registered in `ModReflectionSystem` (i.e. have `[ModSerialized]`) are written. Plain Unity components are not serialized: Unity colliders/renderers/etc. are reconstructed by the `ModBehaviour` wrappers (`ModBehaviour.SetupComponents`) [C/I].
- If a GameObject has a `ModPrefabInstance`, only `ModPrefabInstance` and `ModTransform` are kept (`:123-133`).
- Order within a GameObject: sorted by `savePriority` descending: `ModTransform`(100) > `ModPrefabInstance`(50) > `ModVisualScriptReference`(25) > Default(0) (`ModBehaviourLoadPriority.cs`). The sort is not explicitly stable for equal priorities (List.Sort) [I].
- Duplicate component types on one object: loader matches the n-th occurrence of a type to the n-th existing/added component (`GetOrAddComponent(type, index)`, `Extensions.cs:470`).
- Loader: for each record, resolve `Type.GetType(typeName)` (only Assembly-CSharp / corlib names resolve); if null, try `[ModPreviouslySerializedAs("OldTypeName")]` on any loaded type (`TryGetPreviousSerializedType`, `:294-305`); if still null the next line dereferences it -> exception -> the whole scene load fails (`SceneException`). There is no "skip unknown component" path [C: `:249-255` + try/catch scope `:264-283`].
- After the table the loader deletes components that exist on the GameObject but were not in the file (except `ModTransform`) (`:284-289`).

### 2.3 Values section [D] (`SaveComponentValues`, `:157-165`; `LoadComponentValues`, `:307-317`)

For each component (flattened list in table order):
```
ushort propertyCount
repeat propertyCount:
    string  propertyName
    int32   byteLength            (payload length; back-patched after writing, ModReflectionTypes.cs:265-312)
    payload                       (type-dependent, see 2.5; arrays are prefixed by int32 count)
```
- `OnBeforeSerialize()` is called on each behaviour before it is written; `OnAfterDeserialize()` after its values are read (and `SettingPropertiesFromLoad` is true while loading).
- Loader (`ReadType(br, obj, properties)`, `ModReflectionTypes.cs:495-506`): looks the name up among the component's current serialized properties. **Unknown/removed name -> skipped using `byteLength`** (safe). Known name -> parsed by the current property type, ignoring `byteLength`; a type change would desynchronise the stream [I; no re-sync code seen].
- Values are set through the property setter (FastMember). Setter side effects run during load [C].

### 2.4 Which members are serialized (attribute system) [C]

- A class is a serializable component iff it carries `[ModSerialized(runtimeType, uiName, uiIcon, addable, savePriority, iconColor, folder, modTypeFlags, supportsChildren)]` (`ModSerialized.cs`); discovered by scanning Assembly-CSharp (`ModReflectionSystem.SearchForReflectionTypes`, `ModReflectionSystem.cs:140-257`). 41 such classes exist in this build (Transform, Prop, Logic, Trigger, Spawner, Light, Camera, BoxCollider, Rigidbody, ParticleSystem, TextMeshPro, ScriptReference, PrefabInstance, BoardNode, PlayerSpawn, ...).
- **Serialized members = PUBLIC INSTANCE PROPERTIES** with a getter, declared on the concrete class itself (`BindingFlags.DeclaredOnly`) plus the public properties of `ModBehaviour` (i.e. `guid`). Overridden properties are dropped (`GetBaseDefinition() != getter`). Plain public fields are NOT serialized (field helper `GetReflectedFields` exists but is not used for components) (`ModReflectionSystem.cs:74-112`). Properties of intermediate base classes are not picked up [I from DeclaredOnly].
- `[ModIgnore]` (property/field): excluded (`ModIgnore.cs`). A property with an unsupported type and no `[ModIgnore]` is also dropped, with a console error "Cannot Save/Load".
- `[ModHideInInspector]` / `[ModHeader]`: UI-only metadata read in `ReflectedProperty.Initialize` (`ReflectedProperty.cs`); **do not** affect serialization (e.g. `ModBehaviour.guid` is hidden but serialized).
- `[ModPreviouslySerializedAs("Name")]` applies to **types** only (class-level rename; `ModPreviouslySerializedAs.cs`, used at `ModHierarchySerializer.cs:294`). There is no per-property rename attribute; a renamed property loses its value (its old name is skipped).
- Enums and `[Flags]` enums: int32. Arrays (`T[]`): int32 length + elements. `List<T>` is not in the type map -> unsupported.

### 2.5 Supported value types (`ModReflectionTypes.m_reflectionTypes`, `ModReflectionTypes.cs:10-207`; payload `:265-474`, `:527-762`)

| Type | Payload |
|---|---|
| bool, byte, char, short, ushort, int, uint, long, ulong, float, double | BinaryWriter primitive |
| string | length-prefixed UTF-8 |
| enum / flags enum | int32 |
| Vector2/3/4, Quaternion, Rect, Color, Color32 | raw floats (2/3/4/4/4/4/4 floats; Color32 as bytes) via `Extensions` Write/Read helpers [I on exact Color32 layout] |
| LayerMask | int32 value |
| SerializableGuid | string (the GUID text) |
| AnimationCurve, ParticleSystem.MinMaxCurve / MinMaxGradient / Burst, Gradient | custom field-by-field binary (`Extensions.cs:644-748, 857-900`) |
| Asset / object reference types: Prop, Material, AudioClip, Texture2D, Mesh, Transform, GameObject, Component (any reflected component), RuntimeAnimatorController, Avatar, MonoBehaviour (script), PummelVisualScript, PummelPrefab, RectTransform | `bool hasValue` then, if true, `string guid` (`Extensions.WriteObject/ReadObject`, `Extensions.cs:534-581`) |
| BoardNodeConnection | JSON string of `BoardNodeConnectionContainer` |
| SimpleModHitAction, SimpleAnimationLayer, ModSimpleAnimation, ModWeaponInfo, ModComplexFloat | JSON string (`WriteJson`: `JsonUtility.ToJson` pretty-printed; empty string for null) |
| ModAction | ushort `ModActionType` + JSON string of the concrete action class |
| ModLogicTrigger | ushort `ModTriggerType` + JSON string; ushort action count; per action: ushort type + JSON string |
| Shader | in the type map but has no payload case: serializing a Shader property throws "not implemented" (`:461`) [C] |

Any other type -> `UNKNOWN` (dropped). Script-generated (Visual Script) components reuse the same property serializer for their own public fields, nested as a `byte[]` blob (`ModScriptReference.scriptValues`, `ModScriptReference.cs:76-92`) with the same name/size/payload framing.

### 2.6 Identity and references [C]

- Every `ModBehaviour` has a `guid` (`SerializableGuid`, written as GUID text). A GameObject's identity = the guid of its `ModTransform`. New GUIDs are random (`Guid.NewGuid`).
- Cross-object references are always **GUID strings**, never indices: reference-typed properties (table above) call `WorkshopController.GetAssetWithObject` to turn the object into a `ModAsset` and write its guid; on load `GetAssetWithGuid` resolves it (`Extensions.cs:534-581`, `WorkshopController.cs:777-811`).
- Scene components are made resolvable by `ModBehaviourMonitor.AddOrUpdateModBehaviours` (`ModBehaviourMonitor.cs:~70-92`), which registers a runtime `ModAsset` per `ModBehaviour` with `guid = mb.guid` and `assetLocation = Runtime` (or the owning prefab's guid for objects inside a prefab). So a "reference to another GameObject/component in the scene" = that component's guid (Transform/GameObject refs resolve via the object's `ModTransform` guid).
- Resolution is by exact GUID in a list sorted by guid, binary-searched (`ModAssetList.cs:108-121`). Lookup order: global built-in asset list, then current mod's list, then background-load mod's list (`WorkshopController.cs:777-789`).
- **Dangling reference on load**: `ReadObject` logs "Couldn't find object with guid" and returns null (`Extensions.cs:563-581`); the load continues, and the next save writes `hasValue=false`, i.e. the dangling reference is permanently lost.
- Load order matters: asset list (all `Assets\` files) is built BEFORE the scene is read, and the scene's behaviours are registered (`ModBehaviourMonitor.UpdateBehaviours`) between sections [C] and [D] so that scene-object references resolve (`ModHierarchySerializer.cs:183-196`).
- GUID text format: standard `Guid.ToString()` ("D", lowercase hex with hyphens).

### 2.7 Action Editor / logic [C]

Logic is stored inside the scene (or prefab) as the property values of a `ModLogic` component: its `Triggers` property is a `ModLogicTrigger[]` (array count + per element `ushort triggerType, JSON, ushort n, n x (ushort actionType, JSON)`). The JSON strings are `JsonUtility` dumps of classes in namespace `ModSystem.Logic` (`ModLogicTrigger`, `ModAction` and ~60 subclasses; type ids = `ModActionType` / `ModTriggerType` enums). Examples of JSON fields: `ModAction.m_type`, `m_version`; `TargetAction.m_targetFlags` (enum int), `m_targets` (array of `ModAssetReference<Component>`: each `{m_assetGUID:{...}, m_asset:...}` referencing a component guid) (`ModSystem.Logic/TargetAction.cs`). Concrete action class is chosen by `ModAction.GetActionClassType(type)`; unknown id -> null type -> `ReadJson` returns null [C].
Event-ish components (`ModTrigger`, `ModBoardSpaceEvent`, `ModSpawner`, `ModDestrtible`...) keep their action arrays in the same way.
Visual scripting ("graphs") is a different system: see 5.4.

### 2.8 GUID in JSON files [I]

`SerializableGuid` has a private `System.Guid` (not Unity-serializable) and a `[SerializeField] string serializedGuid`, populated in `OnBeforeSerialize` and parsed in `OnAfterDeserialize` (`SerializableGuid.cs`). So wherever a GUID **struct** is a field of a JsonUtility class it appears as an object with the key `serializedGuid` (e.g. `"previewImageGuid": {"serializedGuid": "..."}`), while `ScreenshotTextureGuid` is a plain string and `ModAsset.guid` in `.pmeta` is the struct form (3.3). A malformed GUID text parses to `Guid.Empty` with a warning (`SerializableGuid.cs:55-65`). Confirm the exact JSON shape against a real file before relying on it.

## 3. Assets: types, identity, sidecars

### 3.1 Asset classes (`ModAsset` hierarchy)

| Class | File ext | Folder | Underlying Unity object | Importer |
|---|---|---|---|---|
| `PummelProp` (+ hidden child `PummelMesh`) | `.obj` (+ optional sibling `.mtl` read for materials) | `Assets\Props\` | `Prop` ScriptableObject (mesh + mats) | `PummelOBJImporter` (native plugin `ModEditorPlugin_Release.dll` parses OBJ, `ModImport.cs`) |
| `PummelTexture` | `.png .jpg .jpeg` | `Assets\Textures\` | `Texture2D` | `PummelImageImporter` |
| `PummelAudioClip` | `.wav .mp3 .ogg` | `Assets\Audio\` | `AudioClip` | `PummelAudioImporter` |
| `PummelMaterial` | `.pmat` | `Assets\Materials\` | `Material` | `PummelMaterialImporter` |
| `PummelPrefab` | `.pfab` | `Assets\Prefabs\` | GameObject tree (binary PMH, see 2) | `PummelPrefabImporter` |
| `PummelVisualScript` | `.pgf` | `Assets\VisualScripts\` | graph JSON + generated C# | `PummelVisualScriptImporter` |
`PummelAssetImporter.GetImporter` is the extension->importer table (`PummelAssetImporter.cs:77-114`); anything else (e.g. `.fbx`, `.gltf`, `.mtl`, `.txt`) has no importer and is ignored with a warning. `AssetType` enum (`AssetType.cs`) is not used by this path. `ModAssetInternal` is an empty subclass. [C]

### 3.2 Internal vs external vs runtime (`AssetLocation`, `AssetLocationGuid.cs`)

`ModAsset.assetLocation` is a GUID naming where an asset lives: Internal `4dde710c-fa53-4c44-8bcd-99be6ee28513` (built-in game asset), External `757b15db-4305-4406-b767-1ec002daa769` (file in the mod's `Assets\`), Runtime `781e4923-2d66-4d60-b01b-a92d2316b7ce` (scene component registered by `ModBehaviourMonitor`). A prefab's own guid is used as the location for components inside that prefab. The `AssetLocation` flags enum is a UI filter only.
- **Internal assets** live in the game's `ModAssetList` ScriptableObject and are loaded lazily via **Addressables using the asset's GUID text as the addressable key** (`ModAsset.GetAddressable`, `ModAsset.cs:282-316`); types: Prop, Material, Texture2D, AudioClip, Shader (`assetTypeString` map, `:88-110`). They are never written to the mod folder; a mod references them by GUID only. The set of built-in GUIDs is in the game's asset bundles, not in code [?]. Two built-in default GUIDs appear in code as board music `3ac2bff4-9bc6-406e-8e6f-b725dc69e08a` and ambience `05f5b5c8-5190-49d7-ad7a-f154db4e4f29` (`BoardModSettings.cs`).
- **External assets** = files under `Assets\` + `.pmeta` sidecar holding their GUID.

### 3.3 `.pmeta` sidecar [C shape / I exact JSON]

`JsonUtility.ToJson(asset)` of the `ModAssetExternal` subclass with the Unity object detached (`ModAssetExternal.cs:50-54`). Serialized fields (public/`[SerializeField]`, non-`[NonSerialized]`): `enabled`, `name` (file name without extension), `guid` (GUID struct), `tags` (string list; lowercased on load), `assetTypeString`, `atlasIndex`, `atlasPtrIndex` (reset on load, `OnAfterDeserialize`), `assetLocation` (GUID struct), `assetFolder` (virtual browser folder such as `/Props/`), `runtimeAtlas`; plus per type: texture `m_compression` (`PummelTextureCompression` enum), `m_filter` (`FilterMode`), `m_wrapMode`; prop `m_meshAsset` (nested `PummelMesh` incl. `m_recalculateNormals`); prefab `isEnabled`; visual script `type`, `m_generatedType`, `m_generatedNodes`, `m_generatedNodeVariants`; material `m_shaderName`, `m_shaderData` (byte[] holding a binary "PMAT" blob of the shader properties; JsonUtility writes byte[] as an int array [I]) (`PummelMaterial.cs:7-36, 56-122`). `filePath`/`fileName` are `[NonSerialized]` and recomputed from the actual file location: the sidecar is positional (`<file>.pmeta` next to `<file>`).
- Identity = `guid` inside the sidecar. Moving/renaming the pair keeps the GUID (editor rename moves both, `ModAssetExternal.Rename`, `:70-110`); copying a file without its sidecar creates a new GUID; deleting a sidecar loses all references to the asset.
- `PummelMesh` (`HideAndDontSave`) has no file of its own.
- Special `.pmat`: the `.pmat` file itself contains only the 4-byte-string `"PMAT"` stub (`PummelMaterial.Save`, `:49-55`); the material data is in the sidecar's `m_shaderData`. [C]
- `.pfab` = PMH binary (section 2) of the prefab root; `.pgf` = JSON `{majorVersion, minorVersion, graphs[], variables[]}` (`PummelVisualScriptSaveData`; `SCRIPT_MAJOR_VERSION 1`, minor 0). The unused binary `Read/Write` in `PummelVisualScript.cs:449-490` is legacy [C].

### 3.4 What an import does on disk (`PummelMod.AddAsset(path, folder, copy)`, `PummelMod.cs:898-939`)

1. (copy=true) create `Assets\<folder>\` if missing; target name = `<stem>(i)<ext>` for the first free i in 0..99 (`i=0` has no suffix); `File.Copy` (no overwrite).
2. `ImportFile` -> `PummelAssetImporter.LoadFile` -> importer -> `TryCreateOrLoadMetaAsset`: if no `<file>.pmeta`, **write a new sidecar immediately** (fresh random GUID).
3. Importer decodes into Unity objects in memory (OBJ via native plugin: triangulated, X mirrored and winding flipped when `ModCreatedVersion>=1`, submesh per material id, vertex cap `UInt32` indices >65535, auto normals if missing; PNG/JPG via `Texture2D.LoadImage`/UnityWebRequest with optional compression, filter and wrap from the sidecar; audio via `UnityWebRequestMultimedia`).
4. Failure to import with copy=true deletes the copied file and its sidecar (`DeleteFailedAsset`, `:941-951`).
Nothing is written to the **source** file path of the import. Texture on-disk cache is NOT used in the editor (see architecture doc section 5).

## 4. Asset list enumeration [C]

`PummelMod.RefreshImportedFiles` (`PummelMod.cs:953-996`): enumerate **all files under `Assets\` recursively except `*.pmeta`/`*.meta`**, group by extension, import every group except `.pmat`/`.pfab` first (in dictionary order), then `.pmat`, then `.pfab` (materials reference textures, prefabs reference everything). Folder names carry no meaning for typing; the extension does. Files with unknown extensions are skipped, no sidecar is created for them. Orphan `.pmeta` (source missing) are ignored silently, not deleted.

## 5. Visual scripts (graphs) [C]

5.1 Stored as `.pgf` JSON assets under `Assets\VisualScripts\` (not in the scene). A scene/prefab references one through a `ModScriptReference` component (`ScriptType` enum, `VisualScript` = asset GUID, `scriptValues` = the script's own exported fields as a `byte[]`, 2.5). 5.2 `.pgf` content = `Graph[]` and `GraphVariable[]` (namespace `VisualScripting`; not mapped further). 5.3 On load the importer registers the script's generated data type/nodes with `GraphDefinitionSystem` and generates a C# class string (`ScriptGenerator`); the game compiles it at runtime (`PummelMod.CompileResult`) [C]. 5.4 `Save()` also regenerates types/nodes before writing (`PummelVisualScript.cs:373`).

## 6. Mod folder naming [C]

- New mod: folder = `Path.Combine(<WorkshopMods>, <typed name>)` verbatim (`NewModPopup.CreateNewMod`, `NewModPopup.cs`; `EditorSystem.CreateNewMod`, `EditorSystem.cs:823-846`). `Meta.json.Name` is initialised from the folder name (`PummelMod` ctor, `:160-170`) so they match at creation. A mod named `MCP_something` therefore gets folder `MCP_something` exactly, provided the UI validation passes.
- Validation (UI only; `EditorPopup.IsValidFilenameInput/IsValidModnameInput`, `EditorPopup.cs:51-76`): non-empty, <=260 chars, no control chars or `\ / ? * : " ; | < > ¥`, not a reserved DOS device name (PRN, AUX, NUL, CON, COM1-9, LPT1-9, CLOCK$), must not end in a space or dot; and no existing mod with the same **Meta Name, case-insensitive** (`EditorAvailableModManager.DoesModWithNameAlreadyExist`). Characters like `_` and digits are fine. The folder-existence check is by name only; creating over an existing folder that has no readable `Meta.json` is not guarded [I].
- Names are **not** sanitised or altered (no slug/escape). Invalid names are blocked by the OK button, not rewritten. Programmatic entry (a plugin calling `EditorSystem.CreateNewMod` directly) bypasses these checks.
- Rename (`EditorSystem.RenameMod`, `EditorSystem.cs:1172-1215`): refuses if the target folder exists; otherwise `Directory.Move(old,new)`, then either `PummelMod.RenameMod` (loaded mod) or `RenameUnloadedMod`. Rewrites `Meta.json` (Name, LastModified), `WorkshopItem.json` (title) and `MinigameDefinitionData.json`/`MapDetails.json` (display name); a loaded mod also runs `RefreshImportedFiles` and its next full save rewrites everything. Folder name == Meta Name == workshop title after rename.
- Template creation (`CreateNewMod` with a template path): `DirectoryCopy` (no overwrite, skips `*.meta`) copies the template tree **including `.pmeta` files, hence identical asset GUIDs as the template**; after load, `RenameMod(<folder>)`, `publishedFileId` reset to 256 (`EditorSystem.cs:823-846`).
- Reading: the editor lists mods by scanning child directories of `WorkshopMods` that contain `Data\Meta.json` (`EditorAvailableModManager.cs:14-24`); displayed name = `Meta.json.Name`, opened by directory. A folder whose name differs from `Meta.Name` (manual copy) is legal for loading.
