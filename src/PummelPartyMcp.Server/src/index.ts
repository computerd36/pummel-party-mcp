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

const GUIDE = [
  "pummel-party-mcp drives the Pummel Party Mod Editor through a plugin inside the running game.",

  "SAFETY: write tools act only on sandbox mods named MCP_ (^MCP_[A-Za-z0-9_ -]+$, max 64 chars, no leading/trailing space); anything else is refused (GUARD_NOT_TEST_MOD). Real mods can never be changed; Workshop publishing is blocked and has no tool. create_test_mod/open_test_mod are refused while a real mod is open (the human closes it). Writes need Safety.EnableWrites and a tested or accepted game version (get_status: writesEnabled, publishBlock, gameVersionCheck).",

  "WORKFLOW: get_status -> list_mods -> open_test_mod or create_test_mod -> get_hierarchy (guids) -> edits (create_object, set_transform, add_component, set_property, ...; many at once: run_batch) -> get_object to verify -> validate_mod -> save_mod -> start_playtest (aiOnly true runs unattended) -> get_playtest_state / get_playtest_events -> stop_playtest. Edits stay in memory until save_mod.",
  "ACTIONS: create_object kind trigger/logic; for a Trigger component set_property TriggerOnEnter (or TriggerOnExit/TriggerOnStay/TriggerOnHit) true to make its slot editable -> get_action_graph -> list_action_types with that trigger address -> add_action -> get_action_graph to verify -> save_mod. Logic components hold a trigger list (add_trigger, set_trigger, remove_trigger; Triggers[0], ...); other components have fixed slots. set_property cannot set triggers or actions.",

  "EDIT RULES: edit tools need the Editing state and return INVALID_STATE while an editor dialog is open, the left mouse button is held in the game window, or play mode is active (stop_playtest, or wait / tell the human, then retry). Every edit selects the edited object. Replies carry changed, undoable, undoStackLength (plus undoPending/note). One call = one undo step. undo has no redo; creating an object is not undoable (use delete_object). Undoing a component add pushes an extra snapshot (extraUndoEntries): one more undo re-adds it. Player-settings trigger edits (scope playerSettings) are NOT undoable: written by save_mod.",

  "PLAYTEST: validate_mod first. start_playtest SAVES the open sandbox mod, applies players/aiOnly, presses Play, returns in PlayMode (limit 60 s); INVALID_STATE on validator errors (error.data.validation; validateFirst false = Run With Errors), an open prefab or dialog, or a BoardSpace mod. aiOnly true: AIs play, rounds run by themselves; otherwise a human plays player 1 (no input is sent). VERIFY, never assume a trigger or action worked: get_playtest_state (running only: players alive/health/score/position/deaths, minigame state/round/time, useHealth + note) and get_playtest_events (ring of 500: player_died/respawned, health_changed, score_changed, round_started/ended, minigame_finished, trigger_fired, action_executed; page with sinceId; cleared when the next playtest starts, so stop_playtest first and read afterwards works). Also get_status.playtest, get_log, screenshot. HEALTH: with useHealth off (default) any Change Health leaving 0 or less kills at once, whatever the amount; hit points need Use Health in the Minigame Settings (by hand). During play these also work: ping, get_status, get_log, screenshot, list_mods, list_component_types, get_hierarchy (EDIT-TIME scene), list_assets/list_trigger_types/list_action_types without address. INVALID_STATE: get_object, get_action_graph, list_action_types with address, get_open_mod, validate_mod and all edit/lifecycle tools incl. save_mod, undo, run_batch. stop_playtest before editing. After a start TIMEOUT: get_status, then stop_playtest; do not retry blindly.",
  "MOD ID: publishedFileId \"256\" means never published (the game's local id for unpublished mods), not a Workshop item or a minigame id.",
  "MINIMAL VALID MINIGAME: NumberOfRounds > 0 (default 1) plus at least one ACTIVE Player Spawn (create_object kind playerSpawn; defaults cover all counts, players and respawn) = valid true. Warnings never block (empty description, no preview image, no collider below a spawn: cube/plane/prop have one). Board mods cannot be made valid via the plugin.",
  "BATCH: run_batch runs 1-100 steps {tool, params?, id?} in order in one write-queue turn (limit 120 s); each write step has its own guard, commit and undo step. Allowed: the edit tools, undo, save_mod and the get_/list_ read tools in its schema (not lifecycle, playtest, screenshot, ping, get_status, get_log, get_open_mod, validate_mod). NO ROLLBACK (object creation is not undoable): stopOnError (default true) stops at the first failed step and results lists what was applied; revert with delete_object or undo per step; false = attempt all; nothing is saved without a save_mod step. Step failures are in results ({ok, result|error}), not a call error; the call fails only on up-front validation (INVALID_PARAMS naming the step, nothing run) or a guard. id [A-Za-z][A-Za-z0-9_-]{0,63}, unique. PLACEHOLDERS: a string VALUE (any depth, not keys) in params starting with $ is a reference $<id>(.name|[n]){1,8} into an EARLIER step's result (no forward/self refs), replaced by that JSON value of any type before the step runs: \"$floor.guid\", \"$dup.objects[0].guid\". \"$$5\" = literal \"$5\"; any other $ string is refused up front; a reference to a failed step or missing path fails that step.",

  "CLAMPING: values may be clamped. Transform: world position within -1000..1000, rotation -3600..3600, scale 0.001..1000 per axis. Setter/inspector limits too (Wait m_seconds 0..3600, m_volume 0..1, Timer m_interval.min >= 0.1). See applied / clamped / inspectorClamped. set_property follows the inspector: never-drawn, conditional (spotAngle only while type is Spot; checked after the call's other values) and omitted choices are refused (get_object: shown, inspector).",

  `ADDRESSES: objects by guid (ModTransform guid from get_hierarchy/get_object). Components by guid, class name (ModLight) or UI name (Light), case-insensitive. Trigger address: {guid, component?, trigger, path?}, or {scope:"playerSettings", trigger, path?} (Minigame only; no guid/component). trigger = the triggerName from get_action_graph (OnEnterActions, OnUseTrigger, Triggers[0], ...); player-settings names: ${PLAYER_TRIGGERS}. component only when several components have a trigger of that name. path (max 8 steps {action, choice}) enters nested Display Popup lists: each step = a Display Popup action in the current list + a 0-based choice whose On Select list becomes the edited one. Some slots are editable only while a component flag is on; get_action_graph reports editable/reason and the action tools refuse non-editable slots.`,

  "PROPERTY VALUES (set_property/set_properties; the shapes get_object returns): Boolean; integers (3.0 ok); Single/Double finite number; String (max 16384); enum name (case-insensitive) or defined integer, flags joined by comma (\"OnSelf, OnTargets\"); Vector2/3/4, Quaternion {x,y[,z[,w]]} or array of 2-4 numbers; Color {r,g,b,a?} 0-1; Color32 0-255 ints; Rect {x,y,width,height}; LayerMask integer; asset references null, a guid or {guid} (from list_assets; registered:false refused); scene-object references null, a component guid or an object guid; arrays a JSON array (max 1024); AnimationCurve {keys:[{time,value,inTangent?,outTangent?}]} (max 256); MinMaxCurve a number or {mode:Constant|TwoConstants|Curve|TwoCurves, constant|constantMin+constantMax|curve|curveMin+curveMax, curveMultiplier?}; MinMaxGradient a color or {mode:Color|TwoColors|Gradient|RandomColor|TwoGradients, color|colorMin+colorMax|gradient|gradientMin+gradientMax}, gradient {mode?, colorKeys:[{color,time}], alphaKeys:[{alpha,time}]}.",
  "PROPERTY RULES: names as get_object lists them (unique case-insensitive match ok). Values are validated first: if one fails nothing changes. NOT settable (INVALID_PARAMS): triggers/action lists (use the action tools), guid and hiddenInInspector properties, ModComplexFloat, ModWeaponInfo, SimpleModHitAction, ModSimpleAnimation[], BoardNodeConnection[], Burst[], byte[]; on a prefab instance only Transform and Prefab Instance. A throwing setter returns INTERNAL naming the properties already set; one undo reverts them.",

  "ACTION PARAMETERS (add_action, set_action, add_trigger, set_trigger): {name: value}, max 64; names are the fields get_action_graph reports (m_seconds), alias without m_ accepted; unknown names, wrong types, bad enums or references are refused and nothing changes. Shapes as PROPERTY VALUES plus: ModComplexFloat/Int a number (= Constant) or {valueType?:Constant|RandomBetweenTwoValues, min?, max?}; ModAssetReference<T> null, guid or {guid}; reference arrays replace the list; DamageInstance an object with any of damage, ragdoll, ragdollVel, hitAnim, removeKeys, blood, bloodVel, bloodAmount, sound, volume; Display Popup m_choices an array patch (element i updates choice i, null = unchanged; keys m_buttonText, m_costType, m_costAmount, m_itemCostType, m_choiceIconReference).",
  "TARGETS: target actions (not e.g. Wait) have targetFlags (Source, Receiver, Advanced) and, with Advanced, a targets list. targetFlags: names joined by comma (\"Source, Advanced\"), an array of names, an integer 0-7 (Source=1, Receiver=2, Advanced=4), or \"0\"/\"\"/\"None\". Flags whose toggle is hidden at the call site or for the action are refused (see callSite in get_action_graph; availableTargetFlags, targetFlagsLocked, appendTargetFlags, targetType in list_action_types). targets: references (object guid, component guid or {guid}; null = empty entry), max 1024, replaced as a whole, only with Advanced in the flags; each must resolve to the action's targetType and be a registered object (scene triggers: scene objects; prefab triggers: the same prefab). Default call-site flags apply on APPEND only.",
].join("\n\n");

const server = new McpServer({ name: "pummel-party-mcp", version: pkg.version }, { instructions: GUIDE });

// For read-only tools the destructive/idempotent hints are meaningless per the MCP spec, so they are omitted.
const READ_ONLY = { readOnlyHint: true, openWorldHint: false } as const;
const WRITE = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const WRITE_IDEMPOTENT = { ...WRITE, idempotentHint: true };
const WRITE_DESTRUCTIVE = { ...WRITE, destructiveHint: true };
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
      "Returns the shared pummel-party-mcp guide (safety, workflow, playtest, batch, edit rules, address forms, value shapes). Local; does not call the game. Read it before using write tools.",
    inputSchema: z.object({}).strict() as any,
    annotations: READ_ONLY,
  },
  async (): Promise<ToolResult> => ({ content: [{ type: "text", text: GUIDE }] }),
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
    type: z.string().optional().describe("Asset type, case-insensitive (Prop, Material, Texture2D, AudioClip, Shader, Prefab...)."),
    includeBuiltIn: z.boolean().optional().describe("Default false."),
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
  "undo", "save_mod",
  "get_object", "get_hierarchy", "get_action_graph", "list_mods", "list_assets", "list_component_types",
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
