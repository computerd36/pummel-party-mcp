# PummelBridge HTTP API (api v1, Phase 1: read-only)

The BepInEx plugin `PummelBridge.Plugin` runs inside Pummel Party and exposes the mod editor's state on a
local HTTP endpoint. This build is **read-only**: it registers no write tools, installs no Harmony patches and
calls no game method that changes the mod, the scene, the selection, the undo stack or the disk.

## Transport

| Item | Value |
|---|---|
| Endpoint | `POST http://127.0.0.1:<port>/rpc` (only this path and verb exist) |
| Bind address | `127.0.0.1` only (TCP listener on the loopback interface) |
| Port | BepInEx config `BepInEx\config\com.pummelbridge.plugin.cfg`, section `[Server]`, `Port` (default `38917`, range 1024-65535) |
| Enable/disable | same section, `Enabled` (default `true`) |
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

Values in results: enums as their names (flags as `"A, B"`), `Vector2/3/4` and `Quaternion` as `{x,y,z[,w]}`,
`Color` as `{r,g,b,a}` (0-1 floats), `Color32` as 0-255 ints, GUIDs as lowercase `8-4-4-4-12` strings, NaN/Infinity
as strings. Asset or object references are objects (see "References").

### Threading

`ping` and `get_log` (without `includeEditor`) are answered on the HTTP thread and work even while the game's
main thread is busy. Everything else is queued to the plugin's `Update()` (max 16 items / 8 ms per frame) and
waits up to 10 s.

---

## Methods

### `ping`

Works with the editor closed. No params.

```json
{"pong": true, "pluginVersion": "0.1.0", "gameVersion": "1.15.2", "unityVersion": "2021.3.45f2",
 "apiVersion": 1, "sessionId": "3b0f2c...e1", "readOnlyBuild": true}
```

`sessionId` is random per game start; a change means the game restarted (log ids restart at 1).

### `get_status`

No params. Never fails because of editor state.

```json
{"editorOpen": true, "editorState": "Editing", "isLoading": false, "modOpen": true,
 "modName": "PB_TEST_Arena", "modType": "Minigame", "modLoaded": true, "isDirty": true,
 "undoStackLength": 1, "prefabContext": null, "playMode": false,
 "activeScene": "ModEditorScene", "readOnlyBuild": true}
```

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
| `type` | string | - | case-insensitive match on `type`, `className` or the internal asset type string |
| `includeBuiltIn` | bool | false | also list the game's built-in (internal) assets |

Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`.

Result: an **array**, mod (external) assets first, then built-in, each sorted by type and name.

```json
[
  {"guid": "4a5b...", "name": "Coin", "type": "Prefab", "className": "PummelPrefab", "location": "external",
   "relativePath": "Assets/Prefabs/Coin.pfab"},
  {"guid": "7c8d...", "name": "grass", "type": "Texture", "className": "PummelTexture", "location": "external",
   "relativePath": "Assets/Textures/grass.png"},
  {"guid": "1d2e...", "name": "Cube", "type": "Prop", "className": "ModAsset", "location": "internal"}
]
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

No write methods (create/delete/set/save/playtest/undo) and no Workshop functionality of any kind. The tool registry
refuses to register write tools in this build; Phase 2 adds them behind a guard layer (`PB_TEST_` allowlist).
