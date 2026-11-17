# Changelog

All notable changes to pummel-party-mcp (the BepInEx plugin and the MCP server) are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The plugin and the server are released together and share one version number.

`packaging/thunderstore/CHANGELOG.md` is a copy of this file. Edit this one; `scripts/package-thunderstore.ps1` copies it into the Thunderstore package and `scripts/prepare-release.ps1` checks that the two files are identical.

## [Unreleased]

### Documentation

- README: the Mod Editor's welcome dialog has to be closed by hand before the edit tools work.

## [1.0.0] - 2026-11-17

First public release. Everything below was added after 0.7.0 and was developed as the unreleased 0.8.0 line; it ships as 1.0.0.

### Added

- Mod settings tools: `list_mod_settings`, `get_mod_settings` and `set_mod_settings` cover minigame and board details, minigame and player settings, music, lighting and screen effects (presets and custom values), and board size and camera. Clamps and conditions are the ones the editor applies. Settings are written by `save_mod` and are not undoable. `capture_preview_image` captures the scene into a texture in the sandbox mod and assigns it as the preview image. Capability `settings`.
- Asset tools (capability `assets`, config section `[Assets]`):
  - `import_asset` copies a local `.png .jpg .jpeg .obj .wav .mp3 .ogg` file into the sandbox mod. The source is only read. Nothing is overwritten (a taken name becomes `name (1)`). Limits: `MaxImportFileMB` (50), `MaxModAssetsMB` (500), `MaxAudioSeconds` (600), `MaxCompressedAudioMB` (15).
  - `set_import_settings`, `create_material` and `set_material` change import settings and materials (the five built-in shader inspectors).
  - `remove_asset` removes only the mod's own custom assets, and is refused while the asset is in use unless `force` is set.
  - `get_asset` shows an asset with its references, `list_asset_rules` shows the rules, and `list_assets` takes a `location` and shows `lengthSeconds` for loaded audio clips.
  - Imports, material changes and removals write or delete files at once and are not undoable.
- Prefab tools (capability `prefabs`): `create_prefab` (empty, or a copy of a scene object), `open_prefab` and `close_prefab`. They enter and leave the prefab edit context like the editor's UI. `get_asset` shows a prefab's object tree and scene instances. Prefab files are written at once and are not undoable.
- `.pfab` write protection. A prefab the game fails to write completely makes the whole mod unloadable. So `create_prefab`, `close_prefab` and every prefab inside `save_mod` and `start_playtest` are first written to memory, checked afterwards (header, length) and restored from a copy if bad. A prefab that cannot be serialised refuses `save_mod` before anything is written.
- `screenshot` takes an optional `camera` (`{position, lookAt}`, `{position, rotation}` or `{focus, rotation?}`) that moves the editor's scene-view camera first. It is a view change only: nothing in the mod changes and there is no undo step. The reply shows the applied `camera`.
- `get_playtest_state` returns `audio`: music and ambience source (clip, volume, playing, time, length), the number of active pooled sources, and the player's volume settings.
- Links (junctions, symlinks, mount points, hard links) inside a sandbox mod are never written or deleted through. The tools refuse with the new error `LINK_REFUSED`. `list_assets` and `get_asset` show `linked`.
- `usage_guide` has topics (overview, safety, editing, actions, playtest, batch, settings, assets). New notes cover shared spawns, LastManStanding points, built-in meshes, harmless log lines, falling and catch zones, checkpoints, `OneUsePerPlayer`, Set Placement and ranking, AI players and jumping, music clips, audio source pool warnings, local transforms in `create_object`, round layouts, players not blocking each other, built-in prop search, invisible walls, punch knockback and the screenshot camera.
- Plugin api version check: before its first plugin call the server pings the plugin and refuses every plugin tool with `API_MISMATCH` when the plugin's api version is not the one it needs, saying whether to update the server or the plugin. The check is repeated after a connection failure or a new game session, and when the last plugin reply is more than 10 s old. If the check itself fails, the error says the tool call was not sent. `ping` and `get_status` show `serverVersion` and `requiredApiVersion`.
- Over-large replies no longer end in cut-off JSON. A reply over 100,000 characters starts with a one-line note, then valid JSON where possible. `get_hierarchy` prunes the deepest levels first (`depthLimited`, `childCount`); if the direct children of the root alone are too many, it lists as many as fit (`childrenShown`, `childrenTruncated`) and `nodesShown` tells how many objects the reply holds. `run_batch` keeps one status row per step and drops the result bodies.
- `get_status` shows `activeWrite`, the write call the plugin is running right now.
- `get_status` shows `lifecycle`: `state` (`active` or `failed`), `armedUtc` and `sessionsThisRun`.
- Usage guide topics: AI navigation, fair starts, player sizes, camera and invisible walls, Set Player Visual, build order, screenshot tips, large scenes and pivots.
- Issue template for bug reports, a roadmap and a release checklist.

### Changed

- The plugin is active only while the Mod Editor is open. When the editor opens, the plugin installs the Workshop publish block first, then starts the API server and the record-only hooks. When the editor closes, it stops the server, ends the calls that still wait or run (`EDITOR_NOT_OPEN`) and removes all its patches. In the main menu and in matches it has no open port, no patches and no per-frame code; only a short check at each scene load runs. The main-menu Workshop window works as in the unmodded game. This replaces the rule that the publish block is installed at game start and never removed. A game method that was patched once keeps a Harmony-generated replacement after the plugin removed its patch: it behaves like the original and carries no patch of the plugin, but its machine code is not the original one. A patch whose removal fails is named in the BepInEx log and stays until the next editor visit.
- If the publish block cannot be installed completely when the Mod Editor opens, only `ping`, `get_status`, `get_log` and `self_test` answer in that editor session; every other tool returns `PUBLISH_BLOCK_INCOMPLETE`. Before, read tools kept working.
- The token file is created the first time the Mod Editor is opened, not at game start. `get_log` collects log lines only while the editor is open.
- With the Mod Editor closed, the server says that nothing answers and asks to open the Mod Editor. A call that was still running when the editor closed returns the new error `EDITOR_CLOSED_DURING_CALL` (it may have partly applied; check before retrying).
- The server's instructions are short. The long rules moved into `usage_guide` topics.
- The plugin may now import and remove assets inside a sandbox mod. This replaces the earlier rule that it never imports or removes assets. The other limits are unchanged: sandbox mods only, never the source file, never a built-in asset.
- `set_import_settings`, `set_material` and `close_prefab` are marked destructive. `import_asset` has a 120 s time limit and longer commit budgets.
- `duplicate_object` returns a top-level `guid`, as `create_object` does.
- `create_material` refusals for an unknown shader list the valid names, and the reply shows `shaderAsset` next to `shader`.
- `list_action_types` rows for Play Sound, Change Health and Kill carry a `note`.
- The plugin and the server are published as separate packages: the plugin on Thunderstore and as a zip on a GitHub release, the server on npm (`npx -y pummel-party-mcp`).
- Node.js 22 or newer is required for the server.
- The server finds the game's Steam folder from the Windows registry first, then Program Files, then Steam's library list. With a mod manager the token file is in the manager's profile: the "token file not found" error and the README now say to set `PUMMEL_PARTY_MCP_TOKEN_FILE`.
- `screenshot` refuses an image too large to send with a clear error that suggests a smaller `maxWidth`. `screenshot` calls with a camera are queued by the plugin and run one at a time.
- A write tool that gets a bare HTTP error without a plugin answer says the edit may or may not have been applied.
- `capture_preview_image` blocks switching mods while the game writes the screenshot.
- Hardening: the plugin's HTTP server copes with connection floods, names supplied by the caller are length-capped and escaped in the log, more reserved Windows device names are refused, and `self_test` also covers the asset-reference members.

### Fixed

- Change Health note: with Use Health off a value of 0 does nothing, whatever the operation. A non-zero value whose result is 0 or less kills. Kill/Destroy (type Kill) kills a player target without this arithmetic, and deactivates a non-player target.
- `add_action` and `set_action` return a warning when a target action has no target and will run on nothing.
- The reference scan also covers the player-settings triggers, an Item mod's prefabs and icon, and the Workshop thumbnail.
- Import checks run on the copy inside the mod (staged as `.mcp-import`). An import interrupted by a crash is set aside at the next start. OBJ face lines are capped.
- `create_prefab` refuses an incomplete reference scan. Failed restores keep their safety copy in `McpRecovery`. Partial renames are detected.
- `start_playtest` protects the prefab files it saves, like `save_mod`.
- `create_object` works under an inactive parent and removes a partly created object if it fails.
- An empty or dangling asset reference reads as `null` or `{guid, missing}` instead of a placeholder asset.
- Reference scans treat a value they cannot read as incomplete, so `remove_asset` cannot miss a reference.
- `set_action` and `set_trigger` report what was already applied when a later parameter fails.
- `get_hierarchy` with a prefab open lists only the open prefab.

## 0.x history (not published)

The 0.x versions were development builds. They were never uploaded to npm or Thunderstore. This list is built from the project's git history.

### 0.7.0

- Game-version gate: write tools are refused with `GAME_VERSION_UNTESTED` on a game version the plugin was not tested with, until `[Safety] AcceptedGameVersion` equals the running version.
- New read tool `self_test`: checks that every game member the plugin needs still resolves, that the Workshop publish block is complete, and the inspector audit.
- `set_property` follows what each component's inspector can really produce (never-drawn properties, conditional properties and omitted choices are refused).
- `ModLight.spotAngle` is clamped to 1..179, the range the editor stores.
- Sandbox mods are named `MCP_...`. First packaged build.

### Earlier development milestones

- 0.4.x: action and trigger editing tools; validator, playtest control (`validate_mod`, `start_playtest`, `stop_playtest`) and `run_batch`.
- Playtest visibility: `get_playtest_state` and `get_playtest_events`.
- Object, component and property editing with undo; the write guard and the Workshop publish block; mod lifecycle tools.
- First version: read-only plugin and MCP server (status, hierarchy, objects, action graphs, assets, log, screenshot).
