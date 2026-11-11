#!/usr/bin/env node
import { readFileSync } from "node:fs";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { BridgeClient, BridgeError, optionsFromEnv, TOOL_TIMEOUTS_MS, isValidTestModName } from "./client.js";

const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { version: string };
const opts = optionsFromEnv();
const client = new BridgeClient(opts);

// ---------------------------------------------------------------------------
// Shared guide. Served as the server `instructions` AND by the local bridge_guide tool
// (not every MCP client shows server instructions to the model). Tool descriptions only
// carry what is specific to the tool.
// ---------------------------------------------------------------------------

const PLAYER_TRIGGERS = "weaponHitTrigger, tickTrigger, onDeathTrigger, onRespawnTrigger, onFirstSpawnTrigger, onRoundEndTrigger";

const GUIDE = [
  "PummelBridge drives the Pummel Party Mod Editor through a plugin inside the running game.",

  "SAFETY: write tools act only on sandbox mods named PB_TEST_ (^PB_TEST_[A-Za-z0-9_ -]+$, max 64 chars, no leading/trailing space); the plugin refuses anything else (GUARD_NOT_TEST_MOD). Real mods can never be opened, changed, renamed or deleted, and Workshop publishing is blocked and has no tool. create_test_mod/open_test_mod are refused while a real mod is open (the human closes it by hand). Writes also need Safety.EnableWrites in the plugin config (get_status: writesEnabled, publishBlock.complete).",

  "WORKFLOW: get_status -> list_mods -> open_test_mod or create_test_mod -> get_hierarchy (guids) -> edits (create_object, set_transform, add_component, set_property/set_properties, ...) -> get_object to verify -> save_mod. Edits stay in memory until save_mod.",
  "ACTIONS: create_object kind trigger/logic; for a Trigger component set_property TriggerOnEnter (or TriggerOnExit/TriggerOnStay/TriggerOnHit) true to make its slot editable -> get_action_graph -> list_action_types with that trigger address -> add_action (check clamped and the returned action) -> get_action_graph to verify -> save_mod. Logic components hold a user-editable trigger list (add_trigger, set_trigger, remove_trigger; addresses Triggers[0], ...); other components have fixed slots. set_property cannot set triggers or actions.",

  "EDIT RULES: edit tools need the editor in Editing state and return INVALID_STATE while an editor dialog is open, the left mouse button is held in the game window, or play mode is active (wait or tell the human, then retry). Every edit selects the edited object and leaves it selected. Replies carry changed, undoable, undoStackLength (plus undoPending/note). One call = one undo step. undo has no redo; creating an object is not undoable (use delete_object). Undoing a component add makes the editor push an extra snapshot (extraUndoEntries + note): one more undo re-adds the component, so check before undoing again. Player-settings trigger edits (scope playerSettings) are NOT undoable: applied at once, written by save_mod.",

  "CLAMPING: values may be clamped. Transform: position so the world position stays within -1000..1000, rotation -3600..3600, scale 0.001..1000 per axis. Setter/inspector limits too (Wait m_seconds 0..3600, Play Sound m_volume 0..1, Timer m_interval.min >= 0.1). Always check applied / clamped / inspectorClamped in the reply.",

  `ADDRESSES: objects by guid (ModTransform guid from get_hierarchy/get_object). Components by component guid, class name (ModLight) or UI name (Light), case-insensitive; a name matching several components is refused (the message lists their guids). Trigger address: {guid, component?, trigger, path?} for an object, or {scope:"playerSettings", trigger, path?} (Minigame mods only; no guid/component). trigger is the triggerName from get_action_graph (OnEnterActions, OnUseTrigger, WeaponHitTrigger, Triggers[0], ...); player-settings names: ${PLAYER_TRIGGERS}. component is only needed when several components have a trigger of that name. path (max 8 steps {action, choice}) enters nested Display Popup choice lists: each step names a Display Popup action in the current list and a 0-based choice whose On Select list becomes the one edited. Some fixed slots are editable only while a component flag is on (OnEnterActions needs TriggerOnEnter, OnExitActions TriggerOnExit); get_action_graph reports editable/reason and the action tools refuse non-editable slots (never editable on prefab instances).`,

  "PROPERTY VALUES (set_property/set_properties; the shapes get_object returns): Boolean; integers (3.0 ok); Single/Double finite number; String (max 16384 chars); enum name (case-insensitive) or defined integer, flags names joined by comma (\"OnSelf, OnTargets\"); Vector2/3/4, Quaternion {x,y[,z[,w]]} or array of 2-4 numbers; Color {r,g,b,a?} 0-1; Color32 0-255 ints; Rect {x,y,width,height}; LayerMask integer; asset references null, a guid or {guid} (guids from list_assets; registered:false refused); scene-object references null, a component guid, or an object guid (first matching component); arrays a JSON array (max 1024); AnimationCurve {keys:[{time,value,inTangent?,outTangent?}], preWrapMode?, postWrapMode?} (max 256 keys); ParticleSystem.MinMaxCurve a number or {mode:Constant|TwoConstants|Curve|TwoCurves, constant|constantMin+constantMax|curve|curveMin+curveMax, curveMultiplier?}; MinMaxGradient a color or {mode:Color|TwoColors|Gradient|RandomColor|TwoGradients, color|colorMin+colorMax|gradient|gradientMin+gradientMax}, gradient {mode?:Blend|Fixed, colorKeys:[{color,time}], alphaKeys:[{alpha,time}]} (1-8 keys each, times 0-1).",
  "PROPERTY RULES: names as get_object lists them (unique case-insensitive match ok). Values are validated first: if one fails nothing changes. Setters run (side effects). NOT settable (INVALID_PARAMS): triggers/action lists (use the action tools), guid and SerializableGuid properties (set the matching reference instead), hiddenInInspector properties, ModComplexFloat, ModWeaponInfo, SimpleModHitAction, ModSimpleAnimation[], BoardNodeConnection[], ParticleSystem.Burst[], byte[]; on a prefab instance only Transform and Prefab Instance are editable. A throwing setter returns INTERNAL naming the properties already set; one undo reverts them.",

  "ACTION PARAMETERS (add_action, set_action, add_trigger, set_trigger): {name: value}, max 64; names are the fields get_action_graph reports (m_seconds), alias without m_ accepted; duplicates refused; unknown names, wrong types, bad enum values or references are refused and nothing changes. Shapes as PROPERTY VALUES plus: ModComplexFloat/Int (m_seconds, m_interval) a number (= Constant) or {valueType?:Constant|RandomBetweenTwoValues, min?, max?} (given keys only); ModAssetReference<T> null, guid or {guid}; reference arrays an array (replaces the list); DamageInstance an object with any of damage, ragdoll, ragdollVel, hitAnim, removeKeys, blood, bloodVel, bloodAmount, sound, volume; Display Popup m_choices an array patch (element i updates choice i, null = unchanged; keys m_buttonText, m_costType, m_costAmount, m_itemCostType, m_choiceIconReference; at most m_choiceCount elements). list_action_types gives names, types, enum values, defaults, limits.",
  "TARGETS: target actions (not e.g. Wait, Set Gravity) have targetFlags (Source, Receiver, Advanced) and, with Advanced, an explicit targets list. targetFlags: names joined by comma (\"Source, Advanced\"), an array of names, an integer 0-7 (Source=1, Receiver=2, Advanced=4), or \"0\"/\"\"/\"None\". Flags whose toggle is hidden at the call site or for the action are refused; with two hidden toggles the flags cannot change (callSite in get_action_graph; availableTargetFlags, targetFlagsLocked, appendTargetFlags, targetType in list_action_types). targets: array of references (object guid, component guid or {guid}; null = empty entry), max 1024, replaced as a whole, only while the resulting flags include Advanced; each must resolve to the action's targetType and be a registered object (scene triggers: scene objects only; prefab triggers: the same prefab). Default call-site flags apply on APPEND only; an insert keeps the action's own default.",
].join("\n\n");

const server = new McpServer({ name: "pummelbridge", version: pkg.version }, { instructions: GUIDE });

// For read-only tools the destructive/idempotent hints are meaningless per the MCP spec, so they are omitted.
const READ_ONLY = { readOnlyHint: true, openWorldHint: false } as const;
const WRITE = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const WRITE_IDEMPOTENT = { ...WRITE, idempotentHint: true };
const WRITE_DESTRUCTIVE = { ...WRITE, destructiveHint: true };
type Annotations = { readOnlyHint: boolean; destructiveHint?: boolean; idempotentHint?: boolean; openWorldHint: boolean };

const MAX_TEXT_CHARS = 100_000;
const RULES = " Rules: see bridge_guide.";

type ToolResult = { content: Array<any>; isError?: boolean };

function errorResult(e: unknown): ToolResult {
  const text =
    e instanceof BridgeError
      ? e.message
      : `Internal error in PummelBridge MCP server: ${(e as Error)?.message ?? String(e)}`;
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
  server.registerTool(
    name,
    { description, inputSchema: schema as any, annotations },
    async (args: Record<string, unknown>): Promise<ToolResult> => {
      try {
        const result = await client.call(name, args ?? {}, TOOL_TIMEOUTS_MS[name], { write });
        if (extra.format) return extra.format(result);
        return textResult(result, extra.narrow ?? "use the tool's filter/limit parameters");
      } catch (e) {
        console.error(`[pummelbridge] ${name} failed: ${(e as Error)?.message}`);
        return errorResult(e);
      }
    },
  );
}

// ---------------------------------------------------------------------------
// Trigger address and action schemas. A top-level z.union would be published without
// "type": "object" (clients reject that), so the address is one flat strict object with
// optional fields plus a refine() enforcing the two forms before anything reaches the plugin.
// ---------------------------------------------------------------------------

const pathStepSchema = z
  .object({
    action: z.number().int().min(0),
    choice: z.number().int().min(0),
  })
  .strict();

const addressFields = {
  guid: z.string().min(1).optional(),
  component: z.string().min(1).optional(),
  scope: z.enum(["object", "playerSettings"]).optional(),
  trigger: z.string().min(1).optional().describe("triggerName"),
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
const targetFlagsSchema = z
  .union([z.string(), z.array(z.string()), z.number().int().min(0).max(7)])
  ;
const targetsSchema = z.array(z.any()).max(1024);

const guidSchema = z.string().min(1);
const compSchema = z.string().min(1).describe("Component guid, class or UI name.");

// ---------------------------------------------------------------------------
// Local tool (does not call the plugin)
// ---------------------------------------------------------------------------

server.registerTool(
  "bridge_guide",
  {
    description:
      "Returns the shared PummelBridge guide: safety model, workflow, edit/undo/selection rules, clamping, address forms, property and parameter value shapes, target flags. Local; does not call the game. Read it before using write tools.",
    inputSchema: z.object({}).strict() as any,
    annotations: READ_ONLY,
  },
  async (): Promise<ToolResult> => ({ content: [{ type: "text", text: GUIDE }] }),
);

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
  "Editor/mod status: editorOpen, editorState, modOpen, modName, hasUnsavedEdits, undoStackLength, prefabContext, playMode, writesEnabled, publishBlock. Never fails because of editor state; use it to find out why other tools fail.",
  {},
  READ_ONLY,
);
register(
  "get_open_mod",
  "Returns the mod open in the editor: name, type, metadata, settings. Needs an open mod (EDITOR_NOT_OPEN / NO_MOD_OPEN otherwise).",
  {},
  READ_ONLY,
);
register(
  "get_hierarchy",
  "Returns the object tree of the open mod with each object's guid, name, components and children. Guids for other tools come from here. Needs an open mod.",
  {
    rootGuid: z.string().min(1).optional().describe("Return only this object's subtree (default: whole scene or open prefab)."),
    maxDepth: z.number().int().min(0).max(64).optional().describe("Levels of children to return, 0..64 (default 64); deeper nodes show depthLimited."),
    includeInactive: z.boolean().optional().describe("Include inactive objects (default true)."),
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
  "Returns the triggers and actions of an object (guid, optional component filter) or of the player settings (scope playerSettings): one entry per trigger slot { component, triggerName, triggerType, triggerParameters, actions: [{ index, type, targetFlags, targets, parameters }], callSite, editable (+ reason) }. triggerName is the trigger address for the action tools; parameter keys are the names add_action/set_action take. Needs an open mod.",
  z
    .object({
      guid: addressFields.guid,
      component: z.string().min(1).optional().describe("Only triggers of this component (class name, UI name or guid). Not with scope playerSettings."),
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
  "Lists assets of the open mod, paged: { total, hasMore, typeCounts, assets }. Built-in assets (includeBuiltIn) exceed 16,000: use search/type with limit/offset. typeCounts ignores the type filter. Needs an open mod.",
  {
    type: z.string().optional().describe("Asset type, case-insensitive exact (Prop, Material, Texture2D, AudioClip, Shader, Prefab...)."),
    includeBuiltIn: z.boolean().optional().describe("Include built-in game assets (default false)."),
    search: z.string().optional().describe("Case-insensitive name substring; whitespace-separated terms must all match."),
    limit: z.number().int().min(1).max(500).optional().describe("Page size 1..500 (default 100)."),
    offset: z.number().int().min(0).optional().describe("Matches to skip (default 0)."),
  },
  READ_ONLY,
  { narrow: "use a smaller limit, plus search and/or type" },
);
register(
  "get_log",
  "Returns recent game/Unity log entries { entries, nextId, dropped, sessionId }; pass the previous nextId as sinceId to poll. Works with the editor closed and while the game is busy.",
  {
    max: z.number().int().min(1).max(2000).optional().describe("Max entries, 1..2000 (default 200)."),
    sinceId: z.number().int().min(0).optional().describe("Only entries newer than this id (nextId of a previous call)."),
    includeEditor: z.boolean().optional().describe("Also return the editor Log window's buffer as editorEntries (needs the game's main thread; default false)."),
  },
  READ_ONLY,
  { narrow: "use a smaller max, or sinceId" },
);
register(
  "screenshot",
  "Captures the game's current view as a PNG (image content). Works with the editor open or closed.",
  { maxWidth: z.number().int().min(64).max(7680).optional().describe("Max width in pixels, 64..7680 (default 1280); downscaled if wider.") },
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
        return errorResult(new BridgeError("INTERNAL: screenshot response lacked a non-empty pngBase64 string or numeric width/height."));
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
  "Lists the editor's mod projects [{ name, folderName, type, isTestMod, isOpen }]; isTestMod marks the PB_TEST_ sandbox mods. Needs the Mod Editor open (EDITOR_NOT_OPEN otherwise); no mod needs to be loaded.",
  {},
  READ_ONLY,
);
register(
  "list_component_types",
  "Lists every registered component type (Transform first): [{ type, uiName, category, addable, reason?, allowsMultiple, supportsChildren, modTypes }]. type or uiName goes to add_component; addable reflects the Add Component picker for the open mod type, and with guid also that object's rules. Needs an open mod (any).",
  { guid: z.string().min(1).optional().describe("Object guid: apply that object's per-object rules too.") },
  READ_ONLY,
);
register(
  "list_action_types",
  "Lists the Action Editor palette: { modType, trigger?, actionTypes: [{ type, className, uiName, category, parameters: [{ name, alias, type, kind, enumValues?, default, min?, max?, writable }], supportsTargets, availableTargetFlags, targetFlagsLocked, appendTargetFlags, targetType, usableHere, reason? }] }. Call FIRST with the trigger address: usableHere/reason then say what the slot accepts (board actions only work in Board Space Event triggers and Display Popup choices) and the target fields are filled in; without an address only the mod-type gate applies. Any address key makes trigger required. Large output: pass an address. Needs an open mod (any).",
  withAddress({}, { triggerRequired: false }),
  READ_ONLY,
  { narrow: "pass a trigger address (guid + trigger, or scope playerSettings + trigger)" },
);
register(
  "list_trigger_types",
  "Lists the trigger types of a Logic component's Event dropdown (None, Start, Enable, Update, Hit, WeaponHit, Timer, BoardSpace, RoundStart, RoundEnd): { modType, triggerTypes: [{ type, className, parameters, sourceTargetAvailable, addable, reason? }] }. Use before add_trigger/set_trigger. Needs an open mod (any).",
  {},
  READ_ONLY,
);

// ---------------------------------------------------------------------------
// Mod lifecycle (write)
// ---------------------------------------------------------------------------

const NAME_RULE = "^PB_TEST_[A-Za-z0-9_ -]+$, max 64 chars, no leading/trailing space.";
const nameSchema = z
  .string()
  .refine(isValidTestModName, { message: `Invalid sandbox mod name. ${NAME_RULE}` })
  .describe(NAME_RULE);
const discardSchema = z.boolean().optional().describe("Allow discarding unsaved changes of the open sandbox mod (default false).");

register(
  "create_test_mod",
  `Creates a blank sandbox mod and opens it (waits up to 45 s). Refused if the name exists (ALREADY_EXISTS), while a real mod is open, or (unless discardUnsaved) while the open sandbox mod has unsaved changes. Needs the Mod Editor open.${RULES}`,
  {
    name: nameSchema,
    type: z.enum(["Minigame", "Board"]).optional().describe('Mod type (default "Minigame").'),
    discardUnsaved: discardSchema,
  },
  WRITE,
);
register(
  "open_test_mod",
  `Opens an existing sandbox mod (waits up to 45 s). Refused while a real mod is open, or (unless discardUnsaved) while the open sandbox mod has unsaved changes; prefer save_mod first. Needs the Mod Editor open.${RULES}`,
  { name: nameSchema, discardUnsaved: discardSchema },
  WRITE,
);
register(
  "save_mod",
  `Saves the open sandbox mod to disk (waits up to 30 s). Only works while a PB_TEST_ sandbox mod is open.${RULES}`,
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
  .regex(/^[^\u0000-\u001f\u007f]+$/, "must not contain control characters");
const finite = z.number();
const vec3 = () =>
  z.union([z.object({ x: finite, y: finite, z: finite }).strict(), z.array(finite).length(3)]);

const CREATE_KINDS = [
  "empty", "prefabInstance", "cube", "sphere", "plane", "prop", "particleSystem", "text", "lineRenderer",
  "playerSpawn", "item", "trigger", "logic", "weaponSource", "boardSpace",
  "directionalLight", "pointLight", "spotLight", "audioSource",
] as const;

register(
  "create_object",
  `Creates an object like the editor's GameObject menu and selects it. kind defaults to "empty" ("prop" if prop is given, "prefabInstance" if prefab is given). playerSpawn/item/trigger/logic/weaponSource: Minigame mods only; boardSpace: Board mods only; prefabInstance and boardSpace: not while a prefab is edited. Parents that cannot have children (prefab instance, board space, item, text) are refused. position/rotation/scale are local, clamped. Not undoable: remove a mistake with delete_object.${RULES}`,
  {
    kind: z.enum(CREATE_KINDS).optional(),
    name: nameFieldSchema.optional(),
    parentGuid: z.string().min(1).nullable().optional().describe("null/omitted = top level."),
    position: vec3().optional(),
    rotation: vec3().optional(),
    scale: vec3().optional(),
    prop: z.string().min(1).optional().describe("Prop asset guid (kind prop)."),
    prefab: z.string().min(1).optional().describe("Prefab asset guid (kind prefabInstance)."),
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
  `Deletes an object and its children (soft delete; undo restores it at the same parent and index). Nothing is selected afterwards. The open prefab's root cannot be deleted. References or actions pointing at the object may break.${RULES}`,
  { guid: guidSchema },
  WRITE_DESTRUCTIVE,
);
register(
  "reparent_object",
  `Moves an object under a new parent (drag and drop); world transform is kept. Refused (INVALID_PARAMS): into itself or a descendant, a parent that cannot have children, a parent in another context (scene vs prefab), the open prefab's root, siblingIndex out of range. No change gives changed false.${RULES}`,
  {
    guid: guidSchema,
    newParentGuid: z.string().min(1).nullable().describe("Required; null = top level."),
    siblingIndex: z.number().int().min(0).optional().describe("Default: last."),
  },
  WRITE,
);
register(
  "set_object",
  `Sets an object's name and/or active flag (at least one).${RULES}`,
  { guid: guidSchema, name: nameFieldSchema.optional(), active: z.boolean().optional().describe("Active checkbox.") },
  WRITE_IDEMPOTENT,
);
register(
  "set_transform",
  `Sets local position, rotation and/or scale (at least one). Values are clamped; the reply gives applied, clamped and transform (Euler angles may be normalised).${RULES}`,
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
  `Adds a component like the Add Component popup; list_component_types (with the object's guid) shows addable types and why not. Editor rules apply (INVALID_PARAMS with its message): single-instance types; Prefab Instance, Board Space, Item and Text need a childless object and exclude each other; nothing on a prefab instance; Transform, Camera, Script are never offered. Returns the new component as get_object shows it.${RULES}`,
  { guid: guidSchema, type: z.string().min(1).describe("Class name (ModLight) or UI name (Light).") },
  WRITE,
);
register(
  "remove_component",
  `Removes a component (the inspector's X). Refused for Transform and other never-offered types, and on a prefab instance for all but Prefab Instance. Removing Prefab Instance, Board Space, Item or Text first destroys its generated children (childrenDestroyed); undo restores the component but not those children (a Prefab Instance re-instantiates).${RULES}`,
  { guid: guidSchema, component: compSchema },
  WRITE_DESTRUCTIVE,
);
register(
  "set_property",
  `Sets one property of one component. Value shapes and the list of non-settable properties are in bridge_guide (PROPERTY VALUES/RULES). The reply's applied shows what was stored; inspectorClamped lists inspector-clamped properties.${RULES}`,
  {
    guid: guidSchema,
    component: compSchema,
    property: z.string().min(1),
    value: z
      .any()
      .refine((v) => v !== undefined, { message: "value is required (use null to clear a reference)" })
      .describe("Required; null clears a reference."),
  },
  WRITE_IDEMPOTENT,
);
register(
  "set_properties",
  `Sets 1-64 properties of one component in one call: one undo step, and if any value is invalid nothing changes. Value shapes and non-settable properties: bridge_guide (PROPERTY VALUES/RULES).${RULES}`,
  {
    guid: guidSchema,
    component: compSchema,
    values: z
      .record(z.string().min(1), z.any())
      .refine((o) => Object.keys(o).length >= 1 && Object.keys(o).length <= 64, { message: "values must have 1-64 entries" })
      .describe("{propertyName: value}, 1-64 entries."),
  },
  WRITE_IDEMPOTENT,
);
register(
  "undo",
  `Undoes the last editor step(s) like Ctrl+Z on the current context's stack (scene or open prefab); no redo. One edit can add two stack entries (selection is a step), but steps 1 still reverts the edit; the selection recorded with the step is restored. Returns { undone, events, undoStackLength }; events: GameObjectUndoEvent (property/name/active/component/transform/selection), MoveGameObject, DeleteGameObject (restored), DuplicateGameObject (clone destroyed). undone 0 + note = empty stack (bottom entry is the initial snapshot).${RULES}`,
  { steps: z.number().int().min(1).max(20).optional().describe("Steps to undo, 1-20 (default 1); stops early on an empty stack.") },
  WRITE_DESTRUCTIVE,
);

// ---------------------------------------------------------------------------
// Action / trigger editing (write)
// ---------------------------------------------------------------------------

register(
  "add_action",
  `Adds an action to a trigger's list. type: ModActionType name (ChangeHealth), class or UI name; call list_action_types with the same address first. index omitted or = count appends (call-site default target flags apply); 0..count-1 inserts before that action (no defaults). Applied in order: constructor defaults, call-site flags (append only), parameters, targetFlags, targets. Refused (INVALID_PARAMS): unknown type, action the call site does not offer, index > count.${RULES}`,
  withAddress(
    {
      type: z.string().min(1),
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
  `Appends a trigger to an object's Logic component trigger list (no insert/reorder). type: ModTriggerType name (Start, Enable, Update, Hit, WeaponHit, Timer, RoundStart, RoundEnd) or class name, default Start; must be addable (list_trigger_types). parameters: Hit/WeaponHit m_hitType flags, Timer m_interval (min >= 0.1; see clamped). Only guid and component address it. NOT_FOUND without a Logic component; INVALID_PARAMS on a prefab instance or non-addable type. Returns the new trigger as a get_action_graph entry; its triggerName Triggers[n] is the address for add_action.${RULES}`,
  z
    .object({
      guid: z.string().min(1),
      component: z.string().min(1).optional(),
      type: z.string().min(1).optional().describe('Default "Start".'),
      parameters: parametersSchema.optional(),
    })
    .strict(),
  WRITE,
);
register(
  "remove_trigger",
  `Removes the trigger Triggers[i] from a Logic component's list (no path); later entries move down by one (indexNote), so re-read get_action_graph. Fixed slots and player-settings triggers cannot be removed (INVALID_PARAMS): clear them with remove_action. Address: bridge_guide.${RULES}`,
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
  `Changes a trigger's type and/or parameters (at least one). type: Logic list entries (Triggers[i]) and addable types only; a different type replaces the trigger, KEEPS its actions and RESETS parameters to the new type's defaults (given parameters are then applied). parameters: Hit/WeaponHit m_hitType (Logic triggers, a Hittable's OnHitActions, weaponHitTrigger), Timer m_interval (Logic Timer, tickTrigger; min >= 0.1). Other slots (Trigger, Item, Weapon, popup choices, onDeath/onRespawn/onFirstSpawn/onRoundEnd) have none.${RULES}`,
  withAddress(
    {
      type: z.string().min(1).optional(),
      parameters: parametersSchema.optional(),
    },
    {
      triggerRequired: true,
      check: (o) => (o.type === undefined && o.parameters === undefined ? "give at least one of type, parameters" : undefined),
    },
  ),
  WRITE_IDEMPOTENT,
);

await server.connect(new StdioServerTransport());
// Exit when the MCP client closes stdin (the SDK transport does not do this itself).
process.stdin.on("end", () => process.exit(0));
process.stdin.on("close", () => process.exit(0));
console.error(`[pummelbridge] MCP server ${pkg.version} ready; plugin at 127.0.0.1:${opts.port}`);
