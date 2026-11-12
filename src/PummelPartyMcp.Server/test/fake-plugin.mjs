import http from "node:http";
import { mkdtempSync, writeFileSync, rmSync, rmdirSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

export const TEST_ROOT = join(tmpdir(), "pummel-party-mcp-test");

// The parent folder is shared by the test files (separate processes): each removes it on exit, but rmdir only
// succeeds when it is empty, so another process's live temp folder is never touched.
process.on("exit", () => {
  try {
    rmdirSync(TEST_ROOT);
  } catch {
    // not empty (another test process is still running) or already gone
  }
});

export function makeTempDir() {
  for (let attempt = 0; ; attempt++) {
    try {
      mkdirSync(TEST_ROOT, { recursive: true });
      return mkdtempSync(join(TEST_ROOT, "t-"));
    } catch (e) {
      if (attempt >= 3 || e.code !== "ENOENT") throw e;
    }
  }
}

export function removeDir(d) {
  rmSync(d, { recursive: true, force: true });
}

export function writeToken(file, token) {
  writeFileSync(file, token + "\r\n");
}

/** Tiny PNG (1x1) as base64. */
export const PNG_B64 =
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

/**
 * Fake plugin. handler(method, params) may return {ok..} body or a promise.
 * Records requests in `requests`.
 */
export async function startFake({ token, handler, delayMs = 0, raw }) {
  const state = { token, requests: [], executions: 0 };
  const server = http.createServer((req, res) => {
    let data = "";
    req.on("data", (c) => (data += c));
    req.on("end", async () => {
      const rec = { method: req.method, url: req.url, headers: req.headers, body: data };
      state.requests.push(rec);
      // raw(req, res, rec, state) may take over the response completely (return true)
      if (raw && raw(req, res, rec, state)) return;
      if (req.headers.origin) {
        res.writeHead(403).end();
        return;
      }
      if (req.headers["x-pummelpartymcp-token"] !== state.token) {
        res.writeHead(401).end();
        return;
      }
      if (delayMs) await new Promise((r) => setTimeout(r, delayMs));
      const { method, params } = JSON.parse(data);
      state.executions++;
      const body = await (handler ?? defaultHandler)(method, params);
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify(body));
    });
  });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  state.port = server.address().port;
  state.close = () => new Promise((r) => { server.closeAllConnections?.(); server.close(r); });
  return state;
}

export function defaultHandler(method, params) {
  switch (method) {
    case "ping":
      return { ok: true, result: { pong: true, pluginVersion: "0.0.1", gameVersion: "g", unityVersion: "u", apiVersion: 2 } };
    case "get_object":
      return { ok: true, result: { guid: params.guid, components: [] } };
    case "screenshot":
      return { ok: true, result: { pngBase64: PNG_B64, width: 1, height: 1 } };
    case "list_mods":
      return { ok: true, result: [{ name: "MCP_a", folderName: "MCP_a", type: "Minigame", isTestMod: true, isOpen: false }] };
    case "create_test_mod":
    case "open_test_mod":
      return { ok: true, result: { name: params.name } };
    case "save_mod":
      return { ok: true, result: { saved: true } };
    case "list_component_types":
      return { ok: true, result: [{ type: "ModTransform", uiName: "Transform", addable: false }] };
    case "create_object":
    case "duplicate_object":
    case "delete_object":
    case "reparent_object":
    case "set_object":
    case "set_transform":
    case "add_component":
    case "remove_component":
    case "set_property":
    case "set_properties":
    case "undo":
    case "add_action":
    case "remove_action":
    case "move_action":
    case "set_action":
    case "add_trigger":
    case "remove_trigger":
    case "set_trigger":
      // edit tools: echo the received params so tests can check pass-through
      return { ok: true, result: { echoed: params, changed: true, undoable: true, undoStackLength: 1 } };
    case "validate_mod":
      return { ok: true, result: { mod: "MCP_a", valid: true, errorCount: 0, issues: [] } };
    case "start_playtest":
      return { ok: true, result: { playing: true, saved: true, echoed: params } };
    case "stop_playtest":
      return { ok: true, result: { stopped: true, alreadyStopped: false, state: "Editing" } };
    case "list_mod_settings":
    case "get_mod_settings":
    case "set_mod_settings":
      return { ok: true, result: { echoed: params } };
    case "capture_preview_image":
      return { ok: true, result: { assignedTo: "details.previewImage" } };
    case "get_asset":
    case "list_asset_rules":
    case "import_asset":
    case "set_import_settings":
    case "create_material":
    case "set_material":
    case "remove_asset":
    case "create_prefab":
    case "open_prefab":
    case "close_prefab":
      return { ok: true, result: { echoed: params, undoable: false } };
    case "run_batch":
      return { ok: true, result: { completed: params.steps.length, total: params.steps.length, echoed: params } };
    case "list_action_types":
    case "list_trigger_types":
    case "get_action_graph":
      return { ok: true, result: { echoed: params } };
    default:
      return { ok: false, error: { code: "UNKNOWN_METHOD", message: method } };
  }
}
