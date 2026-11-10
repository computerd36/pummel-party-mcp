# Pummel Party mod editor — architecture summary

Phase 0 recon, 2026-11-10. Game build 2025-10-16, Unity 2021.3.45f2, Mono x64.
Source: static reading of decompiled `Assembly-CSharp.dll` (not in this repo). **Nothing here has been
verified in the running game yet**; the detail docs tag each claim confirmed-from-code / inferred / unverified.

Detail docs:

| Topic | Doc |
|---|---|
| Editor core, mod lifecycle, save, dirty state, playtest | [architecture/01-editor-core-and-lifecycle.md](architecture/01-editor-core-and-lifecycle.md) |
| Load/save code paths, assets, cache | [architecture/02-serialization-and-assets.md](architecture/02-serialization-and-assets.md) |
| On-disk format | [mod-format.md](mod-format.md) |
| Hierarchy, components, inspector properties, selection, undo | [architecture/03-hierarchy-components-inspector-undo.md](architecture/03-hierarchy-components-inspector-undo.md) |
| Action Editor, triggers, actions; dormant node-graph system | [architecture/04-action-editor-and-graphs.md](architecture/04-action-editor-and-graphs.md) |
| Workshop publish/delete paths and blocklist | [architecture/05-workshop-publish-paths.md](architecture/05-workshop-publish-paths.md) |

## The model in one page

- **Editor singleton:** `EditorSystem.Instance` (null = editor closed). `CurrentMod`, `State`, `IsLoading`.
  UI is Dear ImGui drawn at end of frame; shortcuts are hardcoded input polling in `EditorSystem.Update`.
  There is no command system to hook into.
- **Mod document:** a `PummelMod` subclass (`MinigameMod`, `BoardMod`, `BoardSpaceMod`, `ItemMod`) backed by a
  folder `<persistentDataPath>\WorkshopMods\<name>\` with `Data\` and `Assets\`. Folder name = mod name, unsanitized.
- **Objects:** plain Unity GameObjects carrying a `ModTransform`, under `EditorSystem.m_hierarchyRoot`.
  Stable identity = `ModTransform.guid` (persisted). All references on disk are GUID strings.
- **Components:** `[ModSerialized]` classes discovered by `ModReflectionSystem` (41 types). Serialized state =
  public instance properties. Added via `ModEditorComponentAdderRemover.AddComponentToGameObject`.
- **Inspector:** reflection over public properties; no validation attributes; setters carry side effects.
- **Logic:** only the actions/events model is live. A `ModLogicTrigger` holds a flat `Actions` array
  (35 action types); parameters are private serialized fields. The node-graph/visual-scripting code is dormant.
- **Undo:** per-frame snapshot diff of the *selected* objects; explicit events for move/delete/duplicate.
  No redo found. Creation is not undoable. The dirty flag lives in `EditorUndo` and is set only by undo pushes.
- **Scene file:** `MainScene.scene`, custom uncompressed binary (`PMH` v1.0): hierarchy, component table,
  length-prefixed property records. JSON files use Unity `JsonUtility`.

## What the bridge can call directly

`EditorSystem`: `OpenMod`, `CreateNewMod`, `SaveCurrentMod`, `RenameMod`, `SetCurrentMod(null)`,
`SetEditorState(EnteringPlayMode)`, `AddGameObject`, `Duplicate`, `Delete`, `Copy`/`Paste`, selection via
`EditorSelectionManager`. None of these validate their inputs or the editor state; the UI does that.
Reparent, reorder, rename-object, set-active, and all action-list edits are inlined in ImGui code and must be
replicated by the plugin (call sequences are in docs 03 and 04).

Recipe for undoable edits: select the object → wait one frame → edit → wait one frame.

## Safety findings (these drive the guard design)

1. **Writes happen without "save":**
   - starting a playtest calls `CurrentMod.Save()` (confirmed in source);
   - leaving a prefab edit context saves that prefab (triggered by open / new / rename / set-current-mod);
   - **opening a mod** creates missing `.pmeta` files and overwrites corrupt ones with a new GUID;
   - missing or corrupt settings files load as defaults and the next save overwrites them.
2. **Save is not atomic and keeps no backup.** Every save rewrites every file in place.
3. **Save never deletes files.** Deletion exists only in explicit UI actions (remove asset, delete mod).
4. **Workshop identity travels with the folder.** `Data\WorkshopItem.json` holds `publishedFileId`; `256` means
   never published. A folder copy of a published mod still targets the live item.
5. **Every save rewrites `WorkshopItem.json`.**
6. **Publish chokepoint:** `WorkshopModInstaller.PublishItem()`, reached only from `WorkshopPopup.PublishMod`.
   No save/playtest/close/rename path reaches it.
7. **The game's publish dialog can call `SteamUGC.DeleteItem`** on the mod's current id after certain failed
   updates (inferred from code, untested). Relevant to manual publishing as well.
8. **No timed autosave**, no save on quit, focus loss or mod close.

Consequences for the bridge:

- The `PB_TEST_` guard must cover **opening** a mod, not only writing, because open can write.
- `PB_TEST_` mods must be created fresh (or have `publishedFileId` forced to 256); never by copying a real mod folder.
- The UI-only rules (which component / action is allowed where) must be re-implemented in the bridge.
- Publish blocking is two layers of Harmony prefixes: game methods and the Steamworks UGC write calls
  (list in doc 05). Queries and downloads stay allowed.
- `redo` (planned in Phase 2) appears not to exist in the editor.

## To verify at runtime (on a `PB_TEST_` mod only)

- Undo timing recipe; that creation really is not undoable; that no redo exists.
- Whether edits to never-selected objects are written by save.
- Whether the dirty flag is true right after load.
- Behaviour of `SetEditorState(ExitingPlayMode)` vs the Stop button path.
- GUID uniqueness after duplicate.
- Reachability of prefab-wired Workshop buttons (delete, vote) that the decompile cannot show.
