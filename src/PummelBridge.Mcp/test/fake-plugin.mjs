import http from "node:http";
import { mkdtempSync, writeFileSync, rmSync, mkdirSync } from "node:fs";
import { join } from "node:path";

export const TEST_ROOT =
  "C:\\Users\\user\\AppData\\Local\\Temp\\claude\\ac4aea6a351da76a\\5f221009-445e-4d43-8fe4-d07f0c4ec2b6\\scratchpad\\mcp-test";

export function makeTempDir() {
  mkdirSync(TEST_ROOT, { recursive: true });
  return mkdtempSync(join(TEST_ROOT, "t-"));
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
export async function startFake({ token, handler, delayMs = 0 }) {
  const state = { token, requests: [] };
  const server = http.createServer((req, res) => {
    let data = "";
    req.on("data", (c) => (data += c));
    req.on("end", async () => {
      const rec = { method: req.method, url: req.url, headers: req.headers, body: data };
      state.requests.push(rec);
      if (req.headers.origin) {
        res.writeHead(403).end();
        return;
      }
      if (req.headers["x-pummelbridge-token"] !== state.token) {
        res.writeHead(401).end();
        return;
      }
      if (delayMs) await new Promise((r) => setTimeout(r, delayMs));
      const { method, params } = JSON.parse(data);
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
      return { ok: true, result: [{ name: "PB_TEST_a", folderName: "PB_TEST_a", type: "Minigame", isTestMod: true, isOpen: false }] };
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
      // edit tools: echo the received params so tests can check pass-through
      return { ok: true, result: { echoed: params, changed: true, undoable: true, undoStackLength: 1 } };
    default:
      return { ok: false, error: { code: "UNKNOWN_METHOD", message: method } };
  }
}
