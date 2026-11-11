import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { startFake, makeTempDir, removeDir, writeToken } from "./fake-plugin.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const entry = join(here, "..", "dist", "index.js");

const READ_TOOLS = [
  "bridge_guide", "ping", "get_status", "get_open_mod", "get_hierarchy", "get_object", "get_action_graph",
  "list_assets", "list_component_types", "list_action_types", "list_trigger_types", "get_log", "screenshot", "list_mods",
];
const SIZE_LIMIT = 40000;

async function session({ handler, delayMs, token = "ptoken", env = {}, raw } = {}) {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, token);
  const fake = await startFake({ token, handler, delayMs, raw });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [entry],
    env: { ...process.env, PUMMELBRIDGE_PORT: String(fake.port), PUMMELBRIDGE_TOKEN_FILE: tf, ...env },
    stderr: "pipe",
  });
  let stderr = "";
  transport.stderr?.on("data", (d) => (stderr += d));
  const client = new Client({ name: "test", version: "0.0.0" });
  await client.connect(transport);
  return {
    client,
    fake,
    token,
    stderr: () => stderr,
    close: async () => {
      await client.close();
      await fake.close();
      removeDir(dir);
    },
  };
}

const call = async (client, name, args) => {
  try {
    return await client.callTool({ name, arguments: args });
  } catch (e) {
    return { isError: true, content: [{ type: "text", text: String(e?.message ?? e) }] };
  }
};

test("stdout carries only protocol frames, the list stays small, the process exits when stdin closes", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "rawtoken");
  const child = spawn(process.execPath, [entry], {
    env: { ...process.env, PUMMELBRIDGE_PORT: "1", PUMMELBRIDGE_TOKEN_FILE: tf },
    stdio: ["pipe", "pipe", "pipe"],
  });
  let stderr = "";
  child.stderr.on("data", (d) => (stderr += d));
  const lines = [];
  const rl = createInterface({ input: child.stdout });
  const waiters = [];
  rl.on("line", (l) => {
    lines.push(l);
    for (const w of waiters.splice(0)) w();
  });
  const waitFor = async (pred) => {
    const deadline = Date.now() + 10000;
    for (;;) {
      const found = lines.map((l) => JSON.parse(l)).find(pred);
      if (found) return found;
      if (Date.now() > deadline) throw new Error("timeout waiting for a frame");
      await new Promise((r) => waiters.push(r) && setTimeout(r, 50));
    }
  };
  const send = (o) => child.stdin.write(JSON.stringify(o) + "\n");
  const exited = new Promise((r) => child.on("exit", (code) => r(code)));
  try {
    send({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "raw", version: "0" } } });
    const init = await waitFor((m) => m.id === 1);
    send({ jsonrpc: "2.0", method: "notifications/initialized" });
    send({ jsonrpc: "2.0", id: 2, method: "tools/list" });
    const list = await waitFor((m) => m.id === 2);
    send({ jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "ping", arguments: {} } });
    await waitFor((m) => m.id === 3);

    for (const l of lines) {
      const m = JSON.parse(l); // every stdout line must be JSON
      assert.equal(m.jsonrpc, "2.0", l.slice(0, 80));
    }
    const instr = init.result.instructions;
    assert.ok(instr && instr.length > 1000);
    const toolsChars = JSON.stringify(list.result.tools).length;
    const total = toolsChars + instr.length;
    console.log(`tools/list ${toolsChars} chars + instructions ${instr.length} = ${total} (limit ${SIZE_LIMIT})`);
    assert.ok(total < SIZE_LIMIT, `tool list + instructions are ${total} chars, limit ${SIZE_LIMIT}`);
    assert.ok(!stderr.includes("rawtoken"));

    child.stdin.end();
    const code = await Promise.race([exited, new Promise((r) => setTimeout(() => r("still running"), 5000))]);
    assert.equal(code, 0, "process must exit when stdin closes");
  } finally {
    child.kill();
    removeDir(dir);
  }
});

test("only an explicit allow-list of tools is read-only; every other tool is a write tool", async () => {
  const s = await session();
  try {
    const { tools } = await s.client.listTools();
    for (const t of tools) {
      if (READ_TOOLS.includes(t.name)) assert.equal(t.annotations.readOnlyHint, true, t.name);
      else {
        assert.equal(t.annotations.readOnlyHint, false, t.name);
        assert.match(t.description, /bridge_guide/, `${t.name} should point at bridge_guide`);
      }
      assert.equal(t.inputSchema.type, "object", t.name);
    }
    assert.equal(tools.filter((t) => t.annotations.readOnlyHint).length, READ_TOOLS.length);
    // wrong claims removed: tools that work without a loaded mod do not say they need one
    const by = Object.fromEntries(tools.map((t) => [t.name, t]));
    for (const n of ["get_status", "get_log", "screenshot", "ping"]) assert.doesNotMatch(by[n].description, /mod loaded/i, n);
    assert.match(by.list_mods.description, /EDITOR_NOT_OPEN/);
    // documented defaults and limits
    assert.equal(by.screenshot.inputSchema.properties.maxWidth.maximum, 7680);
    assert.equal(by.get_hierarchy.inputSchema.properties.maxDepth.maximum, 64);
    assert.match(by.screenshot.description + by.screenshot.inputSchema.properties.maxWidth.description, /1280/);
    assert.match(by.get_log.inputSchema.properties.max.description, /200/);
    assert.match(by.get_hierarchy.inputSchema.properties.includeInactive.description, /true/);
    assert.match(by.create_test_mod.inputSchema.properties.type.description, /Minigame/);
    assert.equal(by.get_log.inputSchema.properties.includeEditor.type, "boolean");
    assert.equal(by.create_test_mod.inputSchema.properties.discardUnsaved.type, "boolean");
    assert.equal(by.move_action.annotations.idempotentHint, false);
    assert.equal(by.undo.annotations.destructiveHint, true);
  } finally {
    await s.close();
  }
});

test("unknown arguments are rejected by name and never reach the plugin", async () => {
  const s = await session();
  try {
    const cases = [
      ["create_object", { parent: "abc" }, "parent"],
      ["get_object", { guid: "g", extra: 1 }, "extra"],
      ["get_hierarchy", { maxdepth: 2 }, "maxdepth"],
      ["add_action", { guid: "g", trigger: "OnEnterActions", type: "Wait", parameter: {} }, "parameter"],
      ["get_action_graph", { guid: "g", compnent: "x" }, "compnent"],
      ["add_trigger", { guid: "g", scope: "playerSettings" }, "scope"],
      ["ping", { verbose: true }, "verbose"],
      ["save_mod", { force: true }, "force"],
    ];
    for (const [name, args, key] of cases) {
      const n0 = s.fake.requests.length;
      const r = await call(s.client, name, args);
      assert.equal(r.isError, true, name);
      assert.match(r.content[0].text, new RegExp(key), `${name}: ${r.content[0].text}`);
      assert.equal(s.fake.requests.length, n0, `${name} must not reach the plugin`);
    }
    // the now exposed parameters pass through
    await call(s.client, "get_log", { includeEditor: true, max: 5 });
    assert.deepEqual(JSON.parse(s.fake.requests.at(-1).body).params, { includeEditor: true, max: 5 });
    await call(s.client, "create_test_mod", { name: "PB_TEST_z", discardUnsaved: true });
    assert.deepEqual(JSON.parse(s.fake.requests.at(-1).body).params, { name: "PB_TEST_z", discardUnsaved: true });
  } finally {
    await s.close();
  }
});

test("bridge_guide is served locally and equals the server instructions", async () => {
  const s = await session();
  try {
    const r = await s.client.callTool({ name: "bridge_guide", arguments: {} });
    assert.notEqual(r.isError, true);
    assert.equal(r.content[0].text, s.client.getInstructions());
    assert.match(r.content[0].text, /PB_TEST_/);
    assert.equal(s.fake.requests.length, 0, "bridge_guide must not call the plugin");
  } finally {
    await s.close();
  }
});

test("results are compact JSON; huge results are truncated with a narrowing hint", async () => {
  const s = await session({
    handler: (method) => {
      if (method === "get_hierarchy") return { ok: true, result: { children: "x".repeat(150000) } };
      if (method === "get_log") return { ok: true, result: { entries: ["y".repeat(150000)] } };
      if (method === "list_assets") return { ok: true, result: { assets: ["z".repeat(150000)] } };
      if (method === "list_action_types") return { ok: true, result: { actionTypes: ["w".repeat(150000)] } };
      if (method === "screenshot") return { ok: true, result: { pngBase64: "", width: 1, height: 1 } };
      return { ok: true, result: { a: 1, b: [1, 2], c: { d: null } } };
    },
  });
  try {
    const small = await s.client.callTool({ name: "get_object", arguments: { guid: "g" } });
    assert.equal(small.content[0].text, '{"a":1,"b":[1,2],"c":{"d":null}}');
    const expectations = [
      ["get_hierarchy", {}, /maxDepth.*rootGuid/],
      ["get_log", {}, /\bmax\b/],
      ["list_assets", {}, /limit.*search/],
      ["list_action_types", {}, /address/],
    ];
    for (const [name, args, re] of expectations) {
      const r = await s.client.callTool({ name, arguments: args });
      const text = r.content[0].text;
      assert.ok(text.length <= 100000 + 400, `${name}: ${text.length}`);
      assert.ok(text.length > 100000, name);
      assert.match(text.slice(100000), /truncated/i);
      assert.match(text.slice(100000), re, name);
    }
  } finally {
    await s.close();
  }
});

test("screenshot results are validated before building image content", async () => {
  const bad = [
    { pngBase64: "", width: 1, height: 1 },
    { pngBase64: 5, width: 1, height: 1 },
    { pngBase64: "AAAA", width: "1", height: 1 },
    { pngBase64: "AAAA", width: 1 },
    null,
  ];
  let i = 0;
  const s = await session({ handler: () => ({ ok: true, result: bad[i] }) });
  try {
    for (i = 0; i < bad.length; i++) {
      const r = await s.client.callTool({ name: "screenshot", arguments: {} });
      assert.equal(r.isError, true, JSON.stringify(bad[i]));
      assert.ok(r.content.every((c) => c.type === "text"));
    }
  } finally {
    await s.close();
  }
});

test("the token never appears in tool results, error text or stderr", async () => {
  const token = "supersecrettoken1234";
  const s = await session({ token });
  try {
    const texts = [];
    const grab = (r) => texts.push(JSON.stringify(r));
    grab(await s.client.callTool({ name: "ping", arguments: {} }));
    grab(await s.client.callTool({ name: "get_status", arguments: {} })); // UNKNOWN_METHOD error
    grab(await call(s.client, "get_object", {})); // validation error
    s.fake.token = "someone-else"; // persistent 401 -> Token mismatch
    grab(await s.client.callTool({ name: "ping", arguments: {} }));
    const { tools } = await s.client.listTools();
    grab(tools);
    grab(s.client.getInstructions());
    for (const t of texts) assert.ok(!t.includes(token), "token leaked into a result");
    await new Promise((r) => setTimeout(r, 100));
    assert.ok(!s.stderr().includes(token), "token leaked to stderr");
    assert.match(texts[3], /Token mismatch/);
  } finally {
    await s.close();
  }
});

test("end to end: a short PUMMELBRIDGE_TIMEOUT_MS is observed and a write is sent once", async () => {
  const s = await session({ delayMs: 2000, env: { PUMMELBRIDGE_TIMEOUT_MS: "400" } });
  try {
    const t0 = Date.now();
    const w = await s.client.callTool({ name: "set_object", arguments: { guid: "g", active: true } });
    const dt = Date.now() - t0;
    assert.equal(w.isError, true);
    assert.match(w.content[0].text, /timed out after 0\.4s/);
    assert.match(w.content[0].text, /may or may not have been applied/);
    assert.ok(dt < 1800, `returned after ${dt} ms`);
    assert.equal(s.fake.requests.length, 1, "no retry of a timed-out write");
    const r = await s.client.callTool({ name: "get_object", arguments: { guid: "g" } });
    assert.equal(r.isError, true);
    assert.match(r.content[0].text, /timed out/);
    assert.doesNotMatch(r.content[0].text, /may or may not/);
  } finally {
    await s.close();
  }
});

test("end to end: a redirecting listener is refused", async () => {
  const s = await session({
    raw: (req, res) => {
      res.writeHead(307, { Location: "http://127.0.0.1:1/rpc" });
      res.end();
      return true;
    },
  });
  try {
    const r = await s.client.callTool({ name: "save_mod", arguments: {} });
    assert.equal(r.isError, true);
    assert.match(r.content[0].text, /answered with a redirect; it is not the PummelBridge plugin/);
    assert.doesNotMatch(r.content[0].text, /may or may not/);
  } finally {
    await s.close();
  }
});
