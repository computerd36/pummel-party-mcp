# 06 - Mod settings: how the editor reads and writes them (Phase 4, Part A recon)

Game 1.14.1k. Source: `decompiled/Assembly-CSharp` (read-only, not distributed). This is a reference for the plugin's settings
tools; it names classes, members, enum values and paraphrases behaviour, it does not reproduce game code.

Evidence tags: **[C]** confirmed from code (`File.cs:line`, file names are Assembly-CSharp classes), **[I]** inferred,
**[U]** unverified (needs the running game or files that were out of reach: the game's `StreamingAssets` preset folders and
the Unity Post-Processing package are not part of the decompiled set).

"Settings window" = the Inspector while the yellow `Minigame Settings` / `Board Settings` / `Item Settings` entry at the top of
the Hierarchy is selected (`HierarchyWidget.cs:423`, `InspectorWidget.cs:125-127` -> `ModSettings.DrawInspectorUI`).
Groups are `ImGui.CollapsingHeader`s; **the code of a group, including its clamps and forced values, runs only while the group is
expanded and the settings entry is selected** (`ModEditorGui.cs:157`) [C]. Consequence used throughout: UI limits are not a
property of the data classes; a plugin must apply them itself.

Storage map (all under `<mod>/Data/`):

| File | Written by (`PummelMod.Save`, `PummelMod.cs:567-592`) | Content relevant here |
|---|---|---|
| `Meta.json` | `SaveMetaData` (`:689`) | `SelectedLightingPreset`, `SelectedPostProcessingPreset` (and `LastModified`) |
| `WorkshopItem.json` | `SaveWorkshopItemDetails` (`:594`) | **off limits**; rewritten in full from the in-memory object on every save |
| `Lighting.json` | `SaveLightingSettings` (`ModDataReaderWriter.cs:53`) of **`CustomLighting`** | `ModLightingSettings` public fields (JsonUtility) |
| `PostProcessing.data` | `SavePostProcessSettings` (`ModDataReaderWriter.cs:78`) of **`postProcess` (the ACTIVE object)** | binary, version 1, see 5.4 |
| `MainScene.scene` | `SaveScene` | not settings |
| `ModSettings.json` | `SaveModSpecificData` of the mod type | `MinigameModSettings` / `BoardModSettings` / `ItemModSettings` / `BoardSpaceModSettings` as JsonUtility JSON |
| `MinigameDefinitionData.json` | `MinigameMod.SaveModSpecificData` (`MinigameMod.cs:119-124`) | `SerializableMinigameDefinition` |
| `MapDetails.json` | `BoardMod.SaveModSpecificData` (`BoardMod.cs:77-85`) | `SerializableMapDetails` |
| `ItemDetails.json` | `ItemMod.SaveModSpecificData` (`ItemMod.cs:77-81`) | `SerializableItemDetails` |
| `Preview.jpg` | **not by `Save()`**: `EditorSystem.SaveCurrentMod` (`EditorSystem.cs:808-820`) -> `SavePreviewScreenshot` (`:1363`) | see section 6 |

---

## 1. Inventory

### 1.1 What the user sees, per mod type

| Mod type (`ModType`) | Settings class (`GetModSettings()`) | Groups shown (in order) | Controls | Lighting / screen effects |
|---|---|---|---|---|
| Minigame (3) | `MinigameModSettings` (`MinigameMod.cs:16`) | Minigame Details; Minigame Settings; Player Settings (Player Type, then foldouts Movement, Camera, Punching, Stuns, Respawning, Event Triggers, Health, Name Tags); Music & Ambient Sound; Lighting; Screen Effects (`MinigameModSettings.cs:90-124`) | 3 + 8 + 1 + (10 + 4..10 + 5 + 1 + 3 + 6 + 3 + 2) + 4 = 50..56 + the two groups | yes |
| Board (1) | `BoardModSettings` (`BoardModSettings.cs:68-108`) | Board Details; Custom Spaces; Board Size; Camera Settings; Music & Ambient Sound; Lighting; Screen Effects | 7 + 1 + 4 + 5 + 4 = 21 + the two groups | yes |
| BoardSpace (2) | `BoardSpaceModSettings` | one explanatory text, no controls (`BoardSpaceModSettings.cs:15`) | 0 | **no UI**, although `UsesLighting`/`UsesPostProcessing` are true for this type so `Lighting.json`/`PostProcessing.data` are still written (`BoardSpaceMod.cs:12` overrides only `UsesHierarchy`) [C] |
| Item (0) | `ItemModSettings` | Item Details block: texts, 3 prefab pickers, held transform, icon (`ItemModSettings.cs:25-60`) | 12 + 1 button | `UsesLighting`/`UsesPostProcessing` false (`ItemMod.cs:17-19`): nothing saved |

Data-class field counts (serialized fields; "shown" = a control exists):

| Class (file) | Fields | Shown | Never shown |
|---|---|---|---|
| `SimpleModControllerSettings` -> `ModSettings.json` | 14 | 13 | `SpawnType` |
| `SimpleModPlayerSettings` -> `ModSettings.json` | 48 | 38 | 10: `coyoteTime`, `shootDistance`, `shootRechargeSpeed`, `shotSpreadVertical`, `shotSpreadHorizontal`, `bulletsPerShot`, `firstShotCentred`, `firingPattern`, `respawnAnimationType`, `hitTrigger` |
| `SerializableMinigameDefinition` -> `MinigameDefinitionData.json` | 8 | 3 (Name read-only) | `MinigameNameToken`, `DescriptionToken`, `MinPlayers`, `MaxPlayers`, `InputHelp` (derived) |
| `BoardModSettings` -> `ModSettings.json` | 3 (+Version) | 3 | - |
| `SerializableMapDetails` -> `MapDetails.json` | 16 | 12 (Name read-only) | `SceneName`, `OverrideSpacePrefabs`, `OverrideSpace[13]`, `SpacePrefabOverrideGuids[13]` |
| `SerializableItemDetails` -> `ItemDetails.json` | 12 | 12 | - |
| `ModLightingSettings` -> `Lighting.json` | 39 | 32 (Custom only) | `version`, `presetName`, `shadowCascade4Split`, `shadowProjection`, `shadowCascade2Split`, `defaultReflectionMode`, `reflectionIntensity` |
| `ModPostProcessSettings` -> `PostProcessing.data` | 9 effect types, 65 values (each float/bool/color/vec2 value has an override flag) | all (Custom only) | - |
| `ModMetaData` -> `Meta.json` | 9 | 2 (via the two preset combos) | the rest |

All counts [C] by reading the classes and the draw code listed in section 2.

Dead code: enums `ModSettingsMinigameController` and `ModSettingsMinigamePlayer` and the `m_customMinigameController/Player` fields have no users
other than their own declarations [C] (`MinigameModSettings.cs:18-22`); `ModSettingsPlayerType {ThirdPerson, TopDown}` is only the choice list of the Player Type combo.

---

## 2. Field tables: shown when, choices, limits, write path

Common facts [C]:
- The controls read the field at the start of the frame and write it back at the end of the same call (`x = Widget(x)`), so values are re-clamped **every frame while the group is drawn**. Limits come from explicit `Mathf.Clamp` after the widget, or from the slider (`ModEditorGui.InputSliderFloat` clamps its result, `ModEditorGui.cs:1493-1499`). Plain `InputFloat`/`InputInt`/`InputVec3`/`InputColorPicker4` have no limit of their own.
- Write path "pub field" = public instance field, "pub prop" = public property, "prop (guid)" = property that stores a GUID.
- "Hidden" = never drawn; the value stays whatever the file/defaults hold.
- `SimpleMinigameSettings` = `MinigameModSettings.SimpleMinigameSettings`; `SimpleModPlayerSettings` is the same-named member of `MinigameModSettings`. Both are plain objects created in the `MinigameModSettings` constructor or by JSON load.

### 2.1 Minigame Details (`MinigameModSettings.DrawMinigameDefinition`, `:472-485`)

| Field | Type | Default | Stored in | Shown when | Choices / limits | Write path | Evidence |
|---|---|---|---|---|---|---|---|
| Name | string | mod name | `MinigameDefinitionData.json` `MinigameName` | always, read-only text + a rename button (opens `RenameModPopup`) | rename also rewrites `Meta.json` and `WorkshopItem.json` title: **not a settings write** | not writable by the settings tools | `:474-479`, `PummelMod.cs:255-310` [C] |
| Description | string | `"A minigame mod."` | same, `Description` | always | multiline, no length limit in the widget | `GetMinigameDefinition().description` (pub field of the `MinigameDefinition` ScriptableObject) | `:480`, `MinigameMod.cs:34,90` [C] |
| Preview Image | Texture2D asset | none | same, `ScreenshotTextureGuid` (null/"" = none) | always | asset picker: type `Texture2D`, locations Internal+External (the mod's own assets and the built-in ones), no filter callback, no tags; "None" allowed | `GetMinigameDefinition().screenshotTexture` (pub field); the picker's callback sets exactly this field | `:484`, `ModEditorGui.cs:1333-1358`, `ChooseAssetPopup.cs:85-` [C] |
| (hidden) `MinigameNameToken`, `DescriptionToken` | string | `"MinigameName"`, `"DescriptionName"` | same | never | - | - | `SerializableMinigameDefinition.cs:9-13` [C] |
| (hidden) `MinPlayers`, `MaxPlayers` | int | 2, 8 | same | never | fixed by `PopulateMinigameDefinition` on every load | - | `MinigameMod.cs:31-38` [C] |
| (derived) `InputHelp` | struct | generated | same | never | rebuilt from `canJump`, `canPunch`, `ChosenPlayerType` by `GenerateMinigameControls` (public) on load, on player-type change and on every save | do not write | `MinigameModSettings.cs:487-529`, `MinigameMod.cs:104,121` [C] |

### 2.2 Minigame Settings (`ControllerSettings`, `MinigameModSettings.cs:126-161`) - class `SimpleModControllerSettings`

| Field | Type | Default | Shown when | Choices / limits (on draw) | Runtime re-clamp | Write path | Evidence |
|---|---|---|---|---|---|---|---|
| `NumberOfRounds` ("Rounds") | int | 1 | always | clamp 1..20 | 1..20 (`SimpleModController.cs:104`) | pub field | `:128-129` [C] |
| `EndConditions` | flags `SimpleMinigameEndCondition` (Timer=1, ObtainPoints=2, RemainingPlayersAlive=4, FinishMinigame=8) | Timer | always | any combination incl. 0 ("Nothing"; the dropdown also offers All/Nothing) | - | pub field | `:130`, `ModEditorGui.cs:1815-` [C] |
| `PointsToWin` | int | 5 | `EndConditions` has ObtainPoints | clamp 1..1,000,000 | clamp 0..1,000,000 | pub field | `:134-138` [C] |
| `RoundDuration` ("Round Duration (s)") | float | 60 | `EndConditions` has Timer | clamp 5..1200 | 5..1200 | pub field | `:139-143` [C] |
| `MinPlayersAlive` ("Players Alive") | int | 1 | `EndConditions` has RemainingPlayersAlive | clamp 1..7 | not clamped | pub field | `:144-148` [C] |
| (whole sub-block) | | | the three above are indented under `EndConditions != 0` | | | | `:131` [C] |
| `PlacementCondition` | enum `SimpleMinigamePlacementCondition` (MostPoints, LeastPoints, FinishOrder, LastManStanding) | MostPoints | always | all 4 | - | pub field | `:151` [C] |
| `ShowPlayerScore` ("Show Score in UI") | bool | true | always | - | - | pub field | `:153` [C] |
| `OnlyShowCombinedTeamScoreInTeams` ("(Teams) Hide Player Scores") | bool | false | `ShowPlayerScore` | - | - | pub field | `:154-159` [C] |
| (hidden) `SpawnType` | enum `PlayerSpawnType` | PortalAbove | never | runtime ignores it and hard-codes PortalAbove | - | - | `SimpleModController.cs:108` [C] |

### 2.3 Player Settings - Player Type and foldouts (`MinigameModSettings.cs:163-399`) - classes `SimpleModControllerSettings` + `SimpleModPlayerSettings`

Player Type is a coupled pair of controls over one field, `SimpleMinigameSettings.ChosenPlayerType` (enum `SimpleModPlayerType`: ThirdPerson, TopDown, TwinStick):

| Control | Shown when | Behaviour | Evidence |
|---|---|---|---|
| "Player Type" combo (choices ThirdPerson, TopDown only; `ModSettingsPlayerType`) | always | selecting ThirdPerson -> `ThirdPerson`; selecting TopDown -> `TopDown` unless the value already is `TwinStick` (then it stays). The combo shows `min(type,1)`, so TwinStick displays as TopDown. A change calls `GenerateMinigameControls()`. | `:165-178` [C] |
| "Twinstick Controls" checkbox (Movement foldout, bottom) | type is TopDown or TwinStick | checked -> `TwinStick`, unchecked -> `TopDown`. Does **not** call `GenerateMinigameControls` (it is regenerated at save anyway). | `:219-230` [C] |

`SimpleModPlayerSettings` controls (all pub fields; drawn only while the settings entry is selected and the foldout is open):

| Foldout | Field | Type | Default | Shown when | Choices / limits (on draw) | Evidence |
|---|---|---|---|---|---|---|
| Movement | `movementSpeed` | float | 6 | always | clamp 0..1000 | `:191-192` [C] |
| | `movementAcceleration` | float | 50 | always | clamp 0..1000 | `:194-195` |
| | `movementDecelaration` (sic) | float | 50 | always | clamp 0..10000 | `:197-198` |
| | `midAirMovementAcceleration` | float | 8 | always | clamp 0..1000 | `:200-201` |
| | `playerGravity` | float | 25 | always | shown as "Gravity Scale" = value/25, slider -4..4, so stored range -100..100 | `:203-205` |
| | `stepOffset` | float | 0.5 | always | slider 0..1.75 | `:207` |
| | `canJump` | bool | true | always | - | `:208` |
| | `jumpSpeed` | float | 7 | `canJump` | clamp 0..1000 | `:212-213` |
| | `jumpCount` ("Mid-air Jumps") | int | 1 | `canJump` | shown as `jumpCount - 1`, clamp 0..1000, stored +1 (so stored 1..1001) | `:214-217` |
| Camera | `topDownCameraMode` | enum Static, Follow, FollowAll | Static | type is TopDown/TwinStick (foldout absent for ThirdPerson; in that case it is **forced to Static every frame**) | all 3 | `:234-259` |
| | `topDownCameraDistance` | float | 10 | same | slider 1..40 | `:242` |
| | `topDownCameraViewAngle` | float | 35 | same | slider 0..90 | `:243` |
| | `topDownCameraPositionOffset` | Vector3 | 0,0,0 | same | no limit | `:245` |
| | `topDownCameraFollowAllType` | enum CameraFollowType (Circle, Square) | Square | mode FollowAll | both | `:246-248`, `:408` |
| | `topDownCamerafollowAllAreaRect` | Rect | -5,-5,10,10 | mode FollowAll | edited as centre + size: Square: width, height each clamp 0..2000; Circle: diameter clamp 0..2000 and height := width; centre clamped to +-(1000 - half size) per axis; the rect is rebuilt and re-assigned every frame | `:402-434` |
| | `maxXMoveDist`, `maxZMoveDist` | float | 3, 3 | FollowAll and Square | no limit | `:416-417` |
| | `sphereMaxMoveDist` | float | 3 | FollowAll and Circle | no limit | `:425` |
| Punching | `canPunch` | bool | false | always | - | `:263` |
| | `punchRechargeSpeed` (Cooldown) | float | 0.75 | `canPunch` | clamp 0.1..1000 | `:267-268` |
| | `punchDamage` | int | 1 | `canPunch` | clamp -1000..1000 | `:269-270` |
| | `punchStunsTarget` | bool | false | `canPunch` | - | `:271` |
| | `punchStunDuration` | float | 1 | `canPunch` and `punchStunsTarget` | clamp 0.1..1000 | `:275-276` |
| Stuns | `stunImmunityLength` | float | 1 | always | clamp 0..1000 | `:288-289` |
| Respawning | `respawnEnabled` | bool | true | always | - | `:296` |
| | `respawnTime` | float | 3 | `respawnEnabled` | clamp 0..1000 | `:299-300` |
| | `respawnInvulnerableTime` | float | 1 | `respawnEnabled` | clamp 0..1000 | `:302-303` |
| Event Triggers | `weaponHitTrigger`, `tickTrigger`, `onDeathTrigger`, `onRespawnTrigger`, `onFirstSpawnTrigger`, `onRoundEndTrigger` | trigger objects | empty | always | already covered by the plugin's player-settings trigger tools (`Game/ActionModel.cs`); `tickTrigger` must stay a `TimerTrigger`, `weaponHitTrigger` a `WeaponHitTrigger` (the UI casts back every frame) | `:308-364` |
| Health | `useHealth` | bool | false | always | - | `:368` |
| | `showHealthBar` | bool | false | `useHealth` | - | `:373` |
| | `health` | int | 100 | `useHealth` | clamp 1..1000 | `:374-375` |
| Name Tags | `showNameTags` | bool | true | always | - | `:385` |
| | `nameTagType` | enum `UIPlayerNameTagType` {ScreenSpace=0, WorldSpaceScreenSize=1, WorldSpaceWorldSize=2, WorldSpaceWorldSizeOccluded=3, Disabled=4} | WorldSpaceWorldSizeOccluded | `showNameTags` | combo offers only values 1..3 ("Screen Space ", "World Space", "True World Space"); a stored 0 or 4 is rewritten to 1 on draw. "Disabled" is `showNameTags=false`. | `:386-395` |
| (hidden) | `coyoteTime` (0.2), `shootDistance` (12), `shootRechargeSpeed` (0.1), `shotSpreadVertical` (45), `shotSpreadHorizontal` (20), `bulletsPerShot` (1), `firstShotCentred` (true), `firingPattern` (Random/Circular), `respawnAnimationType` (Instant/Portal), `hitTrigger` | various | see left | never | the runtime reads them (`SimpleModPlayer.cs:1205-1228`) but no control exists; the file/defaults decide | `SimpleModPlayerSettings.cs:9-104` [C] |

### 2.4 Music & Ambient Sound, minigame (`:111-121`) - class `SimpleModControllerSettings`

| Field | Type | Default | Stored in | Shown when | Choices / limits | Write path | Evidence |
|---|---|---|---|---|---|---|---|
| Music | AudioClip asset | built-in clip (guid `13dbf0ac-...`) | `ModSettings.json` -> `SimpleMinigameSettings.MusicGUID` (private `[SerializeField]` SerializableGuid) | always | picker: type `AudioClip`, Internal+External, no filter, "None" allowed | pub prop `Music` (sets the clip and the guid) | `SimpleModControllerSettings.cs:26,43-61` [C] |
| Music Volume | float | 0.3 | same, `MusicVolume` | always | slider 0..1 | pub field | `:115` |
| Ambience | AudioClip asset | built-in clip (guid `90abde3b-...`) | same, `AmbienceGUID` | always | as Music | pub prop `Ambience` | `SimpleModControllerSettings.cs:36,63-81` |
| Ambience Volume | float | 0.5 | same, `AmbienceVolume` | always | slider 0..1 | pub field | `:119` |

### 2.5 Board Details, Custom Spaces, Board Size, Camera, Music (`BoardModSettings.cs`)

Board data is split: `MapDetails` (a ScriptableObject owned by `BoardMod`, `GetMapDetails()`) -> `MapDetails.json`; `BoardModSettings` -> `ModSettings.json`.

| Group | Field | Type | Default | Stored in | Shown when | Choices / limits | Write path | Evidence |
|---|---|---|---|---|---|---|---|---|
| Board Details | Name | string | mod name | `MapDetails.json` `MapName` | read-only + rename button | not a settings write | pub field `mapName` | `:205-210` [C] |
| | Description | string | `"A board mod."` | `Description` | always | multiline, no limit | pub field `description` | `:211`, `BoardMod.cs:27` |
| | Preview Image | Texture2D asset | none | `PreviewImageGuid` | always | picker Texture2D, Internal+External | pub field `previewImage` | `:215` |
| | Trophy Chests | int | 1 | `DefaultGoalChestCount` | always | clamp 1..8 | pub field `defaultGoalChestCount` | `:216-217` |
| | Weapons Cache | bool | true | `SupportsWeaponCache` | always | - | pub field `supportsWeaponCache` | `:219` |
| | Custom Chest Shuffle Position | bool | false | `UseCustomChestShuffleLocation` | always | - | pub field `useCustomChestShuffleLocation` | `:221` |
| | Chest Shuffle Position | Vector3 | 0,0,0 | `ChestShuffleLocation` | always drawn **and editable**; greyed text only when the bool is off (a gizmo draws when on) | none | pub field `chestShuffleLocation` | `:222-235` |
| Custom Spaces | `CustomSpacePrefabs` | PummelPrefab[] (stored as `ModAssetReference<PummelPrefab>[] m_customSpacePrefabs`) | empty | `ModSettings.json` -> `CustomSpaceSettings.m_customSpacePrefabs` | always | generic array editor ("Add Element", remove/reorder), each element picker: type PummelPrefab, location External only (the mod's own prefabs), no tag filter; null elements allowed (listed as "Empty (i)" in the board-space inspector) | pub prop `CustomSpaceSettings.CustomSpacePrefabs` (array getter/setter) | `:291-302`, `BoardModCustomSpaceSettings.cs`, `ModEditorGui.cs:107` [C] |
| Board Size | `UseCustomBoardSize` | bool | false | `ModSettings.json` | always | - | pub field | `:244` |
| | `mapExtents` (Left/Right size, Forward/Backward size, Map Centre) | Rect | new mod -20,-20,40,40; missing key -50,-50,100,100 | `MapDetails.json` `MapExtents` | always drawn; the three inputs are read-only unless `UseCustomBoardSize` | width, height clamp 0..2000; centre clamped to +-(1000 - half size); the rect is rebuilt and written back every frame, also when read-only. **When `UseCustomBoardSize` is false the rect is recomputed from the board nodes at every save** (`BoardMod.cs:79-81,132-154`: bounding box of node positions + 3 wide / 9 deep margin; zero rect when no nodes). | pub field `mapExtents` | `:238-264` [C] |
| Camera Settings | `CameraSettings.CameraViewAngle` ("Elevation Angle") | float | 37.5 | `ModSettings.json` | always | slider 14..90 | pub field | `:166` |
| | `CameraMode` | int 0 Perspective, 1 Orthographic | 0 | | always | combo 0..1. **On change `CameraDistance` is set to 13 (Perspective) or 40 (Orthographic).** | pub field | `:168-180` |
| | `CameraFov` | float | 60 | | mode 0 | slider 30..90 | pub field | `:183` |
| | `CameraDistance` | float | 13 | | mode 0 (no control in mode 1, value stays 40) | slider 6..20 | pub field | `:184` |
| | `CameraSize` | float | 5 | | mode 1 | slider 4..10 | pub field | `:188` |
| | "Reset to default values" button | | | | always | sets mode 0, fov 60, angle 37.5, distance 13, size 5 | | `:191-198` |
| Music & Ambient | Music Clip / Ambient Clip | AudioClip assets | built-in clips (guids `3ac2bff4-...`, `05f5b5c8-...`) set by the `BoardModSettings` constructor for a new mod | `MapDetails.json` `MusicGuid`, `AmbienceGuid` | always | picker AudioClip, Internal+External, "None" allowed | pub fields `music`, `ambience` (plain AudioClip fields; the guid is computed at save by `ExtractMapDetailsData`) | `:97,101`, `SerializableMapDetails.cs:39-62` |
| | Music Volume / Ambience Volume | float | 0.3 / 0.5 for a new mod (`MapDetails` field default is 1) | `MusicVol`, `AmbienceVol` | always | slider 0..1 | pub fields `musicVol`, `ambienceVol` | `:99,103` |
| (hidden) | `SceneName`, `OverrideSpacePrefabs`, `OverrideSpace[13]`, `SpacePrefabOverrideGuids[13]` | | | `MapDetails.json` | never | legacy for built-in boards | - | `SerializableMapDetails.cs:11-21` |

### 2.6 Item settings (`ItemModSettings.cs:25-60`), for completeness

`ItemMod.LoadModSpecificData` ends by creating a fresh `ItemModSettings` even after a successful JSON load (`ItemMod.cs:58-75`); the real data is `ItemDetails` (`ItemDetails.json`).

| Control | Field of `ItemDetails` | Type | Default | Limits | Evidence |
|---|---|---|---|---|---|
| Name, Description | `itemNameToken`, `descriptionToken` | string | `"Mod Item"`, `"A Mod item"` | none | `:35-36` |
| Item Use Prefab, Obtain Prefab, Held Prefab | `PummelPrefab`, `PummelRecievePrefab`, `PummelHeldPrefab` | prefab assets | none | picker PummelPrefab, External only | `:41-44` |
| Held Position / Rotation / Scale | `heldPosition`, `heldRotation`, `heldScale` | Vector3 | 0 / 0 / 100,100,100 | none | `:45-47` |
| Held Bone | `heldBone` | enum `PlayerBone` (all 49 values offered) | LeftHand | - | `:47` |
| Icon | `iconTexture` | Texture2D | none | picker Texture2D, Internal+External | `:49` |
| Icon Offset / Icon Size | `iconOffset`, `iconSize` | Vector2 | 0,0 / 61,61 | none | `:50-51` |
| "Generate Icon" button | - | | | disabled when no obtain prefab; **writes `Textures/ItemPreview.png` and imports it immediately** | `:53-61` |

---

## 3. What a change does immediately

| Section | Setter / handler side effects | After a plugin write | Evidence |
|---|---|---|---|
| Plain controller / player fields | none (public fields). Only the draw code clamps (section 2). | nothing needed for data. If the settings window is open the next frame re-clamps and may reveal differences (e.g. `jumpCount`). | [C] |
| `ChosenPlayerType` change | combo handler logs, calls `GenerateMinigameControls()` -> rewrites `m_minigameDefinition.inputHelp` (in memory) | call `MinigameSettings.GenerateMinigameControls()` after changing the type; it is also called at save and load, so it only matters for the in-memory definition | `MinigameModSettings.cs:174-178`, `MinigameMod.cs:121` [C] |
| Player type ThirdPerson | camera foldout is not drawn and `topDownCameraMode` is forced to Static every frame | write `Static` together with a ThirdPerson change | `:256-259` [C] |
| Camera foldout drawn (TopDown/TwinStick) | `UpdatePreviewCamera` creates a throw-away camera GameObject "Mod Preview Camera" (768x432 render texture, used only for the in-inspector picture) and positions it from the fields; in mode Follow it looks up a `ModPlayerSpawn` with `FindObjectOfType` and dereferences the result without a null check, **so Follow mode with no player spawn in the scene throws on every draw** | do not write `Follow` unless the scene has a `ModPlayerSpawn`; do not create or destroy this camera | `MinigameModSettings.cs:46-88`, `ModPreviewCamera.cs:12` [C] (exception behaviour inside ImGui [I]) |
| Preview image / description | none beyond the field. `MinigameDefinition.screenshotTextureGuid` ([NonSerialized], read by `MinigameMod.GetPreviewImageGuid` and used by `UnloadAssetsExcludingPreviewImage`) is **not** updated by the picker; it is refreshed only from the file on the next load | optionally set `screenshotTextureGuid` to the new asset's guid | `MinigameMod.cs:126-139`, `PummelMod.cs:758-770`, `SerializableMinigameDefinition.cs:36-52` [C] |
| Music / Ambience | prop setters resolve the clip's guid through `WorkshopController.GetAssetWithObject`; a clip that is not a registered asset stores `Empty` | set via the property, read it back | `SimpleModControllerSettings.cs:43-81` [C] |
| Board `CameraMode` change | resets `CameraDistance` (13 or 40) | replicate | `BoardModSettings.cs:168-180` [C] |
| Board `CustomSpacePrefabs` | the draw code serializes `CustomSpaceSettings` every frame (Editing state) and fires the **static** event `BoardModSettings.OnCustomSpacesUpdated` when the text changed; board nodes subscribe and rebuild their custom space object. Board-space components store an **index** into this array (`ModBoardSpace.ModCustomBoardSpace`), so inserting/removing/reordering shifts what existing spaces point at | after a write, invoke the event (private raise: reflection on the static event backing field, or toggle via the same text-diff trick) | `BoardModSettings.cs:291-299`, `BoardNode.cs:372-386`, `ModBoardSpaceInspector.cs:24-26` [C] (raise mechanism [I]) |
| Board map extents | recomputed from nodes at save unless `UseCustomBoardSize` | write extents only with `UseCustomBoardSize` true | `BoardMod.cs:79-81` [C] |
| Lighting preset combo | sets `Meta.SelectedLightingPreset`; picks the object (Custom -> `CustomLighting`, else the widget's cached preset object found by `PresetName == selected`; not found -> logs an error and uses a **new default** `ModLightingSettings`); assigns `mod.lighting`; calls `ApplyLightingChangesToRenderSettings()` | replicate the whole sequence: set meta name, assign `lighting`, apply. Setting only the meta name changes the dropdown but not the scene. | `LightingWidgetLogic.cs:57-73` [C] |
| Custom lighting edits | plain setters (properties with no logic except `ShadowCascade4Split`, which the UI never calls). The scene is updated by the draw loop: `ApplyLightingChangesToRenderSettings(updateGI)` **every frame** while the Custom editor is drawn (GI refresh every 3 s) | call `mod.lighting.ApplyLightingChangesToRenderSettings(true)` once after the batch (public). Without the window open nothing re-applies. | `LightingWidgetLogic.cs:181-186` [C] |
| Lighting apply | writes the process-wide `RenderSettings` (skybox, ambient, fog, reflection), `QualitySettings` shadow values and a **static** directional light object (`ModLightingSettings.m_directionalLight`, shared by all instances) and a cloud-shadow component; the skybox material belongs to the instance. `DynamicGI.UpdateEnvironment` when `updateGI` | apply the *active* object last | `ModLightingSettings.cs:15,580-743` [C] |
| Import built-in lighting profile (button) | replaces `mod.CustomLighting` by a **new object** loaded from the preset file and sets `lighting` to it; the previous object is not released. It does not change `SelectedLightingPreset` (the editor is only visible while it is already Custom) | after replacing, re-read `CustomLighting` | `LightingWidgetLogic.cs:206-215` [C] |
| Export / Import custom / Delete custom lighting profile (buttons) | **disk writes outside the mod**: `Application.persistentDataPath/ModEditorCustomLightingProfiles/<name>.json` (write, read, `File.Delete`) | never call; not part of the mod | `LightingWidgetLogic.cs:48,218-281` [C] |
| Post-processing preset combo | sets `Meta.SelectedPostProcessingPreset`, assigns `mod.postProcess` (Custom -> `CustomPostProcess`, else cached preset found by `PresetName == selected`; **not found -> null, then the next line dereferences it**), sets `EditorSystem.m_postProcessVolume.sharedProfile` to its profile, toggles the scene-view camera's `PostProcessLayer.enabled` off/on | replicate; validate the name against the list first | `PostProcessWidgetLogic.cs:53-78` [C] |
| Custom post-processing edits | values live in `CustomPostProcess.GetPostProcessProfile()`, which is the volume's `sharedProfile` while Custom is active, so value edits are live. Header checkbox = effect `enabled`; per-parameter checkbox = `overrideState` (and calls `RefreshCameraPPLayer`); closing a header removes the effect and refreshes the layer; "Add Effect" adds and does **not** refresh the layer | after adding/removing an effect or flipping an override, toggle `PostProcessLayer.enabled` of `EditorSystem.m_sceneViewCamera` (public field) | `PostProcessWidgetLogic.cs:80-141,470-479` [C] |
| Preset objects | the preset lists inside the two widget logic objects are created once per mod session by `SetupLightingWidget()` / `SetupPostProcessingWidget()` (public on `ModSettings`; they are also called from `MinigameMod`/`BoardMod` load when the editor is in Editing state). They are per-widget caches, **not static**, but they are what `mod.lighting`/`mod.postProcess` point to while a preset is selected. At load time (`PummelMod.LoadLightingSettings/LoadPostProceessing`) `lighting`/`postProcess` are separate objects freshly read from the preset file. | never mutate `mod.lighting`/`mod.postProcess` while a preset is selected; mutate only `CustomLighting`/`CustomPostProcess` | `ModSettings.cs:59-80`, `PummelMod.cs:607-640` [C] |

**Can the settings objects be replaced or mutated while the window is open?** Yes for mutation between frames, with these rules [C unless noted]:
- The window does not cache the data objects: every frame it dereferences `SimpleMinigameSettings`, `SimpleModPlayerSettings`, `CameraSettings`, `m_mapDetails`, `mod.CustomLighting`, `mod.lighting` anew, and `PostProcessWidgetLogic` re-reads the Custom profile (`RefreshCustomPostProcessProfile`). A write between frames is therefore seen at the next frame. This is the same read-modify-write pattern as the Logic inspector (`x = Widget(x)`), but with scalars and no list re-assignment; for the trigger fields the draw code re-assigns the same trigger reference each frame (casting back to `WeaponHitTrigger`/`TimerTrigger`).
- **Mutate in place; do not replace** `SimpleMinigameSettings`, `SimpleModPlayerSettings`, `CameraSettings`, `CustomSpaceSettings`. The settings object itself (`MinigameMod.m_modSettings`) is replaced only on mod load (`MinigameMod.cs:100,115`, `BoardMod.cs:55,73`), so a tool must always fetch `GetModSettings()` fresh.
- Lighting/post-processing objects are replaced by the editor itself at preset switch, import-built-in and load; always fetch `mod.CustomLighting`/`mod.CustomPostProcess` at call time.
- Text fields keep an ImGui edit buffer while active; a write while the user is typing into that very field can be overwritten. The plugin's existing guard (refuse while the mouse button is held or a dialog is open) covers the practical cases [I].
- Out-of-range values written while the group is open are silently clamped next frame; written while it is closed they persist (see section 10).

---

## 4. Dirty state, undo, save

| Question | Answer | Evidence |
|---|---|---|
| Undo for any settings control? | **None.** The settings window and its helpers (`ModEditorGui.Input*`, both widget logics) never touch `EditorUndo`. | `ModEditorGui.cs` has no undo calls; `MinigameModSettings.cs`, `BoardModSettings.cs`, `LightingWidgetLogic.cs`, `PostProcessWidgetLogic.cs` contain none [C] |
| Dirty flag? | The only writer is `EditorUndo.PushUndoEventToStack` (sets `m_isModDirty`, `EditorUndo.cs:58-62`); settings never push, so **settings edits do not set the dirty flag**, the mod does not prompt on exit, and the plugin must track unsaved settings edits itself (as it already does for player-settings triggers) | [C] |
| Which `Save()` step writes what? | `PummelMod.Save` order: `SaveMetaData` (Meta.json: preset names) -> `SaveWorkshopItemDetails` -> `Lighting.json` (**`CustomLighting`**, only if `UsesLighting`) -> `PostProcessing.data` (**`postProcess`, the active object**, only if `UsesPostProcessing`) -> `MainScene.scene` (if `UsesHierarchy`) -> `SaveModSpecificData` (Minigame: `GenerateMinigameControls`, `MinigameDefinitionData.json`, `ModSettings.json`; Board: extents recompute, `MapDetails.json`, `ModSettings.json`; Item: `ItemDetails.json`, `ModSettings.json`; BoardSpace: `ModSettings.json`) -> every external asset's own `Save()` | `PummelMod.cs:567-592` and the four `SaveModSpecificData` overrides [C] |
| `Preview.jpg` | written by `EditorSystem.SaveCurrentMod` only, after `Save()`; the plugin's `save_mod` goes through it | `EditorSystem.cs:808-820` [C] |
| Does entering play mode write them? | **Yes, all of the `Save()` files** (not `Preview.jpg`, and the dirty flag is not cleared): the `EnteringPlayMode` transition calls `CurrentMod.Save()` and then loads the mod from disk in the background for the playtest, so the playtest runs on the saved files, not on the in-memory objects. Skipped when the active mod's load type is `IngameTest`. | `EditorSystem.cs:1864-1876` [C]; background load from disk [I] |
| Other moments? | `DelayedSave` for a newly created empty mod (`EditorSystem.cs:889`); `RenameMod` writes `MinigameDefinitionData.json`/`MapDetails.json`, `Meta.json`, `WorkshopItem.json` at once (`PummelMod.cs:255-310`, `MinigameMod.cs:40-45`); the screenshot button/F10 writes `Textures/Screenshot<N>.jpg`; Item "Generate Icon" writes `Textures/ItemPreview.png`; the lighting profile buttons write outside the mod; closing/unloading nulls the minigame/board music and ambience references (`PummelMod.cs:750`, `MinigameMod.cs:174-179`), so nothing may be saved after an unload. There is no timer autosave. | [C] |
| Hazard on save: audio guid | `SimpleModControllerSettings.OnBeforeSerialize` recomputes both GUIDs from the cached clips; if a clip could not be resolved when `ModSettings.json` was loaded, the cache is null and **the guid is erased on the next save** | `SimpleModControllerSettings.cs:116-126` [C]; whether load order can leave the cache null: [U] |

---

## 5. Lighting and screen effects

### 5.1 Presets: where they live, how they are listed and selected

| Item | Lighting | Screen effects |
|---|---|---|
| Built-in folder | `<game>/StreamingAssets/LightingPresets/*.json` | `<game>/StreamingAssets/PostProcessPresets/*.data` |
| List shown in the combo | file names without extension, in `DirectoryInfo.GetFiles` order, then `"Custom"` appended last | same |
| Names | wiki lists 13: Day, Default, Evening, Forest, Hell, Maple, Night, Outer Space, Spring, Sunrise, Vaporwave, Watermelon, Winter ("13 built-in lighting templates", image captions) [I, not checked against file names] | wiki says "some preset screen effects"; names not documented [U] |
| Listing code | `LightingWidgetLogic.LoadLightingPresets` (`:42-50`) | `PostProcessWidgetLogic.LoadPostProcessPresets` (`:44-51`) |
| Selected preset stored in | `Meta.json` `SelectedLightingPreset`; `""`/null reads as `"Custom"` (`PummelMod.cs:326-333`); a new mod gets `"Default"` (`PummelMod.cs:175-176`) | `SelectedPostProcessingPreset`, same |
| Match between combo entry and object | **by the preset's internal name** (`ModLightingSettings.presetName` / `ModPostProcessSettings.PresetName` read from the file), not by file name | same. A file whose internal name differs from its file name is unreachable (lighting falls back to a default object, screen effects dereferences null) [I for the shipped files] |
| Free-text hazard | the combo for strings contains a text box (Enter accepts any typed string), so a name not in the list can be stored (`ModEditorGui.cs:1581-1600`). Effect of an unknown name: lighting -> default object + error log, and at the next load the missing file yields default lighting; screen effects -> null reference at selection time | [C] |
| Data per preset | the same classes as Custom; at load `lighting`/`postProcess` = a fresh object read from the preset file, or `CustomLighting`/`CustomPostProcess` when `Custom` | `PummelMod.cs:607-640` [C] |

### 5.2 What "Custom" means

- Selecting `Custom` makes `mod.lighting`/`mod.postProcess` point to `CustomLighting`/`CustomPostProcess` and shows the editors (Skybox, Global Light, Ambient Light, Cloud Shadows, Fog, Shadow Settings for lighting; the effect list for screen effects). With any other preset the editors are not drawn [C] (`LightingWidgetLogic.cs:75-81`, `PostProcessWidgetLogic.cs:80-86`).
- **Selecting a preset does not overwrite the custom values in memory** (`CustomLighting`/`CustomPostProcess` stay). What reaches the disk differs:
  - Lighting: `Lighting.json` is always the `CustomLighting` object, so custom values survive a save while a preset is selected [C] (`PummelMod.cs:571-574`).
  - Screen effects: `PostProcessing.data` is written from **`postProcess`, the active object**. With a built-in preset selected, saving writes the preset's values into `PostProcessing.data`; on the next load `CustomPostProcess` is read from that file, so the previous custom values are gone and "Custom" now shows the preset's values [C] (`PummelMod.cs:575-578`, `:633-640`). Effective rule: **to keep custom screen effects, save while `Custom` is selected.**
- Starting custom lighting from a preset: the "Import Built-in Lighting Profile" foldout loads a preset file into a new `CustomLighting` (the list there includes `Custom`; its file does not exist, which yields a default-valued object, i.e. a reset). Screen effects have no import feature, only the preset combo and the manual effect list [C].
- Lighting profiles exported/imported by name live outside the mod (see section 3) and are out of scope.

### 5.3 Lighting custom fields (`ModLightingSettings`, `Lighting.json`; JsonUtility JSON of the public fields, Colors as `{r,g,b,a}` floats)

Shown only when `LightingPreset == Custom` (and, as everywhere, only while the group is open). Write path for all: pub property of `mod.CustomLighting` (same names, capitalised; the underlying public field is lower-case and is what the JSON holds). Apply with `mod.lighting.ApplyLightingChangesToRenderSettings()` afterwards.

| Section | Field (UI label) | Type | Default | Shown when | Limits | Evidence (`LightingWidgetLogic.cs`) |
|---|---|---|---|---|---|---|
| Skybox | `stars` (Stars) | bool | false | always | - | `:86` |
| | `topColor`, `horizonColor`, `groundColor`, `haloColor` | Color | (97,171,255), (92,167,225), (41,64,82), (181,171,145) /255 | always | none (picker with alpha preview) | `:87-90` |
| | `haloExponent` | float | 6 | always | slider 0..32 | `:91` |
| | `haloStrength` | float | 1 | always | slider 0.1..4 | `:92` |
| Global Light | `globalLightEnabled` (Use Global Light) | bool | true | always | - | `:98` |
| | `globalLightRotation` (Sunlight Direction, Euler) | Vector3 | 130,25,0 | `globalLightEnabled` | none | `:102-104` |
| | `globalLightColor` | Color | (1, 0.957, 0.839, 1) | same | none | `:105` |
| | `globalLightIntensity` | float | 0.6 | same | none | `:106` |
| | `globalShadowItensity` (sic; Shadow Strength) | float | 1 | same | slider 0..1 | `:107` |
| | `globalLightShadows` (Sun Shadow Mode) | `UnityEngine.Rendering.LightShadows` None=0, Hard=1, Soft=2 | Soft | same | all 3 | `:108` |
| Ambient Light | `ambientMode` (Ambient Source) | `PummelAmbientMode` Skybox=0, Gradient=1, Color=3 | Skybox | always | exactly these 3 (value 2 is undefined) | `:116` |
| | `ambientIntensity` | float | 0.7 | mode Skybox | none | `:119-121` |
| | `ambientSkyColor`, `ambientEquatorColor`, `ambientGroundColor` | Color | (54,58,66), (29,32,34), (12,11,9) /255 | mode Gradient (all three) | none | `:122-126` |
| | `ambientSkyColor` labelled "Ambient Color" | Color | | mode Color (only this field) | none | `:127-129` |
| Cloud Shadows | `cloudShadows` | bool | false | `globalLightShadows != None` (else a hint text; the condition ignores `globalLightEnabled`) | - | `:136-139` |
| | `cloudSpeed1`, `cloudSpeed2` | float | 0.12, 0.1375 | `cloudShadows` and shadows on | none | `:141-142` |
| | `cloudIntensity` | float | 0.7 | same | slider 0..1 (applied as 1 - value) | `:143` |
| | `cloudStrength` (Cloud Sparseness) | float | 1 | same | slider 0..5 | `:144` |
| | `cloudScale` (Cloud Size) | float | 200 | same | none | `:145` |
| Fog | `fog` | bool | false | always | - | `:157` |
| | `fogColor` | Color | (0.5,0.5,0.5) | `fog` | none | `:160` |
| | `fogMode` | `UnityEngine.FogMode` Linear=1, Exponential=2, ExponentialSquared=3 | Linear | `fog` | all 3 | `:161` |
| | `fogEndDistance`, `fogStartDistance` | float | 300, 0 | `fog` and Linear | none | `:164-165` |
| | `fogDensity` | float | 0.01 | `fog` and not Linear | none | `:169` |
| Shadow Settings | `shadowDistance` | float | 200 | always | clamp 5..250 | `:177` |
| | `shadowCascades` | int via enum `ShadowCascades` Zero=0, Two=2, Four=4 | 4 | always | exactly 0, 2, 4 | `:178` |
| (hidden) | `version`, `presetName`, `shadowCascade4Split` (0.067,0.2,0.467), `shadowProjection` (StableFit), `shadowCascade2Split` (0.2), `defaultReflectionMode`, `reflectionIntensity` (1) | | | never | applied by `ApplyLightingChangesToRenderSettings` (shadow split/projection, reflection) but not editable in the UI | `ModLightingSettings.cs:21-97,619-641,687-694` |

Enum widgets assume a defined value: `ModEditorGui.StartEnum` builds the preview label from `Enum.GetName`, so an undefined stored value (e.g. `ambientMode` 2, `shadowCascades` 3) yields a null label [I: probably an exception per frame]; refuse undefined values.

### 5.4 Screen effects (`PostProcessing.data`, binary; `ModDataReaderWriter.cs:78-190` write, `:331-478` read V1, `:481-499` dispatch)

Format, version 1 [C]: little-endian .NET `BinaryWriter` stream: `uint16 version(=1)`, `string PresetName` (7-bit length-prefixed UTF-8), then nine optional blocks in this fixed order, each = `bool exists` followed (when true) by the block body: **AmbientOcclusion** (enabled, mode override flag, mode byte, intensity, thicknessModifier, radius, color, ambientOnly), **Bloom** legacy (enabled, intensity, threshold, softKnee, clamp, diffusion, anamorphicRatio, threshold *again*, color), **ComputeBloom** (enabled, HighQuality, BloomStrength, BloomThreshold, BloomUpsampleFactor, color), **ChromaticAberration** (enabled, intensity), **ColorGrading** (enabled, temperature, tint, colorFilter, hueShift, saturation, brightness, contrast, 9 mixer values), **DepthOfField** (enabled, focusDistance, aperture, focalLength, kernelSize override flag + byte), **Grain** (enabled, colored, intensity, size, lumContrib), **LensDistortion** (enabled, intensity, intensityX, intensityY, centerX, centerY, scale), **Vignette** (enabled, mode override flag + byte, color, center, intensity, smoothness, roundness, rounded). A "parameter" is `bool overrideState` + value (floats as float32, colors as 4 float32, Vector2 as 2 float32, bool as bool). Version 0 has a loader but the writer only emits 1; unknown versions load as an empty profile silently. A hand-written file is possible but unnecessary: the plugin edits the profile in memory and lets `Save()` write it.

The UI exposes (effects in this order; "Add Effect" menu offers AmbientOcclusion, **ComputeBloom labelled "Bloom"**, ChromaticAberration, ColorGrading, DepthOfField, Grain, LensDistortion, Vignette; legacy `Bloom` is drawn as "Bloom (Legacy)" only if a preset/file contains it and cannot be added; entries already present are greyed except Vignette) [C] (`PostProcessWidgetLogic.cs:80-141`):

| Effect | Controls (all: header checkbox = `enabled`; each parameter has an override checkbox, the value is editable only while it is on) | Slider (clamped to the field's `RangeAttribute`, else 0..10) vs free input | Forced values | Evidence |
|---|---|---|---|---|
| AmbientOcclusion | mode (enum ScalableAmbientObscurance, MultiScaleVolumetricObscurance), intensity, radius (mode Scalable) or thicknessModifier (other mode), color, ambientOnly | intensity, thicknessModifier slider; radius free | - | `:143-176` |
| Bloom (Legacy) | intensity, threshold, softKnee, clamp, diffusion, anamorphicRatio, color | softKnee, diffusion, anamorphicRatio slider; rest free | - | `:178-205` |
| Bloom (ComputeBloom) | HighQuality, BloomStrength ("Intensity"), BloomThreshold, BloomUpsampleFactor, color | BloomThreshold slider; rest free | - | `:207-232` |
| ChromaticAberration | intensity | free (no clamp even though the field has a range) | - | `:234-255` |
| ColorGrading | temperature, tint, colorFilter, hueShift, saturation, brightness, contrast, channel mixer 3 x 3 | all sliders | `gradingMode` forced to LowDefinitionRange (and override on) every frame | `:257-359` |
| DepthOfField | focusDistance, aperture, focalLength, kernelSize (enum KernelSize) | aperture, focalLength slider; focusDistance free | - | `:361-385` |
| Grain | colored, intensity, size, lumContrib | intensity, size, lumContrib slider | - | `:387-411` |
| LensDistortion | intensity, intensityX, intensityY, centerX, centerY, scale | all sliders | - | `:413-439` |
| Vignette | color, center (Vector2), intensity, smoothness, roundness, rounded | intensity, smoothness, roundness slider | `mode` forced to Classic every frame the header is open | `:441-468` |

Slider ranges come from `UnityEngine.Rendering.PostProcessing` attributes read by reflection at draw time (`PostProcessWidgetLogic.cs:517-547`). That package is not in the decompiled set; values below are from the upstream package and are [I], to be read by the same reflection at runtime instead of hard-coded: AO intensity 0..4, thicknessModifier 1..10; Bloom softKnee 0..1, diffusion 1..10, anamorphicRatio -1..1; ColorGrading temperature/tint/saturation/brightness/contrast -100..100, hueShift -180..180, mixers -200..200; DoF aperture 0.05..32, focalLength 1..300; Grain intensity 0..1, size 0.3..3, lumContrib 0..1; Lens intensity -100..100, intensityX/Y 0..1, centerX/Y 0..1 (upstream), scale 0.01..5; Vignette intensity 0..1, smoothness 0.01..1, roundness 0..1. `ComputeBloom`'s attributes are in a game or third-party assembly [U].

### 5.5 How the result reaches the scene, and the agent's view

- Lighting: only through `ApplyLightingChangesToRenderSettings` (section 3). It acts on the editor scene immediately (skybox, ambient, fog, sun, shadows).
- Screen effects: the editor volume `EditorSystem.m_postProcessVolume` (public) uses the active profile as `sharedProfile`; the scene-view camera's `PostProcessLayer` renders it; the toolbar's "Post Processing" button toggles that layer's `enabled` (`ToolbarWidget.cs:~118`). If the user has the layer toggled off, screen effects are invisible even though set [C].
- The agent has `screenshot` only (whole game window, PNG, includes the editor UI; `Tools/ScreenshotTool.cs`). Lighting is visible in the scene view; screen effects only when the layer is enabled; the playtest view shows both from the saved files after entering play mode.

---

## 6. Preview image

Three different images exist; they must not be confused.

| Image | What it is | Where stored | Referenced by | Who writes | In scope? |
|---|---|---|---|---|---|
| **Minigame/board presentation image** (the "Preview Image" under Minigame Details / Board Details; shown on the loading screen, ruleset and lobby) | a `Texture2D` asset of the mod (or a built-in one) | the texture file in `Assets/Textures/` (+ `.pmeta`); reference = guid in `MinigameDefinitionData.json` `ScreenshotTextureGuid` or `MapDetails.json` `PreviewImageGuid` | `MinigameDefinition.screenshotTexture` / `MapDetails.previewImage` | the picker (section 2.1/2.5), saved by `SaveModSpecificData` | **yes (A2)** |
| **Workshop thumbnail** | `ModWorkshopItem.PreviewImage` (guid `previewImageGuid`, External assets only) set in the Publish dialog ("Thumbnail Preview") plus `previewFiles` | `WorkshopItem.json` | upload code | the Publish dialog | **off limits** |
| **`Data/Preview.jpg`** | 640x360 JPEG render of the **scene-view camera** at the moment of the save; no data file references it. Used as the thumbnail in the mod list / new-from-template list and as the **fallback Workshop thumbnail when `workshopItem.PreviewImage` is null** | `Data/Preview.jpg` | `LoadModPopup.cs:65`, `NewModPopup.cs:46`, `WorkshopModInstaller.cs:1175-1181` | only `EditorSystem.SavePreviewScreenshot`, called by `SaveCurrentMod` (`EditorSystem.cs:819, 1363-1368`) | **not a settings field**; produced by the editor's save, never written directly by the plugin |

Facts [C unless noted]:
- Separation from the Workshop item: the minigame/board presentation fields live in `SerializableMinigameDefinition` / `SerializableMapDetails`, which have no title/description/tags/visibility/published-id/preview-file members. `MinigameDefinition.publishedFileId`/`isInternalMod` and the `MapDetails` equivalents are copied **from** the Workshop item on load (`MinigameMod.cs:141-147`, `BoardMod.cs:87-93`) and never written back. Wiki confirms: "not the preview image for the Steam Workshop, that is set in the Publish Mod Window" (`docs/md/Mod_Settings.md`, rows Description and Preview Image). So: Description and Preview Image here are writable; `WorkshopItem.json` (`title`, `description`, `language`, `metaData`, `visibility`, `tags`, `isInternal`, `previewFiles`, `publishedFileId`, `previewImageGuid`) stays unwritable. The Publish dialog also rewrites some Workshop fields when opened (`WorkshopPopup.cs:140-141`, tags cleanup `:488-533`); the plugin never opens it.
- Producing an asset from a screenshot, the editor's way: toolbar camera button (tooltip "Screenshot (F10)", `ToolbarWidget.cs:126`) or F10 (`EditorSystem.cs:1575`) -> `EditorSystem.TakeScreenGrab()` (public, `:1899`) sets a flag; at the end of the next frame `TakePlayModeScreenshot` (`:1437`) renders the scene-view camera (Editing state) into a **1920x1080** target with gizmos and grid hidden, saves **JPEG** as `Assets/Textures/Screenshot<N>.jpg` (first free N), and registers it with `CurrentMod.AddAsset(path, "", copy: false)`. It is **not** assigned to any field; the user then picks it as Preview Image. A plugin screenshot route for A2 is therefore: call `TakeScreenGrab`, wait a frame, find the new `Screenshot<N>` texture asset (same listing as `list_assets`), assign `screenshotTexture`/`previewImage`. The plugin's own `screenshot` tool is a full-window capture incl. the ImGui panels and is a poor source for an in-game image. Aspect: the in-game image is shown as a sprite of its own aspect; the editor's captures are 16:9 [I].
- The "preview camera" classes (`ModPreviewCamera`, 768x432 render texture) are only the small live camera picture inside the Camera foldouts; unrelated to `Preview.jpg` and the presentation image (`ModSettings.cs:31-57`, `ModPreviewCamera.cs`).
- Validator warnings reference this field: minigame "Minigame Details > Preview Image", board "Board Details > Preview Image" (section 8).
- Imported texture route (A2 second half) belongs to Part B (asset import); this section only fixes the target field: the picker accepts any `Texture2D` asset known to the current mod or the built-in list; the callback hands the asset's Unity texture object to the field.
- Items use `iconTexture` the same way (`ItemDetails.json` `IconTextureGuid`); no Workshop relation there either.

---

## 7. Music and ambience

| Aspect | Minigame | Board |
|---|---|---|
| Reference | AudioClip asset by guid: private `MusicGUID`/`AmbienceGUID` in `ModSettings.json`, exposed as props `Music`/`Ambience` (clip object cached, guid recomputed) | `MapDetails.music`/`ambience` fields (clips); guids `MusicGuid`/`AmbienceGuid` in `MapDetails.json`, computed at save |
| Built-in vs custom | both: picker location `Internal | External`; list = the mod's assets plus the game's internal assets (`ChooseAssetPopup.FilterAssets`, `ModAsset.Filter`); asset type must be `AudioClip` (or a subclass); "None" clears it | same |
| Defaults for a new mod | built-in music/ambience guids (`13dbf0ac-...`, `90abde3b-...`), volumes 0.3 / 0.5 | built-in guids set in the `BoardModSettings` constructor (`3ac2bff4-...`, `05f5b5c8-...`), volumes 0.3 / 0.5 |
| Volume limits | slider 0..1 for both | slider 0..1 for both |
| Choice list | built-in assets: see `list_assets` with `includeBuiltIn` and type `AudioClip`; plugin result `location` field says internal/external | same |
| Runtime | minigame controller reads `Music`, `MusicVolume`, `Ambience`, `AmbienceVolume` at start (`SimpleModController.cs:109-112`); looping per wiki | board scene |
| Validator | none for minigames | warns when a clip is set and volume <= 0.1 (music) / <= 0.05 (ambience) (`ModBoardValidator.cs:40-47`) |

[C] for all rows except the "looping" remark (wiki). Setting a clip that is not a registered asset stores an empty guid (minigame prop setter and board save both look the guid up through the asset registry).

---

## 8. Validator rules that concern settings (`validate_mod` surfaces them)

| Rule | Severity | Message | Condition | Evidence |
|---|---|---|---|---|
| Empty minigame description | warning | "Empty minigame description." (hint: Minigame Details > Description) | `description` null/empty | `ModMinigameValidator.cs:24-27` |
| Missing minigame preview | warning | "Missing minigame preview image." (hint: Minigame Details > Preview Image) | `screenshotTexture == null` | `:28-31` |
| Rounds | **error** | "Number of rounds must be greater than zero" | `NumberOfRounds <= 0` (the UI clamps to 1..20 only while drawn) | `:37-40` |
| Respawn vs last man standing | warning | "Respawns are enabled with Last Man Standing end condition." | `EndConditions == RemainingPlayersAlive` exactly (no other flag) and `respawnEnabled` | `:41-44` |
| Empty board description | warning | "Empty board description." | `MapDetails.description` empty | `ModBoardValidator.cs:32-35` |
| Missing board preview | warning | "Missing board preview image." | `previewImage == null` | `:36-39` |
| Music very quiet | warning | "You have music set but the volume is very low." | `music != null && musicVol <= 0.1` | `:40-43` |
| Ambience very quiet | warning | "You have ambient sound set but the volume is very low." | `ambience != null && ambienceVol <= 0.05` | `:44-47` |

Not validated: lighting, screen effects, end conditions `0`, points/duration ranges, camera mode vs spawns, `MinPlayersAlive`, board camera values, extents. The remaining validator rules (spawns, board graph) are not settings. [C]

---

## 9. Entry points for the plugin

Start: `EditorSystem.Instance.CurrentMod` (a `PummelMod`; `mod.GetModSettings()`), after the existing guards (edit state, loaded sandbox mod, no dialog). Cast `MinigameMod` -> `MinigameSettings` / `GetMinigameDefinition()`, `BoardMod` -> `GetMapDetails()` and `(BoardModSettings)GetModSettings()`.

| Section | Mod types | Object to reach | Read / write members | Call after a write | `get_open_mod` today (`Tools/StatusTools.cs:114-148`) |
|---|---|---|---|---|---|
| Minigame details: description, preview | Minigame | `((MinigameMod)mod).GetMinigameDefinition()` (`MinigameDefinition` ScriptableObject) | pub fields `description`, `screenshotTexture` (+ optional `screenshotTextureGuid`); name read-only | none; (optional) set `screenshotTextureGuid` | **not returned** (definition is not a field of the settings object) |
| Minigame settings | Minigame | `((MinigameMod)mod).MinigameSettings.SimpleMinigameSettings` | pub fields of `SimpleModControllerSettings` | none, except `MinigameSettings.GenerateMinigameControls()` after `ChosenPlayerType` | returned (`settings.SimpleMinigameSettings`, all 14 serialized fields incl. `MusicGUID`) |
| Player settings | Minigame | `MinigameSettings.SimpleModPlayerSettings` | pub fields of `SimpleModPlayerSettings`; triggers already handled | `GenerateMinigameControls()` if `canJump`/`canPunch`/type changed (cosmetic); force `topDownCameraMode = Static` for ThirdPerson | returned (all 48 fields; triggers in full) |
| Music & ambience | Minigame | `SimpleMinigameSettings` | props `Music`, `Ambience`; fields `MusicVolume`, `AmbienceVolume` | none | guid strings (`MusicGUID`, `AmbienceGUID`) and volumes; clip names are not returned |
| Board details | Board | `((BoardMod)mod).GetMapDetails()` (`MapDetails`) | pub fields `description`, `previewImage`, `defaultGoalChestCount`, `supportsWeaponCache`, `useCustomChestShuffleLocation`, `chestShuffleLocation`, `mapExtents`, `music`, `ambience`, `musicVol`, `ambienceVol`; `mapName` read-only | none | **not returned** |
| Board settings | Board | `(BoardModSettings)mod.GetModSettings()` -> `CameraSettings`, `CustomSpaceSettings`, field `UseCustomBoardSize` | pub fields; `CustomSpaceSettings.CustomSpacePrefabs` prop (array) | on `CameraMode` change reset `CameraDistance`; after custom spaces: raise `OnCustomSpacesUpdated` | returned (`CameraSettings`, `CustomSpaceSettings` (references), `UseCustomBoardSize`) |
| Item details | Item | `((ItemMod)mod).GetItemDetails()` (`ItemDetails`) | pub fields/props listed in 2.6 | none (never call the Generate Icon path) | not returned (`ItemModSettings` holds only `Version`) |
| Lighting preset | Minigame, Board (BoardSpace has data but no UI) | `mod` (`SetSelectedLightingPreset`, `lighting`, `CustomLighting`) and the widget's preset list (needs `settings.SetupLightingWidget()`, then the private `m_lightingWidgetLogic` -> `m_presetNames`, `m_lightingPresets` by reflection) | `GetSelectedLightingPreset()`; assign `mod.lighting` | `mod.lighting.ApplyLightingChangesToRenderSettings(true)` | name only (`lightingPreset`, `metadata.SelectedLightingPreset`); preset list **not** returned |
| Custom lighting | same | `mod.CustomLighting` (must be the active object, i.e. preset == Custom) | props of `ModLightingSettings` | `mod.lighting.ApplyLightingChangesToRenderSettings(true)` | **not returned** |
| Screen-effect preset | same | `mod` (`SetSelectedPostProcessingPreset`, `postProcess`), `EditorSystem.m_postProcessVolume`, widget list (`m_postProcessWidgetLogic` -> `m_presetNames`, `m_postProcessPresets`) | assign `mod.postProcess`, `volume.sharedProfile` | toggle `EditorSystem.m_sceneViewCamera`'s `PostProcessLayer.enabled` off then on | name only |
| Custom screen effects | same | `mod.CustomPostProcess.GetPostProcessProfile()` (`PostProcessProfile`; `TryGetSettings<T>`, `AddSettings<T>`, `RemoveSettings`, effect fields are `ParameterOverride`s) | `enabled.value`, `<param>.overrideState`, `<param>.value` | layer toggle after add/remove/override flips; values are live | **not returned** |
| Save | all | `EditorSystem.SaveCurrentMod()` (existing `save_mod`) | | | |

Read-side gaps to close before the write side ("same shape in and out"): description, preview image (as `{guid,name,...}` reference like `UnityObjectRef`), board `MapDetails` values, music/ambience as references instead of guid strings, the presets (selected + available), custom lighting values (`ModLightingSettings` public fields), custom screen-effect state (per effect: present, enabled, per parameter overrideState + value), derived/hidden markers per field. The existing generic `SerializedFields(settings)` is a reflection dump and cannot be the contract for sections that live in other objects (definition, `MapDetails`, `CustomLighting`, `CustomPostProcess`).

---

## 10. Risks and gotchas

1. **Clamps are not in the data classes.** Written outside the open window nothing clamps: `NumberOfRounds` 0 is a validator error, a negative `movementSpeed`, `health` 0, `shadowDistance` 1000 etc. persist. Runtime only re-clamps rounds, duration, points. The plugin must apply the table limits itself and report them (same contract as `InspectorRules`). [C]
2. **`PostProcessing.data` is written from the active object**: switching a mod to a preset and saving discards the custom screen effects on disk (lighting does not have this problem). An agent that sets a preset and then wants custom values later must switch to Custom before saving, or accept the loss. Recommend refusing "preset" when the custom profile holds effects unless the caller passes an explicit flag. [C]
3. **Never mutate preset objects**: while a preset is selected `mod.lighting`/`mod.postProcess` are the widget's cached preset objects. Edit only `CustomLighting`/`CustomPostProcess`; with a preset selected the editors are hidden, so refuse custom writes unless preset == Custom (UI parity), or offer an explicit "switch to Custom" step. [C]
4. **Preset names**: validate against the real list; the string combo accepts free text; an unknown or mismatching name gives a null dereference (screen effects) or a default lighting object. [C]/[I]
5. **Immediate disk writes by UI buttons** (never call): lighting profile export/import/delete (persistent data folder, `File.Delete`), Item "Generate Icon", rename popup (renames the folder and rewrites `Meta.json`, `WorkshopItem.json`, definition/map details), screenshot (adds an asset file). [C]
6. **Workshop item**: `Save()` rewrites `WorkshopItem.json` from memory every time (full rewrite, not touched by settings). `Preview.jpg` is a Workshop fallback thumbnail written by the editor's save. Neither is a settings target. The only settings-adjacent write of `WorkshopItem.json` is `RenameMod`. [C]
7. **Shared static state**: `ModLightingSettings.m_directionalLight` (one light for all instances), `RenderSettings`/`QualitySettings` (process-wide), `BoardModSettings.OnCustomSpacesUpdated` (static event). Every `new ModLightingSettings()` allocates a skybox material and loads an addressable synchronously; the editor leaks the replaced object on "Import built-in", and `Release()` destroys the material, so a plugin must not create throw-away instances in a loop and must not release objects the mod still references. [C]
8. **Minigame vs board differences**: minigame music is a property pair over GUIDs inside `ModSettings.json`, board music is plain fields in `MapDetails` (guid at save); board has `UseCustomBoardSize`/extents recomputed at save; board camera mode change resets distance; BoardSpace has no UI but saves lighting data; Item has neither lighting nor screen effects and ignores a loaded `ModSettings.json`. [C]
9. **Follow camera without a spawn throws** inside the settings window each frame (`topDownCameraMode = Follow`, no `ModPlayerSpawn`). [C]
10. **Board custom spaces are index-based**: reorder/remove shifts existing spaces; also `OnCustomSpacesUpdated` is only raised by the window's draw code. [C]
11. **Audio guid loss** on save if a clip failed to resolve at load; and unload nulls the audio references. [C] / load-order part [U]
12. **Enum safety**: undefined stored enum values (ambient mode 2, shadow cascades 1/3, name tag 0/4) are mishandled or rewritten by the widgets; refuse them on write. [C]/[I]
13. `PostProcessing.data` quirks: legacy Bloom writes `threshold` twice and reads it twice (consistent); an effect whose override flag is off is stored but ignored by the renderer (values are meaningful only with `overrideState` true); `FloatParam` limits for fields without `RangeAttribute` fall back to 0..10 when the control is a slider. [C]
14. Entering play mode saves everything first (section 4), so a half-finished settings edit is persisted by a playtest. [C]

---

## 11. Open questions / to verify in the game

1. Exact preset names and whether each preset's internal `presetName`/`PresetName` equals its file name (lighting: a file without `presetName` reads as `Custom`; screen effects: unmatched name means null). Read the two preset folders (needs the running game or a file listing the author provides) and check `get_open_mod.lightingPreset` after selecting each.
2. Whether `StreamingAssets/LightingPresets/Custom.json` or `PostProcessPresets/Custom.data` exist (would duplicate the `Custom` entry).
3. `RangeAttribute` values of the PostProcessing fields and of `ComputeBloom` (read by reflection in the plugin; compare with the upstream list in 5.4).
4. Behaviour of `ModEditorGui.StartEnum` with an undefined enum value (exception per frame or blank label) and of `InputColorPicker4` with values above 1 (HDR) or alpha 0.
5. Whether the settings window must be closed/open for a plugin write to be shown the same frame; and whether writing while the group is expanded and clamping fights are visible (needs one in-game test with a sandbox mod).
6. Load order of audio: can `MusicGUID` deserialize before the mod's own audio assets are registered, causing the guid erase on next save? (Test: a sandbox mod with its own music asset, save, reopen, save, inspect `ModSettings.json`.)
7. Does the playtest really run from the freshly saved files (background load) and so see lighting/screen effects/settings from disk only? (Test: change a value, enter play mode, compare.)
8. What happens to `Follow` camera mode without spawn (exception text in the log, whether the editor stays usable).
9. `TakeScreenGrab` in Editing state: does the 1920x1080 render include post-processing, and does the asset appear in `CurrentMod.Assets` in the same frame it is added (listing latency for the A2 flow)? Is the aspect/size of a presentation image constrained anywhere (loading screen layout)?
10. Raising `BoardModSettings.OnCustomSpacesUpdated` from the plugin (event backing field via reflection) versus leaving it to the next window draw; and what happens to board nodes when the custom space list changes while the window is closed.
11. Whether adding the same effect twice via the Vignette menu entry throws in the editor (Vignette is not greyed when present).
12. `ModSettings.json` of a Minigame mod when `SimpleModControllerSettings` loads with a missing `MusicGUID` key (defaults to the built-in track via the field initialiser, but JsonUtility behaviour for missing keys on nested objects should be checked once).

---

## 12. Corrections found while building the tools (plugin 0.8.0)

- Slider ranges of the screen effects were read from the game's `RangeAttribute`s (all verified): LensDistortion `centerX/centerY` are -1..1 (section 5.4 guessed 0..1); ComputeBloom `BloomThreshold` is 0..2 and `BloomUpsampleFactor` has 0..1 but is drawn as a free input (not clamped); ChromaticAberration `intensity` has 0..1 but is a free input. The drift test compares the documented ranges with the attributes.
- `ComputeBloom` lives in `Unity.Postprocessing.Runtime`, not in a game assembly (question 3 in section 11).
- `NumberOfRounds` 0 cannot be produced by the UI (it clamps to 1..20), so the plugin clamps it and reports it instead of writing a validator error.
- Custom board spaces are read-only in the tools (risk 10): the update event is raised only by the settings window.
