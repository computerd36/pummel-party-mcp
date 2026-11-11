# PummelBridge HTTP API (api v2, Phase 2 increment 4, plugin 0.5.0)

The BepInEx plugin `PummelBridge.Plugin` runs inside Pummel Party and exposes the mod editor's state on a
local HTTP endpoint. api v2 adds the guard layer, the always-on Workshop publish block and four mod lifecycle
tools (`list_mods`, `create_test_mod`, `open_test_mod`, `save_mod`). Increment 2 (still api v2, all changes are
additive) adds object, transform, component and property editing plus `undo`; see [Editing](#editing). Increment 3
(still api v2, additive) adds editing of the Action Editor model (triggers and their action lists); see
[Action editing](#action-editing). Increment 4 (still api v2, additive) adds `validate_mod`, `start_playtest`,
`stop_playtest` and `run_batch`, structured error details (`error.data`) and play-mode rules for every tool; see
[Validation, playtest and batch](#validation-playtest-and-batch). Write tools only ever act on `PB_TEST_` mods; see
[Safety](#safety).

## Transport

| Item | Value |
|---|---|
| Endpoint | `POST http://127.0.0.1:<port>/rpc` (only this path and verb exist) |
| Bind address | `127.0.0.1` only (TCP listener on the loopback interface) |
| Port | BepInEx config `BepInEx\config\com.pummelbridge.plugin.cfg`, section `[Server]`, `Port` (default `38917`, range 1024-65535) |
| Enable/disable | `[General] PluginEnabled` (master switch) and `[Server] Enabled` (server only); see [Safety](#safety) |
| Token file | `BepInEx\config\PummelBridge.token`: 64 lowercase hex chars (32 CSPRNG bytes), created on first start, reused afterwards, regenerated if malformed; always written via a temp file plus atomic move/replace in the same folder (see [Safety](#token-file)) |
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
| complete request (headers and body) not received within 10 s of the connection being accepted (an absolute deadline, not a per-read timeout: dripping bytes does not extend it) | 408 | `REQUEST_TIMEOUT` |
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

An error may carry structured details in `error.data` (additive in increment 4; currently only `start_playtest`'s
validator refusal: `{"validation": {...}}`). Clients that ignore `data` still get the essentials in `message`.

| Code | Meaning |
|---|---|
| `UNKNOWN_METHOD` | no such method in this build |
| `INVALID_PARAMS` | a parameter has the wrong type/range, or a GUID is malformed |
| `EDITOR_NOT_OPEN` | `EditorSystem.Instance` is null (main menu, in a normal game, ...) |
| `NO_MOD_OPEN` | editor open, no mod loaded |
| `BUSY_LOADING` | the editor is loading a mod (`IsLoading`) or the mod has not finished loading (`!IsLoaded`) |
| `NOT_FOUND` | object / component not found |
| `TIMEOUT` | the call did not finish within its time limit (10 s; see [Threading](#threading) for the write-tool limits and the exact semantics). For write tools, a `TIMEOUT` whose message says "nothing was changed" / "safe to retry" was refused or cancelled **before** its commit point: nothing was applied and it never runs later. A write call that has passed its commit point never gets that `TIMEOUT` (see [Timeouts and the commit point](#timeouts-and-the-commit-point)) |
| `INTERNAL` | unexpected exception, or a game member the plugin needs is missing (`member X.Y not found — game version changed?`) |
| `WRITES_DISABLED` | write tool called while `[Safety] EnableWrites = false` |
| `PUBLISH_BLOCK_INCOMPLETE` | write tool called while the Workshop publish block is not complete (`get_status.publishBlock`) |
| `GUARD_NOT_TEST_MOD` | the open mod (open-mod tools) or the target / open mod (lifecycle tools) is not a `PB_TEST_` mod; the message names it |
| `INVALID_STATE` | editor state forbids the call: play mode (also the transitional states `EnteringPlayMode` / `ExitingPlayMode`), a mod that fails the validator (`start_playtest`), unsaved changes, a prefab being edited, an open editor dialog, a broken editor mod list, an object outside the current editing context, the left mouse button held in the game window, the open mod changed while an edit was pending (the message says what to do) |
| `ALREADY_EXISTS` | `create_test_mod`: a folder or a mod (Meta name) with that name exists, case-insensitive |

Edit tools additionally use `NOT_FOUND` (object / component / the edited object was deleted meanwhile),
`INVALID_PARAMS` (bad value, refused property, a rule the editor's UI enforces) and `TIMEOUT` (the editor did not
take its undo snapshot in time, or the call waited too long in the write queue; always before the edit was applied,
so safe to retry).

Values in results: enums as their names (flags as `"A, B"`), `Vector2/3/4` and `Quaternion` as `{x,y,z[,w]}`,
`Color` as `{r,g,b,a}` (0-1 floats), `Color32` as 0-255 ints, GUIDs as lowercase `8-4-4-4-12` strings, NaN/Infinity
as strings. Asset or object references are objects (see "References").

### Threading

`ping` and `get_log` (without `includeEditor`) are answered on the HTTP thread and work even while the game's
main thread is busy. Everything else is queued to the plugin's `Update()` (max 16 items / 8 ms per frame) and
waits up to 10 s (`create_test_mod` / `open_test_mod`: 35 s, with their own 30 s load limit inside; edit tools
of increments 2 and 3: 15 s; `start_playtest` 60 s, `stop_playtest` 30 s, `run_batch` 120 s). A committed write call
may take up to 4 s past its limit (commit grace, below), so **an MCP client must allow at least limit + 4 s + network
margin per call**: 64 s for `start_playtest`, 34 s for `stop_playtest`, 124 s for `run_batch` (the MCP server's
default 20 s request timeout is too short for these three).

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

#### Timeouts and the commit point

Every write tool has a **commit point**: the moment right before its first change to the editor or the mod (edit
tools: step 3 of the edit sequence in [Editing](#editing), when the edit is applied; `undo`: before the first undo
step; `save_mod`: before saving; `create_test_mod` / `open_test_mod`: before the mod list entry is added /
`OpenMod` is called; `start_playtest`: before the toolbar choices are applied and Play is pressed; `stop_playtest`:
before Stop is pressed; `run_batch`: the first commit of any of its steps). Reaching it and the caller's timeout are decided atomically: either the timeout wins and the
body changes nothing, or the body commits and the caller no longer times out.

- **Before the commit point** a call ends with `TIMEOUT` when its time limit runs out (while queued, while the edit
  waits for the editor's undo snapshot, ...), and it is also refused with `TIMEOUT` instead of starting when too
  little of its time limit is left after waiting in the write queue behind other writes: less than 1 s for any
  write tool; less than the post-edit wait (3 s) + 0.5 s for an edit tool, checked at the start and again right before
  the edit is applied; less than 3 s for `undo`, 10 s for `create_test_mod` / `open_test_mod`, 1 s for `save_mod`,
  20 s for `start_playtest`, 10 s for `stop_playtest`; a `run_batch` step is not started with less than 4 s of the
  batch's limit left.
  Such a `TIMEOUT` says "nothing was changed" / "safe to retry": nothing was applied (an edit tool may have selected
  the target object) and the call never runs later. **The caller may simply retry it.**
- **After the commit point** the call always returns its own outcome, normally the regular success result. Edit tools
  wait for the editor's undo push at most 3 s **or until the call's deadline, whichever comes first**, and then reply
  with `undoable` / `undoPending` / `note` saying whether the push was observed in time (see [Editing](#editing)).
  `undo` stops starting further steps when its deadline is near and replies with the steps done so far (`note`). The
  create/open load wait is shortened to end 1 s before the deadline; if the load is not done by then the reply is
  `TIMEOUT` with a message that says the mod **was created / is being opened** and may still finish (check
  `get_status`, do not blindly retry). Errors that occur after the commit point say what was already applied
  ("the edit was applied, but then: ...").
- A committed call normally completes before its deadline (that is what the start budgets are for). If the game's
  main thread stalls, the plugin keeps waiting up to 4 s past the deadline for the result (edit tools: 15 s + 4 s,
  below the MCP server's 20 s request timeout); only if even that runs out the reply is `TIMEOUT` with "the change was
  applied, but the call did not finish ...; check get_status / get_object before retrying".

---

## Methods

### `ping`

Works with the editor closed. No params.

```json
{"pong": true, "pluginVersion": "0.5.0", "gameVersion": "1.15.2", "unityVersion": "2021.3.45f2",
 "apiVersion": 2, "sessionId": "3b0f2c...e1", "capabilities": ["read", "write", "edit", "actions", "playtest", "batch"]}
```

`readOnlyBuild` was removed in api v2. `capabilities` lists what this build implements (`edit` = the object,
component, property and undo tools of increment 2; `actions` = the action/trigger tools of increment 3; `playtest` =
`validate_mod`, `start_playtest`, `stop_playtest`; `batch` = `run_batch`); whether
writes are currently allowed is in `get_status`
(`writesEnabled`, `publishBlock.complete`).

`sessionId` is random per game start; a change means the game restarted (log ids restart at 1).

### `get_status`

No params. Never fails because of editor state.

```json
{"editorOpen": true, "editorState": "Editing", "isLoading": false, "modOpen": true,
 "modName": "PB_TEST_Arena", "modType": "Minigame", "modLoaded": true, "isDirty": true,
 "hasUnsavedEdits": false, "unsavedEditsSource": "tracker",
 "undoStackLength": 1, "prefabContext": null, "playMode": false,
 "activeScene": "ModEditorScene",
 "playtest": {"state": "Editing", "running": false, "startedAt": null, "playModeSince": null, "endedAt": null,
              "playSeconds": null, "startedByBridge": false, "players": "4 Players", "playerCount": 4, "teams": null,
              "aiOnly": false},
 "capabilities": ["read", "write", "edit", "actions", "playtest", "batch"], "writesEnabled": true,
 "publishBlock": {"active": true, "complete": true, "patchedCount": 59, "missing": []}}
```

- `writesEnabled`: current value of `[Safety] EnableWrites`.
- `publishBlock`: `active` = at least one blocking patch installed; `complete` = every required target patched
  and no patch failed (write tools need this); `patchedCount` = methods patched (59 expected for game build
  2025-10-16: 10 game methods, 25 `SteamUGC`, 24 `SteamRemoteStorage`; the count at startup); `missing` =
  `"<target>: <reason>"` for each target that is missing (required) or whose patch failed. The status is **live**:
  every `get_status` and every write call re-checks (Harmony patch info) that each required target still carries the
  plugin's prefix; if one was removed after startup (e.g. another plugin unpatched it), `complete` becomes false,
  `missing` lists it as `"<target>: blocking prefix removed after startup"`, and it stays so until the game is
  restarted. `readOnlyBuild` was removed in api v2.

- `editorState`: `Editing | EnteringPlayMode | PlayMode | ExitingPlayMode` (null when the editor is closed).
- `playMode`: `editorState != "Editing"` (a playtest is starting, running or ending).
- `playtest` (increment 4; null while the editor is closed): `state` (= `editorState`), `running` (= `playMode`);
  `startedAt` / `playModeSince` / `endedAt` (UTC ISO times seen by the plugin's per-frame observer for the current or
  last playtest: left Editing / reached PlayMode / back in Editing; reset when the editor closes); `playSeconds`
  (seconds in PlayMode, frozen at the end); `startedByBridge` (that playtest was started by `start_playtest`);
  `players`, `playerCount`, `teams`, `aiOnly`: the toolbar's current dropdown / checkbox (see `start_playtest`).
  While a playtest runs (`running: true`) also: `paused` (`Time.timeScale == 0`, the toolbar's Pause button),
  `minigame` `{state, round, rounds}` (`GameManager.Minigame`: `MinigameControllerState` name, 1-based round as the
  round UI shows it, rounds) and `board` `{state}` (`GameManager.Board.BoardState`) when those controllers exist.
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

No params. Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`, `INVALID_STATE` while a playtest is starting,
running or ending (see [Play-mode behaviour](#play-mode-behaviour-of-every-tool)).

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

Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`, `NOT_FOUND`, `INVALID_PARAMS`. Works during a playtest: it then
lists the edit-time scene and adds `"playMode": true` and `playModeNote` to the returned root (see
[Play-mode behaviour](#play-mode-behaviour-of-every-tool)).

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

Params: `guid` (required, the object's `ModTransform` guid). Errors: as above + `NOT_FOUND`; `INVALID_STATE` while a
playtest is starting, running or ending.

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
| `guid` | string (GUID) | object guid; required unless `scope` is `"playerSettings"` |
| `component` | string | optional filter: component class name (`ModLogic`), UI name (`Logic`) or component guid |
| `scope` | `"object"` (default) or `"playerSettings"` | `"playerSettings"`: list the minigame's player-settings triggers instead (no `guid`/`component`) |

`trigger` and `path` are refused (this tool lists all triggers). Errors: as above + `NOT_FOUND` (object, or no
component matches the filter), `INVALID_PARAMS` (`scope: "playerSettings"` in a non-Minigame mod), `INVALID_STATE`
while a playtest is starting, running or ending.

Result: an **array** with one entry per trigger slot of the object's components (reflected properties of type
`ModLogicTrigger` / `ModLogicTrigger[]`: `ModLogic.Triggers[i]`, `ModTrigger.OnEnterActions`, `ModItem.OnUseTrigger`,
`ModBoardSpaceEvent.OnEnterTrigger`, ...), in component order and then property order. Empty array if the object has
no triggers. With `scope: "playerSettings"`: six entries, one per trigger the Minigame Settings inspector shows
(`weaponHitTrigger`, `tickTrigger`, `onDeathTrigger`, `onRespawnTrigger`, `onFirstSpawnTrigger`,
`onRoundEndTrigger`), each with `"scope": "playerSettings"` and `component`/`componentGuid` null.

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
   ],
   "callSite": {"label": "Logic: On Timer", "receiverName": "This Object", "sourceName": "Activator",
                "hideSource": true, "appendDefaultTargetFlags": null, "boardSpaceOnlyActions": false},
   "editable": true}
]
```

- `triggerName`: the address of the slot for the action tools (`trigger` param): the property name, plus `[i]` for
  array entries (`Triggers[i]`, the index in the Logic component's list).
- `type`: `ModActionType` name; `targetFlags`: `ModActionTargets` flags (`Source`, `Receiver`, `Advanced`, joined by
  `", "`; `"0"` when none is set) or null for actions without targets (`Wait`, `SetGravity`).
- `parameters`: the action's Unity-serialized fields (private `[SerializeField]` included) except `m_type`,
  `m_version`, `m_targetFlags`, `m_targets`. Keys are the field names = the JSON keys in the mod file.
- `triggerParameters`: the trigger's serialized fields except `m_type`, `m_actions` (e.g. `HitTrigger.m_hitType`).
- Nested triggers (Display Popup choices) appear inside `parameters` (`m_choices[i].m_onSelectTrigger`) with the
  trigger shape (`triggerType`, `triggerClass`, `triggerParameters`, `actions`); address them with `path` (see
  [Action editing](#action-editing)).
- `callSite` (additive in increment 3): what the inspector passes to the Action Editor for this slot. `label`: where
  the slot is shown; `receiverName` / `sourceName`: the labels of the Receiver/Source target toggles; `hideSource`:
  the Source toggle is hidden for every action of this list; `appendDefaultTargetFlags`: target flags the Action
  Editor gives a target action **appended** to this list (null = the action's own default); `boardSpaceOnlyActions`:
  the list accepts the board actions (Heal/Damage/Give .../Display Popup).
- `editable` (additive): the inspector currently shows this slot's "Edit" button. False with a `reason`, e.g. a
  Trigger component's `OnExitActions` while `TriggerOnExit` is false, `OnUseTrigger` of a Pickup item, or any slot of
  a component on a prefab instance. The action tools refuse non-editable slots.

### `list_assets`

| Param | Type | Default | |
|---|---|---|---|
| `type` | string | - | case-insensitive exact match on `type`, `className` or the internal asset type string |
| `includeBuiltIn` | bool | false | also list the game's built-in (internal) assets (over 16,000: use `search`/`type` and paging) |
| `search` | string | - | case-insensitive substring match on the asset name; whitespace-separated terms must all match |
| `limit` | int | 100 | page size, 1..500 |
| `offset` | int | 0 | number of matching assets to skip, >= 0 |

Errors: `INVALID_PARAMS` (bad `limit`/`offset`), `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`. During a playtest
the result additionally has `"playMode": true` (the editor mod's asset list, unchanged by the playtest).

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
delayed first save has written `Data/Meta.json` and `Data/WorkshopItem.json`; `TIMEOUT` after 30 s (less if the
call waited in the write queue; refused with `TIMEOUT` before creating anything if less than 10 s are left). A load
`TIMEOUT` comes after the commit point: the mod was created and may still finish loading; check `get_status` and
`list_mods` instead of retrying (a retry would get `ALREADY_EXISTS`). See
[Timeouts and the commit point](#timeouts-and-the-commit-point).

```json
{"name": "PB_TEST_Arena", "folderName": "PB_TEST_Arena", "type": "Minigame", "publishedFileId": "256"}
```

Errors: guard codes, `INVALID_PARAMS`, `ALREADY_EXISTS`, `INVALID_STATE` (also: "the open mod changed while waiting
for the load", when another mod was opened by hand meanwhile), `BUSY_LOADING` (another create/open is still waiting
for its load), `TIMEOUT`, `INTERNAL` (load failed; the editor shows its own error popup).

### `open_test_mod` (write, lifecycle)

| Param | Type | Default | |
|---|---|---|---|
| `name` | string, required | - | folder name, matched exactly (case-sensitive) among the folders in the mod folder |
| `discardUnsaved` | bool | false | allow dropping unsaved changes of the open test mod |

Opens an existing test mod with `EditorSystem.OpenMod` (what File > Open Mod ends in, minus its popup).
Refused if the name fails the rule even when such a folder exists; `NOT_FOUND` if there is no such folder or it has
no `Data/Meta.json`; `GUARD_NOT_TEST_MOD` if its Meta.json name is not a test-mod name. If that mod is already open
and loaded: success without reloading, `alreadyOpen: true`. Completes after the asynchronous load; `TIMEOUT`
after 30 s (same rules as `create_test_mod`: a load `TIMEOUT` means the mod is being opened and may still finish;
check `get_status`).

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
  undo step); (4) wait until the editor has pushed the pre-edit snapshot as an undo step (at most 3 s or until the
  call's deadline, whichever comes first; normally the next frame); (5) reply. Step 3 is the commit point: a
  `TIMEOUT` can only happen before it (nothing applied, safe to retry); once the edit is applied the call always
  returns its result (see [Timeouts and the commit point](#timeouts-and-the-commit-point)). Structural edits (`duplicate_object`, `delete_object`, `reparent_object`) push their own
  undo event exactly like the editor's Duplicate / Delete / hierarchy drag and drop.
- **Mouse button held**: if the left mouse button is held in the game window during step 2, the call fails with
  `INVALID_STATE` after 5 s and only the selection changed (never a silent non-undoable edit). If it is held during
  step 4 (or the push is not seen before the wait ends for another reason, e.g. the deadline), the edit is done and
  undoable, but the editor records the step only when the button is released or the selection changes: the reply then
  has `undoPending: true`.
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
- triggers and action lists (`ModLogicTrigger`, `ModLogicTrigger[]`, `ModAction`): use the action tools
  (`add_action`, `set_action`, `remove_action`, `move_action`, `add_trigger`, `set_trigger`, `remove_trigger`; see
  [Action editing](#action-editing));
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
| scene-object references (`Transform`, `GameObject`, component types) | as for assets: the referenced component's guid (the `guid` of a reference in `get_object`), or an **object** guid (`ModTransform` guid). An object guid gives the object's `Transform` (or its `GameObject`) when the type is `Transform`, `GameObject` or another type the Transform is an instance of (e.g. `Component`); otherwise the **one** component of that object whose class or wrapped Unity component matches the type (e.g. its Audio Source for an `AudioSource` field), using that component's registered asset, like dropping the object from the hierarchy onto the field. No matching component: refused, the message names the expected type and lists the object's components with their guids; several matching components: refused, the message lists their guids (pass the component guid you mean) |
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
- If the call's deadline comes near after the first step (very slow frames), no further step is started and the reply
  has `undone` < `steps` with a `note` ("stopped after N of M steps ..."); call `undo` again for the rest.

Errors: guard codes, `INVALID_STATE`, `TIMEOUT`.

---

## Action editing

Increment 3. Read tools `list_action_types`, `list_trigger_types` (and `get_action_graph` above); write tools
`add_action`, `remove_action`, `move_action`, `set_action`, `add_trigger`, `remove_trigger`, `set_trigger` (guard kind
**OpenMod**; the common rules of [Editing](#editing) apply: edit-ready checks, write queue, 15 s timeout).

### Model (what the tools change)

A **trigger** (`ModLogicTrigger`) holds a flat, ordered **action list** (`Actions`, polymorphic `ModAction[]`).
Triggers live on components (fixed slots, or the Logic component's user-editable list) and in the minigame's player
settings. The Action Editor window and the Logic inspector edit this model directly (they assign new arrays and write
private fields); there is no command layer. The tools do exactly the same writes:

| Tool | What the editor does (and the bridge does the same) |
|---|---|
| `add_action` (append) | Action Editor "Drag New Action Here": `Actions = Actions.AddElement(typeof(ModAction))`, a new instance (`Activator.CreateInstance` of the palette entry's class) in the last slot; for target actions, if the call site has default target flags (`callSite.appendDefaultTargetFlags`), `TargetFlags` = those |
| `add_action` (insert) | a palette entry dropped onto an action: `List<ModAction>.Insert(i, new instance)`, assigned back; **no** call-site default targets (the action keeps its constructor default: Receiver, or Source for the board-player actions) |
| `remove_action` | the action header's close (X) button: `Actions = Actions.DeleteElement(i, typeof(ModAction))` |
| `move_action` | dragging an action header over its neighbours (the window swaps it with each hovered action), i.e. remove at `index`, insert at `newIndex` |
| `set_action` | the action's inspector (`DrawInspector`) writing its private fields; the target toggles (`TargetFlags` setter); the Advanced target list (`Targets` setter, which wraps each component in a new `ModAssetReference<Component>`) |
| `add_trigger` | Logic inspector "Add Trigger" (`new StartTrigger()` appended, `Triggers = list.ToArray()`), then, for another `type`, the Event dropdown (a new instance of that type) |
| `remove_trigger` | Logic inspector header close (X): `list.Remove(trigger)`, `Triggers = list.ToArray()` |
| `set_trigger` `type` | Logic inspector Event dropdown: `trigger.CreateInstance(type)`, **only the actions are copied** (trigger parameters reset to the new type's defaults), written back into a new `Triggers` array |
| `set_trigger` `parameters` | the trigger's `DrawInspector` (Hit Type flags, Weapon Type flags, Timer interval) |

The Action Editor has no duplicate/copy/paste of actions, so the bridge has none. Action lists are flat; the only
nesting is the **Display Popup** (board) action, whose choices each have an "On Select" action list.

### Addressing a trigger

Used by all action write tools, `set_trigger`, `remove_trigger` and (optionally) `list_action_types`.

| Param | Type | |
|---|---|---|
| `scope` | `"object"` (default) or `"playerSettings"` | case-insensitive |
| `guid` | string (GUID) | object scope (required): the object's `ModTransform` guid |
| `component` | string | object scope, optional: component class name, UI name or component guid (as `get_action_graph`'s filter); needed only when several components of the object have a trigger with that name |
| `trigger` | string, required | object scope: `triggerName` from `get_action_graph`: a trigger property (`OnEnterActions`, `OnUseTrigger`, `WeaponHitTrigger`, `OnEnterTrigger`, ...) or a Logic list entry `Triggers[i]`; exact name first, then case-insensitive. Player-settings scope: one of `weaponHitTrigger`, `tickTrigger`, `onDeathTrigger`, `onRespawnTrigger`, `onFirstSpawnTrigger`, `onRoundEndTrigger` (no index) |
| `path` | array, optional | nested popup-choice lists: `[{"action": <index of a Display Popup action in the current list>, "choice": <0-based choice index>}, ...]`, at most 8 steps; each step enters that choice's "On Select" list |

- Player-settings scope: Minigame mods only (`INVALID_PARAMS` otherwise); `guid`/`component` must be absent.
  `hitTrigger` exists in the data but the editor does not show it, so it is not addressable (`NOT_FOUND`).
- Logic list entries are addressed by index; `remove_trigger` shifts the following entries down by one.
- Only choices below the popup's `m_choiceCount` are addressable (the inspector shows only those).
- Errors: `NOT_FOUND` (no such object / component / trigger, the message lists the object's trigger names; a `path`
  index out of range), `INVALID_PARAMS` (malformed address, ambiguous trigger name, a `path` step that is not a
  Display Popup action, a slot the inspector does not currently show: see `get_action_graph.editable`),
  `INVALID_STATE` (an object outside the editing context, a null trigger in the model).

### Call sites and the two palette gates

The Action Editor offers an action only if both gates pass (`ActionEditorWindow.GetAvailableActionTypes`):

1. **Mod type**: `EditorSystem.CanUseItem(meta.ModTypes)` for the open mod (Minigame-only actions in Minigame mods,
   board actions in Board mods; Item and BoardSpace mods get only the actions available in all mod types).
2. **Call site**: the board actions (Heal Player, Damage Player, Give Keys, Drop Keys, Give Trophy, Remove Trophy,
   Give Item, Spawn keys, Move To Space, Set Connection Active, Set Board Space Active, Display Popup) are offered only
   in the Board Space Event's two lists and in Display Popup choice lists.

Per call site (from the inspectors):

| Trigger | Receiver / Source labels | Source toggle | Default targets on append | Board actions |
|---|---|---|---|---|
| Logic `Triggers[i]` | This Object / Activator | hidden unless the trigger type is Hit or WeaponHit | none | no |
| Trigger `OnHitActions` (shown while `TriggerOnHit` is true or the shape is CustomCollider) | This Trigger / Activator | shown | none | no |
| Trigger `OnEnterActions` / `OnExitActions` / `OnStayActions` (shown while the matching `TriggerOn...` is true and the trigger is not in hit mode) | This Trigger / Activator | shown | Source | no |
| Item `OnPickupTrigger` (always), `OnWhileHeldTrigger` and `OnDropTrigger` (not for ItemType Pickup), `OnUseTrigger` (ItemType Carried or Weapon) | Item / Player | shown | none | no |
| Weapon `WeaponHitTrigger` | Weapon / Hit Object | shown | none | no |
| Hittable `OnHitActions` | This (Destructible) / Hitter | shown | none | no |
| Board Space Event `OnEnterTrigger` (On Player Land), `OnPassTrigger` (On Player Passing) | This Board Space / This Player | shown | none | yes |
| Display Popup choice "On Select" (`path`) | This Board Space / This Player | shown | none | yes |
| player settings `weaponHitTrigger` | Hit Player / Source Player | shown | none | no |
| player settings `onDeathTrigger` | Dying Player / Damage Source | shown | none | no |
| player settings `tickTrigger`, `onRespawnTrigger`, `onFirstSpawnTrigger` (On Round Start), `onRoundEndTrigger` | Player / Source | hidden | Receiver | no |
| any other component trigger (generic inspector) | Receiver / Source | shown | none | no |

### Target flags and targets

`targetFlags` (target actions only; `INVALID_PARAMS` "has no targets" for Wait and Set Gravity): flag names joined by
`,` (`"Source, Advanced"`), an array of names, an integer 0-7 (Source=1, Receiver=2, Advanced=4), or `"0"` / `""` /
`"None"` for none. The Action Editor recomputes the flags from the **visible** toggles every time it draws the action,
so a hidden flag would be cleared; therefore refused:

- a flag whose toggle is hidden: Source where the call site hides it (table above); the action's own hidden toggles:
  Advanced for Change Health, Change Score, Change Player Velocity, Modify Player, Give Minigame Item and Drop
  Minigame Item; Receiver and Advanced for Display Popup;
- any change when two toggles are hidden: the remaining toggle is drawn disabled, so the flags stay at their current
  value (e.g. Change Health in a Logic Start list: only Receiver, fixed; Display Popup: only Source, fixed).

`targets`: the explicit (Advanced) target list, replaced as a whole: an array of references (an object guid, a
component guid, or `{"guid": ...}` as `get_action_graph` returns them; null = an empty entry), max 1024. Accepted only
while the resulting flags include Advanced (the editor shows the list only then). Each element is resolved like the
editor's object picker (see the reference rules of `set_property`) to the action's accepted target type: `Transform`
(Scale, Position, Rotation, Play Tween Animation, Set Active, Spawn Effect, Spawn Prefab, Match Transform),
`AudioSource` (Play Sound), `BoardNode` (board-player actions, Spawn keys, Set Connection Active, Set Board Space
Active: a board space object), `Component` (all other target actions; an object guid then gives the object's
Transform). For the other types an object guid gives that object's one matching component (e.g. the Audio Source of a
speaker object for Play Sound); none or several matching components are refused with the object's components and
their guids listed (see the reference rules of `set_property`). Components of scene objects may reference only scene
objects, components inside a prefab only objects of the same prefab, player-settings lists only scene objects. The
resolved component must be registered as a mod asset, so that its guid is written when the mod is saved
(`INVALID_PARAMS` otherwise); an object guid resolves through the component's registered asset, so it is stored like
the component guid. Show Message uses its targets only
while `m_messageTarget` includes `Target`.

### Parameters (add_action, set_action, add_trigger, set_trigger)

`parameters` is an object `{name: value}` (max 64 entries). Names are the serialized field names `get_action_graph`
reports (`m_seconds`); the name without `m_` is accepted as an alias (`seconds`), then a unique case-insensitive match.
Unknown names (the message lists the valid ones), a name given twice (also via its alias), wrong value types,
undefined enum values, enum values the editor's dropdown does not offer, and references to missing assets/objects or
of the wrong type are refused with `INVALID_PARAMS` and nothing changes: all values are validated before anything is
selected or changed. Value shapes are those of `set_property` (numbers, bools, strings, enum names or integers, flags
joined by `,`, `{x, y, z}`, `{r, g, b, a?}`; a flags value with no flag set is the integer `0`, since `get_action_graph`'s
`"0"` string is not a flag name), plus:

| Field type | JSON accepted |
|---|---|
| `ModComplexFloat` / `ModComplexInt` (`m_seconds`, `m_interval`) | a number (= `{"valueType": "Constant", "min": n}`), or `{valueType?, min?, max?}`: given keys only, the others are kept; `valueType` `Constant` or `RandomBetweenTwoValues` (or 0 / 1). With `RandomBetweenTwoValues`, `min > max` is refused unless the field's clamp fixes it. `ModComplexInt` needs integers |
| `ModAssetReference<T>` (`m_clip`, `m_item`, `m_prefab`, `m_transformToMatch`, `m_speakerImageReference`) | null, a guid or `{"guid": ...}`: a mod or built-in asset (`list_assets`), or an object/component for `Transform`; type-checked against `T`; must be a registered mod asset |
| `ModAssetReference<T>[]` (`m_prefabs`, `m_spaces`) | an array of such references (replaces the list) |
| `DamageInstance` (`m_damageInstance`) | an object with any of the drawn fields `damage`, `ragdoll`, `ragdollVel`, `hitAnim`, `removeKeys`, `blood`, `bloodVel`, `bloodAmount`, `sound`, `volume` (others refused); unspecified fields are kept |
| Display Popup `m_choices` | an array patch: element `i` updates choice `i` (null = unchanged); keys `m_buttonText`, `m_costType`, `m_costAmount`, `m_itemCostType`, `m_choiceIconReference` (Texture reference), the `m_` prefix may be omitted; at most `m_choiceCount` elements |

Fields are written directly, except where the inspector writes through a property: `m_prefabs` (`Prefabs`), `m_item`
of Give Minigame Item (`ItemPrefab`; the prefab's root object must have an Item component, else refused with the
editor's message), `m_prefab` of Set Player Visual (`Prefab`), `m_transformToMatch` (`TransformToMatch`), `m_spaces`
(`Spaces`), `m_speakerImageReference` (`SpeakerImage`), choice icons (`ChoiceIcon`); `m_clip` is set through its
`Asset` as the inspector does. Give Item and Set Player Visual first run the action's own version upgrade
(`UpdateVersion`, which the editor runs whenever it draws the action): a version-0 Give Item then has `m_items` reset
to the single item of its obsolete `m_item` before the given values are applied. Note that a Give Item action added
without parameters stays at version 0, exactly as one added in the editor, and gets that reset the first time the
editor draws it. Raising Display Popup `m_choiceCount` adds empty choices, as the inspector does.

Clamps: where the inspector re-applies a limit on every frame it draws the action, the bridge stores the limited
value and lists the parameter path in `clamped` (e.g. `"m_volume"`, `"m_seconds.max"`, `"m_damageInstance.damage"`,
`"m_choices[0].m_costAmount"`):

| Action / trigger | Limits (from the classes' `DrawInspector`) |
|---|---|
| Wait | `m_seconds.min` 0..3600, `m_seconds.max` min..3600 |
| Timer trigger | `m_interval.min` >= 0.1 |
| Play Tween Animation | `m_time` = Clamp(Abs(t), 0, 3600); `m_easeType` not `notUsed`, `punch`, `once`, `clamp`, `pingPong` (refused) |
| Set Gravity | `m_value` each axis -1000..1000 |
| Modify Player | `m_movementSpeed` 0..100; `m_gravity` each axis -1000..1000 |
| Play Sound | `m_volume` 0..1 |
| Show Message | `m_duration` 0.5..1200 |
| Stun Player | `m_stunDuration` 0..1000 |
| Drop Minigame Item | `m_itemSlot` not `DefaultForItemType` (refused) |
| Heal Player | `m_value` 1..30 |
| Damage Player | `damage` 1..30, `ragdollVel` 0..20, `bloodVel` 0..20, `bloodAmount` 0..4, `volume` 0..1 |
| Give Keys / Drop Keys | `m_value` / `m_keys` = Clamp(Abs(n), 1, 200) |
| Spawn keys | `m_keys` 1..40 |
| Give Trophy / Remove Trophy | `m_trophiesToGive` / `m_trophiesToRemove` 1..20 |
| Display Popup | `m_choiceCount` 0..4; choice `m_costAmount`: Health 1..29, other costs 1..1000 (applied when the amount or the cost type is given) |

Other refusals: `GiveItemAction.m_item` (obsolete), `SetConnectionActiveAction.m_connectionMappings` (built by the
Action Editor from the board connections), `BoardPopupAction.m_isTimed` / `m_timedDuration` and
`BoardSpaceTrigger.m_hitType` (not shown in the editor); Set Player Visual: `m_recolorPrefabUsingPlayerColor`, and child
indices other than -1, while no prefab is set; child indices outside -1..childCount-1 of the prefab's root object;
both child indices equal (and not -1); Display Popup `m_showCloseButton` while every shown choice has a cost (the
checkbox is disabled). Parameters that the inspector shows only in some modes (e.g. Modify Player `m_gravity` while
the type is MovementSpeedMultiplier) are accepted; `list_action_types` notes such conditions.

### Common reply fields and undo

Write replies carry the address (`guid`, `component` (class name), `componentGuid`, `trigger`, and `path` when given;
`scope: "playerSettings"` instead of `guid`/`component`/`componentGuid`), the tool's own fields and the edit fields
`changed`, `undoable`, `undoPending` (only when true), `note` (only when there is something to explain),
`undoStackLength`.

- Object scope: one call = **one undo step**, through the edit pipeline of [Editing](#editing) (the object holding the
  trigger is selected, its snapshot taken, all changes of the call applied in one frame, the editor pushes the step).
  The editor's undo snapshot of the selected object contains its triggers and actions (the mod-file writer serialises
  each `ModLogicTrigger` property: trigger type, trigger JSON, then each action's type + JSON), so `undo` restores
  action lists, parameters, targets and Logic trigger lists. After an undo the Action Editor window may still show the
  pre-undo trigger object until it is reopened (editor behaviour).
- Player-settings scope: applied at once; `undoable: false` and a `note`: the editor has no undo for mod settings (its
  snapshots cover selected objects only), and editing the settings by hand does not set the editor's dirty flag
  either. The bridge marks the mod as having unsaved edits (`get_status.hasUnsavedEdits`); `save_mod` writes the
  settings to `Data/ModSettings.json`.
- Nothing is saved automatically. Target and asset references are stored as live objects and turned into guids when
  the mod (or an undo snapshot) is serialised, which is why only registered objects are accepted.

Errors (all action write tools): guard codes, `INVALID_PARAMS`, `NOT_FOUND`, `INVALID_STATE` (also: the list or trigger
changed while the call waited for the editor's snapshot; nothing was changed), `TIMEOUT`, `INTERNAL`.

### `list_action_types` (read)

| Param | Type | |
|---|---|---|
| (address) | see [Addressing a trigger](#addressing-a-trigger) | optional: `guid` + `trigger` (+ `component`, `path`), or `scope: "playerSettings"` + `trigger`; then gate 2 and the call-site fields are evaluated for that trigger. Any address key present makes `trigger` required |

Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`, address errors; with an address `INVALID_STATE` while a playtest is
starting, running or ending (without an address it works and the result has `"playMode": true`). Works on any open mod.

```json
{"modType": "Minigame",
 "trigger": {"component": "ModTrigger", "componentGuid": "aa10...", "triggerName": "OnEnterActions", "...": "a get_action_graph entry"},
 "actionTypes": [
  {"type": "ChangeHealth", "value": 96, "className": "ChangeHealthAction", "uiName": "Change Health",
   "category": "Minigame Logic", "helpText": "Modify a player's health.", "modTypes": ["Minigame"], "boardSpaceOnly": false,
   "parameters": [
     {"name": "m_operation", "alias": "operation", "type": "ModActionOperation", "kind": "enum",
      "enumValues": ["Set", "Add", "Subtract", "Multiply", "Divide"], "default": "Set", "writable": true},
     {"name": "m_value", "alias": "value", "type": "Int32", "kind": "integer", "default": 0, "writable": true}],
   "supportsTargets": true, "defaultTargetFlags": "Receiver", "hiddenTargetFlags": ["Advanced"], "targetType": "Component",
   "availableTargetFlags": ["Source", "Receiver"], "targetFlagsLocked": false, "appendTargetFlags": "Source",
   "usableHere": true}]}
```

- `trigger` only with an address. One row per action type of the palette registry (`ModActionMeta`), ordered by
  `value` (the persisted `ModActionType` number). `type`: the name the action tools take (they also accept `className`
  or `uiName`). `category`: the palette group (`Utility`, `Visuals`, `Movement`, `Minigame Logic`, `Board Logic`).
  `modTypes`: gate 1 flags. `boardSpaceOnly`: gate 2 flag.
- `parameters`: every serialized field (the names `get_action_graph` uses): `name`, `alias` (without `m_`; absent when
  the name has no prefix), `type` (C# type), `kind` (`bool`, `integer`, `number`, `string`, `enum`, `flags`, `vector2`,
  `vector3`, `vector4`, `color`, `complexFloat`, `complexInt`, `reference`, `referenceArray`, `object`, `choices`,
  `unsupported`), `enumValues` (offered values only; enum and flags kinds) and `flags: true` for flag enums,
  `referenceType` (reference kinds), `default` (value in a new instance, `get_action_graph` shape), `min` / `max` (only
  when a clamp exists), `writable` (+ `reason` when false), `notes` (conditions, clamps).
- Target actions: `defaultTargetFlags` (constructor default, also what an insert keeps), `hiddenTargetFlags` (the
  action's own hidden toggles), `targetType` (accepted type of `targets`); with an address also `availableTargetFlags`,
  `targetFlagsLocked` (two toggles hidden) and `appendTargetFlags` (the flags `add_action` gives the action when
  appending there). Other actions: `supportsTargets: false`, `defaultTargetFlags: null`.
- `usableHere`: gate 1 for the open mod and, with an address, gate 2 for that call site (without an address gate 2 is
  not evaluated; see `boardSpaceOnly`). `reason` when false. `add_action` refuses exactly these.

### `list_trigger_types` (read)

No params. Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`. During a playtest the result has `"playMode": true`.

```json
{"modType": "Minigame", "triggerTypes": [
  {"type": "Timer", "value": 96, "className": "TimerTrigger",
   "parameters": [{"name": "m_interval", "alias": "interval", "type": "ModComplexFloat", "kind": "complexFloat",
                   "default": {"valueType": "Constant", "min": 1.0, "max": 2.0}, "writable": true,
                   "notes": "min clamped to >= 0.1 (TimerTrigger.DrawInspector)"}],
   "sourceTargetAvailable": false, "addable": true}]}
```

One row per `ModTriggerType` in enum order (None, Start, Enable, Update, Hit, WeaponHit, Timer, BoardSpace,
RoundStart, RoundEnd). `addable`: offered by the Logic component's Event dropdown in this mod type (Minigame and other
types: all except None and BoardSpace; Board: also not Hit, WeaponHit, RoundStart, RoundEnd); `reason` when false.
`sourceTargetAvailable`: the Source target toggle is shown in a Logic list of this type (Hit, WeaponHit). Parameters as
in `list_action_types` (Hit: `m_hitType` flags CollisionEnter, CollisionStay, CollisionExit; WeaponHit: `m_hitType`
flags Melee, Projectile; Timer: `m_interval`; BoardSpace: `m_hitType`, not writable).

### `add_action` (write)

| Param | Type | Default | |
|---|---|---|---|
| (address) | | | the trigger, see [Addressing a trigger](#addressing-a-trigger) |
| `type` | string, required | | `ModActionType` name (`ChangeHealth`), class name or UI name, case-insensitive |
| `index` | int 0..100000 | append | position of the new action: omitted or = action count: append; 0..count-1: insert before the action at that index |
| `parameters` | object | | see [Parameters](#parameters-add_action-set_action-add_trigger-set_trigger) |
| `targetFlags` | flags | | see [Target flags and targets](#target-flags-and-targets) |
| `targets` | array | | requires Advanced in the resulting flags |

Refused (`INVALID_PARAMS`): unknown type; the action fails a palette gate (the message gives the reason, e.g. "Heal
Player is only available in the action lists of Board Space Event triggers and Display Popup choices (not in Logic: On
Start)"); `index` > count (the message gives the range). Order on the new instance: constructor defaults; on append
only, the call site's default target flags; then `parameters`, `targetFlags`, `targets`. An insert uses the window's
insert path, which does not apply the call-site default (the window inserts after the action it is dropped on, so
index 0 corresponds to inserting and then dragging the action to the top).

```json
{"guid": "0b6d...", "component": "ModTrigger", "componentGuid": "aa10...", "trigger": "OnEnterActions",
 "index": 0, "mode": "append", "defaultTargetsApplied": true,
 "action": {"index": 0, "type": "ChangeHealth", "className": "ChangeHealthAction", "version": 0, "targetFlags": "Source",
            "targets": [], "parameters": {"m_operation": "Subtract", "m_value": 1}},
 "clamped": [], "actionCount": 1, "changed": true, "undoable": true, "undoStackLength": 5}
```

`mode`: `append` or `insert`; `defaultTargetsApplied`: the call-site default was applied; `action`: the new action as
`get_action_graph` shows it; `actionCount`: length of the list afterwards.

### `remove_action` (write)

Params: (address), `index` (int, required). `parameters`, `targetFlags`, `targets`, `type` are refused.

```json
{"guid": "0b6d...", "component": "ModLogic", "componentGuid": "aa10...", "trigger": "Triggers[0]", "index": 1,
 "removed": {"type": "Wait", "className": "WaitAction"}, "actionCount": 2,
 "changed": true, "undoable": true, "undoStackLength": 6}
```

Removing a Display Popup action removes its nested choice lists with it.

### `move_action` (write)

Params: (address), `index` (int, required), `newIndex` (int, required: the final position, 0..count-1).
`parameters`, `targetFlags`, `targets`, `type` are refused. Same index: `changed: false`.

```json
{"guid": "0b6d...", "component": "ModLogic", "componentGuid": "aa10...", "trigger": "Triggers[0]", "index": 2,
 "newIndex": 0, "order": ["SetActive", "Wait", "ShowMessage"], "changed": true, "undoable": true, "undoStackLength": 7}
```

`order`: the action types of the list after the move.

### `set_action` (write)

Params: (address), `index` (int, required), and at least one of `parameters`, `targetFlags`, `targets`. All changes of
the call are one undo step. Values equal to the current ones give `changed: false`.

```json
{"guid": "0b6d...", "component": "ModLogic", "componentGuid": "aa10...", "trigger": "Triggers[0]", "index": 0,
 "action": {"index": 0, "type": "Wait", "className": "WaitAction", "version": 0, "targetFlags": null, "targets": [],
            "parameters": {"m_seconds": {"valueType": "Constant", "min": 3600.0, "max": 3600.0}}},
 "clamped": ["m_seconds.min", "m_seconds.max"], "changed": true, "undoable": true, "undoStackLength": 8}
```

### `add_trigger` (write)

| Param | Type | Default | |
|---|---|---|---|
| `guid` | string (GUID), required | | object with a Logic component |
| `component` | string | | the Logic component (class/UI name or guid) when the object has several |
| `type` | string | `"Start"` | `ModTriggerType` name or trigger class name; must be `addable` (`list_trigger_types`) |
| `parameters` | object | | trigger parameters (Hit/WeaponHit `m_hitType`, Timer `m_interval`) |

`trigger`, `scope` and `path` are refused (only Logic components have an editable trigger list). `NOT_FOUND` if the
object has no Logic component; `INVALID_PARAMS` on a prefab instance or for a type that is not addable. The trigger is
appended (the inspector has no insert or reorder). Result: the new trigger as a `get_action_graph` entry (its
`triggerName` is `Triggers[n]`) plus `guid`, `clamped` and the edit fields.

### `remove_trigger` (write)

Params: (address) with `trigger` = `Triggers[i]` of a Logic component (no `path`). Fixed slots cannot be removed
(`INVALID_PARAMS`; clear them with `remove_action`). `type`, `parameters`, `index` are refused.

```json
{"guid": "0b6d...", "component": "ModLogic", "componentGuid": "aa10...", "trigger": "Triggers[1]",
 "removed": {"triggerType": "Timer", "actionCount": 3}, "triggerCount": 1,
 "indexNote": "the following triggers moved up by one (Triggers[2] is now Triggers[1], ...)",
 "changed": true, "undoable": true, "undoStackLength": 9}
```

### `set_trigger` (write)

Params: (address), and `type` and/or `parameters`.

- `type`: only for Logic list entries (`Triggers[i]`) and only `addable` types; a different type replaces the trigger
  object with a new one of that type, keeps the action list and resets the trigger parameters (`parameters` are then
  applied to the new trigger). Same type: no change. Fixed slots and popup choices: refused.
- `parameters`: the trigger's drawn fields: Hit `m_hitType` (Logic Hit triggers and the Hittable `OnHitActions`),
  WeaponHit `m_hitType` (Logic WeaponHit triggers and player settings `weaponHitTrigger`), Timer `m_interval` (Logic
  Timer triggers and player settings `tickTrigger`; min >= 0.1). Plain action holders (Trigger, Item and Weapon slots,
  popup choices, the death/respawn/round player-settings triggers) have no parameters; Board Space Event triggers'
  `m_hitType` is refused.

Result: the trigger as a `get_action_graph` entry plus `guid` (object scope), `clamped`, `previousTriggerType` and a
`note` when the type changed, and the edit fields.

## Validation, playtest and batch

Increment 4. Read tool `validate_mod`; write tools `start_playtest`, `stop_playtest`, `run_batch`.

### `validate_mod` (read)

No params. Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`, `INVALID_STATE` (a playtest is starting, running or
ending), `INTERNAL` (the validator threw, or its validator list is incomplete, see below). Works on any open mod.

Runs the editor's own Mod Validator: `EditorSystem.ValidateCurrentMod()` → `ModValidationSystem.ValidateCurrentMod()`,
which calls every `ModValidationCallbackReceiver` (`ModMinigameValidator` for Minigame mods, `ModBoardValidator` for
Board mods; Item and BoardSpace mods have no validator and are always valid). This is the same call behind
File > Validate Mod, the toolbar's Play button and the Publish dialog (which enables its publish button only if the
result has no errors).

```json
{"mod": "PB_TEST_Arena", "modType": "Minigame", "valid": false, "canPlaytest": false, "canPublishPerValidator": false,
 "errorCount": 1, "warningCount": 2, "infoCount": 0,
 "issues": [
   {"severity": "error", "message": "There are no player spawns in your map.",
    "description": "Every minigame map is required to have at least enough player spawns to support 8 players.", "fixable": true},
   {"severity": "warning", "message": "Empty minigame description.", "description": "The minigame description has not been set. ...", "fixable": false},
   {"severity": "warning", "message": "Missing minigame preview image.", "description": "...", "fixable": false}],
 "validators": ["ModBoardValidator", "ModMinigameValidator"]}
```

- `valid`: no errors (`ModValidationResult.IsModValid`). `canPlaytest`: `valid` and the mod type has a playtest (not
  BoardSpace; then `canPlaytestReason`); the Play button would start without the validator popup.
  `canPublishPerValidator`: `valid` (the Publish dialog's other conditions, e.g. Steam online, are not checked).
- `issues`: errors, then warnings, then info, each in the validator's order (the popup's order). `severity`
  `error | warning | info`; `message` = the issue title; `description` only when it differs from the title; `helpLink`
  only when set (the validators set none); `fixable`: the editor offers a "Fix" button (the bridge never runs fixes).
  The editor records **no object reference** for an issue, so there is no `objectGuid` / `objectName` / `component`;
  board messages list the affected spaces inside `description` as `- <name> at (<x>, <y>, <z>)`.
- `validators`: class names in the editor's cached validator list (diagnostic).
- `note`: when a prefab is being edited (the scene root is inactive, so the validator, which uses
  `FindObjectsOfType`, sees no scene objects; the Play button leaves the prefab first), or on the session's first
  validation.

**Side effects (from the source):** none on the mod or the editor UI. The validators only read (settings,
`FindObjectsOfType`, `Physics.Raycast` / `CheckCapsule`, board node connections, Move To Space targets), select nothing,
open no popup, push no undo step, write no file and store their fix actions without running them. Harmless residue:
each validator instance keeps a reference to the last mod/result until the next validation; `ModPlayerSpawn.DetermineRespawnPoint`
logs `Failed to get spawn point for player index N` (Unity error log) when respawning is enabled and a player has no
respawn spawn. The only global state is the static validator list, which the first validation of the session builds with
an AppDomain-wide type scan (as the editor's own first Validate/Play does). If that scan threw, the list would stay
incomplete for the whole session (also for the editor's own Play/Publish checks); the bridge therefore checks after
every run that both validators are listed and otherwise returns `INTERNAL` ("restart the game"). Hence a read tool that
works on any open mod.

#### What a Minigame mod needs to pass the validator (`ModMinigameValidator`)

Errors (block Play without "Run With Errors", and publishing):

1. Minigame Settings `NumberOfRounds` > 0 (default 1).
2. At least one **active** Player Spawn (`kind: "playerSpawn"`) in the scene (inactive objects and deleted objects are
   not seen; the validator also sees active spawns anywhere in the loaded scenes).
3. For every player count 1..8: an initial-spawn Player Spawn whose `PlayerCountMask` includes it.
4. For every player 1..8: an initial-spawn Player Spawn whose `AllowedPlayers` includes that player.
5. At least one initial spawn with `SharedSpawn` true, or at least 8 non-shared initial spawns.
6. If player settings `respawnEnabled` (default true): at least one spawn with `SpawnUsageType` `Respawn`, and for every
   player 1..8 a respawn spawn that allows that player.

A single Player Spawn with its defaults (`SharedSpawn` true, `SpawnUsageType` `InitialSpawn, Respawn`, all players, all
counts) satisfies 2-6. Warnings (do not block): empty minigame description; no preview image (both only settable by
hand in Minigame Settings > Minigame Details); end condition `RemainingPlayersAlive` while respawning is enabled
(never ends); an initial spawn without a collider below it (raycast straight down from 0.25 above the spawn, any
collider: a cube/plane/prop object has a Mesh collider by default, Prop `collisionType` `Mesh`).

#### What a Board mod needs (`ModBoardValidator`), briefly

Errors: at least one board space (the message asks for three in a circuit); exactly one space of type Start; every space
has at least one enabled (not "Start Disabled") Forward/Both connection to an active space (no dead ends); every space
reachable from Start/Graveyard through forward connections or Move To Space actions; no disconnected groups; spaces
reachable only through Move To Space must have Supports Goal disabled; at least two spaces that can hold the trophy
chest (not an intersection, not Pathing, not Graveyard/Weapon/Trophy, Supports Goal on, not start-disabled, no incoming
random teleport). Warnings: description, preview image, music/ambience volume very low, no Graveyard, spaces without a
`WorldGround`-layer collider below them. The bridge cannot edit board connections yet, so a Board mod cannot be made
valid through the bridge.

### `start_playtest` (write)

| Param | Type | Default | |
|---|---|---|---|
| `players` | integer 2-8, or string | the toolbar's current choice | the toolbar's player-count dropdown: `"2 Players"` ... `"8 Players"`, `"2v2 Teams"`, `"2v2v2 Teams"`, `"2v2v2v2 Teams"`, `"3v3 Teams"`, `"4v4 Teams"` (case-insensitive), also `"2"`..`"8"` and `"2v2"`, `"2v2v2"`, `"2v2v2v2"`, `"3v3"`, `"4v4"` |
| `aiOnly` | bool | the toolbar's current checkbox | the toolbar's "AI Only" checkbox: every player is an AI |
| `validateFirst` | bool | `true` | refuse when the validator reports errors; `false` = the validator popup's "Run With Errors" |

Limit 60 s (commit grace 4 s on top; the MCP client needs ≥ 64 s). Guard: OpenMod (loaded test mod, state `Editing`), so
a call while a playtest is starting, running or ending is refused with `INVALID_STATE`.

**What the Play button does** (`ToolbarWidget.Draw`): if a prefab is open, `CurrentPrefab = null` (saves the prefab and
returns to the scene); `ValidateCurrentMod()`; if valid → `SetEditorState(EnteringPlayMode)`, else the validator popup
(whose "Run With Errors" calls `SetEditorState(EnteringPlayMode)`). The button is disabled without a mod, when the
editor is not in `Editing` (and not paused) and for 1 s after a background (Workshop) mod load. The dropdown and
checkbox are drawn every frame and their `Draw` writes `GameManager.TeamMode`, `NumTestPlayers`,
`TestPlayersTeamComposition` (team choices only) and `AIOnlyTest` from the widget's fields; a dropdown change is stored in
`PlayerPrefs` `ModEditorPlayerCountChoice`. `SetEditorState(EnteringPlayMode)` sets the state first, then
`GameManager.partyGameMode = BoardGame`, `CurMap = null` (Minigame), **`CurrentMod.Save()`**, and, only if no background
mod load is running, `WorkshopController.AddModInBackground(modPath)` (a second, runtime copy of the mod loaded from the
just-saved files) → `OnEditorPlay()` (opens `WorkshopMinigame_Scene` / `WorkshopMap_Scene` / `PirateMap_Scene` for
Item mods) and `SetActive(false)` (hides the editor's hierarchy/camera); on a load failure it reverts to `Editing` and
shows its error popup. `PlayMode` is set later by the play scene (`GameBoardController`, or `sceneLoaded` for Item
mods).

**The tool, in order** (all before the commit point refuse with nothing changed):

1. Refusals: a create/open load pending (`BUSY_LOADING`); an editor dialog open; a prefab being edited (the bridge never
   saves a prefab implicitly: return to the scene by hand); a BoardSpace mod (it has no play scene: the editor would
   hide itself and stay in `EnteringPlayMode`); `WorkshopController.CurrentBackgroundLoadMod != null` or the toolbar's
   1 s background-load timer running ("Subscribed mods are being installed in the background", as the disabled button's
   tooltip; calling `SetEditorState` then would leave the editor stuck in `EnteringPlayMode`);
   `WorkshopController.CurrentActiveMod == null` (dereferenced by `SetEditorState`). All `INVALID_STATE`.
2. `ValidateCurrentMod()` (always, as the button). With errors and `validateFirst` true: `INVALID_STATE`, message
   `the mod does not pass the editor's validator (N validator errors: <titles>) ...`, `error.data.validation` = the
   `validate_mod` counts + issues block (`valid`, `errorCount`, `warningCount`, `infoCount`, `issues`).
3. Commit point (needs ≥ 20 s of the limit left, else `TIMEOUT`, nothing changed).
4. Apply the toolbar choices exactly as the dropdown/checkbox do (widget fields, `PlayerPrefs` when the choice changes,
   the `GameManager` values), then `SetEditorState(EnteringPlayMode)`. **This saves the test mod** (see below).
5. Wait (frame by frame) until `PlayMode`; the reply is sent then. If the editor went back to `Editing` at once:
   `INTERNAL` (playtest copy failed to load; the editor shows its popup; the mod was saved). If it returns to edit mode
   before `PlayMode`: `INVALID_STATE`. If `PlayMode` is not reached 1 s before the deadline: `TIMEOUT` saying the mod
   was saved and the playtest is starting (check `get_status`, then `stop_playtest`; do not retry blindly).

```json
{"playing": true, "saved": true, "state": "PlayMode", "mod": "PB_TEST_Arena", "modType": "Minigame",
 "players": "4 Players", "playerCount": 4, "teams": null, "aiOnly": true, "validated": true,
 "validation": {"valid": true, "errorCount": 0, "warningCount": 2, "infoCount": 0, "issues": ["..."]},
 "secondsToPlayMode": 3.4, "note": "Entering play mode saved the mod's files ..."}
```

`teams`: the team composition array for team choices (`[0,0,1,1]` for 2v2), else null. Input: with `aiOnly` every
player is an AI and the rounds run by themselves; otherwise player 1 is played by a human at the keyboard/gamepad. The
bridge sends **no input**. Errors: guard codes, `INVALID_STATE`, `BUSY_LOADING`, `TIMEOUT`, `INTERNAL`.

### `stop_playtest` (write)

No params. Limit 30 s (MCP client ≥ 34 s). Guard: OpenMod **with `allowPlayMode`** (loaded test mod, writes enabled,
publish block complete; any editor state).

**What the Stop button does** (enabled only in `PlayMode`): `Time.timeScale = 1`, `GameManager.disconnectUserEvent =
LeftGame`, `NetSystem.OnDisconnect(DisconnectReasonLeft)`. That starts the disconnect coroutine, which raises
`DisconnectStarted` → `GameNetworkManager.Disconnect` → `SetEditorState(ExitingPlayMode)` (`FocusSceneView`, open
`ModEditorEmptyScene`, `WorkshopController.RemoveMod(CurrentMod)` drops the runtime copy, reinstall-on-exit queued only for
a published id ≠ 256) and then shuts the local game server down (`NetGameServer.ShutdownServer`) and marks the network
status disconnected; when the empty scene has loaded, `SetEditorState(Editing)` → `SetActive(true)` restores the
hierarchy, camera and gizmos and reloads the lighting. (The coroutine's final cleanup waits for the main menu, i.e. until
the editor is left.) Calling `SetEditorState(ExitingPlayMode)` directly, as doc 01 asked, would skip the server shutdown
and the network status reset, so the next playtest's `GameNetworkManager.Awake` would see a still-connected session: the
direct call leaks the session; the bridge uses the Stop button's path.

**The tool:** `Editing` → success `alreadyStopped: true`, nothing done. `ExitingPlayMode` → waits for `Editing`
(`alreadyStopping: true`, nothing pressed). `EnteringPlayMode` → waits up to 15 s for `PlayMode` (the Stop button is
disabled until then), then stops; still entering → `INVALID_STATE` (nothing changed, retry later); back in `Editing` →
`alreadyStopped`. `PlayMode` → commit point (≥ 10 s left, else `TIMEOUT`), the Stop button's three statements, wait
until `Editing` with the hierarchy active again; not reached 1 s before the deadline → `TIMEOUT` ("the Stop sequence was
sent ..."). An open editor dialog does not block stopping.

```json
{"stopped": true, "alreadyStopped": false, "state": "Editing", "playedSeconds": 41.2, "secondsToEditMode": 1.3,
 "note": "back in edit mode: the edit-time scene was restored as it was before Play ..."}
```

Nothing is saved when stopping; the edit-time scene is exactly as before Play (the playtest ran on the separate copy;
undo stacks and the dirty flag are untouched). Errors: guard codes, `INVALID_STATE`, `EDITOR_NOT_OPEN`, `TIMEOUT`.

### What a playtest writes and what reaches Steam

On disk (all inside the open test mod's folder unless noted):

- At Play: `PummelMod.Save()` writes `Data/Meta.json` (new `LastModified`), `Data/WorkshopItem.json` (publishedFileId
  256: the guard's id hygiene runs right before), `Lighting.json`, `PostProcessing.data`, `MainScene.scene`,
  `ModSettings.json` and the minigame definition / map details, and every external asset's `.pmeta`. Unlike `save_mod`:
  no `Preview.jpg`, the dirty flag is not cleared, the editor's mod list is not touched, and the bridge's
  `hasUnsavedEdits` does not change (the save postfix only watches `SaveCurrentMod`).
- While loading the runtime copy, the game may create missing `.pmeta` files in the mod folder and writes its texture
  cache to `<persistentDataPath>/PummelModCache/256/` (harmless, re-creatable cache keyed by the publishedFileId).
- `PlayerPrefs` `ModEditorPlayerCountChoice` (Windows registry, game preferences) when `players` changes the dropdown,
  as the dropdown does. Log lines in the game's and BepInEx's logs.
- At Stop or at the natural end: nothing (for id 256 no reinstall is queued).

Steam / Workshop (checked in the source):

- No Workshop write: Play/Stop reach none of the blocked publish/update/delete paths; `AddModInBackground` only reads.
- Playtime tracking (`StartPlaytimeTracking` / `StopPlaytimeTracking`) runs only when `EditorSystem.Instance == null`,
  i.e. never in the editor.
- Vote popup (`WorkshopVotePopup`, doc 05 residual risk): it lives on a main-menu window and subscribes to
  `GameBoardController.OnExitMinigame` / `OnEndBoardGame` and the main menu's `OnEnterMainMenu`. `OnExitMinigame` is
  raised only when `EditorSystem.Instance == null`; the board path that raises `OnEndBoardGame` is not reached in the
  editor (`OnTurnsEnd` goes to `ExitingPlayMode` instead); for minigames the popup also requires `MinigameID > 256`,
  which a test mod (id 256) never has. When the user later leaves the editor for the main menu, `ReturnedToMainMenu` could
  consider a Board test mod (`IsWorkshopNotInternal`, id 256): the popup would query Steam for item 256 (read) and, only
  if a human clicks a vote button, call `SteamUGC.SetUserItemVote(256)` for a non-existent item. That call is **not**
  covered by the publish block (votes are deliberately allowed) and is harmless (no item 256). Not verifiable statically:
  whether the main-menu window carrying the popup is alive while the editor runs (it is in the main-menu UI).
- Achievements: Workshop minigames skip the minigame achievements (`SetMinigameWon/Lost` return early); a Board playtest
  with a human player could unlock `ACH_FIRST_GAME` (`SteamUserStats.SetAchievement` + `StoreStats`; not Workshop
  related, not blocked). With `aiOnly` no human player exists.

### Play-mode behaviour of every tool

While a playtest starts, runs or ends (`editorState` ≠ `Editing`) the editor's hierarchy persists unchanged but
deactivated (`SetActive(false)`), the playtest runs on a separate runtime copy loaded from the saved files, and
`WorkshopController.CurrentActiveMod` is that runtime copy, so asset/object references of edit-time objects no longer
resolve (they would read as `registered: false`).

| Tool | During a playtest |
|---|---|
| `ping`, `get_log`, `screenshot` | work unchanged (`get_log` and `screenshot` are the way to observe a playtest; the screenshot shows the editor window with the play view) |
| `get_status` | works; `playtest` block with `running: true`, times, `paused`, `minigame` / `board` state |
| `get_hierarchy` | works; lists the **edit-time** scene and adds `playMode: true` + `playModeNote` (the synthetic root shows `active: false`) |
| `list_assets`, `list_trigger_types`, `list_action_types` without an address | work; result has `playMode: true` (static or editor-mod data) |
| `list_mods`, `list_component_types` | work (array results, no marker; `addable` describes edit mode) |
| `get_object`, `get_action_graph`, `list_action_types` with an address, `get_open_mod`, `validate_mod` | `INVALID_STATE` (references would resolve against the runtime copy; the validator would see the runtime objects) |
| every edit tool, `save_mod`, `undo`, `create_test_mod`, `open_test_mod`, `start_playtest`, `run_batch` | `INVALID_STATE` from the guard in every state except `Editing`, including `EnteringPlayMode` and `ExitingPlayMode` (edit pipelines also re-check each frame and stop if a playtest starts) |
| `stop_playtest` | allowed (its purpose) |

### `run_batch` (write)

| Param | Type | Default | |
|---|---|---|---|
| `steps` | array of 1-100 `{tool, params?, id?}` | required | `tool`: tool name; `params`: object (omitted/null = `{}`); `id`: `[A-Za-z][A-Za-z0-9_-]{0,63}`, unique; no other keys |
| `stopOnError` | bool | `true` | stop at the first failed step; `false` = attempt every step |

Limit 120 s for the whole batch (commit grace 4 s; MCP client ≥ 124 s). Guard: OpenMod for the batch, then each write
step's own guard.

Allowed tools: `create_object`, `duplicate_object`, `delete_object`, `reparent_object`, `set_object`, `set_transform`,
`add_component`, `remove_component`, `set_property`, `set_properties`, `add_action`, `remove_action`, `move_action`,
`set_action`, `add_trigger`, `remove_trigger`, `set_trigger`, `undo`, `save_mod`, and the read tools `get_object`,
`get_hierarchy`, `get_action_graph`, `list_mods`, `list_assets`, `list_component_types`, `list_action_types`,
`list_trigger_types`. Refused: `run_batch`, `create_test_mod`, `open_test_mod`, `start_playtest`, `stop_playtest`,
`screenshot` (named reason), and every other tool (`ping`, `get_status`, `get_log`, `get_open_mod`, `validate_mod`,
unknown names).

**Up-front validation** (any failure refuses the whole batch with `INVALID_PARAMS` naming the step, e.g.
`steps[3] (id 'spawn', set_property): ...; nothing was run`): the shape of every step, unknown or refused tools, ids
(syntax, duplicates), the syntax of every reference and that it points to an **earlier** step's id (unknown id, forward
reference and self reference are refused), and the params of every write step without references, parsed with that
tool's own parser (the same checks as a direct call before it reaches the main thread). Steps with references are parsed
when they run; read tools validate their params when they run.

**References.** Any JSON **string value** at any depth of a step's `params` (object keys are never touched) that starts
with `$` is a reference and must match `$<id><accessor>{1,8}`; an accessor is `.name` (an object member,
`[A-Za-z_][A-Za-z0-9_]{0,63}`) or `[n]` (an array element, `0`..`9999`, no leading zeros). The whole string is replaced
by a copy of the JSON value at that path in the referenced step's `result` (any type: string, number, object, array),
resolved right before the step runs and before its own param parsing. Examples: `"$floor.guid"`,
`"$dup.objects[0].guid"`, `"$comp.component.guid"`, `"$t.triggerName"`, `"$graph[0].actions[1].index"`,
`"$obj.transform.localPosition"`. A string starting with `$$` is a literal with one `$` removed (`"$$5"` → `"$5"`); any
other string starting with `$` that is not a valid reference is refused up front. At run time a reference to a step that
failed or whose result lacks the path fails **that step** with `INVALID_PARAMS` (it is not started).

**Execution.** The batch is one turn of the write queue: no other write call interleaves. Steps run in order on the main
thread. A write step runs exactly like a direct call inside that turn: guard evaluation (editor state, test mod, writes
enabled, publish block), Workshop id hygiene, its body with its own time limit (its normal limit, e.g. 15 s for edit
tools, capped at the batch's deadline minus 1 s) and its own commit point; each edit is its **own undo step** (undo steps
are not merged). Read steps run synchronously. The batch commits when its first step commits. Before each step: if the
caller gave up before anything was committed the batch stops (`cancelled`); with less than 4 s of the batch's limit left
the step is **not started** and the batch returns the partial result (`timeBudget`). A step that does not finish within
its limit + 2 s aborts the batch (`stepDidNotFinish`).

**No rollback.** "Atomic where possible" in the plan cannot be met: object creation is not undoable in the editor, so a
failed batch cannot be rolled back. With `stopOnError` the batch stops at the first failed step and `results` lists
exactly what was applied; revert by hand with `delete_object` or one `undo` per step. Nothing is saved unless a
`save_mod` step ran.

```json
{"completed": 2, "attempted": 3, "total": 5, "failedStep": 2, "stoppedEarly": true, "stopReason": "stepFailed",
 "results": [
   {"index": 0, "id": "floor", "tool": "create_object", "ok": true, "result": {"guid": "9b1e...", "...": "..."}},
   {"index": 1, "id": "spawn", "tool": "create_object", "ok": true, "result": {"guid": "41c0...", "...": "..."}},
   {"index": 2, "tool": "set_property", "ok": false, "error": {"code": "INVALID_PARAMS", "message": "..."}}],
 "note": "Stopped at the first failed step (steps[2], stopOnError); steps[3] and later were not run. There is no rollback: ..."}
```

- `completed`: steps that succeeded; `attempted`: steps with a row in `results` (started, or failed while resolving
  references); `total`: steps in the request; `failedStep`: index of the first failed step (absent when none failed);
  `stoppedEarly`: not every step was attempted; `stopReason` (only then, or for `stepDidNotFinish`): `stepFailed`,
  `timeBudget`, `cancelled`, `stepDidNotFinish`, `internalError`.
- `results[i]`: `index`, `id` (when given), `tool`, `ok`, and `result` (the tool's normal result) or `error`
  (`{code, message, data?}`, the tool's normal error).
- Errors of the call itself: guard codes, `INVALID_PARAMS` (up-front validation), `TIMEOUT` (queued too long, before any
  step). Step failures are reported in `results`, not as a call error.

### End-to-end example

```text
1. create_test_mod {"name": "PB_TEST_E2E", "type": "Minigame"}
2. run_batch {"steps": [
     {"id": "floor", "tool": "create_object", "params": {"kind": "cube", "name": "Floor",
        "position": {"x": 0, "y": -0.5, "z": 0}, "scale": {"x": 20, "y": 1, "z": 20}}},
     {"id": "spawn", "tool": "create_object", "params": {"kind": "playerSpawn", "name": "Spawn",
        "position": {"x": 0, "y": 0.1, "z": 0}}},
     {"id": "hazard", "tool": "create_object", "params": {"kind": "trigger", "name": "Hazard",
        "position": {"x": 4, "y": 0.5, "z": 0}}},
     {"tool": "set_property", "params": {"guid": "$hazard.guid", "component": "Trigger",
        "property": "TriggerOnEnter", "value": true}},
     {"tool": "add_action", "params": {"guid": "$hazard.guid", "trigger": "OnEnterActions", "type": "ChangeHealth",
        "parameters": {"m_operation": "Subtract", "m_value": 1}}},
     {"id": "marker", "tool": "create_object", "params": {"kind": "sphere", "name": "Marker",
        "parentGuid": "$floor.guid", "position": {"x": 0, "y": 1, "z": 0}}},
     {"tool": "get_hierarchy"}]}
   -> completed 7; results[0].result.guid is the floor's guid, referenced by steps[5] (`parentGuid`), and results[2].result.guid by steps[3] and steps[4]
3. validate_mod -> valid: true (warnings: empty description, no preview image)
4. save_mod
5. start_playtest {"players": 4, "aiOnly": true} -> playing: true, saved: true
6. get_status (playtest.running, playtest.minigame.round) / get_log {"sinceId": <nextId before step 5>} / screenshot
7. stop_playtest -> stopped: true, state "Editing"
```

---

## Safety

### Config switches (`BepInEx\config\com.pummelbridge.plugin.cfg`)

| Key | Default | Effect |
|---|---|---|
| `[General] PluginEnabled` | `true` | Read at game start. `false`: the plugin does nothing at all (no server, no patches, one log line). Use this to publish by hand. |
| `[Safety] EnableWrites` | `true` | Read on every write call. `false`: every write tool returns `WRITES_DISABLED`. Does not affect the publish block. |
| `[Server] Enabled` | `true` | `false`: no RPC server; the publish block stays active. |
| `[Server] Port` | `38917` | Read at game start. If the port cannot be bound (already in use), one Error is logged and the bridge stays idle: no dispatcher, frame poller, save watch or undo observer runs and no tool is served. The Workshop publish block stays active. |

### Token file

`BepInEx\config\PummelBridge.token` is written to a temp file in the same folder and then moved into place (missing
target) or swapped in with `File.Replace` (malformed target), so a reader never sees a half-written token. Two
processes starting at once converge on one token: the loser of the move re-reads the file and uses the winner's token.
**Limitation:** the file's ACL is not restricted. It inherits the permissions of the game's `BepInEx\config` folder
(on Windows normally the current user plus administrators/SYSTEM, but on a game library on another drive it may also
include "Users"). Restricting it to the current user needs `System.Security.AccessControl`, which is not reliable on
the game's Mono/net472 runtime without extra dependencies, so the plugin does not try. The token is a protection
against other web pages and processes that cannot read that folder; treat any local user who can read the folder as
trusted, or tighten the folder's ACL by hand.

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
   `set_object`, `set_transform`, `add_component`, `remove_component`, `set_property`, `set_properties`, `undo`,
   `add_action`, `remove_action`, `move_action`, `set_action`, `add_trigger`, `remove_trigger`, `set_trigger`,
   `start_playtest`, `run_batch`, and every write step inside a batch; `stop_playtest` with the play-mode option):
   a mod is open (`NO_MOD_OPEN`) and loaded (`BUSY_LOADING`); its Meta name **and**
   folder name pass the rule (`GUARD_NOT_TEST_MOD`, message names the mod); state is `Editing` (`INVALID_STATE`;
   every state other than `Editing`, i.e. also `EnteringPlayMode` and `ExitingPlayMode`, is refused). Only
   `stop_playtest` is declared `allowPlayMode`: it skips this last check, all other checks apply to it unchanged.
   Kind **Lifecycle** (`create_test_mod`, `open_test_mod`): the `name` param passes the rule; if any mod is open it
   is a test mod, else `GUARD_NOT_TEST_MOD` ("close <name> by hand first"); state is `Editing`.

Workshop id hygiene: whenever the guard passes with a loaded test mod open, before every bridge save and after
create/open, an in-memory `workshopItem.publishedFileId` other than 256 (`ModWorkshopItem.DefaultWorkshopID`) is
reset to 256 with a warning in the BepInEx and Unity logs. This is the only workshop-item field the bridge writes.

### Workshop publish block

While the plugin is enabled, Harmony prefixes installed in `Awake` (before the server starts; never removed by the plugin) skip
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
- Checked live: before every write tool (guard step 2) and on every `get_status`, the plugin reads Harmony's patch
  info of each required target (about 14 lookups, no per-frame work). If one no longer carries the plugin's prefix
  (another plugin called `Harmony.UnpatchAll()` / `UnpatchID`, or unpatched it), the block is incomplete from then on:
  one Error per lost target in the BepInEx log, `publishBlock.complete: false` with the target in `missing`, every
  write tool `PUBLISH_BLOCK_INCOMPLETE`. The plugin does **not** re-patch at runtime; restart the game (and remove the
  plugin that unpatched it). Optional targets are not re-checked.
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

No input injection for playtests (a human or AI-only mode drives them); no pause/resume tool; no editing of mod
settings other than the player-settings triggers (description, preview image, rounds, end conditions are set by hand);
no editing of board connections (`BoardNodeConnection[]`), so a Board mod cannot be made validator-clean through the
bridge yet; no redo (the editor has none); no copy/paste/duplicate of
actions (the Action Editor has none); no editing of Set Connection Active connection mappings or of the Display
Popup's hidden timer fields; no prefab asset editing context switch (open/close a prefab by hand), no layer/tag or per-component enable
editing, no asset creation, import or removal. No delete, rename or close of mods. No Workshop functionality of any
kind; publishing is blocked (see [Safety](#safety)).
