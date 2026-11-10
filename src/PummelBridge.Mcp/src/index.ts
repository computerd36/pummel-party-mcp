#!/usr/bin/env node
import { readFileSync } from "node:fs";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { BridgeClient, BridgeError, optionsFromEnv } from "./client.js";

const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { version: string };
const opts = optionsFromEnv();
const client = new BridgeClient(opts);

const server = new McpServer({ name: "pummelbridge", version: pkg.version });

const RO =
  "Read-only. Needs Pummel Party running with the PummelBridge plugin and the Mod Editor open with a mod loaded.";
const READ_ONLY = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };

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
  extra: { timeoutMs?: number; format?: (r: any) => ToolResult } = {},
): void {
  server.registerTool(
    name,
    { description, inputSchema: shape, annotations: READ_ONLY },
    async (args: Record<string, unknown>): Promise<ToolResult> => {
      try {
        const result = await client.call(name, args ?? {}, extra.timeoutMs);
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
  "Check that the PummelBridge plugin inside Pummel Party is reachable. Returns { pong, pluginVersion, gameVersion, unityVersion, apiVersion }. Use first to verify the connection. Read-only; works whenever the game is running with the plugin loaded (the Mod Editor does not need to be open).",
  {},
);
register(
  "get_status",
  `Returns the current editor/mod status (whether the Mod Editor is open, which mod is loaded, loading state). Use to find out why other tools fail or whether the game is ready. ${RO}`,
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
  `Lists assets available to the open mod (models, textures, audio, prefabs, etc.). Optionally filter by asset type, and choose whether to include the game's built-in assets. Use to find asset names to reference. ${RO}`,
  {
    type: z.string().optional().describe("Asset type filter."),
    includeBuiltIn: z.boolean().optional().describe("Include built-in game assets, not just the mod's own."),
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
    timeoutMs: 30000,
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

await server.connect(new StdioServerTransport());
console.error(`[pummelbridge] MCP server ${pkg.version} ready; plugin at 127.0.0.1:${opts.port}`);
