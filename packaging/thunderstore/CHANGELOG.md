# Changelog

## 0.7.0

- Game-version gate: write tools are refused with `GAME_VERSION_UNTESTED` on a game version the plugin was not tested with, until `[Safety] AcceptedGameVersion` equals the running version.
- New read tool `self_test`: checks that every game member the plugin needs still resolves, that the Workshop publish block is complete, and the inspector audit.
- `set_property` follows what each component's inspector can really produce (never-drawn properties, conditional properties, omitted choices are refused).
- `ModLight.spotAngle` is clamped to 1..179, the range the editor stores (200 becomes 179).
- First packaged build. Sandbox mods are named `MCP_...`.
