# PummelPartyMcp.Server

Unofficial project, not affiliated with Rebuilt Games.

MCP server (stdio) that passes tool calls through to the pummel-party-mcp BepInEx plugin running inside Pummel Party (`POST http://127.0.0.1:<port>/rpc`). It only ever connects to `127.0.0.1`.

Tools (same names as plugin methods, one MCP tool per method, plus the local `usage_guide`; 41 in total):

- Read-only: `usage_guide` (local, no plugin call: returns the shared guide that is also sent as the server `instructions`, for clients that do not show instructions to the model), `ping`, `get_status`, `get_open_mod`, `get_hierarchy`, `get_object`, `get_action_graph`, `list_assets`, `list_component_types`, `list_action_types`, `list_trigger_types`, `get_log`, `screenshot` (optional `camera` moves the editor view first, edit mode only), `list_mods`, `validate_mod` (the editor's own validator; read-only), `get_playtest_state` (players, minigame/board state and `audio` of the RUNNING playtest), `get_playtest_events` (event feed of the current or last playtest, paged by `sinceId`).
- Mod lifecycle (write): `create_test_mod`, `open_test_mod`, `save_mod`.
- Object/component editing (write): `create_object`, `duplicate_object`, `delete_object`, `reparent_object`, `set_object`, `set_transform`, `add_component`, `remove_component`, `set_property`, `set_properties`, `undo`.
- Trigger/action editing (write): `add_action`, `remove_action`, `move_action`, `set_action`, `add_trigger`, `remove_trigger`, `set_trigger`.
- Playtest and batch (write): `start_playtest` (SAVES the open sandbox mod, then presses Play), `stop_playtest`, `run_batch` (1-100 ordered steps with `$id.path` placeholders; destructive hint because it can contain deletes).

All but `usage_guide` need the game running. `ping`, `get_status`, `get_log`, `get_playtest_events` and `screenshot` work with the Mod Editor closed; `list_mods` needs the editor open; `get_playtest_state` needs a running playtest; the others need an open mod. Every write tool description ends with "See usage_guide."; the shared rules (safety, workflow, undo/selection, clamping, address forms, value shapes, target flags) live once in the guide, which keeps `tools/list` plus instructions under 40,000 characters (enforced by a test; 39,945 at 0.7.0). To stay under it the published schemas omit `minLength: 1` (empty strings are refused by a refinement instead) and the SDK's default `execution: { taskSupport: "forbidden" }` (the spec's default when absent).

Input schemas are strict: an unknown argument (for example `parent` instead of `parentGuid`) is an error naming the key. Results are compact JSON; text over 100,000 characters is truncated with a note on how to narrow the query, and plugin responses over 8 MB are aborted. A write tool that fails with a timeout, a connection reset or a broken response is never retried (only a 401, which never executes, is retried once after re-reading the token) and the error says the edit may or may not have been applied.

Safety model: write tools only ever act on sandbox mods whose name starts with `MCP_` (name rule `^MCP_[A-Za-z0-9_ -]+$`, max 64 chars, no leading/trailing space; validated here and in the plugin); the plugin refuses anything else. The plugin cannot open, modify, rename or delete the author's real mods, and Workshop publishing is blocked while the plugin is loaded; there is no tool for it. `create_test_mod`/`open_test_mod` are refused while a non-sandbox mod is open (the human must close it by hand); `create_test_mod` and `open_test_mod` will not discard unsaved changes of the open sandbox mod unless `discardUnsaved` is true. There is no generic "call any method" tool. Timeouts: 15 s default, 20 s edit tools (including the action/trigger tools), 30 s `screenshot`/`save_mod`/`undo`, 45 s `create_test_mod`/`open_test_mod`, 70 s `start_playtest`, 40 s `stop_playtest`, 130 s `run_batch` (plugin limits 60/30/120 s plus the 4 s commit grace); asset and prefab tools 40 s, `capture_preview_image` 30 s, `import_asset` 130 s (plugin limits 30 s, 25 s and 120 s; see Core/CommitBudgets.cs). Links (junctions, symlinks, hard links) inside a sandbox mod are never written or deleted through (LINK_REFUSED).

Editing notes: edit tools work only in the editor's Editing state and return `INVALID_STATE` while an editor dialog is open, the left mouse button is held, or play mode is active. The plugin selects the object it edits and leaves it selected. Edits are in memory until `save_mod`. Values may be clamped; the reply shows what was applied. `undo` reverts edits (no redo exists; object creation is not undoable, use `delete_object`; after undoing a component add check `extraUndoEntries`).

Playtest notes: call `validate_mod` first; `start_playtest` is refused with `INVALID_STATE` when the validator reports errors and the error result then carries the validator issues. Any `error.data` the plugin sends is appended to the error text as `Data: <compact JSON>` (cut at 4,000 characters, token scrubbed). `start_playtest` saves the mod to disk (Meta, scene, settings), applies `players` (2-8 or a toolbar label such as `"4 Players"`, `"2v2 Teams"`, `"3v3"`, case-insensitive) and `aiOnly` (AIs play by themselves; otherwise a human must play player 1, the plugin sends no input) and returns once play mode runs. Verify mechanics with `get_playtest_state` (per-player alive/health/score/position/deaths, minigame state/round/time, `useHealth` and a `note` explaining that with health off a Change Health value of 0 does nothing and a non-zero value whose result is 0 or less kills) and `get_playtest_events` (died/respawned, health and score changes, rounds, minigame results, trigger fired / action executed; ring of 500, cleared when the next playtest starts, so `stop_playtest` first and read afterwards works); also `get_status` (`playtest` block, now with `eventCount`, `alivePlayers`, `lastEventId`), `get_log` and `screenshot`; during play `get_object`, `get_action_graph`, `get_open_mod`, `validate_mod` and every edit tool return `INVALID_STATE` (`get_playtest_state` is the opposite: it needs a running playtest); always `stop_playtest` before editing again. A minimal valid Minigame mod needs NumberOfRounds > 0 (default 1) and one active Player Spawn (`create_object` kind `playerSpawn`).

Batch notes: `run_batch` steps are `{tool, params?, id?}` (`tool` is an enum of the 27 allowed tools; `params` is passed through unchanged and validated by the plugin per step; `id` matches `[A-Za-z][A-Za-z0-9_-]{0,63}` and is unique). Any string value in `params` starting with `$` is a placeholder for an earlier step's result (`"$floor.guid"`, `"$dup.objects[0].guid"`); `"$$x"` is a literal. There is no rollback (object creation cannot be undone); `stopOnError` defaults to true; step failures are reported inside `results`, each write step is its own undo step.

Action editing notes: a trigger is addressed either as `{guid, component?, trigger, path?}` (`trigger` = the `triggerName` from `get_action_graph`, e.g. `OnEnterActions` or `Triggers[0]`) or as `{scope: "playerSettings", trigger, path?}` (Minigame mods; `weaponHitTrigger`, `tickTrigger`, `onDeathTrigger`, `onRespawnTrigger`, `onFirstSpawnTrigger`, `onRoundEndTrigger`). `path` (max 8 steps of `{action, choice}`) enters nested Display Popup choice lists. The tool schema is one flat object with optional fields plus a validation rule (not a top-level union, which MCP clients cannot use as an input schema), so malformed addresses, a `path` longer than 8, negative indexes and a missing `type` on `add_action` are rejected before reaching the plugin. `parameters` and `targets` are arbitrary JSON passed through. Call `list_action_types` with the trigger address first to see which actions are usable there and their parameter names, types and limits. Default call-site targets are applied on append only; values may be clamped (see `clamped` and the returned action). Some fixed slots are only editable while a flag of the component is on (`editable`/`reason` in `get_action_graph`, e.g. `TriggerOnEnter`). Logic components have a user-editable trigger list (`add_trigger`, `set_trigger`, `remove_trigger`); other components have fixed slots. Edits of object triggers are undoable (one call = one undo step); player-settings trigger edits are NOT undoable and are written by `save_mod`. Full contract: `docs/plugin-api.md`.

## Recommended workflow

1. `get_status`: is the editor open, which mod, state `Editing`?
2. `list_mods`, then `open_test_mod` (or `create_test_mod`).
3. `get_hierarchy`: object guids.
4. Edits: `create_object`, `set_transform`, `add_component` (see `list_component_types`), `set_property` / `set_properties`, `reparent_object`, ...
5. Actions: `create_object` kind `trigger` (or `logic`) -> `set_property` `TriggerOnEnter` true (makes the slot editable) -> `get_action_graph` (`triggerName`, `editable`, `callSite`) -> `list_action_types` with that trigger address -> `add_action` / `set_action` / `move_action` / `remove_action` (Logic: `list_trigger_types`, `add_trigger`, `set_trigger`, `remove_trigger`) -> `get_action_graph` to verify.
6. `get_object` to verify (and `undo` if wrong). Many edits at once: `run_batch` (no rollback).
7. `validate_mod`, then `save_mod`.
8. `start_playtest` (`aiOnly: true` runs unattended; saves the mod) -> verify with `get_playtest_state` / `get_playtest_events` (plus `get_status` / `get_log` / `screenshot`) -> `stop_playtest` before editing again. `publishedFileId` `"256"` in `get_open_mod` / `create_test_mod` / `open_test_mod` means never published (the game's local id for unpublished mods), not a Workshop item or minigame id.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `PUMMEL_PARTY_MCP_PORT` | `38917` | Plugin port |
| `PUMMEL_PARTY_MCP_GAME_DIR` | auto-detected (Windows) | Game folder; token is read from `<GameDir>\BepInEx\config\PummelPartyMcp.token` |
| `PUMMEL_PARTY_MCP_TOKEN_FILE` | (derived) | Full path of the token file; overrides the above |
| `PUMMEL_PARTY_MCP_TIMEOUT_MS` | (per tool) | Overrides every request timeout (1..600000 ms); mainly for tests |

Token file lookup order: `PUMMEL_PARTY_MCP_TOKEN_FILE`, `PUMMEL_PARTY_MCP_GAME_DIR`, then on Windows Steam library discovery: Steam's `steamapps\libraryfolders.vdf` (in `%ProgramFiles(x86)%\Steam` or `%ProgramFiles%\Steam`) lists the libraries, and the one with `steamapps\appmanifest_880940.acf` (Pummel Party's app id) gives the game folder through its `installdir`. The parser is a small built-in one (no dependency). Detection is lazy (first tool call that needs the token), cached, repeated after a failure, and never prevents startup; when nothing is found the error names both variables. Linux and macOS: only the two variables (the tested setup is Windows).

The token is cached and re-read after a 401 or connection failure, so game restarts need no special handling. It is never logged or returned (it is also scrubbed from any text a listener sends back).

Token and port: the token is sent to whatever process listens on `127.0.0.1:<port>`, so anything that squats the port would receive it. Do not expose the port (no port forwarding or proxying) and keep the plugin's port private. HTTP redirects are refused (`redirect: "error"`), so a listener cannot bounce the token and request body to another port; the call then fails with "answered with a redirect; it is not the pummel-party-mcp plugin".

## Build and test

```
npm ci
npm run build
npm test
```

`npm pack --dry-run` lists what a package would contain (`dist/*.js`, `README.md`, `LICENSE`, `package.json`; `prepack` builds first). The package is marked `"private": true` and its repository fields are placeholders until the maintainer decides to publish.

## Register with Claude Code (example only)

```
claude mcp add pummel-party-mcp -- node <abs path>\dist\index.js
```

Asset tools (0.8.0): `get_asset`, `list_asset_rules`, `import_asset`, `set_import_settings`, `create_material`, `set_material`, `remove_asset` (and `list_assets` with `location`). `import_asset` copies a local absolute path into the sandbox mod (the source is only read; UNC/network paths are refused; size limits are plugin config values), `remove_asset` deletes only the mod's own custom assets and returns `ASSET_IN_USE` with the references while the asset is used. `import_asset`, `create_material` and `remove_asset` change files immediately and are not undoable; none of them is allowed inside `run_batch`. Long-form rules: `usage_guide` topic `assets`.
