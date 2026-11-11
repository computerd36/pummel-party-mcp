# PummelBridge HTTP API (api v2, Phase 2 increment 2, plugin 0.3.0)

The BepInEx plugin `PummelBridge.Plugin` runs inside Pummel Party and exposes the mod editor's state on a
local HTTP endpoint. api v2 adds the guard layer, the always-on Workshop publish block and four mod lifecycle
tools (`list_mods`, `create_test_mod`, `open_test_mod`, `save_mod`). Increment 2 (still api v2, all changes are
additive) adds object, transform, component and property editing plus `undo`; see [Editing](#editing). Write
tools only ever act on `PB_TEST_` mods; see [Safety](#safety). Action editing, playtest and batch tools come in
later increments.

## Transport

| Item | Value |
|---|---|
| Endpoint | `POST http://127.0.0.1:<port>/rpc` (only this path and verb exist) |
| Bind address | `127.0.0.1` only (TCP listener on the loopback interface) |
| Port | BepInEx config `BepInEx\config\com.pummelbridge.plugin.cfg`, section `[Server]`, `Port` (default `38917`, range 1024-65535) |
| Enable/disable | `[General] PluginEnabled` (master switch) and `[Server] Enabled` (server only); see [Safety](#safety) |
| Token file | `BepInEx\config\PummelBridge.token`: 64 lowercase hex chars (32 CSPRNG bytes), created on first start, reused afterwards, regenerated if malformed |
| Connection | one request per connection; every response has `Connection: close` |
| Request body | UTF-8 JSON, `Content-Type: application/json` (parameters such as `; charset=utf-8` allowed), `Content-Length` required, max 1 MB (1 048 576 bytes); chunked bodies are refused |
| Concurrency | up to 16 requests in flight; more get HTTP 503 |

Required headers:

```
Host: 127.0.0.1:<port>          (or localhost:<port>)
X-PummelBridge-Token: <token>
Content-Type: application/json
Content-Length: <n>
```

### Request checks (in this order) and HTTP status codes

| Check | Status | `error.code` in the body |
|---|---|---|
| peer is not a loopback address | 403 | `FORBIDDEN` |
| malformed request line/headers, duplicate `Host`/`Content-Length`/token header | 400 | `BAD_REQUEST` |
| headers larger than 16 KB | 431 | `HEADERS_TOO_LARGE` |
| any `Origin` header present (even `null`) | 403 | `FORBIDDEN` |
| `Host` is not exactly `127.0.0.1:<port>` or `localhost:<port>` | 403 | `FORBIDDEN` |
| token header missing or wrong (constant-time compare) | 401 | `UNAUTHORIZED` |
| path is not `/rpc` | 404 | `NOT_FOUND` |
| verb is not `POST` | 405 (+ `Allow: POST`) | `METHOD_NOT_ALLOWED` |
| content type is not `application/json` | 415 | `UNSUPPORTED_MEDIA_TYPE` |
| `Transfer-Encoding` present or no valid `Content-Length` | 411 | `LENGTH_REQUIRED` |
| body larger than 1 MB | 413 | `PAYLOAD_TOO_LARGE` |
| headers/body not received within 10 s | 408 | `REQUEST_TIMEOUT` |
| body is not valid UTF-8 / not JSON / not an object / `method` not a non-empty string / `params` not an object | 400 | `INVALID_PARAMS` |
| more than 16 requests in flight | 503 | `SERVER_BUSY` |

Authentication is checked before routing, so an unknown path without a token gets 401, not 404.
All non-200 responses use the same `{"ok":false,"error":{...}}` body shape; clients should branch on the HTTP
status first.

## Envelope

Request:

```json
{"method": "get_object", "params": {"guid": "6f1c..."}}
```

`params` may be omitted or `null` (treated as `{}`).

Response (HTTP 200 for every well-formed, authenticated call):

```json
{"ok": true, "result": { ... }}
{"ok": false, "error": {"code": "NO_MOD_OPEN", "message": "no mod is open in the editor"}}
```

| Code | Meaning |
|---|---|
| `UNKNOWN_METHOD` | no such method in this build |
| `INVALID_PARAMS` | a parameter has the wrong type/range, or a GUID is malformed |
| `EDITOR_NOT_OPEN` | `EditorSystem.Instance` is null (main menu, in a normal game, ...) |
| `NO_MOD_OPEN` | editor open, no mod loaded |
| `BUSY_LOADING` | the editor is loading a mod (`IsLoading`) or the mod has not finished loading (`!IsLoaded`) |
| `NOT_FOUND` | object / component not found |
| `TIMEOUT` | the Unity main thread did not service the call within 10 s (the queued work is discarded, it never runs later) |
| `INTERNAL` | unexpected exception, or a game member the plugin needs is missing (`member X.Y not found — game version changed?`) |
| `WRITES_DISABLED` | write tool called while `[Safety] EnableWrites = false` |
| `PUBLISH_BLOCK_INCOMPLETE` | write tool called while the Workshop publish block is not complete (`get_status.publishBlock`) |
| `GUARD_NOT_TEST_MOD` | the open mod (open-mod tools) or the target / open mod (lifecycle tools) is not a `PB_TEST_` mod; the message names it |
| `INVALID_STATE` | editor state forbids the call: play mode, unsaved changes, a prefab being edited, an open editor dialog, a broken editor mod list, an object outside the current editing context, the left mouse button held in the game window, the open mod changed while an edit was pending (the message says what to do) |
| `ALREADY_EXISTS` | `create_test_mod`: a folder or a mod (Meta name) with that name exists, case-insensitive |

Edit tools additionally use `NOT_FOUND` (object / component / the edited object was deleted meanwhile),
`INVALID_PARAMS` (bad value, refused property, a rule the editor's UI enforces) and `TIMEOUT` (the editor did not
take its undo snapshot in time, or the call waited too long in the write queue).

Values in results: enums as their names (flags as `"A, B"`), `Vector2/3/4` and `Quaternion` as `{x,y,z[,w]}`,
`Color` as `{r,g,b,a}` (0-1 floats), `Color32` as 0-255 ints, GUIDs as lowercase `8-4-4-4-12` strings, NaN/Infinity
as strings. Asset or object references are objects (see "References").

### Threading

`ping` and `get_log` (without `includeEditor`) are answered on the HTTP thread and work even while the game's
main thread is busy. Everything else is queued to the plugin's `Update()` (max 16 items / 8 ms per frame) and
waits up to 10 s (`create_test_mod` / `open_test_mod`: 35 s, with their own 30 s load limit inside; edit tools
of increment 2: 15 s).

Write tools: params are type-checked on the HTTP thread (`INVALID_PARAMS`). On the main thread every write tool
joins one **write queue**: only one write tool body runs at a time, later calls wait in arrival order. When a
call's turn comes, the guard checks and the start of the body run in **one** unit of work, so the editor state
cannot change in between; the guard is evaluated at that moment, not when the call arrived. A write body may span
several frames (mod loads, the editor's undo snapshots); it re-validates the editor state every frame and fails
with an error instead of touching a stale object if the mod is switched, unloaded or reloaded, a prefab is opened
or closed, a playtest starts or the edited object disappears. The timeout covers the time spent waiting in the
queue; a call that times out while still queued never runs. A `create_test_mod`/`open_test_mod` holds the queue
until its load finishes, so other write calls wait (and may time out) meanwhile. Read tools are not queued and run
between frames as before.

---

## Methods

### `ping`

Works with the editor closed. No params.

```json
{"pong": true, "pluginVersion": "0.3.0", "gameVersion": "1.15.2", "unityVersion": "2021.3.45f2",
 "apiVersion": 2, "sessionId": "3b0f2c...e1", "capabilities": ["read", "write", "edit"]}
```

`readOnlyBuild` was removed in api v2. `capabilities` lists what this build implements (`edit` = the object,
component, property and undo tools of increment 2); whether writes are currently allowed is in `get_status`
(`writesEnabled`, `publishBlock.complete`).

`sessionId` is random per game start; a change means the game restarted (log ids restart at 1).

### `get_status`

No params. Never fails because of editor state.

```json
{"editorOpen": true, "editorState": "Editing", "isLoading": false, "modOpen": true,
 "modName": "PB_TEST_Arena", "modType": "Minigame", "modLoaded": true, "isDirty": true,
 "hasUnsavedEdits": false, "unsavedEditsSource": "tracker",
 "undoStackLength": 1, "prefabContext": null, "playMode": false,
 "activeScene": "ModEditorScene", "capabilities": ["read", "write", "edit"], "writesEnabled": true,
 "publishBlock": {"active": true, "complete": true, "patchedCount": 59, "missing": []}}
```

- `writesEnabled`: current value of `[Safety] EnableWrites`.
- `publishBlock`: `active` = at least one blocking patch installed; `complete` = every required target patched
  and no patch failed (write tools need this); `patchedCount` = methods patched (59 expected for game build
  2025-10-16: 10 game methods, 25 `SteamUGC`, 24 `SteamRemoteStorage`); `missing` = `"<target>: <reason>"` for
  each target that is missing (required) or whose patch failed. `readOnlyBuild` was removed in api v2.

- `editorState`: `Editing | EnteringPlayMode | PlayMode | ExitingPlayMode` (null when the editor is closed).
- `playMode`: `editorState != "Editing"` (a playtest is starting, running or ending).
- `isDirty`: the editor's own dirty flag of the current undo context (scene, or the open prefab), read from fields.
  It is set by every undo push, including the initial snapshot the editor pushes on the first frame after any load,
  so it is `true` for an untouched mod (confirmed in-game). `null` if the field layout changed or no mod is open.
- `hasUnsavedEdits`: the bridge's view, used by `create_test_mod`/`open_test_mod` (see [Unsaved edits](#unsaved-edits)).
  `null` when no mod is open.
- `unsavedEditsSource`: `"tracker"` (the bridge's own tracking) or `"editorFlag"` (fallback to `isDirty`: the mod was
  already open with a long undo history when the plugin first saw it, or the save watch could not be installed);
  `null` when no mod is open.
- `prefabContext`: `{"name": "Coin", "guid": "..."}` while a prefab asset is being edited, else null.

### `get_open_mod`

No params. Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`.

```json
{
  "name": "PB_TEST_Arena", "folderName": "PB_TEST_Arena", "type": "Minigame", "className": "MinigameMod",
  "usesHierarchy": true, "usesLighting": true, "usesPostProcessing": true,
  "metadata": {"MetaDataVersion": 1, "ModVersion": 1, "ModCreatedVersion": 1, "Name": "PB_TEST_Arena",
               "ModType": 3, "LastModified": "2026-11-10T17:00:00", "SelectedLightingPreset": "",
               "SelectedPostProcessingPreset": "", "IsUnlocked": false},
  "settingsClass": "MinigameModSettings",
  "settings": {"Version": 0, "SimpleMinigameSettings": {"...": "..."},
               "SimpleModPlayerSettings": {"movementSpeed": 6.0, "onDeathTrigger": {"triggerType": "None", "...": "..."}}},
  "lightingPreset": "Custom", "postProcessingPreset": "Custom",
  "publishedFileId": "256", "isPublished": false
}
```

- `metadata` and `settings` are generic JSON trees of the objects' Unity-serialized fields (what `Meta.json` /
  `ModSettings.json` store). Triggers inside settings use the trigger shape of `get_action_graph`.
- From the workshop item only `publishedFileId` (as a **string**, Steam ids are 64-bit) and `isPublished`
  (`publishedFileId != 256`) are exposed.

### `get_hierarchy`

| Param | Type | Default | |
|---|---|---|---|
| `rootGuid` | string (GUID) | - | start at this object instead of the context root |
| `maxDepth` | int 0-64 | 64 | levels of children below the first returned mod-object level |
| `includeInactive` | bool | true | false skips inactive objects and their subtrees |

Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`, `NOT_FOUND`, `INVALID_PARAMS`.

Without `rootGuid` the result is a synthetic node for the current context root (scene hierarchy, or the prefab
root while a prefab is edited). Its direct children are level 0. With `rootGuid` the given object is level 0.

```json
{
  "guid": null, "name": "HierarchyRoot", "context": "scene", "prefab": null, "active": true,
  "components": [], "childCount": 2, "truncated": false, "nodeCount": 3,
  "children": [
    {"guid": "0b6d...", "name": "Floor", "active": true, "components": ["ModProp", "ModBoxCollider"],
     "childCount": 0, "children": []},
    {"guid": "51aa...", "name": "Spawner", "active": true, "components": ["ModPrefabInstance"],
     "childCount": 0, "childrenHidden": true, "children": []},
    {"guid": "9c02...", "name": "Logic", "active": false, "components": ["ModLogic"],
     "childCount": 4, "children": null, "depthLimited": true}
  ]
}
```

- `components`: mod component class names (`ModReflectionSystem` types), excluding `ModTransform` (every object has one).
- Objects with a component that does not support children (prefab instance, board space, item, text; matched the way
  `EditorSystem.ShouldHideChildGameObjects` does) report `childCount: 0`, `children: []` and `childrenHidden: true`
  if Unity children exist: they are generated/locked, not shown by the editor and not stored in the scene file.
- `depthLimited: true` + `children: null`: more levels exist beyond `maxDepth` (`childCount` is still exact).
- Hard cap 10 000 nodes; then `truncated: true` on the returned root.
- Only objects with a `ModTransform` are listed (editor helper objects are not under the roots anyway).

### `get_object`

Params: `guid` (required, the object's `ModTransform` guid). Errors: as above + `NOT_FOUND`.

Search order: current context root, then the other root (scene vs. prefab objects); children of
"hides-children" objects are not addressable.

```json
{
  "guid": "0b6d...", "name": "Floor", "active": true, "activeInHierarchy": true, "layer": 0, "tag": "Untagged",
  "path": "Arena/Floor", "context": "scene", "parentGuid": "77e1...", "childCount": 0,
  "transform": {"localPosition": {"x": 0, "y": -0.5, "z": 0},
                "localEulerAngles": {"x": 0, "y": 45, "z": 0},
                "localScale": {"x": 20, "y": 1, "z": 20}},
  "components": [
    {"type": "ModTransform", "uiName": "Transform", "guid": "0b6d...", "enabled": true,
     "properties": {"position": {"type": "Vector3", "value": {"x": 0, "y": -0.5, "z": 0}},
                    "rotation": {"type": "Vector3", "value": {"x": 0, "y": 45, "z": 0}},
                    "scale": {"type": "Vector3", "value": {"x": 20, "y": 1, "z": 20}},
                    "guid": {"type": "SerializableGuid", "value": "0b6d...", "hiddenInInspector": true}}},
    {"type": "ModProp", "uiName": "Prop", "guid": "c3f9...", "enabled": true,
     "properties": {"prop": {"type": "Prop", "value": {"guid": "1d2e...", "name": "Cube", "assetType": "Prop", "location": "internal"}},
                    "brokenThing": {"type": "Single", "error": "Object reference not set to an instance of an object"}}},
    {"type": "ModTrigger", "uiName": "Trigger", "guid": "aa10...", "enabled": true,
     "properties": {"OnEnterActions": {"type": "ModLogicTrigger", "value": {"triggerType": "None", "actionCount": 2}}}}
  ]
}
```

- `parentGuid` is null when the parent is the context root.
- `properties` = the component's reflected property list from `ModReflectionSystem` (public instance properties,
  `[ModIgnore]` excluded): exactly what the inspector, the undo snapshot and `MainScene.scene` use. Each entry is
  `{type, value}`, or `{type, error}` if the getter threw, or `{type, unavailable}` (see below).
  `hiddenInInspector: true` marks `[ModHideInInspector]` properties.
- Triggers are summarized as `{triggerType, actionCount}`; use `get_action_graph` for the actions.
- Nested serializable classes are walked by their Unity-serialized fields (depth limit 8, arrays capped at 512
  items followed by `{"truncatedItems": n}`); deeper levels give `{"truncated": "<type>"}`; types the converter does
  not know give `{"unsupported": "<type>"}`.
- **Side-effect-free reading.** Getters the game implements with side effects are not called:
  - components whose property wrappers go through `ModBehaviour.Component` while `m_component` is null (reading
    would run `SetupComponents()`, which adds Unity components) report every property as `unavailable`;
  - `ModItem.ItemPrefab/ItemPrefabGUID/DroppedItemPrefab/DroppedItemPrefabGUID`,
    `ModPrefabInstance.PrefabReference/PrefabGUID`, `ModSpawner.Prefabs/PrefabGUIDs` are read from their backing
    fields (the getters rewrite caches and can clear the stored guid);
  - `ModMeshRenderer.materials`, `ModLineRenderer.Materials`, `ModSkinnedMeshRenderer.materials` are read from the
    cache field or `Renderer.sharedMaterials` (the getter fills a cache);
  - `ModParticleSystem` renderer properties are `unavailable` while its renderer field is null (the getter would
    add a `ParticleSystemRenderer`);
  - `ModScriptReference.scriptValues` is always `unavailable` (binary serializer).

### `get_action_graph`

| Param | Type | |
|---|---|---|
| `guid` | string (GUID), required | object guid |
| `component` | string | optional filter: component class name (`ModLogic`), UI name (`Logic`) or component guid |

Errors: as above + `NOT_FOUND` (object, or no component matches the filter).

Result: an **array** with one entry per trigger slot of the object's components (reflected properties of type
`ModLogicTrigger` / `ModLogicTrigger[]`: `ModLogic.Triggers[i]`, `ModTrigger.OnEnterActions`, `ModItem.OnUseTrigger`,
`ModBoardSpaceEvent.OnEnterTrigger`, ...). Empty array if the object has no triggers.

```json
[
  {"component": "ModLogic", "componentGuid": "aa10...", "triggerName": "Triggers[0]",
   "triggerType": "Timer", "triggerClass": "TimerTrigger",
   "triggerParameters": {"m_interval": {"valueType": "Constant", "min": 2.0, "max": 2.0}},
   "actions": [
     {"index": 0, "type": "Wait", "className": "WaitAction", "version": 0, "targetFlags": null, "targets": [],
      "parameters": {"m_seconds": {"valueType": "Constant", "min": 1.0, "max": 2.0}}},
     {"index": 1, "type": "SetActive", "className": "SetActiveAction", "version": 0,
      "targetFlags": "Advanced",
      "targets": [{"guid": "0b6d...", "name": "Floor", "assetType": "Transform", "location": "runtime",
                   "objectGuid": "0b6d...", "objectName": "Floor"}],
      "parameters": {"m_newState": "Toggle"}}
   ]}
]
```

- `type`: `ModActionType` name; `targetFlags`: `ModActionTargets` flags (`Source`, `Receiver`, `Advanced`) or null for
  actions without targets (`Wait`, `SetGravity`).
- `parameters`: the action's Unity-serialized fields (private `[SerializeField]` included) except `m_type`,
  `m_version`, `m_targetFlags`, `m_targets`. Keys are the field names = the JSON keys in the mod file.
- `triggerParameters`: the trigger's serialized fields except `m_type`, `m_actions` (e.g. `HitTrigger.m_hitType`).
- Nested triggers (Display Popup choices) appear inside `parameters` with the same trigger shape.

### `list_assets`

| Param | Type | Default | |
|---|---|---|---|
| `type` | string | - | case-insensitive exact match on `type`, `className` or the internal asset type string |
| `includeBuiltIn` | bool | false | also list the game's built-in (internal) assets (over 16,000: use `search`/`type` and paging) |
| `search` | string | - | case-insensitive substring match on the asset name; whitespace-separated terms must all match |
| `limit` | int | 100 | page size, 1..500 |
| `offset` | int | 0 | number of matching assets to skip, >= 0 |

Errors: `INVALID_PARAMS` (bad `limit`/`offset`), `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`.

Result: an object. `assets` is sorted by `type`, then `name`, then `guid` (ordinal, ignore case) before paging.
`total` and `hasMore` describe the filtered set; `typeCounts` counts assets per type after `includeBuiltIn` and
`search` but ignoring the `type` filter, so a caller can see which types exist. Example for
`{"search": "crate", "type": "Prop", "includeBuiltIn": true, "limit": 2}`:

```json
{
  "total": 3, "offset": 0, "limit": 2, "returned": 2, "hasMore": true,
  "typeCounts": {"Material": 1, "Prop": 3},
  "assets": [
    {"guid": "1d2e...", "name": "Crate", "type": "Prop", "className": "ModAsset", "location": "internal"},
    {"guid": "4a5b...", "name": "Crate Wood", "type": "Prop", "className": "ModAsset", "location": "internal"}
  ]
}
```

- `type`: external assets use the asset class without the `Pummel` prefix (`Prefab`, `Material`, `Texture`,
  `AudioClip`, `Mesh`, `Prop`, `VisualScript`); internal ones use their Unity type (`Prop`, `Material`,
  `Texture2D`, `AudioClip`, `Shader`).
- `relativePath` (external only): path inside the mod folder with `/`. Absolute paths are never returned.
- Entries with location `runtime` (scene components registered for guid lookups) are not assets and are skipped.

### `get_log`

| Param | Type | Default | |
|---|---|---|---|
| `max` | int 1-2000 | 200 | max entries returned |
| `sinceId` | int >= 0 | - | return entries with `id > sinceId`, oldest first |
| `includeEditor` | bool | false | also return the editor Log window's buffer (needs the main thread) |

Source: a 2000-entry ring buffer fed by `Application.logMessageReceivedThreaded` from plugin start (all Unity log
output of the game, editor and playtests). Without `sinceId` the newest `max` entries are returned.

```json
{"entries": [{"id": 41, "time": "2026-11-10T17:05:01.123Z", "level": "error",
              "message": "NullReferenceException: ...", "source": "unity", "stack": "ModLogic.Update () ..."},
             {"id": 42, "time": "2026-11-10T17:05:02.004Z", "level": "info", "message": "Saved", "source": "unity"}],
 "nextId": 42, "dropped": false, "sessionId": "3b0f2c...e1"}
```

- `level`: `info | warning | error | exception | assert`. `stack` only for error/exception/assert (max 4000 chars);
  messages are capped at 8000 chars.
- Tail the log by passing the previous `nextId` as `sinceId`. `dropped: true` means entries after `sinceId` were
  overwritten before being read. Compare `sessionId` to detect a game restart.
- `includeEditor: true` adds `editorEntries: [{index, level, message, source: "editor", stack?}]`: the static
  buffer of the game's `Console` class that the editor's Log window displays (last ~100 messages since the window's
  Clear button). It contains the same Unity messages as `entries`, so it is opt-in; it is useful to see exactly what
  the user sees in the Log window.

### `screenshot`

Params: `maxWidth` (int 64-7680, default 1280). Works with the editor open or closed.

```json
{"pngBase64": "iVBORw0KGgoAAAANSUhEUgAA...", "width": 1280, "height": 720}
```

Captured on the main thread after `WaitForEndOfFrame` with `ScreenCapture.CaptureScreenshotAsTexture()`, downscaled
(keeping aspect ratio) when wider than `maxWidth`, PNG-encoded; all temporary textures are destroyed. The capture
is not written to disk.

### `list_mods`

No params. Errors: `EDITOR_NOT_OPEN`. Read-only.

```json
[{"name": "My Real Mod", "folderName": "My Real Mod", "type": "Minigame", "isTestMod": false, "isOpen": false},
 {"name": "PB_TEST_Sandbox", "folderName": "PB_TEST_Sandbox", "type": "Minigame", "isTestMod": true, "isOpen": true}]
```

- Source: the editor's cached mod list (`EditorAvailableModManager`), not a rescan (`ReadListOfAvailableMods` would
  rebuild the editor's list). The list is built when the editor opens and updated by the editor's own
  create/rename and by `create_test_mod`; folders added on disk afterwards appear after re-entering the editor.
  Sorted by `folderName` (ordinal, ignore case). No absolute paths.
- `isTestMod`: both `name` (Meta.json) and `folderName` pass the test-mod rule. `isOpen`: it is the editor's current mod.

### `create_test_mod` (write, lifecycle)

| Param | Type | Default | |
|---|---|---|---|
| `name` | string, required | - | must pass the test-mod rule; becomes folder name and Meta name |
| `type` | `"Minigame"` or `"Board"` | `"Minigame"` | exact spelling |
| `discardUnsaved` | bool | false | additive extra (the MCP server does not send it): allow dropping unsaved changes of the open test mod |

Creates a blank mod the way File > New Mod > "Empty" does (`EditorSystem.CreateNewMod(path, type)` with no
template, plus `AddAvailableMod`) and opens it. Besides the guard it checks: the game's filename rule
(`EditorPopup.IsValidFilenameInput`, which the popup applies and `CreateNewMod` skips); no folder and no mod
(editor list and every `Data/Meta.json` on disk) with that name, case-insensitive (`ALREADY_EXISTS`); leaving
the open mod is allowed (rules under `open_test_mod`). Completes from a later frame once the mod is loaded and its
delayed first save has written `Data/Meta.json` and `Data/WorkshopItem.json`; `TIMEOUT` after 30 s.

```json
{"name": "PB_TEST_Arena", "folderName": "PB_TEST_Arena", "type": "Minigame", "publishedFileId": "256"}
```

Errors: guard codes, `INVALID_PARAMS`, `ALREADY_EXISTS`, `INVALID_STATE`, `BUSY_LOADING` (another create/open is
still waiting for its load), `TIMEOUT`, `INTERNAL` (load failed; the editor shows its own error popup).

### `open_test_mod` (write, lifecycle)

| Param | Type | Default | |
|---|---|---|---|
| `name` | string, required | - | folder name, matched exactly (case-sensitive) among the folders in the mod folder |
| `discardUnsaved` | bool | false | allow dropping unsaved changes of the open test mod |

Opens an existing test mod with `EditorSystem.OpenMod` (what File > Open Mod ends in, minus its popup).
Refused if the name fails the rule even when such a folder exists; `NOT_FOUND` if there is no such folder or it has
no `Data/Meta.json`; `GUARD_NOT_TEST_MOD` if its Meta.json name is not a test-mod name. If that mod is already open
and loaded: success without reloading, `alreadyOpen: true`. Completes after the asynchronous load; `TIMEOUT`
after 30 s.

```json
{"name": "PB_TEST_Arena", "folderName": "PB_TEST_Arena", "type": "Minigame", "publishedFileId": "256", "alreadyOpen": false}
```

Leaving the open mod (create and open) gives `INVALID_STATE` unless:
- no editor dialog is open (`EditorPopupManager.CurrentPopup == null`);
- no prefab is being edited (leaving the prefab context would save that prefab);
- the open mod has no unsaved edits (`get_status.hasUnsavedEdits` is false), or `discardUnsaved: true`. This mirrors
  the editor's own "unsaved changes" popup, but with the bridge's tracking (see [Unsaved edits](#unsaved-edits))
  instead of the editor's flag, which is already true for an untouched, freshly opened mod. An untouched mod can now
  be left without `save_mod`/`discardUnsaved`. The bridge never auto-saves. The error message names the first
  reason (e.g. `scene undo stack changed`, `bridge: set_property`).

### `save_mod` (write, open mod)

No params. The editor's own save (`EditorSystem.SaveCurrentMod`: all mod files, `Data/Preview.jpg`, clears the
current dirty flag), after the guard (loaded test mod, not loading, edit mode) and the Workshop id hygiene. If the
mod's Meta name is missing from the editor's mod list, the entry is added first (otherwise the editor's
`UpdateLoadedModLastModifiedTime` would insert a broken nameless entry).
Saving while a prefab is being edited is allowed, as with Ctrl+S: the scene file stores each object's own active
flag, so the deactivated scene root does not change it, and the prefab is saved with the other assets. In that
case the scene's dirty flag stays set and `Preview.jpg` shows the prefab view.

```json
{"saved": true, "name": "PB_TEST_Arena", "isDirty": false, "hasUnsavedEdits": false}
```

`isDirty`: dirty flag of the current undo context right after the save (same source as `get_status.isDirty`).
`hasUnsavedEdits`: the bridge's view right after the save (false; a save is a clean point for the whole mod).

The scene file is written from the live hierarchy (`ModDataReaderWriter.SaveScene(m_hierarchyRoot)`), so every
change is saved, also changes that are not on the undo stack (e.g. on objects that were never selected).

### `list_component_types` (read)

| Param | Type | Default | |
|---|---|---|---|
| `guid` | string (GUID) | - | optional object: also apply the per-object rules of `add_component` |

Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`, `NOT_FOUND` (guid), `INVALID_PARAMS`. Works on any open
mod (read-only; it evaluates the rules without touching the editor's state).

Result: an **array** with one entry per registered mod component type, in registry order (Transform first):

```json
[{"type": "ModTransform", "uiName": "Transform", "category": "Default", "addable": false,
  "reason": "Transform is not offered by the editor's Add Component picker",
  "allowsMultiple": false, "supportsChildren": true, "modTypes": ["Minigame", "Board", "Item", "BoardSpace"]},
 {"type": "ModLight", "uiName": "Light", "category": "Rendering", "addable": true,
  "allowsMultiple": false, "supportsChildren": true, "modTypes": ["Minigame", "Board", "Item", "BoardSpace"]},
 {"type": "ModTrigger", "uiName": "Trigger", "category": "Gameplay", "addable": false,
  "reason": "Trigger is only available in Minigame mods (this is a Board mod)",
  "allowsMultiple": false, "supportsChildren": true, "modTypes": ["Minigame"]}]
```

- `type`: class name (use it, or `uiName`, with `add_component`). `category`: the picker folder (`Default` = top level).
- `addable`: the editor's Add Component picker would offer **and enable** it: the type is addable, allowed for this
  mod type, allowed in the current context (prefab edit or scene) and, with `guid`, allowed on that object (see
  `add_component`; an object outside the current editing context is never addable). `reason` (only when not
  addable): the editor's own tooltip text where the editor has one, else the bridge's.
- `allowsMultiple`: no `[DisallowMultipleComponent]` (more than one per object possible). `supportsChildren`:
  false for types whose object cannot have children (Prefab Instance, Board Space, Item, Text). `modTypes`: the
  type's mod-type flags (empty for RigidBody and Hittable: never offered).

---

## Editing

Object, transform, component, property and undo tools (all write tools with guard kind **OpenMod**, except the read
tool `list_component_types` above). They drive the editor through its own methods and its own undo system.

### Common rules

- **Guard** (see [Safety](#safety)): a loaded test mod is open (Meta and folder name pass the rule), not loading,
  state `Editing`. Every edit tool also refuses with `INVALID_STATE` while an editor dialog is open and while a
  selected asset has unapplied import settings (selecting an object would pop up the editor's "Unapplied Import
  Settings" dialog), and with `BUSY_LOADING` while a `create_test_mod`/`open_test_mod` load is pending.
- **Addressing**: objects by their `ModTransform` guid (`guid` in `get_hierarchy` / `get_object`); components by
  component guid, class name (`ModLight`) or UI name (`Light`), case-insensitive. A name that matches several
  components of the object is refused (`INVALID_PARAMS`; the message lists their guids).
- **Editing context**: the scene (objects below the scene root) or, while a prefab is open in the editor, that
  prefab's root object and its children. Objects of the other context give `INVALID_STATE` ("return to the scene
  hierarchy first" / "open that prefab in the editor"); the bridge never opens or closes prefabs. Children of
  objects that hide their children (prefab instances, board spaces, items, texts) are not addressable (`NOT_FOUND`).
- **Undoable like a user's edit.** The editor's undo is a snapshot of the selected objects, taken every frame by
  `EditorSystem.Update` while the left mouse button is up; an edit is undoable only if a snapshot of the object was
  taken before the edit. An edit of an existing object therefore runs: (1) select the object if it is not already the
  only selected object; (2) wait until the editor's undo baseline is a snapshot of exactly that object (checked every
  frame, at most 5 s; normally the same or the next frame, depending on Unity's script order, provided the left mouse
  button is up and has been up for 0.1 s); (3) apply the whole edit in one frame (all properties of one call = **one**
  undo step); (4) wait until the editor has pushed the pre-edit snapshot as an undo step (at most 3 s, normally the
  next frame); (5) reply. Structural edits (`duplicate_object`, `delete_object`, `reparent_object`) push their own
  undo event exactly like the editor's Duplicate / Delete / hierarchy drag and drop.
- **Mouse button held**: if the left mouse button is held in the game window during step 2, the call fails with
  `INVALID_STATE` after 5 s and only the selection changed (never a silent non-undoable edit). If it is held during
  step 4, the edit is done and undoable, but the editor records the step only when the button is released or the
  selection changes: the reply then has `undoPending: true`.
- **Selection side effect**: the edited object stays selected, as after editing it by hand (the previous selection
  is replaced). `create_object` and `duplicate_object` select the new object(s); `delete_object` leaves nothing
  selected (the editor's Delete deselects); `undo` restores the selection recorded with the undone step. A selection
  change itself makes the editor push an undo step that only restores the previous selection, so one edit may add
  two entries to `undoStackLength`; `undo` with `steps: 1` still reverts the edit itself.
- **Common reply fields** of the editing write tools (except `undo`):

  | Field | |
  |---|---|
  | `changed` | the call changed the object (false: e.g. the same value set again; then nothing is recorded) |
  | `undoable` | the pre-edit state is (or, with `undoPending`, will be) on the editor's undo stack; always false for `create_object` |
  | `undoPending` | only present when true, see above |
  | `note` | only present when there is something to explain |
  | `undoStackLength` | entries on the current context's undo stack after the call (scene, or the open prefab) |

- Every successful edit marks the mod as having unsaved edits (see [Unsaved edits](#unsaved-edits)). Nothing is
  saved automatically; call `save_mod`.
- **No redo**: the editor has no redo stack or API (only Ctrl+Z and the Edit menu call `EditorUndo.Undo`), so the
  bridge has no redo either.
- Not undoable in the editor itself (so not via the bridge): object creation (Ctrl+Z after creating only re-selects
  the previously selected object), and the destruction of generated children when a Prefab Instance / Board Space /
  Item / Text component is removed.

### Unsaved edits

`get_status.hasUnsavedEdits` (and the create/open rule) reports whether the open mod changed since it was loaded or
last saved. The bridge reads every undo stack of the open mod (the scene's and each prefab's) once per frame and
latches "unsaved" when a stack changes, **except** for the editor's initial snapshot after a load (an empty stack
getting its first entry) and an undo that pops that single entry again; every bridge edit latches it as well. The
clean point moves at load completion and on every completed `EditorSystem.SaveCurrentMod` (Ctrl+S, File > Save, the
unsaved-changes popup's Save button, `save_mod`), which a Harmony **postfix** observes; the postfix only records that
a save happened. Consequences:

- An untouched, freshly opened or created mod reports `false`.
- Selecting objects in the editor counts as a change, because the editor records selection changes as undo steps.
- Undoing everything does not make the mod clean again (latched until the next save).
- Entering a playtest writes the mod files but does not count as a save (the editor does not clear its flag either).
- Fallback to the editor's flag (`unsavedEditsSource: "editorFlag"`) when the plugin first saw the mod with more
  than one undo entry on a stack (it was open before the plugin could watch it) until the next save, or when the save
  postfix could not be installed (a warning is logged at startup).
- An edit followed by an undo within the same frame is not seen (practically impossible by hand).

### `create_object` (write)

| Param | Type | Default | |
|---|---|---|---|
| `kind` | string | `"empty"`; `"prop"` if `prop` is given; `"prefabInstance"` if `prefab` is given | creation entry, see the table (case-insensitive) |
| `name` | string, 1-256 chars, no control characters | the editor's default name | |
| `parentGuid` | string (GUID) or null | null | parent object; null/omitted = top level of the scene, or the open prefab's root object |
| `position` | Vector3 | (0,0,0) local | local position via `ModTransform.position` (clamped, see `set_transform`) |
| `rotation` | Vector3 | (0,0,0) | local Euler angles via `ModTransform.rotation` |
| `scale` | Vector3 | (1,1,1) | local scale via `ModTransform.scale` |
| `prop` | string (GUID) | - | only with kind `prop`: a Prop asset (`list_assets` type `Prop`, built-in or mod) |
| `prefab` | string (GUID) | - | only with kind `prefabInstance`: a Prefab asset of the mod (`list_assets` type `Prefab`) |

Vector3 values are `{x, y, z}` (or `[x, y, z]`), finite numbers.

Creates the object exactly like the editor's GameObject menu / hierarchy context menu
(`EditorSystem.AddGameObject(type, parent)`, which names it, adds the components and selects it). With `prop` or
`prefab` it does what dropping that asset onto a hierarchy item does (`ModAsset.OnSceneDragDropStart(parent)` and
the drop end: a Prop object named after the asset with the prop assigned, or a Prefab Instance referencing the
prefab). `name`, `position`, `rotation`, `scale` are applied in the same frame. Guids are assigned by the editor
(each new component gets a fresh guid in its constructor).

| `kind` | Editor menu entry | Components (besides Transform) | Offered |
|---|---|---|---|
| `empty` | Create Empty | - (named `GameObject`, `GameObject (n)`) | always |
| `prefabInstance` | Prefab Instance | Prefab Instance | not while a prefab is edited |
| `cube`, `sphere`, `plane` | 3D Object > Cube / Sphere / Plane | Prop (built-in cube/sphere/plane) | always |
| `prop` | 3D Object > Prop | Prop (empty, or the `prop` asset) | always |
| `particleSystem` | 3D Object > Particle System | Particle System | always |
| `text` | 3D Object > Text | Text | always |
| `lineRenderer` | 3D Object > Line Renderer | Line Renderer | always |
| `playerSpawn` | Minigame Object > Player Spawn | Player Spawn | Minigame mods |
| `item` | Minigame Object > Item | Item | Minigame mods |
| `trigger` | Minigame Object > Trigger | Trigger | Minigame mods |
| `logic` | Minigame Object > Logic | Logic | Minigame mods |
| `weaponSource` | Minigame Object > Weapon Source | Weapon | Minigame mods |
| `boardSpace` | Board Object > Board space | Board Space | Board mods, not while a prefab is edited |
| `directionalLight`, `pointLight`, `spotLight` | Light > Directional / Point / Spot Light | Light (type set) | always |
| `audioSource` | Audio > Audio Source | Audio Source | always |

Also refused with `INVALID_PARAMS`, as the editor's menus are disabled there: a parent that does not support children
(prefab instance, board space, item, text), and Item/BoardSpace mods without an open prefab (they have no scene
hierarchy). The `ObjectType` members `NewPrefab`, `NewPlayer`, `Camera` have no menu entry and no implementation in
`AddGameObject`; they are not exposed.

Errors: guard codes, `INVALID_PARAMS`, `NOT_FOUND` (parent), `INVALID_STATE`, `BUSY_LOADING`, `TIMEOUT`, `INTERNAL`.

Result: the new object as a `get_hierarchy` node plus `parentGuid` (null at top level), `siblingIndex`, `transform`
(as in `get_object`), `kind`, `selected`, `undoable` (always **false**: creation is not undoable in the editor; remove
the object with `delete_object`, which is undoable), `undoStackLength`, and a `note` if the editor had not yet taken
a snapshot of the new object when the call returned (3 s bound).

```json
{"guid": "9b1e...", "name": "Crate", "active": true, "components": ["ModProp"], "childCount": 0, "children": [],
 "parentGuid": null, "siblingIndex": 3,
 "transform": {"localPosition": {"x": 0, "y": 1, "z": 0}, "localEulerAngles": {"x": 0, "y": 0, "z": 0},
               "localScale": {"x": 1, "y": 1, "z": 1}},
 "kind": "prop", "selected": true, "undoable": false, "undoStackLength": 4}
```

### `duplicate_object` (write)

Params: `guid` (string GUID, required). The editor's Duplicate (Ctrl+D) = `Copy()` + `Paste()` on the selection, so
the bridge selects the object first; the editor's copy clipboard (Ctrl+C/Ctrl+V) is replaced, as with Ctrl+D. The
clone (with all its children) is placed next to the original (same parent; for the open prefab's root object: below
it), keeps the name, is selected, and one `DuplicateGameObject` undo event is pushed (undo destroys the clone).
Every component of the clone and of its children gets a fresh guid (the `ModBehaviour` constructor assigns one and
the field is not serialized, so `Instantiate` does not copy it). The bridge checks this after every duplicate and,
should a guid ever collide, undoes the duplicate and returns `INTERNAL`.

```json
{"sourceGuid": "0b6d...", "objects": [{"guid": "77aa...", "name": "Floor", "active": true, "components": ["ModProp"],
  "childCount": 0, "children": [], "parentGuid": null, "siblingIndex": 5, "transform": {"...": "..."}}],
 "changed": true, "undoable": true, "undoStackLength": 6}
```

`objects`: the clone(s) in the `create_object` result shape (node with subtree, `parentGuid`, `siblingIndex`,
`transform`). Errors: guard codes, `NOT_FOUND`, `INVALID_STATE`, `TIMEOUT`, `INTERNAL`.

### `delete_object` (write)

Params: `guid` (string GUID, required). The editor's Delete on the selected object: a soft delete (the object and its
children move under the editor's hidden, inactive `UndoDeletedObjects` root and their asset entries are disabled;
nothing is destroyed) with one `DeleteGameObject` undo event (undo puts the object back at the same parent and
index). The open prefab's root object cannot be deleted (`INVALID_PARAMS`). Afterwards nothing is selected.

```json
{"guid": "0b6d...", "name": "Arena", "deleted": 4, "changed": true, "undoable": true, "undoStackLength": 7}
```

`deleted`: the object plus its editor-visible descendants. Errors: guard codes, `NOT_FOUND`, `INVALID_PARAMS`,
`INVALID_STATE`, `TIMEOUT`, `INTERNAL`.

### `reparent_object` (write)

| Param | Type | Default | |
|---|---|---|---|
| `guid` | string (GUID), required | | object to move |
| `newParentGuid` | string (GUID) or null; the key is **required** | | new parent; null = top level of the scene, or directly below the open prefab's root object |
| `siblingIndex` | int >= 0 | - | final index among the new parent's children; omitted = append as last child (like dropping onto the parent) |

Replicates the hierarchy widget's drag and drop: push a `MoveGameObject` undo event, `ModTransform.SetParent` (world
position, rotation and scale are kept and the local values recomputed, as in the editor), for `siblingIndex` then
`SetAsLastSibling` + `SetSiblingIndex`, then re-baseline the undo snapshot. Refused with `INVALID_PARAMS`: moving an
object into itself or its own descendant (cycle); a parent that does not support children (prefab instance, board
space, item, text: the same rule `get_hierarchy` uses); a parent in another context (scene vs prefab, or another
prefab); moving the open prefab's root object; a `siblingIndex` beyond the last index after the move (the message
gives the range). Same parent and same or omitted index: `changed: false`, nothing is pushed.

```json
{"guid": "9c02...", "parentGuid": "77e1...", "siblingIndex": 0,
 "transform": {"localPosition": {"x": -2, "y": 0, "z": 0}, "...": "..."},
 "changed": true, "undoable": true, "undoStackLength": 8}
```

Errors: guard codes, `INVALID_PARAMS`, `NOT_FOUND`, `INVALID_STATE`, `TIMEOUT`.

### `set_object` (write)

| Param | Type | |
|---|---|---|
| `guid` | string (GUID), required | |
| `name` | string, 1-256 chars, no control characters | the inspector's name field (`GameObject.name`) |
| `active` | bool | the inspector's active checkbox (`GameObject.SetActive`) |

At least one of `name`/`active`; both in one call = one undo step. Names need not be unique (address objects by guid).

```json
{"guid": "0b6d...", "name": "Floor", "active": false, "activeInHierarchy": false,
 "changed": true, "undoable": true, "undoStackLength": 9}
```

Errors: guard codes, `INVALID_PARAMS`, `NOT_FOUND`, `INVALID_STATE`, `TIMEOUT`.

### `set_transform` (write)

| Param | Type | |
|---|---|---|
| `guid` | string (GUID), required | |
| `position` | Vector3 | local position (`ModTransform.position`) |
| `rotation` | Vector3 | local Euler angles in degrees (`ModTransform.rotation`) |
| `scale` | Vector3 | local scale (`ModTransform.scale`) |

At least one of them; one undo step. The values go through `ModTransform`'s own property setters (the inspector's
path), which clamp:

| Property | Limit (constants in `ModTransform`) |
|---|---|
| `position` | the resulting **world** position is clamped to -1000..1000 per axis (`maxPosition`); the local value follows |
| `rotation` | each axis clamped to -3600..3600 (`maxRotation`); stored as the editor's rotation hint and written to `localEulerAngles` |
| `scale` | each axis clamped to 0.001..1000 (`minScale`, `maxScale`) |

```json
{"guid": "0b6d...", "applied": {"position": {"x": 0, "y": 1000, "z": 0}, "rotation": {"x": 0, "y": 45, "z": 0},
                                "scale": {"x": 2, "y": 2, "z": 2}},
 "clamped": ["position"],
 "transform": {"localPosition": {"x": 0, "y": 1000, "z": 0}, "localEulerAngles": {"x": 0, "y": 45, "z": 0},
               "localScale": {"x": 2, "y": 2, "z": 2}},
 "changed": true, "undoable": true, "undoStackLength": 10}
```

`applied`: the three `ModTransform` property values after the call (as `get_object` shows them on the `ModTransform`
component); `clamped`: given properties whose applied value differs from the request (by more than 1e-4, relative);
`transform`: Unity's local values (Euler angles may be normalised differently from `rotation`).
Errors: guard codes, `INVALID_PARAMS`, `NOT_FOUND`, `INVALID_STATE`, `TIMEOUT`, `INTERNAL`.

### `add_component` (write)

| Param | Type | |
|---|---|---|
| `guid` | string (GUID), required | object |
| `type` | string, required | class name (`ModLight`) or UI name (`Light`), case-insensitive |

Calls `ModEditorComponentAdderRemover.AddComponentToGameObject(object, type)`, which is what the Add Component popup
calls (the component's `Awake` creates its Unity components). Before that, every rule the editor's UI applies is
checked; violations give `INVALID_PARAMS` with the editor's message:

- the type is addable and allowed for the mod type (`EditorSystem.CanUseItem`); types the picker never offers
  (Transform, Camera, Audio Listener, Mesh Filter, Script, Animator, ..., and RigidBody / Hittable, which have no mod
  type) are refused;
- not on a prefab instance (the inspector has no Add Component button there);
- the editor's `PerformChecksForComponentAddability` rules: one per object for `[DisallowMultipleComponent]` types;
  Prefab Instance, Board Space, Item and Text need an object without children and exclude each other; Prefab Instance
  needs an otherwise bare object; renderer types that share a single-instance base class exclude each other (Prop,
  Mesh Renderer, Particle System, Text, Water); Mover and Simple Animator exclude each other; Board Space Event only
  inside a prefab; Prefab Instance and Board Space never inside a prefab. The editor's registry has no "required
  component" relations.

Both the bridge's replica of these rules and the editor's own check code are evaluated; either one blocking refuses.

```json
{"guid": "0b6d...", "component": {"type": "ModLight", "uiName": "Light", "guid": "c41f...", "enabled": true,
  "properties": {"type": {"type": "InternalLight", "value": "Point"}, "...": "..."}},
 "changed": true, "undoable": true, "undoStackLength": 11}
```

`component`: the new component as `get_object` shows it. Errors: guard codes, `INVALID_PARAMS`, `NOT_FOUND`,
`INVALID_STATE`, `TIMEOUT`, `INTERNAL`.

### `remove_component` (write)

| Param | Type | |
|---|---|---|
| `guid` | string (GUID), required | object |
| `component` | string, required | component guid, class name or UI name |

The inspector's remove (X) button: `ModEditorComponentAdderRemover.RemoveComponentFromGameObject`. Refused
(`INVALID_PARAMS`) where the inspector has no remove button: non-addable types (Transform and the other types the
picker never offers), and on a prefab instance everything except the Prefab Instance component (the inspector shows
only Transform and Prefab Instance there). For types that do not support children (Prefab Instance, Board Space,
Item, Text) the editor first destroys the object's (generated) children; undo restores the component but not those
children, except where the component regenerates them itself (a Prefab Instance re-instantiates its prefab).

```json
{"guid": "0b6d...", "removed": {"type": "ModLight", "uiName": "Light", "guid": "c41f..."}, "childrenDestroyed": 0,
 "changed": true, "undoable": true, "undoStackLength": 12}
```

`childrenNote` is added when `childrenDestroyed > 0`. Errors: guard codes, `INVALID_PARAMS`, `NOT_FOUND`,
`INVALID_STATE`, `TIMEOUT`.

### `set_property` and `set_properties` (write)

| Tool | Params |
|---|---|
| `set_property` | `guid` (string GUID, required), `component` (string, required: guid, class or UI name), `property` (string, required), `value` (required key; JSON as below; `null` clears a reference) |
| `set_properties` | `guid`, `component` as above, `values` (object `{propertyName: value, ...}`, 1-64 entries) |

All properties of one call are set in the same frame = **one** undo step. Property names are the component's
reflected properties as `get_object` lists them (exact name; a unique case-insensitive match is accepted). Every value
is validated and converted **before** anything is selected or changed; if one fails, nothing changes. Each value is
written through the property's public setter, the same setter the inspector's `ReflectedProperty.SetValue` reaches,
so the setter's side effects run (a light type updates the Unity light, a prop rebuilds its mesh, a prefab instance
re-instantiates its prefab, ...). If a setter throws, the call returns `INTERNAL` with the exception message; the
message names the properties already set by this call (one `undo` reverts them).

Refused with `INVALID_PARAMS`:
- `guid` (assigned by the editor) and every `SerializableGuid` property (set the matching reference property, e.g.
  `ItemPrefab` instead of `ItemPrefabGUID`);
- properties marked `hiddenInInspector`;
- triggers and action lists (`ModLogicTrigger`, `ModLogicTrigger[]`, `ModAction`): use the action tools of the next
  increment;
- types the bridge cannot write yet: `ModComplexFloat`, `ModWeaponInfo`, `SimpleModHitAction`, `ModSimpleAnimation[]`,
  `BoardNodeConnection[]`, `ParticleSystem.Burst[]`, `byte[]` (e.g. `ModScriptReference.scriptValues`), and any type
  not in the table below;
- on a prefab instance: components other than Transform and Prefab Instance (edit the prefab instead).

Value JSON (the shapes `get_object` returns, so values round-trip):

| Property type | JSON accepted |
|---|---|
| `Boolean` | `true` / `false` |
| integer types (`Int32`, `UInt32`, `Byte`, ...) | an integer within the type's range (an integral float such as `3.0` is accepted) |
| `Single`, `Double` | a finite number (NaN/Infinity refused; `Single` must fit a 32-bit float) |
| `String` | a string (max 16384 chars) |
| enums | a name, case-insensitive (`"Point"`), or a defined integer value; flags enums: names joined by `,` (`"OnSelf, OnTargets"`) or an integer made of defined bits |
| `Vector2`, `Vector3`, `Vector4`, `Quaternion` | `{x, y[, z[, w]]}` with exactly these keys, or an array of 2/3/4 numbers |
| `Color` | `{r, g, b, a?}` floats; `a` defaults to 1 |
| `Color32` | `{r, g, b, a?}` integers 0-255; `a` defaults to 255 |
| `Rect` | `{x, y, width, height}` |
| `LayerMask` | an integer bit mask |
| asset references (`Prop`, `Material`, `AudioClip`, `Texture2D`, `Mesh`, `Shader`, `PummelPrefab`, `PummelVisualScript`, ...) | `null`, a guid string, or `{"guid": "..."}`; other keys (`name`, `assetType`, ...) are ignored, so a reference from `get_object` can be sent back; one with `"registered": false` is refused |
| scene-object references (`Transform`, `GameObject`, component types) | as for assets: the referenced component's guid (the `guid` of a reference in `get_object`), or an **object** guid (`ModTransform` guid): then the first component of that object whose class or wrapped Unity component matches the property type is used, like dropping the object from the hierarchy onto the field |
| arrays `T[]` of the above | a JSON array (max 1024 elements); a `{"truncatedItems": n}` marker from `get_object` is refused |
| `AnimationCurve` | `{keys: [{time, value, inTangent?, outTangent?}], preWrapMode?, postWrapMode?}`; wrap modes `Default`, `Once`, `Clamp`, `Loop`, `PingPong`, `ClampForever` (default `ClampForever`); max 256 keys |
| `ParticleSystem.MinMaxCurve` | a number (= Constant), or `{mode: "Constant", constant}`, `{mode: "TwoConstants", constantMin, constantMax}`, `{mode: "Curve", curveMultiplier?, curve}`, `{mode: "TwoCurves", curveMultiplier?, curveMin, curveMax}` (curves as above, `curveMultiplier` defaults to 1) |
| `ParticleSystem.MinMaxGradient` | a color `{r, g, b, a?}` (= Color), or `{mode: "Color", color}`, `{mode: "TwoColors", colorMin, colorMax}`, `{mode: "Gradient"` or `"RandomColor", gradient}`, `{mode: "TwoGradients", gradientMin, gradientMax}`; a gradient is `{mode?: "Blend"/"Fixed", colorKeys: [{color, time}], alphaKeys: [{alpha, time}]}` with 1-8 keys each and times 0-1 |

Error messages name the property path and the expected type, e.g.
`'color': expected Color: {r, g, b, a?} (floats, a defaults to 1) (got string "red")` or
`'type': 'Area' is not a InternalLight value; expected one of Directional, Point, Spot (case-insensitive) or its integer value`.

Reference rules (as the editor's object picker): the guid must resolve to a mod asset (as listed by `list_assets`,
including built-in ones) or to an object of the mod; the resolved value must be an instance of the property's
(element) type (`'materials[0]': 'Cube' is a Prop (Prop), expected Material`); scene objects may only reference scene
objects and prefab objects only objects of the same prefab; deleted (missing) assets are refused.

Inspector-only clamps: some custom inspectors re-apply limits on every frame they draw a component; the bridge applies
the same limits when writing and lists the affected properties in `inspectorClamped`:
`ModAudioSource.volume` 0-1, `.spatialBlend` 0-1, `.stereoPanning` -1..1; `ModBoardSpace.SpawnRotation` 0-360,
`.SpawnDistance` 0-4; `ModFireWeapon.FireTime` >= 0.1; `ModItem.SecondsBetweenHeldAction` >= 0.1, `.Cooldown` >= 0.1;
`ModLight.spotAngle` 0-180; `ModParticleSystem.arcSpread` 0-1; `ModSpawner.SpawnCount` 0-20;
`ModText.OutlineThickness` 0-1, `.ShadowOffsetX` / `.ShadowOffsetY` -1..1, `.ShadowSoftness` 0-1;
`ModTrigger.StayTriggerInterval` >= 0.1; `ModWater.CellSize` 0.1-10, `.WaveHeight` 0-1, `.WaveSpeed` 0-10.
Setters may clamp further; `applied` shows the value actually stored.

Properties whose getters have side effects (read side, see `get_object`) are written through their setters after the
object has been selected and snapshotted (the editor's snapshot runs every getter of the selected object anyway):
`ModItem.ItemPrefab` / `DroppedItemPrefab` (the setter stores the asset and its guid), `ModPrefabInstance.PrefabReference`
(re-instantiates the prefab copy, stores the guid), `ModSpawner.Prefabs` (rebuilds the guid list),
`ModMeshRenderer.materials`, `ModLineRenderer.Materials`, `ModSkinnedMeshRenderer.materials` (write the renderer's
shared materials), `ModProp.customMaterials`, and the `ModParticleSystem` renderer properties (`material`,
`renderMode`, `Prop`, ...; `INVALID_STATE` if the particle renderer is still not initialised when the edit is
applied). Their `...GUID` siblings and `ModScriptReference.scriptValues` are refused (see above).

```json
{"method": "set_properties", "params": {"guid": "0b6d...", "component": "Light",
  "values": {"type": "Spot", "spotAngle": 200, "color": {"r": 1, "g": 0.5, "b": 0}}}}
```
```json
{"guid": "0b6d...", "component": {"type": "ModLight", "guid": "c41f..."},
 "applied": {"type": {"type": "InternalLight", "value": "Spot"}, "spotAngle": {"type": "Single", "value": 180.0},
             "color": {"type": "Color", "value": {"r": 1.0, "g": 0.5, "b": 0.0, "a": 1.0}}},
 "inspectorClamped": ["spotAngle"], "changed": true, "undoable": true, "undoStackLength": 13}
```

`applied`: each written property read back after the editor recorded the step, in `get_object`'s `{type, value}`
shape. Errors: guard codes, `INVALID_PARAMS`, `NOT_FOUND`, `INVALID_STATE`, `TIMEOUT`, `INTERNAL`.

### `undo` (write)

| Param | Type | Default | |
|---|---|---|---|
| `steps` | int 1-20 | 1 | number of Ctrl+Z presses |

Exactly what Ctrl+Z does in `EditorSystem.Update`: `GetUndoManager().Undo()` on the current context's undo stack (the
scene's, or the open prefab's), one step per frame like repeated key presses; stops early when the stack is empty.
Then it waits one more frame and reports the stack length. There is **no redo** (the editor has none).

```json
{"undone": 1, "events": ["GameObjectUndoEvent"], "undoStackLength": 12}
```

- `events`: the type of each undone step: `GameObjectUndoEvent` (property, name, active, component and transform
  edits, and selection changes), `MoveGameObject` (reparent), `DeleteGameObject` (restores the object),
  `DuplicateGameObject` (destroys the clone).
- The bottom entry of a stack is the editor's initial snapshot; undoing it changes nothing but the selection.
- `extraUndoEntries` + `note`: the editor pushed a new snapshot right after the undo. This happens after undoing a
  component add: `EditorUndo.Undo` re-baselines while the component it removes still exists (Unity destroys it at the
  end of the frame), so the next snapshot differs. Editor behaviour, also with Ctrl+Z: one more undo re-adds the
  component.
- `undone: 0` + `note: "nothing to undo"` when the stack was empty.

Errors: guard codes, `INVALID_STATE`, `TIMEOUT`.

---

## Safety

### Config switches (`BepInEx\config\com.pummelbridge.plugin.cfg`)

| Key | Default | Effect |
|---|---|---|
| `[General] PluginEnabled` | `true` | Read at game start. `false`: the plugin does nothing at all (no server, no patches, one log line). Use this to publish by hand. |
| `[Safety] EnableWrites` | `true` | Read on every write call. `false`: every write tool returns `WRITES_DISABLED`. Does not affect the publish block. |
| `[Server] Enabled` | `true` | `false`: no RPC server; the publish block stays active. |
| `[Server] Port` | `38917` | |

### Test-mod rule

A name is a test-mod name iff it starts with the exact, case-sensitive prefix `PB_TEST_`, has at least one more
character, is at most 64 characters long, consists only of ASCII `A-Z a-z 0-9 _ -` and spaces, and has no leading
or trailing space. `pb_test_x`, `PB_TEST_`, `PB_TEST_a.b`, `PB_TEST_../x` and non-ASCII look-alikes are not. The
prefix is hardcoded; there is no configuration, allowlist or bypass.

### Guard (central, in the tool registry)

A write tool cannot be registered without a guard kind (`ToolDefinition.Write` requires one, a registry without
`WriteSafety` refuses write tools). Checks, in order, on the main thread in the same unit of work as the start of the
body, when the call's turn in the write queue comes (see [Threading](#threading)):

1. `[Safety] EnableWrites` → `WRITES_DISABLED`
2. publish block complete → `PUBLISH_BLOCK_INCOMPLETE`
3. editor open → `EDITOR_NOT_OPEN`
4. not loading → `BUSY_LOADING`
5. kind **OpenMod** (`save_mod`, `create_object`, `duplicate_object`, `delete_object`, `reparent_object`,
   `set_object`, `set_transform`, `add_component`, `remove_component`, `set_property`, `set_properties`, `undo`):
   a mod is open (`NO_MOD_OPEN`) and loaded (`BUSY_LOADING`); its Meta name **and**
   folder name pass the rule (`GUARD_NOT_TEST_MOD`, message names the mod); state is `Editing` (`INVALID_STATE`).
   Kind **Lifecycle** (`create_test_mod`, `open_test_mod`): the `name` param passes the rule; if any mod is open it
   is a test mod, else `GUARD_NOT_TEST_MOD` ("close <name> by hand first"); state is `Editing`.

Workshop id hygiene: whenever the guard passes with a loaded test mod open, before every bridge save and after
create/open, an in-memory `workshopItem.publishedFileId` other than 256 (`ModWorkshopItem.DefaultWorkshopID`) is
reset to 256 with a warning in the BepInEx and Unity logs. This is the only workshop-item field the bridge writes.

### Workshop publish block

While the plugin is enabled, Harmony prefixes installed in `Awake` (before the server starts; never removed) skip
every Workshop create/update/delete path and return a safe default: the type's `Invalid` handle where one exists
(`UGCUpdateHandle_t.Invalid` = `ulong.MaxValue`, `SteamAPICall_t.Invalid` = 0), else `false` / 0 / null.

- Game layer (all required): `WorkshopModInstaller.PublishItem`, `.SubmitItemUpdate(UGCQueryHandle_t)`,
  `.BeginUGCItemContentUpdate`, `.OnCreateItem`, `.OnSteamUpdateUGCQuery`, `.SetUGCPreviewFiles`;
  `WorkshopPopup.PublishMod`, `.ResetPublishFileIdAndRetryPublish`, `.DeleteBlankWorkshopItemOnFailedPublish`;
  `WorkshopBrowserItem.Delete`.
- `Steamworks.SteamUGC`: `CreateItem`, `StartItemUpdate`, `SubmitItemUpdate`, `DeleteItem` (required); every
  public static method whose first parameter is `UGCUpdateHandle_t` except the getter `GetItemUpdateProgress`
  (enumerated at startup); `AddDependency`, `RemoveDependency`, `AddAppDependency`, `RemoveAppDependency`.
- `Steamworks.SteamRemoteStorage`: every method whose name contains `FileWrite` or starts with
  `UpdatePublishedFile`, plus `FileDelete`, `FileForget`, `FileShare`, `SetSyncPlatforms`, `PublishWorkshopFile`,
  `PublishVideo`, `CreatePublishedFileUpdateRequest`, `CommitPublishedFileUpdate`, `DeletePublishedFile`.
- Not blocked (normal play keeps working): queries, `Get*`, `Release*`, `DownloadItem`, `SubscribeItem`,
  `UnsubscribeItem`, `SetUserItemVote`, favourites, playtime tracking, `UGCDownload`, `UGCRead`, `FileRead`,
  `FileExists`.
- Fail closed: if a required target is missing or any patch fails, an Error is logged, `publishBlock.complete` is
  false and every write tool returns `PUBLISH_BLOCK_INCOMPLETE`. Read tools keep working.
- Each blocked call logs a Warning (first call at once, then at most once per method per 10 s with the number of
  calls not logged) to the BepInEx log and to the Unity log, so the editor's Log window shows it:
  `[PummelBridge] Workshop publishing is blocked while the PummelBridge plugin is enabled. Blocked call: WorkshopPopup.PublishMod(PummelMod). Nothing was sent to Steam. To publish, set [General] PluginEnabled = false in BepInEx\config\com.pummelbridge.plugin.cfg and restart the game.`

**What the author sees when clicking Publish with the plugin loaded:** File > Publish Mod to Workshop still opens
the dialog (its read-only Workshop details query is allowed). Clicking "Publish mod to workshop" / "Publish update
to workshop" does nothing visible in the dialog: `WorkshopPopup.PublishMod` is skipped as a whole, so the mod is
**not saved**, no reinstall-on-exit is queued, nothing is sent to Steam, no progress bar appears and the button
stays enabled. The Log window shows the line above (per click, at most once per 10 s). The dialog closes normally
and the editor keeps working. Note: opening the dialog on a published mod copies the remote title, description and
visibility into the in-memory mod; that is game behaviour, unchanged by the bridge.

**How to publish by hand:** quit the game, set `[General] PluginEnabled = false`, start the game, publish from the
editor as usual, quit, set it back to `true`.

---

## References

Asset/object references (property values, action targets, settings) use one shape:

| Field | |
|---|---|
| `guid` | guid of the mod asset entry (for scene objects: the referenced component's `ModBehaviour` guid, for a `Transform` the `ModTransform` guid); null if unregistered |
| `name` | asset name, or the Unity object name |
| `assetType` | Unity type of the referenced object (`Transform`, `Material`, `AudioClip`, ...) or asset class short name |
| `location` | `internal` (built-in), `external` (mod file), `runtime` (scene object) |
| `objectGuid`, `objectName` | for scene objects: `ModTransform` guid and name of the GameObject (use with `get_object`) |
| `registered: false` | object not found in the asset registry (the scene file would store it as null) |
| `missing: true` | only a guid is stored and no asset with that guid exists |

## Not in this build

No action editing (triggers/actions), playtest or batch tools (later Phase 2 increments); no redo (the editor has
none); no prefab asset editing context switch (open/close a prefab by hand), no layer/tag or per-component enable
editing, no asset creation, import or removal. No delete, rename or close of mods. No Workshop functionality of any
kind; publishing is blocked (see [Safety](#safety)).
