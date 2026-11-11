# PummelBridge HTTP API (api v2, Phase 2 increment 1)

The BepInEx plugin `PummelBridge.Plugin` runs inside Pummel Party and exposes the mod editor's state on a
local HTTP endpoint. api v2 adds the guard layer, the always-on Workshop publish block and four mod lifecycle
tools (`list_mods`, `create_test_mod`, `open_test_mod`, `save_mod`). Write tools only ever act on `PB_TEST_`
mods; see [Safety](#safety). Object/component/action editing comes in later increments.

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
| `INVALID_STATE` | editor state forbids the call: play mode, unsaved changes, a prefab being edited, an open editor dialog, a broken editor mod list (the message says what to do) |
| `ALREADY_EXISTS` | `create_test_mod`: a folder or a mod (Meta name) with that name exists, case-insensitive |

Values in results: enums as their names (flags as `"A, B"`), `Vector2/3/4` and `Quaternion` as `{x,y,z[,w]}`,
`Color` as `{r,g,b,a}` (0-1 floats), `Color32` as 0-255 ints, GUIDs as lowercase `8-4-4-4-12` strings, NaN/Infinity
as strings. Asset or object references are objects (see "References").

### Threading

`ping` and `get_log` (without `includeEditor`) are answered on the HTTP thread and work even while the game's
main thread is busy. Everything else is queued to the plugin's `Update()` (max 16 items / 8 ms per frame) and
waits up to 10 s (`create_test_mod` / `open_test_mod`: 35 s, with their own 30 s load limit inside).

Write tools: params are type-checked on the HTTP thread (`INVALID_PARAMS`), then the guard checks and the tool
body run on the main thread in **one** dispatched unit of work, so the editor state cannot change in between.

---

## Methods

### `ping`

Works with the editor closed. No params.

```json
{"pong": true, "pluginVersion": "0.2.0", "gameVersion": "1.15.2", "unityVersion": "2021.3.45f2",
 "apiVersion": 2, "sessionId": "3b0f2c...e1", "capabilities": ["read", "write"]}
```

`readOnlyBuild` was removed in api v2. `capabilities` lists what this build implements; whether writes are
currently allowed is in `get_status` (`writesEnabled`, `publishBlock.complete`).

`sessionId` is random per game start; a change means the game restarted (log ids restart at 1).

### `get_status`

No params. Never fails because of editor state.

```json
{"editorOpen": true, "editorState": "Editing", "isLoading": false, "modOpen": true,
 "modName": "PB_TEST_Arena", "modType": "Minigame", "modLoaded": true, "isDirty": true,
 "undoStackLength": 1, "prefabContext": null, "playMode": false,
 "activeScene": "ModEditorScene", "capabilities": ["read", "write"], "writesEnabled": true,
 "publishBlock": {"active": true, "complete": true, "patchedCount": 59, "missing": []}}
```

- `writesEnabled`: current value of `[Safety] EnableWrites`.
- `publishBlock`: `active` = at least one blocking patch installed; `complete` = every required target patched
  and no patch failed (write tools need this); `patchedCount` = methods patched (59 expected for game build
  2025-10-16: 10 game methods, 25 `SteamUGC`, 24 `SteamRemoteStorage`); `missing` = `"<target>: <reason>"` for
  each target that is missing (required) or whose patch failed. `readOnlyBuild` was removed in api v2.

- `editorState`: `Editing | EnteringPlayMode | PlayMode | ExitingPlayMode` (null when the editor is closed).
- `playMode`: `editorState != "Editing"` (a playtest is starting, running or ending).
- `isDirty`: dirty flag of the current undo context (scene, or the open prefab), read from fields. It is set only by
  undo-snapshot pushes; the editor probably pushes an initial snapshot right after a load, so `true` does not prove
  the user edited anything. `null` if the field layout changed or no mod is open.
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
- the editor's dirty flag is false, or `discardUnsaved: true`. This mirrors the editor's own "unsaved changes"
  popup, which uses `EditorSystem.IsModDirty` (current undo context). That flag is set by the first undo snapshot
  after any load, so it is **already true for an untouched, freshly opened or created mod** (confirmed in-game):
  expect to call `save_mod` first (harmless on a test mod) or pass `discardUnsaved: true`. The bridge never
  auto-saves.

### `save_mod` (write, open mod)

No params. The editor's own save (`EditorSystem.SaveCurrentMod`: all mod files, `Data/Preview.jpg`, clears the
current dirty flag), after the guard (loaded test mod, not loading, edit mode) and the Workshop id hygiene. If the
mod's Meta name is missing from the editor's mod list, the entry is added first (otherwise the editor's
`UpdateLoadedModLastModifiedTime` would insert a broken nameless entry).
Saving while a prefab is being edited is allowed, as with Ctrl+S: the scene file stores each object's own active
flag, so the deactivated scene root does not change it, and the prefab is saved with the other assets. In that
case the scene's dirty flag stays set and `Preview.jpg` shows the prefab view.

```json
{"saved": true, "name": "PB_TEST_Arena", "isDirty": false}
```

`isDirty`: dirty flag of the current undo context right after the save (same source as `get_status.isDirty`).

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
`WriteSafety` refuses write tools). Checks, in order, on the main thread in the same unit of work as the body:

1. `[Safety] EnableWrites` → `WRITES_DISABLED`
2. publish block complete → `PUBLISH_BLOCK_INCOMPLETE`
3. editor open → `EDITOR_NOT_OPEN`
4. not loading → `BUSY_LOADING`
5. kind **OpenMod** (`save_mod`): a mod is open (`NO_MOD_OPEN`) and loaded (`BUSY_LOADING`); its Meta name **and**
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

No object/component/property/action editing, undo, playtest or batch tools (later Phase 2 increments). No delete,
rename or close of mods. No Workshop functionality of any kind; publishing is blocked (see [Safety](#safety)).
