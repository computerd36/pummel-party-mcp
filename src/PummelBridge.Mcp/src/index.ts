#!/usr/bin/env node
import { readFileSync } from "node:fs";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { BridgeClient, BridgeError, optionsFromEnv, TOOL_TIMEOUTS_MS, isValidTestModName } from "./client.js";

const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { version: string };
const opts = optionsFromEnv();
const client = new BridgeClient(opts);

const INSTRUCTIONS =
  "PummelBridge connects to the Pummel Party Mod Editor. Read tools: ping, get_status, get_open_mod, get_hierarchy, get_object, get_action_graph, list_assets, list_component_types, get_log, screenshot, list_mods. " +
  "Write tools: create_test_mod, open_test_mod, save_mod (mod lifecycle) and create_object, duplicate_object, delete_object, reparent_object, set_object, set_transform, add_component, remove_component, set_property, set_properties, undo (object/component editing). " +
  "Safety model: write tools only ever act on sandbox mods whose name starts with PB_TEST_; the plugin refuses anything else. The bridge cannot open, modify, rename or delete the author's real mods, and Workshop publishing is blocked entirely while the plugin is loaded (there is no tool for it). " +
  "If a real (non-sandbox) mod is open in the editor, create_test_mod/open_test_mod are refused until the human closes it by hand. " +
  "Recommended workflow: get_status (editor open? which mod? Editing state?) -> list_mods, then open_test_mod or create_test_mod -> get_hierarchy (object guids) -> edits (create_object, set_transform, add_component, set_property/set_properties, ...) -> get_object to verify the result -> save_mod. " +
  "Edits are in memory until save_mod. Every edit selects the object it edits and leaves it selected. Edit tools return INVALID_STATE while an editor dialog is open, the mouse button is held, or play mode is active. undo reverts edits (there is no redo; creating an object is not undoable, use delete_object).";

const server = new McpServer({ name: "pummelbridge", version: pkg.version }, { instructions: INSTRUCTIONS });

const RO =
  "Read-only; never changes anything. Needs Pummel Party running with the PummelBridge plugin and the Mod Editor open with a mod loaded.";
const READ_ONLY = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
const WRITE = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const WRITE_IDEMPOTENT = { ...WRITE, idempotentHint: true };
const WRITE_DESTRUCTIVE = { ...WRITE, destructiveHint: true };

type ToolResult = { content: Array<any>; isError?: boolean };

function errorResult(e: unknown): ToolResult {
  const text =
    e instanceof BridgeError
      ? e.message
      : `Internal error in PummelBridge MCP server: ${(e as Error)?.message ?? String(e)}`;
  return { content: [{ type: "text", text }], isError: true };
}

function register(
  name: string,
  description: string,
  shape: z.ZodRawShape,
  extra: { format?: (r: any) => ToolResult; annotations?: typeof READ_ONLY } = {},
): void {
  server.registerTool(
    name,
    { description, inputSchema: shape, annotations: extra.annotations ?? READ_ONLY },
    async (args: Record<string, unknown>): Promise<ToolResult> => {
      try {
        const result = await client.call(name, args ?? {}, TOOL_TIMEOUTS_MS[name]);
        if (extra.format) return extra.format(result);
        return { content: [{ type: "text", text: JSON.stringify(result, null, 2) ?? "null" }] };
      } catch (e) {
        console.error(`[pummelbridge] ${name} failed: ${(e as Error)?.message}`);
        return errorResult(e);
      }
    },
  );
}

register(
  "ping",
  "Check that the PummelBridge plugin inside Pummel Party is reachable. Returns { pong, pluginVersion, gameVersion, unityVersion, apiVersion } (apiVersion 2 includes the write tools). Use first to verify the connection. Read-only; works whenever the game is running with the plugin loaded (the Mod Editor does not need to be open).",
  {},
);
register(
  "get_status",
  `Returns the current editor/mod status (whether the Mod Editor is open, which mod is loaded, loading state), plus publishBlock (state of the plugin's Workshop-publish safety patches) and writesEnabled (whether write tools are enabled in the plugin config, Safety.EnableWrites). Use to find out why other tools fail or whether the game is ready. ${RO}`,
  {},
);
register(
  "get_open_mod",
  `Returns the mod currently open in the Mod Editor: its name, type, metadata and settings. Use to learn what the modder is working on. ${RO}`,
  {},
);
register(
  "get_hierarchy",
  `Returns the object tree (scene hierarchy) of the open mod, with each object's GUID, name and children. GUIDs needed by get_object and get_action_graph come from this tool. Use rootGuid to return only a subtree, maxDepth to limit depth on big mods, and includeInactive to include disabled objects. ${RO}`,
  {
    rootGuid: z.string().optional().describe("GUID of the object to use as tree root (from a previous get_hierarchy). Omit for the whole scene."),
    maxDepth: z.number().int().min(0).optional().describe("Maximum depth of the tree to return."),
    includeInactive: z.boolean().optional().describe("Include inactive (disabled) objects."),
  },
);
register(
  "get_object",
  `Returns the components and property values of one object in the open mod. Requires the object's GUID, which comes from get_hierarchy. Use to inspect how an object is configured. ${RO}`,
  { guid: z.string().min(1).describe("GUID of the object (obtain from get_hierarchy).") },
);
register(
  "get_action_graph",
  `Returns the triggers and actions (the action graph) attached to an object, showing what fires what. Requires the object's GUID from get_hierarchy; optionally restrict to one component by name. Use to understand or debug a mod's logic. ${RO}`,
  {
    guid: z.string().min(1).describe("GUID of the object (obtain from get_hierarchy)."),
    component: z.string().optional().describe("Only return the graph of this component (name as shown by get_object)."),
  },
);
register(
  "list_assets",
  `Lists assets available to the open mod (models, textures, audio, prefabs, etc.), paged. Built-in game assets (includeBuiltIn) number over 16,000, so use search and/or type plus limit/offset instead of listing everything. Returns { total, offset, limit, returned, hasMore, typeCounts, assets }; typeCounts shows how many assets of each type match the search (ignoring the type filter), so you can see which types exist. Page with offset while hasMore is true. Use to find asset names to reference. ${RO}`,
  {
    type: z.string().optional().describe("Asset type filter (case-insensitive exact match, e.g. Prop, Material, Texture2D, AudioClip, Shader)."),
    includeBuiltIn: z.boolean().optional().describe("Include built-in game assets (over 16,000), not just the mod's own."),
    search: z.string().optional().describe("Case-insensitive substring match on the asset name; whitespace-separated terms must all match."),
    limit: z.number().int().min(1).max(500).optional().describe("Page size (1..500, default 100)."),
    offset: z.number().int().min(0).optional().describe("Number of matching assets to skip (default 0)."),
  },
);
register(
  "get_log",
  `Returns recent game/Unity log entries as { entries, nextId }. Pass the previous nextId as sinceId to fetch only new entries (incremental polling). Use to find errors after the modder tries something. ${RO}`,
  {
    max: z.number().int().min(1).max(2000).optional().describe("Maximum number of entries to return (1..2000)."),
    sinceId: z.number().int().min(0).optional().describe("Only return entries newer than this id (use nextId from a previous call)."),
  },
);
register(
  "screenshot",
  `Captures the game's current view as a PNG image (returned as image content). Use to see what the editor or scene currently looks like. Optionally downscale with maxWidth. ${RO}`,
  { maxWidth: z.number().int().min(64).max(3840).optional().describe("Maximum image width in pixels (64..3840); downscaled if wider.") },
  {
    format: (r) => {
      if (!r || typeof r.pngBase64 !== "string") {
        return errorResult(new BridgeError("INTERNAL: screenshot response did not contain pngBase64."));
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

const NAME_RULE = "Must match ^PB_TEST_[A-Za-z0-9_ -]+$, at most 64 characters, no leading or trailing space.";
const nameSchema = z
  .string()
  .refine(isValidTestModName, { message: `Invalid sandbox mod name. ${NAME_RULE}` })
  .describe(`Sandbox mod name. ${NAME_RULE}`);

const SAFETY =
  "Safety model: write tools only ever act on sandbox mods whose name starts with PB_TEST_; the plugin refuses anything else (GUARD_NOT_TEST_MOD). The bridge cannot open, modify, rename or delete the author's real mods, and Workshop publishing is blocked entirely while the plugin is loaded; there is no tool for it.";
const WRITE_REQ =
  "Needs Pummel Party running with the PummelBridge plugin and the Mod Editor open, and writes enabled in the plugin config (Safety.EnableWrites).";

register(
  "list_mods",
  "Lists the local mod projects the editor knows, as [{ name, folderName, type, isTestMod, isOpen }]. isTestMod is true for sandbox mods (name starts with PB_TEST_), the only ones the write tools can touch. Read-only; never changes anything. Needs Pummel Party running with the PummelBridge plugin.",
  {},
);
register(
  "create_test_mod",
  `Creates a new blank sandbox mod and opens it in the editor (asynchronous load, waits up to 45 s). ${SAFETY} Refused while a non-sandbox mod is open in the editor; the human must close it by hand. Refused if the name already exists (ALREADY_EXISTS). ${WRITE_REQ}`,
  {
    name: nameSchema,
    type: z.enum(["Minigame", "Board"]).optional().describe("Mod type (plugin default if omitted)."),
  },
  { annotations: WRITE },
);
register(
  "open_test_mod",
  `Opens an existing sandbox mod in the editor (asynchronous load, waits up to 45 s). ${SAFETY} Refused while a non-sandbox mod is open in the editor; the human must close it by hand. Refuses to discard unsaved changes of the currently open sandbox mod unless discardUnsaved is true; prefer save_mod first. ${WRITE_REQ}`,
  {
    name: nameSchema,
    discardUnsaved: z.boolean().optional().describe("Allow discarding unsaved changes of the currently open sandbox mod (default false)."),
  },
  { annotations: WRITE },
);
register(
  "save_mod",
  `Saves the open sandbox mod to disk (waits up to 30 s). Only works while a PB_TEST_ sandbox mod is open. ${SAFETY} ${WRITE_REQ}`,
  {},
  { annotations: WRITE_IDEMPOTENT },
);


// ---------------------------------------------------------------------------
// Object / component editing tools (plugin api v2, increment 2)
// ---------------------------------------------------------------------------

const EDIT_COMMON =
  "Edit tool. Works only on PB_TEST_ sandbox mods (GUARD_NOT_TEST_MOD otherwise), only in the editor's Editing state, and returns INVALID_STATE while an editor dialog is open, the left mouse button is held in the game window, or play mode is active (wait or tell the human, then retry). " +
  "Objects are addressed by their guid from get_hierarchy (or get_object); components by component guid, class name (ModLight) or UI name (Light), case-insensitive. " +
  "The bridge selects the object it edits and leaves it selected (the user's previous selection is replaced). " +
  "Edits are in memory only until save_mod is called; nothing is saved automatically. Workshop publishing is blocked entirely; there is no tool for it. " +
  "The reply contains changed, undoable and undoStackLength (plus undoPending/note when something needs explaining); verify the result with get_object. Use the undo tool to revert; there is no redo.";

const EDIT_UNDO_STEP = "All changes of one call form one undo step.";

const guidSchema = z.string().min(1).describe("GUID of the object (its ModTransform guid, from get_hierarchy).");
const nameFieldSchema = z
  .string()
  .min(1)
  .max(256)
  .regex(/^[^\u0000-\u001f\u007f]+$/, "must not contain control characters")
  .describe("Object name, 1-256 characters, no control characters. Names need not be unique; address objects by guid.");
const finite = z.number();
const vec3 = (what: string) =>
  z
    .union([
      z.object({ x: finite, y: finite, z: finite }).strict(),
      z.tuple([finite, finite, finite]),
    ])
    .describe(`${what} as {x, y, z} or [x, y, z]; finite numbers.`);

const CREATE_KINDS = [
  "empty", "prefabInstance", "cube", "sphere", "plane", "prop", "particleSystem", "text", "lineRenderer",
  "playerSpawn", "item", "trigger", "logic", "weaponSource", "boardSpace",
  "directionalLight", "pointLight", "spotLight", "audioSource",
] as const;

register(
  "list_component_types",
  `Lists every registered mod component type (Transform first) as [{ type, uiName, category, addable, reason?, allowsMultiple, supportsChildren, modTypes }]. type (class name, e.g. ModLight) or uiName can be passed to add_component. addable says whether the editor's Add Component picker would offer and enable the type for the open mod type and editing context; with guid it also applies the per-object rules of add_component (reason explains why not). Use before add_component. Works on any open mod. ${RO}`,
  { guid: z.string().min(1).optional().describe("Optional object guid (from get_hierarchy): also apply the per-object rules of add_component (addable is then specific to that object).") },
);
register(
  "create_object",
  `Creates a new object like the editor's GameObject menu and selects it. kind (default "empty"; "prop" if prop is given; "prefabInstance" if prefab is given) is one of: empty, prefabInstance (not while a prefab is edited), cube, sphere, plane, prop, particleSystem, text, lineRenderer, playerSpawn / item / trigger / logic / weaponSource (Minigame mods only), boardSpace (Board mods only, not while a prefab is edited), directionalLight, pointLight, spotLight, audioSource. With kind prop, prop is a Prop asset guid (list_assets type Prop, built-in or mod); with kind prefabInstance, prefab is a Prefab asset guid (list_assets type Prefab). parentGuid null/omitted = top level of the scene (or directly below the open prefab's root); a parent that does not support children (prefab instance, board space, item, text) is refused. position/rotation/scale are local and go through ModTransform (position is clamped so the world position stays within -1000..1000, rotation to -3600..3600 per axis, scale to 0.001..1000 per axis). Returns the new object (guid, name, components, parentGuid, siblingIndex, transform, kind, selected). Creation is NOT undoable in the editor (undoable is always false; undo would only re-select); remove a mistaken object with delete_object, which is undoable. ${EDIT_COMMON}`,
  {
    kind: z.enum(CREATE_KINDS).optional().describe(`Creation entry (exact spelling). Default "empty", or "prop" if prop is given, or "prefabInstance" if prefab is given. One of: ${CREATE_KINDS.join(", ")}.`),
    name: nameFieldSchema.optional().describe("Name for the new object, 1-256 characters, no control characters (default: the editor's default name)."),
    parentGuid: z.string().min(1).nullable().optional().describe("GUID of the parent object (from get_hierarchy). null or omitted = top level of the scene (or directly below the open prefab's root object)."),
    position: vec3("Local position (default 0,0,0)").optional(),
    rotation: vec3("Local Euler angles in degrees (default 0,0,0)").optional(),
    scale: vec3("Local scale (default 1,1,1)").optional(),
    prop: z.string().min(1).optional().describe("Only with kind prop: GUID of a Prop asset (list_assets type Prop)."),
    prefab: z.string().min(1).optional().describe("Only with kind prefabInstance: GUID of a Prefab asset of the mod (list_assets type Prefab)."),
  },
  { annotations: WRITE },
);
register(
  "duplicate_object",
  `Duplicates an object with all its children (the editor's Ctrl+D): the clone keeps the name, is placed next to the original under the same parent, is selected, and every component gets a fresh guid. Replaces the editor's copy clipboard. Returns { sourceGuid, objects: [clone nodes with new guids] }. Undoable (undo destroys the clone). ${EDIT_COMMON}`,
  { guid: guidSchema.describe("GUID of the object to duplicate (from get_hierarchy).") },
  { annotations: WRITE },
);
register(
  "delete_object",
  `Deletes an object and its children (the editor's Delete; a soft delete that undo can reverse). Afterwards nothing is selected. The open prefab's root object cannot be deleted. Returns { guid, name, deleted } where deleted counts the object plus its editor-visible descendants. Undoable: undo puts it back at the same parent and index. Destructive within the sandbox mod until saved: references or actions pointing at the object may break. ${EDIT_COMMON}`,
  { guid: guidSchema.describe("GUID of the object to delete (from get_hierarchy).") },
  { annotations: WRITE_DESTRUCTIVE },
);
register(
  "reparent_object",
  `Moves an object under a new parent (like hierarchy drag and drop). World position, rotation and scale are kept; the local values are recomputed (see transform in the reply). newParentGuid is required: a GUID, or null for the top level of the scene (or directly below the open prefab's root). siblingIndex is the final index among the new parent's children (omitted = last child). Refused (INVALID_PARAMS) for: moving an object into itself or its descendant, a parent that does not support children (prefab instance, board space, item, text), a parent in another context (scene vs prefab), moving the open prefab's root, or a siblingIndex out of range. Same parent and same/omitted index gives changed false. Undoable. ${EDIT_COMMON}`,
  {
    guid: guidSchema.describe("GUID of the object to move (from get_hierarchy)."),
    newParentGuid: z.string().min(1).nullable().describe("GUID of the new parent object, or null for the top level of the scene (or directly below the open prefab's root object). Required; pass null explicitly for top level."),
    siblingIndex: z.number().int().min(0).optional().describe("Final index among the new parent's children (>= 0). Omitted = append as last child."),
  },
  { annotations: WRITE },
);
register(
  "set_object",
  `Sets an object's name and/or active flag (the inspector's name field and active checkbox). Give at least one of name/active; both in one call = one undo step. Returns { guid, name, active, activeInHierarchy }. Undoable. ${EDIT_COMMON}`,
  {
    guid: guidSchema,
    name: nameFieldSchema.optional(),
    active: z.boolean().optional().describe("Active checkbox (GameObject.SetActive)."),
  },
  { annotations: WRITE_IDEMPOTENT },
);
register(
  "set_transform",
  `Sets an object's local position, rotation and/or scale through ModTransform's own setters (the inspector path). Give at least one. Values are clamped: position so the resulting WORLD position is within -1000..1000 per axis, rotation (Euler degrees) to -3600..3600 per axis, scale to 0.001..1000 per axis. The reply returns applied (the values actually stored), clamped (names of properties whose applied value differs from the request) and transform (Unity's local values; Euler angles may be normalised differently). ${EDIT_UNDO_STEP} Undoable. ${EDIT_COMMON}`,
  {
    guid: guidSchema,
    position: vec3("Local position (world position is clamped to -1000..1000 per axis)").optional(),
    rotation: vec3("Local Euler angles in degrees (each axis clamped to -3600..3600)").optional(),
    scale: vec3("Local scale (each axis clamped to 0.001..1000)").optional(),
  },
  { annotations: WRITE_IDEMPOTENT },
);
register(
  "add_component",
  `Adds a component to an object, like the editor's Add Component popup. type is a class name (ModLight) or UI name (Light), case-insensitive; list_component_types (optionally with the object's guid) shows which types are addable and why not. The editor's rules are enforced and violations return INVALID_PARAMS with the editor's message (e.g. one per object for single-instance types; Prefab Instance, Board Space, Item and Text need a childless object and exclude each other; no components on a prefab instance; types the picker never offers such as Transform, Camera, Script are refused). Returns the new component as get_object shows it (including its guid). Undoable, but note: undoing a component add makes the editor push one extra snapshot right after the undo, so the undo reply carries extraUndoEntries and a note; one more undo then re-adds the component. Check extraUndoEntries instead of assuming the component is gone, and verify with get_object. ${EDIT_COMMON}`,
  {
    guid: guidSchema,
    type: z.string().min(1).describe("Component class name (e.g. ModLight) or UI name (e.g. Light), case-insensitive. See list_component_types."),
  },
  { annotations: WRITE },
);
register(
  "remove_component",
  `Removes a component from an object (the inspector's X button). component is the component guid, class name or UI name; a name matching several components is refused (the message lists their guids). Refused (INVALID_PARAMS) where the inspector has no remove button: Transform and other types the picker never offers, and on a prefab instance everything except the Prefab Instance component. For Prefab Instance, Board Space, Item and Text the editor first destroys the object's generated children (reported as childrenDestroyed + childrenNote); undo restores the component but not those children, except a Prefab Instance, which re-instantiates its prefab. Undoable, but undoing a component change may leave the editor's extra snapshot described under the undo tool. ${EDIT_COMMON}`,
  {
    guid: guidSchema,
    component: z.string().min(1).describe("Component guid, class name (ModLight) or UI name (Light), case-insensitive."),
  },
  { annotations: WRITE_DESTRUCTIVE },
);

const VALUE_SHAPES =
  "Value shapes (the same shapes get_object returns, so values round-trip): Boolean true/false; integer types an integer in range (3.0 accepted); Single/Double a finite number; String a string (max 16384 chars); enums a name, case-insensitive (\"Point\"), or a defined integer, flags enums names joined by comma (\"OnSelf, OnTargets\"); Vector2/3/4 and Quaternion {x,y[,z[,w]]} with exactly those keys or an array of 2-4 numbers; Color {r,g,b,a?} floats 0-1 (a defaults to 1); Color32 {r,g,b,a?} integers 0-255; Rect {x,y,width,height}; LayerMask an integer bit mask; " +
  "asset references (Prop, Material, AudioClip, Texture2D, Mesh, Shader, prefab, visual script, ...) null, a guid string or {\"guid\": \"...\"} (a reference object from get_object can be sent back; one with registered:false is refused; guids come from list_assets); " +
  "scene-object references (Transform, GameObject, component types) null, a component guid, or an object guid (then the object's first matching component is used); arrays T[] a JSON array (max 1024 elements; a {truncatedItems} marker is refused); " +
  "AnimationCurve {keys:[{time,value,inTangent?,outTangent?}], preWrapMode?, postWrapMode?} (wrap: Default, Once, Clamp, Loop, PingPong, ClampForever; max 256 keys); " +
  "ParticleSystem.MinMaxCurve a number (Constant) or {mode:\"Constant\",constant} / {mode:\"TwoConstants\",constantMin,constantMax} / {mode:\"Curve\",curveMultiplier?,curve} / {mode:\"TwoCurves\",curveMultiplier?,curveMin,curveMax}; " +
  "ParticleSystem.MinMaxGradient a color {r,g,b,a?} or {mode:\"Color\",color} / {mode:\"TwoColors\",colorMin,colorMax} / {mode:\"Gradient\"|\"RandomColor\",gradient} / {mode:\"TwoGradients\",gradientMin,gradientMax}, a gradient being {mode?:\"Blend\"|\"Fixed\", colorKeys:[{color,time}], alphaKeys:[{alpha,time}]} with 1-8 keys each, times 0-1.";
const PROPERTY_RULES =
  "Property names are those get_object lists for the component (exact name; a unique case-insensitive match is accepted). Values are validated and converted before anything changes: if one fails, nothing changes. Values are written through the property's public setter (side effects run: a light type updates the light, a prop rebuilds its mesh, a prefab instance re-instantiates its prefab). Values may be clamped by setters or the inspector's limits: the reply's applied shows what was actually stored (in get_object's {type, value} shape) and inspectorClamped lists properties clamped by inspector rules, so always check applied. " +
  "NOT settable here (INVALID_PARAMS): triggers and action lists (ModLogicTrigger, ModLogicTrigger[], ModAction; action editing is not available yet), guid and all SerializableGuid properties (set the matching reference property instead, e.g. ItemPrefab rather than ItemPrefabGUID), properties marked hiddenInInspector, ModComplexFloat, ModWeaponInfo, SimpleModHitAction, ModSimpleAnimation[], BoardNodeConnection[], ParticleSystem.Burst[], byte[] (e.g. ModScriptReference.scriptValues) and any other unlisted type; on a prefab instance only the Transform and Prefab Instance components are editable (edit the prefab instead). A setter that throws returns INTERNAL naming the properties already set; one undo reverts them. " +
  VALUE_SHAPES;
register(
  "set_property",
  `Sets one property of one component of an object. ${PROPERTY_RULES} ${EDIT_UNDO_STEP} Undoable. ${EDIT_COMMON}`,
  {
    guid: guidSchema,
    component: z.string().min(1).describe("Component guid, class name (ModLight) or UI name (Light), case-insensitive."),
    property: z.string().min(1).describe("Property name as listed by get_object for that component."),
    value: z
      .any()
      .refine((v) => v !== undefined, { message: "value is required (use null to clear a reference)" })
      .describe("New value, arbitrary JSON in the shape of the property's type (see the tool description); null clears a reference. Required key."),
  },
  { annotations: WRITE_IDEMPOTENT },
);
register(
  "set_properties",
  `Sets several properties of one component of an object in one call (1-64 entries): all are applied in the same frame as ONE undo step, and if any value is invalid nothing changes. ${PROPERTY_RULES} Undoable. ${EDIT_COMMON}`,
  {
    guid: guidSchema,
    component: z.string().min(1).describe("Component guid, class name (ModLight) or UI name (Light), case-insensitive."),
    values: z
      .record(z.string().min(1), z.any())
      .refine((o) => Object.keys(o).length >= 1 && Object.keys(o).length <= 64, { message: "values must have 1-64 entries" })
      .describe("Object {propertyName: value, ...} with 1-64 entries; each value in the shape of that property's type (see the tool description)."),
  },
  { annotations: WRITE_IDEMPOTENT },
);
register(
  "undo",
  `Undoes the last editor step(s) like pressing Ctrl+Z, on the current context's undo stack (the scene's, or the open prefab's). steps is 1-20 (default 1). There is NO redo: an undone edit must be redone by repeating it. Creating an object is not undoable (undo would only re-select the previously selected object); use delete_object to remove it. Selection changes are undo steps too, so one edit can add two entries to undoStackLength, but steps 1 still reverts the edit itself. The undo restores the selection recorded with the undone step. Returns { undone, events, undoStackLength }: events lists the type of each undone step (GameObjectUndoEvent = property/name/active/component/transform edits and selection changes, MoveGameObject = reparent, DeleteGameObject = restores the object, DuplicateGameObject = destroys the clone). undone 0 with note "nothing to undo" means the stack was empty; the bottom entry of a stack is the editor's initial snapshot and undoing it changes nothing but the selection. IMPORTANT after undoing a component add: the reply may include extraUndoEntries and a note, because the editor pushed a new snapshot right after the undo; this is editor behaviour (same with Ctrl+Z) and one more undo re-adds the component, so check extraUndoEntries/note and verify with get_object before undoing again. ${EDIT_COMMON}`,
  { steps: z.number().int().min(1).max(20).optional().describe("Number of undo steps (1-20, default 1); stops early when the stack is empty.") },
  { annotations: WRITE },
);

await server.connect(new StdioServerTransport());
console.error(`[pummelbridge] MCP server ${pkg.version} ready; plugin at 127.0.0.1:${opts.port}`);
