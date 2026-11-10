# 03 - Hierarchy, GameObjects, Components, Inspector properties, Selection, Undo

Recon of the Pummel Party mod editor internals, from the decompiled `Assembly-CSharp` (read-only; no game code is reproduced here, only names, signatures and paraphrases).
All paths are relative to `decompiled/Assembly-CSharp/`. Line numbers refer to the decompiled files.

Legend: **[C]** = confirmed directly in the code cited. **[I]** = inferred from reading the code (not run). **[?]** = unverified, see section 10.

## 0. Big picture

- The editor is a singleton `EditorSystem` (`EditorSystem.Instance`) driving ImGui widgets (`HierarchyWidget`, `InspectorWidget`, `SceneViewWidget`, ...). [C]
- Mod content is **plain `UnityEngine.GameObject`s** living in the real Unity scene under `EditorSystem.m_hierarchyRoot` (a public `Transform`), plus one `ModTransform` marker component per object. [C]
- All editable state is exposed through **C# public properties** on `ModBehaviour` subclasses, discovered by reflection once at startup (`ModReflectionSystem`). The inspector, the save file serializer and the undo snapshot all use the same property list. [C]
- Undo is a **snapshot-diff** system, not a command system, except for three structural commands (move/delete/duplicate). It records the serialized state of the *currently selected* objects. [C]
- Most "operations" (create, duplicate, delete, add component) have reusable methods on `EditorSystem` / `ModEditorComponentAdderRemover`; reparent/reorder are **inlined** in `HierarchyWidget` drag-drop code; rename / set-active / layer are inlined in widgets as direct property writes.

## 1. What a "gameobject" is

### 1.1 Type and marker
- No wrapper type. A mod object is a `GameObject` that has a `ModTransform` (subclass of `ModBehaviour : MonoBehaviour`). `AddGameObject` builds it as `new GameObject("GameObject", typeof(ModTransform))` (`EditorSystem.cs:2461`). [C]
- `ModTransform` carries `[ModSerialized(typeof(Transform), "Transform", ..., addable:false, ModBehaviourLoadPriority.ModTransform)]` and `[DisallowMultipleComponent]` (`ModTransform.cs:4-7`). Every object in a saved mod has exactly one. `EditorSystem.AddTransforms(GameObject)` (`:2576`) retro-fits `ModTransform` on a whole subtree. [C]
- Each `ModBehaviour` (including `ModTransform`) has a `guid` (`SerializableGuid`, `ModBehaviour.cs:46`), created in the constructor via `SerializableGuid.NewGuid()` when a mod is loaded and `ModBehaviourMonitor.Instance != null` (`ModBehaviour.cs:126-133`). The serializer writes the **ModTransform guid as the object's identity** and the per-component guids after it (`ModHierarchySerializer.SaveComponents`, `ModHierarchySerializer.cs:~140-150`). [C]

### 1.2 Stable identity usable in an API
- Recommended object ID: `gameObject.GetComponent<ModTransform>().guid` (string form of `SerializableGuid`). It is persisted in the mod file and survives save/load [C]. Duplicates probably get fresh guids because `m_guid` is a protected field without `[SerializeField]` (so `Instantiate` does not copy it and the constructor assigns a new one), and `CopyComponentTo` explicitly skips the `guid` property (`Extensions.cs:~136`) [I; see open question 1].
- Component ID: `ModBehaviour.guid` of that component.
- Reverse lookup guid -> object: `ModBehaviourMonitor` registers every behaviour as a `ModAsset` in `CurrentMod.Assets` (`AddOrUpdateModBehaviours`, `ModBehaviourMonitor.cs:~80-100`) with `asset.guid = mb.guid` and `asset.unityObjectAddressable = mb.Component` (the *underlying* Unity component, e.g. `Light`, or the `Transform` for a `ModTransform`). So `WorkshopController.Instance.GetAssetWithGuid(guid)` / `CurrentMod.Assets.GetAssetWithGuid(guid)` (`ModAssetList.cs:108`, binary search; `WorkshopController.cs:777`) returns the asset, whose `unityObjectAddressable` is the Transform -> `.gameObject`. [C]
- Caveat: registration is lazy (`UpdateBehaviours` runs in `ModBehaviourMonitor.Update` and at `LoadTransform`); a just-created object may not be in `Assets` until the next frame. Cheaper and safer for a plugin: keep its own `Dictionary<guid, ModTransform>` built by walking `m_hierarchyRoot` (see 1.4). [I]
- `GameObject.GetInstanceID()` is what the hierarchy ImGui uses for widget IDs (`HierarchyWidget.cs:76`); it is session-only. Do not persist.

### 1.3 Distinguishing mod objects from editor helper objects
- Mod objects: descendants of `EditorSystem.m_hierarchyRoot` (scene view) or of `m_prefabsRoot` (prefab edit mode; `EditorSystem.Root()` returns whichever is active, `EditorSystem.cs:952`). Both roots are `DontDestroyOnLoad` (`:1303-1304`). [C]
- Helper objects are NOT under these roots: the scene-view camera (`m_sceneViewCamera`, `:87`), gizmo meshes (`ModGizmoVisual` with a `MeshCollider`, picked by raycast in `SceneViewWidget.cs:341-357`), quad gizmo icons (`ModEditorQuadGizmoManager`), the `UndoDeletedObjects` holder (`EditorUndo.GetDeletedRoot`), `OutlineSource` components that the editor adds to renderers of selected objects (`EditorSystem.AddOutlineSource`, `:990`). [C]
- Gotcha: some editor-added components DO sit on mod objects: `OutlineSource` (selection outline; removed on deselect, `:1928-1938`), and clickable colliders (`ModBehaviour.m_clickableCollider`: Sphere/Mesh collider added by `UpdateClickableCollider`, `ModBehaviour.cs:208-256`). Filter by `ModBehaviour`/`ModReflectionSystem.GetType(...) != null`, not by raw `GetComponents`. [C]
- Children of prefab instances are real Unity objects (instantiated copy) but are hidden from the hierarchy and treated as opaque; see section 8.

### 1.4 Enumerating the tree
- Root: `EditorSystem.Instance.m_hierarchyRoot` (scene) / `.Root()` (current context) / `CurrentPrefab.PrefabObject.transform` (inside a prefab). Walk `Transform.GetChild(i)`; this is exactly what the widget does (`HierarchyWidget.DrawTransform`, `:441-631`). [C]
- Stop descending when `EditorSystem.ShouldHideChildGameObjects(tr)` is true (`EditorSystem.cs:971`): true if any component on `tr` has a `ModReflectedType` with `supportsChildren == false` (BoardSpace, Item, PrefabInstance, Text). Serializer does the same (`SaveHierarchy` writes 0 children for such objects). [C]
- Include inactive objects: `GetComponentsInChildren<ModTransform>(true)` (the editor itself uses `includeInactive: true`, `HierarchyWidget.cs:315`). [C]
- Per-object display metadata the widget derives: icon/colour by presence of `ModPrefabInstance`, `ModItem`, `ModBoardSpace` (`TransformDetails.GetIcon`, `:91-121`); extra text from `ModBehaviour.GetExtraDetailsText()`. [C]
- Current mod and mod type: `EditorSystem.Instance.CurrentMod` (`PummelMod`), `.ModType` (`PummelModType`: Item, Board, BoardSpace, Minigame), `.UsesHierarchy`; `EditorSystem.Instance.State` (`EditorState`: Editing, EnteringPlayMode, PlayMode, ExitingPlayMode). [C]

## 2. Hierarchy operations

Menu creation items live in `EditorSystem.DoGameObjectMenu(GameObject)` (`:2299-2409`), which just calls `AddGameObject(ObjectType, creationRoot)`. The `HierarchyWidget` itself contains only: selection, rename, drag-drop reparent/reorder, asset drop, context menu.

### 2.1 Create empty / template / primitive / prop / etc.
- **`public GameObject AddGameObject(ObjectType objectType, Transform creationRoot = null)`** (`EditorSystem.cs:2458`). Reusable, not ImGui-coupled except: (a) falls back to the private field `this.creationRoot` if `creationRoot` is null (that field is only set by `DoGameObjectMenu`, so **always pass the parent**); (b) calls `Instance.GetWindow(typeof(HierarchyWidget))?.SetWindowFocus()` (ImGui focus request, harmless outside a frame as far as seen) and (c) **selects the new object** (`m_selectionManager.SetSelectedObject`). Sequence [C]:
  1. `new GameObject("GameObject", typeof(ModTransform))`; `ModTransform.SetParent(creationRoot, worldPositionStays:false)`.
  2. Per `ObjectType`: set name and `AddComponent<ModX>()` then usually `SetupComponents()`.
  3. Select it.
- No undo event is pushed and nothing marks the mod dirty directly (see 7.4: creation is NOT undoable; dirty is set only when the snapshot system pushes). [C/I]
- `ObjectType` values handled: Empty (name "GameObject", "GameObject (n)" de-dup by counting siblings), Cube/Sphere/Plane (adds `ModProp`, assigns `prop` from asset GUIDs `cubeGuid`/`sphereGuid`/`planeGuid` via `WorkshopController.Instance.Assets.GetAssetWithGuid`), Prop (empty `ModProp`), Trigger (+`SetupComponents`), Directional/Point/SpotLight (`ModLight` with `type` set), NewPrefabInstance (`ModPrefabInstance`), Item (`ModItem`), BoardSpace (`ModBoardSpace`), ParticleSystem, Text ("WorldText"), AudioSource, Logic, LineRenderer, FireWeapon ("WeaponSource"), PlayerSpawn. Enum members `NewPrefab`, `NewPlayer`, `Camera` exist but have **no case** (produce a bare empty object named "GameObject"). [C]
- Menu availability (UI rules, not enforced by `AddGameObject`): all items disabled when `ShouldHideChildGameObjects(creationRoot)`; Prefab Instance disabled while editing a prefab; Minigame Object submenu only for `PummelModType.Minigame`; Board space only for `Board` and not in prefab edit; Player Spawn/Item/Trigger/Logic/Weapon Source are in "Minigame Object" (`:2352-2384`). A plugin must replicate these checks itself. [C]
- From an asset (prop/prefab dropped from the asset browser): `ModAsset.OnSceneDragDropStart(Transform root = null)` (`ModAsset.cs:562`) = `AddGameObject(Prop or NewPrefabInstance, root)` + set `ModProp.prop` / `ModPrefabInstance.PrefabReference`, name = asset name, layer 30 (preview), then `OnSceneDragDropEnd(go, delivered:true)` resets layer to 0 (`:595`). Prefab instances cannot be dropped while editing a prefab. [C]
- Prefab **asset** creation (not a hierarchy op): `EditorSystem.CreateNewPrefab(ModTransform)` (`:2411`) writes a `.pfab` file - file I/O in the mod folder; out of scope for hierarchy edits.

### 2.2 Duplicate (and copy/paste)
- `public void Duplicate()` = `Copy(); Paste();` (`EditorSystem.cs:2038`). `Copy()` (`:1986`) stores the selected objects in the private `copiedObjects` array. `Paste()` (`:1999-2036`) [C]:
  1. For each copied object: `BroadcastMessageExt("OnPreDuplicate")` on it, then `Object.Instantiate(original, parent)` where parent = (nothing selected) ? (prefab root or `m_hierarchyRoot`) : (last selected has `ModPrefab`) ? last selected : **last selected's parent**. So duplicates land as siblings of the *currently selected* object, not of the original. Name copied verbatim (no " (1)" suffix).
  2. Destroy `OutlineSource`s on the clone; if editing a prefab, destroy any `ModPrefabInstance` (and untag) in the clone.
  3. `BroadcastMessageExt("OnPostDuplicate")` (components re-run `SetupComponents`; `ModPrefabInstance.OnPostDuplicate` forces re-instantiation).
  4. `PushUndoEventToStack(new DuplicateGameObject(originals, clones))` (marks dirty), `SetSelectedObjects(clones)`, then re-baseline `lastGameobjectUndoEvent = GetCurUndoEvent()`.
- **Selection-coupled**: both source and destination are derived from the selection. A plugin must `SetSelectedObject(src)` first (or reimplement steps 1-4 with an explicit parent).
- Not ImGui-coupled. Guards `CanDuplicate` etc. are just "something selected && CurrentMod != null" (`:655-713`).

### 2.3 Delete
- `public void Delete()` (`EditorSystem.cs:2047`): operates on the **selection**. Filters with `Deleteable(g)` (= no `ModPrefab` component, i.e. cannot delete a prefab's root object), deselects each, then `PushUndoEventToStack(new DeleteGameObject(list))` and re-baselines. [C]
- The *constructor* of `DeleteGameObject` performs the deletion: records parent+sibling index, reparents each object under the inactive `EditorUndo.GetDeletedRoot()` ("UndoDeletedObjects"), and sets `behaviour.AssetReference.enabled = false` on all child `ModBehaviour`s (`DeleteGameObject.cs:14-37`). So delete is a **soft delete**: objects are never destroyed until editor teardown (`EditorSystem.OnDestroy`, `:2680`). Code elsewhere filters by `transform.root == EditorUndo.GetDeletedRoot()` (e.g. `ModBoardSpace.cs:335`). [C]
- Plugin implication: after delete the GameObject still exists (inactive, under another root); do not treat `go != null` as "alive"; check root. [C]

### 2.4 Rename
- Inline in `HierarchyWidget.DrawTransform` (`:484-500`): while renaming (F2 / context menu `StartRename`) an ImGui text field writes `tr.gameObject.name = text` **every frame**. Inspector name field (`InspectorWidget.cs:144-151`) writes `name` for **all selected objects**. No undo call, no dirty call; captured only by the snapshot system if the object is selected (name is part of the snapshot: `SaveHierarchy` writes name/activeSelf/layer/tag, `ModHierarchySerializer.cs:~95-105`). [C]
- Plugin recipe: select object, wait one frame, set `gameObject.name`. Hierarchy label cache refreshes by comparing `m_lastName` (`TransformDetails.CacheNames`). [C]
- Registered asset names (`ModAsset.name` used in object pickers) are refreshed in `ModBehaviourMonitor.AddOrUpdateModBehaviours` (copies `gameObject.name`); when that runs for existing behaviours is unclear [?] #4.

### 2.5 Reparent
- Inlined in `HierarchyWidget.DrawTransform` drop-target block (`:521-555`, drop **onto** an item) and `DoInterObjectLocation` (`:652-707`, drop **between** items). Sequence [C]:
  1. Build `new MoveGameObject(GameObject[])` (captures original parents + sibling indices) from the selection if the dragged item is selected, else from the dragged item alone; `PushUndoEventToStack(move)` **before** moving.
  2. For each: `go.GetComponent<ModTransform>().SetParent(newParent)` (default `worldPositionStays: true` -> world transform preserved, local values recomputed).
  3. `undo.lastGameobjectUndoEvent = undo.GetCurUndoEvent()` (re-baseline).
- Preconditions enforced only by the UI: drop targets exist only when `!TransformDetails.m_ignoreChildTransforms` (i.e. target allows children). No cycle check in the widget (Unity throws if you parent to a descendant) [?]. Also the context menu is suppressed for board spaces.
- Reusable? No - copy the three steps. `MoveGameObject` is a public struct and can be constructed directly.

### 2.6 Reorder siblings
- Same `DoInterObjectLocation` (`:652-707`): after optional reparent to `tr.parent`, `go.transform.SetAsLastSibling()` then, unless dropping in the last slot, `SetSiblingIndex(tr.GetSiblingIndex())` (target index re-read after the move, so the dragged item ends up directly before the target). Wrapped by the same Move undo/re-baseline. [C]
- Keyboard up/down only changes selection (`EditorSystem.Update`, `:1709-1768`), not order.

### 2.7 Set active
- Inspector checkbox `##hidden` (`InspectorWidget.cs:134-141`): `SetActive(value)` on **every selected object** (`GameObject.activeSelf`). Per-component enable checkbox: `MonoBehaviour.enabled` (`:250-252`) for components whose `ModReflectedType.addable` is true; `ModBehaviour.OnEnable/OnDisable` -> `SetState`. No undo call; captured by snapshot (`activeSelf`, per-component `enabled` are in the snapshot). [C]
- Layer: `InspectorWidget.cs:153-171`, with a Yes/No/Cancel popup when children exist (`SetLayerRecursively` -> `GameObject.SetLayer(int, includeChildren)` extension in `MalbersAnimationsExtensions.cs:96`). Layer 30 is used for drag-preview objects.

## 3. Component registry

### 3.1 Definition and discovery
- Attribute `[ModSerialized(Type runtimeType, string uiName, string uiIcon, bool addable, ModBehaviourLoadPriority savePriority, ComponentIconColor iconColor, string folder, PummelModTypeFlags modTypeFlags, bool supportsChildren)]` on the `ModBehaviour` subclass (`ModSerialized.cs`); public fields `addable`/`modTypeFlags` can also be set by named args (that is how `ModAudioListener`, `ModCamera`, `ModMeshFilter` are non-addable and `ModBoardSpaceEvent`, `ModRigidbody` change flags). [C]
- `ModReflectionSystem.Initialize()` -> `SearchForReflectionTypes()` (`ModReflectionSystem.cs:140-257`) scans `Assembly.GetExecutingAssembly()` (i.e. Assembly-CSharp only) for `[ModSerialized]`, builds one `ModReflectedType` per class, additionally reading `[ModCustomInspector]`, `[ModCustomGizmo]`, `[ModQuadGizmo]`, `[DisallowMultipleComponent]` (on the type and on its base type), fills `properties` (3.4) and a `FilterFolder` tree of addable types by `folder` for the picker. Lookups: `ModReflectionSystem.GetType(Type)` (by wrapper type), `GetReflectedType(Type)` (by `runtimeType`, i.e. the Unity type), `GetType(int)`, `TypeCount`, `ReflectedTypesRootFolder`. [C]
- Enumeration for a plugin: `for i in 0..ModReflectionSystem.TypeCount: ModReflectionSystem.GetType(i)` (`ModTransform` is forced to index 0). [C]

### 3.2 Add / remove
- Add (what the Add Component popup ends up calling): **`ModEditorComponentAdderRemover.AddComponentToGameObject(GameObject go, Type componentType) -> Component`** (`:19`) = `go.AddComponent(type)` + fires `OnAddComponent(go)`. `ModBehaviour.Awake` runs `SetupComponents()` which creates the underlying Unity component (e.g. `ModLight` adds a `Light`). The inspector callback `InspectorWidget.OnAddComponents` (`:211-224`) loops over **all selected objects** calling it with `ModReflectedType.systemType`. [C]
- Pre-check (UI only; **not** enforced by `AddComponentToGameObject`): `SetupComponentAddabilityCheck(List<GameObject>)` then `(bool blocked, string reason) PerformChecksForComponentAddability(ModReflectedType)` (`:44-146`). Both are static and ImGui-free, so a plugin can call them. The popup additionally requires `type.addable && EditorSystem.CanUseItem(type.modTypeFlags)` (`AddComponentPopup.DisplayComponent`, `:118-122`; `CanUseItem` maps `CurrentMod.ModType` -> flag: Minigame->Minigame, Board->Map, Item->Item, BoardSpace->BoardSpace, `EditorSystem.cs:2707`). [C]
- Restriction rules in `PerformChecksForComponentAddability` [C]:
  - `!supportsChildren` types need an object with no children.
  - `ModBoardSpaceEvent` only inside a prefab; `ModPrefabInstance`/`ModBoardSpace` never inside a prefab.
  - `ModPrefabInstance` only on an object whose only components are Transform+ModTransform (or RectTransform+ModTransform).
  - `[DisallowMultipleComponent]` types: one per object. Types without it (see table) may be added repeatedly.
  - A type that has `!supportsChildren` cannot be added if the object already has a different `!supportsChildren` component.
  - Base-class rule: if the type's base class is `[DisallowMultipleComponent]` and the object already has a component with the same base (`ModRenderer` subclasses: `ModMeshRenderer`, `ModProp`, `ModParticleSystem`, `ModText`, `ModWater` are mutually exclusive on one object).
  - `ModMover` and `ModSimpleAnimator` are mutually exclusive.
  - No "required component" mechanism exists in the registry; dependencies are created by `SetupComponents` (e.g. `ModProp` creates its own mesh filter/renderer, `ModTrigger` its collider).
- Remove: two paths. Inspector X button: **`ModEditorComponentAdderRemover.RemoveComponentFromGameObject(Component)`** (`:31`): if the type is `!supportsChildren` destroys all child gameobjects (`Object.Destroy`, hard destroy, not undoable), then `Object.Destroy(component)` and fires `OnRemoveComponent`. Context-menu "Delete X Component" (`ComponentContextMenu.cs`) just calls `Object.Destroy(component)` (no event). Both guard on `ModReflectedType.addable` only (so Transform and the non-addable engine wrappers cannot be removed in the UI). `ModBehaviour.OnDestroy` destroys the wrapped Unity component and deregisters the guid asset. [C]
- Copy/paste component: `GameObject.CopyComponentTo<T>(T original)` and `Component.PasteComponentValuesTo(original)` (`Extensions.cs:120-150`): copy public *declared* properties one by one except `guid`. [C]

### 3.3 Registry table (41 `[ModSerialized]` classes, `ModX.cs`)
"Add" = shown in Add Component for the mod types in "Mod types" (requires `addable`). "Multi" = more than one per object allowed (no `[DisallowMultipleComponent]` on the class). "Kids" = `supportsChildren`. Inspector: C = `[ModCustomInspector]` class, R = pure reflection list. Display name is `uiName`. Mod types: Mg = Minigame, Bd = Board (flag `Map`), It = Item, Bs = BoardSpace; "All" = all four.

| Class | Display name | Category (folder) | Mod types | Add | Multi | Kids | Inspector |
|---|---|---|---|---|---|---|---|
| `ModTransform` | Transform | Default | All | no | no | yes | R |
| `ModProp` | Prop | Rendering | All | yes | no | yes | C `ModPropInspector` |
| `ModMeshRenderer` | Mesh Renderer | Rendering | All | yes | no | yes | R |
| `ModMeshFilter` | Mesh Filter | Rendering | All | no (named `addable=false`) | no | yes | R |
| `ModLight` | Light | Rendering | All | yes | no | yes | C `ModLightInspector` |
| `ModLineRenderer` | Line Renderer | Rendering | All | yes | no | yes | R |
| `ModParticleSystem` | Particle System | Rendering | All | yes | no | yes | C |
| `ModText` | Text | Rendering | All | yes | no | **no** | C |
| `ModWater` | Water | Rendering | All | yes | no | yes | C |
| `ModSkinnedMeshRenderer` | Skinned Mesh Renderer | Default | All | no | no | yes | R |
| `ModBoxCollider` | Box Collider | Physics | All | yes | yes | yes | R |
| `ModSphereCollider` | Sphere Collider | Physics | All | yes | yes | yes | R |
| `ModCapsuleCollider` | Capsule Collider | Physics | All | yes | yes | yes | R |
| `ModMeshCollider` | Mesh Collider | Physics | All | yes | no | yes | C |
| `ModRigidbody` | RigidBody | Physics | **None** (named override) | listed but never offered (`CanUseItem` false for every mod type) | no | yes | R |
| `ModAudioSource` | Audio Source | Audio | All | yes | yes | yes | C |
| `ModAudioListener` | Audio Listener | Audio | All | no (named `addable=false`) | yes | yes | R |
| `ModCamera` | Camera | Camera | All | no (named `addable=false`) | no | yes | R |
| `ModBoardCamera` | Board Camera | Camera | Bd | no | yes | yes | R |
| `ModCameraShake` | Camera Shake | Camera | All | no | yes | yes | R |
| `ModTrigger` | Trigger | Gameplay | Mg | yes | no | yes | C `ModTriggerInspector` |
| `ModLogic` | Logic | Gameplay | Mg, Bd | yes | no | yes | C |
| `ModSpawner` | Spawner | Gameplay | Mg | yes | no | yes | C `ModSpawnerInspector` |
| `ModPlayerSpawn` | Player Spawn | Gameplay | Mg | yes | no | yes | C |
| `ModItem` | Item | Gameplay | Mg | yes | no | **no** | C |
| `ModFireWeapon` | Weapon | Gameplay | Mg | yes | yes | yes | C |
| `ModAINavigation` | AI Navigation Node | Gameplay | Mg | yes | no | yes | R |
| `ModMover` | Mover | Gameplay | All | yes | yes | yes | C |
| `ModSimpleAnimator` | Simple Animator | Gameplay | All | yes | no | yes | C |
| `ModNote` | Note | Gameplay | All | yes | yes | yes | C |
| `ModPrefabInstance` | Prefab Instance | Gameplay | All | yes | no | **no** | C `ModPrefabInstanceInspector` |
| `ModBoardSpace` | Board Space | Gameplay | Bd | yes | no | **no** | C |
| `ModBoardSpaceEvent` | Board Space Event | Gameplay | Bd (named override) | yes (prefab only) | no | yes | C |
| `ModDestructible` | Hittable | Gameplay | **None** | never offered | no | yes | C |
| `ModScriptReference` | Script | Default | All | no | yes | yes | R |
| `ModAnimator` | Animator | Default | All | no | yes | yes | R |
| `ModCharacterController` | Character Controller | Default | All | no | no | yes | R |
| `ModCharacterMover` | Character Mover | Default | All | no | yes | yes | R |
| `ModNavMeshAgent` | Nav Mesh Agent | Default | All | no | yes | yes | C |
| `ModPlayerAnimation` | Player Animation | Default | All | no | yes | yes | R |
| `ModThirdPersonCamera` | Third Person Camera | Default | All | no | yes | yes | R |

All of the above confirmed by reading each class's attribute line [C]. Notes: `runtimeType` (the wrapped Unity type) is the first attribute argument (e.g. `ModLight -> Light`, `ModText -> TextMeshPro`, `ModWater -> LowPolyWater`). `savePriority` is non-default only for `ModTransform` (100) and `ModPrefabInstance` (50); the serializer writes components in descending priority so Transform loads first. Category "Default" is the root level of the picker (a child folder literally named "Default" is skipped when drawing; `AddComponentPopup.cs:78`) [I]. Mod types in this table come from the `PummelModTypeFlags` argument; the "Add" column additionally requires `CanUseItem`.

### 3.4 Property discovery for a registered type
See section 4. The registry's `ModReflectedType.properties` (array of `ReflectedProperty`) and `propertyMap` (name -> property) are what the inspector, save and undo iterate.

## 4. Property system

### 4.1 Discovery
- **Reflection over public instance C# properties, not fields.** `ModReflectionSystem.GetReflectedProperties(Type)` (`:74-112`): `type.GetProperties(DeclaredOnly | Instance | Public)` of the concrete class, plus the declared properties of `ModBehaviour` (so `guid`) for any `ModBehaviour` subclass; drops properties whose getter overrides a base getter; drops properties with `[ModIgnore]`; keeps a property only if its type (or array element type) is in `ModReflectionTypes.GetReflectionType` map, or is an enum / flags enum. Anything else without `[ModIgnore]` logs "Cannot Save/Load" and is dropped. (`GetReflectedFields` exists but `SearchForReflectionTypes` uses only properties.) [C]
- **`DeclaredOnly` caveat**: properties declared on an intermediate base class (e.g. `ModRenderer`, which declares none) are not visited. Not a problem for current types but a rule for the plugin's own reflection. [C]
- `ReflectedProperty` (`ReflectedProperty.cs`): `name`, `uiName` (PascalCase split + Title Case), `systemType`, `reflectionType` (`ModReflectionType`), `isArray`/`elementType`, `isEnum`, `isFlags`, `isNested` (name contains "."), `hasHeader/header/separator` (`[ModHeader]`), `hideInInspector` (`[ModHideInInspector]`), `stringLineCount` (Unity `[Multiline(n)]`). Read/write via FastMember `ObjectAccessor`: **`object GetValue(object target)`**, **`void SetValue(object target, object value, int index = -1)`** (index only for array elements). Exceptions in set are caught and logged, not thrown. [C]
- Attributes available: `[ModIgnore]`, `[ModHideInInspector]`, `[ModHeader(text, separator)]`, `[ModCustomInspector(Type)]`, `[ModCustomGizmo]`, `[ModQuadGizmo]`, `[ModWikiPageURL]`, `[ModPreviouslySerializedAs]` (rename compat on load, `TryGetPreviousSerializedType`), Unity `[Multiline]`. **There are no range/min/max/validation attributes** in this system (no `[Range]` found on `Mod*.cs`). [C]
- Validation lives in (a) property setters (e.g. `ModTransform` clamps), (b) hard-coded ranges in custom inspectors (e.g. `ModLightInspector` draws `spotAngle` as a slider 0-180 and shows `range` only for non-directional lights; `ModTriggerInspector` clamps `StayTriggerInterval >= 0.1` after drawing; shows `Size/Radius/Height` per `TriggerShape`). A plugin writing the property directly **bypasses inspector-only clamps**. [C]

### 4.2 Supported property types (`ModReflectionType`, `ModReflectionTypes.cs`)

| Reflection type | CLR type(s) | Inspector widget (`ModEditorGui.DrawValue`, `:344-693`) | Serialized as (`ModReflectionTypes.WriteType/ReadType`) | Plugin JSON suggestion |
|---|---|---|---|---|
| BOOL | bool | checkbox | bool | bool |
| BYTE/CHAR/SHORT/USHORT/INT/UINT/LONG/ULONG | integers | `InputInt` (byte/uint via int; long/ulong/short/ushort/char have **no inspector case** -> red "type" placeholder text, but are saved) | native widths | number |
| FLOAT / DOUBLE | float / double | `InputFloat` / `InputDouble` (plain input, no range) | float / double | number |
| STRING | string | single-line, or multiline if `[Multiline]` | string | string |
| VECTOR2/3/4, QUATERNION, RECT | UnityEngine structs | `InputVec2/3/4`, `InputQuaternion`, `InputRect` | native | arrays / object |
| COLOR / COLOR32 | Color / Color32 | color picker (both as `Color`) | color | [r,g,b,a] |
| enum / flags enum | any enum | `InputEnum` / `InputFlags` (cast through int) | int | name or int |
| LAYERMASK | LayerMask | layer mask picker | | int |
| MINMAXCURVE, MINMAXGRADIENT, BURST, ANIMATIONCURVE | particle/curve types | curve/gradient editors (MinMaxCurve and Burst draw nothing in `DrawValue` itself; custom inspectors such as `ModParticleSystemInspector` draw them) | custom binary | complex |
| PROP, MATERIAL, AUDIOCLIP, TEXTURE2D, MESH, ANIMATORCONTROLLER, AVATAR, SHADER, PUMMELVISUALSCRIPT, PUMMELPREFAB | asset references (`Prop`, `Material`, `AudioClip`, ...) | "None (Type)" button -> `ChooseAssetPopup`; also accepts drag from Hierarchy/component header | **asset GUID** (`WriteObject`: bool + `ModAsset.guid`, resolved with `WorkshopController.GetAssetWithObject/GetAssetWithGuid`) | asset guid string |
| TRANSFORM, GAMEOBJECT, COMPONENT, MODSCRIPT, RECTTRANSFORM | **scene object references** | same button; picker location flags depend on being inside a prefab (`ModEditorGui.cs:498-501`) | **GUID of the target's ModBehaviour asset** (same `WriteObject`); GAMEOBJECT read converts a `Transform` to its `gameObject` | `ModTransform.guid` of target |
| SERIALIZABLEGUID | SerializableGuid | read-only (no widget) | guid | string |
| BOARDNODECONNECTION | BoardNodeConnection | none in DrawValue | JSON container | complex |
| MODACTION, MODLOGICTRIGGER, MODWEAPONINFO, MODCOMPLEXFLOAT, SIMPLEMODHITACTION, SIMPLEANIMATIONLAYER, SIMPLEANIMATION | mod-logic structs/classes (`ModComplexFloat : ModComplexValue<float>` etc.) | each has its own `DrawInspector`/`InputX` that opens action/trigger editors or sub-widgets | `WriteJson(...)` (Unity `JsonUtility`-style) or custom | opaque; treat as JSON blobs |
| arrays of any of the above | `T[]` | tree "Element: n" with up/down/delete buttons, "Add Element" (new elements are `Activator.CreateInstance` for non-Unity classes); arrays are **replaced wholesale** through `DeleteElement/AddElement/Swap` | count + elements | JSON array |
| nested "A.B" names | n/a | `isNested` support exists in `ReflectedProperty` but no current reflected type uses it [?] | | |

[C] for the table rows above from `ModReflectionTypes` dictionary (`:10-140`), `GetReflectionType` (`:210`), `DrawValue`.

### 4.3 How the inspector reads/writes (and what that implies)
- `InspectorWidget.DrawComponents(go, isPrefabInstance)` (`:226-307`) iterates `go.GetComponents(typeof(ModBehaviour))`; for each: `ModReflectionSystem.GetType(component.GetType())`; if `type.hasInspector` -> `Activator.CreateInstance(type.inspectorType)` as `ModInspector` and `OnInspector(ModReflectedType)` after `type.SetTarget(component)` and `ModEditorGui.m_curComponent = component`; else `ModEditorGui.DrawValue(component, property)` for each property. Custom inspectors choose which properties to show with `ModEditorGui.DrawValue(string name, ModReflectedType)`; **properties not drawn by a custom inspector are not editable in the UI** but still saved. [C]
- **Write model: no change detection.** `DrawValue(ref object val, ReflectedValue, comp)` ends with `value.SetValue(comp, val)` on **every frame** the component header is open (`:689-692`), unless an asset-picker callback already set it (`m_valueSet`). So (a) property setters must be idempotent and cheap, and (b) there is **no OnValidate/onChanged callback, no per-edit undo record, no dirty flag set by the inspector**. Everything downstream relies on the setter itself having side effects (e.g. `ModLight.type` -> `Light.type`; `ModTransform.position` -> `localPosition`; `ModProp.prop` rebuilds mesh/colliders; `ModTrigger.TriggerShape` -> `UpdateTriggerShape`). [C]
- Therefore "after changing a field": **the setter already applied it visually**. Extra calls a faithful plugin should consider [C unless noted]:
  - Nothing else to call for normal properties; do *not* call `OnBeforeSerialize/OnAfterDeserialize` (serializer-only; `OnAfterDeserialize` is invoked after load, `ModHierarchySerializer.LoadComponentValues`).
  - `ModHierarchySerializer.SettingPropertiesFromLoad` is true only while undo/load applies values; some setters branch on `SettingPropertyFromLoad` (e.g. `ModPrefabInstance.PrefabReference` does not rewrite its stored GUID while loading). Normal edits must leave it false.
  - For prefab instances changing `PrefabReference` calls `UpdatePrefab()` (destroys and re-instantiates the child copy) unless play mode.
  - Hierarchy label refresh for extra details text: `OnAddComponent`/`OnRemoveComponent` events or `GetOrCreateTransformDetails(tr).RefreshTransformDetails()` (the label also self-refreshes each frame when extra text is non-empty).
  - To get undo + dirty: select the object and let the snapshot loop run (section 7).
- Reading a whole component for an API: iterate `ModReflectionSystem.GetType(component.GetType()).properties` and `property.GetValue(component)`; this is precisely what the saver does (`ModReflectionTypes.WriteTypes`). Note the saver calls `behaviour.OnBeforeSerialize()` first (`SaveComponentValues`). [C]
- Safe-write semantics to mirror: `ReflectedProperty.SetValue` catches exceptions and logs (so a plugin gets no error signal); wrap in own try/catch and read back. [C]

### 4.4 Custom inspectors: what they add beyond reflection
- They are plain classes implementing `ModInspector` (`void OnInspector(ModReflectedType)`), attached with `[ModCustomInspector(typeof(X))]` (`ModCustomInspector.cs`). They add: conditional visibility (light range / spot angle; trigger shape fields), grouping and help text, sliders with hard-coded ranges, nested action editors (`InputModLogicTrigger`), extra buttons (e.g. wiki link), and direct property writes after drawing (`modTrigger.OnHitActions = ...`). They do **not** add new persistent state; everything persists via the reflected property list. `ModPropertyDrawer` is an empty marker interface (unused). [C]
- Gizmos (`[ModCustomGizmo]`, `[ModQuadGizmo]`) are scene-view only and irrelevant for the API.

## 5. Transform

- Edited via **`ModTransform` properties**, which wrap the real `UnityEngine.Transform` (`ModTransform.cs`). The reflected (saved + inspector) properties are `position`, `rotation`, `scale`, plus `guid`; `WorldPosition` and `worldRotation` are `[ModIgnore]` (code-only). [C]
  - `position` get/set = **local** position; set then re-applies `WorldPosition = WorldPosition` (clamp to +/-1000 on each world axis, `maxPosition`). [C]
  - `rotation` get = a cached **local Euler hint** (`m_rotationHint`, so the inspector does not flip angles), set clamps each axis to +/-3600 and writes `localEulerAngles`. `SetHint()` resyncs the hint (called after parent change and world-rotation writes). [C]
  - `scale` = **local** scale, clamped per axis to [0.001, 1000]. [C]
  - `WorldPosition` (get/set world position, clamped), `worldRotation` (get/set world euler). [C]
  - Helpers: `SetPosition(Vector3, bool local)`, `SetRotation(Vector3, bool local)`, `SetScale(Vector3)`, `SetTRS(p, r, s, local)`, `SetParent(Transform, bool worldPositionStays = true)`. They additionally notify `ModBoardChangeTracker.Instance` **only when `IsTrackingChanges`** (runtime board changes; not edit mode). [C]
- Scene-view gizmo (`SceneViewWidget.DoImGuizmo`, `:365-488`) writes `component.WorldPosition`, `component.worldRotation`, `component.scale` / `localScale`; the mode (local/world, pivot/center, snap) are editor settings (`CurGizmoTransformMode`, `CurToolPosition`, snap increments). No undo call - the snapshot loop picks it up. A plugin does not need these settings: just set `ModTransform.position/rotation/scale` (local) or `WorldPosition`/`worldRotation`. [C]
- Rotation is Euler degrees; there is no quaternion property on `ModTransform`. [C]
- Parent changes: `OnTransformParentChanged` -> `SetHint()`. [C]

## 6. Selection (`EditorSelectionManager`, `EditorSystem.GetSelectionManager()`)

Single instance, created by `EditorSystem` (`m_selectionManager`). Not tied to ImGui. [C]

| Member | Notes |
|---|---|
| `List<GameObject> SelectedObjects` (property), `GetSelectedObjectCount()`, `GetSelectedObjectAtIndex(i)`, `GetFirstSelected()`, `GetLastSelected()`, `IsSelected(GameObject)`, `IsAnyObjectSelected()`, `IsParentSelected(go)` | multi-select list; last element = "active" for gizmo |
| `SetSelectedObject(go)` | = `DeselectAllObjectsAndGizmos()` + `AddSelectedObject` |
| `SetSelectedObjects(GameObject[])` | replace selection |
| `AddSelectedObject(go)`, `DeselectObject(go)`, `DeselectAllObjects()`, `DeselectAllObjectsAndGizmos()`, `DeselectAll()` | `AddSelectedObject` also deselects asset + mod-settings selection |
| `HandleGameObjectSelection(go, multiSelect)` | viewport-click semantics (toggle if multi) |
| `GetPrefabRoot(go)` | **every Add/Deselect/IsSelected passes through this**: a descendant of an object with `!supportsChildren` (prefab instance, board space, item, text) is redirected to the outermost such ancestor (`:407-424`) |
| `LockSelection(bool)` / `m_selectionDisabled` | when set, `AddSelectedObject` is a no-op |
| Mod settings: `SelectModSettings()`, `DeselectModSettings()`, `IsModSettingsSelected()` | the "Board/Minigame Settings" pseudo-item in the hierarchy |
| Assets: `SelectAsset(ModAssetExternal)`, `DeselectSelectedAsset(bool changingMod)`, `GetSelectedAsset()`, `IsSelectedAssetDirty()` (compares serialized bytes), `RevertSelectedAsset()` | asset selection is exclusive with object selection; `SelectAsset` can silently fail if the current asset has unapplied import settings |
| Gizmos: `SetSelectedGizmo`, `AddSelectedGizmo`, `DeselectGizmo`, ... | editor handles; ignore |

- Events (public `Action` fields, plain C# delegates, invoked synchronously): `OnSelectObject(GameObject)`, `OnDeselectObject(GameObject)`, `OnSelectAsset`, `OnDeselectAsset`, `OnAttemptedToDeselectDirtyAsset`. `EditorSystem` subscribes `RespondToObjectSelected` (adds outline, expands tree parents: `m_treeState = true` on ancestors, `:1914-1926`) and `HierarchyWidget` subscribes scroll-to-selected. [C]
- Selection is **global state used as the implicit argument** of Duplicate/Delete/Copy/Paste, the snapshot undo and the inspector. [C]
- Null-safety: `GetSelectedObjectAtIndex` logs a warning and returns null for bad indices. Selected objects destroyed externally stay in the list (no cleanup) [?].

## 7. Undo / redo (`EditorUndo`, `BaseUndoEvent`, `GameObjectUndoEvent`, `MoveGameObject`, `DeleteGameObject`, `DuplicateGameObject`)

### 7.1 Architecture [C]
- `EditorSystem.GetUndoManager()` (`EditorSystem.cs:729`) returns one `EditorUndo` for the scene hierarchy (`m_heirarchyUndoManager`) **or one per open prefab** (`m_undoManagerList[CurrentPrefab]`). Stacks are cleared in `SetCurrentMod` / `LoadModIntoEditor`.
- `EditorUndo` fields: `Stack<BaseUndoEvent> m_undoEventStack` (unbounded), `lastGameobjectUndoEvent` (the **baseline** = snapshot of the current selection's state), `m_isModDirty`, a 5 MiB `m_tempBuffer` (`byte[5242880]`) and a `GameObject[1024]` scratch array (hard cap: serializing a selection larger than 1024 objects or 5 MiB would throw [I]).
- Event types (all implement `public interface BaseUndoEvent { void Undo(); }`): `GameObjectUndoEvent` (struct: `GameObject[] objects` + `byte[] data` = serialized snapshot), `MoveGameObject` (parents + sibling indices), `DeleteGameObject`, `DuplicateGameObject`. **No redo exists** (no redo stack or API; only Ctrl+Z and the Edit menu call `Undo()`), **no grouping/transaction API**, **no names/labels**.

### 7.2 The snapshot loop (how ordinary property edits are recorded)
- Every `EditorSystem.Update()` (not in play mode: `UpdatePlayMode` returns early, `:1587-1591`) runs the following (`EditorSystem.cs:1644-1661`, `DoUndoSerialize` `:1793-1807`) [C]:
  - Exactly 1 object selected: if left mouse is **not** held and `Time.time - undo.lastUndoEventTime > 0.1`, run `DoUndoSerialize()`; if mouse held, set `lastUndoEventTime = Time.time` (so continuous drags are coalesced into one event, recorded 0.1 s after release).
  - 0 or >1 selected: run `DoUndoSerialize()` whenever the left mouse is not held (no 0.1 s debounce).
- `DoUndoSerialize`: if the stack is empty -> baseline = current snapshot and push it (first state). Else `SerializeUndoEvent()` (writes `ModHierarchySerializer.SaveTransform(w, go.transform, ignoreRoot:false, ignoreChildren:true)` for every selected object into the temp buffer); if `!IsCurUndoEventEqual()` (different object list or different bytes vs `lastGameobjectUndoEvent`) -> **push the OLD baseline** onto the stack and set baseline = new snapshot (`BuildUndoEvent`). [C]
- Hence the stack holds *previous states*; `Undo()` (`EditorUndo.cs:139`) = peek top, `Undo()` it (for `GameObjectUndoEvent` -> `ModHierarchySerializer.LoadTransform(... ignoreChildren:true)` back into the same objects, then re-select them if the selection differs), fire `OnUndo(go)` per object (hierarchy label refresh), pop, set baseline = current state. [C]
- **What a snapshot covers** (`ModHierarchySerializer.SaveTransform`, `.cs:~85-170`): for each selected object only (no children): name, `activeSelf`, layer, tag; the list of `ModBehaviour` components (type name, guid, `enabled`) restricted to registered types, sorted by save priority; and **all reflected property values** of each. On restore, missing components are added (`GetOrAddComponent` + `SetupComponents`), extra components are destroyed (ModTransform excepted), then values are re-applied with `SettingPropertiesFromLoad = true` and `OnAfterDeserialize()`. So component add/remove, enable/disable, rename, activate, layer/tag, transform and every property edit are undoable **as long as the object was selected when the edit happened and for at least one Update** [I - by reading; not run].

### 7.3 Structural events (explicit, pushed by the caller) [C]
- `MoveGameObject`: ctor records parent/sibling index; `Undo` = `SetParent(oldParent, worldPositionStays:true)`, `SetAsLastSibling`, `SetSiblingIndex(old)`, re-select.
- `DeleteGameObject`: see 2.3; `Undo` reparents back (world pos stays), restores sibling index, re-enables `AssetReference`s, re-selects.
- `DuplicateGameObject`: `Undo` selects the originals and `Object.Destroy`s the clones (hard destroy; clones cannot be restored - no redo anyway).
- After pushing any of these, the caller sets `lastGameobjectUndoEvent = GetCurUndoEvent()` so the snapshot loop does not record the structural change a second time. **If a plugin pushes a structural event and forgets the re-baseline, the next frame pushes a spurious snapshot event** (harmless but doubles undo steps). [C]

### 7.4 Not covered by undo [C/I]
- **Creation**: `AddGameObject` pushes nothing. Newly selected object differs from the baseline -> next frame the loop pushes the *previous selection's* snapshot; Ctrl+Z then merely re-selects the previous object. The new object stays. [I]
- Child objects of the selected object (`ignoreChildren:true`), parent/sibling changes outside `MoveGameObject` (e.g. `Transform.SetParent` by a plugin is not captured by the snapshot), component removal side effect "destroy all children" (`RemoveComponentFromGameObject` for `!supportsChildren`), deletion of children is not restored by a component re-add.
- Asset operations (import, materials, prefab asset creation/deletion, mod settings, post-process), scene-wide settings, play mode, anything done to unselected objects, edits made in the same frame as the selection change (the baseline is captured *after* the edit; see 7.5).
- Undo of selection-only changes is not a thing: selection is restored only as a side effect.
- Dirty flag: `EditorUndo.IsModDirty()`/`SetModAsNotDirty()`; set **only** by `PushUndoEventToStack`; cleared by `EditorSystem.SaveCurrentMod` (`:808-821`) and `ClearUndoStack`. `EditorSystem.IsModDirty` is for the *current* undo manager (scene vs prefab). Edits that never trigger a push (e.g. unselected object) leave the mod "clean" and an exit prompt would not warn. [C]

### 7.5 Recipe so plugin edits behave like user edits [I - derived from the code, needs in-game verification]
1. Run on the Unity main thread, outside any assumption about ImGui frames (all calls below are plain C#).
2. **Property / name / active / layer / component add-remove on object X:**
   1. `sel.SetSelectedObject(X)` (this also makes the baseline correct after the next Update).
   2. **Yield one frame (`yield return null`) and make sure the left mouse button is not held.** The `Update` after selection snapshots X's *pre-edit* state as baseline (and pushes the previous selection's snapshot).
   3. Apply the edit(s) (several edits on X in the same frame = one undo step).
   4. Yield at least one more frame (and > 0.1 s of `Time.time` since the last mouse-hold; practically immediately true). The next `Update` sees the difference, pushes the baseline (marks dirty), sets the new baseline.
   5. For a multi-object edit select all of them (list order matters: `Equal` compares by index) and follow the same two-frame protocol (>1 selected: no debounce).
   - Edit in the **same frame** as `SetSelectedObject` => baseline already contains the edit => the edit is not undoable. [I]
3. **Reparent / reorder:** replicate 2.5: `undo.PushUndoEventToStack(new MoveGameObject(new[]{go}))` -> move (`ModTransform.SetParent(newParent)`, `SetAsLastSibling()`, `SetSiblingIndex(i)`) -> `undo.lastGameobjectUndoEvent = undo.GetCurUndoEvent()`. Selection at the time of the baseline matters (`GetCurUndoEvent` serializes the *selection*).
4. **Delete:** `sel.SetSelectedObject(go)` (or SetSelectedObjects) then `EditorSystem.Instance.Delete()`.
5. **Duplicate:** select source, `EditorSystem.Instance.Duplicate()`; the clone is selected and the destination parent is the source's parent. For a different parent, reparent the clone afterwards with the recipe in step 3 (undoing then undoes the move first, then the duplicate).
6. **Create:** call `AddGameObject(type, parent)`; the object is selected; optionally push `DuplicateGameObject`-like handling is impossible (no create-event type). Accept that creation is non-undoable, or implement a compensation (the plugin delete tool can use `Delete()`, which is undoable).
7. Persist: `EditorSystem.Instance.SaveCurrentMod()` (writes the scene from the live hierarchy; also takes a preview screenshot; ends with `SetModAsNotDirty`). Not under undo.
8. To undo from the API: `EditorSystem.Instance.GetUndoManager().Undo()` (check `CanUndo` first); no redo.

## 8. Prefabs and prefab instances

- Prefab **asset** = `PummelPrefab` (`ModAssetExternal`) with a hidden `PrefabObject` GameObject under `m_prefabsRoot` carrying a `ModPrefab` (marker, `targetPrefab`). Opening one (`ModAssetExternal.Open()` / `EditorSystem.CurrentPrefab = prefab`, setter `:437-495`) deselects, focuses the prefab object, toggles `m_prefabsRoot`/`m_hierarchyRoot` active state, saves the previously open prefab, and switches the undo manager. "Return to Hierarchy" in `HierarchyWidget` (`:308-327`) re-syncs all instances (`ModPrefabInstance.ForceUpdate`) and reimports the asset. [C]
- **Prefab root cannot be deleted** (`Deleteable`), cannot gain `ModPrefabInstance`/`ModBoardSpace`; `ModBoardSpaceEvent` is only addable inside a prefab. [C]
- **Prefab instance** = an object with `ModPrefabInstance` (`supportsChildren == false`, only Transform+PrefabInstance components allowed at add time). It instantiates a copy of the prefab as a child (`UpdatePrefab`, `ModPrefabInstance.cs`), strips `ModPrefab` and outlines, assigns fresh component GUIDs to the copy (`PummelPrefabReferenceUpdater.AssignNewGUIDsToComponents`) and remaps self-references. [C]
- Locked: children are hidden in the hierarchy (`m_ignoreChildTransforms`), not selectable individually (`GetPrefabRoot`), the inspector for an instance shows only `ModTransform` and `ModPrefabInstance` (`InspectorWidget.DrawComponents`, `:233`), no Add Component button (`:179`), save writes only those two components (`SaveComponents` filters; `SaveHierarchy` writes 0 children). **There are no per-instance overrides** - state lives in the prefab asset; instance transform + `PrefabReference` are the only per-instance data. [C]
- Consequences for hierarchy edits: do not try to edit descendants of an instance (they are regenerated on `UpdatePrefab`, discarded on save). Editing the prefab asset requires `CurrentPrefab = ...` (changes `Root()`, undo manager and `AddGameObject` default root), and double-click on an instance opens it. Deleting a prefab asset nulls `PrefabReference` on all instances (`PummelPrefab.OnDestroy`). [C]
- Copy/paste inside a prefab strips nested `ModPrefabInstance` components. [C]

## 9. Gotchas for external callers

1. **Selection as implicit argument**: `Duplicate/Copy/Paste/Delete/CanX` and the undo snapshot all read the selection; destination of Paste = parent of last selected. [C]
2. **`AddGameObject` default root** comes from a private field set only by the context menu; always pass `creationRoot`. It also changes the selection and requests ImGui window focus. [C]
3. **Mouse/keyboard polling**: `EditorSystem.Update` evaluates `Input.GetMouseButton(0)` for undo timing; `PollKeyInput` and gizmo handling read `Input`. If a user holds the left button, snapshots (and therefore dirty/undo) are deferred. Shortcuts (`Ctrl+D/C/V/Z/S`, Delete) only fire when the scene view/hierarchy/toolbar is focused and the inspector is not; these are `Input` based, so synthetic calls to the methods are the safe route. [C]
4. **ImGui-coupled code**: `HierarchyWidget` reparent/reorder/rename/asset-drop, `InspectorWidget` add/remove component UI, `ModEditorGui.DrawValue` are all inline in `Draw()`; no reusable method. Methods that are **safe outside a frame** (pure Unity/C#): `EditorSystem.AddGameObject/Duplicate/Copy/Paste/Delete/SaveCurrentMod`, `ModEditorComponentAdderRemover.*`, `ModReflectedType/ReflectedProperty.GetValue/SetValue`, `EditorSelectionManager.*`, `EditorUndo.*`, `ModTransform.*`, `ModHierarchySerializer.*`. [C/I]
5. **`ModEditorGui.m_curComponent`** (static) is set by the inspector before custom inspectors run and used to find the owning `ModPrefab` for object pickers; irrelevant when bypassing the UI but a reason not to call `DrawValue` from a plugin. [C]
6. **`ModEditorComponentAdderRemover` uses static scratch sets** filled by `SetupComponentAddabilityCheck`; call it right before `PerformChecksForComponentAddability` each time. [C]
7. **Hard destroy vs soft delete**: component removal / `RemoveComponentFromGameObject` use `Object.Destroy` (end of frame; the component is still present until then, so re-query next frame); object delete is a soft reparent. [C]
8. **Component remove destroys children** for `!supportsChildren` types (irreversible via undo). [C]
9. **Timing of `guid` assignment**: new behaviours get a guid only if a mod is loaded and `ModBehaviourMonitor.Instance != null`; the asset registry entry appears on the monitor's next `UpdateBehaviours`. [C]
10. **Play mode**: while `EditorState.PlayMode`, `EditorSystem.Update` returns early (no snapshots, no shortcuts); `ModBehaviour.IsPlayMode` changes setter behaviour (e.g. prefab instance updates). Do not edit during play mode. [C]
11. **Prefab edit context**: `GetUndoManager()`, `Root()`, `AddGameObject` defaults and component-add rules all flip with `CurrentPrefab`. Selection is reset when it changes. [C]
12. **Mod type gating**: component availability and menu items depend on `CurrentMod.ModType`; adding a Minigame-only component to a Board mod via API works technically (nothing enforces it in `AddComponentToGameObject`) but would produce a mod the editor UI would not allow. Validation: `EditorSystem.ValidateCurrentMod()` (`ModValidationSystem`). [C/I]
13. **Name collisions**: no uniqueness is enforced for gameobject names (Empty de-dups its default name only). The API should address objects by guid. [C]
14. **Thread**: `SerializableGuid`/Unity calls must be on the main thread (project convention).

## 10. Open questions / unverified

1. Does `Object.Instantiate` (used by Paste) produce clones with *new* component guids? `ModBehaviour.m_guid` is a protected non-serialized-attribute field and the constructor assigns a new guid only when a mod is loaded, but `Instantiate` copies serialized fields and runs constructors; whether the clone's `m_guid` is regenerated (or `OnPostDuplicate` fixes it) was not found. If duplicated objects could share guids, the guid lookup would be ambiguous. Verify in game (read `ModTransform.guid` of original vs clone).
2. Snapshot-undo timing and "creation is not undoable" are inferred from code, not observed. Verify: create object, press Ctrl+Z.
3. Whether `Transform.SetParent` into a descendant is guarded anywhere (the widget does not check); Unity logs an error and refuses.
4. When `ModAsset.name` (shown in object pickers) is refreshed after a rename: `AddOrUpdateModBehaviours` copies the name but is only called for new/loaded behaviours and `UpdateAllBehaviours(root)` (at `SetActive` for play/edit switching).
5. Whether `ModHierarchySerializer.SaveTransform` with `ignoreChildren:true` throws for very large property sets (5 MiB buffer) or selections > 1024 (scratch array); no explicit guard.
6. Nested property names ("A.B") are supported by `ReflectedProperty` but no type seems to use them; `GetNestedPropValue` has an obvious early `return null` in the FastMember branch (looks like a latent bug), so avoid nested names.
7. Exact folder layout of the Add Component picker (`FilterFolder.PlaceNode`) - not read; the table's category is the attribute `folder` string.
8. `ModRigidbody` / `ModDestructible` have `modTypeFlags = None`, so the picker never offers them; whether they are created some other way (e.g. by Hittable UI, or by other components) was not traced.
9. Behaviour when a selected object is destroyed outside the editor (selection list is not pruned; `GetPrefabRoot` handles null only for the argument).
10. Selection/undo behaviour with `m_selectionDisabled` (`LockSelection`) - who sets it was not traced.
11. `ModAudioListener` is `[ModSerialized(..., addable=true)]` with named `addable=false`: confirmed as non-addable by attribute text; the interaction positional-vs-named ordering is standard C#, so named wins. [C by language rules]
12. Redo: none found in `EditorUndo`, `EditorSystem` or `MainMenuWidget`; absence of evidence across the decompiled root folder only (not exhaustively searched in subfolders).

## 11. Programmatic entry points

"Undo?" = records an undo event or is captured by it; "Dirty?" = sets `EditorUndo.m_isModDirty`; "UI?" = coupled to ImGui frame/popup/mouse.

| Method signature | What it does | Records undo? | Marks dirty? | UI-coupled? | Evidence |
|---|---|---|---|---|---|
| `GameObject EditorSystem.AddGameObject(ObjectType, Transform creationRoot = null)` | Creates object + ModTransform + type-specific components, selects it | No (not undoable) | No (until next snapshot push) | Weak (requests hierarchy window focus; null root uses menu field) | `EditorSystem.cs:2458` |
| `void EditorSystem.Duplicate()` / `Copy()` / `Paste()` | Instantiate copies of selection next to the selected object | Yes (`DuplicateGameObject`) | Yes | No (selection-coupled) | `EditorSystem.cs:2038,1986,1999` |
| `void EditorSystem.Delete()` | Soft-deletes selection (not prefab roots) | Yes (`DeleteGameObject`) | Yes | No (selection-coupled) | `EditorSystem.cs:2047`; `DeleteGameObject.cs` |
| `bool EditorSystem.Deleteable(GameObject)` | false for prefab roots | n/a | n/a | No | `:2072` |
| `new MoveGameObject(GameObject[])` + `ModTransform.SetParent(Transform, bool=true)` + `Transform.SetSiblingIndex` | Reparent / reorder (recipe, inlined in widget) | Only if you push `MoveGameObject` | Only via push | Inlined in `HierarchyWidget` drag-drop | `HierarchyWidget.cs:521-555, 652-707` |
| `void EditorUndo.PushUndoEventToStack(BaseUndoEvent)` | Pushes event, sets dirty | (is the record) | Yes | No | `EditorUndo.cs:58` |
| `GameObjectUndoEvent EditorUndo.GetCurUndoEvent()` / `lastGameobjectUndoEvent` setter | Serialize current selection; re-baseline | n/a | No | No | `EditorUndo.cs:106` |
| `void EditorUndo.Undo()` ; `bool CanUndo` | Pop and apply top event; no redo | n/a | No | No | `EditorUndo.cs:139,36` |
| `void EditorUndo.ClearUndoStack()`; `SetModAsNotDirty()`; `IsModDirty()`; `UndoStackLength()` | stack mgmt | n/a | clears | No | `EditorUndo.cs:159,53,48,170` |
| `EditorUndo EditorSystem.GetUndoManager()` | scene or current-prefab manager | n/a | n/a | No | `EditorSystem.cs:729` |
| `Component ModEditorComponentAdderRemover.AddComponentToGameObject(GameObject, Type)` | AddComponent + `OnAddComponent` event | Captured by snapshot if object selected | via snapshot | No | `ModEditorComponentAdderRemover.cs:19` |
| `void ...RemoveComponentFromGameObject(Component)` | Destroys children if `!supportsChildren`, destroys component, `OnRemoveComponent` | Component part captured by snapshot; children destruction not | via snapshot | No | `:31` |
| `void ...SetupComponentAddabilityCheck(List<GameObject>)`; `(bool, string) ...PerformChecksForComponentAddability(ModReflectedType)` | Addability rules (static state) | n/a | n/a | No | `:44,80` |
| `bool EditorSystem.CanUseItem(PummelModTypeFlags)` | Mod-type gating | n/a | n/a | No | `EditorSystem.cs:2707` |
| `ModReflectedType ModReflectionSystem.GetType(Type)` / `GetType(int)` / `GetReflectedType(Type)` / `TypeCount` | Registry lookup/enumeration | n/a | n/a | No | `ModReflectionSystem.cs:48-69` |
| `object ReflectedProperty.GetValue(object)` / `void SetValue(object, object, int index = -1)` | Read/write a reflected property | Captured by snapshot if selected | via snapshot | No | `ReflectedProperty.cs` |
| `ModEditorGui.DrawValue(...)` | ImGui widget for a property | n/a | n/a | **Yes** (ImGui only) | `ModEditorGui.cs:332-344` |
| `ModTransform.position/rotation/scale/WorldPosition/worldRotation`, `SetPosition/SetRotation/SetScale/SetTRS/SetParent` | Transform edit | Captured by snapshot if selected | via snapshot | No | `ModTransform.cs` |
| `GameObject.name`, `GameObject.SetActive`, `GameObject.SetLayer(int, bool)` | Rename / active / layer (direct writes) | Captured by snapshot if selected | via snapshot | No | `HierarchyWidget.cs:499`, `InspectorWidget.cs:139,150,168` |
| `EditorSelectionManager.SetSelectedObject/SetSelectedObjects/AddSelectedObject/DeselectObject/DeselectAll...` | Selection | No | No | No | `EditorSelectionManager.cs:112-181` |
| `void EditorSystem.SaveCurrentMod(bool delayed = false)` | Writes mod to disk, clears dirty | n/a | clears | No (screenshot render) | `EditorSystem.cs:808` |
| `ModHierarchySerializer.SaveTransform/LoadTransform(...)` | binary snapshot of an object (undo, save) | n/a | n/a | No | `ModHierarchySerializer.cs` |
| `Transform EditorSystem.Root()` / field `m_hierarchyRoot` / `CurrentPrefab` | Scene roots, prefab context | n/a | n/a | No | `EditorSystem.cs:952,91,437` |
| `ModAsset WorkshopController.GetAssetWithGuid(SerializableGuid)` | guid -> registered asset (`unityObjectAddressable`) | n/a | n/a | No | `WorkshopController.cs:777` |
