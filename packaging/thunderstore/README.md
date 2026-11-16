# PummelPartyMcp {{VERSION}}

Lets an AI agent (through the Model Context Protocol) see and edit what is open in Pummel Party's built-in Mod Editor: objects, components, Action Editor logic, the editor's validator and playtests.

Unofficial project, not affiliated with Rebuilt Games. It is an editor tool, not a cheat tool. It is active only while the Mod Editor is open: in the main menu and in matches it has no open port, no per-frame code and, unless removing one failed (the BepInEx log then says so), no patches in the game. A game method that was patched once keeps a Harmony-generated replacement after the plugin removed its patch: it behaves like the original and carries no patch of the plugin, but its machine code is not the original one.

## Safety model

- Write tools only act on sandbox mods whose name starts with `MCP_` (hardcoded). Real mods cannot be opened, changed or deleted by the tools.
- No mod deletion, no Workshop tool of any kind. Assets: `import_asset` only reads the file you name and copies it into the sandbox mod's `Assets` folder (checked name, fixed sub-folder, never overwrites, size limits in the config `[Assets]`); `remove_asset` deletes only the sandbox mod's own custom assets. Both write or delete files immediately and are not undoable.
- The API listens on `127.0.0.1` only, every request needs a per-install random token, and requests with an `Origin` header or a non-local `Host` are refused.
- On a game version the plugin was not tested with, write tools are refused until you accept that version in the config.
- Writes can be switched off in the config (`[Safety] EnableWrites = false`).

## Workshop publishing is blocked

While the Mod Editor is open with this plugin enabled, publishing to the Steam Workshop (create, update, delete) is blocked on purpose, so a bug or a bad AI edit cannot reach a live Workshop item. Outside the Mod Editor the plugin blocks nothing. To publish a mod by hand: quit the game, set `[General] PluginEnabled = false` in `BepInEx/config/com.pummelpartymcp.plugin.cfg`, start the game, publish as usual, quit, and set it back to `true`. You can also simply remove or disable the plugin folder.

## The MCP server is installed separately

This package contains only the BepInEx plugin (it needs BepInEx 5, x64, Mono). The MCP server that your AI client talks to is a separate Node.js program (Node.js 22 or newer). Add it to Claude Code once:

```
claude mcp add pummel-party-mcp -- cmd /c npx -y pummel-party-mcp
```

Then start the game, open the Mod Editor and start Claude Code. The full documentation, the troubleshooting list, the changelog and the source are in the project repository (see the website field of this package).

Back up your mod projects before you use this. Tested on game version `1.14.1k` (Windows, Steam).
