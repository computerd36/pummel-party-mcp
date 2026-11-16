# pummel-party-mcp

pummel-party-mcp lets an AI agent see and edit what is open in Pummel Party's built-in Mod Editor through the Model Context Protocol (MCP): objects, components and their properties, Action Editor logic (triggers and actions), the editor's validator and playtests. It has two parts: a BepInEx 5 plugin that runs inside the game and serves a token-protected API on `127.0.0.1`, and an MCP server (Node.js, stdio) that exposes one tool per plugin method to an MCP client. Claude Code is the supported client.

Unofficial project, not affiliated with Rebuilt Games.

## Quick install

Windows and Steam. Read [Use at your own risk](#use-at-your-own-risk) first, and back up your mod projects (see [Safety model](#safety-model)).

1. **Install BepInEx and the plugin.**
   - With a mod manager (recommended): in a Thunderstore-compatible mod manager, select the game Pummel Party, search for `PummelPartyMcp` (team `computerd36`) and install it. The manager installs BepInEx 5 as a dependency. Start the game from the manager.
   - Manually: install BepInEx 5 (x64, Mono build, tested with 5.4.23.5) into the game folder: download `BepInEx_win_x64_5.4.23.5.zip` from the [BepInEx releases](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) and extract it next to the game's executable. Then download `PummelPartyMcp-<version>.zip` from the [GitHub releases](https://github.com/computerd36/pummel-party-mcp/releases), extract it, and copy its `BepInEx` folder into the game folder (merge the folders). The other files in the zip (manifest, README, CHANGELOG, icon) are not needed. Each release page lists the SHA-256 of the zip; to check it, run `Get-FileHash .\PummelPartyMcp-<version>.zip -Algorithm SHA256` in PowerShell.

   The game folder is usually `<Steam>\steamapps\common\Pummel Party`. After the next start, `BepInEx\LogOutput.log` shows lines from `PummelPartyMcp` (see [Check that the plugin runs](#check-that-the-plugin-runs)).

2. **Add the server to Claude Code.** Node.js 22 or newer must be installed. Run this once, from any folder:

   ```powershell
   claude mcp add pummel-party-mcp -- cmd /c npx -y pummel-party-mcp
   ```

   Which command you need depends on where the plugin is installed:

   - **Installed manually into the game folder:** the command above is enough. The server finds the game and the plugin's token file by itself on a normal Steam install, so no path is needed. If it does not find your game, add the game folder (this is the only setting most people ever need):

     ```powershell
     claude mcp add pummel-party-mcp --env PUMMEL_PARTY_MCP_GAME_DIR="D:\Games\Pummel Party" -- cmd /c npx -y pummel-party-mcp
     ```

   - **Installed with a mod manager:** the plugin lives in the manager's profile folder, so the token file is there too, and the automatic search through Steam does not find it. Tell the server where it is. Use the manager's "browse profile folder" function to open the profile folder; the token file is `<profile folder>\BepInEx\config\PummelPartyMcp.token` (it exists after the game has been started once with the plugin):

     ```powershell
     claude mcp add pummel-party-mcp --env PUMMEL_PARTY_MCP_TOKEN_FILE="<profile folder>\BepInEx\config\PummelPartyMcp.token" -- cmd /c npx -y pummel-party-mcp
     ```

   All settings of the server are listed under [Environment variables of the MCP server](#environment-variables-of-the-mcp-server).

3. **Start the game and open the Mod Editor** from the main menu. Then start Claude Code (or restart it, so that it loads the new server) and approve the `pummel-party-mcp` server if asked.

4. **Try a first prompt:**

   > Create a sandbox minigame called MCP_Arena with a 20 by 20 floor and a player spawn, add a trigger zone that changes health, validate it, save it and run an AI-only playtest. Tell me what actually happened to the players.

   Write tools only work on sandbox mods whose name starts with `MCP_`. Your real mods are never changed. More on the workflow is in [First use](#first-use).

If something does not work, see [Troubleshooting](#troubleshooting).

## What it does

It is an editor tool, not a cheat tool. An agent can look at the open mod, build objects, components and logic, change mod settings, import textures, meshes and sounds into a sandbox mod, create prefabs, validate the mod, run an AI-only playtest and read back what actually happened (player state, events, screenshots, log). What exists is listed in [Tools](#tools); what is planned is in [ROADMAP.md](ROADMAP.md); what changed is in [CHANGELOG.md](CHANGELOG.md).

**While the plugin is enabled, publishing to the Steam Workshop is blocked** (create, update and delete), on purpose, so that a bug or a bad AI edit cannot reach a live Workshop item. To publish a mod by hand, quit the game, set `[General] PluginEnabled = false` in the plugin config (see [Configuration](#configuration)), start the game, publish as usual, quit, and set it back to `true`.

## Use at your own risk

- **No endorsement.** This project is not endorsed, supported or approved by Rebuilt Games or by Valve. Pummel Party has no separate developer licence agreement that we could find; the [Steam Subscriber Agreement](https://store.steampowered.com/subscriber_agreement/) applies. Its wording on third-party software and on reverse engineering is broad, and nobody has confirmed that a plugin like this one is permitted. You decide whether to use it; see [docs/legal-notes.md](docs/legal-notes.md) for what was checked. Nothing here is legal advice.
- **Disable the plugin for normal play.** BepInEx loads the plugin every time the game starts, including for online matches. It does nothing there except keep the Workshop publish block and a few record-only hooks installed, but a third-party plugin loaded during online play is exactly what the agreement's anti-cheat wording describes. When you are not modding, set `[General] PluginEnabled = false` in the plugin config (see [Configuration](#configuration)) and restart the game, or keep a separate game shortcut without BepInEx (a mod-manager profile without the plugin does the same).
- **You are responsible for what you publish.** The plugin never publishes anything. If you publish a mod that an AI agent helped build, you do it by hand, under the Steam Workshop terms, as its author.
- **No warranty** (see the [licence](#licence)). Back up your mod projects before you start (see [Safety model](#safety-model)).

## Safety model

What you can check:

- Sandbox mods only. Write tools act only on mods whose name starts with `MCP_` (exact, case-sensitive; letters, digits, `_`, `-` and spaces; at most 64 characters). The rule is hardcoded; there is no setting or allowlist.
- Real mods cannot be opened, changed, renamed or deleted by the tools. `create_test_mod` and `open_test_mod` refuse while a real mod is open; you close it by hand.
- No mod deletion, no mod rename, no Workshop tool of any kind. Assets are handled only by the bounded tools below (0.8.0): `import_asset` copies a file you name into the open sandbox mod's `Assets` folder and never touches the source (it is only read; never moved, changed, locked or deleted); it writes only to a fixed sub-folder (`Textures`, `Props`, `Audio`) under a checked, plain name, never overwrites (a taken name becomes `name (1)`), accepts only `.png .jpg .jpeg .obj .wav .mp3 .ogg` whose content matches, refuses local-path tricks (UNC/network/device paths are not supported yet), and is limited by `[Assets] MaxImportFileMB` (50) and `MaxModAssetsMB` (500). If the game cannot import the copy, only what that call created is removed. `remove_asset` deletes only the open sandbox mod's own custom textures, props, audio clips, materials and prefabs (never built-in assets or the open prefab; the path must be strictly inside the mod's `Assets` folder; refused while the asset is still used unless `force`). Imports, material changes and removals write or delete files immediately and are not undoable. The editor's own `AddAsset(copy: true)` is never called (it can delete the source file). Prefabs (0.8.0): `create_prefab` (empty, or a copy of a scene object), `open_prefab` and `close_prefab` enter and leave the prefab edit context like the editor's UI; creating and closing write the `.pfab` immediately. Because a `.pfab` the game fails to write completely makes the whole mod unloadable, every prefab write the plugin triggers (create, close, and each prefab inside `save_mod`) is serialised in memory first (refused if that fails), checked afterwards (header and length) and restored from a copy kept before the write if it is bad; a new prefab's files are removed on failure. Prefab names never overwrite an existing asset or file.
- Links (junctions, symlinks, mount points, hard links) inside a sandbox mod are never followed for writes or deletes: the tools refuse (`LINK_REFUSED`) and write nothing.
- Workshop create/update/delete is blocked at two layers: the game's own publish methods and the Steamworks write calls underneath them. It fails closed: if a block target cannot be patched (for example after a game update) or a patch is removed later, every write tool returns `PUBLISH_BLOCK_INCOMPLETE`. `get_status` shows the live state (`publishBlock.complete`).
- Loopback only. The server binds `127.0.0.1`, every request needs a per-install random token, and requests with an `Origin` header or a non-local `Host` are rejected.
- Game-version gate (0.7.0): on a game version the plugin was not tested with, write tools are refused with `GAME_VERSION_UNTESTED` until `[Safety] AcceptedGameVersion` equals the running version exactly (tested version: `1.14.1k`; read tools keep working; the config is re-read on the next call, no restart).
- Writes can be switched off with `[Safety] EnableWrites = false`.

What this does not protect against:

- The editor's own behaviour. If you open a real mod by hand, the game itself may write to its folder (for example it can create missing `.pmeta` files). The plugin only reads in that case, but it cannot stop the editor from doing what it always does.
- Saving is not atomic (it is the editor's own save). A crash during a save can leave a sandbox mod in a bad state. The plugin never saves automatically, except that `start_playtest` saves the sandbox mod, like the Play button does.
- The token file is readable by local processes of the same Windows user (and possibly other users, depending on the folder's permissions). A malicious local process is out of scope.
- A game update can change editor internals in ways the plugin does not detect.

Back up your mod projects before you use this, even though real mods are not touched. The repo has two scripts; neither script has a built-in destination. Either pass `-DestinationRoot` / `-BackupRoot` (outside the repo, outside the game folder, ideally on another drive) or copy `scripts/backup.settings.example.psd1` to `scripts/backup.settings.psd1` (untracked) and fill in `DestinationRoot`, `BackupRoots` and optionally `GameRoot`; parameters override the file. The scripts are in the repository; with a release install you can get them from the repository's `scripts` folder:

```powershell
.\scripts\backup-mods.ps1 -DestinationRoot "D:\PummelBackups"
.\scripts\verify-backup.ps1 -BackupRoot "D:\PummelBackups"

# or, with scripts\backup.settings.psd1 in place:
.\scripts\backup-mods.ps1
.\scripts\verify-backup.ps1
```

The backup copies files only, writes a SHA-256 manifest and never modifies the source; the verify script is read-only.

## What is active during normal play

Today the plugin is loaded every time the game starts, with BepInEx. Outside the Mod Editor it does the following:

- It keeps the Workshop publish block installed.
- It installs a few record-only hooks (they record what happens and change nothing; most return at once outside a playtest).
- It runs the API server on `127.0.0.1` with its token. Write tools only act on `MCP_` sandbox mods, and need the editor open.

To have nothing of it active, set `[General] PluginEnabled = false` (see [Configuration](#configuration)) or start the game without the plugin.

**TODO (planned for 1.0.0, not implemented yet; replace this paragraph with the final text once it is):** the plugin is active only while the Mod Editor is open. The server, the hooks and the Workshop publish block start when the editor opens and stop when it closes. In normal play and online matches nothing of the plugin is active.

## Supported setup

- Windows with the Steam version of Pummel Party. Tested on game version `1.14.1k` (Unity 2021.3, Mono runtime). Other platforms and game versions are best effort: on an untested game version write tools are refused until you accept it (see [After a game update](#after-a-game-update)).
- BepInEx 5, x64, Mono build (developed against 5.4.23.5).
- Node.js 22 or newer, for the MCP server.
- Claude Code as the MCP client. Other clients may work but are not documented or tested.
- No .NET SDK is needed to use it. The .NET 8 SDK is only needed to build the plugin yourself (see [Development](#development)).
- The plugin and the server are released together and share one version number (see [CHANGELOG.md](CHANGELOG.md)). Interfaces may change between versions.
- Support is through [GitHub issues](https://github.com/computerd36/pummel-party-mcp/issues) with the bug report form.

## Check that the plugin runs

Start the game and check `BepInEx\LogOutput.log`. The plugin prints lines like these (each prefixed by BepInEx with level and plugin name):

```text
PummelPartyMcp <version> (api v2); game version <version>, Unity <version>
Workshop publish block active: 59 methods patched. Publishing to the Workshop is disabled while this plugin is enabled.
Listening on http://127.0.0.1:38917/rpc (...); writes enabled, publish block complete
```

The first run also creates the config file and the token file in `BepInEx\config\`. In Claude Code you can ask the agent to call `get_status` or `self_test` to check the whole chain.

## First use

Start the game, open the Mod Editor from the main menu, then start your MCP client and ask for something like the prompt in [Quick install](#quick-install).

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
| Mod settings | `list_mod_settings`, `get_mod_settings`, `set_mod_settings`, `capture_preview_image` |
| Assets (0.8.0) | `list_asset_rules`, `get_asset`, `import_asset`, `set_import_settings`, `create_material`, `set_material`, `remove_asset` (`list_assets` also takes `location: external`) |
| Prefabs (0.8.0) | `create_prefab`, `open_prefab`, `close_prefab` (`get_asset` shows a prefab's object tree and instances; edits inside an open prefab use the normal edit tools) |
| Guide and self-check | `usage_guide` (topics: overview, safety, editing, actions, playtest, batch, settings, assets), `self_test` |

0.8.0 also adds the asset tools: import textures (`.png .jpg .jpeg`), meshes (`.obj`) and sounds (`.wav .mp3 .ogg`) from a local path into a sandbox mod, change their import settings, create and edit materials (the five built-in shader inspectors), look up where an asset is used, and remove the mod's own assets again. Read the safety model above before using them; the rules and limits are in [docs/plugin-api.md](docs/plugin-api.md#assets-import-import-settings-materials-removal) and `usage_guide` topic `assets`. 0.8.0 adds the mod settings tools: minigame and board details, minigame and player settings, music, lighting and screen effects (presets and custom values), board size and camera, and a preview image captured from the scene (this one creates a texture file in the sandbox mod). Settings are not undoable, are written by `save_mod`, and never touch the Workshop item. The server instructions are now short; the long rules are in `usage_guide` topics. 0.7.0 adds the read tool `self_test` and stricter `set_property` rules that follow what each component's inspector really shows. The full contract (parameters, results, error codes, timeouts, play-mode behaviour) is in [docs/plugin-api.md](docs/plugin-api.md); the server is described in [src/PummelPartyMcp.Server/README.md](src/PummelPartyMcp.Server/README.md).

## Limits and known gaps

- No redo (the editor has none). `undo` works like Ctrl+Z; object creation is not undoable (use `delete_object`).
- No input injection: a playtest is AI-only (`aiOnly: true`) or driven by a human at the keyboard.
- Some property types are not settable (for example triggers and action lists through `set_property`, `ModWeaponInfo`, `BoardNodeConnection[]`); use the dedicated tools or the editor.
- Assets: import only from local absolute paths (UNC/network/device paths are refused for now), no `.bmp`, `.mtl`, `.pmat`, `.pfab`, no overwrite or replace of an existing asset, no rename or duplicate of prefabs, no prefab from a subtree that holds object references or Prefab Instance / Board Space components, no material properties for shaders without a known inspector. Everything the asset tools do was checked against the game's code and unit-tested on temp folders, and tried in the game in a Phase 4 test run; what is still unconfirmed there is listed in docs/plugin-api.md ("Verified and unverified in the game"). Custom board spaces, Item mods and the legacy Bloom effect are not covered by the settings tools.
- Board mods: connections cannot be edited, so a Board mod cannot be made valid through the plugin; runtime visibility of board playtests is basic.
- Only tested on Windows, Steam, game version `1.14.1k`.

## Configuration

`BepInEx\config\com.pummelpartymcp.plugin.cfg` (created on first start; read at game start unless noted):

| Key | Default | Effect |
|---|---|---|
| `[General] PluginEnabled` | `true` | `false`: the plugin does nothing (no server, no patches, so Workshop publishing works). |
| `[Safety] EnableWrites` | `true` | Read on every write call. `false`: write tools return `WRITES_DISABLED`. Does not affect the publish block. |
| `[Safety] AcceptedGameVersion` | not set | 0.7.0: set it to accept an untested game version so writes are allowed again (see below). |
| `[Assets] MaxImportFileMB` | `50` | `import_asset`: largest source file in MB (1-1024). Read on every import call. |
| `[Assets] MaxModAssetsMB` | `500` | `import_asset`: largest total size of a sandbox mod's `Assets` folder after an import, in MB (1-8192). Read on every import call. |
| `[Assets] MaxAudioSeconds` | `600` | `import_asset`: longest audio clip in seconds (1-7200), read from the file header. Read on every import call. |
| `[Assets] MaxCompressedAudioMB` | `15` | `import_asset`: largest OGG/MP3 whose header gives no duration, in MB (1-1024). Read on every import call. |
| `[Server] Enabled` | `true` | `false`: no API server; the publish block stays active. |
| `[Server] Port` | `38917` | Loopback port (1024-65535). If it cannot be bound the plugin stays idle; the publish block stays active. |

The token is in `BepInEx\config\PummelPartyMcp.token` (64 hex characters, created on first start). Never post it.

### Environment variables of the MCP server

| Variable | Default | Meaning |
|---|---|---|
| `PUMMEL_PARTY_MCP_PORT` | `38917` | Plugin port (must match `[Server] Port`) |
| `PUMMEL_PARTY_MCP_GAME_DIR` | auto-detected (Windows) | Game folder; the token is read from `<GameDir>\BepInEx\config\PummelPartyMcp.token` |
| `PUMMEL_PARTY_MCP_TOKEN_FILE` | derived from the game folder | Full path of the token file; overrides the game folder |
| `PUMMEL_PARTY_MCP_TIMEOUT_MS` | per tool | Overrides every request timeout (mainly for tests) |

Token file lookup, in this order: `PUMMEL_PARTY_MCP_TOKEN_FILE`, then `PUMMEL_PARTY_MCP_GAME_DIR`, then automatic detection on Windows: the server takes the Steam folder from the registry (`HKCU\Software\Valve\Steam` `SteamPath`, then `HKLM\SOFTWARE\WOW6432Node\Valve\Steam` `InstallPath`), then tries `%ProgramFiles(x86)%\Steam` and `%ProgramFiles%\Steam`. It reads Steam's `steamapps\libraryfolders.vdf` in those folders and picks the library that holds `steamapps\appmanifest_880940.acf` (880940 is Pummel Party's Steam app id), using the `installdir` from that manifest. This only finds a game installed through Steam, not a plugin inside a mod manager profile, and a Steam path with non-ASCII characters may be missed in the registry step; in those cases set `PUMMEL_PARTY_MCP_GAME_DIR` or `PUMMEL_PARTY_MCP_TOKEN_FILE`. Detection runs on the first tool call that needs the token, is cached, runs again after a failure and never stops the server from starting. If nothing is found, the error names the two variables. On Linux and macOS only the two variables are supported (the tested setup is Windows). A Steam installation in an unusual place that is not listed in `libraryfolders.vdf` needs `PUMMEL_PARTY_MCP_GAME_DIR`.

## After a game update

For users:

- A new game version is not trusted automatically. Writes are refused with `GAME_VERSION_UNTESTED` until `[Safety] AcceptedGameVersion` is set in the config file. Reading keeps working.
- The safe choice is to wait for a new release that lists the new game version as tested.
- If you do not want to wait, you can accept the version at your own risk. Do it only after you have checked the points below: back up your mods, run the read tool `self_test` (it re-resolves every game member the plugin needs, the publish block and the inspector audit; `ok: true` is the evidence to look for), check `get_log` and `BepInEx\LogOutput.log` for errors, and try on a fresh `MCP_` mod. Then set `AcceptedGameVersion` in `BepInEx\config\com.pummelpartymcp.plugin.cfg` to the running game version, exactly as `get_status` shows it (no restart needed; the config is re-read on the next call). A plugin error naming a missing game member (`game version changed?`) means an editor internal moved.
- If a Workshop block target no longer resolves, the block is incomplete and writes stay off (`PUBLISH_BLOCK_INCOMPLETE`). Reading keeps working. Do not publish through the plugin in that state; it is the plugin that blocks publishing, so there is nothing to bypass.
- Report breakage as an issue.

For maintainers, after every game update:

1. Run the offline drift test (`PummelPartyMcp.Plugin.PatchTests`; see [Development](#development)). It loads the game's assemblies for reflection only and checks that every Workshop block target and every game member the plugin relies on still resolves.
2. Build the plugin and run `self_test` in the game (`ok: true`).
3. Do the in-game checks: open a fresh `MCP_` mod, build something, validate, save, run an AI-only playtest, and read the log. Back up your mods first.
4. Add the game version to the tested list in `src/PummelPartyMcp.Plugin/Core/GameVersionGate.cs` (`TestedVersions`), and update the tested version in this README and in the Thunderstore README.
5. Make a new release (see [docs/release-checklist.md](docs/release-checklist.md)).

## Troubleshooting

- **No `PummelPartyMcp` lines in `BepInEx\LogOutput.log`.** BepInEx is not installed or did not start (the log file does not exist), the plugin DLL is not in `BepInEx\plugins\`, or `[General] PluginEnabled` is `false`. Check the folder `BepInEx\plugins\PummelPartyMcp\` (a mod manager puts it in its own profile folder).
- **The agent says it cannot find the token or the game.** The server could not detect your Steam install. Set `PUMMEL_PARTY_MCP_GAME_DIR` (or `PUMMEL_PARTY_MCP_TOKEN_FILE`) in the server's registration, as shown in [Quick install](#quick-install). The game must have been started once with the plugin, so that the token file exists. With a mod manager, the token file is under the manager's profile folder, not the Steam game folder: point `PUMMEL_PARTY_MCP_TOKEN_FILE` at it.
- **Connection refused or timeout.** The game is not running, the plugin is disabled (`PluginEnabled`, `[Server] Enabled`), the port is taken by another program (the plugin then stays idle; see the log), or `PUMMEL_PARTY_MCP_PORT` does not match `[Server] Port`.
- **`GAME_VERSION_UNTESTED`.** The game was updated. See [After a game update](#after-a-game-update).
- **`PUBLISH_BLOCK_INCOMPLETE`.** A Workshop block target could not be patched, usually after a game update, and writes stay off on purpose. Read tools still work. Check `get_status` and the log, and report it as an issue.
- **`WRITES_DISABLED`.** `[Safety] EnableWrites` is `false`.
- **`GUARD_NOT_TEST_MOD`.** The open mod is not a sandbox mod. Close it in the editor, then create or open a mod whose name starts with `MCP_`.
- **`INVALID_STATE`.** The editor is not in the state the tool needs: a dialog is open, a mouse button is held, a playtest is running (call `stop_playtest`), or no mod is open.
- **Publishing to the Workshop does nothing.** That is the publish block. See [What it does](#what-it-does) for how to publish by hand.
- **Claude Code does not show the tools.** Run `claude mcp list` to see whether the server is registered and connected, and restart Claude Code. The `cmd /c` in front of `npx` is needed on native Windows; without it Claude Code cannot start `npx`.
- **Still stuck.** Open an issue with the bug report form. Include the game, plugin and server versions, the `self_test` output and an excerpt of `BepInEx\LogOutput.log`. Do not post the token file or private mod files.

## Development

Layout:

| Path | Content |
|---|---|
| `src/PummelPartyMcp.Plugin` | BepInEx plugin (C#, net472): `Core` (Unity-free: HTTP, auth, dispatch, rules), `Game` (editor access), `Tools`, `Safety`, `Hooks` |
| `src/PummelPartyMcp.Plugin.Tests` | net8.0 console harness for `Core`; needs no game |
| `src/PummelPartyMcp.Plugin.PatchTests` | net472 Harmony and drift tests; reads the game DLLs for reflection only |
| `src/PummelPartyMcp.Server` | MCP server (TypeScript) and its tests |
| `docs/` | `plugin-api.md` (API reference), `editor-architecture.md` and `architecture/` (how the editor works), `mod-format.md`, `legal-notes.md`, `release-checklist.md` |
| `scripts/` | backup and verify scripts; `package-thunderstore.ps1`; `prepare-release.ps1` |
| `packaging/thunderstore/` | templates for the Thunderstore package (README, CHANGELOG copy, icon) |
| `.github/workflows/ci.yml` | CI (see below) |
| `.github/ISSUE_TEMPLATE/` | bug report form |
| `skills/pummel-modder/` | agent skill |
| `CHANGELOG.md`, `ROADMAP.md` | changes and plans |

### Build from source

PowerShell, from the repo root. Replace the game folder with yours. You need the .NET 8 SDK (the plugin targets .NET Framework 4.7.2 and uses reference assemblies from NuGet), Node.js 22 or newer, and a game install with BepInEx (see [Quick install](#quick-install), step 1, for BepInEx).

1. Install BepInEx 5 into the game folder (as in Quick install). Start the game once and quit; `BepInEx\LogOutput.log` should exist.

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

4. Start the game and check `BepInEx\LogOutput.log` (see [Check that the plugin runs](#check-that-the-plugin-runs)).

5. Build the MCP server.

   ```powershell
   cd src\PummelPartyMcp.Server
   npm ci
   npm run build
   cd ..\..
   ```

6. Register the local build with your MCP client. In this repo, Claude Code picks up the project file `.mcp.json` when started in the repo root:

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

   The server finds the plugin's token file by itself (see [Environment variables of the MCP server](#environment-variables-of-the-mcp-server)), so this file needs no path or `env` block on a normal Steam install. The equivalent command, usable from any folder:

   ```powershell
   claude mcp add pummel-party-mcp -- node "<path to repo>\src\PummelPartyMcp.Server\dist\index.js"
   ```

   Add `--env PUMMEL_PARTY_MCP_GAME_DIR="$game"` if auto-detection does not find your game.

### Tests

PowerShell, from the repo root:

```powershell
dotnet run -c Release --project src\PummelPartyMcp.Plugin.Tests
dotnet run -c Release --project src\PummelPartyMcp.Plugin.PatchTests   # add -p:GameDir="$game" unless set in src\GameDir.props.user
cd src\PummelPartyMcp.Server; npm test
```

The patch tests need a local game install with BepInEx (they reference its `0Harmony.dll` and related assemblies). The first two suites print their results to the console; the server tests build the project and run Node's test runner against a fake plugin.

### CI

GitHub Actions, `.github/workflows/ci.yml`, covers what can be built without the game: the MCP server tests (Ubuntu and Windows, Node 22 and 24), the Unity-free `PummelPartyMcp.Plugin.Tests` harness (.NET 8, on Windows only, because the plugin's path rules are Windows rules; it compiles only the plugin's `Core` sources and is run with `GameDir` pointing nowhere), a check that the npm package contains only the allowed files (`dist`, README, LICENSE, `package.json`), and a guard that fails when a `*.dll`, `*.pdb`, `*.exe`, `*.token`, a `decompiled/` folder or a file over 5 MB is tracked. The plugin build and `PummelPartyMcp.Plugin.PatchTests` are **not** run in CI, because they need the game's assemblies; run them locally with the game installed, and re-run the drift test (`PatchTests`) after every game update. The workflow has no secrets and publishes nothing.

### Packaging and release

`scripts\package-thunderstore.ps1` builds the plugin in Release and assembles `artifacts\thunderstore\` plus `artifacts\PummelPartyMcp-<version>.zip` (the plugin DLL only; the version comes from the plugin csproj; the changelog is copied from the root `CHANGELOG.md`; the script refuses to run with uncommitted changes under `src\PummelPartyMcp.Plugin` unless you pass `-AllowDirty`; `artifacts/` is gitignored). The server's `package.json` is ready for `npm pack` (`prepack` builds, `files` is an allow-list); the maintainer removes `"private": true` when publishing.

`scripts\prepare-release.ps1` does everything short of publishing: it checks the version numbers, builds, runs the three test suites, builds the zip and the npm tarball into `release-out\` (gitignored) and prints their SHA-256. It never publishes, tags or pushes. The step-by-step procedure, including every file that holds a version number, is in [docs/release-checklist.md](docs/release-checklist.md). The maintainer does all publishing by hand.

Game DLLs, decompiled game code and other people's mod files must never be committed. `decompiled/`, `lib/`, `*.dll` and similar are in `.gitignore`; keep it that way. `CLAUDE.md` holds the working rules for AI agents that contribute to this repo.

### Official mod documentation

The game's official mod documentation wiki is not included in this repository, because no content licence is declared for it. For offline reference (useful to an AI agent working on this code), fetch your own copy: `python scripts/dump_pummel_wiki.py` writes the pages to `docs/wiki/`, and `python scripts/wiki2md.py` converts them to Markdown in `docs/md/`. Both folders are ignored by git. Be considerate: the dump script makes one request per page.

## Licence

MIT, see [LICENSE](LICENSE). Pummel Party and its assets belong to Rebuilt Games and are not included in this repository.
