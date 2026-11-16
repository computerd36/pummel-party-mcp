# Roadmap

This is a short, honest list. It is not a promise or a schedule. For what changed in each version, see [CHANGELOG.md](CHANGELOG.md).

## What exists (1.0.0)

By capability, not by tool name. The full tool list is in the [README](README.md#tools).

- **Reading the editor:** status, open mod, object hierarchy, objects and their properties, action graphs, assets, the plugin log, screenshots (with an optional editor camera move).
- **Sandbox mod lifecycle:** list mods, create or open a sandbox mod (`MCP_...`), save.
- **Editing objects:** create, duplicate, delete, reparent, rename, move; add and remove components; set properties (checked against what the editor's inspector allows); undo.
- **Logic:** Action Editor triggers and actions, including nested choice lists and the player-settings triggers.
- **Batches:** many steps in one call, with references between steps.
- **Mod settings:** minigame and board details, player settings, music, lighting, screen effects, board size and camera, preview image.
- **Assets:** import textures, meshes and sounds from a local path; import settings; materials; look up where an asset is used; remove the mod's own assets.
- **Prefabs:** create, open and close prefabs, with protection against truncated prefab files.
- **Checking and playtesting:** the editor's validator, AI-only or hand-played playtests, live player and audio state, an event feed (deaths, health, score, rounds, triggers, actions), the log.
- **Self-check and guide:** `self_test` re-checks the game members the plugin needs; `usage_guide` holds the rules and value shapes.
- **Safety:** sandbox mods only, Workshop publishing blocked, loopback and token only, game-version gate.

## Planned next

Roughly in order of how often they came up in test runs. Nothing here is started unless the changelog says so.

From two benchmark runs (an agent building whole minigames):

- True paging of the object tree's children (an offset or limit for the direct children of one object) and of run_batch results. list_assets and get_log already page; an over-large tree or batch reply is summarised, not paged (a very wide object still shows only the children that fit).
- Choose which player's camera a playtest screenshot shows.
- Read-only object state during a playtest (positions, active state and similar).
- A playtest-only way to move a player, for testing (for example a checkpoint or a catch zone).
- Layer control for objects.
- A mesh helper for tiled textures.
- Short frame sequences, to judge motion.

Other ideas:

- An installer command in the npm package that finds the game, checks BepInEx, copies the plugin and registers the server. It would show exactly what it writes, ask first, never touch mod folders, and have an uninstall.
- Input injection for playtests (a simulated human player).
- Richer runtime visibility for board mods (turns, spaces, events, items).
- Templates ("make a minigame like X" scaffolds).
- Mod diffing (compare two saved versions).
- A lint for common mod mistakes.
- Other MCP clients (Claude Code is the only documented one).

## Deliberately out of scope

- **Publishing to the Steam Workshop.** The plugin blocks it and no tool will ever do it. Publishing stays a manual step by the mod's author.
- **Editing existing mods that are not sandbox mods.** Write tools only act on mods named `MCP_...`. A possible future "allow existing mod" mode would have to back the mod up first, and would still never publish. It is not planned for 1.x.
- **Anything active in normal or online play.** The plugin is an editor tool, not a cheat tool. Giving it features for matches is out of scope.
- **Other platforms, for now.** Windows with Steam is the only tested setup. Other platforms are best effort and not tested.
- **Redistributing the game's files or documentation.** The repository holds none of them.
