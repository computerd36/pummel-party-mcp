# PummelBridge.Mcp

MCP server (stdio) that passes tool calls through to the PummelBridge BepInEx plugin running inside Pummel Party (`POST http://127.0.0.1:<port>/rpc`). It only ever connects to `127.0.0.1`.

Tools (same names as plugin methods): `ping`, `get_status`, `get_open_mod`, `get_hierarchy`, `get_object`, `get_action_graph`, `list_assets`, `get_log`, `screenshot`, `list_mods`, plus three write tools: `create_test_mod`, `open_test_mod`, `save_mod`. All except those three are read-only; all but `ping` and `list_mods` need the game running (most also the Mod Editor open).

Safety model: write tools only ever act on sandbox mods whose name starts with `PB_TEST_` (name rule `^PB_TEST_[A-Za-z0-9_ -]+$`, max 64 chars, no leading/trailing space; validated here and in the plugin); the plugin refuses anything else. The bridge cannot open, modify, rename or delete the author's real mods, and Workshop publishing is blocked while the plugin is loaded; there is no tool for it. `create_test_mod`/`open_test_mod` are refused while a non-sandbox mod is open (the human must close it by hand); `open_test_mod` will not discard unsaved changes unless `discardUnsaved` is true. There is no generic "call any method" tool. Timeouts: 15 s default, 30 s `screenshot`/`save_mod`, 45 s `create_test_mod`/`open_test_mod`.

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
