#!/usr/bin/env node
import { readFileSync } from "node:fs";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { PluginClient, PluginError, optionsFromEnv, TOOL_TIMEOUTS_MS, isValidTestModName } from "./client.js";

const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { version: string };
const opts = optionsFromEnv();
const client = new PluginClient(opts);

// ---------------------------------------------------------------------------
// Shared guide. Served as the server `instructions` AND by the local usage_guide tool
// (not every MCP client shows server instructions to the model). Tool descriptions only
// carry what is specific to the tool.
// ---------------------------------------------------------------------------

const PLAYER_TRIGGERS = "weaponHitTrigger, tickTrigger, onDeathTrigger, onRespawnTrigger, onFirstSpawnTrigger, onRoundEndTrigger";

const PARAGRAPHS: string[] = [
  "pummel-party-mcp drives the Pummel Party Mod Editor through a plugin inside the running game.",

  "SAFETY: write tools act only on sandbox mods named MCP_ (^MCP_[A-Za-z0-9_ -]+$, max 64 chars, no leading/trailing space); anything else is refused (GUARD_NOT_TEST_MOD). Real mods can never be changed; Workshop publishing is blocked and has no tool. create_test_mod/open_test_mod are refused while a real mod is open (the human closes it). Writes need Safety.EnableWrites and a tested or accepted game version (get_status: writesEnabled, publishBlock, gameVersionCheck). Asset tools only read the source path you give (never change or delete it), only write inside the sandbox mod's Assets folder, and only remove the mod's own custom assets (usage_guide assets). Links inside a sandbox mod (junctions, symlinks, mount points, hard-linked files) are never written or deleted through: LINK_REFUSED, nothing written.",

  "WORKFLOW: get_status -> list_mods -> open_test_mod or create_test_mod -> get_hierarchy (guids) -> edits (create_object, set_transform, add_component, set_property, ...; many at once: run_batch) -> get_object to verify -> validate_mod -> save_mod -> start_playtest (aiOnly true runs unattended) -> get_playtest_state / get_playtest_events -> stop_playtest. Edits stay in memory until save_mod.",
  "ACTIONS: create_object kind trigger/logic; for a Trigger component set_property TriggerOnEnter (or TriggerOnExit/TriggerOnStay/TriggerOnHit) true to make its slot editable -> get_action_graph -> list_action_types with that trigger address -> add_action -> get_action_graph to verify -> save_mod. Logic components hold a trigger list (add_trigger, set_trigger, remove_trigger; Triggers[0], ...); other components have fixed slots. set_property cannot set triggers or actions.",

  "EDIT RULES: edit tools need the Editing state and return INVALID_STATE while an editor dialog is open, the left mouse button is held in the game window, or play mode is active (stop_playtest, or wait / tell the human, then retry). Every edit selects the edited object. Replies carry changed, undoable, undoStackLength (plus undoPending/note). One call = one undo step. undo has no redo; creating an object is not undoable (use delete_object). Undoing a component add pushes an extra snapshot (extraUndoEntries): one more undo re-adds it. Player-settings trigger edits (scope playerSettings) are NOT undoable: written by save_mod.",

  "PLAYTEST: validate_mod first. start_playtest SAVES the open sandbox mod (prefab files are dry-run before and verified after that save like save_mod: prefabsVerified; a prefab that cannot be serialised or a link in the mod refuses the start; if a prefab file had to be restored the error says which and whether the playtest started), applies players/aiOnly, presses Play, returns in PlayMode (limit 60 s); INVALID_STATE on validator errors (error.data.validation; validateFirst false = Run With Errors), an open prefab or dialog, or a BoardSpace mod. aiOnly true: AIs play, rounds run by themselves; otherwise a human plays player 1 (no input is sent). VERIFY, never assume a trigger or action worked: get_playtest_state (running only: players alive/health/score/position/deaths, minigame state/round/time, useHealth + note; supported:false + reason when the minigame controller is missing: still loading or the round is over) and get_playtest_events (ring of 500: player_died/respawned, health_changed, score_changed, round_started/ended, minigame_finished, trigger_fired, action_executed; page with sinceId; cleared when the next playtest starts, so stop_playtest first and read afterwards works). Also get_status.playtest, get_log, screenshot. HEALTH: with useHealth off (default) health is a 0/1 flag (0 until the first player reset at round start, then 1, and 1 again after every respawn): a Change Health value of 0 does nothing (any operation, so Set 0 kills nobody); for a non-zero value a result of 0 or less after clamping kills at once (Add -25, Subtract 25, Set -1; from health 1, Divide 2 kills but Multiply 2 clamps to 1 and does not), a result above 0 leaves health at 1. Only a player target is affected (Source (Activator) in a Trigger or Logic Hit). Kill/Destroy (type Kill) kills a player target without this arithmetic, with the same refusals as any death; a non-player target is deactivated, so target the player: Source (Activator) in a Trigger or Logic Hit, Receiver in player-settings triggers (it killed AI players from a kill-zone trigger in a test). Hit points need Use Health in the Minigame Settings (by hand). action_executed means the action ran, not that it had an effect or was audible (a target action with no target logs it too). During play these also work: ping, get_status, get_log, screenshot, list_mods, list_component_types, get_hierarchy (EDIT-TIME scene), list_assets/list_trigger_types/list_action_types without address. INVALID_STATE: get_object, get_action_graph, list_action_types with address, get_open_mod, validate_mod and all edit/lifecycle tools incl. save_mod, undo, run_batch. stop_playtest before editing. After a start TIMEOUT: get_status, then stop_playtest; do not retry blindly.",
  "MOD ID: publishedFileId \"256\" means never published (the game's local id for unpublished mods), not a Workshop item or a minigame id.",
  "MINIMAL VALID MINIGAME: NumberOfRounds > 0 (default 1) plus at least one ACTIVE Player Spawn (create_object kind playerSpawn; defaults cover all counts, players and respawn) = valid true. Warnings never block (empty description, no preview image, no collider below a spawn: cube/plane/prop have one). Board mods cannot be made valid via the plugin.",
  "BATCH: run_batch runs 1-100 steps {tool, params?, id?} in order in one write-queue turn (limit 120 s); each write step has its own guard, commit and undo step. Allowed: the edit tools, undo, save_mod and the get_/list_ read tools in its schema (not lifecycle, playtest, screenshot, ping, get_status, get_log, get_open_mod, validate_mod). NO ROLLBACK (object creation is not undoable): stopOnError (default true) stops at the first failed step and results lists what was applied; revert with delete_object or undo per step; false = attempt all; nothing is saved without a save_mod step. Step failures are in results ({ok, result|error}), not a call error; the call fails only on up-front validation (INVALID_PARAMS naming the step, nothing run) or a guard. id [A-Za-z][A-Za-z0-9_-]{0,63}, unique. PLACEHOLDERS: a string VALUE (any depth, not keys) in params starting with $ is a reference $<id>(.name|[n]){1,8} into an EARLIER step's result (no forward/self refs), replaced by that JSON value of any type before the step runs: \"$floor.guid\", \"$dup.objects[0].guid\". \"$$5\" = literal \"$5\"; any other $ string is refused up front; a reference to a failed step or missing path fails that step.",

  "CLAMPING: values may be clamped. Transform: world position within -1000..1000, rotation -3600..3600, scale 0.001..1000 per axis. Setter/inspector limits too (Wait m_seconds 0..3600, m_volume 0..1, Timer m_interval.min >= 0.1). See applied / clamped / inspectorClamped. set_property follows the inspector: never-drawn, conditional (spotAngle only while type is Spot; checked after the call's other values) and omitted choices are refused (get_object: shown, inspector).",

  `ADDRESSES: objects by guid (ModTransform guid from get_hierarchy/get_object). Components by guid, class name (ModLight) or UI name (Light), case-insensitive. Trigger address: {guid, component?, trigger, path?}, or {scope:"playerSettings", trigger, path?} (Minigame only; no guid/component). trigger = the triggerName from get_action_graph (OnEnterActions, OnUseTrigger, Triggers[0], ...); player-settings names: ${PLAYER_TRIGGERS}. component only when several components have a trigger of that name. path (max 8 steps {action, choice}) enters nested Display Popup lists: each step = a Display Popup action in the current list + a 0-based choice whose On Select list becomes the edited one. Some slots are editable only while a component flag is on; get_action_graph reports editable/reason and the action tools refuse non-editable slots.`,

  "PROPERTY VALUES (set_property/set_properties; the shapes get_object returns): Boolean; integers (3.0 ok); Single/Double finite number; String (max 16384); enum name (case-insensitive) or defined integer, flags joined by comma (\"OnSelf, OnTargets\"); Vector2/3/4, Quaternion {x,y[,z[,w]]} or array of 2-4 numbers; Color {r,g,b,a?} 0-1; Color32 0-255 ints; Rect {x,y,width,height}; LayerMask integer; asset references null, a guid or {guid} (from list_assets; registered:false refused); scene-object references null, a component guid or an object guid; arrays a JSON array (max 1024); AnimationCurve {keys:[{time,value,inTangent?,outTangent?}]} (max 256); MinMaxCurve a number or {mode:Constant|TwoConstants|Curve|TwoCurves, constant|constantMin+constantMax|curve|curveMin+curveMax, curveMultiplier?}; MinMaxGradient a color or {mode:Color|TwoColors|Gradient|RandomColor|TwoGradients, color|colorMin+colorMax|gradient|gradientMin+gradientMax}, gradient {mode?, colorKeys:[{color,time}], alphaKeys:[{alpha,time}]}.",
  "PROPERTY RULES: names as get_object lists them (unique case-insensitive match ok). Values are validated first: if one fails nothing changes. NOT settable (INVALID_PARAMS): triggers/action lists (use the action tools), guid and hiddenInInspector properties, ModComplexFloat, ModWeaponInfo, SimpleModHitAction, ModSimpleAnimation[], BoardNodeConnection[], Burst[], byte[]; on a prefab instance only Transform and Prefab Instance. A throwing setter returns INTERNAL naming the properties already set; one undo reverts them.",

  "ACTION PARAMETERS (add_action, set_action, add_trigger, set_trigger): {name: value}, max 64; names are the fields get_action_graph reports (m_seconds), alias without m_ accepted; unknown names, wrong types, bad enums or references are refused and nothing changes. Shapes as PROPERTY VALUES plus: ModComplexFloat/Int a number (= Constant) or {valueType?:Constant|RandomBetweenTwoValues, min?, max?}; ModAssetReference<T> null, guid or {guid}; reference arrays replace the list; DamageInstance an object with any of damage, ragdoll, ragdollVel, hitAnim, removeKeys, blood, bloodVel, bloodAmount, sound, volume; Display Popup m_choices an array patch (element i updates choice i, null = unchanged; keys m_buttonText, m_costType, m_costAmount, m_itemCostType, m_choiceIconReference).",
  "TARGETS: target actions (not e.g. Wait) have targetFlags (Source, Receiver, Advanced) and, with Advanced, a targets list. targetFlags: names joined by comma (\"Source, Advanced\"), an array of names, an integer 0-7 (Source=1, Receiver=2, Advanced=4), or \"0\"/\"\"/\"None\". Flags whose toggle is hidden at the call site or for the action are refused (see callSite in get_action_graph; availableTargetFlags, targetFlagsLocked, appendTargetFlags, targetType in list_action_types). targets: references (object guid, component guid or {guid}; null = empty entry), max 1024, replaced as a whole, only with Advanced in the flags; each must resolve to the action's targetType and be a registered object (scene triggers: scene objects; prefab triggers: the same prefab). Default call-site flags apply on APPEND only. A target action with no Source/Receiver flag and no Advanced targets runs on nothing (PlaySound is silent, ChangeHealth does nothing) while the playtest still logs action_executed; add_action/set_action return a warning for this (not for Spawn Prefab with Spawn At Position on, or Show Message to Everyone). PlaySound: an AudioSource target plays the action's clip if one is set (else its own), any other target component plays a positional one-shot; in a test it was audible with Receiver (This Object) and with an Advanced target (list_action_types notes).",
  "MINIGAME NOTES: Player Spawn SharedSpawn (on by default) lets several players pick the same spawn (the game picks randomly and removes only non-shared spawns); with the Point shape they start on the same point, so with punching on they can hit each other at round start (seen in a test). The Line and Circle shapes place players by index (Line with Step distribution; Random distribution is random) and are an alternative to turning SharedSpawn off. The validator reports an error when there is no shared initial spawn and fewer than 8 non-shared initial spawns. LastManStanding placement: a player who dies while the round is running gets 10 x (players dead so far + 1) points, and every player still alive when the round ends gets 10 x (dead + 1) at once, so survivors share the top value (a test with 4 players saw 10/20/30/40; this is per round).",
  "KNOWN LOG LINES: not caused by the mod: 'Loading Minigame: <name>' at playtest start, 'Closing a minigame mod.' and 'Cannot send rpc when not connected' at stop, and at the results screen 'Graphics.CopyTexture can only copy memory with the same size' and 'Releasing render texture that is set as Camera.targetTexture!' (from the game's own results-screen screenshot code).",
];

const SETTINGS_TOPIC =
  "MOD SETTINGS (Minigame and Board mods; sandbox mod, edit mode): list_mod_settings {section?} is the discovery tool: per section and field the type, accepted shape, default, current value, shown/shownWhen (a field is only writable while its condition holds), choices the editor UI offers, limits, writable (+ reason), presets. get_mod_settings {section?} returns current values in exactly the shapes set_mod_settings accepts. set_mod_settings {section, values:{field:value,...}} applies several fields of ONE section together; everything is validated first and any problem (unknown/never-shown/read-only field, a conditional field whose controlling field is not set to the right value in the same or an earlier call, a choice the UI does not offer, wrong type, undefined enum value, unknown preset name, asset guid of a missing or wrong-type asset, camera mode Follow without a Player Spawn) refuses the whole call (INVALID_PARAMS naming field and rule, nothing changed). Limits the UI applies in draw code are applied by the plugin and reported in clamped (e.g. NumberOfRounds 0 -> 1). Reply: applied (read back), previous (old values: settings have NO undo, revert by hand), clamped, implied (side effects such as ThirdPerson forcing camera Static), changed, undoable false, note (which file save_mod writes), warnings, validation (settings-related validator messages). Edits are unsaved until save_mod. " +
  "SECTIONS: details (description, previewImage; the name is read-only), minigameSettings (rounds, end conditions, points, duration, placement, score display), playerSettings (player type, movement, jumping, camera, punching, stuns, respawn, health, name tags; the six event triggers are edited with the action tools, scope playerSettings), music (Music/Ambience assets + volumes), lighting, screenEffects; Board: details (+ trophy chests, weapon cache, chest shuffle), customSpaces (read-only), boardSize, camera, music, lighting, screenEffects. Item and BoardSpace mods have no settings tools. The Workshop item (title, description, tags, thumbnail) is never touched. " +
  "ASSETS: music/previewImage take null, a guid or {guid} from list_assets (type AudioClip / Texture for the mod's own textures, Texture2D for built-in ones; includeBuiltIn for built-in assets). get shows {guid,name,assetType,location}. " +
  "LIGHTING / SCREEN EFFECTS: field preset = a name from list_mod_settings (last entry Custom); custom fields are editable only while preset is Custom (give preset Custom in the same call). Presets are never modified. Lighting is applied to the scene after each change, so screenshot shows it. screenEffects: <effect> true/false adds/removes an effect (ambientOcclusion, bloom, chromaticAberration, colorGrading, depthOfField, grain, lensDistortion, vignette), <effect>.enabled, <effect>.<param> = {value, override} (a bare value sets value and override on; {override:false} switches off; the editor ignores values whose override is off). Slider params are clamped to the game's ranges. The effects are visible only while the toolbar's Post Processing button is on. SAVE WARNING: PostProcessing.data is written from the ACTIVE effects, so save_mod while a built-in screen-effects preset is selected overwrites custom effects on disk (they survive in memory until reopen): switch back to Custom before save_mod to keep them (replies warn; Lighting.json always keeps custom lighting). " +
  "PREVIEW IMAGE: capture_preview_image takes the editor's own screenshot of the scene view (like F10), which CREATES Assets/Textures/Screenshot<N>.jpg (+ .pmeta) in the sandbox mod immediately, and assigns it as the in-game preview image (assignment unsaved until save_mod). Not allowed in run_batch; refused while a dialog or prefab is open. Data/Preview.jpg is not touched. " +
  "PLAYTEST: settings tools are edit-mode only (INVALID_STATE during a playtest); a playtest runs on the SAVED files, so save_mod before start_playtest (start_playtest does) and verify a changed setting with get_playtest_state. BATCH: set_mod_settings, get_mod_settings and list_mod_settings are allowed in run_batch.";

const ASSETS_TOPIC =
  "ASSETS (sandbox mod, edit mode, no dialog; import_asset, set_import_settings and remove_asset also need no prefab being edited, create_material and set_material work inside a prefab too): list_asset_rules shows the live limits, accepted files, import-setting fields and per-shader material properties; list_assets {location: external} lists the mod's own custom assets (internal = built-in); get_asset {guid} shows one asset in detail (file size, import settings, shader/properties, mesh/audio facts, removable, and references = every place that uses it). " +
  "IMPORT: import_asset {path, name?, type?} COPIES a local file into the open sandbox mod and registers it. path = an absolute local path (C:\\...); UNC/network/device paths are refused in this version. The source is only read (never moved, changed, locked or deleted). Allowed: .png .jpg .jpeg (Assets/Textures), .obj (Assets/Props; a .mtl is not copied), .wav .mp3 .ogg (Assets/Audio), case-insensitive on input, written lower-case. The content must match the extension (a renamed file is refused); zero-byte files, textures over 8192 px per side, OBJs without any face ('f ') line, OBJs over 500,000 vertex lines or 1,000,000 face lines, audio longer than [Assets] MaxAudioSeconds (default 600 s, read from the WAV/OGG/MP3 header), an OGG/MP3 whose header gives no duration and that is over MaxCompressedAudioMB (default 15), and files over the size limits (MaxImportFileMB default 50, MaxModAssetsMB default 500; plugin config, re-read per call) are refused. The cheap checks run on the source; every content check runs again on the copy inside the mod (written as <name>.mcp-import, renamed and registered only after it passes; on failure only the copy is removed). An import interrupted by a game crash during decoding is renamed to <name>.mcp-import-failed at the next start so it is not decoded again. name = the asset name (A-Z a-z 0-9 space _ . ( ) -, max 64; default = the source file name with other characters replaced by _). The destination folder is fixed, nothing is ever overwritten: a taken name becomes 'name (1)' ... '(20)' (renamedBecauseExists), then ALREADY_EXISTS. The reply is the new asset (guid, name, type, location external, relativePath, size), copy.sha256, checks, undoable false. The files exist on disk NOW, not only after save_mod, and stay even if you never save. IMPORT_FAILED (error.data.cleanedUp) = the game could not read the file; the copy was removed again, the source untouched. Audio is decoded by the game on the main thread and blocks it until done: prefer small files. Not allowed in run_batch. " +
  "USE: textures are referenced by guid in a material (set_material _MainTex), mesh props with create_object kind prop {prop: guid}, audio clips in a Play Sound action's clip parameter or as music/ambience (set_mod_settings music Music/Ambience), a texture as the preview image (set_mod_settings details previewImage = guid). " +
  "IMPORT SETTINGS: set_import_settings {guid, values} for the mod's own textures (compression Normal|HighQuality|None, filterMode Point|Bilinear|Trilinear, wrapMode Repeat|Clamp|Mirror|MirrorOnce) and props (recalculateNormals). Applied like the inspector's Apply: .pmeta written and, for compression/recalculateNormals, the file re-imported. A re-import swaps the texture object, so a texture used by components, actions or the preview image is refused for compression changes (ASSET_IN_USE lists them; filterMode/wrapMode are applied in place and always allowed); materials' texture slots are re-pointed automatically. For a prop, objects already placed in the scene keep showing the previous mesh until the mod is reopened. Audio, materials and prefabs have no import settings. " +
  "MATERIALS: create_material {name?, shader?, properties?} creates Assets/Materials/<name>.pmat + .pmeta NOW (the editor's Create > Material, default shader unless shader = a built-in Shader name or guid; an unknown name is refused with the valid names; asset names are matched first and the reply's shader is the Unity shader's own name; observed in a test: asking for 'Standard Shader' returned 'Standard Decal Receiver', see shader and shaderAsset in the reply) and saves the properties; set_material {guid, shader?, properties} changes only the mod's own materials and saves them at once (like Apply). properties = {key: value} per shader as listed by list_asset_rules (Standard / Standard Decal Receiver: _Mode Opaque|Cutout|Fade|Transparent, _Cutoff, _MainTex, _Color, _MetallicGlossMap, _Metallic, _Glossiness, _BumpMap, _ParallaxMap, _Parallax, _OcclusionMap, Emission, _EmissionColor, _EmissionMap, Tiling, Offset; three more shaders have their own short lists). Colours {r,g,b,a?} 0..1 (emission up to 64), vectors {x,y}, textures null or a Texture asset guid; ranges are clamped to the shader's own range (see clamped); conditional properties (_Cutoff only in Cutout, _Parallax only with a height map, _EmissionColor/_EmissionMap only with Emission) must have their controlling property in the same call or before. Shaders without a known inspector accept no properties. Assign a material with set_property on a renderer's 'materials' (or a prop's 'customMaterials') using the material guid. Materials are not undoable; reply has previous. Not allowed in run_batch. " +
  "REMOVE: remove_asset {guid, force?} deletes the file and its .pmeta NOW (not undoable) and only for the mod's own custom textures, props, audio clips, materials and prefabs (never built-in assets or the open prefab; a removed prefab leaves its instances empty). It is refused with ASSET_IN_USE and the list of references (objects/components of the scene and every prefab, action parameters and targets incl. nested popup choices, the Minigame player-settings triggers, materials' texture slots, settings such as music/ambience/custom spaces, an Item mod's prefabs and icon, the Workshop item's thumbnail and preview files; an incomplete scan counts as in use) unless force true, which removes anyway (referencesNulled lists them; referencesNote explains). Scene Prefab Instances are set to none by the game at once; other references still point at the removed asset until a reopen (read tools can show its name). Seen in the game for an action parameter: the guid stays in the saved scene file and reads as missing after a reopen; other kinds not checked. A material list can read [null] instead of empty; an undo afterwards logged \"Couldn't find object with guid\" once (restoring nothing is inferred from the code). Save and reopen to see the real state. The mod's preview image is never removed (set it to null or another texture first), nor is any asset while the mod settings cannot be read (even with force). LINKS: a path the call would write or delete that runs through a junction/symlink/mount point inside the mod, or a hard-linked file, is refused with LINK_REFUSED (nothing written); list_assets/get_asset mark such assets linked: true (reading works). Never create links inside a mod folder. save_mod + get_asset/list_assets afterwards confirms the state.\n\n" +
  "PREFABS: create_prefab {name?, fromObject?} makes a prefab through the editor (Create > Prefab, or dragging a scene object onto the Asset Browser): an empty one, or a copy of the scene object fromObject (guid) with its children. Assets/Prefabs/<name>.pfab + .pmeta are written NOW (not undoable), the source object stays untouched in the scene (no instance replaces it), the copy gets new object/component guids and its root sits at the origin. Refused: a subtree containing a Prefab Instance or Board Space, whose actions/components reference scene objects, or whose values could not all be read (incomplete scan) (the editor copies those references unchanged, so they would keep pointing at the scene); a name that exists as an asset or as a file is never reused (a free _n name is chosen and reported: nameAdjusted). open_prefab {guid} enters the prefab edit context (the Asset Browser double-click; no file is written): get_hierarchy then shows the prefab and object/component/property/action tools and undo (the prefab's own undo stack) work on it; edits stay in memory. close_prefab {} saves the prefab to disk NOW (.pfab + .pmeta, verified) and refreshes every instance in the scene (the Return to Hierarchy sequence); until then instances show the old version. While a prefab is open create_prefab, open_prefab, import_asset, remove_asset, create_test_mod/open_test_mod and start_playtest are refused (call close_prefab; their refusals name it); save_mod works and also writes the open prefab. create_prefab/open_prefab/close_prefab are not allowed in run_batch; edits inside an open prefab may be batched. Place an instance with create_object kind prefabInstance {prefab: guid}; use it with a Spawn Prefab action or Spawner (list_assets type Prefab). SAFETY OF THE FILES: a prefab file the game fails to write completely makes the whole mod unloadable, so every prefab write (create, close, save_mod, and the save start_playtest makes) is dry-run in memory first (a prefab that cannot be serialised refuses the call, nothing written), the file is checked afterwards (header, length) and a bad one is restored from a copy taken before (new ones are removed): the reply or the error says so. get_asset on a prefab shows its object tree, scene instances and references.\n\n" +
  "MESHES: the built-in meshes LowPolyCube, Primitive Cube 01 and LowPolyPlane did not show a tiled texture in a test (flat colour; a cut-out texture can make the object invisible); imported OBJ meshes with UVs in 0..1 work. LowPolyPlane is about 10 x 10 units at scale 1 (observed).";

const P = (...prefixes: string[]) => PARAGRAPHS.filter((p) => prefixes.some((x) => p.startsWith(x)));

const TOPICS: Record<string, string> = {
  overview: [PARAGRAPHS[0], ...P("WORKFLOW:", "MINIMAL VALID MINIGAME:")].join("\n\n"),
  safety: P("SAFETY:", "MOD ID:").join("\n\n"),
  editing: P("EDIT RULES:", "CLAMPING:", "PROPERTY VALUES", "PROPERTY RULES:").join("\n\n"),
  actions: P("ACTIONS:", "ADDRESSES:", "ACTION PARAMETERS", "TARGETS:").join("\n\n"),
  playtest: P("PLAYTEST:", "MINIGAME NOTES:", "KNOWN LOG LINES:").join("\n\n"),
  batch: P("BATCH:").join("\n\n"),
  settings: SETTINGS_TOPIC,
  assets: ASSETS_TOPIC,
};
const TOPIC_NAMES = Object.keys(TOPICS) as [string, ...string[]];

// Server instructions are counted against the tools/list size limit, so they stay short: the rest is in usage_guide topics.
const GUIDE =
  "pummel-party-mcp drives the Pummel Party Mod Editor through a plugin inside the running game.\n\n" +
  "SAFETY: write tools act only on sandbox mods named MCP_ (^MCP_[A-Za-z0-9_ -]+$, max 64 chars); anything else is refused (GUARD_NOT_TEST_MOD). Real mods can never be changed; Workshop publishing is blocked and has no tool. Writes also need Safety.EnableWrites (get_status: writesEnabled).\n\n" +
  "WORKFLOW: get_status -> list_mods -> open_test_mod or create_test_mod -> get_hierarchy -> edits (set_property, add_action, set_mod_settings, ...; many at once: run_batch) -> get_object / get_mod_settings to verify -> validate_mod -> save_mod -> start_playtest -> get_playtest_state -> stop_playtest. Edits stay in memory until save_mod. Settings edits are not undoable.\n\n" +
  "DETAILS: call usage_guide (no topic = overview + topic list; topics: " + TOPIC_NAMES.join(", ") + ") before using write tools: it holds the rules, value shapes and limits that tool descriptions only point to.";

const server = new McpServer({ name: "pummel-party-mcp", version: pkg.version }, { instructions: GUIDE });

// For read-only tools the destructive/idempotent hints are meaningless per the MCP spec, so they are omitted.
const READ_ONLY = { readOnlyHint: true, openWorldHint: false } as const;
const WRITE = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const WRITE_IDEMPOTENT = { ...WRITE, idempotentHint: true };
const WRITE_DESTRUCTIVE = { ...WRITE, destructiveHint: true };
// overwrites files irreversibly but the same call twice leaves the same state (set_import_settings, set_material)
const WRITE_DESTRUCTIVE_IDEMPOTENT = { ...WRITE, destructiveHint: true, idempotentHint: true };
type Annotations = { readOnlyHint: boolean; destructiveHint?: boolean; idempotentHint?: boolean; openWorldHint: boolean };

// A non-empty string. A refinement instead of .min(1): the published schema then omits "minLength":1, which was repeated
// ~50 times in tools/list (about 700 characters of the 40,000 budget); empty strings are still refused before the plugin.
const nonEmpty = () => z.string().refine((v) => v.length > 0, { message: "must not be empty" });

// registerTool() stamps every tool with execution: { taskSupport: "forbidden" }, which is also what a missing field means in the
// MCP spec (no task support). Removing it saves ~40 characters per tool (about 1,600 of the 40,000 budget).
function withoutDefaultExecution<T extends { execution?: unknown }>(tool: T): T {
  tool.execution = undefined;
  return tool;
}

const MAX_TEXT_CHARS = 100_000;
const RULES = " See usage_guide.";

type ToolResult = { content: Array<any>; isError?: boolean };

function errorResult(e: unknown): ToolResult {
  const text =
    e instanceof PluginError
      ? e.message
      : `Internal error in pummel-party-mcp MCP server: ${(e as Error)?.message ?? String(e)}`;
  return { content: [{ type: "text", text }], isError: true };
}

function textResult(result: unknown, narrow: string): ToolResult {
  let text = JSON.stringify(result) ?? "null";
  if (text.length > MAX_TEXT_CHARS) {
    text =
      text.slice(0, MAX_TEXT_CHARS) +
      `\n[Output truncated: ${MAX_TEXT_CHARS} of ${text.length} characters shown. Narrow the query: ${narrow}]`;
  }
  return { content: [{ type: "text", text }] };
}

/**
 * Registers one tool. `annotations` is mandatory so a new write tool can never silently be
 * published as read-only. Input schemas are strict: unknown keys are an error naming the key.
 */
function register(
  name: string,
  description: string,
  input: z.ZodRawShape | z.ZodType,
  annotations: Annotations,
  extra: { format?: (r: any) => ToolResult; narrow?: string } = {},
): void {
  const schema = input instanceof z.ZodType ? input : z.object(input).strict();
  const write = !annotations.readOnlyHint;
  withoutDefaultExecution(server.registerTool(
    name,
    { description, inputSchema: schema as any, annotations },
    async (args: Record<string, unknown>): Promise<ToolResult> => {
      try {
        const result = await client.call(name, args ?? {}, TOOL_TIMEOUTS_MS[name], { write });
        if (extra.format) return extra.format(result);
        return textResult(result, extra.narrow ?? "use the tool's filter/limit parameters");
      } catch (e) {
        console.error(`[pummel-party-mcp] ${name} failed: ${(e as Error)?.message}`);
        return errorResult(e);
      }
    },
  ));
}

// ---------------------------------------------------------------------------
// Trigger address and action schemas. A top-level z.union would be published without
// "type": "object" (clients reject that), so the address is one flat strict object with
// optional fields plus a refine() enforcing the two forms before anything reaches the plugin.
// ---------------------------------------------------------------------------

const pathStepSchema = z
  .object({
    // plain number schemas plus a refinement: the integer/minimum bounds zod would emit cost ~60 characters each, seven tools carry a path
    action: z.number().refine((n) => Number.isInteger(n) && n >= 0, { message: "must be an integer >= 0" }),
    choice: z.number().refine((n) => Number.isInteger(n) && n >= 0, { message: "must be an integer >= 0" }),
  })
  .strict();

const addressFields = {
  guid: nonEmpty().optional(),
  component: nonEmpty().optional(),
  scope: z.enum(["object", "playerSettings"]).optional(),
  trigger: nonEmpty().optional(),
  path: z.array(pathStepSchema).max(8).optional(),
};

type Obj = Record<string, unknown>;
const has = (o: Obj, k: string) => o[k] !== undefined;

/** Returns an error message, or undefined when the address is well-formed. */
function addressProblem(o: Obj, o2: { triggerRequired: boolean }): string | undefined {
  const anyAddress = ["guid", "component", "scope", "trigger", "path"].some((k) => has(o, k));
  if (!anyAddress && !o2.triggerRequired) return undefined;
  if (!has(o, "trigger")) return "trigger is required (the triggerName from get_action_graph, or a player-settings trigger name)";
  if (o.scope === "playerSettings") {
    if (has(o, "guid") || has(o, "component")) return 'with scope "playerSettings", guid and component must be absent';
  } else if (!has(o, "guid")) {
    return 'an address needs either guid (+ trigger) or scope "playerSettings" (+ trigger)';
  }
  return undefined;
}

function withAddress<T extends z.ZodRawShape>(
  extra: T,
  o2: { triggerRequired: boolean; check?: (o: any) => string | undefined },
) {
  return z
    .object({ ...addressFields, ...extra })
    .strict()
    .superRefine((o: any, ctx) => {
      const msg = addressProblem(o, o2) ?? o2.check?.(o);
      if (msg) ctx.addIssue({ code: "custom", message: msg });
    });
}

const indexSchema = (what: string) => z.number().int().min(0).max(100000).describe(what);
// The doc gives no minimum for parameters, so an empty object is allowed.
const parametersSchema = z
  .record(z.string(), z.any())
  .refine((o) => Object.keys(o).length <= 64, { message: "parameters must have at most 64 entries" });
// string, array of strings or integer 0-7; a refinement keeps the published schema small (used by two tools)
const targetFlagsSchema = z
  .any()
  .refine((v) => typeof v === "string" || (Array.isArray(v) && v.every((x) => typeof x === "string")) || (Number.isInteger(v) && v >= 0 && v <= 7), {
    message: "targetFlags must be a string, an array of strings or an integer 0-7",
  })
  .describe('"Source, Advanced", ["Source"] or integer 0-7.');
const targetsSchema = z.array(z.any()).max(1024);

const guidSchema = nonEmpty();
const compSchema = nonEmpty().describe("Guid, class or UI name.");

// ---------------------------------------------------------------------------
// Local tool (does not call the plugin)
// ---------------------------------------------------------------------------

withoutDefaultExecution(server.registerTool(
  "usage_guide",
  {
    description:
      "Long-form guide, local (no game call). Optional topic: overview, safety, editing, actions, playtest, batch, settings, assets; none = overview plus the topic list. Read before write tools.",
    inputSchema: z.object({ topic: z.enum(TOPIC_NAMES).optional() }).strict() as any,
    annotations: READ_ONLY,
  },
  async (args: { topic?: string }): Promise<ToolResult> => {
    const topic = args?.topic;
    const text = topic ? TOPICS[topic] : TOPICS.overview + "\n\nTOPICS (call usage_guide with topic): " + TOPIC_NAMES.join(", ") + ".";
    return { content: [{ type: "text", text }] };
  },
));

// ---------------------------------------------------------------------------
// Read tools
// ---------------------------------------------------------------------------

register(
  "ping",
  "Checks that the plugin is reachable: { pong, pluginVersion, gameVersion, apiVersion, sessionId, capabilities }. Works with the Mod Editor closed; use first.",
  {},
  READ_ONLY,
);
register(
  "get_status",
  "Editor/mod status: editorState, modOpen/modName, hasUnsavedEdits, undoStackLength, prefabContext, playtest (running, playSeconds, minigame/board, eventCount, alivePlayers), writesEnabled, publishBlock, gameVersionCheck {writesAllowed,...}. Never fails because of editor state; shows why other tools fail.",
  {},
  READ_ONLY,
);
register(
  "get_open_mod",
  "Returns the open mod: name, type, metadata, settings, publishedFileId (\"256\" = never published, a local id; not a Workshop item). Needs an open mod.",
  {},
  READ_ONLY,
);
register(
  "get_hierarchy",
  "Returns the object tree with each object's guid, name, components and children (guids for other tools). Needs an open mod.",
  {
    rootGuid: nonEmpty().optional().describe("Only this object's subtree."),
    maxDepth: z.number().int().min(0).max(64).optional().describe("0..64 (default 64); deeper nodes show depthLimited."),
    includeInactive: z.boolean().optional().describe("Default true."),
  },
  READ_ONLY,
  { narrow: "use maxDepth and/or rootGuid (a subtree)" },
);
register(
  "get_object",
  "Returns one object's transform and components with all property values (the shapes set_property takes). Needs an open mod.",
  { guid: guidSchema },
  READ_ONLY,
  { narrow: "this tool has no filter; inspect a smaller object or use get_hierarchy" },
);
register(
  "get_action_graph",
  "Triggers and actions of an object (guid, optional component) or of the player settings (scope playerSettings): one entry per slot { component, triggerName, triggerType, actions: [{ index, type, targetFlags, targets, parameters }], callSite, editable (+ reason) }. triggerName is the trigger address; parameter keys are the names add_action/set_action take.",
  z
    .object({
      guid: addressFields.guid,
      component: nonEmpty().optional().describe("Only this component's triggers; not with scope playerSettings."),
      scope: addressFields.scope,
    })
    .strict()
    .superRefine((o, ctx) => {
      let msg: string | undefined;
      if (o.scope === "playerSettings") {
        if (o.guid !== undefined || o.component !== undefined) msg = 'with scope "playerSettings", guid and component must be absent';
      } else if (o.guid === undefined) msg = 'guid is required unless scope is "playerSettings"';
      if (msg) ctx.addIssue({ code: "custom", message: msg });
    }),
  READ_ONLY,
  { narrow: "use a component filter, or query a smaller object" },
);
register(
  "list_assets",
  "Lists assets, paged: { total, hasMore, typeCounts, assets }. Built-in assets (includeBuiltIn) exceed 16,000: use search/type with limit/offset. typeCounts ignores the type filter. Needs an open mod.",
  {
    type: z.string().optional().describe("Asset type, case-insensitive (Prop, Material, Texture (own) / Texture2D (built-in), AudioClip, Shader, Prefab...)."),
    includeBuiltIn: z.boolean().optional().describe("Default false."),
    location: z.enum(["external", "internal"]).optional().describe("external = the mod's own custom assets (with sizeBytes); internal = built-in."),
    search: z.string().optional().describe("Name substring; space-separated terms must all match."),
    limit: z.number().int().min(1).max(500).optional().describe("1..500 (default 100)."),
    offset: z.number().int().min(0).optional().describe("Default 0."),
  },
  READ_ONLY,
  { narrow: "use a smaller limit, plus search and/or type" },
);
register(
  "get_log",
  "Returns recent game log entries { entries, nextId, dropped, sessionId }; pass the previous nextId as sinceId to poll. Works with the editor closed and while the game is busy.",
  {
    max: z.number().int().min(1).max(2000).optional().describe("1..2000 (default 200)."),
    sinceId: z.number().int().min(0).optional().describe("A previous nextId."),
    includeEditor: z.boolean().optional().describe("Also the editor Log window (default false)."),
  },
  READ_ONLY,
  { narrow: "use a smaller max, or sinceId" },
);
register(
  "screenshot",
  "Captures the game's current view as a PNG (image content). Works with the editor open or closed.",
  { maxWidth: z.number().int().min(64).max(7680).optional().describe("64..7680 (default 1280).") },
  READ_ONLY,
  {
    format: (r) => {
      const ok =
        r &&
        typeof r.pngBase64 === "string" &&
        r.pngBase64.length > 0 &&
        typeof r.width === "number" &&
        Number.isFinite(r.width) &&
        typeof r.height === "number" &&
        Number.isFinite(r.height);
      if (!ok) {
        return errorResult(new PluginError("INTERNAL: screenshot response lacked a non-empty pngBase64 string or numeric width/height."));
      }
      return {
        content: [
          { type: "image", mimeType: "image/png", data: r.pngBase64 },
          { type: "text", text: `Screenshot ${r.width} x ${r.height}` },
        ],
      };
    },
  },
);
register(
  "list_mods",
  "Lists the editor's mod projects [{ name, folderName, type, isTestMod, isOpen }]; isTestMod = MCP_ sandbox. Needs the Mod Editor open (EDITOR_NOT_OPEN otherwise); no mod needs to be loaded.",
  {},
  READ_ONLY,
);
register(
  "list_component_types",
  "Lists registered component types: [{ type, uiName, category, addable, reason?, allowsMultiple, supportsChildren, modTypes }]. type or uiName goes to add_component; addable follows the Add Component picker (with guid also that object's rules). Needs an open mod.",
  { guid: nonEmpty().optional().describe("Also apply this object's rules.") },
  READ_ONLY,
);
register(
  "list_action_types",
  "Lists the Action Editor palette: { modType, actionTypes: [{ type, className, uiName, category, parameters: [{ name, alias, type, kind, enumValues?, default, min?, max? }], supportsTargets, availableTargetFlags, targetFlagsLocked, appendTargetFlags, targetType, usableHere, reason? }] }. Call FIRST with the trigger address: usableHere/reason then say what the slot accepts; without an address only the mod-type gate applies. Any address key makes trigger required.",
  withAddress({}, { triggerRequired: false }),
  READ_ONLY,
  { narrow: "pass a trigger address (guid + trigger, or scope playerSettings + trigger)" },
);
register(
  "list_trigger_types",
  "Lists the trigger types of a Logic component's Event dropdown: { modType, triggerTypes: [{ type, className, parameters, sourceTargetAvailable, addable, reason? }] }. Use before add_trigger/set_trigger. Needs an open mod.",
  {},
  READ_ONLY,
);

register(
  "list_mod_settings",
  "Discovery for the open Minigame/Board mod's settings: sections and, per field, type, shape, default, current value, shown/shownWhen, choices, limits, writable (+reason), presets. Use before set_mod_settings (usage_guide topic settings). Needs an open mod, edit mode.",
  { section: nonEmpty().optional().describe("One section; default all.") },
  READ_ONLY,
  { narrow: "pass a section" },
);
register(
  "get_mod_settings",
  "Current settings by section {values, readOnly} in exactly the shapes set_mod_settings accepts (description, preview image, music, lighting, screen effects, board details...). Needs an open mod, edit mode.",
  { section: nonEmpty().optional().describe("One section; default all.") },
  READ_ONLY,
  { narrow: "pass a section" },
);

register(
  "validate_mod",
  "Runs the editor's Mod Validator (as the Play button does): { valid, canPlaytest, errorCount, warningCount, issues: [{ severity, message, description?, fixable }] }. Changes nothing. Call before start_playtest (usage_guide: MINIMAL VALID MINIGAME).",
  {},
  READ_ONLY,
);

register(
  "get_playtest_state",
  "State of the RUNNING playtest (else INVALID_STATE): { players: [{ index, name, isAI, team?, alive, health?, score, position, deaths, stunned?, respawnIn?, heldItem? }], minigame { state, round, rounds, timeRemaining?, results? }, useHealth, note }; Board mods: board, keys, trophies. Compact; poll it.",
  {},
  READ_ONLY,
);
register(
  "get_playtest_events",
  "Events of the current or last playtest { entries: [{ id, time, playSeconds, type, player?, ... }], nextId, dropped }: player_died/respawned, health_changed, score_changed, round_started/ended, minigame_finished, trigger_fired, action_executed. Pass nextId as sinceId. Kept after stop_playtest until the next playtest starts.",
  {
    max: z.number().int().min(1).max(500).optional().describe("1..500 (default 100)."),
    sinceId: z.number().int().min(0).optional().describe("A previous nextId."),
  },
  READ_ONLY,
  { narrow: "use a smaller max, or sinceId" },
);

register(
  "self_test",
  "Read-only check of the game members, publish block and inspector audit the plugin relies on: { ok, missingMembers, publishBlock, inspectorAudit, notes }. Evidence for the human before accepting an untested game version.",
  {},
  READ_ONLY,
);

// ---------------------------------------------------------------------------
// Mod lifecycle (write)
// ---------------------------------------------------------------------------

const NAME_RULE = "^MCP_[A-Za-z0-9_ -]+$, max 64 chars, no leading/trailing space.";
const nameSchema = z
  .string()
  .refine(isValidTestModName, { message: `Invalid sandbox mod name. ${NAME_RULE}` })
  .describe(NAME_RULE);
const discardSchema = z.boolean().optional().describe("Drop unsaved changes of the open sandbox mod (default false).");

register(
  "create_test_mod",
  `Creates a blank sandbox mod and opens it (up to 45 s). Refused if the name exists (ALREADY_EXISTS), while a real mod is open, or (unless discardUnsaved) while the open sandbox mod has unsaved changes.${RULES}`,
  {
    name: nameSchema,
    type: z.enum(["Minigame", "Board"]).optional().describe('Mod type (default "Minigame").'),
    discardUnsaved: discardSchema,
  },
  WRITE,
);
register(
  "open_test_mod",
  `Opens an existing sandbox mod (up to 45 s). Refused while a real mod is open, or (unless discardUnsaved) while the open sandbox mod has unsaved changes; save_mod first.${RULES}`,
  { name: nameSchema, discardUnsaved: discardSchema },
  WRITE,
);
register(
  "save_mod",
  `Saves the open sandbox mod to disk (waits up to 30 s). Only works while a MCP_ sandbox mod is open.${RULES}`,
  {},
  WRITE_IDEMPOTENT,
);

// ---------------------------------------------------------------------------
// Object / component editing (write)
// ---------------------------------------------------------------------------

const nameFieldSchema = z
  .string()
  .min(1)
  .max(256)
  .refine((v) => !/[\u0000-\u001f\u007f]/.test(v), { message: "must not contain control characters" });
const finite = z.number();
// The exact check ({x,y,z} with finite numbers and no other keys, or an array of 3 numbers) is done by a
// refinement instead of a union schema: the union would add ~230 characters to each of the six vector fields.
const vec3Strict = z.union([z.object({ x: finite, y: finite, z: finite }).strict(), z.array(finite).length(3)]);
const vec3 = () =>
  z
    .any()
    .refine((v) => vec3Strict.safeParse(v).success, { message: "must be {x,y,z} or [x,y,z] with finite numbers" })
    .describe("{x,y,z} or [x,y,z]");

const CREATE_KINDS = [
  "empty", "prefabInstance", "cube", "sphere", "plane", "prop", "particleSystem", "text", "lineRenderer",
  "playerSpawn", "item", "trigger", "logic", "weaponSource", "boardSpace",
  "directionalLight", "pointLight", "spotLight", "audioSource",
] as const;

register(
  "set_mod_settings",
  `Sets several fields of one settings section together (all-or-nothing validation; not undoable, previous values returned; unsaved until save_mod).${RULES}`,
  {
    section: nonEmpty().describe("Section from list_mod_settings."),
    values: z.record(nonEmpty(), z.any()).refine((o) => Object.keys(o).length >= 1 && Object.keys(o).length <= 128, { message: "values must have 1-128 entries" }).describe("{field: value}."),
  },
  WRITE_IDEMPOTENT,
);
register(
  "capture_preview_image",
  `Takes the editor's screenshot of the scene (like F10): CREATES a JPEG texture file + .pmeta in the sandbox mod's Assets/Textures right away and assigns it as the in-game preview image (unsaved until save_mod).${RULES}`,
  {},
  WRITE,
);

// ---------------------------------------------------------------------------
// Assets (import, import settings, materials, removal)
// ---------------------------------------------------------------------------

register(
  "get_asset",
  "One asset in detail { guid, name, type, location, relativePath, sizeBytes, ownCustomAsset, importSettings, material shader/properties, removable, references: { count, items, scanComplete }, limits }. references lists every object, action, material and setting that uses it. Needs an open mod, edit mode.",
  { guid: guidSchema },
  READ_ONLY,
);
register(
  "list_asset_rules",
  "Discovery for assets: accepted file types, destination folders, live size limits, import-setting fields per type, material properties per shader (types, limits, conditions) and the built-in shader assets (usage_guide topic assets).",
  {},
  READ_ONLY,
);
register(
  "import_asset",
  `COPIES a local file into the open sandbox mod's Assets folder (Textures/Props/Audio) and registers it: .png .jpg .jpeg .obj .wav .mp3 .ogg. The source is only read. Files exist on disk immediately; not undoable (remove_asset deletes). Never overwrites.${RULES}`,
  {
    path: nonEmpty().describe("Absolute local path (C:\\...). UNC/network paths are refused."),
    name: z.string().max(64).optional().describe("Asset name (A-Za-z0-9 _.()-); default: the source file name."),
    type: z.enum(["Texture", "Prop", "AudioClip"]).optional().describe("Optional check against the file."),
  },
  WRITE,
);
register(
  "set_import_settings",
  `Sets import settings of one of the mod's own textures (compression, filterMode, wrapMode) or props (recalculateNormals) and applies them like the inspector's Apply (writes the .pmeta now, re-imports if needed). Not undoable.${RULES}`,
  {
    guid: guidSchema,
    values: z.record(nonEmpty(), z.any()).refine((o) => Object.keys(o).length >= 1 && Object.keys(o).length <= 8, { message: "values must have 1-8 entries" }).describe("{field: value}."),
  },
  WRITE_DESTRUCTIVE_IDEMPOTENT,
);
register(
  "create_material",
  `Creates a material like the editor's Create > Material (Assets/Materials/<name>.pmat + .pmeta written NOW) and sets its properties. Not undoable. Shader name/guid from list_asset_rules.${RULES}`,
  {
    name: z.string().max(64).optional().describe("Material name (A-Za-z0-9 _.()-); default Material_<n>."),
    shader: nonEmpty().optional().describe("Built-in Shader name or guid; default: the editor's default shader."),
    properties: z.record(nonEmpty(), z.any()).optional().describe("{property: value} per shader."),
  },
  WRITE,
);
register(
  "set_material",
  `Changes the shader and/or properties of one of the mod's own materials and saves it at once (like Apply). Not undoable; the reply has previous.${RULES}`,
  {
    guid: guidSchema,
    shader: nonEmpty().optional(),
    properties: z.record(nonEmpty(), z.any()).optional().describe("{property: value}."),
  },
  WRITE_DESTRUCTIVE_IDEMPOTENT,
);
register(
  "create_prefab",
  "Creates a prefab like the editor: empty, or a copy of scene object fromObject. Writes Assets/Prefabs/<name>.pfab + .pmeta NOW (verified); not undoable. See usage_guide assets.",
  {
    name: z.string().max(64).optional().describe("Prefab name; an existing one gets _n."),
    fromObject: nonEmpty().optional().describe("Scene object guid."),
  },
  WRITE,
);
register(
  "open_prefab",
  "Enters a prefab's edit context (writes no file; edits stay in memory). See usage_guide assets.",
  { guid: guidSchema },
  WRITE,
);
register(
  "close_prefab",
  "Leaves the open prefab: SAVES it to disk NOW (verified) and refreshes its scene instances. See usage_guide assets.",
  {},
  WRITE_DESTRUCTIVE,
);
register(
  "remove_asset",
  `DELETES one of the mod's own custom assets (texture, prop, audio clip, material, prefab) and its .pmeta from the sandbox mod NOW; not undoable. Refused with ASSET_IN_USE (references listed) while it is used, unless force true (the references are then lost).${RULES}`,
  { guid: guidSchema, force: z.boolean().optional().describe("Default false.") },
  WRITE_DESTRUCTIVE,
);

register(
  "create_object",
  `Creates an object (like the GameObject menu) and selects it. kind defaults to "empty" ("prop" if prop is given, "prefabInstance" if prefab is given). playerSpawn/item/trigger/logic/weaponSource: Minigame only; boardSpace: Board only; prefabInstance/boardSpace: not while a prefab is edited. Parents that cannot have children (prefab instance, board space, item, text) are refused. Transform values are local, clamped. Not undoable: use delete_object.${RULES}`,
  {
    kind: z.enum(CREATE_KINDS).optional(),
    name: nameFieldSchema.optional(),
    parentGuid: nonEmpty().nullable().optional().describe("null/omitted = top level."),
    position: vec3().optional(),
    rotation: vec3().optional(),
    scale: vec3().optional(),
    prop: nonEmpty().optional().describe("Prop asset guid (kind prop)."),
    prefab: nonEmpty().optional().describe("Prefab asset guid (kind prefabInstance)."),
  },
  WRITE,
);
register(
  "duplicate_object",
  `Duplicates an object with its children (Ctrl+D); the clone is placed next to the original, selected, with fresh guids. Undo destroys the clone.${RULES}`,
  { guid: guidSchema },
  WRITE,
);
register(
  "delete_object",
  `Deletes an object and its children (undo restores it). Nothing is selected afterwards. The open prefab's root cannot be deleted. References or actions pointing at the object may break.${RULES}`,
  { guid: guidSchema },
  WRITE_DESTRUCTIVE,
);
register(
  "reparent_object",
  `Moves an object under a new parent; world transform is kept. Refused: into itself or a descendant, a parent that cannot have children or is in another context, the open prefab's root, siblingIndex out of range. No change gives changed false.${RULES}`,
  {
    guid: guidSchema,
    newParentGuid: nonEmpty().nullable().describe("Required; null = top level."),
    siblingIndex: z.number().int().min(0).optional().describe("Default: last."),
  },
  WRITE,
);
register(
  "set_object",
  `Sets an object's name and/or active flag (at least one).${RULES}`,
  { guid: guidSchema, name: nameFieldSchema.optional(), active: z.boolean().optional().describe("Active flag.") },
  WRITE_IDEMPOTENT,
);
register(
  "set_transform",
  `Sets local position, rotation and/or scale (at least one). Values are clamped; the reply gives applied, clamped and transform.${RULES}`,
  {
    guid: guidSchema,
    position: vec3().optional(),
    rotation: vec3().optional(),
    scale: vec3().optional(),
  },
  WRITE_IDEMPOTENT,
);
register(
  "add_component",
  `Adds a component like the Add Component popup; list_component_types (with the guid) shows addable types and why not. Editor rules apply: single-instance types; Prefab Instance, Board Space, Item and Text need a childless object and exclude each other; nothing on a prefab instance.${RULES}`,
  { guid: guidSchema, type: nonEmpty().describe("Class name (ModLight) or UI name (Light).") },
  WRITE,
);
register(
  "remove_component",
  `Removes a component. Refused for Transform and never-offered types, and on a prefab instance for all but Prefab Instance. Removing Prefab Instance, Board Space, Item or Text destroys its generated children (childrenDestroyed); undo restores the component but not those children.${RULES}`,
  { guid: guidSchema, component: compSchema },
  WRITE_DESTRUCTIVE,
);
register(
  "set_property",
  `Sets one property of one component. Value shapes and the list of non-settable properties are in usage_guide (PROPERTY VALUES/RULES). The reply's applied shows what was stored; inspectorClamped lists inspector-clamped properties.${RULES}`,
  {
    guid: guidSchema,
    component: compSchema,
    property: nonEmpty(),
    value: z
      .any()
      .refine((v) => v !== undefined, { message: "value is required (use null to clear a reference)" })
      .describe("Required; null clears a reference."),
  },
  WRITE_IDEMPOTENT,
);
register(
  "set_properties",
  `Sets 1-64 properties of one component in one call: one undo step, and if any value is invalid nothing changes. Value shapes and non-settable properties: usage_guide (PROPERTY VALUES/RULES).${RULES}`,
  {
    guid: guidSchema,
    component: compSchema,
    values: z
      .record(nonEmpty(), z.any())
      .refine((o) => Object.keys(o).length >= 1 && Object.keys(o).length <= 64, { message: "values must have 1-64 entries" })
      .describe("{propertyName: value}, 1-64 entries."),
  },
  WRITE_IDEMPOTENT,
);
register(
  "undo",
  `Undoes the last editor step(s) like Ctrl+Z on the current context's stack; no redo. One edit can add two stack entries (selection is a step), but steps 1 still reverts the edit. Returns { undone, events, undoStackLength }; undone 0 + note = empty stack.${RULES}`,
  { steps: z.number().int().min(1).max(20).optional().describe("1-20 (default 1).") },
  WRITE_DESTRUCTIVE,
);

// ---------------------------------------------------------------------------
// Action / trigger editing (write)
// ---------------------------------------------------------------------------

register(
  "add_action",
  `Adds an action to a trigger's list. type: ModActionType name (ChangeHealth), class or UI name; call list_action_types with the same address first. index omitted = append (call-site default target flags apply); 0..count-1 inserts before that action. Applied in order: defaults, call-site flags (append only), parameters, targetFlags, targets. Refused: unknown type, an action the call site does not offer, index > count.${RULES}`,
  withAddress(
    {
      type: nonEmpty(),
      index: indexSchema("Omitted = append.").optional(),
      parameters: parametersSchema.optional(),
      targetFlags: targetFlagsSchema.optional(),
      targets: targetsSchema.optional(),
    },
    { triggerRequired: true },
  ),
  WRITE,
);
register(
  "remove_action",
  `Removes the action at index from a trigger's list (later actions shift down; a Display Popup takes its nested lists with it).${RULES}`,
  withAddress({ index: indexSchema("Action index.") }, { triggerRequired: true }),
  WRITE_DESTRUCTIVE,
);
register(
  "move_action",
  `Moves the action at index to newIndex (its final position, 0..count-1) in the same list; same index gives changed false.${RULES}`,
  withAddress(
    { index: indexSchema("Current index."), newIndex: indexSchema("Final index.") },
    { triggerRequired: true },
  ),
  WRITE,
);
register(
  "set_action",
  `Changes parameters, targetFlags and/or targets of the action at index (at least one); one undo step; values equal to the current ones give changed false.${RULES}`,
  withAddress(
    {
      index: indexSchema("Action index."),
      parameters: parametersSchema.optional(),
      targetFlags: targetFlagsSchema.optional(),
      targets: targetsSchema.optional(),
    },
    {
      triggerRequired: true,
      check: (o) =>
        o.parameters === undefined && o.targetFlags === undefined && o.targets === undefined
          ? "give at least one of parameters, targetFlags, targets"
          : undefined,
    },
  ),
  WRITE_IDEMPOTENT,
);
register(
  "add_trigger",
  `Appends a trigger to an object's Logic component list. type: Start (default), Enable, Update, Hit, WeaponHit, Timer, RoundStart, RoundEnd or class name; must be addable (list_trigger_types). parameters: Hit/WeaponHit m_hitType, Timer m_interval (min >= 0.1). Only guid and component address it. Returns the trigger as a get_action_graph entry (Triggers[n] = its address).${RULES}`,
  z
    .object({
      guid: nonEmpty(),
      component: nonEmpty().optional(),
      type: nonEmpty().optional().describe('Default "Start".'),
      parameters: parametersSchema.optional(),
    })
    .strict(),
  WRITE,
);
register(
  "remove_trigger",
  `Removes the trigger Triggers[i] from a Logic component's list (no path); later entries shift down (indexNote), so re-read get_action_graph. Fixed slots and player-settings triggers cannot be removed: clear them with remove_action.${RULES}`,
  withAddress(
    {},
    {
      triggerRequired: true,
      check: (o) => (o.path !== undefined && o.path.length > 0 ? "path is not accepted by remove_trigger" : undefined),
    },
  ),
  WRITE_DESTRUCTIVE,
);
register(
  "set_trigger",
  `Changes a trigger's type and/or parameters (at least one). type: Logic list entries (Triggers[i]) only; a different type replaces the trigger, KEEPS its actions and RESETS parameters to the new type's defaults (given parameters are then applied). parameters: Hit/WeaponHit m_hitType, Timer m_interval (min >= 0.1); other slots have none.${RULES}`,
  withAddress(
    {
      type: nonEmpty().optional(),
      parameters: parametersSchema.optional(),
    },
    {
      triggerRequired: true,
      check: (o) => (o.type === undefined && o.parameters === undefined ? "give at least one of type, parameters" : undefined),
    },
  ),
  WRITE_IDEMPOTENT,
);

// ---------------------------------------------------------------------------
// Playtest and batch (write)
// ---------------------------------------------------------------------------

// The toolbar's player-count dropdown labels (case-insensitive) plus the short forms the doc lists.
const PLAYERS_STRING_RE = /^(?:[2-8](?: Players)?|2v2(?: Teams)?|2v2v2(?: Teams)?|2v2v2v2(?: Teams)?|3v3(?: Teams)?|4v4(?: Teams)?)$/i;

register(
  "start_playtest",
  `SAVES the open sandbox mod to disk, then presses Play and waits (up to 60 s) for play mode. Validator errors give INVALID_STATE unless validateFirst is false. aiOnly: AIs play by themselves; else a human must.${RULES}`,
  {
    players: z
      .union([
        z.number().int().min(2).max(8),
        z.string().regex(PLAYERS_STRING_RE, { message: 'players must be 2-8 or a toolbar label such as "4 Players", "2v2 Teams", "3v3" (also "2".."8", "2v2", "2v2v2", "2v2v2v2", "3v3", "4v4")' }),
      ])
      .optional()
      .describe('2-8, or "N Players", "2v2/2v2v2/2v2v2v2/3v3/4v4 Teams" (case-insensitive, " Players"/" Teams" optional). Default: toolbar choice.'),
    aiOnly: z.boolean().optional().describe("Every player is an AI (default: toolbar checkbox)."),
    validateFirst: z.boolean().optional().describe("Default true; false = Run With Errors."),
  },
  WRITE,
);
register(
  "stop_playtest",
  `Stops the playtest like the Stop button and waits (up to 30 s) for edit mode. Saves nothing; alreadyStopped true if not playing. Needed before edits.${RULES}`,
  {},
  WRITE_IDEMPOTENT,
);

const BATCH_TOOLS = [
  "create_object", "duplicate_object", "delete_object", "reparent_object", "set_object", "set_transform",
  "add_component", "remove_component", "set_property", "set_properties",
  "add_action", "remove_action", "move_action", "set_action", "add_trigger", "remove_trigger", "set_trigger",
  "undo", "save_mod", "set_mod_settings",
  "get_object", "get_hierarchy", "get_action_graph", "get_mod_settings", "list_mod_settings", "list_mods", "list_assets", "list_component_types",
  "list_action_types", "list_trigger_types",
] as const;
const batchStepSchema = z
  .object({
    tool: z.enum(BATCH_TOOLS),
    params: z
      .record(z.string(), z.any())
      .nullable()
      .optional()
      .describe('Tool params (omitted/null = {}); a string value starting with $ is a reference like "$id.guid".'),
    id: z
      .string()
      .regex(/^[A-Za-z][A-Za-z0-9_-]{0,63}$/, { message: "id must match [A-Za-z][A-Za-z0-9_-]{0,63}" })
      .optional()
      .describe("Unique step id."),
  })
  .strict();
register(
  "run_batch",
  `Runs 1-100 steps in order (up to 120 s); params may reference earlier results ("$floor.guid"; usage_guide: BATCH). NO ROLLBACK; stopOnError (default true) stops at the first failure. Can contain deletes.${RULES}`,
  z
    .object({
      steps: z
        .array(batchStepSchema)
        .min(1)
        .max(100)
        .refine(
          (a) => {
            const ids = a.map((x) => x.id).filter((x) => x !== undefined);
            return new Set(ids).size === ids.length;
          },
          { message: "step ids must be unique" },
        ),
      stopOnError: z.boolean().optional().describe("Default true; false = attempt every step."),
    })
    .strict(),
  WRITE_DESTRUCTIVE,
);

await server.connect(new StdioServerTransport());
// Exit when the MCP client closes stdin (the SDK transport does not do this itself).
process.stdin.on("end", () => process.exit(0));
process.stdin.on("close", () => process.exit(0));
console.error(`[pummel-party-mcp] MCP server ${pkg.version} ready; plugin at 127.0.0.1:${opts.port}`);
