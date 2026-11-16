# pummel-party-mcp HTTP API (api v2, plugin 0.8.0)

The BepInEx plugin `PummelPartyMcp.Plugin` runs inside Pummel Party and exposes the mod editor's state on a
local HTTP endpoint. api v2 adds the guard layer, the Workshop publish block and four mod lifecycle
tools (`list_mods`, `create_test_mod`, `open_test_mod`, `save_mod`). Increment 2 (still api v2, all changes are
additive) adds object, transform, component and property editing plus `undo`; see [Editing](#editing). Increment 3
(still api v2, additive) adds editing of the Action Editor model (triggers and their action lists); see
[Action editing](#action-editing). Increment 4 (still api v2, additive) adds `validate_mod`, `start_playtest`,
`stop_playtest` and `run_batch`, structured error details (`error.data`) and play-mode rules for every tool; see
[Validation, playtest and batch](#validation-playtest-and-batch). Plugin 0.6.0 (still api v2, additive) adds playtest
visibility: the read tools `get_playtest_state` and `get_playtest_events`, a small summary in `get_status.playtest`, and the
capability `"playtestState"`; see [Playtest visibility](#playtest-visibility). Plugin 0.7.0 (still api v2, additive) adds the game-version gate (`GAME_VERSION_UNTESTED`, `[Safety] AcceptedGameVersion`, `gameVersionCheck`), the read tool `self_test`, and enforces the editor inspector's visibility rules in `set_property` / `set_properties`; see [Game-version gate](#game-version-gate), [`self_test`](#self_test-read) and [Inspector visibility rules](#inspector-visibility-rules-plugin-070). Plugin 0.8.0 (still api v2, additive) adds mod settings: `list_mod_settings`, `get_mod_settings`, `set_mod_settings` and `capture_preview_image`, the capability `"settings"`, and the settings rule tables; see [Mod settings](#mod-settings). Plugin 0.8.0 also adds the asset tools (`get_asset`, `list_asset_rules`, `import_asset`, `set_import_settings`, `create_material`, `set_material`, `remove_asset`), the capability `"assets"` and the `[Assets]` config section; see [Assets](#assets-import-import-settings-materials-removal). Plugin 0.8.0 also adds the prefab tools (`create_prefab`, `open_prefab`, `close_prefab`), the capability `"prefabs"` and the write protection around every `.pfab` the editor writes (also in `save_mod`); see [Prefabs](#prefabs-create-open-close-the-pfab-write-protection). Write tools only ever act on `MCP_` mods; see
[Safety](#safety). The plugin is active only while the Mod Editor is open: the endpoint below exists only then; see
[Lifecycle](#lifecycle-active-only-while-the-mod-editor-is-open).

## Transport

| Item | Value |
|---|---|
| Endpoint | `POST http://127.0.0.1:<port>/rpc` (only this path and verb exist) |
| Bind address | `127.0.0.1` only (TCP listener on the loopback interface) |
| Port | BepInEx config `BepInEx\config\com.pummelpartymcp.plugin.cfg`, section `[Server]`, `Port` (default `38917`, range 1024-65535) |
| Enable/disable | `[General] PluginEnabled` (master switch) and `[Server] Enabled` (server only); see [Safety](#safety) |
| Lifetime | listens only while the Mod Editor is open (a new listener per editor session); otherwise the connection is refused. See [Lifecycle](#lifecycle-active-only-while-the-mod-editor-is-open) |
| Token file | `BepInEx\config\PummelPartyMcp.token`: 64 lowercase hex chars (32 CSPRNG bytes), created the first time the Mod Editor is opened, reused afterwards (also across editor sessions and game restarts), regenerated if malformed; always written via a temp file plus atomic move/replace in the same folder (see [Safety](#token-file)) |
| Connection | one request per connection; every response has `Connection: close` |
| Request body | UTF-8 JSON, `Content-Type: application/json` (parameters such as `; charset=utf-8` allowed), `Content-Length` required, max 1 MB (1 048 576 bytes); chunked bodies are refused |
| Concurrency | up to 16 requests in flight; more get HTTP 503 |

Required headers:

```
Host: 127.0.0.1:<port>          (or localhost:<port>)
X-PummelPartyMcp-Token: <token>
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
| `EDITOR_NOT_OPEN` | `EditorSystem.Instance` is null (main menu, in a normal game, ...), or the Mod Editor is opening or closing; the call was not run and nothing was changed |
| `EDITOR_CLOSED_DURING_CALL` | the Mod Editor was closed while the call was running and its own result never came: a write may have partly applied (a read changed nothing). Do not retry blindly: open the Mod Editor and check `get_status` / `get_object` first. See [Lifecycle](#lifecycle-active-only-while-the-mod-editor-is-open) |
| `NO_MOD_OPEN` | editor open, no mod loaded |
| `BUSY_LOADING` | the editor is loading a mod (`IsLoading`) or the mod has not finished loading (`!IsLoaded`) |
| `NOT_FOUND` | object / component not found |
| `TIMEOUT` | the call did not finish within its time limit (10 s; see [Threading](#threading) for the write-tool limits and the exact semantics). For write tools, a `TIMEOUT` whose message says "nothing was changed" / "safe to retry" was refused or cancelled **before** its commit point: nothing was applied and it never runs later. A write call that has passed its commit point never gets that `TIMEOUT` (see [Timeouts and the commit point](#timeouts-and-the-commit-point)) |
| `INTERNAL` | unexpected exception, or a game member the plugin needs is missing (`member X.Y not found — game version changed?`) |
| `WRITES_DISABLED` | write tool called while `[Safety] EnableWrites = false` |
| `PUBLISH_BLOCK_INCOMPLETE` | write tool called while the Workshop publish block is not complete (`get_status.publishBlock`) |
| `GUARD_NOT_TEST_MOD` | the open mod (open-mod tools) or the target / open mod (lifecycle tools) is not a `MCP_` mod; the message names it |
| `INVALID_STATE` | editor state forbids the call: play mode (also the transitional states `EnteringPlayMode` / `ExitingPlayMode`), no playtest running (`get_playtest_state`), a mod that fails the validator (`start_playtest`), unsaved changes, a prefab being edited, an open editor dialog, a broken editor mod list, an object outside the current editing context, the left mouse button held in the game window, the open mod changed while an edit was pending (the message says what to do) |
| `ALREADY_EXISTS` | `create_test_mod`: a folder or a mod (Meta name) with that name exists, case-insensitive; `import_asset`: no free name (`name`, `name (1)` ... `(20)` all taken); `create_prefab`: the file the editor would write already exists on disk, or no free name up to `_99`; `create_material`: the file the editor would create (or the requested name) already exists |
| `ASSET_IN_USE` | `remove_asset` (references, or the preview image, or the mod settings could not be read) and `set_import_settings` (a compression change would break references): `error.data` lists them |
| `IMPORT_FAILED` | `import_asset`: the game's importer rejected the copied file; the copy was removed (`error.data.cleanedUp`), the source was not touched |
| `LINK_REFUSED` | a path the call would write or delete runs through a junction, symlink or mount point inside the sandbox mod (including the mod folder itself), or is a hard-linked file (or its link count cannot be determined); nothing was written or deleted, the message names the path. See [Links inside a sandbox mod](#links-inside-a-sandbox-mod-every-write-and-delete) |
| `GAME_VERSION_UNTESTED` | write tool called while the running game version is not in the plugin's tested list and not accepted by `[Safety] AcceptedGameVersion` (or the version could not be read); read tools are unaffected. See [Game-version gate](#game-version-gate) |

Edit tools additionally use `NOT_FOUND` (object / component / the edited object was deleted meanwhile),
`INVALID_PARAMS` (bad value, refused property, a rule the editor's UI enforces) and `TIMEOUT` (the editor did not
take its undo snapshot in time, or the call waited too long in the write queue; always before the edit was applied,
so safe to retry).

Values in results: enums as their names (flags as `"A, B"`), `Vector2/3/4` and `Quaternion` as `{x,y,z[,w]}`,
`Color` as `{r,g,b,a}` (0-1 floats), `Color32` as 0-255 ints, GUIDs as lowercase `8-4-4-4-12` strings, NaN/Infinity
as strings. Asset or object references are objects (see "References").

### Threading

`ping`, `get_log` (without `includeEditor`) and `get_playtest_events` are answered on the HTTP thread and work even while the game's
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

Answered on the HTTP thread (also while the main thread is busy). No params. Like every tool it needs the Mod Editor open:
with it closed nothing listens.

```json
{"pong": true, "pluginVersion": "0.8.0", "gameVersion": "1.14.1k", "unityVersion": "2021.3.45f2",
 "apiVersion": 2, "sessionId": "3b0f2c...e1", "capabilities": ["read", "write", "edit", "actions", "playtest", "batch", "playtestState", "selfTest", "gameVersionGate", "settings", "assets", "prefabs"],
 "gameVersionCheck": {"running": "1.14.1k", "tested": ["1.14.1k"], "accepted": false, "writesAllowed": true}}
```

`readOnlyBuild` was removed in api v2. `capabilities` lists what this build implements (`edit` = the object,
component, property and undo tools of increment 2; `actions` = the action/trigger tools of increment 3; `playtest` =
`validate_mod`, `start_playtest`, `stop_playtest`; `batch` = `run_batch`; `playtestState` = `get_playtest_state`,
`get_playtest_events`; `selfTest` = `self_test`; `gameVersionGate` = the version gate); `gameVersionCheck` is the gate status as last loaded from the config (`ping` does not reload the file; `get_status` does). Whether
writes are currently allowed is in `get_status`
(`writesEnabled`, `publishBlock.complete`).

`sessionId` is random per game start and stays the same across editor sessions; a change means the game restarted (log ids
restart at 1).

### `get_status`

No params. Never fails because of the editor's own state, and answers also when the publish block is incomplete. Exceptions:
while the Mod Editor is closing it returns `EDITOR_NOT_OPEN`; when the plugin cannot read the editor at all (game update) it
returns `INTERNAL`.

```json
{"editorOpen": true, "editorState": "Editing", "isLoading": false, "modOpen": true,
 "modName": "MCP_Arena", "modType": "Minigame", "modLoaded": true, "isDirty": true,
 "hasUnsavedEdits": false, "unsavedEditsSource": "tracker",
 "undoStackLength": 1, "prefabContext": null, "playMode": false,
 "activeScene": "ModEditorScene",
 "playtest": {"state": "Editing", "running": false, "startedAt": null, "playModeSince": null, "endedAt": null,
              "playSeconds": null, "startedByPlugin": false, "players": "4 Players", "playerCount": 4, "teams": null,
              "aiOnly": false, "eventCount": 0, "lastEventId": 0, "alivePlayers": null},
 "capabilities": ["read", "write", "edit", "actions", "playtest", "batch", "playtestState", "selfTest", "gameVersionGate", "settings", "assets", "prefabs"], "writesEnabled": true,
 "gameVersionCheck": {"running": "1.14.1k", "tested": ["1.14.1k"], "accepted": false, "writesAllowed": true},
 "publishBlock": {"active": true, "complete": true, "patchedCount": 59, "missing": []},
 "activeWrite": null,
 "lifecycle": {"state": "active", "armedUtc": "2026-11-16T12:00:00.0000000Z", "sessionsThisRun": 1}}
```

- `writesEnabled`: current value of `[Safety] EnableWrites`.
- `gameVersionCheck`: `{running, tested[], accepted, writesAllowed}` (+ `readable: false` when the version string could not be read, `forcedUntested: true` when `ForceGameVersionUntested` is on). `accepted` = `[Safety] AcceptedGameVersion` equals `running` exactly; `writesAllowed` = running is tested or accepted. See [Game-version gate](#game-version-gate).
- `publishBlock`: `active` = at least one blocking patch installed; `complete` = every required target patched
  and no patch failed (write tools need this); `patchedCount` = methods patched (59 expected for game build
  2025-10-16: 10 game methods, 25 `SteamUGC`, 24 `SteamRemoteStorage`; the count when the Mod Editor opened); `missing` =
  `"<target>: <reason>"` for each target that is missing (required) or whose patch failed. The status is **live**:
  every `get_status` and every write call re-checks (Harmony patch info) that each required target still carries the
  plugin's prefix; if one was removed after the editor opened (e.g. another plugin unpatched it), `complete` becomes false,
  `missing` lists it as `"<target>: blocking prefix removed after the Mod Editor opened"`, and it stays so until the Mod Editor is
  closed and opened again (a new editor session installs the block again). `readOnlyBuild` was removed in api v2.
- `lifecycle` (additive): `state` `active` (everything started) or `failed` (the publish block, the token or
  the port failed when the editor opened: only `ping`, `get_status`, `get_log` and `self_test` answer, or nothing does);
  `armedUtc` = when the current editor session started (UTC); `sessionsThisRun` = editor sessions since the game started.
  See [Lifecycle](#lifecycle-active-only-while-the-mod-editor-is-open).

- `editorState`: `Editing | EnteringPlayMode | PlayMode | ExitingPlayMode` (null when the editor is closed).
- `playMode`: `editorState != "Editing"` (a playtest is starting, running or ending).
- `playtest` (increment 4; null while the editor is closed): `state` (= `editorState`), `running` (= `playMode`);
  `startedAt` / `playModeSince` / `endedAt` (UTC ISO times seen by the plugin's per-frame observer for the current or
  last playtest: left Editing / reached PlayMode / back in Editing; reset when the editor closes); `playSeconds`
  (seconds in PlayMode, frozen at the end); `startedByPlugin` (that playtest was started by `start_playtest`);
  `players`, `playerCount`, `teams`, `aiOnly`: the toolbar's current dropdown / checkbox (see `start_playtest`).
  While a playtest runs (`running: true`) also: `paused` (`Time.timeScale == 0`, the toolbar's Pause button),
  `minigame` `{state, round, rounds}` (`GameManager.Minigame`: `MinigameControllerState` name, 1-based round as the
  round UI shows it, rounds) and `board` `{state}` (`GameManager.Board.BoardState`) when those controllers exist.
  Always (also after the playtest, until the next one starts): `eventCount` (entries in the event feed), `lastEventId`
  (id of the newest entry, 0 = none yet; keeps counting across playtests) and `alivePlayers` (players not dead at the
  last per-frame sample, null before the first sample of the current playtest); the data itself is in
  `get_playtest_state` / `get_playtest_events` ([Playtest visibility](#playtest-visibility)).
- `isDirty`: the editor's own dirty flag of the current undo context (scene, or the open prefab), read from fields.
  It is set by every undo push, including the initial snapshot the editor pushes on the first frame after any load,
  so it is `true` for an untouched mod (confirmed in-game). `null` if the field layout changed or no mod is open.
- `hasUnsavedEdits`: the plugin's view, used by `create_test_mod`/`open_test_mod` (see [Unsaved edits](#unsaved-edits)).
  `null` when no mod is open.
- `unsavedEditsSource`: `"tracker"` (the plugin's own tracking) or `"editorFlag"` (fallback to `isDirty`: the mod was
  already open with a long undo history when the plugin first saw it, or the save watch could not be installed);
  `null` when no mod is open.
- `prefabContext`: `{"name": "Coin", "guid": "..."}` while a prefab asset is being edited, else null.
- `activeWrite`: the write tool that currently holds the plugin's write queue, `{"tool": "set_property", "seconds": 1.2}`
  (`seconds` since it started), or `null` when no write call is running. Visibility only: later write calls wait behind it.
  A write call that is still running more than 14 s (dispatcher commit grace 4 s + stuck grace 10 s) after its caller's
  deadline is logged once as an error in the BepInEx log; it is not released.

### `get_open_mod`

No params. Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`, `INVALID_STATE` while a playtest is starting,
running or ending (see [Play-mode behaviour](#play-mode-behaviour-of-every-tool)).

```json
{
  "name": "MCP_Arena", "folderName": "MCP_Arena", "type": "Minigame", "className": "MinigameMod",
  "usesHierarchy": true, "usesLighting": true, "usesPostProcessing": true,
  "metadata": {"MetaDataVersion": 1, "ModVersion": 1, "ModCreatedVersion": 1, "Name": "MCP_Arena",
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
- `settingsDetail` points to `get_mod_settings`, which returns every setting (also the ones this generic dump lacks: description, preview image, music and ambience as references, lighting and screen-effect values, board details) in the shapes `set_mod_settings` accepts; see [Mod settings](#mod-settings).
- From the workshop item only `publishedFileId` (as a **string**, Steam ids are 64-bit) and `isPublished`
  (`publishedFileId != 256`) are exposed. **`"256"` is the "never published" sentinel**
  (`ModWorkshopItem.DefaultWorkshopID`), not a Workshop item: the game also uses it at runtime as the local id of every
  unpublished mod (`MinigameDefinition.publishedFileId`, the `PummelModCache/256` folder, `GetModWithWorkshopID(256)`).
  Do not describe it as a Workshop item or as a "minigame id"; every `MCP_` mod has it (the plugin forces it).

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
While a prefab is edited, the synthetic node has exactly one child: the open prefab's root object (`childCount` 1, or
0 when `includeInactive` is false and that object is inactive). The other prefabs' objects, which the editor keeps under
the same internal root, are not listed (the editor's hierarchy does not show them either); `rootGuid` still reaches them.

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
  `shown: false` marks properties the editor's inspector never draws; `inspector` summarizes the property's inspector rules (visibility condition, restricted choices, clamp); see [Inspector visibility rules](#inspector-visibility-rules-plugin-070).
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
| `location` | `external` \| `internal` | - | `external` = the mod's own custom assets (the editor's "Custom" folder; rows also carry `sizeBytes` and `fileExists`), `internal` = built-in |
| `search` | string | - | case-insensitive substring match on the asset name; whitespace-separated terms must all match |
| `limit` | int | 100 | page size, 1..500 |
| `offset` | int | 0 | number of matching assets to skip, >= 0 |

Errors: `INVALID_PARAMS` (bad `limit`/`offset`), `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`. AudioClip rows also
carry `lengthSeconds` (3 decimals, as `get_asset`) when the clip is already loaded; a built-in clip that is not loaded
has no such field (nothing is loaded or decoded for the listing). During a playtest the result additionally has `"playMode": true` (the editor mod's asset list, unchanged by the playtest).

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

Source: a 2000-entry ring buffer fed by `Application.logMessageReceivedThreaded` while the Mod Editor is open (all Unity
log output of the game, editor and playtests). It is kept across editor sessions; lines from outside the Mod Editor are
only in `BepInEx\LogOutput.log`. Without `sinceId` the newest `max` entries are returned.

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

Params: `maxWidth` (int 64-7680, default 1280), `camera` (optional, see below). Without `camera` it works in
every editor state and captures whatever the view shows (also a playtest). Exceptions: while the Mod Editor is opening or
closing it returns `EDITOR_NOT_OPEN` (a capture never returns a picture taken after the editor closed); with an incomplete
publish block it returns `PUBLISH_BLOCK_INCOMPLETE`.

```json
{"pngBase64": "iVBORw0KGgoAAAANSUhEUgAA...", "width": 1280, "height": 720}
```

`camera` moves the Mod Editor's scene-view camera before the capture, so an agent can look at the scene from a chosen
angle. Exactly one of these forms (anything else is `INVALID_PARAMS` with a message saying why):

| Form | Meaning |
|---|---|
| `{"position": {x,y,z}, "lookAt": {x,y,z}}` | camera at `position`, aimed at `lookAt` (same point is refused; straight up or down works) |
| `{"position": {x,y,z}, "rotation": {x,y}}` | `rotation.x` is pitch and `rotation.y` yaw in degrees (positive pitch looks down; pitch is clamped to -90..90, yaw wrapped to 0..360; there is no roll) |
| `{"focus": "<object guid>"}` | frames the object like the editor's F key (bounds of its renderers along the camera's current direction; an object without renderers is framed 2 units in front of its position); optionally with `rotation` to choose the viewing direction first. `focus` with `position` or `lookAt` is refused |

The reply then also has `camera: {position: {x,y,z}, rotation: {x,y}}`, the values actually applied (rounded to 3
decimals). `camera` needs the Mod Editor in edit mode with a mod open (`EDITOR_NOT_OPEN`, `NO_MOD_OPEN`,
`BUSY_LOADING`; `INVALID_STATE` during a playtest, with the reason in the message; `NOT_FOUND` for an unknown `focus`
guid). It is a view change only and not a write tool: it works for any open mod (the `MCP_` guard is for writes to mod
data), changes nothing in the mod, saves nothing, adds no undo step and does not set the unsaved-edits flag. The camera
stays where it was put, with one exception: `open_prefab` re-frames the camera on the prefab and `close_prefab` restores
the view from before the prefab was opened (`EditorSystem` prefab context switch), so a camera set while a prefab is
open is lost at `close_prefab`. `focus` also needs an object that is active in the current context: a prefab's objects
need `open_prefab` first, scene objects need `close_prefab` first, and a switched-off object is refused with
`INVALID_STATE` (an inactive object has no renderers, so the camera would frame empty space). The reported
`rotation` is the pitch/yaw passed to the editor camera (for `focus` without `rotation`: the camera's own current pitch
and yaw, unclamped), not recomputed Euler angles. A capture that does not finish after the camera moved answers
`TIMEOUT` with a message saying the camera was moved; call `screenshot` again. How it is applied: `EditorCamera` (reached as `EditorSystem.SceneViewWidget.CurEditorCamera`)
rebuilds its rotation from its own pitch/yaw every frame and smooths its position toward a private target, so the plugin
sets both the public setters (`SetRotation`, `SetTargetPosition`, `Focus`) and the transform, and zeroes the private
velocity. It then waits two rendered frames and the end of the frame before capturing.

Concurrent `screenshot` calls are captured one at a time in arrival order: a call's `camera` is applied only when its
turn comes (after the previous capture finished), so a second call can never move the camera between another call's
move and its capture. A call whose caller already timed out is skipped without moving the camera. A capture that does
not finish within 5 s (frames stalled) gives up its turn and answers `TIMEOUT`.

Captured on the main thread after `WaitForEndOfFrame` with `ScreenCapture.CaptureScreenshotAsTexture()`, downscaled
(keeping aspect ratio) when wider than `maxWidth`, PNG-encoded; all temporary textures are destroyed. The capture
is not written to disk.

### `list_mods`

No params. Errors: `EDITOR_NOT_OPEN`. Read-only.

```json
[{"name": "My Real Mod", "folderName": "My Real Mod", "type": "Minigame", "isTestMod": false, "isOpen": false},
 {"name": "MCP_Sandbox", "folderName": "MCP_Sandbox", "type": "Minigame", "isTestMod": true, "isOpen": true}]
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
| `discardUnsaved` | bool | false | additive extra (the MCP server passes it through when the agent gives it): allow dropping unsaved changes of the open test mod |

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
{"name": "MCP_Arena", "folderName": "MCP_Arena", "type": "Minigame", "publishedFileId": "256"}
```

`publishedFileId` `"256"` = never published (the game's local id for unpublished mods; not a Workshop item).

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
{"name": "MCP_Arena", "folderName": "MCP_Arena", "type": "Minigame", "publishedFileId": "256", "alreadyOpen": false}
```

`publishedFileId` `"256"` = never published (the game's local id for unpublished mods; not a Workshop item).

Leaving the open mod (create and open) gives `INVALID_STATE` unless:
- no editor dialog is open (`EditorPopupManager.CurrentPopup == null`);
- no prefab is being edited (leaving the prefab context would save that prefab);
- the open mod has no unsaved edits (`get_status.hasUnsavedEdits` is false), or `discardUnsaved: true`. This mirrors
  the editor's own "unsaved changes" popup, but with the plugin's tracking (see [Unsaved edits](#unsaved-edits))
  instead of the editor's flag, which is already true for an untouched, freshly opened mod. An untouched mod can now
  be left without `save_mod`/`discardUnsaved`. The plugin never auto-saves. The error message names the first
  reason (e.g. `scene undo stack changed`, `plugin: set_property`).

### `save_mod` (write, open mod)

No params. The editor's own save (`EditorSystem.SaveCurrentMod`: all mod files, `Data/Preview.jpg`, clears the
current dirty flag), after the guard (loaded test mod, not loading, edit mode) and the Workshop id hygiene. If the
mod's Meta name is missing from the editor's mod list, the entry is added first (otherwise the editor's
`UpdateLoadedModLastModifiedTime` would insert a broken nameless entry).
Saving while a prefab is being edited is allowed, as with Ctrl+S: the scene file stores each object's own active
flag, so the deactivated scene root does not change it, and the prefab is saved with the other assets. In that
case the scene's dirty flag stays set and `Preview.jpg` shows the prefab view.

Prefab files are protected: `PummelMod.Save` rewrites **every** prefab of the mod in place (`PummelPrefab.Save` opens the
`.pfab` with `FileMode.Create` and serialises straight into it), and a truncated `.pfab` makes the whole mod fail to load.
So before the save every prefab is serialised in memory (the dry run); one that throws refuses the call with
`INVALID_STATE` naming it, **nothing is saved**. A safety copy of each prefab's `.pfab` and `.pmeta` is kept in memory; after
the save each file is verified (header, length equal to the dry run, `.pmeta` valid JSON) and any failing file is put back
(written next to the target and swapped in). If a file failed, or the editor's save itself threw, the call fails with
`INTERNAL`, `error.data.prefabs` lists every prefab's outcome, the mod does not count as saved (`hasUnsavedEdits` stays true) and
the message says which files were restored. The reply of a good save adds `prefabsVerified` (number of prefabs checked). The
scene file (`MainScene.scene`) and the other data files are not covered by this protection: the game serialises the scene into
a `MemoryStream` first and then writes the bytes (`ModDataReaderWriter.SaveScene`), so a serializer exception cannot truncate
it; a full disk during that final write could (not changed in this increment). Saves made by the user (Ctrl+S, autosave, leaving
a prefab in the UI) are not protected by the plugin.

```json
{"saved": true, "name": "MCP_Arena", "isDirty": false, "hasUnsavedEdits": false}
```

`isDirty`: dirty flag of the current undo context right after the save (same source as `get_status.isDirty`).
`hasUnsavedEdits`: the plugin's view right after the save (false; a save is a clean point for the whole mod).

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
  addable): the editor's own tooltip text where the editor has one, else the plugin's.
- `allowsMultiple`: no `[DisallowMultipleComponent]` (more than one per object possible). `supportsChildren`:
  false for types whose object cannot have children (Prefab Instance, Board Space, Item, Text). `modTypes`: the
  type's mod-type flags (empty for RigidBody and Hittable: never offered). `customInspector`: the type has a hand-written inspector (its rules are in [Inspector visibility rules](#inspector-visibility-rules-plugin-070)).

### `self_test` (read)

No params. Changes nothing. Answers also when the publish block is incomplete. After a game update it re-resolves every game member the plugin looks up by name (fields, properties, methods, the enum values and properties the inspector rules name; one central registry, `Core/RequiredMembers.cs`, the same list the offline drift test checks), re-checks the publish block and compares the custom-inspector components with the audited snapshot.

```json
{"ok": true, "gameVersion": "1.14.1k", "gameVersionCheck": {"running": "1.14.1k", "tested": ["1.14.1k"], "accepted": false, "writesAllowed": true},
 "membersChecked": 439, "missingMembers": [],
 "publishBlock": {"active": true, "complete": true, "patchedCount": 59, "missing": [], "targetsChecked": 64, "unresolvedTargets": []},
 "inspectorAudit": {"customInspectorComponents": 21, "unauditedProperties": []},
 "notes": ["self_test resolves members by name only (existence, type, signature); it cannot tell whether a member still behaves the same."]}
```

`ok` is true when no member is missing, the publish block is complete and every target resolves, the audit finds no new custom inspector or property, and the version is readable. An untested version does not make `ok` false (that is what the test is for): `notes` then says how to accept it. Use the result as evidence before setting `[Safety] AcceptedGameVersion`.

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
  prefab's root object and its children. Objects of the other context give `INVALID_STATE` ("call close_prefab first (or
  use Return to Hierarchy in the editor)" / "open that prefab in the editor"); `open_prefab` and `close_prefab` change the context. Children of
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
  plugin has no redo either.
- Not undoable in the editor itself (so not via the plugin): object creation (Ctrl+Z after creating only re-selects
  the previously selected object), and the destruction of generated children when a Prefab Instance / Board Space /
  Item / Text component is removed.

### Unsaved edits

`get_status.hasUnsavedEdits` (and the create/open rule) reports whether the open mod changed since it was loaded or
last saved. The plugin reads every undo stack of the open mod (the scene's and each prefab's) once per frame and
latches "unsaved" when a stack changes, **except** for the editor's initial snapshot after a load (an empty stack
getting its first entry) and an undo that pops that single entry again; every plugin edit latches it as well. The
clean point moves at load completion and on every completed `EditorSystem.SaveCurrentMod` (Ctrl+S, File > Save, the
unsaved-changes popup's Save button, `save_mod`), which a Harmony **postfix** observes; the postfix only records that
a save happened. Consequences:

- An untouched, freshly opened or created mod reports `false`.
- Selecting objects in the editor counts as a change, because the editor records selection changes as undo steps.
- Undoing everything does not make the mod clean again (latched until the next save).
- Entering a playtest writes the mod files but does not count as a save (the editor does not clear its flag either).
- Fallback to the editor's flag (`unsavedEditsSource: "editorFlag"`) when the plugin first saw the mod with more
  than one undo entry on a stack (it was open before the plugin could watch it) until the next save, or when the save
  postfix could not be installed (a warning is logged when the Mod Editor opens).
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

Inactive parents: `AddGameObject` parents the new object first and then sets component fields that Unity only
initialises in `Awake`, which does not run below an inactive parent, so in the editor it throws after the object exists.
When `parentGuid` names an object that is inactive in the hierarchy (switched off itself or below a switched-off
object), the plugin therefore builds the object under the context root (top level of the scene, or the open prefab's
root object) and moves it under the requested parent in the same frame, keeping the local values (what a user does
by dragging it there); an unnamed `empty` object gets the default name it would have got under that parent. The reply
is the same as for an active parent. When the context root itself is inactive (the open prefab's root object switched
off), the call is refused with `INVALID_STATE` before anything is created (switch it on first). If creating the object
fails anyway after the editor built it, the plugin deselects and destroys the partly created object and the error says
so (`creating the object failed (...); the partly created object ('Prop') was removed again; nothing was created`).

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
the plugin selects the object first; the editor's copy clipboard (Ctrl+C/Ctrl+V) is replaced, as with Ctrl+D. The
clone (with all its children) is placed next to the original (same parent; for the open prefab's root object: below
it), keeps the name, is selected, and one `DuplicateGameObject` undo event is pushed (undo destroys the clone).
Every component of the clone and of its children gets a fresh guid (the `ModBehaviour` constructor assigns one and
the field is not serialized, so `Instantiate` does not copy it). The plugin checks this after every duplicate and,
should a guid ever collide, undoes the duplicate and returns `INTERNAL`.

```json
{"sourceGuid": "0b6d...", "guid": "77aa...", "objects": [{"guid": "77aa...", "name": "Floor", "active": true, "components": ["ModProp"],
  "childCount": 0, "children": [], "parentGuid": null, "siblingIndex": 5, "transform": {"...": "..."}}],
 "changed": true, "undoable": true, "undoStackLength": 6}
```

`guid`: the guid of the first (root) clone, `objects[0].guid`, as `create_object` returns its `guid` at the top level.
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

Both the plugin's replica of these rules and the editor's own check code are evaluated; either one blocking refuses.

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
- types the plugin cannot write yet: `ModComplexFloat`, `ModWeaponInfo`, `SimpleModHitAction`, `ModSimpleAnimation[]`,
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

Inspector-only clamps: some custom inspectors re-apply limits on every frame they draw a component; the plugin applies
the same limits when writing and lists the affected properties in `inspectorClamped`:
`ModAudioSource.volume` 0-1, `.spatialBlend` 0-1, `.stereoPanning` -1..1; `ModBoardSpace.SpawnRotation` 0-360,
`.SpawnDistance` 0-4; `ModFireWeapon.FireTime` >= 0.1; `ModItem.SecondsBetweenHeldAction` >= 0.1, `.Cooldown` >= 0.1;
`ModLight.spotAngle` 1-179 (the inspector slider shows 0-180, but Unity's `Light.spotAngle` stores 1-179; 200 was observed to become 179); `ModParticleSystem.arcSpread` 0-1; `ModSpawner.SpawnCount` 0-20;
`ModText.OutlineThickness` 0-1, `.ShadowOffsetX` / `.ShadowOffsetY` -1..1, `.ShadowSoftness` 0-1;
`ModParticleSystem.radiusThickness` 0-1, `.arc` 0-360 (drag limits), `ModParticleSystem.arcSpread` 0-1 (listed above), `ModTrigger.StayTriggerInterval` >= 0.1; `ModWater.CellSize` 0.1-10, `.WaveHeight` 0-1, `.WaveSpeed` 0-10.
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
 "applied": {"type": {"type": "InternalLight", "value": "Spot"}, "spotAngle": {"type": "Single", "value": 179.0},
             "color": {"type": "Color", "value": {"r": 1.0, "g": 0.5, "b": 0.0, "a": 1.0}}},
 "inspectorClamped": ["spotAngle"], "changed": true, "undoable": true, "undoStackLength": 13}
```

`applied`: each written property read back after the editor recorded the step, in `get_object`'s `{type, value}`
shape. Errors: guard codes, `INVALID_PARAMS`, `NOT_FOUND`, `INVALID_STATE`, `TIMEOUT`, `INTERNAL`.

#### Inspector visibility rules (plugin 0.7.0)

The editor draws a component in one of two ways. 21 component types have a hand-written inspector (`[ModCustomInspector]`;
`ModAINavigationInspector` exists in the game but is attached to nothing, so AI Navigation Node uses the generic path)
that decides which properties are drawn, when, with which choices and which limits; every other component is drawn by the
generic loop over its reflected properties. `set_property` / `set_properties` apply the same rules, so the plugin cannot
produce a value the editor's UI cannot produce (audit of game build 1.14.1k, from the decompiled inspectors).

- **Never drawn** (the custom inspector has no control for it, or draws something else under its label): refused with
  `INVALID_PARAMS`, e.g. `'startSize': the editor's inspector does not offer 'startSize' on Particle System (never drawn), so the editor UI cannot set it`.
- **Drawn only under a condition**: refused while the condition is false, naming the condition and the state after the
  call, e.g. `'spotAngle' is only shown by the editor's inspector while type is Spot (type is Point after this call); set the controlling property first (or in the same set_properties call)`.
  The condition is evaluated against the values **after** the call's other values, so
  `{"type": "Spot", "spotAngle": 40}` works in one call and `{"TriggerOnHit": true, "TriggerOnEnter": true}` is refused.
  A condition the plugin cannot evaluate (the controlling value is unreadable, e.g. a wrapper component that is not set up
  yet) never blocks the write.
- **Restricted choices**: an enum value the inspector's dropdown omits, or a flag its buttons cannot toggle, is refused:
  `'rolloffMode': the editor's inspector does not offer the value Custom (not offered: Custom)`,
  `'shapeType': the editor's inspector cannot choose Mesh; it offers only Sphere, Hemisphere, Box, BoxShell, BoxEdge, Cone, ConeVolume or Donut`.
- **Limits** the inspector re-applies while drawing (sliders, `Mathf.Clamp`, minimums) are clamped as before and listed in
  `inspectorClamped` (table above); they are also in the rules below.
- A property **without a rule** is always drawn. Properties of types the plugin cannot write anyway (triggers,
  `ModWeaponInfo`, `ModComplexFloat`, `SerializableGuid`, ...) are refused as listed above, before any rule.
- Components **without** a custom inspector (everything except the 21 below): the generic inspector honours only
  `[ModHideInInspector]` (refused, as before), `[ModHeader]` (a label and an optional separator), `[Multiline]` (the text
  box height) and the property's type. It has no control for `char`, `short`, `ushort`, `long`, `ulong`, `MinMaxCurve`,
  `Burst`, `SerializableGuid`, `BoardNodeConnection` and the simple-animation types, so such a property would be refused
  with `the editor's generic inspector draws no control for '<name>' (type ...)`; no generic component of 1.14.1k has one
  (all those properties belong to components with a custom inspector). No range or conditional attributes exist.
- `get_object` marks what it knows for each property of a component: `"inspector": "<rule summary>"` (e.g.
  `"shown while type is Spot; clamped 1..179"`) and `"shown": false` when the property is never drawn. `list_component_types`
  has `customInspector` per type. Nothing else is needed to discover the rules: read the component with `get_object` after
  `add_component`, or use the tables below.

Uncertain cases the plugin allows (the source is ambiguous or depends on state it does not model): `ModBoardSpace.CustomGoalSpawn`
and `.BlockGoalSpawn` (drawn only while the space can support a goal spawn: depends on the board graph); `ModNavMeshAgent.agentTypeID` (the inspector's 'Agent Type' combo discards its result and its 'Quality'
dropdown writes this property, so the UI only produces 0..4); `ModParticleSystem.noiseRemapCurveY/Z` (the inspector wires all
three axes to `noiseRemapCurveX`, so the UI produces Y and Z equal to X); the particle `Prop` / stretch / module sub-properties
are checked only against the controlling value, not against the collapsing header being open (UI state).
The Material asset inspector (`CustomBoardSpaceInspector`) belongs to assets, not to components, and is out of scope.

Per component (generated from `Core/InspectorRules.cs`; a unit test keeps this block equal to the table):

<!-- inspector-rules:begin (generated from Core/InspectorRules.cs; a test keeps it in sync) -->

**ModAudioSource**

| Property | Rule |
|---|---|
| `volume` | clamped 0..1 |
| `spatialBlend` | clamped 0..1 |
| `stereoPanning` | shown while spatialBlend is not 1; clamped -1..1 |
| `rolloffMode` | shown while spatialBlend is not 0; not offered: Custom |
| `minDistance` | shown while spatialBlend is not 0 |
| `maxDistance` | shown while spatialBlend is not 0 |
| `useDopplerEffect` | shown while spatialBlend is not 0 |

All other properties of ModAudioSource are always drawn.

**ModBoardSpace**

| Property | Rule |
|---|---|
| `nodeEventType` | never shown |
| `nodeEventAction` | never shown |
| `hasInteraction` | never shown |
| `baseNodeType` | not offered: MapEvent, Trophy, Weapon |
| `ModCustomBoardSpace` | shown while baseNodeType is Custom |
| `ConsumeStepOnPass` | shown while baseNodeType is Custom |
| `StopRocket` | shown while baseNodeType is Custom |
| `DisabledOnStart` | shown while baseNodeType is not Start or Pathing |
| `SpawnRotation` | shown while CustomGoalSpawn is on; clamped 0..360 |
| `SpawnDistance` | shown while CustomGoalSpawn is on; clamped 0..4 |
| `CustomGoalSpawn` | note: drawn only while the space can support a goal spawn (depends on the board graph: not at the end of a teleport, not an intersection, not Pathing/Graveyard or a custom space that cannot be landed on); not checked by the plugin |
| `BlockGoalSpawn` | note: drawn only while the space can support a goal spawn (board graph); not checked by the plugin |

All other properties of ModBoardSpace are always drawn.

**ModBoardSpaceEvent**: every settable property is always drawn without restrictions.

**ModDestructible**: every settable property is always drawn without restrictions.

**ModFireWeapon**

| Property | Rule |
|---|---|
| `FireTime` | minimum 0.1 |
| `BeamCooldown` | shown while the weapon's damage type is Beam |

All other properties of ModFireWeapon are always drawn.

**ModItem**

| Property | Rule |
|---|---|
| `ItemPrefabGUID` | never shown |
| `DroppedItemPrefabGUID` | never shown |
| `ItemSlot` | shown while ItemType is not Pickup |
| `UniqueDropPrefab` | shown while ItemType is not Pickup |
| `UniquePickup` | shown while ItemType is not Pickup; note: for Pickup items the inspector resets UniquePickup to false and ItemPickupPriority to 0 every frame |
| `ItemPickupPriority` | shown while ItemType is not Pickup |
| `HeldPositionOffset` | shown while ItemType is not Pickup |
| `HeldRotationOffset` | shown while ItemType is not Pickup |
| `SecondsBetweenHeldAction` | shown while ItemType is not Pickup; minimum 0.1 |
| `CanBeDropped` | shown while ItemType is not Pickup |
| `AutoDropAfterTime` | shown while ItemType is not Pickup |
| `DropOnDeath` | shown while ItemType is not Pickup |
| `ItemHoldAnimation` | shown while ItemType is not Worn or Pickup |
| `DroppedItemPrefab` | shown while ItemType is not Pickup and UniqueDropPrefab is on |
| `CustomItemBeaconColor` | shown while ItemBeaconColor is Custom |
| `WeaponType` | shown while ItemType is Weapon |
| `Cooldown` | shown while ItemType is Carried or Weapon; minimum 0.1 |
| `HasLimitedUses` | shown while ItemType is Carried or Weapon |
| `DropAfterUses` | shown while ItemType is Carried or Weapon and HasLimitedUses is on |
| `DropAfterTime` | shown while ItemType is not Pickup and AutoDropAfterTime is on |
| `DisappearWhenDropped` | shown while ItemType is not Pickup and CanBeDropped is on or AutoDropAfterTime is on or DropOnDeath is on |

All other properties of ModItem are always drawn.

**ModLight**

| Property | Rule |
|---|---|
| `range` | shown while type is not Directional |
| `spotAngle` | shown while type is Spot; clamped 1..179 |

All other properties of ModLight are always drawn.

**ModLogic**: every settable property is always drawn without restrictions.

**ModMeshCollider**

| Property | Rule |
|---|---|
| `mesh` | shown while no mesh renderer on the object provides the mesh |

All other properties of ModMeshCollider are always drawn.

**ModMover**

| Property | Rule |
|---|---|
| `Direction` | shown while DirectionType is Custom |
| `RotationAxis` | shown while RotationAxisType is Custom |

All other properties of ModMover are always drawn.

**ModNavMeshAgent**

| Property | Rule |
|---|---|
| `speed` | never shown |
| `quality` | never shown |
| `autoTraverseOffMeshLink` | never shown |
| `agentTypeID` | note: uncertain: the 'Agent Type' combo discards its result and the 'Quality' dropdown writes agentTypeID, so the UI only produces 0..4 (ObstacleAvoidanceType values); not enforced |

All other properties of ModNavMeshAgent are always drawn.

**ModNote**: every settable property is always drawn without restrictions.

**ModParticleSystem**

| Property | Rule |
|---|---|
| `startSize` | never shown |
| `arcSpeed` | never shown |
| `allowRoll` | never shown |
| `shadowBias` | never shown |
| `animationMode` | never shown |
| `fps` | never shown |
| `startSizeY` | shown while startSize3D is on |
| `startSizeZ` | shown while startSize3D is on |
| `startRotationX` | shown while startRotation3D is on |
| `startRotationY` | shown while startRotation3D is on |
| `randomSeed` | shown while autoRandomSeed is off |
| `simulationSpace` | not offered: Custom |
| `shapeType` | only Sphere, Hemisphere, Box, BoxShell, BoxEdge, Cone, ConeVolume, Donut |
| `angle` | shown while shapeType is Cone or ConeVolume |
| `length` | shown while shapeType is Cone or ConeVolume |
| `radius` | shown while shapeType is not Box, BoxShell or BoxEdge |
| `radiusThickness` | shown while shapeType is not Box, BoxShell or BoxEdge; clamped 0..1 |
| `arc` | shown while shapeType is not Box, BoxShell or BoxEdge; clamped 0..360 |
| `arcMode` | shown while shapeType is not Box, BoxShell or BoxEdge |
| `arcSpread` | shown while shapeType is not Box, BoxShell or BoxEdge; clamped 0..1 |
| `donutRadius` | shown while shapeType is Donut |
| `boxThickness` | shown while shapeType is BoxShell or BoxEdge |
| `speed` | shown while separateAxes is off |
| `speedX` | shown while separateAxes is on |
| `speedY` | shown while separateAxes is on |
| `speedZ` | shown while separateAxes is on |
| `velocityOverLifeTimeSpace` | shown while separateAxes is on |
| `forceOverLifetimeRandomize` | shown while forceOverLifetimeX is TwoConstants |
| `sizeOverLifetimeSize` | shown while sizeOverLifetimeSeparateAxes is off |
| `sizeOverLifetimeX` | shown while sizeOverLifetimeSeparateAxes is on |
| `sizeOverLifetimeY` | shown while sizeOverLifetimeSeparateAxes is on |
| `sizeOverLifetimeZ` | shown while sizeOverLifetimeSeparateAxes is on |
| `sizeBySpeedSize` | shown while sizeBySpeedSeparateAxes is off |
| `sizeBySpeedSizeX` | shown while sizeBySpeedSeparateAxes is on |
| `sizeBySpeedSizeY` | shown while sizeBySpeedSeparateAxes is on |
| `sizeBySpeedSizeZ` | shown while sizeBySpeedSeparateAxes is on |
| `rotationOverLifetimeX` | shown while rotationOverLifetimeSeparateAxes is on |
| `rotationOverLifetimeY` | shown while rotationOverLifetimeSeparateAxes is on |
| `rotationBySpeedY` | shown while rotationBySpeedSeparateAxes is on |
| `rotationBySpeedZ` | shown while rotationBySpeedSeparateAxes is on |
| `noiseStrength` | shown while noiseSeparateAxes is off |
| `noiseStrengthX` | shown while noiseSeparateAxes is on |
| `noiseStrengthY` | shown while noiseSeparateAxes is on |
| `noiseStrengthZ` | shown while noiseSeparateAxes is on |
| `noiseOctaveMultiplier` | shown while noiseOctaves is greater than 1 |
| `noiseOctaveScale` | shown while noiseOctaves is greater than 1 |
| `noiseRemapCurve` | shown while noiseRemapEnabled is on and noiseSeparateAxes is off |
| `noiseRemapCurveX` | shown while noiseRemapEnabled is on and noiseSeparateAxes is on |
| `noiseRemapCurveY` | shown while noiseRemapEnabled is on and noiseSeparateAxes is on; note: uncertain: the inspector's three-axis widget is wired to noiseRemapCurveX for all three axes, so the UI only produces Y and Z equal to X; not enforced |
| `noiseRemapCurveZ` | shown while noiseRemapEnabled is on and noiseSeparateAxes is on |
| `rowIndex` | shown while rowMode is Custom |
| `frameOverTime` | shown while timeMode is Lifetime |
| `cycleCount` | shown while timeMode is Lifetime |
| `speedRange` | shown while timeMode is Speed |
| `Prop` | shown while renderMode is Mesh |
| `cameraVelocityScale` | shown while renderMode is Stretch |
| `velocityScale` | shown while renderMode is Stretch |
| `lengthScale` | shown while renderMode is Stretch |
| `freeformStretching` | shown while renderMode is Stretch |
| `rotateWithStretchDirection` | shown while renderMode is Stretch and freeformStretching is on |

All other properties of ModParticleSystem are always drawn.

**ModPlayerSpawn**

| Property | Rule |
|---|---|
| `LineLength` | shown while SpawnShape is Line |
| `Radius` | shown while SpawnShape is Circle |
| `SpawnDistribution` | shown while SpawnShape is Line or Circle |
| `SharedSpawn` | shown while SpawnUsageType is InitialSpawn |
| `AllowedPlayers` | shown while Advanced is on |
| `PlayerCountMask` | shown while Advanced is on |

All other properties of ModPlayerSpawn are always drawn.

**ModPrefabInstance**

| Property | Rule |
|---|---|
| `PrefabGUID` | never shown |

All other properties of ModPrefabInstance are always drawn.

**ModProp**

| Property | Rule |
|---|---|
| `tintColor` | shown while customMaterials holds no material |

All other properties of ModProp are always drawn.

**ModSimpleAnimator**

| Property | Rule |
|---|---|
| `UseLateUpdate` | never shown |

All other properties of ModSimpleAnimator are always drawn.

**ModSpawner**

| Property | Rule |
|---|---|
| `SpawnCount` | clamped 0..20 |
| `PrefabGUIDs` | never shown |
| `Interval1` | shown while Type is AlwaysSpawn |
| `Delay` | shown while Type is SpawnOnDestruction |
| `SpawnPoints` | shown while LocationType is OnTargets |

All other properties of ModSpawner are always drawn.

**ModText**

| Property | Rule |
|---|---|
| `Font` | not offered: COUNT |
| `HorizontalAlignment` | only Left, Center, Right, Justified |
| `FontStyles` | flags only Bold, Italic, Underline, Strikethrough |
| `OutlineColor` | shown while Outline is on |
| `OutlineThickness` | shown while Outline is on; clamped 0..1 |
| `ShadowColor` | shown while Shadow is on |
| `ShadowOffsetX` | shown while Shadow is on; clamped -1..1 |
| `ShadowOffsetY` | shown while Shadow is on; clamped -1..1 |
| `ShadowSoftness` | shown while Shadow is on; clamped 0..1 |

All other properties of ModText are always drawn.

**ModTrigger**

| Property | Rule |
|---|---|
| `Center` | shown while TriggerShape is not CustomCollider |
| `Size` | shown while TriggerShape is Box |
| `Radius` | shown while TriggerShape is Sphere or Capsule |
| `Height` | shown while TriggerShape is Capsule |
| `TriggerOnEnter` | shown while TriggerOnHit is off and TriggerShape is not CustomCollider |
| `TriggerOnExit` | shown while TriggerOnHit is off and TriggerShape is not CustomCollider |
| `TriggerOnStay` | shown while TriggerOnHit is off and TriggerShape is not CustomCollider |
| `StayTriggerInterval` | shown while TriggerOnHit is off and TriggerShape is not CustomCollider and TriggerOnStay is on; minimum 0.1 |

All other properties of ModTrigger are always drawn.

**ModWater**

| Property | Rule |
|---|---|
| `CellSize` | clamped 0.1..10 |
| `WaveHeight` | clamped 0..1 |
| `WaveSpeed` | clamped 0..10 |

All other properties of ModWater are always drawn.

<!-- inspector-rules:end -->


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

| Tool | What the editor does (and the plugin does the same) |
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

The Action Editor has no duplicate/copy/paste of actions, so the plugin has none. Action lists are flat; the only
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

Clamps: where the inspector re-applies a limit on every frame it draws the action, the plugin stores the limited
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
  either. The plugin marks the mod as having unsaved edits (`get_status.hasUnsavedEdits`); `save_mod` writes the
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
  appending there). Other actions: `supportsTargets: false`, `defaultTargetFlags: null`. `note` (only on Play Sound, Change
  Health and Kill): what the action does at run time, including that a target action with no target does nothing.
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
`get_action_graph` shows it; `actionCount`: length of the list afterwards. `warnings` (only when present, here and in
`set_action` and for the same steps inside `run_batch`): a target action that will execute on nothing, see
[No target](#no-target-target-actions-that-do-nothing). The call is never refused for it.

#### No target: target actions that do nothing

`TargetAction.Execute` calls `ExecuteOnTarget` only for the Source flag, the Receiver flag and each resolved Advanced target.
With no flag and no Advanced targets nothing happens, yet the playtest feed still reports `action_executed` (it means the
action ran, not that it had an effect). Examples: Play Sound is silent (with a target it is audible: an `AudioSource` target
plays the action's clip if one is set, else its own; any other target component plays a positional one-shot; in a test it was audible
with Receiver (This Object) and with an Advanced target), Change Health does nothing. In a Trigger or Logic Hit event the player is
Source (Activator) and Receiver is the object itself; in player-settings triggers the player is Receiver. `add_action` and `set_action` add `warnings: ["..."]` when the
resulting action is a `TargetAction` with no Source/Receiver flag and no Advanced target that resolves (an Advanced flag with an empty
list counts as no target). Not warned: Spawn Prefab with Spawn At Position on (its own `Execute` spawns at the
position without a target; with it off, the default, it warns) and Show Message with `Everyone` in its message target (shown without a
target). The warning names the call site's Source and Receiver labels. It is advisory: a failure while computing it is skipped and
never fails the edit. The other `TargetAction` subclasses need a target: four override `Execute` (Spawn Prefab, Set Connection Active,
Give Item, Move Space), but the last three only set their random source and call the base `Execute`, and only Spawn Prefab's own
`Execute` does work (the spawn at a position) without a target. `list_action_types` rows carry a `note` for Play Sound, Change Health and Kill.

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

All values are validated before anything is written. If the game throws while the parameters (or then the targets) are
written, the ones written before stay applied (as with `set_property`): the call fails, the edit counts as an unsaved
change, and the message ends with `; already applied in this call: m_a, m_b (one undo reverts them)` (player-settings
triggers: `(player-settings triggers have no undo)`). The same holds for `set_trigger` parameters.

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

## Mod settings

Plugin 0.8.0 (api v2, additive, capability `"settings"`) reads and writes the settings the editor's **Mod Settings
window** shows (the yellow `Minigame Settings` / `Board Settings` entry at the top of the Hierarchy): minigame and board
details, minigame settings, player settings, music and ambience, lighting, screen effects, and for Board mods board
size and camera. Four tools: `list_mod_settings` and `get_mod_settings` (read), `set_mod_settings` and
`capture_preview_image` (write, guard OpenMod). The design notes are in `docs/architecture/06-mod-settings.md`.

What the tools do **not** touch: the Workshop item (title, description, tags, visibility, published id, thumbnail files:
`WorkshopItem.json`), `Data/Preview.jpg` (the editor's scene thumbnail, also the Workshop's fallback thumbnail), renaming
or deleting mods, and every editor button that writes to disk (lighting profile export/delete, item "Generate Icon").
`name` of the details sections is read-only for that reason. The minigame/board details the game shows are stored in
their own files (`MinigameDefinitionData.json` / `MapDetails.json`), separate from the Workshop item.

### How the editor applies settings, and what the plugin does about it

The window's draw code clamps values and hides controls; the data classes have **no limits of their own**, and the draw
code runs only while the group is expanded. The plugin therefore applies the same limits itself and reports them in
`clamped`, refuses fields the editor never shows (or shows only under a condition that is false after the call's other
values), and offers only the choices the UI offers. The rule table is `Core/SettingsRules.cs`; the tables
[below](#settings-rule-tables) are generated from it (a test keeps them in sync) and `list_mod_settings` returns the same
information per field. Settings have **no undo** in the editor and do not set its dirty flag: replies say
`undoable: false` and return the previous values, and the plugin's own unsaved-edit tracking (`get_status.hasUnsavedEdits`)
counts every settings change until `save_mod`.

### `list_mod_settings` (read)

| Param | Type | |
|---|---|---|
| `section` | string, optional | one section (case-insensitive); default all sections of the open mod's type |

Needs a loaded mod in edit mode (`INVALID_STATE` during a playtest, like `get_open_mod`: the running mod is loaded from the
saved files, not from the editor's objects). Minigame and Board mods have sections; BoardSpace and Item mods return
`sections: []` and a `note` (BoardSpace has no settings window; Item details are not covered in 0.8.0: read them with
`get_open_mod`). Reply: `{mod, modType, sections: [...], note}`; each section:

```json
{"name": "minigameSettings", "title": "Minigame Settings", "savedTo": "Data/ModSettings.json (written by save_mod)", "note": "...",
 "fields": [{"name": "PointsToWin", "label": "Points to win", "type": "int", "shape": "integer", "writable": true,
             "default": 5, "value": 5, "shown": false, "shownWhen": "EndConditions is ObtainPoints",
             "limits": {"min": 1, "max": 1000000, "applied": "values outside are clamped and reported in set_mod_settings.clamped"}}],
 "notShown": ["..."], "implied": ["..."], "refused": ["..."], "presets": ["Day", "..."]}
```

Per field: `name`, `label` (the UI label), `type` (`bool`, `int`, `float`, `string`, `enum`, `flags`, `choice`, `vector2`, `vector3`,
`rect`, `color`, `assetRef`, `assetRef[]`, `preset`; a screen-effect parameter is `param(<type>)`), `shape` (the accepted JSON), `writable`
(+ `reason` when not; `neverShown` when the editor has no control), `default` (a new mod's), `value` (current, same shape
as `get_mod_settings`), `shown` (+ `shownWhen`: the condition in terms of other fields of the section; `null` = cannot be
evaluated), `choices` (what the UI dropdown offers; presets: the editor's list), `assets` (`types`, `locations`
`internal` = built-in / `external` = the mod's own, `nullAllowed`), `limits` (`min`/`max`, `source` for run-time limits,
`rect` for the centre + size rule), `note`. Section extras: `notShown` (members of the same objects without a control),
`implied` (side effects the UI performs), `refused` (value combinations the plugin refuses), `presets` (lighting and
screen effects: the names the editor lists, ending with `Custom`), `unusablePresets` (listed but unselectable: the
editor matches presets by their internal name), `saveWarning` (screen effects only, see below).

### `get_mod_settings` (read)

Same `section` param and rules. Reply: `{mod, modType, sections: {<name>: {values: {<field>: <value>}, readOnly: [names]}}, note}`.
`values` use **exactly the shapes `set_mod_settings` accepts** (round trip; a test parses every writable field's output as
input). Fields whose controlling condition is false are still returned (their stored values); parameters of absent screen
effects are omitted; hidden members, `SpawnType` and the event triggers are not returned (`get_action_graph` with
`scope: "playerSettings"` shows the triggers). Value shapes: bool; integer; number (floats are printed with float
precision); string; enum name; flags `"Timer, ObtainPoints"` (names joined by `, `; `""` = none); `{x,y}`, `{x,y,z}`,
`{x,y,width,height}`; color `{r,g,b,a}` (0-1); asset `null` or `{guid, name, assetType, location}` (input: `null`, a guid or
`{guid}`); screen-effect parameter `{value, override}`.

### `set_mod_settings` (write, guard OpenMod)

| Param | Type | |
|---|---|---|
| `section` | string | a section of the open mod's type |
| `values` | object | `{field: value, ...}`, 1-128 fields of that section |

Errors: the guard's (`GUARD_NOT_TEST_MOD`, `INVALID_STATE` outside edit mode / with a dialog open, ...), `INVALID_PARAMS`.
**All-or-nothing:** every value is parsed, checked and clamped before anything changes. `INVALID_PARAMS` (message names the
field and the rule, `nothing was changed`) for: an unknown field; a field the editor never shows (`notShown`) or the plugin
does not write (read-only, with reason); a wrong type; an enum/flag name or number that is not defined; a choice the UI
does not offer; a preset name that is not in the editor's list; an asset reference to a missing asset, an asset of the
wrong type (e.g. a mesh as music) or the wrong location; a **conditional field whose condition is false after applying the
call's other values** (give the controlling field in the same call, e.g. `{"EndConditions": "Timer, ObtainPoints", "PointsToWin": 8}`);
camera mode `Follow` without a Player Spawn in the scene (the editor throws on every draw then).

Reply:

```json
{"section": "minigameSettings",
 "applied": {"RoundDuration": {"type": "float", "value": 5}},
 "previous": {"RoundDuration": 60},
 "clamped": [{"field": "RoundDuration", "requested": 1, "applied": 5, "min": 5, "max": 1200, "reason": "the editor limits this value to this range"}],
 "implied": [], "changed": true, "undoable": false,
 "note": "mod settings have no undo in the editor: use 'previous' to revert by hand with set_mod_settings. The change is in memory until save_mod, which writes Data/ModSettings.json (written by save_mod).",
 "warnings": ["..."], "validation": [{"severity": "warning", "message": "Missing minigame preview image."}]}
```

- `applied` is read back from the game after the write (the stored value, so clamps and normalisation show); implied
  side effects are in it too (`"implied": true`) and in `implied` (`{field, value, why}`): ThirdPerson forces the camera
  mode to Static; changing the board camera mode resets the distance (13 Perspective, 40 Orthographic) unless the call
  sets the distance itself.
- `previous` holds the values before the call (revert by hand: there is no undo). `changed` is false when every value
  already was the stored one; then the mod is **not** marked as having unsaved edits.
- `clamped`: what the UI's draw code would have clamped. `NumberOfRounds` 0 becomes 1 (the editor never lets 0 stand;
  the validator only rejects 0 when it is written by other means).
- `validation`: the settings-related messages of the editor's validator after the change (see the table at the end
  of the rule tables), e.g. the Last Man Standing / respawn warning for values the UI can produce but the validator
  dislikes. `null` + `validationError` when the validator could not run. Errors in `validation` do not undo the change.
- `warnings`: screen effects: switching **from Custom to a preset while custom effects exist** (see below); lighting: custom
  values are kept while a preset is selected.
- A game exception in the middle of the write returns `INTERNAL` naming the fields already applied and the previous values.
- The call is a write like any other: queued, guarded, committed at the point it changes the first value (`TIMEOUT` before
  that means nothing changed and a retry is safe). One call = one write turn; there is no undo step.

### Lighting and screen effects (presets and custom values)

Both sections have a `preset` field (names from `list_mod_settings`, validated against the editor's own list; the last
entry is `Custom`) and the custom fields, which the UI shows **only while `Custom` is selected**, so they are refused
otherwise (select `Custom` in the same call or first).

- Selecting a preset does what the editor's combo does: remembers the name, picks the preset object, assigns it to the
  mod and applies it. Preset objects are shared caches: the plugin **never edits them**; custom values are written only to
  the mod's own custom objects (`CustomLighting`, `CustomPostProcess`).
- After every lighting change the plugin calls `ApplyLightingChangesToRenderSettings` once; after screen-effect changes it
  toggles the scene view's post-processing layer once (what the editor does after adding/removing effects), so a
  `screenshot` shows the result. Screen effects are visible only while the toolbar's **Post Processing** button is on.
- **Save semantics.** `Lighting.json` always holds the custom lighting, so custom lighting survives a preset switch.
  `PostProcessing.data` is written from the **active** effects: saving while a built-in screen-effects preset is selected
  overwrites the custom effects on disk with the preset's, and they are gone after reopening the mod (in memory they
  survive until then). The plugin never does this silently: switching from Custom to a preset while custom effects exist
  adds a `warnings` entry, and `list_mod_settings` / `get_mod_settings` carry a `saveWarning` while that state exists.
  Select `Custom` again before `save_mod` to keep the effects. **Deliberate decision** (Phase 4 review, doc 06 risk 2
  "partly"): this switch is **warned about, not refused**. Selecting a preset is a legitimate, intended edit (the editor
  allows it with one click), the custom effects stay intact in memory until a save, and the warning plus `saveWarning`
  tell the agent exactly how to keep them; refusing would block a normal workflow to protect against a reversible state.
- Screen effects: field `<effect>` (bool) adds (`true`) or removes (`false`, its values are lost) the effect: `ambientOcclusion`,
  `bloom` (the game's ComputeBloom, labelled "Bloom"), `chromaticAberration`, `colorGrading`, `depthOfField`, `grain`,
  `lensDistortion`, `vignette`; `<effect>.enabled` is its header checkbox; `<effect>.<parameter>` is `{value, override}`
  (the editor ignores a value whose override is off): a bare value (`0.4`, `{r,g,b}`) sets the value **and** the override on,
  `{"override": false}` switches it off, `{"value": 0.4, "override": false}` stores without applying. Slider parameters
  are clamped to the `RangeAttribute` of the game's field, **read at run time** (0..10 if a field had none); parameters
  the editor draws as free inputs are not clamped. Values the editor forces while an effect's header is open are
  forced too (Color Grading `gradingMode` = LowDefinitionRange with override on; Vignette `mode` = Classic). The legacy
  Bloom effect (only present in some presets, cannot be added in the editor) is not covered.
- Presets whose internal name differs from their file name cannot be selected in the editor; they are listed under
  `unusablePresets` and refused.

### `capture_preview_image` (write, guard OpenMod)

No params. Takes the editor's own screenshot of the scene view and makes it the minigame's / board's **in-game preview
image** (the "Preview Image" under Minigame Details / Board Details; shown on the loading screen, ruleset and lobby).
Exactly what happens:

1. Refused (`INVALID_STATE`) with a dialog open, while a prefab is being edited, while the editor already takes a
   screenshot, for mods other than Minigame/Board; guard as every write tool (edit mode, `MCP_` mod).
2. `EditorSystem.TakeScreenGrab()` (the toolbar camera button / F10). At the end of the next frame the editor renders the
   scene-view camera (gizmos and grid hidden for the moment, restored afterwards) into a 1920x1080 target and **writes
   `Assets/Textures/Screenshot<N>.jpg`** (first free N) **inside the open sandbox mod's folder**, and its importer writes
   the `Screenshot<N>.jpg.pmeta` next to it, then registers the texture as a mod asset. Nothing is written outside the
   mod folder (the plugin checks the asset's path is inside it and refuses to assign otherwise). The selection is not
   changed.
3. The plugin waits (up to 12 s) for the new asset, assigns its texture as the preview image (same fields as the
   picker, plus the guid the editor keeps for it) and marks the mod as having unsaved edits.

The editor writes the file into whatever mod is open when its screenshot coroutine finishes (a few frames after step 2).
So that a mod opened by hand in that moment (possibly a real one) can never receive it, and so that closing the mod
cannot break the editor's screenshot code (it would throw on the missing mod and leave the editor "taking a screenshot",
with gizmo icons and grid hidden, for the rest of the session), the plugin refuses these editor actions from step 2 until
the file is written, for at most 8 s even if the capture never finishes (Harmony prefixes; the original is skipped as a
whole, so a refused action changes nothing):

| Refused action (editor UI) | Patched method | What the user sees |
|---|---|---|
| Open a mod: File > Open Mod dialog, File > Recent Mods, Published Mods > Open in mod editor | `EditorSystem.OpenMod` | the dialog closes (the game closes it before opening), the current mod stays open; with unsaved changes the Save / Don't Save prompt appears first and its Save still saves |
| New Mod dialog > OK | `NewModPopup.CreateNewMod` (the dialog's action: creates the mod, then always adds a mod-list entry) | the dialog closes, no folder and no list entry are created |
| (any other caller of mod creation) | `EditorSystem.CreateNewMod` | backstop; the only other caller is the game's automated test |
| File > Close Mod | `EditorSystem.SetCurrentMod` with null only (switching to a mod always passes) | the mod stays open; with unsaved changes the prompt appears first and its Save still saves |
| Delete Mod (File menu list and Open Mod dialog), confirmed | `MainMenuWidget.DeleteModResult`, `LoadModPopup.DeleteModResult` (close the open mod, then delete the folder) | the confirmation closes, nothing is closed or deleted |

One warning is logged per capture (BepInEx log and game console): `Opening, creating, closing or deleting a mod was
refused for a moment ...`; try again a second later. At all other times the prefixes do nothing. Leaving the editor
(exit to the main menu) is never refused: `EditorSystem.OnDisable` ends the hold, and the screenshot coroutine runs on the
editor object, so it stops when the editor is destroyed (or it writes into the still-open sandbox mod before that). If
any of these methods is missing or cannot be patched when the Mod Editor opens (game update), `capture_preview_image` refuses with
`INTERNAL` (`capture_preview_image is disabled because the plugin's mod-switch guard could not be installed ...; nothing
was changed`); every other tool keeps working. The prefixes are not part of the Workshop publish block.

Reply: `{asset: {guid, name, assetType, location, relativePath, fileExists, sizeBytes, width, height}, assignedTo:
"details.previewImage", undoable: false, note}`. The **file exists on disk immediately** (also if you never save and
reopen: it stays as an unused asset); the **assignment is in memory until `save_mod`**. `Data/Preview.jpg` and the Workshop
item are not touched. `TIMEOUT`: nothing was assigned (check `list_assets` for a `Screenshot<N>` texture). Not allowed
inside `run_batch` (several frames). `set_mod_settings` accepts any texture asset guid (`previewImage`; `list_assets` types the mod's own textures `Texture`, built-in ones `Texture2D`) too.

### Board custom spaces and other limits

`customSpaces.CustomSpacePrefabs` is **read-only**: board spaces store an *index* into this list (changing it re-points
existing spaces) and the editor rebuilds the spaces through a static event that only its own window raises; the plugin
does not raise it. Not covered in 0.8.0: Item mods (read with `get_open_mod`), BoardSpace mods (no settings window), the
legacy Bloom effect, importing textures/audio (a later increment). Hidden members of the data classes (the `notShown` lists)
are refused. Settings written while the editor's own settings window is open are re-clamped by the window on its next draw,
and values the window would not show stay as written.

### Settings rule tables

Generated from `Core/SettingsRules.cs`. "Shown while" is the condition under which the editor draws the control;
conditions are judged after applying the call's other values. Defaults are a new mod's.

<!-- settings-rules:begin (generated from Core/SettingsRules.cs; a test keeps it in sync) -->

**Minigame / details** (Minigame Details)

Saved to: Data/MinigameDefinitionData.json (written by save_mod). The minigame's own description and presentation image (loading screen, ruleset, lobby). NOT the Workshop item: its title, description, tags, visibility and thumbnail are never touched. Use capture_preview_image to create a preview image from the scene.

| Field | Type | Default | Rule |
|---|---|---|---|
| `name` | string | none | read-only: the name is the mod's name; renaming a mod is a separate editor action (Rename dialog: renames the folder and rewrites Meta.json and the Workshop item) that the plugin never performs |
| `description` | string | A minigame mod. | multiline text, no length limit in the editor (the plugin accepts up to 16384 characters) |
| `previewImage` | assetRef | none | asset type Texture2D, internal, external, null allowed; null = none (the validator warns: Missing minigame preview image). A guid from list_assets (type Texture for the mod's own textures, Texture2D for built-in ones) or the asset capture_preview_image created. |

Not shown by the editor (refused): minigameNameToken, descriptionToken, minPlayers, maxPlayers, inputHelp.

**Minigame / minigameSettings** (Minigame Settings)

Saved to: Data/ModSettings.json (written by save_mod). How the minigame is played. The player type and all player options are in section playerSettings.

| Field | Type | Default | Rule |
|---|---|---|---|
| `NumberOfRounds` | int | 1 | clamped 1..20; the validator requires at least 1; the UI clamps to 1..20 |
| `EndConditions` | flags | Timer | flags: Timer, ObtainPoints, RemainingPlayersAlive, FinishMinigame; any combination; "" or "None" or 0 = no end condition (the UI's "Nothing") |
| `PointsToWin` | int | 5 | shown while EndConditions is ObtainPoints; clamped 1..1000000 |
| `RoundDuration` | float | 60 | shown while EndConditions is Timer; clamped 5..1200 |
| `MinPlayersAlive` | int | 1 | shown while EndConditions is RemainingPlayersAlive; clamped 1..7 |
| `PlacementCondition` | enum | MostPoints | choices: MostPoints, LeastPoints, FinishOrder, LastManStanding |
| `ShowPlayerScore` | bool | true |  |
| `OnlyShowCombinedTeamScoreInTeams` | bool | false | shown while ShowPlayerScore is on |
| `SpawnType` | string | none | read-only: the editor has no control for it and the game ignores the value (it always uses PortalAbove) |

**Minigame / playerSettings** (Player Settings)

Saved to: Data/ModSettings.json (written by save_mod). The player type and every foldout of the Player Settings group. The six event triggers (weaponHitTrigger, tickTrigger, onDeathTrigger, onRespawnTrigger, onFirstSpawnTrigger, onRoundEndTrigger) are edited with the action tools (get_action_graph / add_action ... with scope "playerSettings"), not here. Changing the player type, canJump or canPunch also regenerates the controls help shown on the loading screen (the editor does so at save too).

| Field | Type | Default | Rule |
|---|---|---|---|
| `ChosenPlayerType` | enum | TopDown | choices: ThirdPerson, TopDown, TwinStick; the UI offers ThirdPerson/TopDown in a combo and TwinStick as a checkbox under Movement (only while TopDown); all three are accepted |
| `movementSpeed` | float | 6 | clamped 0..1000 |
| `movementAcceleration` | float | 50 | clamped 0..1000 |
| `movementDecelaration` | float | 50 | clamped 0..10000; the game's member name is spelled movementDecelaration |
| `midAirMovementAcceleration` | float | 8 | clamped 0..1000 |
| `playerGravity` | float | 25 | clamped -100..100; the UI shows playerGravity / 25 ("Gravity Scale", slider -4..4); this is the stored value |
| `stepOffset` | float | 0.5 | clamped 0..1.75 |
| `canJump` | bool | true |  |
| `jumpSpeed` | float | 7 | shown while canJump is on; clamped 0..1000 |
| `jumpCount` | int | 1 | shown while canJump is on; clamped 1..1001; the UI shows jumpCount - 1 as "Mid-air Jumps" (0..1000); this is the stored value |
| `topDownCameraMode` | enum | Static | shown while ChosenPlayerType is TopDown or TwinStick; choices: Static, Follow, FollowAll; Follow needs a Player Spawn object in the scene (the editor throws on every draw otherwise); with ThirdPerson the editor forces Static |
| `topDownCameraDistance` | float | 10 | shown while ChosenPlayerType is TopDown or TwinStick; clamped 1..40 |
| `topDownCameraViewAngle` | float | 35 | shown while ChosenPlayerType is TopDown or TwinStick; clamped 0..90 |
| `topDownCameraPositionOffset` | vector3 | {x: 0, y: 0, z: 0} | shown while ChosenPlayerType is TopDown or TwinStick |
| `topDownCameraFollowAllType` | enum | Square | shown while ChosenPlayerType is TopDown or TwinStick and topDownCameraMode is FollowAll; choices: Circle, Square |
| `topDownCamerafollowAllAreaRect` | rect | {x: -5, y: -5, width: 10, height: 10} | shown while ChosenPlayerType is TopDown or TwinStick and topDownCameraMode is FollowAll; width and height clamped 0..2000, centre kept within +-(1000 - half size); for the Circle shape the height is set to the width; the member's name has a lower-case f: topDownCamerafollowAllAreaRect |
| `maxXMoveDist` | float | 3 | shown while ChosenPlayerType is TopDown or TwinStick and topDownCameraMode is FollowAll and topDownCameraFollowAllType is Square |
| `maxZMoveDist` | float | 3 | shown while ChosenPlayerType is TopDown or TwinStick and topDownCameraMode is FollowAll and topDownCameraFollowAllType is Square |
| `sphereMaxMoveDist` | float | 3 | shown while ChosenPlayerType is TopDown or TwinStick and topDownCameraMode is FollowAll and topDownCameraFollowAllType is Circle |
| `canPunch` | bool | false |  |
| `punchRechargeSpeed` | float | 0.75 | shown while canPunch is on; clamped 0.1..1000 |
| `punchDamage` | int | 1 | shown while canPunch is on; clamped -1000..1000 |
| `punchStunsTarget` | bool | false | shown while canPunch is on |
| `punchStunDuration` | float | 1 | shown while canPunch is on and punchStunsTarget is on; clamped 0.1..1000 |
| `stunImmunityLength` | float | 1 | clamped 0..1000 |
| `respawnEnabled` | bool | true |  |
| `respawnTime` | float | 3 | shown while respawnEnabled is on; clamped 0..1000 |
| `respawnInvulnerableTime` | float | 1 | shown while respawnEnabled is on; clamped 0..1000 |
| `useHealth` | bool | false |  |
| `showHealthBar` | bool | false | shown while useHealth is on |
| `health` | int | 100 | shown while useHealth is on; clamped 1..1000 |
| `showNameTags` | bool | true |  |
| `nameTagType` | enum | WorldSpaceWorldSizeOccluded | shown while showNameTags is on; choices: WorldSpaceScreenSize, WorldSpaceWorldSize, WorldSpaceWorldSizeOccluded; UI labels: WorldSpaceScreenSize = "Screen Space", WorldSpaceWorldSize = "World Space", WorldSpaceWorldSizeOccluded = "True World Space"; "no name tags" is showNameTags = false |
| `weaponHitTrigger` | trigger | none | read-only: an event trigger: use get_action_graph with scope "playerSettings" and add_action / set_action / remove_action / move_action |
| `tickTrigger` | trigger | none | read-only: an event trigger: use get_action_graph with scope "playerSettings" and add_action / set_action / remove_action / move_action |
| `onDeathTrigger` | trigger | none | read-only: an event trigger: use get_action_graph with scope "playerSettings" and add_action / set_action / remove_action / move_action |
| `onRespawnTrigger` | trigger | none | read-only: an event trigger: use get_action_graph with scope "playerSettings" and add_action / set_action / remove_action / move_action |
| `onFirstSpawnTrigger` | trigger | none | read-only: an event trigger: use get_action_graph with scope "playerSettings" and add_action / set_action / remove_action / move_action |
| `onRoundEndTrigger` | trigger | none | read-only: an event trigger: use get_action_graph with scope "playerSettings" and add_action / set_action / remove_action / move_action |

Implied: the editor forces topDownCameraMode to Static for the ThirdPerson player type.

Refused: `topDownCameraMode` = Follow unless the scene has a Player Spawn object.

Not shown by the editor (refused): coyoteTime, shootDistance, shootRechargeSpeed, shotSpreadVertical, shotSpreadHorizontal, bulletsPerShot, firstShotCentred, firingPattern, respawnAnimationType, hitTrigger.

**Minigame / music** (Music & Ambient Sound)

Saved to: Data/ModSettings.json (written by save_mod). Background track and ambience of the minigame. Clips are assets: a built-in one (list_assets includeBuiltIn, type AudioClip) or one imported into the mod.

| Field | Type | Default | Rule |
|---|---|---|---|
| `Music` | assetRef | 13dbf0ac-803d-49d1-997f-0a559bd43061 | asset type AudioClip, internal, external, null allowed; null = no music; a game property (stores the clip's asset guid) |
| `MusicVolume` | float | 0.3 | clamped 0..1 |
| `Ambience` | assetRef | 90abde3b-7f29-43ee-80c9-40ec418d5993 | asset type AudioClip, internal, external, null allowed; null = no ambience |
| `AmbienceVolume` | float | 0.5 | clamped 0..1 |

**Minigame / lighting** (Lighting)

Saved to: Data/Meta.json (selected preset) and Data/Lighting.json (the custom values, always) (written by save_mod). Pick a preset by name, or "Custom" and edit the custom values. Custom values live in the mod's own custom lighting object and survive a preset switch (Lighting.json always holds them); the preset objects themselves are never modified. After a change the plugin applies the lighting to the editor scene once, so a screenshot shows it. Presets are listed by list_mod_settings (the built-in files of the game).

| Field | Type | Default | Rule |
|---|---|---|---|
| `preset` | preset | Default | choices: the preset names of list_mod_settings (built-in files plus "Custom"); one of the names list_mod_settings shows (the last entry is "Custom"); anything else is refused (the editor would silently fall back to default lighting) |
| `stars` | bool | false | shown while preset is Custom |
| `topColor` | color | {r: 0.380392, g: 0.670588, b: 1, a: 1} | shown while preset is Custom |
| `horizonColor` | color | {r: 0.360784, g: 0.654902, b: 0.882353, a: 1} | shown while preset is Custom |
| `groundColor` | color | {r: 0.160784, g: 0.25098, b: 0.321569, a: 1} | shown while preset is Custom |
| `haloColor` | color | {r: 0.709804, g: 0.670588, b: 0.568627, a: 1} | shown while preset is Custom |
| `haloExponent` | float | 6 | shown while preset is Custom; clamped 0..32 |
| `haloStrength` | float | 1 | shown while preset is Custom; clamped 0.1..4 |
| `globalLightEnabled` | bool | true | shown while preset is Custom |
| `globalLightRotation` | vector3 | {x: 130, y: 25, z: 0} | shown while preset is Custom and globalLightEnabled is on |
| `globalLightColor` | color | {r: 1, g: 0.956863, b: 0.839216, a: 1} | shown while preset is Custom and globalLightEnabled is on |
| `globalLightIntensity` | float | 0.6 | shown while preset is Custom and globalLightEnabled is on |
| `globalShadowItensity` | float | 1 | shown while preset is Custom and globalLightEnabled is on; clamped 0..1; the game's member name is spelled globalShadowItensity |
| `globalLightShadows` | enum | Soft | shown while preset is Custom and globalLightEnabled is on; choices: None, Hard, Soft |
| `ambientMode` | enum | Skybox | shown while preset is Custom; choices: Skybox, Gradient, Color |
| `ambientIntensity` | float | 0.7 | shown while preset is Custom and ambientMode is Skybox |
| `ambientSkyColor` | color | {r: 0.211765, g: 0.227451, b: 0.258824, a: 1} | shown while preset is Custom and ambientMode is Gradient or Color; labelled "Ambient Color" in Color mode |
| `ambientEquatorColor` | color | {r: 0.113725, g: 0.12549, b: 0.133333, a: 1} | shown while preset is Custom and ambientMode is Gradient |
| `ambientGroundColor` | color | {r: 0.047059, g: 0.043137, b: 0.035294, a: 1} | shown while preset is Custom and ambientMode is Gradient |
| `cloudShadows` | bool | false | shown while preset is Custom and globalLightShadows is not None |
| `cloudSpeed1` | float | 0.12 | shown while preset is Custom and globalLightShadows is not None and cloudShadows is on |
| `cloudSpeed2` | float | 0.1375 | shown while preset is Custom and globalLightShadows is not None and cloudShadows is on |
| `cloudIntensity` | float | 0.7 | shown while preset is Custom and globalLightShadows is not None and cloudShadows is on; clamped 0..1 |
| `cloudStrength` | float | 1 | shown while preset is Custom and globalLightShadows is not None and cloudShadows is on; clamped 0..5 |
| `cloudScale` | float | 200 | shown while preset is Custom and globalLightShadows is not None and cloudShadows is on |
| `fog` | bool | false | shown while preset is Custom |
| `fogColor` | color | {r: 0.5, g: 0.5, b: 0.5, a: 1} | shown while preset is Custom and fog is on |
| `fogMode` | enum | Linear | shown while preset is Custom and fog is on; choices: Linear, Exponential, ExponentialSquared |
| `fogEndDistance` | float | 300 | shown while preset is Custom and fog is on and fogMode is Linear |
| `fogStartDistance` | float | 0 | shown while preset is Custom and fog is on and fogMode is Linear |
| `fogDensity` | float | 0.01 | shown while preset is Custom and fog is on and fogMode is not Linear |
| `shadowDistance` | float | 200 | shown while preset is Custom; clamped 5..250 |
| `shadowCascades` | choice | Four | shown while preset is Custom; choices: Zero, Two, Four; stored as the numbers 0, 2 or 4 |

Not shown by the editor (refused): version, presetName, shadowCascade4Split, shadowProjection, shadowCascade2Split, defaultReflectionMode, reflectionIntensity.

**Minigame / screenEffects** (Screen Effects)

Saved to: Data/Meta.json (selected preset) and Data/PostProcessing.data (the ACTIVE effects: the preset's values while a preset is selected) (written by save_mod). Pick a preset by name, or "Custom" and add / remove effects and edit their parameters. Each parameter is {value, override}: the editor ignores a value whose override is off, so a bare value in set_mod_settings sets the value AND switches the override on; {override: false} switches it off. SAVE WARNING: PostProcessing.data is written from the active effects, so saving while a built-in preset is selected overwrites the custom effects on disk with the preset's values (they survive in memory until the mod is reopened; select Custom again before save_mod to keep them). The effects are visible in the scene view only while the toolbar's Post Processing button is on; the plugin refreshes the effect layer after a change. The legacy Bloom effect (only present in some presets) cannot be added and is not covered.

| Field | Type | Default | Rule |
|---|---|---|---|
| `preset` | preset | Default | choices: the preset names of list_mod_settings (built-in files plus "Custom"); one of the names list_mod_settings shows (the last entry is "Custom"); anything else is refused (the editor would throw) |
| `ambientOcclusion` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `ambientOcclusion.enabled` | bool | true | shown while preset is Custom and ambientOcclusion is on; the effect's on/off switch |
| `ambientOcclusion.mode` | param(enum) | (game default) | shown while preset is Custom and ambientOcclusion is on; choices: ScalableAmbientObscurance, MultiScaleVolumetricObscurance |
| `ambientOcclusion.intensity` | param(float) | (game default) | shown while preset is Custom and ambientOcclusion is on; clamped 0..4 (slider limits read from the game's Range attribute) |
| `ambientOcclusion.radius` | param(float) | (game default) | shown while preset is Custom and ambientOcclusion is on and ambientOcclusion.mode is ScalableAmbientObscurance; no limit (free input) |
| `ambientOcclusion.thicknessModifier` | param(float) | (game default) | shown while preset is Custom and ambientOcclusion is on and ambientOcclusion.mode is not ScalableAmbientObscurance; clamped 1..10 (slider limits read from the game's Range attribute) |
| `ambientOcclusion.color` | param(color) | (game default) | shown while preset is Custom and ambientOcclusion is on |
| `ambientOcclusion.ambientOnly` | param(bool) | (game default) | shown while preset is Custom and ambientOcclusion is on |
| `bloom` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `bloom.enabled` | bool | true | shown while preset is Custom and bloom is on; the effect's on/off switch |
| `bloom.HighQualityBloom` | param(bool) | (game default) | shown while preset is Custom and bloom is on |
| `bloom.BloomStrength` | param(float) | (game default) | shown while preset is Custom and bloom is on; no limit (free input) |
| `bloom.BloomThreshold` | param(float) | (game default) | shown while preset is Custom and bloom is on; clamped 0..2 (slider limits read from the game's Range attribute) |
| `bloom.BloomUpsampleFactor` | param(float) | (game default) | shown while preset is Custom and bloom is on; no limit (free input) |
| `bloom.color` | param(color) | (game default) | shown while preset is Custom and bloom is on |
| `chromaticAberration` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `chromaticAberration.enabled` | bool | true | shown while preset is Custom and chromaticAberration is on; the effect's on/off switch |
| `chromaticAberration.intensity` | param(float) | (game default) | shown while preset is Custom and chromaticAberration is on; no limit (free input) |
| `colorGrading` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `colorGrading.enabled` | bool | true | shown while preset is Custom and colorGrading is on; the effect's on/off switch |
| `colorGrading.temperature` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `colorGrading.tint` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `colorGrading.colorFilter` | param(color) | (game default) | shown while preset is Custom and colorGrading is on |
| `colorGrading.hueShift` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -180..180 (slider limits read from the game's Range attribute) |
| `colorGrading.saturation` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `colorGrading.brightness` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `colorGrading.contrast` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerRedOutRedIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerRedOutGreenIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerRedOutBlueIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerGreenOutRedIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerGreenOutGreenIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerGreenOutBlueIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerBlueOutRedIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerBlueOutGreenIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerBlueOutBlueIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `depthOfField` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `depthOfField.enabled` | bool | true | shown while preset is Custom and depthOfField is on; the effect's on/off switch |
| `depthOfField.focusDistance` | param(float) | (game default) | shown while preset is Custom and depthOfField is on; no limit (free input) |
| `depthOfField.aperture` | param(float) | (game default) | shown while preset is Custom and depthOfField is on; clamped 0.05..32 (slider limits read from the game's Range attribute) |
| `depthOfField.focalLength` | param(float) | (game default) | shown while preset is Custom and depthOfField is on; clamped 1..300 (slider limits read from the game's Range attribute) |
| `depthOfField.kernelSize` | param(enum) | (game default) | shown while preset is Custom and depthOfField is on; choices: Small, Medium, Large, VeryLarge |
| `grain` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `grain.enabled` | bool | true | shown while preset is Custom and grain is on; the effect's on/off switch |
| `grain.colored` | param(bool) | (game default) | shown while preset is Custom and grain is on |
| `grain.intensity` | param(float) | (game default) | shown while preset is Custom and grain is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `grain.size` | param(float) | (game default) | shown while preset is Custom and grain is on; clamped 0.3..3 (slider limits read from the game's Range attribute) |
| `grain.lumContrib` | param(float) | (game default) | shown while preset is Custom and grain is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `lensDistortion` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `lensDistortion.enabled` | bool | true | shown while preset is Custom and lensDistortion is on; the effect's on/off switch |
| `lensDistortion.intensity` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `lensDistortion.intensityX` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `lensDistortion.intensityY` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `lensDistortion.centerX` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped -1..1 (slider limits read from the game's Range attribute) |
| `lensDistortion.centerY` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped -1..1 (slider limits read from the game's Range attribute) |
| `lensDistortion.scale` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped 0.01..5 (slider limits read from the game's Range attribute) |
| `vignette` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `vignette.enabled` | bool | true | shown while preset is Custom and vignette is on; the effect's on/off switch |
| `vignette.color` | param(color) | (game default) | shown while preset is Custom and vignette is on |
| `vignette.center` | param(vector2) | (game default) | shown while preset is Custom and vignette is on |
| `vignette.intensity` | param(float) | (game default) | shown while preset is Custom and vignette is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `vignette.smoothness` | param(float) | (game default) | shown while preset is Custom and vignette is on; clamped 0.01..1 (slider limits read from the game's Range attribute) |
| `vignette.roundness` | param(float) | (game default) | shown while preset is Custom and vignette is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `vignette.rounded` | param(bool) | (game default) | shown while preset is Custom and vignette is on |

**Board / details** (Board Details)

Saved to: Data/MapDetails.json (written by save_mod). The board's own description and presentation image (lobby and in game). NOT the Workshop item: its title, description, tags, visibility and thumbnail are never touched. Use capture_preview_image to create a preview image from the scene.

| Field | Type | Default | Rule |
|---|---|---|---|
| `name` | string | none | read-only: the name is the mod's name; renaming a mod is a separate editor action that the plugin never performs |
| `description` | string | A board mod. |  |
| `previewImage` | assetRef | none | asset type Texture2D, internal, external, null allowed; null = none (the validator warns: Missing board preview image) |
| `defaultGoalChestCount` | int | 1 | clamped 1..8; players' rulesets can override it |
| `supportsWeaponCache` | bool | true |  |
| `useCustomChestShuffleLocation` | bool | false |  |
| `chestShuffleLocation` | vector3 | {x: 0, y: 0, z: 0} | world space; the editor lets you edit it even while the checkbox is off (it only greys it out), so it has no condition here either |

Not shown by the editor (refused): sceneName, overrideSpacePrefabs, overrideSpace, spacePrefabOverrides.

**Board / customSpaces** (Custom Spaces)

Saved to: Data/ModSettings.json (written by save_mod). The list of custom board space prefabs. Board spaces store an INDEX into this list, so adding, removing or reordering entries re-points existing spaces; the editor also rebuilds the spaces through an event that only its own window raises. Read-only in 0.8.0.

| Field | Type | Default | Rule |
|---|---|---|---|
| `CustomSpacePrefabs` | assetRef[] | none | read-only: index-based list whose change event is raised only by the editor's own settings window; the plugin does not write it (edit it in the editor); asset type PummelPrefab, external |

**Board / boardSize** (Board Size)

Saved to: Data/ModSettings.json (UseCustomBoardSize) and Data/MapDetails.json (mapExtents) (written by save_mod). Without a custom size the editor recomputes the extents from the board nodes at every save (bounding box plus a margin), so writing them is only meaningful with UseCustomBoardSize on.

| Field | Type | Default | Rule |
|---|---|---|---|
| `UseCustomBoardSize` | bool | false |  |
| `mapExtents` | rect | {x: -20, y: -20, width: 40, height: 40} | shown while UseCustomBoardSize is on; width and height clamped 0..2000, centre kept within +-(1000 - half size); {x, y, width, height} with x/y the corner (the UI edits size and centre); the default shown is a new mod's |

**Board / camera** (Camera Settings)

Saved to: Data/ModSettings.json (written by save_mod). How the camera views the board. Changing CameraMode also resets CameraDistance (13 for Perspective, 40 for Orthographic), as the editor does.

| Field | Type | Default | Rule |
|---|---|---|---|
| `CameraViewAngle` | float | 37.5 | clamped 14..90 |
| `CameraMode` | choice | Perspective | choices: Perspective, Orthographic |
| `CameraFov` | float | 60 | shown while CameraMode is Perspective; clamped 30..90 |
| `CameraDistance` | float | 13 | shown while CameraMode is Perspective; clamped 6..20 |
| `CameraSize` | float | 5 | shown while CameraMode is Orthographic; clamped 4..10 |

Implied: changing the camera mode resets the distance (13 Perspective, 40 Orthographic).

**Board / music** (Music & Ambient Sound)

Saved to: Data/MapDetails.json (written by save_mod). Music and ambient clips of the board (assets, built-in or imported). The validator warns when a clip is set and its volume is very low.

| Field | Type | Default | Rule |
|---|---|---|---|
| `Music` | assetRef | 3ac2bff4-9bc6-406e-8e6f-b725dc69e08a | asset type AudioClip, internal, external, null allowed; null = no music |
| `MusicVolume` | float | 0.3 | clamped 0..1 |
| `Ambience` | assetRef | 05f5b5c8-5190-49d7-ad7a-f154db4e4f29 | asset type AudioClip, internal, external, null allowed; null = no ambience |
| `AmbienceVolume` | float | 0.5 | clamped 0..1 |

**Board / lighting** (Lighting)

Saved to: Data/Meta.json (selected preset) and Data/Lighting.json (the custom values, always) (written by save_mod). Pick a preset by name, or "Custom" and edit the custom values. Custom values live in the mod's own custom lighting object and survive a preset switch (Lighting.json always holds them); the preset objects themselves are never modified. After a change the plugin applies the lighting to the editor scene once, so a screenshot shows it. Presets are listed by list_mod_settings (the built-in files of the game).

| Field | Type | Default | Rule |
|---|---|---|---|
| `preset` | preset | Default | choices: the preset names of list_mod_settings (built-in files plus "Custom"); one of the names list_mod_settings shows (the last entry is "Custom"); anything else is refused (the editor would silently fall back to default lighting) |
| `stars` | bool | false | shown while preset is Custom |
| `topColor` | color | {r: 0.380392, g: 0.670588, b: 1, a: 1} | shown while preset is Custom |
| `horizonColor` | color | {r: 0.360784, g: 0.654902, b: 0.882353, a: 1} | shown while preset is Custom |
| `groundColor` | color | {r: 0.160784, g: 0.25098, b: 0.321569, a: 1} | shown while preset is Custom |
| `haloColor` | color | {r: 0.709804, g: 0.670588, b: 0.568627, a: 1} | shown while preset is Custom |
| `haloExponent` | float | 6 | shown while preset is Custom; clamped 0..32 |
| `haloStrength` | float | 1 | shown while preset is Custom; clamped 0.1..4 |
| `globalLightEnabled` | bool | true | shown while preset is Custom |
| `globalLightRotation` | vector3 | {x: 130, y: 25, z: 0} | shown while preset is Custom and globalLightEnabled is on |
| `globalLightColor` | color | {r: 1, g: 0.956863, b: 0.839216, a: 1} | shown while preset is Custom and globalLightEnabled is on |
| `globalLightIntensity` | float | 0.6 | shown while preset is Custom and globalLightEnabled is on |
| `globalShadowItensity` | float | 1 | shown while preset is Custom and globalLightEnabled is on; clamped 0..1; the game's member name is spelled globalShadowItensity |
| `globalLightShadows` | enum | Soft | shown while preset is Custom and globalLightEnabled is on; choices: None, Hard, Soft |
| `ambientMode` | enum | Skybox | shown while preset is Custom; choices: Skybox, Gradient, Color |
| `ambientIntensity` | float | 0.7 | shown while preset is Custom and ambientMode is Skybox |
| `ambientSkyColor` | color | {r: 0.211765, g: 0.227451, b: 0.258824, a: 1} | shown while preset is Custom and ambientMode is Gradient or Color; labelled "Ambient Color" in Color mode |
| `ambientEquatorColor` | color | {r: 0.113725, g: 0.12549, b: 0.133333, a: 1} | shown while preset is Custom and ambientMode is Gradient |
| `ambientGroundColor` | color | {r: 0.047059, g: 0.043137, b: 0.035294, a: 1} | shown while preset is Custom and ambientMode is Gradient |
| `cloudShadows` | bool | false | shown while preset is Custom and globalLightShadows is not None |
| `cloudSpeed1` | float | 0.12 | shown while preset is Custom and globalLightShadows is not None and cloudShadows is on |
| `cloudSpeed2` | float | 0.1375 | shown while preset is Custom and globalLightShadows is not None and cloudShadows is on |
| `cloudIntensity` | float | 0.7 | shown while preset is Custom and globalLightShadows is not None and cloudShadows is on; clamped 0..1 |
| `cloudStrength` | float | 1 | shown while preset is Custom and globalLightShadows is not None and cloudShadows is on; clamped 0..5 |
| `cloudScale` | float | 200 | shown while preset is Custom and globalLightShadows is not None and cloudShadows is on |
| `fog` | bool | false | shown while preset is Custom |
| `fogColor` | color | {r: 0.5, g: 0.5, b: 0.5, a: 1} | shown while preset is Custom and fog is on |
| `fogMode` | enum | Linear | shown while preset is Custom and fog is on; choices: Linear, Exponential, ExponentialSquared |
| `fogEndDistance` | float | 300 | shown while preset is Custom and fog is on and fogMode is Linear |
| `fogStartDistance` | float | 0 | shown while preset is Custom and fog is on and fogMode is Linear |
| `fogDensity` | float | 0.01 | shown while preset is Custom and fog is on and fogMode is not Linear |
| `shadowDistance` | float | 200 | shown while preset is Custom; clamped 5..250 |
| `shadowCascades` | choice | Four | shown while preset is Custom; choices: Zero, Two, Four; stored as the numbers 0, 2 or 4 |

Not shown by the editor (refused): version, presetName, shadowCascade4Split, shadowProjection, shadowCascade2Split, defaultReflectionMode, reflectionIntensity.

**Board / screenEffects** (Screen Effects)

Saved to: Data/Meta.json (selected preset) and Data/PostProcessing.data (the ACTIVE effects: the preset's values while a preset is selected) (written by save_mod). Pick a preset by name, or "Custom" and add / remove effects and edit their parameters. Each parameter is {value, override}: the editor ignores a value whose override is off, so a bare value in set_mod_settings sets the value AND switches the override on; {override: false} switches it off. SAVE WARNING: PostProcessing.data is written from the active effects, so saving while a built-in preset is selected overwrites the custom effects on disk with the preset's values (they survive in memory until the mod is reopened; select Custom again before save_mod to keep them). The effects are visible in the scene view only while the toolbar's Post Processing button is on; the plugin refreshes the effect layer after a change. The legacy Bloom effect (only present in some presets) cannot be added and is not covered.

| Field | Type | Default | Rule |
|---|---|---|---|
| `preset` | preset | Default | choices: the preset names of list_mod_settings (built-in files plus "Custom"); one of the names list_mod_settings shows (the last entry is "Custom"); anything else is refused (the editor would throw) |
| `ambientOcclusion` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `ambientOcclusion.enabled` | bool | true | shown while preset is Custom and ambientOcclusion is on; the effect's on/off switch |
| `ambientOcclusion.mode` | param(enum) | (game default) | shown while preset is Custom and ambientOcclusion is on; choices: ScalableAmbientObscurance, MultiScaleVolumetricObscurance |
| `ambientOcclusion.intensity` | param(float) | (game default) | shown while preset is Custom and ambientOcclusion is on; clamped 0..4 (slider limits read from the game's Range attribute) |
| `ambientOcclusion.radius` | param(float) | (game default) | shown while preset is Custom and ambientOcclusion is on and ambientOcclusion.mode is ScalableAmbientObscurance; no limit (free input) |
| `ambientOcclusion.thicknessModifier` | param(float) | (game default) | shown while preset is Custom and ambientOcclusion is on and ambientOcclusion.mode is not ScalableAmbientObscurance; clamped 1..10 (slider limits read from the game's Range attribute) |
| `ambientOcclusion.color` | param(color) | (game default) | shown while preset is Custom and ambientOcclusion is on |
| `ambientOcclusion.ambientOnly` | param(bool) | (game default) | shown while preset is Custom and ambientOcclusion is on |
| `bloom` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `bloom.enabled` | bool | true | shown while preset is Custom and bloom is on; the effect's on/off switch |
| `bloom.HighQualityBloom` | param(bool) | (game default) | shown while preset is Custom and bloom is on |
| `bloom.BloomStrength` | param(float) | (game default) | shown while preset is Custom and bloom is on; no limit (free input) |
| `bloom.BloomThreshold` | param(float) | (game default) | shown while preset is Custom and bloom is on; clamped 0..2 (slider limits read from the game's Range attribute) |
| `bloom.BloomUpsampleFactor` | param(float) | (game default) | shown while preset is Custom and bloom is on; no limit (free input) |
| `bloom.color` | param(color) | (game default) | shown while preset is Custom and bloom is on |
| `chromaticAberration` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `chromaticAberration.enabled` | bool | true | shown while preset is Custom and chromaticAberration is on; the effect's on/off switch |
| `chromaticAberration.intensity` | param(float) | (game default) | shown while preset is Custom and chromaticAberration is on; no limit (free input) |
| `colorGrading` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `colorGrading.enabled` | bool | true | shown while preset is Custom and colorGrading is on; the effect's on/off switch |
| `colorGrading.temperature` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `colorGrading.tint` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `colorGrading.colorFilter` | param(color) | (game default) | shown while preset is Custom and colorGrading is on |
| `colorGrading.hueShift` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -180..180 (slider limits read from the game's Range attribute) |
| `colorGrading.saturation` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `colorGrading.brightness` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `colorGrading.contrast` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerRedOutRedIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerRedOutGreenIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerRedOutBlueIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerGreenOutRedIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerGreenOutGreenIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerGreenOutBlueIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerBlueOutRedIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerBlueOutGreenIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `colorGrading.mixerBlueOutBlueIn` | param(float) | (game default) | shown while preset is Custom and colorGrading is on; clamped -200..200 (slider limits read from the game's Range attribute) |
| `depthOfField` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `depthOfField.enabled` | bool | true | shown while preset is Custom and depthOfField is on; the effect's on/off switch |
| `depthOfField.focusDistance` | param(float) | (game default) | shown while preset is Custom and depthOfField is on; no limit (free input) |
| `depthOfField.aperture` | param(float) | (game default) | shown while preset is Custom and depthOfField is on; clamped 0.05..32 (slider limits read from the game's Range attribute) |
| `depthOfField.focalLength` | param(float) | (game default) | shown while preset is Custom and depthOfField is on; clamped 1..300 (slider limits read from the game's Range attribute) |
| `depthOfField.kernelSize` | param(enum) | (game default) | shown while preset is Custom and depthOfField is on; choices: Small, Medium, Large, VeryLarge |
| `grain` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `grain.enabled` | bool | true | shown while preset is Custom and grain is on; the effect's on/off switch |
| `grain.colored` | param(bool) | (game default) | shown while preset is Custom and grain is on |
| `grain.intensity` | param(float) | (game default) | shown while preset is Custom and grain is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `grain.size` | param(float) | (game default) | shown while preset is Custom and grain is on; clamped 0.3..3 (slider limits read from the game's Range attribute) |
| `grain.lumContrib` | param(float) | (game default) | shown while preset is Custom and grain is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `lensDistortion` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `lensDistortion.enabled` | bool | true | shown while preset is Custom and lensDistortion is on; the effect's on/off switch |
| `lensDistortion.intensity` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped -100..100 (slider limits read from the game's Range attribute) |
| `lensDistortion.intensityX` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `lensDistortion.intensityY` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `lensDistortion.centerX` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped -1..1 (slider limits read from the game's Range attribute) |
| `lensDistortion.centerY` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped -1..1 (slider limits read from the game's Range attribute) |
| `lensDistortion.scale` | param(float) | (game default) | shown while preset is Custom and lensDistortion is on; clamped 0.01..5 (slider limits read from the game's Range attribute) |
| `vignette` | bool | false | shown while preset is Custom; true adds the effect (Add Effect menu), false removes it (closing its header; its values are lost) |
| `vignette.enabled` | bool | true | shown while preset is Custom and vignette is on; the effect's on/off switch |
| `vignette.color` | param(color) | (game default) | shown while preset is Custom and vignette is on |
| `vignette.center` | param(vector2) | (game default) | shown while preset is Custom and vignette is on |
| `vignette.intensity` | param(float) | (game default) | shown while preset is Custom and vignette is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `vignette.smoothness` | param(float) | (game default) | shown while preset is Custom and vignette is on; clamped 0.01..1 (slider limits read from the game's Range attribute) |
| `vignette.roundness` | param(float) | (game default) | shown while preset is Custom and vignette is on; clamped 0..1 (slider limits read from the game's Range attribute) |
| `vignette.rounded` | param(bool) | (game default) | shown while preset is Custom and vignette is on |

**Settings-related validator messages**

| Mod | Section | Message |
|---|---|---|
| Minigame | details | Empty minigame description. |
| Minigame | details | Missing minigame preview image. |
| Minigame | minigameSettings | Number of rounds must be greater than zero |
| Minigame | playerSettings | Respawns are enabled with Last Man Standing end condition. |
| Minigame | minigameSettings | Respawns are enabled with Last Man Standing end condition. |
| Board | details | Empty board description. |
| Board | details | Missing board preview image. |
| Board | music | You have music set but the volume is very low. |
| Board | music | You have ambient sound set but the volume is very low. |

<!-- settings-rules:end -->

## Assets (import, import settings, materials, removal)

Plugin 0.8.0 (api v2, additive, capability `"assets"`) replaces the old rule "the plugin never imports or removes assets"
with the bounded rules below. Seven tools: `get_asset` and `list_asset_rules` (read), `import_asset`,
`set_import_settings`, `create_material`, `set_material` and `remove_asset` (write, guard **OpenMod**: an open, loaded
sandbox mod whose Meta name and folder name pass the `MCP_` rule, Editing state, writes enabled, publish block complete,
game version accepted). None of them is allowed inside `run_batch` (named reason). The recon is
`docs/architecture/07-asset-import.md`; the corrections found while building are appended there.

All five write tools refuse (`INVALID_STATE`) while an editor dialog (including "Unapplied Import Settings") is open or an
asset with unapplied import settings is selected; `import_asset`, `set_import_settings` and `remove_asset` also refuse
while a prefab is being edited (`create_material` and `set_material` also work inside a prefab). Nothing here is undoable
(`undoable: false`): files are written or deleted when the call runs, not at `save_mod`.

### Links inside a sandbox mod (every write and delete)

Review C1 (plugin 0.8.0). The game enumerates `Assets` with `GetFiles(AllDirectories)`, which follows junctions, and its
writes (`File.WriteAllBytes`, `FileMode.Create`, `File.Delete`) go straight through a junction, a symlink or a hard link.
So one central, Unity-free check, `Core/SandboxPath.cs` (`RequireInsideMod`), runs on the main thread immediately before
**every** file operation of the plugin and before every game call that writes or deletes a file for it, on every path that
call touches. It refuses when:

- the path, after normalisation, is not strictly below the mod root, or the mod root's folder name fails the `MCP_` rule
  (`GUARD_NOT_TEST_MOD`), or the mod root itself is a reparse point;
- any folder from the mod root down to the file's parent (including `Assets` and each sub-folder) is a reparse point
  (junction, directory symlink, mount point);
- the file exists and is a reparse point (file symlink) or a folder;
- the file exists with more than one hard link, or its link count cannot be determined (Win32 `GetFileInformationByHandle`).

Error `LINK_REFUSED` (links) or `INVALID_STATE`; the message names the operation and the offending path relative to the mod
and ends "nothing was written or deleted". Reading through links stays allowed: `list_assets` and `get_asset` mark such
assets `linked: true` with `linkedReason`. Paths checked per call:

| Call | Paths checked before the game call / file operation |
|---|---|
| `import_asset` | `Assets` and `Assets\<Sub>` (folders), the staging copy, the final copy, its `.pmeta`; again right before `ImportFile`; every rollback delete; the `McpRecovery` journal |
| `set_import_settings` | the `.pmeta` (rewritten), the file and `.pmeta` (and a prop's nested mesh file and `.pmeta`) that `ReimportAsset` would delete if the file were missing |
| `create_material` | `Assets\Materials`, `<unique>.pmat` + `.pmeta`, the renamed `.pmat` + `.pmeta`, the files `Save` rewrites, the cleanup's `RemoveAsset` paths |
| `set_material` | `.pmat` and `.pmeta` (rewritten in place) |
| `remove_asset` | every path `PummelMod.RemoveAsset` deletes: file + `.pmeta`, for a prop also the nested mesh's file + `.pmeta` |
| `create_prefab` | `Assets\Prefabs`, the new `.pfab` + `.pmeta` (and again right before `CreateNewPrefab`), the rename targets, the cleanup paths |
| `close_prefab` | `.pfab` + `.pmeta`, again right before the `CurrentPrefab` setter saves them; restore and delete paths |
| `save_mod`, `start_playtest` | every file `PummelMod.Save` writes (Data: Meta.json, WorkshopItem.json, Lighting.json, PostProcessing.data, MainScene.scene, MinigameDefinitionData.json, MapDetails.json, ItemDetails.json, ModSettings.json; save_mod also Preview.jpg) and, per saved asset, its `.pmeta` plus the `.pmat`/`.pfab`/`.pgf` itself: one bad target refuses the whole save, nothing written, every refused path named (up to 8) |
| `capture_preview_image` | `Assets\Textures`, the `Screenshot<N>.jpg` the editor will write and its `.pmeta` |
| `open_test_mod` | the mod root must not be a link; a file reached through a link inside `Assets` without a `.pmeta` refuses the open (loading would write that `.pmeta` through the link) |
| prefab restore | the target, the temp file, the `McpRecovery` safety copy |

Do not create junctions or links inside a mod folder; the tools refuse to write through them.

### File-system safety (import)

1. The editor's `PummelMod.AddAsset(path, folder, copy: true)` is **never called**: on an early exception it deletes the
   *source* file, and with 100 duplicates it deletes an existing `(99)` file (doc 07, H1/H2). `AddAsset(copy: false)` and
   `ImportFile` on a file outside the mod are never used either (they write a `.pmeta` beside the file).
2. The plugin copies the file itself (`Core/AssetImportCore.cs`): the source is opened read-only with
   `FileShare.ReadWrite | Delete`, read once and closed; it is never moved, renamed, changed, locked or deleted. A `.pmeta`
   next to the source is ignored.
3. The destination is always `<mod>\Assets\{Textures|Props|Audio}\<name><.ext>`; the tool takes a source path and an
   optional **name**, never a destination. The final full path must be exactly one folder below `Assets` after
   normalisation; the mod folder comes from the open mod object and its leaf name is re-checked against the sandbox rule;
   `Assets` must be `<mod>\Assets` and not a link. Names: `A-Za-z0-9 _.()-`, at most 64 characters, no `..`, no leading or
   trailing space or dot, no separators or drive letters, no control characters, no reserved device names (CON, NUL,
   COM1, ...). Without `name` the source's file name is used with other characters replaced by `_`.
4. **No overwrite.** A free name is chosen where neither the file nor `<file>.pmeta` exists (`name`, then `name (1)` ...
   `name (20)`; all taken: `ALREADY_EXISTS`, nothing copied). The copy is first written as `<name>.<ext>.mcp-import`
   (`FileMode.CreateNew`; an extension the game has no importer for, so a leftover is never decoded), hashed and
   length-checked; **every content check and cap then runs on that copy** (review M1), and only then it is renamed to its
   final name (`File.Move`, which fails if the name appeared in between: the next name is tried). Only then
   `PummelMod.ImportFile(FileInfo)` registers the file that is now inside the mod.
5. On any failure after the copy, only what this call created is deleted: the staging copy, the copy, and the `.pmeta`
   only when this call can prove it created it (it did not exist right before the import step and that step was reached;
   review L11). Each path is re-checked to be exactly `Assets\<Sub>\<name>` and to pass the link check first; if the asset
   got registered it is unregistered with `RemoveAsset`, which deletes the same two files. The reply is `IMPORT_FAILED`
   with `error.data.cleanedUp`; a content check that fails on the copy is `INVALID_PARAMS` ("checked on the copy"). The
   importer writes the `.pmeta` **before** it decodes: right before `ImportFile` the plugin writes
   `<mod>\McpRecovery\import-pending.json` and removes it right after; if the game dies during the decode, the next plugin
   start (and `open_test_mod`) renames the copy to `<name>.<ext>.mcp-import-failed` (and its own `.pmeta` to
   `...mcp-import-failed.pmeta`) after checking its SHA-256, so the mod is not decoded again on every reopen. The OBJ
   importer's "empty list, no cleanup" failure is handled the same way; `.bmp` (offered by the editor menu, no importer) is
   refused up front.
6. Allow-list: `.png .jpg .jpeg` (textures), `.obj` (meshes), `.wav .mp3 .ogg` (audio); case-insensitive on input, the
   copy gets a lower-case extension (the audio importer switches on the exact-case extension). The content is sniffed
   before anything is copied: PNG signature and header, JPEG markers, RIFF/WAVE, OggS, MP3 ID3 tag or frame sync, OBJ as
   text (no NUL byte in the first 8 KB) with at least one `v ` line. A renamed file (a PNG called `.wav`, a text file
   called `.png`) is refused; zero-byte files are refused.
7. Limits: `[Assets] MaxImportFileMB` (default 50), `MaxModAssetsMB` (default 500, total size of the mod's `Assets`
   folder after the import), `MaxAudioSeconds` (default 600: the clip's duration read from the WAV `fmt`/`data` chunks,
   the OGG Vorbis/Opus header and last granule position, or the MP3 Xing/Info/VBRI frame count; an MP3 without a frame
   count is also refused when its first-frame bitrate implies more) and `MaxCompressedAudioMB` (default 15: the size limit
   for an OGG/MP3 whose header gives no duration; a WAV without a readable header is refused), edited in the plugin config
   and re-read on every call; plus constants: texture sides <= 8192 px (read from the header), OBJ vertex lines <= 500,000
   and face lines <= 1,000,000, name <= 64 characters, at most 20 numbered duplicates. The editor has none of these; the
   audio decode busy-waits on the main thread.
8. Sources: any readable **local absolute path** (`C:\...`). Refused in this version: UNC (`\\server\share`), device
   (`\\.\`, `\\?\`) and network-drive paths, alternate data streams, links/junctions, folders, relative paths. Reason: the
   audio loader and the native OBJ loader are unverified on them (the copy itself would be safe); this is a limitation
   that can be lifted later. A source inside the open mod's own `Assets` folder is refused (nothing to import). A source
   inside **another** mod's folder is allowed (read-only by construction). The game's loaders only ever see the
   plain-ASCII destination name below the mod folder; the mod **folder** path itself may contain non-ASCII characters,
   which the audio loader (raw path string, not a URI) may not handle [unverified in the game].
9. Time: the copy and the registration run on the main thread inside the call (the guard and the mod folder are only known
   there; at most one file of the configured size). Commit point: a timeout before the copy starts is safe to retry
   ("nothing was changed"); after it the call returns its own result. The time a call needs left at its commit point is
   sized for the work after it (review L7, `Core/CommitBudgets.cs`, nothing measured in the game): import 90 s (limit
   120 s), set_import_settings 20 s, create/set_material and remove_asset 10 s (limit 30 s), create/close_prefab 15 s and
   open_prefab 10 s (limit 30 s), capture_preview_image 15 s (limit 25 s), save_mod 12 s (limit 20 s). If the main thread
   stalls beyond the limit anyway, the reply says "the change was applied" (never a silent TIMEOUT). The MCP server waits at
   least the limit + 4 s commit grace + a margin.

### `list_asset_rules` (read)

No params. Returns `import` (extensions, destination folders, live limits from the config, caps, source rules),
`importSettings` per type, `materials.shaderRules` (properties per shader with type, limits, conditions) and
`materials.builtInShaderAssets` (`{guid, name}` of every built-in Shader asset). The block under
[Generated asset rules](#generated-asset-rules) is the same table.

### `get_asset` (read)

`{guid}` (any asset except runtime entries). Returns the `list_assets` row (`guid`, `name`, `type`, `className`,
`location`, `relativePath`, `fileExists`, `sizeBytes`), `ownCustomAsset` (external and inside the open mod's `Assets`),
and for the mod's own assets: textures `width`/`height`/`importSettings`/`importSettingFields`; props `vertices`,
`triangles`, `subMeshes`, `importSettings`; audio clips `lengthSeconds`, `frequency`, `channels`; prefabs `isOpen`, `objectCount`, `objects` (the prefab's object tree in the
`get_hierarchy` node shape; the guids work with `get_object`), `instanceCount` and `instances` (`[{guid, name}]`: Prefab
Instance objects in the scene that use it; spawners, items and actions that use it are in `references`); materials `shader`,
`knownShader`, `properties` (in the shapes `set_material` accepts) and `propertyFields`; `removable` (+ `removableReason`);
`references`: `{count, items: [{kind, owner, ownerGuid?, where}], scanComplete, settingsScanFailed, objectsScanned,
scanned}` where `kind` is `component`, `action`, `playerTrigger`, `material`, `setting` or `workshop`. A Prefab Instance holds
one reference that the reader shows twice (`PrefabReference` and `PrefabGUID`); it is listed and counted once per instance. The same
holds for a Spawner's prefab slots (`Prefabs[i]` and `PrefabGUIDs[i]`: once per slot) and an Item's `ItemPrefab` / `ItemPrefabGUID` and
`DroppedItemPrefab` / `DroppedItemPrefabGUID`. The scan (review
H1, `ReferenceCollector.Locations`) reads every scene and prefab object with the plugin's own value converter (so it finds
what `get_object` shows: component properties, action parameters and targets, nested Display Popup choice lists), the
Minigame player-settings triggers (the six Event Triggers and the hidden `hitTrigger`, which are not scene objects),
materials' texture slots, the settings the settings tools know (music, ambience, preview image, board custom spaces), an
Item mod's prefabs (use, receive, held) and icon, and the Workshop item's thumbnail and preview files (read only).
`scanComplete: false` means something could not be read or an array was cut off (`truncatedItems`, review L1): the scan
is then not a proof of "unused" and counts as "in use". `linked: true` marks a file that runs through a link (see
[Links](#links-inside-a-sandbox-mod-every-write-and-delete)). Needs an open mod; edit mode.

### `import_asset` (write, guard OpenMod)

| Param | Type | |
|---|---|---|
| `path` | string | required, absolute local path |
| `name` | string | asset name, see rule 3; default derived from the file name |
| `type` | `Texture`, `Prop`, `AudioClip` | optional; must match the file |

Timeout 120 s. Reply: `asset` (the `list_assets` row of the new asset: `guid`, `name`, `type`, `className`, `location:
"external"`, `relativePath`, `fileExists`, `sizeBytes`), `copy` (`relativePath`, `sizeBytes`, `sha256` of the copied
bytes, equal to the source's), `source` (`path`, `sizeBytes`, `modified: false`), `nameDerived`, `renamedBecauseExists`,
`checks` (the pre-checks on the source and the `copy:` checks on the copy), `checkedOnCopy: true`, `limits` (incl.
`maxAudioSeconds`, `maxCompressedAudioMB`), `modAssetsBytesAfter`, `undoable: false`, `note`. Errors:
`INVALID_PARAMS` (path/name/type, extension, content, size, caps, duration), `NOT_FOUND` (source missing), `INVALID_STATE`
(dialog, prefab edited, `Assets\<Sub>` is a file), `LINK_REFUSED` (a link on the way), `ALREADY_EXISTS` (no free name), `IMPORT_FAILED` (the game's
importer rejected the file; the copy was removed). The game may also create an empty `PummelModCache\<id>` folder in its
own data folder for PNG/JPG imports (directory only, hazard H17). A neighbouring `.mtl` is not copied.

### `set_import_settings` (write, guard OpenMod)

`{guid, values}` for the open sandbox mod's own textures and props, fields as in the generated table (the inspector's
fields and choices; `compression` also accepts 0..2, `filterMode` 0..2, `wrapMode` 0..3). Everything is validated first.
Applied like the inspector's **Apply**: the field is set, the `.pmeta` written and, for `compression` and
`recalculateNormals`, the file re-imported (`EditorSystem.ReimportAsset`, which re-checks that the file exists; the file
is checked before). `filterMode` and `wrapMode` are set on the existing `Texture2D` in place and the `.pmeta` is written
(a re-import would only produce the same values in a new object).

**What a re-import replaces (found in the source, doc 07 H8 confirmed):** a texture re-import decodes into a **new**
`Texture2D` and the asset list swaps it in; things that hold the old object by identity (component properties, action
parameters, the preview image) would not find the asset any more and would be saved as "none". The tool therefore
re-points materials' texture slots itself and **refuses a compression change with `ASSET_IN_USE`** (references listed)
while anything else uses the texture. For props the registered `Prop` object keeps its identity (only its mesh is
replaced), so references survive. Objects already placed in the scene keep showing the previous mesh until the mod is
reopened (`ModProp.Prop` only updates the mesh filter when the Prop object changes; reply `warnings`). A failed re-import is detected by the Prop's materials array: a successful
`PummelOBJImporter.Load` always assigns a new `prop.mats` array, while every failure path (error log, empty result,
discarded by `ReimportAsset`) returns before that, so an unchanged array means the re-import did not take effect and the
previous values are restored (the old check on the Mesh reference was wrong: PummelOBJImporter.Load looks up the nested PummelMesh in the asset list, but that nested asset is never registered, so every re-import builds a NEW Mesh; it is stored in the freshly deserialised PummelProp's MeshAsset, and ModAssetList.AddAsset never copies m_meshAsset onto the registered asset, so it was always "unchanged"). Reply: `applied`, `previous`,
`unchanged`, `reimported`, `materialSlotsRepointed`, `undoable: false`.

### `create_material` and `set_material` (write, guard OpenMod)

`create_material {name?, shader?, properties?}`: calls the editor's own `EditorSystem.CreateNewMaterial()`, which writes
`Assets\Materials\<Material_N>.pmat` and its `.pmeta` immediately (default shader: the editor's
`defaultShaderGuid`; `shader` = the name or guid of a built-in Shader asset: a name is matched case-insensitively against
the built-in **asset** names first, then against the Unity shader names; an unknown name is refused with `INVALID_PARAMS`
listing the valid asset names). The reply's `shader` is the Unity shader's own name and `shaderAsset` the asset's name; when they
differ `shaderNote` says so. Observed in a test: asking for `shader: "Standard Shader"`
returned `shader: "Standard Decal Receiver"`. Asset names are matched first and the reply reports the Unity shader's own name, which
explains it (the asset's own name was not read in code; the editor's default shader also reports "Standard Decal Receiver"). Before the call the plugin asks the editor
which name it will use (`GetUniqueAssetName`, a check against *asset names* only) and refuses with `ALREADY_EXISTS` when
that file or its `.pmeta` already exists on disk without a loaded material (the editor would overwrite it); a custom `name`
is applied afterwards with the editor's own rename (both target names are checked free first). The rename counts as done
only when both files are at the new name, nothing is left at the old one and no editor dialog appeared (review L8,
`RenameGuard`); a half-done rename (file moved, `.pmeta` not) is moved back only when provably safe (the moved bytes equal
the recorded ones, both paths pass the link check), otherwise the leftover is named. A failed rename, like any failure
after creation, fails the call and removes the new material again. `set_material {guid, shader?, properties?}` changes only the open mod's **own** materials
(never built-in ones). Properties are validated per shader from the generated table before anything changes (unknown key,
wrong type, a texture that is not a texture asset, a conditional property whose condition is false: all refuse the call),
clamped like the inspector (ranges to the **shader's own range**, read at run time; colours 0..1; emission up to 64) and
reported in `clamped`. After the properties the texture keywords and, for the standard shaders, `UpdateKeywords` (blend
keywords, queue, render type for the rendering mode) run, as the inspector does on every draw. The material is then
**saved at once** (`PummelMaterial.Save()`: `.pmat` + `.pmeta`, what the inspector's Apply writes) — it does not wait for
`save_mod`. Changing the shader of an existing material drops properties the new shader lacks, and conditional
properties must then be set in the same call. Shaders **without a known inspector** (anything but the four rule entries)
can be chosen, but their properties are refused (the generic inspector is not supported). Replies carry `properties`
(read back), `previous` (set_material), `clamped`, `persisted: true`, `undoable: false`. Assigning a material works with
the existing tools: `set_property` on a renderer's `materials` array or a prop's `customMaterials` with the material guid
(`[{"guid": "<material guid>"}]` or a guid string per element; see [set_property](#set_property-and-set_properties-write)).

### `remove_asset` (write, guard OpenMod)

`{guid, force?}`. Allowed only when **all** hold: the asset is external, in the open mod's asset list and its **full
file path is strictly inside** the mod's `Assets` folder ("external" alone is not enough, doc 07 H11); its class is a
texture, prop (mesh), audio clip, material or **prefab** (`.pfab`); it is not a built-in asset, not the open prefab, not selected with unapplied import settings;
for a prop the nested mesh asset points at the same file (the editor's removal deletes that path too); and it is not
the mod's preview image (never removed, even with `force`: set it to null or another texture first); and the mod settings
could be read (if that scan throws, the asset may be the preview image, so it is not removed even with `force`; review
L3). Every path the removal deletes passes the link check (`LINK_REFUSED` otherwise). The editor's own
`PummelMod.RemoveAsset` is then called, which destroys the Unity object, deletes `<file>` and `<file>.pmeta` and unlists
the asset; a selected (non-dirty) asset is deselected first. Before that the references are scanned (see `get_asset`): any
reference, or an incomplete scan, refuses the call with `ASSET_IN_USE` (`error.data` = the references) unless
`force: true`; with `force` the reply lists `referencesNulled`. What the game does (code): `PummelPrefab.OnDestroy` sets `PrefabReference = null` on every Prefab Instance found by `GetComponentsInChildren` under the scene hierarchy root at once (they show "Missing Prefab"); instances inside other prefabs are not on that walk, and inactive objects probably are not either (`includeInactive` is not passed). Other references may still point at the destroyed asset in memory until the mod is reopened; read tools (`get_object`, `get_action_graph`) show such a reference from its stored guid (`{guid, missing: true}`, or `null` when none is stored), not as the removed asset, because an asset value is shown as an asset only while it is the registered asset (see [References](#references); earlier builds showed the removed asset's name and `location: external`, seen for a Spawn Prefab action parameter; `{guid, missing: true}` was seen for a Spawner component's `Prefabs`, whose `PrefabGUIDs` keeps the guid). Seen in the game for an action parameter (Spawn Prefab `m_prefabs[0]`, plus `m_assetGUID` entries of `MainScene.scene`): the removed guid stays in the saved scene file and the reference reads as `{guid, missing: true}` after a reopen (the game copes: "prefab is null. Not spawning mod prefab."); other kinds of references were not checked in the game. `ModAssetReference.OnBeforeSerialize` re-derives the guid from the held object, so why the guid stays is not established from the code; on load `GetAsset` sets the asset to null when the guid is not in the list. "Saved as none" is therefore not what was seen. Also seen: after a forced material removal a material list read `[null]` rather than an empty list, and an `undo` logged "Couldn't find object with guid: <removed>" once in the game log. That the undo restores nothing for that reference is inferred from the code (`Extensions.ReadObject` returns null for an unknown guid), not observed. The plugin changes none of this. For a prefab the scan covers Prefab Instance objects in the scene, Spawner prefab
lists, item prefabs, Spawn Prefab / Give Minigame Item / Set Player Visual action parameters (also inside the player-settings
triggers and nested popup choices), an Item mod's use/receive/held prefabs and settings; for a texture also materials'
texture slots, the preview image and the Workshop item's thumbnail and preview files (`kind: "workshop"`: refused without
`force`; with `force` the Workshop item keeps a dangling guid, which the plugin never writes); references held by
objects inside the prefab itself (an item prefab naming itself) are ignored, because they go away with it. The editor's removal
(`PummelPrefab.OnDestroy`) sets every scene instance's reference to none (they show "Missing Prefab" and are saved without a
prefab); the prefab's undo stack stays in the editor's list (harmless). Reply: `removed`, `deletedFiles`, `undoable: false`, `note`.

### Reply fields common to the write tools

`undoable: false`; `persisted`/`note` say what is on disk now. No record of who imported an asset is kept: the boundary
is the editor's own split between the mod's custom assets and built-in assets, checked by path.

### Verified and unverified in the game

Confirmed in the game (Phase 4 test run, plugin 0.8.0, game 1.14.1k): the lighting presets
Day, Default, Evening, Forest, Hell, Maple, Night, Outer Space, Spring, Sunrise, Vaporwave, Watermelon, Winter (+ Custom) and the
screen-effect presets Ancient TV, Default, Old TV, Vibrant Cool, Vibrant Warm (+ Custom); lighting and screen-effect changes show
at once without the settings window open; `capture_preview_image` writes `Assets/Textures/Screenshot1.jpg` (1920x1080) and its
`.pmeta` at once and assigns it; new materials get the shader "Standard Decal Receiver" and renaming a material raises no dialog;
Cutout and Fade store both `_ALPHATEST_ON` and `_ALPHABLEND_ON` (what the game's own inspector does); WAV decoding and
`lengthSeconds`; OBJ import (a cube: 24 vertices, 12 triangles); `save_mod` with a hard-linked `.png` present succeeds (it writes only the `.pmeta`); an empty
`McpRecovery` folder stays in a sandbox mod after an import.
An OBJ without face lines imported as an empty Prop (0 vertices, 0 triangles) in that run, so `import_asset` now refuses it.

Still not confirmed in the game: the audio and OBJ loaders on non-ASCII mod folder paths, what `LoadObjFile` does with other
malformed text, MP3 and OGG decoding and their duration limits, the shipped shader property ranges, the value of
`defaultShaderGuid`, the compression refusal for a texture used only by a player-settings trigger, whether the open settings
window re-clamps values the plugin wrote, and the visual effect of a mesh re-import on placed objects (from the code they keep the previous mesh until the mod is
reopened; not looked at in the game). The in-game test script is
`docs/phase4-ingame-tests.local.md` (local).

### Generated asset rules

<!-- asset-rules:begin (generated by AssetRules.ToMarkdown(); do not edit by hand) -->

### Asset import rules (generated)

Importable files: .png, .jpg, .jpeg, .obj, .wav, .mp3, .ogg (case-insensitive; the copy gets a lower-case extension). Destination folders inside the mod's `Assets` folder: Textures (png, jpg, jpeg), Props (obj), Audio (wav, mp3, ogg).

Constant caps: name <= 64 characters from `A-Za-z0-9 _.()-`; texture side <= 8192 px; OBJ vertex lines <= 500000, OBJ face lines <= 1000000; at most 20 numbered duplicates (`name (1)` ...). Size limits are configurable: `[Assets] MaxImportFileMB` (default 50) and `MaxModAssetsMB` (default 500). Audio: `MaxAudioSeconds` (default 600, read from the WAV/OGG/MP3 header) and, for an OGG/MP3 whose duration the header does not give, `MaxCompressedAudioMB` (default 15). The content checks run on the copy inside the mod (written as `<name>.mcp-import`, renamed only after they pass).

#### Import settings (`set_import_settings`)

| Asset type | Field | Choices | Default | Effect |
|---|---|---|---|---|
| Texture | `compression` | Normal, HighQuality, None | Normal | applied when the image is decoded: compressed only if not None and both sides are multiples of 4. Needs a re-import (new texture object), see set_import_settings |
| Texture | `filterMode` | Point, Bilinear, Trilinear | Bilinear | set on the Texture2D (applied in place, no re-import) |
| Texture | `wrapMode` | Repeat, Clamp, Mirror, MirrorOnce | Repeat | set on the Texture2D (applied in place, no re-import) |
| Prop | `recalculateNormals` | true, false | false | normals are recalculated when the mesh is re-imported (always done when the OBJ has none). Needs a re-import; objects already in the scene keep showing the previous mesh until the mod is reopened |

Audio clips, materials and prefabs have no import-setting fields (materials: `set_material`).

#### Material properties (`create_material`, `set_material`)

**`Standard`, `Standard Decal Receiver`** (inspector `StandardShaderInspector`)

| Key | Type | Shown when | Notes |
|---|---|---|---|
| `_Mode` | one of Opaque, Cutout, Fade, Transparent | always | Opaque, Cutout, Fade, Transparent; sets blend keywords, queue and render type like the inspector |
| `_Cutoff` | number 0..1 | _Mode = Cutout | Alpha Cutoff |
| `_MainTex` | Texture asset guid or null | always | Albedo |
| `_Color` | color {r,g,b,a} 0..1 | always | Color |
| `_MetallicGlossMap` | Texture asset guid or null | always | keyword _METALLICGLOSSMAP follows the texture |
| `_Metallic` | number (shader range; documented 0..1) | always | Metallic |
| `_Glossiness` | number (shader range; documented 0..1) | always | Smoothness |
| `_BumpMap` | Texture asset guid or null | always | keyword _NORMALMAP follows the texture and _BumpScale is set to 1 |
| `_ParallaxMap` | Texture asset guid or null | always | keyword _PARALLAXMAP follows the texture |
| `_Parallax` | number (shader range; documented 0.005..0.08) | _ParallaxMap has a texture | Height |
| `_OcclusionMap` | Texture asset guid or null | always | Occlusion |
| `Emission` | bool | always | keyword _EMISSION |
| `_EmissionColor` | color {r,g,b,a} 0..64 (HDR) | Emission = true | Emission Color |
| `_EmissionMap` | Texture asset guid or null | Emission = true | Emission map |
| `Tiling` | vector {x,y} | always | Material.mainTextureScale |
| `Offset` | vector {x,y} | always | Material.mainTextureOffset |

**`Custom/ModBoardSpaceShader`** (inspector `CustomBoardSpaceInspector`)

| Key | Type | Shown when | Notes |
|---|---|---|---|
| `_MainTex` | Texture asset guid or null | always | Albedo |
| `Tiling` | vector {x,y} | always | Material.mainTextureScale |
| `Offset` | vector {x,y} | always | Material.mainTextureOffset |
| `_EdgeColor` | color {r,g,b,a} 0..1 | always | Edge Color |
| `_InsideColor` | color {r,g,b,a} 0..1 | always | Inside Color |
| `_IconColor` | color {r,g,b,a} 0..1 | always | Icon Color |
| `_Metallic` | number (shader range; read at run time) | always | Metallic |
| `_Glossiness` | number (shader range; read at run time) | always | Smoothness |
| `_IconScale` | number (shader range; read at run time) | always | Icon Scale |

**`Particles/Additive Intensify Mod`** (inspector `ParticleShaderInspector`)

| Key | Type | Shown when | Notes |
|---|---|---|---|
| `_TintColor` | color {r,g,b,a} 0..1 | always | Tint Color |
| `_MainTex` | Texture asset guid or null | always | Particle Texture |
| `Tiling` | vector {x,y} | always | Material.mainTextureScale |
| `Offset` | vector {x,y} | always | Material.mainTextureOffset |
| `_InvFade` | number (shader range; read at run time) | always | Soft Particles Factor |
| `_Glow` | number (shader range; read at run time) | always | Intensity |

**`Legacy Shaders/Particles/Alpha Blended Mod`** (inspector `AlphaBlendedShaderInspector`)

| Key | Type | Shown when | Notes |
|---|---|---|---|
| `_TintColor` | color {r,g,b,a} 0..1 | always | Tint Color |
| `_MainTex` | Texture asset guid or null | always | Particle Texture |
| `Tiling` | vector {x,y} | always | Material.mainTextureScale |
| `Offset` | vector {x,y} | always | Material.mainTextureOffset |
| `_InvFade` | number (shader range; read at run time) | always | Soft Particles Factor |

Shaders without a known inspector are accepted by `create_material`/`set_material` for the shader choice only; their properties are refused (the generic inspector is not supported).

<!-- asset-rules:end -->

## Prefabs (create, open, close, the .pfab write protection)

Plugin 0.8.0 (api v2, additive, capability `"prefabs"`). Three write tools, guard **OpenMod** (open, loaded `MCP_` sandbox mod,
Editing state, writes enabled, publish block complete, game version accepted): `create_prefab`, `open_prefab`, `close_prefab`.
None is allowed inside `run_batch`; edits inside an already open prefab are batched like any other edit. Prefab removal is
`remove_asset` (see [Assets](#assets-import-import-settings-materials-removal)); there is no rename or duplicate of prefabs.
All refuse (`INVALID_STATE`) while an editor dialog is open or an asset with unapplied import settings is selected.

### The truncated-file hazard and how every prefab write is protected

`PummelPrefab.Save` truncates the `.pfab` (`File.Open(path, FileMode.Create)`) and serialises straight into the file stream
(`ModHierarchySerializer.SaveTransform`). A component whose serialisation throws, a full disk or a crash leaves a truncated file,
and a truncated prefab makes the **whole mod fail to load** (`PummelPrefabImporter` reads past the end). The game's own paths that
write a prefab: `Create > Prefab` (`EditorSystem.CreateNewPrefab`), leaving the prefab context (the `CurrentPrefab` setter), the
Apply/Reimport button, and every `PummelMod.Save`. The plugin wraps the four it triggers (create, close, `save_mod`, and the synchronous save inside `start_playtest`, review C2):

1. **Dry run** (before anything is written): the same `SaveTransform` call runs against a `MemoryStream` for the in-memory
   prefab (it writes no file; the editor's own undo snapshot makes the same call every frame). Any exception refuses the
   operation (`INVALID_STATE`, the prefab and the cause named, nothing written). For `create_prefab fromObject` the source
   subtree is dry-run (the copy serialises like its source); the new prefab itself is dry-run right after creation.
2. **Safety copy**: the previous `.pfab` and `.pmeta` bytes are read into memory (limit 64 MB per file; no temp file is made
   before the write). A prefab that did not exist is recorded as new.
3. **Verification** after the editor wrote: the `.pfab` exists; starts with the header `03 'P' 'M' 'H' 01 00 00 00` (identifier
   `PMH`, version 1.0, what `ModHierarchySerializer.SaveVersion` writes); is at least 19 bytes; has exactly the length of the
   dry run (identical bytes, or identical except the root object's active flag, are reported as `identicalToSerialisation`;
   same length but other content is kept with a warning); was actually rewritten when it should have been; and the `.pmeta` is
   non-empty valid JSON. There is no read-only parse entry in the game (`LoadTransform` instantiates objects and touches editor
   state), so the length equality with the in-memory serialisation is the completeness proof; a structural parse is not done.
4. **Repair** on any failure: an existing prefab gets its previous bytes back (written to `<file>.mcp-restore.tmp` in the same
   folder, swapped in with `File.Replace` - or, in a runtime without `File.Replace` (`PlatformNotSupportedException` /
   `NotImplementedException`), with `File.Copy(overwrite)`, the strategy of `Core/TokenAuth.cs` (review L5) - verified, temp
   removed even on error; every path passes the link check first); a new prefab's `.pfab` and `.pmeta` are
   removed, after re-checking that each path is strictly inside the mod's `Assets` folder and is a `.pfab`/`.pmeta`
   (the same rules as the asset import cleanup); the registered asset of a failed `create_prefab` is unregistered
   (`PummelMod.RemoveAsset`, which also destroys the copy). The error says what was restored or removed. If even the restore
   fails, the error says **RESTORE FAILED** loudly and the safety copy is kept on disk - only in that case - as
   `<mod>McpRecovery<file>.<time>.mcp-snapshot` (a folder the game never reads; `preservedSnapshots` in the reply): copy
   it back by hand before reopening the mod. No temp file is left behind.

`ReimportAsset(prefab)` is never called (it destroys the in-memory prefab before it parses the file, doc 07 H6).

### `create_prefab` (write, guard OpenMod)

`{name?, fromObject?}`. Needs the scene context (a prefab being edited: `INVALID_STATE`, call `close_prefab` first). Calls
`EditorSystem.CreateNewPrefab(ModTransform)`: an empty prefab, or - with `fromObject` (a scene object guid) - an instantiated
copy of that object and its children (the Asset Browser drag-and-drop). Refused: a subtree containing a Prefab Instance or Board
Space component (the editor's own rule); a subtree whose components or actions hold references to **objects** (action targets,
object properties): the editor copies those references unchanged (the copy gets fresh object and component guids, but the stored
references are not remapped when a prefab is created, only when an instance is made), so they would keep pointing at scene objects
- the plugin refuses and lists them (`INVALID_PARAMS`) instead of letting them break silently; a subtree whose component values
could not all be read or were cut off (`INVALID_STATE`: references cannot be ruled out, review M2); a subtree that cannot be
serialised. The game's `FullPrefabsFolder` must be `<mod>AssetsPrefabs` (review L4).
References to assets (materials, textures, sounds) are fine. The source objects stay exactly as they were in the scene (no
instance replaces them; `create_object kind prefabInstance` places one). The prefab root is reset to position 0, rotation 0,
scale 1; its active flag follows the source; it is deactivated in memory afterwards like every closed prefab.

Names: the editor names the file from asset names only (case-sensitively) and would overwrite an existing file that has no
loaded asset (NTFS is case-insensitive). The plugin asks the editor which name it would use (`GetUniqueAssetName`), refuses if that
file (or its `.pmeta`) exists on disk or the name has characters outside `A-Z a-z 0-9 space _ . ( ) -` (rename the source object
first with `set_object`), and picks a free name itself: `name` (or the source object's name, or `Prefab_<n>`), then `_1`..`_99`,
checked against asset names (case-insensitive) and files. `nameAdjusted` is true when a requested name had to change. A name
different from the editor's is applied with `ModAssetExternal.Rename` afterwards (both target files checked free first). A rename
that does not complete (file moved without its `.pmeta`, an editor dialog, nothing moved) fails the call with `INTERNAL`: the
prefab exists and is verified under the editor's name (`error.data.created`), a half-done move is undone only when provably safe
(`movedBack`), otherwise the leftover is named (`orphanFile`); it is never reported as success (review L8).

```json
{"asset": {"guid": "...", "name": "Crate", "type": "Prefab", "relativePath": "Assets/Prefabs/Crate.pfab", "sizeBytes": 412, "fileExists": true},
 "requestedName": "Crate", "nameAdjusted": false, "fromObject": {"guid": "...", "name": "Crate"},
 "objects": {"guid": "...", "name": "Crate", "active": true, "components": [], "children": []},
 "written": {"files": ["Assets/Prefabs/Crate.pfab", "Assets/Prefabs/Crate.pfab.pmeta"], "sizeBytes": 412, "verified": true, "identicalToSerialisation": true, "immediately": true},
 "persisted": true, "undoable": false, "note": "..."}
```

The files exist on disk when the call returns, even if `save_mod` is never called. Not undoable (remove with `remove_asset`).
Errors: `INVALID_PARAMS` (bad name, source rules), `INVALID_STATE`, `ALREADY_EXISTS`, `INTERNAL` (the editor failed or wrote a bad file;
the message says what was cleaned up), `TIMEOUT` (nothing changed before the commit point).

### `open_prefab` (write, guard OpenMod)

`{guid}`: enters the edit context like a double-click in the Asset Browser (`EditorSystem.CurrentPrefab = prefab`: clears the
selection, remembers the scene camera, activates the prefab object, hides the scene). Only prefabs of the open sandbox mod
(own custom asset, file inside `Assets`) can be opened. **Refused while another prefab is open** (opening one from the editor saves
the open one without the instance refresh and without the plugin's protection). No file is written. The reply has `opened`
(the asset row), `context: "prefab"`, `objects` (tree), `undoStackLength` (this prefab's own stack) and a warning when the prefab
cannot be serialised right now (`close_prefab`/`save_mod` would refuse).

### `close_prefab` (write, guard OpenMod)

No params. Needs an open prefab (`INVALID_STATE` otherwise). Runs the sequence of the UI's "Return to Hierarchy" handler
(`HierarchyWidget`): clear selection, `isEnabled` from the active state, `ModPrefabInstance.ForceUpdate` on **every instance in
the scene** that references the prefab (the only place the editor refreshes live instances), clear selection, then
`CurrentPrefab = null`, whose setter **saves the prefab** (`.pfab` + `.pmeta`) and hides it. The UI's final `ReimportAsset` is
skipped (see above). The dry run and the safety copy happen before any change; the verification and repair after. Reply:
`closed` (`guid`, `name`, `relativePath`), `context: "scene"`, `savedToDisk: true`, `file` (`sizeBytes`, `verified`,
`identicalToSerialisation`), `instancesRefreshed`, `instances` (`[{guid, name}]`), `reimported: false`, `undoable: false`. On a
failed write the call fails with `INTERNAL`: the previous file is back, the edits are still in memory (the message says whether the
prefab is still open), and you should fix the cause and retry; if refreshing instances fails the prefab stays open and nothing is saved.
The prefab's undo history stays in the editor and is available again after `open_prefab` (it is not on disk). The plugin's
unsaved-edits flag stays set until `save_mod` (the file is saved, the mod is not).

### Which tools work while a prefab is open

| Tool | Prefab open |
|---|---|
| `get_status` | `prefabContext: {name, guid}` |
| `get_hierarchy` | `context: "prefab"`, the prefab object is the root; `get_object` / `get_action_graph` work on prefab objects (open or not) |
| `create_object`, `duplicate_object`, `delete_object`, `reparent_object`, `set_object`, `set_transform`, `add_component`, `remove_component`, `set_property(ies)`, action/trigger tools | work on the prefab's objects; the prefab root cannot be moved or deleted; `create_object kind prefabInstance` and `boardSpace` and the components Prefab Instance / Board Space are refused inside a prefab (the editor disables them); objects of the scene are `INVALID_STATE` ("a prefab is being edited") |
| `undo` | the open prefab's own undo manager (`EditorSystem.GetUndoManager()`); the scene's stack is untouched |
| `save_mod` | works (also writes the open prefab, protected) |
| `run_batch` | edits inside the open prefab may be batched; `create_prefab`, `open_prefab`, `close_prefab` may not |
| `create_prefab`, `open_prefab` | refused (`INVALID_STATE`; `close_prefab` first) |
| `import_asset`, `set_import_settings`, `remove_asset` | refused (`INVALID_STATE`) |
| `create_material`, `set_material`, mod settings tools | allowed (they do not touch the prefab); `capture_preview_image` refused |
| `create_test_mod`, `open_test_mod` | refused (leaving the mod would save the prefab outside the protection) |
| `start_playtest` | refused (the Play button would save the prefab and leave the context); `validate_mod` works but does not see scene objects |

### Verified and unverified in the game

Confirmed in the game (Phase 4 test run): a prefab made from scene objects gets new object and component guids, its root is at
0/0/1 and renamed to the prefab name, root `active` is false while the prefab is not open and true while it is open;
`duplicate_object` on the prefab root puts the copy inside the prefab
as a child of the root; `close_prefab` refreshed 2 instances without a problem and the scene undo stack is untouched by prefab
edits; with a read-only `.pfab`, `close_prefab` and `save_mod` return `INTERNAL`, the bytes stay unchanged, the edits are kept
and a retry works.

Not confirmed in the game: the awake side effects of the scene-subtree copy with live components in every case, the exact
bytes the real serializer writes (the header is read from the game's static constructor), `ForceUpdate`
on many instances, and the visual result of closing a prefab. The in-game script is `docs/phase4-ingame-tests.local.md`
(local), section C.

## Validation, playtest and batch

Increment 4. Read tool `validate_mod`; write tools `start_playtest`, `stop_playtest`, `run_batch`. Plugin 0.6.0 adds the read tools
`get_playtest_state` and `get_playtest_events` ([Playtest visibility](#playtest-visibility)).

### `validate_mod` (read)

No params. Errors: `EDITOR_NOT_OPEN`, `BUSY_LOADING`, `NO_MOD_OPEN`, `INVALID_STATE` (a playtest is starting, running or
ending), `INTERNAL` (the validator threw, or its validator list is incomplete, see below). Works on any open mod.

Runs the editor's own Mod Validator: `EditorSystem.ValidateCurrentMod()` → `ModValidationSystem.ValidateCurrentMod()`,
which calls every `ModValidationCallbackReceiver` (`ModMinigameValidator` for Minigame mods, `ModBoardValidator` for
Board mods; Item and BoardSpace mods have no validator and are always valid). This is the same call behind
File > Validate Mod, the toolbar's Play button and the Publish dialog (which enables its publish button only if the
result has no errors).

```json
{"mod": "MCP_Arena", "modType": "Minigame", "valid": false, "canPlaytest": false, "canPublishPerValidator": false,
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
  only when set (the validators set none); `fixable`: the editor offers a "Fix" button (the plugin never runs fixes).
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
incomplete for the whole session (also for the editor's own Play/Publish checks); the plugin therefore checks after
every run that both validators are listed and otherwise returns `INTERNAL` ("restart the game"). Hence a read tool that
works on any open mod.

#### What a Minigame mod needs to pass the validator (`ModMinigameValidator`)

Errors (block Play without "Run With Errors", and publishing):

1. Minigame Settings `NumberOfRounds` > 0 (default 1).
2. At least one **active** Player Spawn (`kind: "playerSpawn"`) in the scene (inactive objects and deleted objects are
   not seen; the validator also sees active spawns anywhere in the loaded scenes).
3. For every player count 1..8: an initial-spawn Player Spawn whose `PlayerCountMask` includes it.
4. For every player 1..8: an initial-spawn Player Spawn whose `AllowedPlayers` includes that player.
5. At least one initial spawn with `SharedSpawn` true, or at least 8 non-shared initial spawns (an error when there is no shared
   initial spawn and fewer than 8 non-shared ones). With `SharedSpawn` the same spawn can be picked by several players; with the Point
   shape they start on the same point (seen in a test: with punching on they hit each other at round start). The Line and Circle shapes
   place players by index (Line with Step distribution) and are an alternative to turning `SharedSpawn` off.
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
`WorldGround`-layer collider below them. The plugin cannot edit board connections yet, so a Board mod cannot be made
valid through the plugin.

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

1. Refusals: a create/open load pending (`BUSY_LOADING`); an editor dialog open; a prefab being edited (the plugin never
   saves a prefab implicitly: call `close_prefab` first); a BoardSpace mod (it has no play scene: the editor would
   hide itself and stay in `EnteringPlayMode`); `WorkshopController.CurrentBackgroundLoadMod != null` or the toolbar's
   1 s background-load timer running ("Subscribed mods are being installed in the background", as the disabled button's
   tooltip; calling `SetEditorState` then would leave the editor stuck in `EnteringPlayMode`);
   `WorkshopController.CurrentActiveMod == null` (dereferenced by `SetEditorState`). All `INVALID_STATE`.
2. `ValidateCurrentMod()` (always, as the button). With errors and `validateFirst` true: `INVALID_STATE`, message
   `the mod does not pass the editor's validator (N validator errors: <titles>) ...`, `error.data.validation` = the
   `validate_mod` counts + issues block (`valid`, `errorCount`, `warningCount`, `infoCount`, `issues`).
2b. Save protection (review C2 + C1), in the same main-thread step as the Play call: the link pre-check of every file
   `PummelMod.Save` will write (`LINK_REFUSED`, nothing written), and the dry run + safety copy of every prefab (a prefab
   that cannot be serialised refuses the playtest with `INVALID_STATE`, nothing changed).
3. Commit point (needs ≥ 20 s of the limit left, else `TIMEOUT`, nothing changed).
4. Apply the toolbar choices exactly as the dropdown/checkbox do (widget fields, `PlayerPrefs` when the choice changes,
   the `GameManager` values), then `SetEditorState(EnteringPlayMode)`. **This saves the test mod** (see below). Right after
   that synchronous save every prefab file is verified like after `save_mod`; a bad one is restored (a new one removed) and
   the call fails with `INTERNAL` saying which prefab files were restored and whether the playtest **WAS started** (then
   stop it with `stop_playtest`) or **NOT started** (`error.data`: `playtestStarted`, `state`, `prefabs`,
   `restoredPrefabs`). On success the reply carries `prefabsVerified` (and `prefabWarnings` if any).
5. Wait (frame by frame) until `PlayMode`; the reply is sent then. If the editor went back to `Editing` at once:
   `INTERNAL` (playtest copy failed to load; the editor shows its popup; the mod was saved). If it returns to edit mode
   before `PlayMode`: `INVALID_STATE`. If `PlayMode` is not reached 1 s before the deadline: `TIMEOUT` saying the mod
   was saved and the playtest is starting (check `get_status`, then `stop_playtest`; do not retry blindly).

```json
{"playing": true, "saved": true, "state": "PlayMode", "mod": "MCP_Arena", "modType": "Minigame",
 "players": "4 Players", "playerCount": 4, "teams": null, "aiOnly": true, "validated": true,
 "validation": {"valid": true, "errorCount": 0, "warningCount": 2, "infoCount": 0, "issues": ["..."]},
 "secondsToPlayMode": 3.4, "note": "Entering play mode saved the mod's files ..."}
```

`teams`: the team composition array for team choices (`[0,0,1,1]` for 2v2), else null. Input: with `aiOnly` every
player is an AI and the rounds run by themselves; otherwise player 1 is played by a human at the keyboard/gamepad. The
plugin sends **no input**. Errors: guard codes, `INVALID_STATE`, `BUSY_LOADING`, `TIMEOUT`, `INTERNAL`.

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
direct call leaks the session; the plugin uses the Stop button's path.

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

### Playtest visibility

Plugin 0.6.0, read-only. `get_playtest_state` answers "what is the situation right now", `get_playtest_events` answers
"what happened" without having to poll at the right moment. Both work when the human started the playtest by hand
and when the mod being played is one of the author's real mods: they only read runtime state (every member read was checked
in the game source to be a plain getter or a private field, never a getter with side effects) and never modify or save anything.
The per-frame sampling behind the event feed runs only while the editor is in `PlayMode`; with no playtest it costs one
comparison per frame.

#### `get_playtest_state` (read)

No params. Runs on the main thread. **Only while a playtest is running** (`PlayMode`); otherwise `INVALID_STATE` with a
message that says why: `Editing` (start one; `get_playtest_events` still has the last playtest), `EnteringPlayMode` (still
starting, retry), `ExitingPlayMode` (ending; read `get_playtest_events`), editor closed.

```json
{"running": true, "mod": "MCP_Arena", "modType": "Minigame", "playtest": 1, "paused": false,
 "useHealth": false, "note": "useHealth is off in the Minigame Settings (player settings): ... a value of 0 does nothing; a non-zero value whose result is 0 or less kills ... use the Kill action ...",
 "respawn": {"enabled": true, "time": 3, "invulnerableTime": 1},
 "minigame": {"type": "simple", "state": "Playing", "round": 1, "rounds": 2, "endCondition": "Timer", "placement": "MostPoints",
              "playerType": "TopDown", "roundLength": 60, "timeRemaining": 41.3, "timeElapsed": 18.6, "finished": false},
 "players": [
   {"index": 0, "name": "Bot 1", "isAI": true, "alive": true, "score": 0, "position": {"x": 1.2, "y": 0.1, "z": -3.4}, "deaths": 0},
   {"index": 1, "name": "Bot 2", "isAI": true, "alive": false, "score": 0, "position": {"x": 4.0, "y": 0.1, "z": 0.0}, "deaths": 1, "respawnIn": 1.4}],
 "alivePlayers": 3, "events": {"count": 12, "lastId": 12}}
```

Top level: `running` (always true), `mod`, `modType`, `playtest` (counts playtests since the game started, matches
`get_playtest_events.playtest`), `paused` (`Time.timeScale == 0`), `alivePlayers`, `events` `{count, lastId}`, `audio`.

`audio` (every playtest, whatever the mod type; any value that cannot be read is omitted or null, it never fails the call):
`{"music": {clip, volume, playing, timeSeconds, lengthSeconds} | null, "ambience": {...} | null, "modAudioSources": [...], "activeSources",
"masterVolume", "musicVolume", "effectsVolume"}`, floats rounded to 3 decimals. `music` is the game's music source
(`AudioSystemObj.currentMusic`), `ambience` the playing pooled source of sound type Ambience (what a minigame's ambience
plays through); both are the minigame settings' Music and Ambience only, and `null` when there is no such source or it
holds no clip. In a Mod Editor playtest `ambience` stays `null` even with an Ambience set: the game starts ambience in
`MinigameController.OnPlayersReady`, which only the board-game flow calls (seen in the game: Music played, ambience did not). `modAudioSources` lists the mod's own Audio Source components in the running scene (the editor's
edit-time copies are skipped): `{object, clip, volume, playing, loop, timeSeconds, lengthSeconds}` (`timeSeconds` and
`lengthSeconds` only with a clip), at most 20, with `modAudioSourcesTotal` when there are more. A looping Audio Source
object used as music shows up there, not under `music`; Play Sound on an Audio Source target plays that source
directly. A one-shot Play Sound on a non-Audio-Source target plays through the game's pool and is only counted in
`activeSources`. Nothing is loaded for any of this. `clip` is the AudioClip name, `volume` the
source's current volume (fades show), `playing` its `isPlaying`, `timeSeconds` its `time`, `lengthSeconds` the clip's
length. `activeSources` is the number of pooled/temporary one-shot and looping sources playing now (the ambience source
included, the music source not). The three volumes are the player's own game options as applied
(`AudioSystem` master/music/effects, 0..1): a music volume of 0 explains silence.
Minigame mods add:

| Field | Meaning (source) |
|---|---|
| `useHealth` | the Minigame Settings' "Use Health" (`SimpleModPlayerSettings.useHealth`, default **false**) |
| `maxHealth` | the settings' `health` (default 100); only when `useHealth` is true |
| `note` | only when `useHealth` is false: the rule below, in one paragraph |
| `respawn` | `{enabled, time, invulnerableTime}` from the player settings (`respawnEnabled` default true, `respawnTime` default 3 s); only `enabled` when off |
| `minigame.type` | `"simple"` for editor-made minigames (`SimpleModController`), `"other"` for any other controller (then only `state`, `round`, `rounds`, `finished` and the players' common fields exist) |
| `minigame.state` | `MinigameControllerState` name (`Initializing`, `EnablePlayers`, `Countdown`, `Playing`, `RoundResetWait`, `RoundStartWait`, `PreScoreScreen`, ...) |
| `minigame.round`, `rounds` | 1-based round as the round UI shows it (clamped to `rounds`), and the round count |
| `minigame.endCondition` | the settings' end-condition flags (`Timer`, `ObtainPoints`, `RemainingPlayersAlive`, `FinishMinigame`, joined by `", "`) |
| `minigame.placement`, `playerType` | `PlacementCondition` (`MostPoints`, `LeastPoints`, `FinishOrder`, `LastManStanding`) and `ChosenPlayerType` (`ThirdPerson`, `TopDown`, `TwinStick`) |
| `minigame.roundLength` | with the `Timer` end condition: seconds per round |
| `minigame.timeRemaining` | with `Timer` and state `Playing`: seconds left in this round (`MinigameController.TimeRemaining`, the timer UI's value) |
| `minigame.timeElapsed` | state `Playing`: seconds since this round's `StartNewRound` (`Time.time - round_begin_time`) |
| `minigame.pointsToWin` | with `ObtainPoints`: the score that ends the round |
| `minigame.minPlayersAlive` | with `RemainingPlayersAlive` (the round ends when that many or fewer remain; only without respawn) |
| `minigame.finishedPlayers` | with `FinishOrder` / `FinishMinigame`: how many finished this round |
| `minigame.finished` | true in state `PreScoreScreen` / `ShowScoreScreen`; then also `results` `[{rank, index, name, score}]` (ranked by the game's own `GamePlayer.MinigameScore`, which `BuildResults` filled: the points, negated for `LeastPoints`; ties share a rank; `score` is the raw points) and `winners` (names with rank 1) |

`players` (every `CharacterBase` of the controller, in spawn order; fields marked `?` appear only when they apply):

| Field | Meaning (source) |
|---|---|
| `index` | `GamePlayer.GlobalID`: the number every event's `player` uses |
| `name`, `isAI`, `team?` | `GamePlayer.Name`, `IsAI`; `GameTeam.TeamID` in team mode |
| `alive` | `!CharacterBase.IsDead` |
| `health?`, `maxHealth?` | `SimpleModPlayer.CurHealth` / `MaxHealth`; **only when `useHealth` is true** (with health off the stored value is a 0/1 flag, not hit points, see below) |
| `score` | `CharacterBase.Score` (a `short`: the number the score UI shows; -32768..32767) |
| `position` | `{x, y, z}` world position, 2 decimals |
| `deaths` | deaths the **plugin counted** since the playtest began (diff of `alive`); the game keeps no per-player death count |
| `stunned?` | true while `SimpleModPlayer.Stunned` |
| `invulnerable?` | true during the respawn invulnerability window (`IsInulnerable`) |
| `respawnIn?` | seconds until the respawn, while dead, respawns are on and the round is `Playing` (`respawnTime - (Time.time - m_lastDeathTime)`) |
| `finished?`, `placement?` | `HasFinished` (a Set Placement / finish was reached) and the 1-based finish order (`Placement + 1`) |
| `lastDamagedBy?` | index of the player whose weapon last damaged this one (`LastSourceOfPlayerDamage`; the game clears it on death) |
| `heldItem?`, `heldItemType?`, `heldItemCount?` | the most recently picked-up held item (`ModItem` name and `ItemType`) and how many are held (private `m_filoHeldItemStack`, read by reflection) |

Not provided because the game does not track it per player (or only privately and in a way that cannot be read purely):
kills / damage dealt, ammo or weapon cooldowns, per-team totals (sum the players' `score` by `team`), a death count other
than the plugin's, the state of running (waiting) actions.

**Board mods** (`modType` `"Board"`): `players` carry `index`, `name`, `isAI`, `team?`, `health` / `maxHealth`
(`BoardActor.ServerHealth`, `maxHealth`; board health is always in use), `keys` (`BoardPlayer.Gold`: the board's
"keys" are the gold counter, `GiveKeysAction` calls `GiveGold`), `trophies` (`GoalScore`), `space` (`BoardNode.NodeID`
of the current space), `state` (`BoardPlayerState` name) and `position`. `board` is `{supported: true, state
(GameBoardState name), turn (CurTurnNum), currentPlayer (index), keysAre}`. The board runtime is only read this far; if the
board controller does not exist yet the result is `board: {supported: false, reason}` with an empty `players`. Item mods
and other types: `supported: false`, `reason`, empty `players`.

While there is no minigame controller (state `PlayMode`) the result is `supported: false` with a `reason` and an empty
`players`. Two situations look the same from the plugin, so the reason names both: the play scene is still loading (retry in a
moment), or the round/minigame is already over and the game has dropped the controller (`get_playtest_events` still has what
happened).

#### `get_playtest_events` (read)

| Param | Type | Default | |
|---|---|---|---|
| `max` | int 1-500 | 100 | max entries returned |
| `sinceId` | int >= 0 | - | return entries with `id > sinceId`, oldest first (without it: the newest `max`) |

Answered on the HTTP thread like `get_log`; works in every editor state, also after the playtest (stop first, read
afterwards). Exceptions: while the Mod Editor is opening or closing it returns `EDITOR_NOT_OPEN`, with an incomplete
publish block `PUBLISH_BLOCK_INCOMPLETE`. Event ids keep counting across editor sessions of one game run.

```json
{"entries": [
   {"id": 1, "time": "2026-11-11T18:00:01.020Z", "playSeconds": 0, "type": "playtest_started"},
   {"id": 2, "time": "2026-11-11T18:00:02.410Z", "playSeconds": 1.39, "type": "player_added", "player": 0, "name": "Bot 1", "isAI": true},
   {"id": 9, "time": "2026-11-11T18:00:08.002Z", "playSeconds": 6.98, "type": "trigger_fired", "player": 1, "name": "Bot 2", "object": "Hazard",
    "objectGuid": "aa10...", "component": "ModTrigger", "trigger": "OnEnterActions", "triggerType": "None", "event": "Hit", "actions": 1,
    "seed": 123456, "source": "player 1", "receiver": "Hazard"},
   {"id": 11, "time": "2026-11-11T18:00:08.010Z", "playSeconds": 6.99, "type": "player_died", "player": 1, "name": "Bot 2", "deaths": 1,
    "cause": "ChangeHealth action [receiver Hazard, source player 1]"}],
 "nextId": 11, "dropped": false, "running": true, "playtest": 1, "logicEvents": true, "suppressedLogicEvents": 0, "sessionId": "3b0f2c...e1"}
```

- `entries[]`: `id` (monotonic over the whole game session, first id 1; the clear below does **not** reset it), `time` (UTC), `playSeconds`
  (seconds since `PlayMode` was reached, 2 decimals), `type`, `player` + `name` when the event is about one player, then
  the type's own fields (flat).
- `nextId` = pass as `sinceId` next time; `dropped: true` = entries after `sinceId` were overwritten (500-entry ring) or
  cleared by a new playtest before you read them (an `id` from the previous playtest is still accepted: you get the new
  playtest's events and `dropped: true`).
- `running` (a playtest is in `PlayMode` now), `playtest` (sequence number; a change means the buffer was cleared for a
  new playtest, started by the plugin or by hand), `logicEvents` (false when the recording hooks could not be installed:
  then `trigger_fired` / `action_executed` never appear, everything else still does), `suppressedLogicEvents` (trigger/action
  records skipped by the rate limits, total), `sessionId` (changes when the game restarts).
- The buffer is **cleared when a new playtest starts** (the editor leaves `Editing`) and is kept after it ended until the next one starts.

Event types:

| `type` | When (how it is detected) | Extra fields |
|---|---|---|
| `playtest_started` / `playtest_ended` | the editor reached / left `PlayMode` | `playSeconds` (ended: length) |
| `player_added` / `player_removed` | a player appears in / disappears from the controller's player list (every player is `player_added` once at the start) | `isAI`, `team?`, `health?` |
| `health_changed` | the health value changed (only with health in use; not on a respawn) | `old`, `new`, `delta`, `max`, `by?` (damaging player), `cause?` |
| `player_died` | `alive` true to false | `health?` (0), `deaths` (the plugin's count), `by?`, `cause?` |
| `player_respawned` | `alive` false to true (a respawn after `respawnTime`, or the reset at the start of the next round) | `health?` |
| `score_changed` | `Score` changed (board: `keys_changed`, and `trophies_changed`) | `old`, `new`, `delta`, `cause?` |
| `player_stunned` | `Stunned` false to true (the end of a stun is not recorded) | |
| `round_started` / `round_ended` | the controller state entered / left `Playing` | `round`, `rounds`, (`round_ended`: `state` it went to) |
| `minigame_finished` | the controller state entered `PreScoreScreen` / `ShowScoreScreen` | `results`, `winners` (as `get_playtest_state`), `rounds` |
| `turn_started` (Board) | `GameBoardController.CurPlayer` changed | `turn` |
| `trigger_fired` | a mod trigger ran its action list (`ModLogicTrigger.RunActionsLocal`; triggers with no actions never run) | `object` (GameObject name), `objectGuid` (the `get_hierarchy` guid), `component`, `trigger` (`Triggers[i]`, `OnEnterActions`, `OnUseTrigger`, `playerSettings.onDeathTrigger`, ... where the owner type is known, else `#<index>`), `triggerType`, `event` (`ModEventType`), `actions` (count), `seed`, `source`, `receiver` (`player N` or an object name) |
| `action_executed` | a mod action ran (`ModAction.Execute`; `Wait` is handled by the runner and not recorded) | `action` (`ModActionType`), `index` (position in its list), `seed` (the same `seed` as the `trigger_fired` of that run), `source`, `receiver` |

`player` of a logic record is the player involved (the event's source player, else the receiver player); `cause` is a
**hint**: the Change Health / Kill / Damage / Heal (health) or Change Score / Set Placement (score) actions that ran in the
same or the previous frame, up to three joined by `"; "`, with their source/receiver; it says an action ran at about
that time, not which player it targeted.

**How the events are detected.** The health, score, alive, stunned, round, minigame and player events come from
**diffing** a per-frame snapshot of the players (plain getters only; a frame in which nothing changed allocates nothing
and records nothing). Only `trigger_fired`, `action_executed` and the cause hints need the game to tell us something, and
for those the plugin installs **record-only Harmony postfixes** (own Harmony id `com.pummelpartymcp.plugin.playtesthooks`;
precedent `Hooks/SaveWatch.cs`) on `ModLogicTrigger.RunActionsLocal(ModEvent)` and on the eight declared overrides of
`ModAction.Execute(ModEvent)` (`ModAction`, `TargetAction`, `WaitAction`, `GiveItemAction`, `MoveSpaceAction`,
`SetConnectionActiveAction`, `SetGravityAction`, `SpawnPrefabAction`). A postfix returns at once unless a playtest is in
`PlayMode`, never changes arguments or results, and swallows every exception. If a target cannot be patched, a warning is
logged when the Mod Editor opens and only those two event types are affected (`logicEvents` reports it). The hooks are installed when the Mod Editor opens, after the RPC server started (`[General] PluginEnabled` and `[Server] Enabled` both true, publish block complete), separately from the publish block, and removed when the editor closes, so they never run in a match outside the editor.

**Rate limits for `trigger_fired` / `action_executed`** (gameplay events are never limited): at most one record per
trigger slot / action per second (the skipped ones are counted in `repeats` on the next record of that key), and at most
10 per second overall (burst 20), so a per-frame `Update` trigger or a Timer cannot push deaths out of the 500-entry ring.
The ring holds 500 entries; read it every few seconds in a long test, or stop and read.

#### What health and score mean in simple minigames

The runtime is `SimpleModController` / `SimpleModPlayer` (editor-made minigames); the rules are read from the game source.

- **Health in use (`useHealth` true):** `maxHealth` (`health`, default 100) is the starting and maximum value; the player
  is set to it at spawn and at every respawn / round reset. `Change Health` computes `result = op(current, value)`
  (`Set`, `Add`, `Subtract`, `Multiply`, `Divide`; **the action's default operation is `Set`**, so `m_value: -25` without
  `m_operation` sets health to -25 = dead), clamps it to `0..health`, stores it, and if the stored health is 0 or less the
  player is killed. `m_value` 0 is a no-op (a hit animation only). A weapon hit applies the weapon's damage as `Subtract`.
- **Health off (`useHealth` false, the default): `Change Health` is a kill switch, with one exception.** The stored health is 0 until the first
  `ResetPlayer` (the controller runs it for every player at round start), which sets it to the "max", 1 when health is off; it is
  set to 1 again at every respawn and round reset. The stored value is clamped to 0..1. `ChangeHealth` returns early when `m_value` is 0 (a hit animation only), whatever the
  operation, so `Set 0`, `Add 0`, `Multiply 0` kill nobody. For any non-zero value the result is computed from the stored
  health: `Add -25`, `Subtract 25` and `Set -1` leave 0 or less and kill at once, whatever the amount (so does anything from stored 0
  that gives 0, such as `Multiply` by any non-zero number); from health 1, `Multiply 2` gives 2, clamps to 1 and does not kill, `Divide 1`
  gives 1, `Divide 2` gives 0 and kills. A result above 0 sets the value to 1 (nothing visible) and the next decrease kills. `Divide` by 0 is the same as value 0 (early return; `PerformOperation` would also return the old value). This is why a
  Change Health -25 in a mod with default settings killed bots instantly. Only a player target is affected. **Kill/Destroy** (type
  `Kill`, `KillAction`) kills a player target by calling `KillPlayer` directly, without this arithmetic and with the same refusals as
  any death (respawn invulnerability, respawn animation, between rounds, and only when the player is owned by this peer); a
  non-player target is deactivated instead, so target the player: Source (Activator) in a Trigger or Logic Hit
  (`SimpleModPlayer.HandleTrigger` sets Source = the player, Receiver = the event receiver), Receiver in player-settings triggers.
  Seen in a test: Kill on a kill-zone trigger killed AI players in a local playtest. Turning on **Use Health** in the Minigame Settings (by hand; the plugin edits no settings except
  the player-settings triggers) and setting **Health** gives real hit points. The `note` of `get_playtest_state` repeats this while health is off.
- **Death is refused in three situations** (health is still changed): during the respawn invulnerability window after a
  respawn (`respawnInvulnerableTime`, default 1 s), during the portal respawn animation, and between rounds. The
  player then stays alive with health 0 and the next `Change Health` kills it - so a hit during invulnerability can
  leave a player alive at 0 health.
- **Death and respawn:** `onDeathTrigger` runs, then the player ragdolls, health is set to 0, the stun is cleared. If
  `respawnEnabled` (default true) and the round is `Playing`, after `respawnTime` (default 3 s) the player is teleported to a
  respawn point, health is reset to the maximum, `isDead` clears and `onRespawnTrigger` runs. With respawn off a dead player stays
  dead until the next round (the `RemainingPlayersAlive` end condition only counts when respawn is off).
- **Score** is a signed 16-bit value per player (`CharacterBase.Score`; `Change Score` applies `Set/Add/...` and clamps to
  -32768..32767). The game itself adds points in two cases: `LastManStanding` placement gives a dying player
  `10 * (players already dead + 1)` points (and each survivor the same at the round end), and finishing (`Set Placement` /
  `SetFinished`) gives `100 * (players - already finished)`. Nothing else changes a score unless a Change Score action does.
- **Falling, spawns and respawn:** no height limit exists in `SimpleModPlayer`, `SimpleModController`, `MinigameController` or
  `CharacterMover`, so a player without a catch-zone trigger falls forever. Round 1 (`MinigameController.SpawnPlayers`) and every
  later round start (`SimpleModController.ModResetRound`) call `ModPlayerSpawn.DetermineInitialSpawnPoints` on the spawns whose
  `SpawnUsageType` includes `InitialSpawn`. A respawn (`DetermineRespawnPoint`) takes a random spawn (seeded by the kill seed) among
  those with `Respawn` whose `AllowedPlayers` includes the player; it is not the nearest and `PlayerCountMask` is ignored (initial spawns honour both, `FilterSpawnsForPlayerCount`). With no respawn spawn at all the player goes to
  (0, half the player height, 0) without a log line; with respawn spawns that none allows, same position and the log shows "Failed to get spawn point for player index".
  `FindSpawnPoints` (`FindObjectsOfType`, active objects only) collects the spawns once, while the list is empty, so enabling or disabling
  spawns later changes nothing. Players cannot act while
  the controller is not `Playing` (`CanDoActions`); `ResetPlayer` does not clear a stun (only a death does).
- **Action runners and triggers:** each trigger firing creates a `ModActionRunner` that holds its `ModEvent`, so Source is kept across a
  Wait. Position and Match Transform set the transform of a player directly (no velocity reset). `ModTrigger.OneUsePerPlayer` keeps a
  list of activating sources that is never cleared (each player once, not reset per round). Actions keep running during the round-reset and
  round-start waits (`ShouldMinigameBeRunningActions`), so a Wait that spans the round end teleports after the round-reset teleport.
  Match Transform on a player turns it to the marker's yaw only (`TeleportPlayer` with `Euler(0, y, 0)`).
- **AI:** `GetAIInput` sends jump only once the AI has been airborne for 0.35 x coyote time (at most every 0.5 s): walking off an edge it gets a
  coyote jump, it uses extra air jumps, and a jump buffered just before landing fires on landing. It never jumps deliberately at a gap, so an AI
  crossing a gap does not prove the gap is jumpable.
- **Round layouts:** the scene is not reset between rounds (only players are), no trigger knows the round number and the initial spawns are the
  same every round. A layout per round works with Logic objects whose RoundEnd triggers run Set Active on the courses and the next Logic
  object; the chain advances one step per round (a Logic object enabled during a RoundEnd event does not fire in that event).
- **Players overlapping:** players do not block each other (8 players in a small pen overlapped freely in a test); there is no setting for it.
- **Known log line:** at a playtest start of a mod with many assets `ArgumentOutOfRangeException` in
  `ModBehaviourMonitor.FindAndRemoveNullBehaviours` was seen in 2 of 3 starts of a 61-asset mod. Probably a race: the mod-preparation thread
  switches the active mod (`WorkshopController`) while the main thread walks the asset list. The playtest runs normally.
- **Audio pool:** `AudioSystemObj` pools sources (8 at start, `MaxSourceCount` 32); it logs "All audio sources in use, creating new source" and
  at the limit "All audio sources in use, and max sources reached, stopping random source.".
- **Rounds and results:** the round ends when an enabled end condition is met (`Timer`: time up; `ObtainPoints`: someone
  reached `pointsToWin`; `RemainingPlayersAlive`: `minPlayersAlive` or fewer left and respawn is off, solo or team mode;
  `FinishMinigame`: everyone finished). After the last round `BuildResults` copies the scores to `GamePlayer.MinigameScore`
  (negated for `LeastPoints`, which negates the `Set Placement` points too; `FinishOrder` has no branch of its own and ranks by score like `MostPoints`) and the controller goes to `PreScoreScreen`: that is `minigame_finished`.

### What a playtest writes and what reaches Steam

On disk (all inside the open test mod's folder unless noted):

- At Play: `PummelMod.Save()` writes `Data/Meta.json` (new `LastModified`), `Data/WorkshopItem.json` (publishedFileId
  256: the guard's id hygiene runs right before), `Lighting.json`, `PostProcessing.data`, `MainScene.scene`,
  `ModSettings.json` and the minigame definition / map details, and every external asset's `.pmeta`. Unlike `save_mod`:
  no `Preview.jpg`, the dirty flag is not cleared, the editor's mod list is not touched, and the plugin's
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
| `ping`, `get_log`, `screenshot` | work unchanged (`get_log` and `screenshot` are the way to observe a playtest from outside; the screenshot shows the editor window with the play view); `screenshot`'s `camera` is edit mode only (`INVALID_STATE`) |
| `get_status` | works; `playtest` block with `running: true`, times, `paused`, `minigame` / `board` state, `eventCount`, `lastEventId`, `alivePlayers` |
| `get_playtest_state` | **needs** a running playtest (`PlayMode`): reads the runtime players and minigame/board; `INVALID_STATE` in every other state (the message says what to do) |
| `get_playtest_events` | works in every editor state, also after the playtest (reads the plugin's own buffer; answered on the HTTP thread) |
| `get_hierarchy` | works; lists the **edit-time** scene and adds `playMode: true` + `playModeNote` (the synthetic root shows `active: false`) |
| `list_assets`, `list_trigger_types`, `list_action_types` without an address | work; result has `playMode: true` (static or editor-mod data) |
| `list_mods`, `list_component_types` | work (array results, no marker; `addable` describes edit mode) |
| `get_object`, `get_action_graph`, `list_action_types` with an address, `get_open_mod`, `validate_mod`, `list_mod_settings`, `get_mod_settings` | `INVALID_STATE` (references would resolve against the runtime copy; the validator would see the runtime objects) |
| every edit tool, `set_mod_settings`, `capture_preview_image`, `save_mod`, `undo`, `create_test_mod`, `open_test_mod`, `start_playtest`, `run_batch` | `INVALID_STATE` from the guard in every state except `Editing`, including `EnteringPlayMode` and `ExitingPlayMode` (edit pipelines also re-check each frame and stop if a playtest starts) |
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
`set_action`, `add_trigger`, `remove_trigger`, `set_trigger`, `undo`, `save_mod`, `set_mod_settings`, and the read tools `get_object`,
`get_hierarchy`, `get_action_graph`, `get_mod_settings`, `list_mod_settings`, `list_mods`, `list_assets`, `list_component_types`, `list_action_types`,
`list_trigger_types`. Refused: `run_batch`, `create_test_mod`, `open_test_mod`, `start_playtest`, `stop_playtest`,
`screenshot`, `capture_preview_image`, `import_asset`, `set_import_settings`, `create_material`, `set_material`, `remove_asset`, `create_prefab`, `open_prefab`, `close_prefab` (named reason; edits inside an already open prefab may be batched like any other edit), and every other tool (`ping`, `get_status`, `get_log`, `get_open_mod`, `validate_mod`,
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
1. create_test_mod {"name": "MCP_E2E", "type": "Minigame"}
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
   (Hazard's Change Health Subtract 1 kills every player that enters it, because Use Health is off in the default Minigame Settings: see
   [What health and score mean](#what-health-and-score-mean-in-simple-minigames))
   -> completed 7; results[0].result.guid is the floor's guid, referenced by steps[5] (`parentGuid`), and results[2].result.guid by steps[3] and steps[4]
3. validate_mod -> valid: true (warnings: empty description, no preview image)
4. save_mod
5. start_playtest {"players": 4, "aiOnly": true} -> playing: true, saved: true
6. get_status (playtest.running, playtest.minigame.round, playtest.eventCount) / get_log {"sinceId": <nextId before step 5>} / screenshot
7. observe what happens to the players (a trigger that "should" hurt players has to be verified, not assumed):
   get_playtest_state -> useHealth false + note (health is off: a non-zero Change Health that leaves 0 or less kills at once; value 0 does nothing),
     players[i].alive / score / position / deaths, minigame.state "Playing", timeRemaining
   get_playtest_events {"max": 100} -> entries such as
     {"id": 3, "playSeconds": 6.4, "type": "action_executed", "action": "ChangeHealth", "receiver": "Hazard", "source": "player 2", ...}
     {"id": 4, "playSeconds": 6.4, "type": "player_died", "player": 2, "name": "Bot 3", "deaths": 1, "cause": "ChangeHealth action [receiver Hazard, source player 2]"}
   poll with {"sinceId": <nextId>}, or stop first (step 8) and read the whole feed afterwards: it is kept until the next playtest starts
8. stop_playtest -> stopped: true, state "Editing"
9. get_playtest_events {"sinceId": 0} -> the full feed of that playtest (player_added x4, round_started, deaths, minigame_finished with results)
```

---

## Safety

### Config switches (`BepInEx\config\com.pummelpartymcp.plugin.cfg`)

| Key | Default | Effect |
|---|---|---|
| `[General] PluginEnabled` | `true` | Read at game start. `false`: the plugin does nothing at all (no server, no patches, no scene-load check, one log line). Use this to publish by hand. |
| `[Safety] EnableWrites` | `true` | Read on every write call (the plugin reloads the file when its modification time changed, see below). `false`: every write tool returns `WRITES_DISABLED`. Does not affect the publish block. |
| `[Safety] AcceptedGameVersion` | empty | A game version you accept although the plugin was not tested with it. Applies only to that exact string (case-sensitive; surrounding whitespace in the cfg line is ignored); the next game update closes the gate again. Read on every write call. See [Game-version gate](#game-version-gate). |
| `[Safety] ForceGameVersionUntested` | `false` | Test switch: treats the running version as untested even if it is in the tested list. Can only make the gate stricter; it never accepts a version. |
| `[Assets] MaxImportFileMB` | `50` | `import_asset`: largest source file, 1..1024 MB. Read on every import call (no restart). |
| `[Assets] MaxModAssetsMB` | `500` | `import_asset`: largest total size of the sandbox mod's `Assets` folder after the import, 1..8192 MB. Read on every import call. |
| `[Assets] MaxAudioSeconds` | `600` | `import_asset`: longest audio clip, 1..7200 s, read from the WAV/OGG/MP3 header of the copy (the game decodes the whole clip on its main thread). Read on every import call. |
| `[Assets] MaxCompressedAudioMB` | `15` | `import_asset`: largest OGG/MP3 whose duration its header does not give (e.g. a VBR MP3 without a Xing/VBRI header), 1..1024 MB. Read on every import call. |
| `[Server] Enabled` | `true` | Read when the Mod Editor opens. `false`: no RPC server and no hooks; the publish block is still installed while the editor is open. |
| `[Server] Port` | `38917` | Read when the Mod Editor opens. If the port cannot be bound (already in use), one Error is logged (BepInEx and the editor's Log window) and no tool is served in that editor session. The Workshop publish block stays until the editor closes. |

**Config reload.** BepInEx 5.4.23 does not watch the `.cfg` file, so a plain edit would only apply after a game restart. The plugin therefore stats the file on every guarded call (write tools, `get_status`, `self_test`) and calls `ConfigFile.Reload()` when its modification time changed; `EnableWrites`, `AcceptedGameVersion` and `ForceGameVersionUntested` follow edits without a restart (a malformed file keeps the values already loaded). `ping` runs on the HTTP thread and reports the values loaded at the last reload. The file is also reloaded when the Mod Editor opens, so `Enabled` and `Port` apply from the next editor session. `PluginEnabled` is read at game start only.

### Links inside sandbox mods

The plugin never writes, creates, moves or deletes a file through a junction, directory symlink, mount point, file
symlink or hard link inside a sandbox mod, nor when the mod folder itself is a link (`LINK_REFUSED`, nothing written). The
rule is central (`Core/SandboxPath.cs`) and runs before every file operation and every game call that writes or deletes
files for the plugin; the full list is under [Links inside a sandbox mod](#links-inside-a-sandbox-mod-every-write-and-delete).
When the Mod Editor opens (with writes enabled and a complete publish block) the plugin also renames a copy left by an import that a crash interrupted during decoding
(`McpRecoveryimport-pending.json`; sandbox mods only, hash-checked, rename only).

### Token file

The token file is read, or created, when the Mod Editor opens (so on a fresh install it appears at the first editor open),
and kept afterwards: the token stays the same across editor sessions and game restarts. `BepInEx\config\PummelPartyMcp.token` is written to a temp file in the same folder and then moved into place (missing
target) or swapped in with `File.Replace` (malformed target), so a reader never sees a half-written token. Two
processes starting at once converge on one token: the loser of the move re-reads the file and uses the winner's token.
**Limitation:** the file's ACL is not restricted. It inherits the permissions of the game's `BepInEx\config` folder
(on Windows normally the current user plus administrators/SYSTEM, but on a game library on another drive it may also
include "Users"). Restricting it to the current user needs `System.Security.AccessControl`, which is not reliable on
the game's Mono/net472 runtime without extra dependencies, so the plugin does not try. The token is a protection
against other web pages and processes that cannot read that folder; treat any local user who can read the folder as
trusted, or tighten the folder's ACL by hand.

### Test-mod rule

A name is a test-mod name iff it starts with the exact, case-sensitive prefix `MCP_`, has at least one more
character, is at most 64 characters long, consists only of ASCII `A-Z a-z 0-9 _ -` and spaces, and has no leading
or trailing space. `mcp_x`, `MCP_`, `MCP_a.b`, `MCP_../x` and non-ASCII look-alikes are not. The
prefix is hardcoded; there is no configuration, allowlist or bypass.

### Game-version gate

The plugin replicates editor call sequences and reads private game members, so after a game update it may silently misbehave. `Application.version` (the string logged at startup and returned by `ping` as `gameVersion`) is compared with the list of tested versions compiled into the plugin (`1.14.1k`). The comparison is exact (ordinal, case-sensitive, no trimming). If the running version is not in the list, the plugin logs a Warning when the Mod Editor opens, **read tools keep working** and **every write tool is refused with `GAME_VERSION_UNTESTED`** (the message names the running version, the tested versions and how to accept), unless `[Safety] AcceptedGameVersion` equals the running version exactly. If the version cannot be read at all, it counts as untested and cannot be accepted. The check is step 3 of the central guard (after `EnableWrites` and the publish block, before the editor-state checks), so no write tool, lifecycle tool or batch step can skip it; the publish block is not affected by it. Workflow after an update: call `self_test`; if `ok` is true, set `[Safety] AcceptedGameVersion = <version>` in the cfg (no restart, see Config reload) and verify with a small edit on a `MCP_` mod. To test the gate without an update set `[Safety] ForceGameVersionUntested = true` (stricter only), then clear it.

### Guard (central, in the tool registry)

A write tool cannot be registered without a guard kind (`ToolDefinition.Write` requires one, a registry without
`WriteSafety` refuses write tools). Checks, in order, on the main thread in the same unit of work as the start of the
body, when the call's turn in the write queue comes (see [Threading](#threading)):

1. `[Safety] EnableWrites` → `WRITES_DISABLED`
2. publish block complete → `PUBLISH_BLOCK_INCOMPLETE`
3. game version tested or accepted → `GAME_VERSION_UNTESTED` (also when the version cannot be read)
4. editor open → `EDITOR_NOT_OPEN`
5. not loading → `BUSY_LOADING`
6. kind **OpenMod** (`save_mod`, `create_object`, `duplicate_object`, `delete_object`, `reparent_object`,
   `set_object`, `set_transform`, `add_component`, `remove_component`, `set_property`, `set_properties`, `undo`,
   `add_action`, `remove_action`, `move_action`, `set_action`, `add_trigger`, `remove_trigger`, `set_trigger`,
   `start_playtest`, `run_batch`, `set_mod_settings`, `capture_preview_image`, `import_asset`, `set_import_settings`,
   `create_material`, `set_material`, `remove_asset`, `create_prefab`, `open_prefab`, `close_prefab`, and every write step inside a batch; `stop_playtest` with the play-mode option):
   a mod is open (`NO_MOD_OPEN`) and loaded (`BUSY_LOADING`); its Meta name **and**
   folder name pass the rule (`GUARD_NOT_TEST_MOD`, message names the mod); state is `Editing` (`INVALID_STATE`;
   every state other than `Editing`, i.e. also `EnteringPlayMode` and `ExitingPlayMode`, is refused). Only
   `stop_playtest` is declared `allowPlayMode`: it skips this last check, all other checks apply to it unchanged.
   Kind **Lifecycle** (`create_test_mod`, `open_test_mod`): the `name` param passes the rule; if any mod is open it
   is a test mod, else `GUARD_NOT_TEST_MOD` ("close <name> by hand first"); state is `Editing`.

Workshop id hygiene: whenever the guard passes with a loaded test mod open, before every plugin save and after
create/open, an in-memory `workshopItem.publishedFileId` other than 256 (`ModWorkshopItem.DefaultWorkshopID`) is
reset to 256 with a warning in the BepInEx and Unity logs. This is the only workshop-item field the plugin writes.

### Workshop publish block

While the Mod Editor is open and the plugin is enabled, Harmony prefixes skip
every Workshop create/update/delete path and return a safe default: the type's `Invalid` handle where one exists
(`UGCUpdateHandle_t.Invalid` = `ulong.MaxValue`, `SteamAPICall_t.Invalid` = 0), else `false` / 0 / null.

- Game layer (all required): `WorkshopModInstaller.PublishItem`, `.SubmitItemUpdate(UGCQueryHandle_t)`,
  `.BeginUGCItemContentUpdate`, `.OnCreateItem`, `.OnSteamUpdateUGCQuery`, `.SetUGCPreviewFiles`;
  `WorkshopPopup.PublishMod`, `.ResetPublishFileIdAndRetryPublish`, `.DeleteBlankWorkshopItemOnFailedPublish`;
  `WorkshopBrowserItem.Delete`.
- `Steamworks.SteamUGC`: `CreateItem`, `StartItemUpdate`, `SubmitItemUpdate`, `DeleteItem` (required); every
  public static method whose first parameter is `UGCUpdateHandle_t` except the getter `GetItemUpdateProgress`
  (enumerated when the block is installed); `AddDependency`, `RemoveDependency`, `AddAppDependency`, `RemoveAppDependency`.
- `Steamworks.SteamRemoteStorage`: every method whose name contains `FileWrite` or starts with
  `UpdatePublishedFile`, plus `FileDelete`, `FileForget`, `FileShare`, `SetSyncPlatforms`, `PublishWorkshopFile`,
  `PublishVideo`, `CreatePublishedFileUpdateRequest`, `CommitPublishedFileUpdate`, `DeletePublishedFile`.
- Not blocked (normal play keeps working): queries, `Get*`, `Release*`, `DownloadItem`, `SubscribeItem`,
  `UnsubscribeItem`, `SetUserItemVote`, favourites, playtime tracking, `UGCDownload`, `UGCRead`, `FileRead`,
  `FileExists`.
- When: installed when the Mod Editor opens, as the first step (before the server, the hooks and the import recovery;
  inside the editor scene's load, before the editor's first frame, so before its File menu can open the publish dialog).
  Removed when the editor closes, as the last step. Outside the Mod Editor the plugin blocks nothing: the main-menu
  Workshop window (`WorkshopBrowserItem.Delete` included) works as in the unmodded game. No publish or update path
  exists outside the editor (every one starts in the editor's `WorkshopPopup`).
- Fail closed: if a required target is missing or any patch fails when the editor opens, an Error is logged (BepInEx and
  the editor's Log window), `publishBlock.complete` is false and, for that editor session, every tool except `ping`,
  `get_status`, `get_log` and `self_test` returns `PUBLISH_BLOCK_INCOMPLETE` (nothing runs). The patches that did go in
  stay until the editor closes.
- Checked live: before every write tool (guard step 2) and on every `get_status`, the plugin reads Harmony's patch
  info of each required target (about 14 lookups, no per-frame work). If one no longer carries the plugin's prefix
  (another plugin called `Harmony.UnpatchAll()` / `UnpatchID`, or unpatched it), the block is incomplete from then on:
  one Error per lost target in the BepInEx log, `publishBlock.complete: false` with the target in `missing`, every
  write tool `PUBLISH_BLOCK_INCOMPLETE`. The plugin does **not** re-patch within an editor session; close and reopen
  the Mod Editor (and remove the plugin that unpatched it). Optional targets are not re-checked.
- Each blocked call logs a Warning (first call at once, then at most once per method per 10 s with the number of
  calls not logged) to the BepInEx log and to the Unity log, so the editor's Log window shows it:
  `[PummelPartyMcp] Workshop publishing is blocked while the Mod Editor is open with the pummel-party-mcp plugin enabled. Blocked call: WorkshopPopup.PublishMod(PummelMod). Nothing was sent to Steam. To publish, set [General] PluginEnabled = false in BepInEx\config\com.pummelpartymcp.plugin.cfg and restart the game.`

**What the author sees when clicking Publish with the plugin loaded:** File > Publish Mod to Workshop still opens
the dialog (its read-only Workshop details query is allowed). Clicking "Publish mod to workshop" / "Publish update
to workshop" does nothing visible in the dialog: `WorkshopPopup.PublishMod` is skipped as a whole, so the mod is
**not saved**, no reinstall-on-exit is queued, nothing is sent to Steam, no progress bar appears and the button
stays enabled. The Log window shows the line above (per click, at most once per 10 s). The dialog closes normally
and the editor keeps working. Note: opening the dialog on a published mod copies the remote title, description and
visibility into the in-memory mod; that is game behaviour, unchanged by the plugin.

**How to publish by hand:** quit the game, set `[General] PluginEnabled = false`, start the game, publish from the
editor as usual, quit, set it back to `true`.

### Lifecycle (active only while the Mod Editor is open)

The plugin is loaded by BepInEx at game start, but it is active only while the Mod Editor is open (state machine
`Core/EditorLifecycle.cs`, host `Plugin.cs`).

- **Dormant** (game start, main menu, matches): no Harmony patch of the plugin, no listener, no log listener, no thread
  of its own (right after a disarm, connection threads of the last session may still finish their reply for a moment),
  the component is disabled (no `Update`). The only code is one `SceneManager.sceneLoaded` callback: per scene load one
  scene-name compare and one read of `EditorSystem.Instance`.
- **Arming** (synchronously in the scene-loaded callback of `ModEditorScene`, or of any scene while
  `EditorSystem.Instance` exists): the publish block first (also when a later step throws), then the log listener,
  game-version log, import recovery, token, a new dispatcher and listener, then the save watch, the mod-switch guard and
  the playtest hooks. One Info line with the time taken.
- **Active**: as described in this document. Scene loads during an editor playtest change nothing. The plugin stays
  armed while an editor object exists: `EditorSystem.Instance` is not null, or (when it is null) an active `EditorSystem`
  object is still found (the game nulls `Instance` in every editor's `OnDestroy`, so after a rare exit that kept an old
  editor object alive, closing a second editor nulls it while the old one lives). This also covers the rare case where
  the game keeps the editor object after it goes back to the main menu.
- **Failed**: the publish block is incomplete (only `ping`, `get_status`, `get_log`, `self_test` answer;
  `PUBLISH_BLOCK_INCOMPLETE`), `EditorSystem.Instance` cannot be read (game update: the block is installed, only those four
  answer; `INTERNAL`; the editor is then found by the object search), or the token file or the port failed (nothing
  answers). Ends like Active when the editor closes.
- **Disarming** (the editor object was seen and is gone; or it never appeared and a later scene that is not
  `ModEditorScene` loaded with no editor object: there is no time-out, so an editor that comes up late still finds the
  block): the listener stops; queued calls, waiting write calls and waiting screenshots fail with `EDITOR_NOT_OPEN` ("not
  run, nothing changed"); calls already running get up to 2 s (and at least 10 frames) to finish with their own result
  (a screenshot that would capture after the editor closed fails with `EDITOR_NOT_OPEN`); whatever still runs then fails
  with `EDITOR_CLOSED_DURING_CALL` ("may have partly applied"). Then the hooks and, last, the publish block are removed
  and checked (Harmony patch info); one Info line, or one Error if a patch is left. If removing a patch fails, one Error in the BepInEx log names it and it stays in the game until the next time the Mod Editor opens and closes: a leftover playtest hook still runs (and returns at once) in mod minigames, a leftover publish-block prefix still blocks the main-menu Workshop delete. Restart the game to be sure.
- Quit while the editor is open: the listener stops and queued calls fail; nothing is unpatched (the process ends).

Kept for the whole game run: the log ring, the playtest event feed (ids keep counting), the undo tracker, `sessionId`
and the token. New per editor session: the patches, the publish-block monitor, the dispatcher and the listener.
`get_status.lifecycle` shows the state. A game method that was patched once keeps a Harmony-generated replacement after the plugin removed its patch: it behaves like the original and carries no patch of the plugin, but its machine code is not the original one.

---

## References

Asset/object references (property values, action targets, settings) use one shape:

| Field | |
|---|---|
| `guid` | guid of the mod asset entry (for scene objects: the referenced component's `ModBehaviour` guid, for a `Transform` the `ModTransform` guid); null if unregistered |
| `name` | asset name, or the Unity object name |
| `assetType` | for an imported (external) asset its asset class short name, as `list_assets` and `capture_preview_image` show it (`Texture`, `Prop`, `Material`, ...; `set_mod_settings` and `get_object` agree); for anything else the Unity type of the referenced object (`Transform`, `AudioClip`, ...) |
| `location` | `internal` (built-in), `external` (mod file), `runtime` (scene object) |
| `objectGuid`, `objectName` | for scene objects: `ModTransform` guid and name of the GameObject (use with `get_object`) |
| `registered: false` | object not found in the asset registry (the scene file would store it as null) |
| `missing: true` | only a guid is stored and no asset with that guid exists |

A mod-asset value (a prefab, prop, ... held by a component or an action parameter) is shown as that asset only while
it is the very asset object the game has registered under its guid. Otherwise the stored guid decides: the registered
asset with that guid, `{guid, missing: true}`, or `null` when no guid is stored. Unity's serializer (undo snapshots,
scene files) cannot store "none" for such a field and can leave a default placeholder object (`"DefaultObject"`, an
unregistered guid) there; that reads as `null` ("no asset"), not as an asset. A reference that cannot be read at all
(e.g. a renamed game member after a game update) shows `{"error": ...}` in `get_object`, and `remove_asset` then counts
its reference scan as incomplete.

## Not in this build

No input injection for playtests (a human or AI-only mode drives them); no pause/resume tool; no editing of
Item details; custom board spaces are read-only (see [Mod settings](#mod-settings));
no editing of board connections (`BoardNodeConnection[]`), so a Board mod cannot be made validator-clean through the
plugin yet; no redo (the editor has none); no copy/paste/duplicate of
actions (the Action Editor has none); no editing of Set Connection Active connection mappings or of the Display
Popup's hidden timer fields; no layer/tag or per-component enable
editing, no rename or duplicate of prefabs. Assets: import only from local absolute paths (no UNC/network/device paths), no
mesh import of `.mtl` files, no import of `.bmp`, `.pmat`, `.pfab` or `.pgf`, no overwrite or replace of an existing asset, no
rename or duplicate of assets, no material properties for shaders without a known inspector, no
audio/OBJ import settings beyond what the inspector offers. No delete, rename or close of mods. No Workshop functionality of any
kind; publishing is blocked (see [Safety](#safety)).
