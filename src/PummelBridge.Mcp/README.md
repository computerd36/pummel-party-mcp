# PummelBridge.Mcp

MCP server (stdio) that passes tool calls through to the PummelBridge BepInEx plugin running inside Pummel Party (`POST http://127.0.0.1:<port>/rpc`). It only ever connects to `127.0.0.1`.

Tools (same names as plugin methods, one MCP tool per method, 25 in total):

- Read-only: `ping`, `get_status`, `get_open_mod`, `get_hierarchy`, `get_object`, `get_action_graph`, `list_assets`, `list_component_types`, `get_log`, `screenshot`, `list_mods`.
- Mod lifecycle (write): `create_test_mod`, `open_test_mod`, `save_mod`.
- Object/component editing (write): `create_object`, `duplicate_object`, `delete_object`, `reparent_object`, `set_object`, `set_transform`, `add_component`, `remove_component`, `set_property`, `set_properties`, `undo`.

All but `ping` and `list_mods` need the game running (most also the Mod Editor open).

Safety model: write tools only ever act on sandbox mods whose name starts with `PB_TEST_` (name rule `^PB_TEST_[A-Za-z0-9_ -]+$`, max 64 chars, no leading/trailing space; validated here and in the plugin); the plugin refuses anything else. The bridge cannot open, modify, rename or delete the author's real mods, and Workshop publishing is blocked while the plugin is loaded; there is no tool for it. `create_test_mod`/`open_test_mod` are refused while a non-sandbox mod is open (the human must close it by hand); `open_test_mod` will not discard unsaved changes unless `discardUnsaved` is true. There is no generic "call any method" tool. Timeouts: 15 s default, 20 s edit tools, 30 s `screenshot`/`save_mod`/`undo`, 45 s `create_test_mod`/`open_test_mod`.

Editing notes: edit tools work only in the editor's Editing state and return `INVALID_STATE` while an editor dialog is open, the left mouse button is held, or play mode is active. The bridge selects the object it edits and leaves it selected. Edits are in memory until `save_mod`. Values may be clamped; the reply shows what was applied. `undo` reverts edits (no redo exists; object creation is not undoable, use `delete_object`; after undoing a component add check `extraUndoEntries`). Triggers/actions are not settable yet. Full contract: `docs/bridge-api.md`.

## Recommended workflow

1. `get_status`: is the editor open, which mod, state `Editing`?
2. `list_mods`, then `open_test_mod` (or `create_test_mod`).
3. `get_hierarchy`: object guids.
4. Edits: `create_object`, `set_transform`, `add_component` (see `list_component_types`), `set_property` / `set_properties`, `reparent_object`, ...
5. `get_object` to verify (and `undo` if wrong).
6. `save_mod`.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `PUMMELBRIDGE_PORT` | `38917` | Plugin port |
| `PUMMELBRIDGE_GAME_DIR` | `C:\Program Files (x86)\Steam\steamapps\common\Pummel Party` | Game dir; token is read from `<GameDir>\BepInEx\config\PummelBridge.token` |
| `PUMMELBRIDGE_TOKEN_FILE` | (derived) | Full path of the token file; overrides the above |

The token is cached and re-read after a 401 or connection failure, so game restarts need no special handling. It is never logged or returned.

## Build and test

```
npm install
npm run build
npm test
```

## Register with Claude Code (example only)

```
claude mcp add pummelbridge -- node <abs path>\dist\index.js
```
