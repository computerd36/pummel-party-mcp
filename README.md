# pummel-party-mcp

pummel-party-mcp lets an AI agent see and edit what is open in Pummel Party's built-in Mod Editor through the Model Context Protocol (MCP): objects, components and their properties, Action Editor logic (triggers and actions), the editor's validator and playtests. It has two parts: a BepInEx 5 plugin that runs inside the game and serves a token-protected API on `127.0.0.1`, and an MCP server (Node.js, stdio) that exposes one tool per plugin method to an MCP client such as Claude Code.

Unofficial project, not affiliated with Rebuilt Games.

It is an editor tool, not a cheat tool. In normal gameplay it does nothing, with one consequence you must know about: **while the plugin is enabled, publishing to the Steam Workshop is blocked** (create, update and delete), on purpose, so that a bug or a bad AI edit cannot reach a live Workshop item. To publish a mod by hand, quit the game, set `[General] PluginEnabled = false` in the plugin config (see [Configuration](#configuration)), start the game, publish as usual, quit, and set it back to `true`.

## Use at your own risk

- **No endorsement.** This project is not endorsed, supported or approved by Rebuilt Games or by Valve. Pummel Party has no separate developer licence agreement that we could find; the [Steam Subscriber Agreement](https://store.steampowered.com/subscriber_agreement/) applies. Its wording on third-party software and on reverse engineering is broad, and nobody has confirmed that a plugin like this one is permitted. You decide whether to use it; see [docs/legal-notes.md](docs/legal-notes.md) for what was checked. Nothing here is legal advice.
- **Disable the plugin for normal play.** BepInEx loads the plugin every time the game starts, including for online matches. It does nothing there except keep the Workshop publish block and a few record-only hooks installed, but a third-party plugin loaded during online play is exactly what the agreement's anti-cheat wording describes. When you are not modding, set `[General] PluginEnabled = false` in the plugin config (see [Configuration](#configuration)) and restart the game, or keep a separate game shortcut without BepInEx.
- **You are responsible for what you publish.** The plugin never publishes anything. If you publish a mod that an AI agent helped build, you do it by hand, under the Steam Workshop terms, as its author.
- **No warranty** (see the [licence](#licence)). Back up your mod projects before you start (see [Safety model](#safety-model)).

## Status

Pre-1.0 (0.7.0). Tested on Pummel Party game version `1.14.1k` (Windows, Steam), Unity 2021.3, Mono runtime. Interfaces may change between versions.

## Safety model

What you can check:

- Sandbox mods only. Write tools act only on mods whose name starts with `MCP_` (exact, case-sensitive; letters, digits, `_`, `-` and spaces; at most 64 characters). The rule is hardcoded; there is no setting or allowlist.
- Real mods cannot be opened, changed, renamed or deleted by the tools. `create_test_mod` and `open_test_mod` refuse while a real mod is open; you close it by hand.
- No mod deletion, no asset import or removal, no Workshop tool of any kind.
- Workshop create/update/delete is blocked at two layers: the game's own publish methods and the Steamworks write calls underneath them. It fails closed: if a block target cannot be patched (for example after a game update) or a patch is removed later, every write tool returns `PUBLISH_BLOCK_INCOMPLETE`. `get_status` shows the live state (`publishBlock.complete`).
- Loopback only. The server binds `127.0.0.1`, every request needs a per-install random token, and requests with an `Origin` header or a non-local `Host` are rejected.
- Game-version gate (0.7.0): on a game version the plugin was not tested with, write tools are refused with `GAME_VERSION_UNTESTED` until `[Safety] AcceptedGameVersion` equals the running version exactly (tested version: `1.14.1k`; read tools keep working; the config is re-read on the next call, no restart).
- Writes can be switched off with `[Safety] EnableWrites = false`.

What this does not protect against:

- The editor's own behaviour. If you open a real mod by hand, the game itself may write to its folder (for example it can create missing `.pmeta` files). The plugin only reads in that case, but it cannot stop the editor from doing what it always does.
- Saving is not atomic (it is the editor's own save). A crash during a save can leave a sandbox mod in a bad state. The plugin never saves automatically, except that `start_playtest` saves the sandbox mod, like the Play button does.
- The token file is readable by local processes of the same Windows user (and possibly other users, depending on the folder's permissions). A malicious local process is out of scope.
- A game update can change editor internals in ways the plugin does not detect.

Back up your mod projects before you use this, even though real mods are not touched. The repo has two scripts; neither script has a built-in destination. Either pass `-DestinationRoot` / `-BackupRoot` (outside the repo, outside the game folder, ideally on another drive) or copy `scripts/backup.settings.example.psd1` to `scripts/backup.settings.psd1` (untracked) and fill in `DestinationRoot`, `BackupRoots` and optionally `GameRoot`; parameters override the file:

```powershell
.\scripts\backup-mods.ps1 -DestinationRoot "D:\PummelBackups"
.\scripts\verify-backup.ps1 -BackupRoot "D:\PummelBackups"

# or, with scripts\backup.settings.psd1 in place:
.\scripts\backup-mods.ps1
.\scripts\verify-backup.ps1
```

The backup copies files only, writes a SHA-256 manifest and never modifies the source; the verify script is read-only.

## Requirements

- Pummel Party on Steam, Windows.
- BepInEx 5, x64, Mono build (developed against 5.4.23.5).
- .NET 8 SDK, to build the plugin (it targets .NET Framework 4.7.2 and uses reference assemblies from NuGet).
- Node.js 20 or newer, for the MCP server.

## Install and build

PowerShell, from the repo root. Replace the game folder with yours.

1. Install BepInEx 5 into the game folder. Download `BepInEx_win_x64_5.4.23.5.zip` from the [BepInEx releases](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) and extract it into the game folder (next to the game's executable). Start the game once and quit; `BepInEx\LogOutput.log` should exist.

2. Build the plugin. `GameDir` tells the build where the game's assemblies are; they are referenced from there and never copied into the repo or the build output, and the repo does not redistribute them.

   ```powershell
   $game = "C:\Program Files (x86)\Steam\steamapps\common\Pummel Party"
   dotnet build src\PummelPartyMcp.Plugin\PummelPartyMcp.Plugin.csproj -c Release -p:GameDir="$game"
   ```

   `GameDir` is resolved in this order: `-p:GameDir=...` on the command line; an untracked file `src\GameDir.props.user` that you create once (the plugin build and the patch tests both read it; `*.user` is gitignored); the standard Steam location (`%ProgramFiles(x86)%\Steam\steamapps\common\Pummel Party`). If the folder does not contain `PummelParty_Data\Managed\Assembly-CSharp.dll` the build stops with an error that says so. A `PummelPartyMcp.Plugin.csproj.user` next to the plugin project still works too.

   ```xml
   <!-- src\GameDir.props.user -->
   <Project><PropertyGroup><GameDir>D:\Games\Pummel Party</GameDir></PropertyGroup></Project>
   ```

3. Copy the plugin into the game folder. This is the only change to the game folder besides BepInEx itself.

   ```powershell
   New-Item -ItemType Directory -Force "$game\BepInEx\plugins\PummelPartyMcp" | Out-Null
   Copy-Item src\PummelPartyMcp.Plugin\bin\Release\PummelPartyMcp.Plugin.dll "$game\BepInEx\plugins\PummelPartyMcp\"
   ```

4. Start the game and check `BepInEx\LogOutput.log`. The plugin prints lines like these (each prefixed by BepInEx with level and plugin name):

   ```text
   PummelPartyMcp 0.7.0 (api v2); game version <version>, Unity <version>
   Workshop publish block active: 59 methods patched. Publishing to the Workshop is disabled while this plugin is enabled.
   Listening on http://127.0.0.1:38917/rpc (...); writes enabled, publish block complete
   ```

   The first run also creates the config file and the token file in `BepInEx\config\`.

5. Build the MCP server.

   ```powershell
   cd src\PummelPartyMcp.Server
   npm ci
   npm run build
   cd ..\..
   ```

6. Register it with your MCP client. In this repo, Claude Code picks up the project file `.mcp.json` when started in the repo root:

   ```json
   {
     "mcpServers": {
       "pummel-party-mcp": {
         "type": "stdio",
         "command": "node",
         "args": ["src/PummelPartyMcp.Server/dist/index.js"]
       }
     }
   }
   ```

   The server finds the plugin's token file by itself (see below), so this file needs no path or `env` block on a normal Steam install. The equivalent command, usable from any folder:

   ```powershell
   claude mcp add pummel-party-mcp -- node "<path to repo>\src\PummelPartyMcp.Server\dist\index.js"
   ```

   Add `--env PUMMEL_PARTY_MCP_GAME_DIR="$game"` if auto-detection does not find your game.

Environment variables of the MCP server:

| Variable | Default | Meaning |
|---|---|---|
| `PUMMEL_PARTY_MCP_PORT` | `38917` | Plugin port (must match `[Server] Port`) |
| `PUMMEL_PARTY_MCP_GAME_DIR` | auto-detected (Windows) | Game folder; the token is read from `<GameDir>\BepInEx\config\PummelPartyMcp.token` |
| `PUMMEL_PARTY_MCP_TOKEN_FILE` | derived from the game folder | Full path of the token file; overrides the game folder |
| `PUMMEL_PARTY_MCP_TIMEOUT_MS` | per tool | Overrides every request timeout (mainly for tests) |

Token file lookup, in this order: `PUMMEL_PARTY_MCP_TOKEN_FILE`, then `PUMMEL_PARTY_MCP_GAME_DIR`, then automatic detection on Windows: the server reads Steam's `steamapps\libraryfolders.vdf` (from `%ProgramFiles(x86)%\Steam` or `%ProgramFiles%\Steam`) and picks the library that holds `steamapps\appmanifest_880940.acf` (880940 is Pummel Party's Steam app id), using the `installdir` from that manifest. Detection runs on the first tool call that needs the token, is cached, runs again after a failure and never stops the server from starting. If nothing is found, the error names the two variables. On Linux and macOS only the two variables are supported (the tested setup is Windows). A Steam installation in an unusual place that is not listed in `libraryfolders.vdf` needs `PUMMEL_PARTY_MCP_GAME_DIR`.

## First use

Start the game, open the Mod Editor from the main menu, then start your MCP client and ask for something like:

> Create a sandbox minigame called MCP_Arena with a 20 by 20 floor and a player spawn, add a trigger zone that changes health, validate it, save it and run an AI-only playtest. Tell me what actually happened to the players.

The agent should follow this sequence:

1. `get_status` and `list_mods`; `create_test_mod` (name starts with `MCP_`).
2. Build with `create_object`, `add_component`, `set_property`, and the trigger/action tools, or many steps at once with `run_batch`.
3. Read back with `get_object` / `get_action_graph`.
4. `validate_mod`, then `save_mod`.
5. `start_playtest` with `aiOnly: true` (this saves the mod).
6. `get_playtest_state`, `get_playtest_events`, `screenshot`, `get_log` to see what happened.
7. `stop_playtest` before editing again.

The server also ships a guide as `usage_guide`; clients that do not show server instructions to the model can call it. The skill file `skills/pummel-modder/SKILL.md` is a short agent-facing version of the workflow.

## Tools

| Purpose | Tools |
|---|---|
| Status and reading | `ping`, `get_status`, `get_open_mod`, `get_hierarchy`, `get_object`, `get_action_graph`, `list_assets`, `get_log`, `screenshot` |
| Mod lifecycle | `list_mods`, `create_test_mod`, `open_test_mod`, `save_mod` |
| Objects | `create_object`, `duplicate_object`, `delete_object`, `reparent_object`, `set_object`, `set_transform` |
| Components and properties | `list_component_types`, `add_component`, `remove_component`, `set_property`, `set_properties` |
| Actions and triggers | `list_action_types`, `list_trigger_types`, `add_action`, `remove_action`, `move_action`, `set_action`, `add_trigger`, `remove_trigger`, `set_trigger` |
| Validation, playtest, observation | `validate_mod`, `start_playtest`, `stop_playtest`, `get_playtest_state`, `get_playtest_events` |
| Batch and undo | `run_batch`, `undo` |
| Guide and self-check | `usage_guide`, `self_test` |

0.7.0 adds the read tool `self_test` and stricter `set_property` rules that follow what each component's inspector really shows. The full contract (parameters, results, error codes, timeouts, play-mode behaviour) is in [docs/plugin-api.md](docs/plugin-api.md); the server is described in [src/PummelPartyMcp.Server/README.md](src/PummelPartyMcp.Server/README.md).

## Limits and known gaps

- No redo (the editor has none). `undo` works like Ctrl+Z; object creation is not undoable (use `delete_object`).
- No input injection: a playtest is AI-only (`aiOnly: true`) or driven by a human at the keyboard.
- Some property types are not settable (for example triggers and action lists through `set_property`, `ModWeaponInfo`, `BoardNodeConnection[]`); use the dedicated tools or the editor.
- No asset import, creation or removal; no editing of mod settings except the player-settings triggers (description, preview image, rounds and end conditions are set by hand; the Use Health setting too).
- Board mods: connections cannot be edited, so a Board mod cannot be made valid through the plugin; runtime visibility of board playtests is basic.
- Only tested on Windows, Steam, game version `1.14.1k`.

## Configuration

`BepInEx\config\com.pummelpartymcp.plugin.cfg` (created on first start; read at game start unless noted):

| Key | Default | Effect |
|---|---|---|
| `[General] PluginEnabled` | `true` | `false`: the plugin does nothing (no server, no patches, so Workshop publishing works). |
| `[Safety] EnableWrites` | `true` | Read on every write call. `false`: write tools return `WRITES_DISABLED`. Does not affect the publish block. |
| `[Safety] AcceptedGameVersion` | not set | 0.7.0: set it to accept an untested game version so writes are allowed again (see below). |
| `[Server] Enabled` | `true` | `false`: no API server; the publish block stays active. |
| `[Server] Port` | `38917` | Loopback port (1024-65535). If it cannot be bound the plugin stays idle; the publish block stays active. |

The token is in `BepInEx\config\PummelPartyMcp.token` (64 hex characters, created on first start).

## After a game update

- A new game version is not trusted automatically. Writes are refused with `GAME_VERSION_UNTESTED` until `[Safety] AcceptedGameVersion` is set in the config file. Do that only after you have checked the points below.
- If a Workshop block target no longer resolves, the block is incomplete and writes stay off (`PUBLISH_BLOCK_INCOMPLETE`). Reading keeps working. Do not publish through the plugin in that state; it is the plugin that blocks publishing, so there is nothing to bypass.
- Run the read tool `self_test` (it re-resolves every game member the plugin needs, the publish block and the inspector audit; `ok: true` is the evidence to look for) and check `get_log` and `BepInEx\LogOutput.log` for errors; a plugin error naming a missing game member (`game version changed?`) means an editor internal moved.
- The repo has an offline drift test (`PummelPartyMcp.Plugin.PatchTests`) that loads the game's assemblies for reflection only and checks that every Workshop block target still resolves; see [Development](#development).
- Back up your mods, try on a fresh `MCP_` mod, and report breakage as an issue.

## Development

Layout:

| Path | Content |
|---|---|
| `src/PummelPartyMcp.Plugin` | BepInEx plugin (C#, net472): `Core` (Unity-free: HTTP, auth, dispatch, rules), `Game` (editor access), `Tools`, `Safety`, `Hooks` |
| `src/PummelPartyMcp.Plugin.Tests` | net8.0 console harness for `Core`; needs no game |
| `src/PummelPartyMcp.Plugin.PatchTests` | net472 Harmony and drift tests; reads the game DLLs for reflection only |
| `src/PummelPartyMcp.Server` | MCP server (TypeScript) and its tests |
| `docs/` | `plugin-api.md` (API reference), `editor-architecture.md` and `architecture/` (how the editor works), `mod-format.md` |
| `scripts/` | backup and verify scripts; `package-thunderstore.ps1` |
| `packaging/thunderstore/` | templates for the Thunderstore package (README, CHANGELOG, icon placeholder) |
| `.github/workflows/ci.yml` | CI (see below) |
| `skills/pummel-modder/` | agent skill |

Tests (PowerShell, from the repo root):

```powershell
dotnet run -c Release --project src\PummelPartyMcp.Plugin.Tests
dotnet run -c Release --project src\PummelPartyMcp.Plugin.PatchTests   # add -p:GameDir="$game" unless set in src\GameDir.props.user
cd src\PummelPartyMcp.Server; npm test
```

The patch tests need a local game install with BepInEx (they reference its `0Harmony.dll` and related assemblies). The first two suites print their results to the console; the server tests build the project and run Node's test runner against a fake plugin.

CI (GitHub Actions, `.github/workflows/ci.yml`) covers what can be built without the game: the MCP server tests (Ubuntu and Windows, Node 20 and 24), the Unity-free `PummelPartyMcp.Plugin.Tests` harness (.NET 8; it compiles only the plugin's `Core` sources and is run with `GameDir` pointing nowhere), and a guard that fails when a `*.dll`, `*.pdb`, `*.exe`, `*.token`, a `decompiled/` folder or a file over 5 MB is tracked. The plugin build and `PummelPartyMcp.Plugin.PatchTests` are **not** run in CI, because they need the game's assemblies; run them locally with the game installed, and re-run the drift test (`PatchTests`) after every game update. The workflow has no secrets and publishes nothing.

Packaging (prepared only, nothing is published): `scripts\package-thunderstore.ps1` builds the plugin in Release and assembles `artifacts\thunderstore\` plus `artifacts\PummelPartyMcp-<version>.zip` (the plugin DLL only; the version comes from the plugin csproj; the script refuses to run with uncommitted changes under `src\PummelPartyMcp.Plugin` unless you pass `-AllowDirty`; `artifacts/` is gitignored). The server's `package.json` is ready for `npm pack` and `npm publish` (`prepack` builds, `files` is an allow-list) but keeps `"private": true` and placeholder repository fields until the maintainer decides to publish.

Game DLLs, decompiled game code and other people's mod files must never be committed. `decompiled/`, `lib/`, `*.dll` and similar are in `.gitignore`; keep it that way. `CLAUDE.md` holds the working rules for AI agents that contribute to this repo, and `PLAN.md` the project plan and decision log.

## Licence

MIT, see [LICENSE](LICENSE). Pummel Party and its assets belong to Rebuilt Games and are not included in this repository.
