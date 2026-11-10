# PummelBridge.Mcp

Read-only MCP server (stdio) that passes tool calls through to the PummelBridge BepInEx plugin running inside Pummel Party (`POST http://127.0.0.1:<port>/rpc`). It only ever connects to `127.0.0.1`.

Tools (same names as plugin methods): `ping`, `get_status`, `get_open_mod`, `get_hierarchy`, `get_object`, `get_action_graph`, `list_assets`, `get_log`, `screenshot`. All are read-only; all but `ping` need the game running with the Mod Editor open.

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
