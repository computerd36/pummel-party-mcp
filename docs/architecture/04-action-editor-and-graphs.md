# 04 - Action Editor and logic/graph model

Recon of the decompiled `Assembly-CSharp` (read-only). Evidence is given as `File.cs:line` (root folder) or `ModSystem.Logic/File.cs:line`.
Convention: **[C]** = confirmed from code, **[I]** = inferred, **[?]** = unverified. No game code is reproduced here.

## 0. Summary

* There are two unrelated systems.
  * (a) **Action/event system** (`ModSystem.Logic`): flat, ordered lists of polymorphic `ModAction` objects hung on `ModLogicTrigger` objects, which are members of mod components. This is what the shipped mod editor's "Action Editor" window edits. **Live, fully reachable.** [C]
  * (b) **Node-graph "visual scripting"** (`VisualScripting\*`, `PummelVisualScript` `.pgf` assets, `ScriptGenerator`, Roslyn). Data model, asset importer and code generator exist, but **no editor window, menu entry or caller opens/creates graphs, and Roslyn compilation has no caller**. Dormant / dev-leftover in this build. [C, absence of callers by whole-tree grep]
* The two systems do not reference each other. [C]
* The Action Editor is a thin ImGui view. Every edit is an in-place mutation of a `ModLogicTrigger` (its `Actions` array or an action's own fields). There is **no model-level edit API, no undo call and no dirty call** inside the window. Undo and dirty tracking come from a global snapshot-diff of the *selected* GameObjects (section 3.4).

## 1. Data model of (a)

### 1.1 Where actions/triggers live

* Actions never live on a component directly. They live in `ModLogicTrigger.m_actions` (`ModAction[]`, `[SerializeReference]`, `ModLogicTrigger.cs:13-14`), exposed as `Actions` (get/set, `:44-53`). [C]
* `ModLogicTrigger` is a plain `[Serializable]` class (not a Unity Object). Subclasses fix the `ModTriggerType` in their constructor and carry trigger-specific params. [C]
* Component -> trigger storage (found by grep for `ModLogicTrigger`):

| Component (class) | Mod types | Trigger members | Evidence |
|---|---|---|---|
| `ModLogic` ("Logic") | Minigame, Map (board) | `Triggers` : `ModLogicTrigger[]` (private `m_triggers`, `[SerializeReference]`); user adds/removes freely | `ModLogic.cs:5-20,24-39` |
| `ModTrigger` ("Trigger") | Minigame | `OnHitActions`, `OnEnterActions`, `OnExitActions`, `OnStayActions` (4 fixed plain `ModLogicTrigger`) + bools `TriggerOnHit/Enter/Exit/Stay` | `ModTrigger.cs:8-60`, `ModTriggerInspector.cs:36-75` |
| `ModBoardSpaceEvent` | Board space | `OnEnterTrigger`, `OnPassTrigger` (typed `BoardSpaceTrigger`, setter casts) | `ModBoardSpaceEvent.cs:14-47` |
| `ModDestructible` ("Hittable") | - | `OnHitActions` | `ModDestructibleInspector.cs:10` |
| `ModFireWeapon` ("Weapon") | Minigame | `WeaponHitTrigger` | `ModFireWeapon.cs:20,66` |
| `ModItem` ("Item") | Minigame | `OnPickupTrigger`, `OnDropTrigger`, `OnUseTrigger`, `OnWhileHeldTrigger` | `ModItem.cs:26-35,457-493` |
| Minigame player settings (`SimpleModPlayerSettings`) | Minigame | `weaponHitTrigger`, `tickTrigger`, `onDeathTrigger`, `onRespawnTrigger`, `onFirstSpawnTrigger`, `onRoundEndTrigger` (public fields) | `SimpleModPlayerSettings.cs:88-94`, `MinigameModSettings.cs:314-358` |
| `BoardPopupAction.m_choices[i].OnSelectTrigger` (nested, 1.6) | Board | one per popup choice | `InteractionPopupChoice.cs:25,45-55` |

* Only `ModLogic` has user-addable triggers; elsewhere trigger slots are fixed members. [C]
* Triggers are addressed at runtime **by index** (`ModLogic` array index; fixed indices elsewhere, e.g. board space enter=0, pass=1, `ModBoardSpaceEvent.cs:55-60`). Re-ordering/removing `ModLogic` triggers re-indexes them on next `Setup`. [C]

### 1.2 Class hierarchy

* `ModAction` (`ModAction.cs`): `[Serializable]`; serialized `m_type` (`ModActionType`) and `m_version` (short; used by `GiveItemAction` / `SetPlayerVisualAction` to upgrade old data). Virtuals: `ActionRunMode`, `GetExecutionTime(Random)`, `DrawInspector()`, `Execute(ModEvent)`, `IsCoroutine()`, `IsRelayed()`. Non-serialized runtime flags `HasStarted` / `IsFinished`. [C]
  * `TargetAction : ModAction` adds `m_targetFlags` (`ModActionTargets`) and `m_targets` (`ModAssetReference<Component>[]`). Most actions derive from it (`TargetAction.cs`). [C]
    * `BoardPlayerTargetAction : TargetAction`: ctor sets `TargetFlags = Source`; resolves board players from `BoardNode` / `ModBoardSpace` / `ModBoardPlayer` targets. Base of the board-logic actions. [C]
  * `WaitAction` and `SetGravityAction` derive directly from `ModAction` (no targets). [C]
* `ModLogicTrigger` and subclasses: see 1.4.

### 1.3 Action types: enum -> class -> display name -> category (35 actions)

Enum->class map: `ModAction.GetActionClassType` (`ModAction.cs:97-197`, a switch; `None` maps to bare `ModAction`). Display name / category / mod type / usage flags: class attribute `[ModActionMeta(...)]` (`ModActionMeta.cs:6-35`). The registry `ModActionMeta.GetAllActions()` is filled by reflection over the whole assembly at `RuntimeInitializeOnLoad(BeforeSceneLoad)` (`ModActionMeta.cs:74-95`). Enum values are persisted ushort IDs. "Board" = `PummelModTypeFlags.Map`. Run mode column = `ActionRunMode` override (`-` = default `All`).

| Enum (value) | Class | Base | UI name | Category | Mod type | Usage flag | Run mode |
|---|---|---|---|---|---|---|---|
| None (0) | `ModAction` | - | (not listed) | - | - | - | All |
| Wait (32) | `WaitAction` | ModAction | Wait | Utility | All | None | - (coroutine; runner special-cases it) |
| Kill (64) | `KillAction` | TargetAction | Kill/Destroy | Utility | All | None | - |
| ChangeHealth (96) | `ChangeHealthAction` | TargetAction | Change Health | MinigameLogic | Minigame | None | - |
| ChangeScore (128) | `ChangeScoreAction` | TargetAction | Change Score | MinigameLogic | Minigame | None | - |
| SpawnEffect (160) | `SpawnEffectAction` | TargetAction | Spawn Effect | Visuals | All | None | Origin |
| Scale (256) | `ScaleAction` | TargetAction | Scale | Movement | All | None | ServerRelay |
| SetColor (288) | `SetColorAction` | TargetAction | Set Color | Visuals | All | None | ServerRelay |
| SetActive (320) | `SetActiveAction` | TargetAction | Set Active | Utility | All | None | ServerRelay |
| PlayTweenAnimation (352) | `PlayTweenAnimationAction` | TargetAction | Play Tween Animation | Movement | All | None | ServerRelay |
| Position (384) | `PositionAction` | TargetAction | Position | Movement | All | None | ServerRelay |
| Rotation (416) | `RotationAction` | TargetAction | Rotation | Movement | All | None | ServerRelay |
| SetGravity (448) | `SetGravityAction` | ModAction | Set Gravity | MinigameLogic | Minigame | None | ServerRelay |
| SetPlacement (480) | `SetPlacementAction` | TargetAction | Set Placement | MinigameLogic | Minigame | None | - |
| ModifyPlayer (512) | `ModifyPlayerAction` | TargetAction | Modify Player | MinigameLogic | Minigame | None | - |
| PlaySound (544) | `PlaySoundAction` | TargetAction | Play Sound | Utility | All | None | - |
| SpawnPrefab (576) | `SpawnPrefabAction` | TargetAction | Spawn Prefab | Utility | All | None | ServerRelay |
| HealPlayer (608) | `HealPlayerAction` | BoardPlayerTargetAction | Heal Player | BoardLogic | Board | BoardSpaceOnly | - |
| DamagePlayer (640) | `DamagePlayerAction` | BoardPlayerTargetAction | Damage Player | BoardLogic | Board | BoardSpaceOnly | - |
| GiveKeys (672) | `GiveKeysAction` | BoardPlayerTargetAction | Give Keys | BoardLogic | Board | BoardSpaceOnly | - |
| DropKeys (704) | `DropKeysAction` | BoardPlayerTargetAction | Drop Keys | BoardLogic | Board | BoardSpaceOnly | - |
| GiveTrophy (736) | `GiveTrophyAction` | BoardPlayerTargetAction | Give Trophy | BoardLogic | Board | BoardSpaceOnly | - |
| GiveItem (768) | `GiveItemAction` | BoardPlayerTargetAction | Give Item | BoardLogic | Board | BoardSpaceOnly | - |
| SpawnKeys (800) | `SpawnKeysAction` | TargetAction | Spawn keys | BoardLogic | Board | BoardSpaceOnly | - |
| ChangeVelocity (832) | `ChangeVelocityAction` | TargetAction | Change Player Velocity | MinigameLogic | Minigame | None | - |
| MoveSpace (864) | `MoveSpaceAction` | BoardPlayerTargetAction | Move To Space | BoardLogic | Board | BoardSpaceOnly | - |
| ShowMessage (1024) | `ShowMessageAction` | TargetAction | Show Message | Visuals | All | None | - |
| GiveMinigameItem (1056) | `GiveMinigameItemAction` | TargetAction | Give Minigame Item | MinigameLogic | Minigame | None | ServerRelay |
| StunPlayer (1088) | `StunPlayerAction` | TargetAction | Stun Player | MinigameLogic | Minigame | None | - |
| DropMinigameItem (1120) | `DropMinigameItemAction` | TargetAction | Drop Minigame Item | MinigameLogic | Minigame | None | ServerNoRelay |
| MatchTransform (1184) | `MatchTransformAction` | TargetAction | Match Transform | Movement | All | None | ServerRelay |
| SetPlayerVisual (1248) | `SetPlayerVisualAction` | TargetAction | Set Player Visual | Visuals | Minigame | None | ServerRelay |
| SetConnectionActive (1280) | `SetConnectionActiveAction` | TargetAction | Set Connection Active | BoardLogic | Board | BoardSpaceOnly | ServerRelay |
| SetBoardSpaceActive (1312) | `SetBoardSpaceActiveAction` | TargetAction | Set Board Space Active | BoardLogic | Board | BoardSpaceOnly | ServerRelay |
| BoardPopup (1344) | `BoardPopupAction` | BoardPlayerTargetAction | Display Popup | BoardLogic | Board | BoardSpaceOnly | - (runner special-cases it) |
| RemoveTrophy (1376) | `RemoveTrophyAction` | BoardPlayerTargetAction | Remove Trophy | BoardLogic | Board | BoardSpaceOnly | - |

Notes [C]:
* The stored UI string is prefixed with an icon glyph (`EditorIcons.GetIconString`, `ModActionMeta.cs:34`); use the plain names above, and `GetActionMeta(type).HelpText` for descriptions.
* `ActionCategory`: Other=0, Visuals=1, MinigameLogic=2, BoardLogic=4, Movement=8, Utility=16 (`ActionCategory.cs`). The palette groups by category.
* `GetActionClassType` logs an error and returns null for any enum value outside this table.
* `None` is constructible (bare `ModAction`) but has no meta, so it is not offered in the palette.

### 1.4 Triggers and events

Trigger types (`ModTriggerType`, ushort IDs; class from `ModLogicTrigger.GetTriggerClassType`, `ModLogicTrigger.cs:207-240`). Hide lists in `ModLogicInspector.cs:7-9` decide which types the `ModLogic` "Event" dropdown offers.

| Enum (value) | Class | Serialized params | Fires when | Logic dropdown: Minigame | Board |
|---|---|---|---|---|---|
| None (0) | `ModLogicTrigger` | - | never; used as a plain action holder (Trigger, Item, popup choice ...) | hidden | hidden |
| Start (32) | `StartTrigger` | - | `OnStart` (first server Update once players ready) | yes | yes |
| Enable (40) | `OnEnableTrigger` | - | `OnEnable` (object (re)enabled) | yes | yes |
| Update (48) | `UpdateTrigger` | - | every frame | yes | yes |
| Hit (64) | `HitTrigger` | `m_hitType` : `HitType` flags (CollisionEnter=1, Stay=2, Exit=4) | `OnEvent` of `ModEventType.Hit` with matching flag | yes | hidden |
| WeaponHit (80) | `WeaponHitTrigger` | `m_hitType` : `WeaponHitEventType` flags (Melee=1, Projectile=2) | `OnEvent` WeaponHit | yes | hidden |
| Timer (96) | `TimerTrigger` | `m_interval` : `ModComplexFloat` | accumulator reaches interval | yes | yes |
| BoardSpace (112) | `BoardSpaceTrigger` | `m_hitType` : `HitType` | board-space land / pass events | hidden | hidden |
| RoundStart (128) | `RoundStartTrigger` | - | subscribes in `OnEnable` | yes | hidden |
| RoundEnd (144) | `RoundEndTrigger` | - | subscribes in `OnEnable` | yes | hidden |

Hide lists: minigame hides {0,112}; board hides {0,112,64,80,128,144} [C]. Other components offer no type dropdown.

Event types (`ModEventType`; payload classes derive from `ModEvent`, which carries `Source`/`Receiver` (`IEventReceiver`), `Seed`, `CurrentActionIndex`, `IsOrigin`; `ModEvent.cs`): None=0, Hit=32 (`HitEvent`), WeaponHit=48 (`WeaponHitEvent`), SpawnerSpawnPrefab=64 (`SpawnerSpawnPrefabEvent`), WeaponFire=96 (`WeaponFireEvent`), WeaponStop=112, ConfirmWeaponHit=128 (`ConfirmHitEvent`), PlayerDeath=144, PlayerRespawn=160, PlayerFirstSpawn=176, SpawnPrefabAction=192 (`SpawnPrefabActionEvent`), BoardPopupChoice=208, RoundEnd=224. Raised from `SimpleModPlayer.cs:1424-2317`, `ModFireWeapon.cs:165`, `ModItem.cs:1023`, `BoardPopupActionInteraction.cs:99`. Events are runtime-only payloads and are not authored. [C]

"Receiver"/"Source" display names ("This Object"/"Activator" etc.) are passed per call site into the window (section 4); they only relabel the UI. [C]

### 1.5 Validity per mod type / context

* Gate 1 (mod type): `EditorSystem.CanUseItem(meta.ModTypes)` (`EditorSystem.cs:2707-2717`) switches on `CurrentMod.ModType`: Board -> Map flag, Minigame -> Minigame flag, Item -> Item flag, BoardSpace -> BoardSpace flag. No action sets the Item/BoardSpace flags specifically; those mod types see only `All` actions. [C]
* Gate 2 (usage flags): `m_usageFlags.HasFlag(meta.UsageFlags)` (`ActionEditorWindow.cs:199`). `None` is always satisfied; `BoardSpaceOnly` actions appear only when the opening call site passed `BoardSpaceOnly`, i.e. the Board Space Event's two triggers and Display Popup choice triggers (`ModBoardSpaceEventInspector.cs:10,14`, `BoardPopupAction.cs:227`). A Logic component in a board mod never offers Heal/Damage/Give*/Move/Popup. [C]
* Both gates are enforced **only in the palette** (`GetAvailableActionTypes`, `ActionEditorWindow.cs:193-209`). Nothing in the model validates; a plugin can add any action to any trigger and the game will serialize it. Runtime tolerance of mismatches is **[?]**. A plugin should replicate gates 1 and 2.
* Gate 3 (UI only): `TargetAction` hide flags (`GetHideFlags`, static `ActionEditorWindow.HideSourceFlag`) hide the Source toggle where no source exists. [C]

### 1.6 Nesting

* Action lists are **flat**; there is no generic if/loop/group action. [C]
* The only nesting: `BoardPopupAction` holds `m_choices` (`List<InteractionPopupChoice>`), each with an `OnSelectTrigger` (`ModLogicTrigger`) that has its own action list (`InteractionPopupChoice.cs:25,45-55`). The choice class also keeps an older `m_actions` array (`:22,96-100`), apparently legacy [I].
* Breadcrumb trail: a static history stack of `ActionEditorBreadcrumb` objects, each wrapping the `ActionWindowTriggerInfo` of one trigger (`ActionEditorBreadcrumb.cs`, `ActionEditorBreadcrumbTrail.cs`). Top-level opens pass `clearHistory:true`; the popup choice's "On Select" passes `clearHistory:false` so it pushes a crumb (`BoardPopupAction.cs:227`). Clicking an earlier crumb truncates the trail and re-`Show`s that trigger with `addToHistory:false` (`ActionEditorBreadcrumbTrail.cs:103-122`). Pure UI navigation; nothing persisted. [C]
* Runtime: a popup action blocks the following actions until the popup and its nested list are done (`ModActionRunner.cs`, `IsPreviousActionStillRunning`). [C]

## 2. Action parameters

* Parameters are **private `[SerializeField]` instance fields** on each action class (Unity `JsonUtility` serializable) with C# initializers as defaults. There is no per-parameter attribute; class-level attributes are `[Serializable]`, `[ModWikiPageURL]`, `[ModActionMeta]`. Public properties exist for only a few (`WaitAction.Seconds`, `TargetAction.Targets/TargetFlags/TargetReferences`). A plugin must use **reflection on private fields** or a JSON overlay. [C]
* Each action's inspector is hand-written in its `DrawInspector()` override (ImGui widgets writing the fields); there is no declarative schema, so the parameter list below was read from the field declarations. [C]
* Types in use: int, float, bool, string, Vector3, Color, enums, flag enums (`ModGiveItemMask`, `TransformValues`), `ModComplexFloat`, `ModAssetReference<T>` (and arrays), `List<ConnectionMapping>`, `List<InteractionPopupChoice>`, `DamageInstance`.

### 2.1 Fields per action (besides inherited targets)

| Action | Fields (defaults where non-trivial) |
|---|---|
| Wait | `m_seconds` ModComplexFloat (Constant, min 1, max 2; inspector clamps 0..3600) |
| Kill, SetPlacement | none |
| ChangeHealth / ChangeScore | `m_operation` ModActionOperation (Set, Add, Subtract, Multiply, Divide), `m_value` int |
| SpawnEffect | `m_effectType` SpawnEffectType (Blood) |
| Scale | `m_operation`, `m_scale` Vector3 (1,1,1) |
| SetColor | `m_color` (white) |
| SetActive / SetBoardSpaceActive | `m_newState` SetActiveType (default Disabled; Enabled/Disabled/Toggle) |
| PlayTweenAnimation | `m_tweenType`, `m_easeType` LeanTweenType (linear), `m_time` 1, `m_startValueType`, `m_startValue`, `m_endValueType` (Offset), `m_endValue` (1,1,1), `m_rotationAxis` (up), `m_rotationDegrees` |
| Position / Rotation | `m_space` ModActionSpace (World/Local), `m_operation`, `m_position` / `m_rotation` Vector3 (1,1,1) |
| SetGravity | `m_value` Vector3 (0,-9.81,0) |
| ModifyPlayer | `m_playerActionType`, `m_movementSpeed` 1, `m_gravity` |
| PlaySound | `m_clip` ModAssetReference<AudioClip>, `m_volume` 1 |
| SpawnPrefab | `m_prefabs` ModAssetReference<PummelPrefab>[], `m_spawnAtPosition`, `m_position`, `m_rotation`, `m_parentToTarget`, `m_targetPositionOffset`, `m_targetRotationOffset` |
| ChangeVelocity | `m_operation`, `m_space`, `m_localTarget` (Receiver), `m_velocityDir`, `m_customDir`, `m_speed` 1 |
| ShowMessage | `m_messageTarget` (Everyone), `m_message` "Hello!", `m_duration` 1 |
| StunPlayer | `m_stunDuration` 1 |
| GiveMinigameItem | `m_item` ModAssetReference<PummelPrefab> |
| DropMinigameItem | `m_itemSlot` (RightHand), `m_dropAllItems` |
| MatchTransform | `m_transformToMatch` ModAssetReference<Transform>, `m_transformValuesToMatch` (all position + rotation) |
| SetPlayerVisual | `m_prefab`, `m_recolorPrefabUsingPlayerColor` true, `m_verticalLookTargetChildIndex` -1, `m_movementRotationTargetChildIndex` -1 |
| HealPlayer | `m_value` int |
| DamagePlayer | `m_damageInstance` DamageInstance |
| GiveKeys / SpawnKeys | `m_value` / `m_keys` int (1) |
| DropKeys | `m_keys` 1, `m_spawnkeys` true |
| GiveTrophy | `m_playSound` true, `m_trophiesToGive` 1 |
| RemoveTrophy | `m_trophiesToRemove` 1 |
| GiveItem | `m_item` Items, `m_items` ModGiveItemMask (default most items), upgraded via `m_version` |
| MoveSpace | `m_spaces` ModAssetReference<BoardNode>[] |
| SetConnectionActive | `m_connectionMappings` List<ConnectionMapping> |
| BoardPopup | `m_titleText`, `m_bodyText`, `m_speakerImageReference` (Texture2D ref), `m_showCloseButton`, `m_closeButtonText` "Close", `m_choiceCount`, `m_choices` (label, cost type/amount/item, icon, `OnSelectTrigger`), `m_isTimed`, `m_timedDuration` 10 |

Value ranges are enforced by inspector-side clamps (Wait 0..3600, popup health cost 1..29, other costs 1..1000), **not** in the data. [C]

### 2.2 Targets

* `TargetAction.m_targetFlags : ModActionTargets` (`[Flags]`: Source=1, Receiver=2, Advanced=4; `ModActionTargets.cs`, `TargetAction.cs:11-12,38-48`). Source = the event's source (e.g. the activating player), Receiver = the event's receiver (component owning the trigger / hit player), Advanced = the explicit list `m_targets`. Several flags may be set; `Execute` runs the action once per selected target in the order Source, Receiver, Advanced. [C]
* Constructor defaults: `TargetAction` Receiver; `BoardPlayerTargetAction` Source. The window overwrites `TargetFlags` with the call site's default only on append and only when that default is non-zero (section 3). [C]
* "Self" has no special form; it is Receiver (or Source). Items redirect to the holding player (`TargetAction.GetSource/GetReceiver`). [C]
* Players have no id; they are reached via Source/Receiver. Board actions accept `BoardNode`/`ModBoardSpace`/`ModBoardPlayer` and act on the players on that space (`BoardPlayerTargetAction.cs`). [C]
* Explicit references: `m_targets : ModAssetReference<Component>[]`; accepted component type is narrowed in the UI only (`GetAcceptedTargetType`: `Transform[]` for SetActive, `BoardNode[]` for board actions, `Component[]` default). Null / unresolved references are skipped at runtime. [C]

### 2.3 Complex values and timing

* `ModComplexValue<T>`: serialized `valueType` (`ComplexValueType`: Constant / RandomBetweenTwoValues), `min`, `max`. `ModComplexFloat.GetValue(Random)` returns `min` or a uniform sample in [min,max]. Used by `WaitAction.m_seconds` and `TimerTrigger.m_interval`. `ModComplexInt` samples from a static RNG (ignores the passed one). `ModEvent.GetActionRandomness` derives a seeded RNG from event seed + action index so random waits agree across clients. [C]
* `ModActionExecutionTime` struct: `RunDuration`, `AdditionalDelayDuration` (single-arg ctor sets both to the same value; Wait returns `(seconds, 0)`); `ModAction.GetExecutionTime` default 0; `ModLogicTrigger.GetExecutionTime` sums them. [C]
* Sequencing is list order only; the Wait action is the only delay mechanism. [C]
* `ModActionRunMode` (per class, not serialized): All, ServerRelay (server runs, tells clients to run that action index), ServerNoRelay (server only), Origin (only the originating client). [C]

## 3. Editing API of (a)

### 3.1 What the window does (all inline)

`ActionEditorWindow.DrawActionPanel` mutates the shown trigger directly; there is no command/method layer.

| Operation | Behaviour | Evidence |
|---|---|---|
| Add (drop on trailing "Drag New Action Here" area) | grow `trigger.Actions` by one via the `AddElement` array extension, `Activator.CreateInstance(type)` into the last slot; if the type is a `TargetAction` and `m_defaultTargets != 0`, set `TargetFlags = m_defaultTargets` | `ActionEditorWindow.cs:178-186` |
| Insert (drop palette entry on an existing action) | copy to `List<ModAction>`, `Insert(index+1, Activator.CreateInstance(type))`, assign back. **Does not apply `m_defaultTargets`** (inconsistent with Add) | `:148-153` |
| Remove (X on action header) | `Actions = DeleteElement(index, typeof(ModAction))` | `:121-125` |
| Reorder (drag header onto another) | **swap** of two array slots (not move/insert) while hovering | `:136-146` |
| Duplicate / copy / paste an action | **not present**. `ModAction.ShallowCopy()` exists but is used only by the runner | grep [C] |
| Change a parameter | `ModEditorGui.InputModAction` just calls `action.DrawInspector()`; each action writes its private fields from ImGui widgets every frame | `ModEditorGui.cs:695-698` |
| Change targets | `TargetAction.DrawToggleButtonSet` recomputes `TargetFlags` from three toggles; Advanced list via `ModEditorGui.DrawValue(this,"Targets",...)` | `TargetAction.cs:~170-260` |
| Change an action's type in place | `ModAction.ChangeType(type)` returns a fresh default instance but has no caller | `ModAction.cs:87-95` |

A new action's defaults = its parameterless ctor + field initializers; the public ctor passes the right `ModActionType` to the base ctor, so `Type` is correct without extra calls. The window calls no `Setup`/`Reset`. [C]

Pieces a plugin can reuse without the window: `ModAction.GetActionClassType`, `ModActionMeta.GetAllActions/GetActionMeta`, `ModLogicTrigger.GetTriggerClassType/CreateInstance`, the `Actions` / `Triggers` setters, `EditorSystem.CanUseItem`. The meta registry is filled at `BeforeSceneLoad`, so it is ready for a plugin. [C]

### 3.2 Trigger-level operations (Logic component)

`ModLogicInspector.OnInspector` (`ModLogicInspector.cs:11-35`): "Add Trigger" appends a new `StartTrigger`; header X removes; changing the Event dropdown calls `CreateInstance(newType)` and copies only `Actions` (trigger-specific params reset). Every frame it assigns `modLogic.Triggers = list.ToArray()`, replacing the array instance. A held reference to the array, or to a trigger after a type change, can go stale; re-fetch before editing. [C]

### 3.3 Programmatic entry points

"UI-coupled" = needs an ImGui frame / `EditorSystem` UI. Undo/dirty columns describe the call itself; see 3.4 for the global mechanism.

| Method signature | What it does | Records undo? | Marks dirty? | UI-coupled? | Evidence |
|---|---|---|---|---|---|
| `static Type ModAction.GetActionClassType(ModActionType type)` | enum -> action CLR type; null + error log if unmapped | no | no | no | `ModAction.cs:97` |
| `ModAction ModAction.ChangeType(ModActionType type)` | returns a new default instance of that type (replaces nothing) | no | no | no | `ModAction.cs:87` |
| `ModAction ModAction.ShallowCopy()` | MemberwiseClone (shares arrays and refs) | no | no | no | `ModAction.cs:57` |
| `static Dictionary<Type,ModActionMeta> ModActionMeta.GetAllActions()`; `static ModActionMeta GetActionMeta(Type)` | metadata registry (UI name with icon glyph, help, category, mod-type flags, usage flags) | n/a | n/a | no | `ModActionMeta.cs:56-62` |
| `static bool EditorSystem.CanUseItem(PummelModTypeFlags)` | palette gate 1 vs `Instance.CurrentMod.ModType` | n/a | n/a | needs an open mod (null `CurrentMod` throws) | `EditorSystem.cs:2707` |
| `ModAction[] ModLogicTrigger.Actions {get;set;}` | the action list; assigning a new array is the entire edit | no | no | no | `ModLogicTrigger.cs:44-53` |
| `static Type ModLogicTrigger.GetTriggerClassType(ModTriggerType)`; `ModLogicTrigger ModLogicTrigger.CreateInstance(ModTriggerType)` | enum -> trigger type; new default trigger (instance method, call on any trigger) | no | no | no | `ModLogicTrigger.cs:197-240` |
| `ModLogicTrigger[] ModLogic.Triggers {get;set;}` | trigger array of a Logic component | no | no | no | `ModLogic.cs:24-39` |
| trigger slots: `ModBoardSpaceEvent.OnEnterTrigger/OnPassTrigger`, `ModTrigger.On{Hit,Enter,Exit,Stay}Actions`, `ModItem.On{Pickup,Drop,Use,WhileHeld}Trigger`, `ModDestructible.OnHitActions`, `ModFireWeapon.WeaponHitTrigger` (get/set) | component trigger members (BoardSpaceEvent setter casts to `BoardSpaceTrigger`) | no | no | no | 1.1 |
| `Activator.CreateInstance(Type)` on an action type | construct an action with defaults and correct `m_type` | no | no | no | `ActionEditorWindow.cs:151,181` |
| `TargetAction.TargetFlags {set}`; `TargetAction.Targets {set}` | set target flags; set explicit Component targets (wraps each in a new `ModAssetReference<Component>`) | no | no | no | `TargetAction.cs:15-48` |
| `void ActionEditorWindow.Show(ActionWindowTriggerInfo info, bool clearHistory, bool addToHistory = true)` | open window on a live trigger; sets static labels/flags, rebuilds palette, pushes breadcrumb | no | no | **yes** | `ActionEditorWindow.cs:44-62` |
| `static void ModEditorGui.InputModLogicTrigger(string label, ref ModLogicTrigger modTrigger, string recieverName, string sourceName, bool hideSourceFlag, ModActionTargets defaultTargets, AdditionalActionUsageFlags usageFlags, bool clearHistory, string triggerName, string objectInfo)` | draws "Edit" button; on click builds `ActionWindowTriggerInfo` and calls `Show` | no | no | **yes** (inside an ImGui frame) | `ModEditorGui.cs:700-720` |
| `EditorSystem.ActionEditor` (property) | the single window instance | n/a | n/a | yes | `EditorSystem.cs:385,1100` |
| `EditorSystem.GetUndoManager()` -> `EditorUndo` (`IsModDirty()`, `PushUndoEventToStack`, `Undo`), `EditorSystem.IsModDirty`, `SaveCurrentMod()` | global undo / dirty / save (3.4) | - | - | partly | `EditorSystem.cs:407,729,1660`, `EditorUndo.cs` |

There are **no** methods named AddAction / RemoveAction / MoveAction anywhere; a plugin implements them as array manipulation on `Actions`.

### 3.4 Undo and dirty marking

* Neither the window nor any `DrawInspector` calls an undo API or sets a dirty flag. [C]
* Undo is **snapshot-based per selection**: each frame `EditorSystem` calls `DoUndoSerialize()` (`EditorSystem.cs:1643-1661`, `1793-1807`) when the mouse is up (and 0.1 s after the last click for a single selected object). It serializes the *currently selected GameObjects* with `ModHierarchySerializer.SaveTransform(..., ignoreChildren:true)` and, if the bytes differ from the previous image, pushes a `GameObjectUndoEvent`; `EditorUndo.PushUndoEventToStack` also sets `m_isModDirty` (`EditorUndo.cs`). [C]
* Consequences for a plugin [I, derived from the above]:
  1. If the target GameObject is **selected** when `Actions` is mutated, the change is detected on a later frame, becomes an undo step and marks the mod dirty, at no cost to the plugin.
  2. If it is **not selected**, there is no undo entry and no dirty flag. Whether a later save still writes it is **[?]** (`SaveCurrentMod` not read).
  3. Safest sequence: select the object, mutate on the main thread, let 1-2 frames (>0.1 s) pass, then save. No public "set dirty" exists other than pushing an event onto the undo manager.
  4. `Ctrl+Z` restores byte images of the selected objects including their triggers/actions. The Action window keeps a reference to the pre-undo trigger object, so it can show stale data [I].
* Prefab editing uses a separate `EditorUndo` per `PummelPrefab`; `GetUndoManager()` picks the right one (`EditorSystem.cs:315,729-740`). [C]

## 4. How the Action Editor window is opened

* Input: an `ActionWindowTriggerInfo` readonly struct (`ActionWindowTriggerInfo.cs`): `Trigger` (the **live** `ModLogicTrigger` to edit), `ReceiverName`, `SourceName`, `HideSourceFlag`, `DefaultTargets`, `Flags` (`AdditionalActionUsageFlags`), `ObjectInfo` and `TriggerName` (breadcrumb text only); plus `clearHistory` on `Show`. [C]
* Only opened from inspectors: each component inspector draws an "Edit" button through `ModEditorGui.InputModLogicTrigger` (`ModEditorGui.cs:700-720`), also via the generic reflection cases for `MODLOGICTRIGGER` / `MODACTION` (`ModEditorGui.cs:~569-582`). The window is created once (`EditorSystem.cs:1100`) and registered as a normal `EditorWidget` (`:1109`); `Show` sets `IsOpen`. [C]
* Per-call-site inputs, e.g. Logic: ("This Object", "Activator", hideSource unless Hit/WeaponHit, default targets 0, flags None); Trigger Enter/Exit/Stay: default Source; Board Space Event: flags `BoardSpaceOnly`; player tick/respawn/round settings: hideSource true, default Receiver (`MinigameModSettings.cs:314-358`). [C]
* **A plugin does not need the window.** It holds no model state beyond `m_trigger` (a reference), search text and the palette cache; all edits are direct writes to the trigger. Editing `trigger.Actions` works whether or not the window is open. If the window is open on the same trigger object it re-reads `Actions` every frame; if the Logic inspector later replaces that trigger object, the window edits an orphan. Opening it programmatically needs a full `ActionWindowTriggerInfo` and an ImGui frame, and is only useful to show the user something. [C/I]
* The window's statics (`CurrentReceiverName`, `CurrentSourceName`, `HideSourceFlag`, `m_defaultTargets`) are global UI state set by `Show`; a plugin mimicking "Add" should take its own default targets. [C]

## 5. Serialization of (a) at the model level

* Two layers:
  1. Unity serialization (`[Serializable]`, `[SerializeField]`, `[SerializeReference]` on `m_actions` / `m_triggers`) for in-memory and prefab use. [C]
  2. Mod file format: `ModReflection` walks a component's **public properties and fields** (`ModReflectionSystem.cs:76,116`, `DeclaredOnly|Instance|Public`), so `ModLogic.Triggers`, `ModTrigger.OnHitActions` etc. are the persisted members; private `m_*` are not discovered directly. Types are tagged `ModReflectionType.MODLOGICTRIGGER` / `MODACTION` (`ModReflectionTypes.cs:189-194`). [C]
* Wire layout per trigger (`ModReflectionTypes.cs:382-394` write, `640-652` read): `ushort ModTriggerType`, the trigger as pretty-printed Unity `JsonUtility` JSON (`Extensions.cs:816-841`), `ushort` action count, then per action `ushort ModActionType` + `JsonUtility` JSON of the concrete action. A lone `ModAction` property is the same type+JSON pair (`:375-381`, `631-635`). On read the CLR type is resolved through `GetTriggerClassType` / `GetActionClassType` (an unmapped enum value gives a null type and a failed/empty read), then `Actions` is overwritten with the explicit list. [C]
* Persisted per action: all Unity-serializable fields including inherited private ones (`m_type`, `m_version`, `m_targetFlags`, `m_targets`). Runtime-only: `m_hasStarted`, `m_isFinished`, `m_curEvent`, trigger runners / owner / index. [C]
* Object and asset references: `ModAssetReference<T>` serializes `SerializableGuid m_assetGUID` plus the direct `m_asset`. In editor mode, just before serialization (`OnBeforeSerialize`) the GUID is recomputed from the live object with `WorkshopController.GetAssetWithObject(m_asset)` (so the object must be a registered mod asset); on deserialize `GetAsset()` resolves GUID -> `ModAsset` (or its `unityObjectAddressable`) (`ModAssetReference.cs:60-126`, `WorkshopController.cs:777-811`). Unresolvable GUID -> `Asset == null`. On prefab instantiation `PummelPrefabReferenceUpdater` rewires target references via `TargetAction.UpdateTargetReference(oldGuid, newGuid, newComponent)` (`PummelPrefabReferenceUpdater.cs:153`). [C]
* Writing targets from a plugin: `new ModAssetReference<Component>(component)` or `TargetAction.Targets = ...`; the GUID is filled on next serialize. Whether bare scene components resolve to an asset GUID depends on `Assets.GetAssetWithObject` **[?]**.
* Nested popup-choice triggers are plain fields inside the popup action's JSON (no wire-level type tag; `m_onSelectTrigger` is not `[SerializeReference]`), so they round-trip as base-type `ModLogicTrigger` with their actions array; trigger-subclass params would not survive, but none are used there [I].
* No list-level versioning; per-action `m_version` is handled by individual classes. [C]

## 6. Runtime of (a)

* Setup: `ModLogic.OnAfterDeserialize` in play mode calls `SetupTriggers` -> `ModLogicTrigger.Setup(owner, index)` (`ModLogic.cs:60-77`); board space events do likewise (`ModBoardSpaceEvent.cs:52-60`). [C]
* Firing (server side only): `ModLogic.Update` calls `OnStart` once and `OnUpdate` each frame; `OnEnable` runs on activation; `OnEvent(ModEvent)` forwards component events. `HitTrigger.OnEvent` filters by event type and `HitType` flags; `TimerTrigger.OnUpdate` accumulates delta time. A firing trigger with actions calls `GameManager.Board.ExecuteTriggerAndRelayEvent(triggerIndex, event)` (`ModLogicTrigger.cs` `Execute`, `GameBoardController.cs:6391-6403`), which RPCs the event (type, trigger index, serialized event incl. seed) to clients and runs it locally. [C]
* Execution: receiver `OnExecuteTrigger` -> `ModLogicTrigger.RunActionsLocal` -> a `ModActionRunner` keyed by event seed that holds **shallow copies** of the actions and runs a coroutine over them in list order (`ModActionRunner.cs`). Per action: skip if `Origin` mode and not origin, or `ServerNoRelay` and not server. `ServerRelay`: the server relays the action index (`RelayAction`) and executes; clients wait (10 s timeout) for the queued index. `Wait` sets a timer checked before the next action; coroutine actions yield their instruction; a started `BoardPopup` blocks the next action until finished. Stops early when the minigame stops running actions; completion raises `OnFinishedRunningActions`, removing the runner. [C]
* `SpawnPrefab` with networked positions relays a `SpawnPrefabActionEvent` carrying chosen positions/orientations. [C]
* Runtime flags live on the shallow copies, but arrays/refs are shared with the authored action. [C]

## 7. System (b): visual-scripting node graph - dormant

* Present [C]: namespace `VisualScripting` (37 files). `Graph` (name, graph type Class/Function, node list, link list, guid), `GraphNode` (id, position, slots, base-node guid), `GraphSlot` (id, slot guid, data string, custom flag), `GraphLink` (id, start/end slot ids), `GraphVariable` (name, id, public / network-sync / owner flags, data-type guid, data), `NodeType` (33 values: Event, Constant, InstanceFunction, StaticFunction, Branch, VariableGet/Set, PropertyGet/Set, Arithmetic*, Logic*, Iterators, comparisons, ModEvent, CustomFunction ...), `GraphDefinitionSystem` (singleton catalogue loaded from Unity `Resources` assets `GraphNodeList`, `GraphNodeVariantList`, `GraphBaseDataTypeList`, `GraphDefinitionSystem.cs:214-216`). `PummelVisualScript` (`.pgf`; importer registered at `PummelAssetImporter.cs:106`) stores `graphs[]` + `variables[]` as `JsonUtility` JSON under a mod's `VisualScripts/` folder (`PummelMod.cs:46,113`); `ScriptGenerator` and the `*Generator` classes turn graphs into C# text (`PummelVisualScript.Generate`); `VisualScriptType`: Basic, MinigameController, MinigamePlayer, Item, MovingMinigamePlayer. `[ModScript]` marks a few classes (`BoardNodeEvent`, `Interaction`; `ModReflectionTypes.cs:220`). `RoslynUtility.CompileScript` wraps Roslyn. `NodeEditor` is a static P/Invoke wrapper over the native plugin `ModEditorPlugin` (imgui-node-editor).
* Evidence that it is not reachable in the shipped editor:
  * `neBegin` (opens a node-editor canvas) has no caller; only its P/Invoke declaration exists. [C]
  * `EditorSystem.Init` registers a fixed widget set: toolbar, main menu, inspector, scene view, hierarchy, log, file browser, action editor (`EditorSystem.cs:1092-1109`). No visual-scripting window class exists in the tree. [C]
  * `EditorSystem.FocusVisualScriptWindow` (focus an ImGui window titled "Visual Scripting", `:1909-1912`) and `CreateNewVisualScript` (`:2445-2456`) have **no callers**. [C] Whether the window exists in a stripped assembly or native code is **[?]**; the decompile contains only `Assembly-CSharp`.
  * `RoslynUtility.CompileScript` has no caller and `PummelMod.CompileResult` (`:151`) has a private setter that is never assigned, so no mod script assembly is ever produced in this build; `ModScriptReference` (`:101-104`) and `WorkshopController.cs:461-463,838` consume that null. [C]
  * `MinigameMod.cs:50-60` renames visual scripts named "ModPlayer"/"ModController" on template creation, suggesting old templates once shipped `.pgf` files [I].
  * The user wiki in `docs/md` never mentions a graph editor; all user-facing logic goes through Actions.
* Conclusion: (b) is a dormant/dev-leftover subsystem (model, importer, code generator present; editor UI and compile step absent). A plugin cannot create or edit graphs through the shipped UI and should not rely on it. [C/I]

## 8. Gotchas for external callers

1. **Private fields**: set action parameters by reflection (`NonPublic|Instance`) or a JSON overlay (`JsonUtility.FromJsonOverwrite`) onto a freshly constructed instance. Field names are the on-disk JSON keys; they are stable only as long as the game does not rename them.
2. **Persisted IDs**: `ModActionType` / `ModTriggerType` numeric values are persisted; an action whose enum is missing from `GetActionClassType` cannot be loaded.
3. **Instance replacement**: the `ModLogic.Triggers` array (every frame) and trigger objects (on type change) are replaced by the inspector. Re-resolve from the component immediately before editing; writes to a stale trigger are lost.
4. **Aliasing**: `GetShallowCopy`/`ShallowCopy` share `Actions` and nested refs. Never put one action instance into two lists; clone via JSON round-trip (`ToJson` then `FromJson(type)`).
5. **Index addressing**: triggers (and action indices for `ServerRelay`) are addressed by index over the network; edit only in edit mode, not during a playtest.
6. **Validity not enforced in data**: replicate the palette gates (mod type, `BoardSpaceOnly` call sites, hide-source, default targets) yourself; do not offer board actions on Logic components.
7. **Default targets**: window applies the call-site default only on append, not insert; ctor defaults are Receiver (Source for board-player actions). Set `TargetFlags` explicitly; a target action with no flags and no targets does nothing.
8. **Target references need registered live objects**: the GUID is recomputed at serialize time; an unregistered object serializes an empty GUID and the target is lost on reload.
9. **Undo/dirty only via selection snapshots** (3.4): edits to unselected objects may be neither undoable nor flagged dirty (unsaved-changes prompt skipped).
10. **Main thread only**; the window iterates the same array while drawing.
11. **Icon glyphs** (private-use FontAwesome codepoints) are embedded in `ModActionMeta.UIString`; use the table's plain names for AI-facing output.
12. **Names**: the meta string for SpawnKeys is "Spawn keys" (lower-case k); "Move To Space" is `MoveSpaceAction`; "Kill/Destroy" is `KillAction`.
13. **`ModComplexInt.GetValue`** ignores its RNG argument for random sampling (not deterministic across clients); no action seen uses it.
14. **Popup choices**: reach nested triggers via the popup action's private `m_choices` list; each `InteractionPopupChoice` exposes `OnSelectTrigger`. The legacy `m_actions` array on the choice is not used by the editor.

## 9. Open questions / unverified

* **[?]** Does `SaveCurrentMod` serialize all mod objects regardless of dirty state, so that edits on an unselected object survive save + reload? (`SaveCurrentMod` and `ModHierarchySerializer` not read.)
* **[?]** Is there a public way to mark the mod dirty without a selection? Only `EditorUndo.PushUndoEventToStack` (public) was seen setting the flag.
* **[?]** Do scene `ModBehaviour` components resolve in `Assets.GetAssetWithObject` (needed for `ModAssetReference<Component>` targets)? Likely through `ModBehaviour.guid` registration; not traced.
* **[?]** Runtime behaviour of an action placed in a mod type or call site it was not designed for (gate enforcement is UI-only).
* **[?]** Completeness of the component list in 1.1: grep found `ModLogicTrigger` members only in the listed classes; spawner/mover/animator components were not read individually.
* **[?]** Whether a "Visual Scripting" window exists in a stripped assembly or the native `ModEditorPlugin` (check the running editor's menus).
* **[?]** Whether GameObject Duplicate/Copy/Paste deep-copies `Actions` (likely via the serializer round-trip [I]) or shares instances.
* **[?]** Purpose of `InteractionPopupChoice.m_actions` (legacy) and whether old mods still load actions from it.
* **[?]** Client-side relay mechanics beyond the `GameBoardController` call sites (`RPCRelayEvent*` handlers not read).
