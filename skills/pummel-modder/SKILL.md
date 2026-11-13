---
name: pummel-modder
description: Use when the user wants to inspect, build, change, validate or playtest a Pummel Party mod in the built-in Mod Editor through the pummel-party-mcp tools (get_status, create_test_mod, run_batch, validate_mod, start_playtest, ...). Covers the safety rules, the standard workflow, building a minimal valid minigame, actions and triggers, and how to verify a mechanic by playtest.
---

# Pummel Party modding with pummel-party-mcp

You drive the Mod Editor of a running Pummel Party through MCP tools. A plugin inside the game does the work. Full contract: `docs/plugin-api.md`; the tool `usage_guide` returns the detailed rules, call it before your first write.

## First calls

1. `get_status`. Check:
   - `editorOpen` / `editorState` is `Editing` (otherwise tell the human to open the Mod Editor, or wait; `EDITOR_NOT_OPEN`).
   - `modOpen` / `modName`: which mod is open.
   - `writesEnabled` must be true (else `WRITES_DISABLED`: the human must change the plugin config).
   - `publishBlock.complete` must be true (else every write returns `PUBLISH_BLOCK_INCOMPLETE`).
   - `gameVersionCheck`: if it reports an untested game version, writes are refused with `GAME_VERSION_UNTESTED` (see Errors).
   - `hasUnsavedEdits`, `playtest`.
2. `list_mods` (isTestMod, isOpen), then `open_test_mod` or `create_test_mod`.
3. `get_hierarchy` for guids.

## Safety rules (never work around them)

- You may only change sandbox mods whose name starts with `MCP_` (`^MCP_[A-Za-z0-9_ -]+$`, max 64 chars, no leading or trailing space). Create new ones with `create_test_mod`; never copy a real mod.
- Never ask the human to rename a real mod to `MCP_...`, and never try another name for a real mod after `GUARD_NOT_TEST_MOD`.
- If a real mod is open, `create_test_mod` / `open_test_mod` are refused. Tell the human to close it by hand. You can still read it (`get_hierarchy`, `get_object`, `get_action_graph`, `validate_mod`) but never edit it.
- Workshop publishing is blocked and has no tool. Never suggest disabling the plugin to publish on your own initiative. If the human asks how to publish, say that publishing is manual, and that it needs `[General] PluginEnabled = false` in `BepInEx\config\com.pummelpartymcp.plugin.cfg` plus a game restart (and back afterwards).
- Assets: `import_asset` only reads the source file you name (an absolute local path) and copies it into the sandbox mod; never ask for a destination path, never try to make it overwrite, and never point it at a UNC/network path. `remove_asset` deletes the mod's own custom assets for good (no undo): clear references first and use `force` only when the human agrees the references may be lost (seen for an action parameter: the old guid stays in the scene file and reads as missing after a reopen; other kinds not checked; read tools show the old name until then). Never remove or replace built-in assets. Do not create junctions, symlinks or hard links inside a mod folder: the tools refuse to write or delete through them (`LINK_REFUSED`; `list_assets`/`get_asset` show `linked: true`); tell the human instead of working around it.
- Do not look for ways around `GAME_VERSION_UNTESTED`, `WRITES_DISABLED` or `PUBLISH_BLOCK_INCOMPLETE`: report them to the human.

## Standard workflow

`get_status` -> `list_mods` -> `create_test_mod` / `open_test_mod` -> `get_hierarchy` -> edits -> read back -> `validate_mod` -> `save_mod` -> `start_playtest` -> `get_playtest_state` / `get_playtest_events` -> `stop_playtest`.

- Edits stay in memory until `save_mod`. Save explicitly; nothing is saved automatically.
- After every write, read back: `get_object` (components and properties) or `get_action_graph` (triggers and actions). Do not trust that a call did what you meant.
- Check `applied` and `clamped` (and `inspectorClamped`) in the reply: values may be clamped (transform position within -1000..1000, scale 0.001..1000 per axis, `Wait m_seconds` 0..3600, `m_volume` 0..1, Timer `m_interval.min` >= 0.1).
- Edit tools need `Editing` state and return `INVALID_STATE` while an editor dialog is open, the left mouse button is held in the game window, or a playtest is active.
- Objects are addressed by the `ModTransform` guid from `get_hierarchy`. Components by guid, class name (`ModLight`) or UI name (`Light`), case-insensitive; a name matching several components is refused with the guids listed.

### run_batch

Use `run_batch` for many steps (1-100 steps `{tool, params?, id?}`, runs in one turn, up to 120 s). Steps may be the edit tools, `undo`, `save_mod` and the `get_` / `list_` read tools. Lifecycle, playtest, `screenshot`, `get_status`, `validate_mod` are not allowed inside.

- A string value in `params` that starts with `$` references an earlier step's result: `$<id>.member` or `[n]`. Example: `"$floor.guid"`, `"$dup.guid"` (or `"$dup.objects[0].guid"`). `"$$5"` is the literal `"$5"`. Forward references are refused up front.
- There is no rollback (object creation is not undoable). `stopOnError` defaults to true; `results` lists exactly what was applied. Step failures are in `results`, not a call error. Revert with `delete_object` or one `undo` per step.
- Each write step is its own undo step. Nothing is saved unless a `save_mod` step ran.

```json
{"steps": [
  {"id": "hazard", "tool": "create_object", "params": {"kind": "trigger", "name": "Hazard", "position": {"x": 4, "y": 0.5, "z": 0}}},
  {"tool": "set_property", "params": {"guid": "$hazard.guid", "component": "Trigger", "property": "TriggerOnEnter", "value": true}},
  {"tool": "add_action", "params": {"guid": "$hazard.guid", "trigger": "OnEnterActions", "type": "ChangeHealth", "parameters": {"m_operation": "Subtract", "m_value": 1}}}
]}
```

## Assets (import, materials, removal)

Details: `usage_guide` topic `assets` and `docs/plugin-api.md`. Start with `list_asset_rules` (limits, accepted files, shader properties) and `list_assets` with `location: external` (the mod's own assets).

- `import_asset {path, name?}`: `.png .jpg .jpeg` -> `Assets/Textures`, `.obj` -> `Assets/Props`, `.wav .mp3 .ogg` -> `Assets/Audio`. The content must match the extension; size limits come from the plugin config. Files exist on disk immediately and stay even if you never save. A taken name becomes `name (1)`; nothing is overwritten. `IMPORT_FAILED` means the game could not read the file and the copy was removed. Audio longer than `[Assets] MaxAudioSeconds` (600 s) is refused; all content checks run again on the copy before it is registered.
- Use what you imported: material `_MainTex` = the texture guid (`create_material` / `set_material`), then `set_property` on a renderer's `materials` or a prop's `customMaterials` with the material guid; a mesh with `create_object kind prop {prop: guid}`; a sound as an action's clip parameter or as music (`set_mod_settings` music); a texture as preview image (`set_mod_settings` details `previewImage`).
- `set_import_settings` (texture compression/filter/wrap, prop recalculateNormals) applies like the inspector's Apply; a compression change is refused (`ASSET_IN_USE`) while components, actions or the preview image still use the texture.
- `get_asset {guid}` shows `references` (every use) and `removable`. `remove_asset` is refused with `ASSET_IN_USE` while the asset is used; clear the references, or pass `force: true` only with the human's OK. Not undoable. None of the asset write tools works inside `run_batch`.

## Prefabs

Details: `usage_guide` topic `assets` (PREFABS) and `docs/plugin-api.md` (Prefabs).

- `create_prefab {name?, fromObject?}` writes `Assets/Prefabs/<name>.pfab` + `.pmeta` NOW (not undoable). Empty, or a copy of a scene object and its children; the scene object stays as it is (no instance replaces it). It refuses a subtree with Prefab Instance / Board Space components or with action targets / object references (they would silently keep pointing at the scene): for such content create an empty prefab and build it inside. A taken name gets `_n` (`nameAdjusted`).
- `open_prefab {guid}` enters the edit context (no file written); then use `get_hierarchy` (context `prefab`) and the normal edit tools and `undo` (the prefab has its own undo stack); `create_object kind prefabInstance` is not possible inside a prefab. Edits stay in memory.
- `close_prefab` SAVES the prefab (verified) and refreshes every instance in the scene; until then instances show the old version. Always close before `create_prefab`, `import_asset`, `remove_asset`, lifecycle tools or `start_playtest` (all refuse while a prefab is open); `save_mod` works with one open.
- Place an instance with `create_object kind prefabInstance {prefab: guid}`; use it with a Spawn Prefab action or a Spawner (`list_assets type Prefab`); `get_asset` shows the prefab's objects, instances and references. `remove_asset` also removes prefabs (refused while instances, spawners, items or actions use it, unless `force`).
- Every prefab write (create, close, each prefab in `save_mod`) is dry-run in memory first, verified afterwards and restored if it came out bad: if a call fails with `INVALID_STATE` saying a prefab cannot be serialised, nothing was written; fix or delete the object that throws. If `INTERNAL` says a file was restored, the in-memory edits are intact: fix the cause and retry.
- `create_prefab`, `open_prefab`, `close_prefab` do not work inside `run_batch`; edits inside an open prefab may be batched.

## Minimal valid minigame

`validate_mod` runs the editor's own validator (same as the Play button). A Minigame mod is valid when:

- `NumberOfRounds` > 0 (default 1), and
- there is at least one active Player Spawn (`create_object` kind `playerSpawn`). With its defaults (`SharedSpawn` true, `SpawnUsageType` `InitialSpawn, Respawn`, all players, all counts) a single spawn satisfies the validator's per-player and respawn rules. Alternatively the validator wants at least 8 non-shared initial spawns.

Warnings never block: empty description and no preview image (both only settable by hand in Minigame Settings), and an initial spawn without a collider below it. Cube, plane and prop objects have a collider by default.

Docs example: floor = `create_object` kind `cube`, position (0, -0.5, 0), scale (20, 1, 20); spawn = kind `playerSpawn` at (0, 0.1, 0).

Board mods cannot be made valid through the plugin (connections are not editable). `start_playtest` refuses BoardSpace mods.

## Actions and triggers

- Create the carrier: `create_object` kind `trigger` or `logic`.
- A Trigger component's slot is editable only while its flag is on: `set_property` `TriggerOnEnter` true (or `TriggerOnExit` / `TriggerOnStay` / `TriggerOnHit`) first. `get_action_graph` reports `editable` and `reason`; action tools refuse non-editable slots.
- Address a trigger as `{guid, component?, trigger}` where `trigger` is the `triggerName` from `get_action_graph` (`OnEnterActions`, `OnUseTrigger`, `Triggers[0]`), or `{scope: "playerSettings", trigger}` (Minigame mods): `weaponHitTrigger`, `tickTrigger`, `onDeathTrigger`, `onRespawnTrigger`, `onFirstSpawnTrigger`, `onRoundEndTrigger`. `component` is needed only when several components have a trigger of that name.
- Logic components hold a trigger list: `list_trigger_types`, `add_trigger`, `set_trigger`, `remove_trigger` (addresses `Triggers[0]`, ...; later entries shift down on removal). Other components have fixed slots. `set_property` cannot set triggers or action lists.
- Call `list_action_types` with the trigger address first: it shows what the slot accepts (`usableHere`, `reason`), parameter names, types, enums, defaults and limits. Then `add_action`, and read `get_action_graph` back. Default target flags apply on append only.
- Change Health: the action's default operation is `Set`. Always pass `m_operation` explicitly (`Subtract`, `Add`, ...); `m_value: -25` without it sets health to -25, which kills.
- Health rule: with Use Health off (the default; only switchable by hand in the Minigame Settings) health is a 0/1 flag (0 until the first player reset at round start, then 1, and 1 again after every respawn). A Change Health value of 0 does nothing, whatever the operation (Set 0 kills nobody); for a non-zero value a result of 0 or less kills at once (Add -25, Subtract 25, Set -1; from health 1, Divide 2 kills but Multiply 2 clamps to 1). Only a player target is affected (Source (Activator) in a Trigger or Logic Hit). Kill/Destroy (type Kill) kills a player target without this arithmetic, with the same refusals as any death; a non-player target is deactivated, so target the player: Source (Activator) in a Trigger or Logic Hit, Receiver in player-settings triggers. Hit points need Use Health on and Health set, by the human.
- Target actions with no target do nothing: with no Source/Receiver flag and no Advanced targets, Play Sound is silent and Change Health has no effect, yet the playtest feed still logs `action_executed` (it means the action ran, not that it had an effect or was audible). `add_action` / `set_action` return a warning for this. Play Sound: an AudioSource target plays the action's clip if one is set, any other target component plays a positional one-shot.
- Player-settings trigger edits are not undoable and are written by `save_mod`.

## Mod settings

- list_mod_settings first (fields, conditions, choices, limits, presets), then set_mod_settings {section, values} (all-or-nothing; check clamped, implied, validation), get_mod_settings to verify, save_mod.
- Settings are not undoable: keep the returned previous values. Conditional fields need their controlling field in the same or an earlier call.
- Lighting/screenEffects: pick a preset by name or Custom; edit custom values only with Custom; keep Custom selected when saving screen effects. Take a screenshot to see the result.
- capture_preview_image creates a texture file in the mod right away (assignment saved by save_mod). The Workshop item is never touched.

## Playtest and verification

1. `validate_mod` (errors give `INVALID_STATE` in `start_playtest`; `validateFirst: false` is "Run With Errors").
2. `start_playtest` with `aiOnly: true` and e.g. `players: 4`. It saves the mod, presses Play and returns in play mode (limit 60 s). No input is injected: without `aiOnly` a human has to play player 1.
3. While running: `get_playtest_state` (players alive, health, score, position, deaths; minigame state, round, time; `useHealth` and a `note`), `get_playtest_events` (ring of 500, page with `sinceId`), `get_status.playtest`, `get_log`, `screenshot` for visual checks.
4. `stop_playtest` before editing again. The event feed is kept until the next playtest starts, so stopping first and reading afterwards works.

Notes from tests (see `usage_guide`):
- Player Spawn `SharedSpawn` (on by default) lets several players pick the same spawn; with the Point shape they start on the same point and, with punching on, hit each other at round start (seen in a test). Line and Circle shapes place players by index and are an alternative to turning it off. The validator errors when there is no shared initial spawn and fewer than 8 non-shared initial spawns.
- LastManStanding placement: a dying player gets 10 x (dead so far + 1) points each round, and players still alive at the round end each get 10 x (dead + 1), so survivors share the top value.
- The built-in meshes LowPolyCube, Primitive Cube 01 and LowPolyPlane did not show a tiled texture (flat colour; a cut-out texture can make the object invisible); imported OBJ meshes with UVs in 0..1 work. LowPolyPlane is about 10 x 10 units at scale 1 (observed).
- Harmless log lines not caused by the mod: "Loading Minigame: <name>" at start, "Closing a minigame mod." and "Cannot send rpc when not connected" at stop, and on the results screen "Graphics.CopyTexture can only copy memory with the same size" and "Releasing render texture that is set as Camera.targetTexture!".

During a playtest `get_object`, `get_action_graph`, `get_open_mod`, `validate_mod` and every edit tool (including `save_mod`, `run_batch`) return `INVALID_STATE`.

What to conclude: verify, never assume. A trigger "worked" only if the events or state show it (`trigger_fired`, `action_executed`, `player_died`, `health_changed`, `score_changed`, round and `minigame_finished` events). Trigger and action events are rate limited (one per key per second), so a missing repeat is not proof that it did not run. A hit during respawn invulnerability can leave a player alive at 0 health. Absence of events in a short test is not proof of correctness; a `screenshot` shows only one moment.

## Undo and unsaved edits

- `undo` reverts like Ctrl+Z; there is no redo. Creating an object is not undoable: use `delete_object`.
- One call is one undo step; one edit can add two stack entries (selection is a step), but `steps: 1` still reverts the edit. Undoing a component add pushes an extra snapshot (`extraUndoEntries`): one more undo re-adds it, so check before undoing again.
- `get_status.hasUnsavedEdits` is the plugin's tracking (selection changes count; undoing everything does not clear it; only a save does). `create_test_mod` / `open_test_mod` refuse to leave a mod with unsaved edits unless you `save_mod` first or the human agrees to `discardUnsaved: true`.

## Errors and the next step

- `INVALID_STATE`: read the message: dialog open, mouse held, play mode active (`stop_playtest`), validator errors, unsaved edits (save first), prefab being edited (`close_prefab`), wrong editing context. Fix the state, or ask the human, then retry.
- `TIMEOUT`: read the message. "nothing was changed" / "safe to retry" means before the commit point: retrying is safe. A message saying the mod was created, is being opened, or that the change was applied means it may already have happened: `get_status`, `list_mods`, `get_object` first, never retry blindly. After a `start_playtest` timeout: `get_status`, then `stop_playtest`. A client-side timeout without a plugin reply is ambiguous: check before retrying a write.
- `GUARD_NOT_TEST_MOD`: the target or open mod is not a `MCP_` mod. Stop; do not rename or retry.
- `PUBLISH_BLOCK_INCOMPLETE`: the plugin's Workshop safety patches are not all in place (for example after a game update). Writes stay off; tell the human; read tools still work.
- `GAME_VERSION_UNTESTED`: the game version is not one the plugin was tested with; writes are refused until the human sets `[Safety] AcceptedGameVersion` in the plugin config. Tell the human, do not try to work around it.
- `set_property` / `set_properties` follow the editor inspector (0.7.0): a property the inspector never draws, one drawn only under a condition (e.g. `spotAngle` only while `type` is Spot; conditions are checked after the call's other values, so `{type: "Spot", spotAngle: 40}` works) or a choice it omits is refused with `INVALID_PARAMS`; `get_object` shows `shown` and `inspector` per property.
- `INVALID_PARAMS`: a value or rule was refused (non-editable slot, hidden target flag, unknown parameter name, unsettable property). Re-read with `get_object` / `list_action_types` and fix the call. Values are validated first: on failure nothing changed.

## Reporting to the human

Say plainly:

- what you built or changed (mod name, objects, triggers and actions);
- what was verified by a playtest (which events or state you saw) and what only by reading back (`get_object` / `get_action_graph`);
- what could not be verified (visual look, balance, human-driven input, anything the event feed does not show, warnings left open such as empty description or preview image);
- whether the mod is saved, and anything the human must do by hand (Use Health setting, description, preview image, publishing).
