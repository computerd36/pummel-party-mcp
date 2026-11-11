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
  "PummelBridge connects to the Pummel Party Mod Editor. Nearly all tools are read-only inspection tools; list_mods is read-only too. Only three tools write: create_test_mod, open_test_mod, save_mod. " +
  "Safety model: write tools only ever act on sandbox mods whose name starts with PB_TEST_; the plugin refuses anything else. The bridge cannot open, modify, rename or delete the author's real mods, and Workshop publishing is blocked entirely while the plugin is loaded (there is no tool for it). " +
  "If a real (non-sandbox) mod is open in the editor, create_test_mod/open_test_mod are refused until the human closes it by hand.";

const server = new McpServer({ name: "pummelbridge", version: pkg.version }, { instructions: INSTRUCTIONS });

const RO =
  "Read-only; never changes anything. Needs Pummel Party running with the PummelBridge plugin and the Mod Editor open with a mod loaded.";
const READ_ONLY = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
const WRITE = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };

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
  { annotations: { ...WRITE, idempotentHint: true } },
);

await server.connect(new StdioServerTransport());
console.error(`[pummelbridge] MCP server ${pkg.version} ready; plugin at 127.0.0.1:${opts.port}`);
