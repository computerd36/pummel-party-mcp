import { test } from "node:test";
import assert from "node:assert/strict";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { readFileSync } from "node:fs";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { PluginClient } from "../dist/client.js";
import { startFake, makeTempDir, removeDir, writeToken, defaultHandler } from "./fake-plugin.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const entry = join(here, "..", "dist", "index.js");
const pkgVersion = JSON.parse(readFileSync(join(here, "..", "package.json"), "utf8")).version;
const MAX = 100000;

async function session({ handler, raw, ownPing = false } = {}) {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "ltoken");
  const fake = await startFake({ token: "ltoken", handler, raw, ownPing });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [entry],
    env: { ...process.env, PUMMEL_PARTY_MCP_PORT: String(fake.port), PUMMEL_PARTY_MCP_TOKEN_FILE: tf },
    stderr: "pipe",
  });
  const client = new Client({ name: "test", version: "0.0.0" });
  await client.connect(transport);
  return {
    client,
    fake,
    close: async () => {
      await client.close();
      await fake.close();
      removeDir(dir);
    },
  };
}

/** Splits a reduced reply into its first-line note and the JSON that follows. */
function splitNote(text) {
  const i = text.indexOf("\n");
  return { note: text.slice(0, i), json: JSON.parse(text.slice(i + 1)), body: text.slice(i + 1) };
}

// ---------------------------------------------------------------------------
// Large replies
// ---------------------------------------------------------------------------

test("run_batch over budget: valid JSON, note first, per-step status kept, bodies dropped", async () => {
  const results = [];
  for (let i = 0; i < 30; i++) {
    results.push(
      i === 7
        ? { index: i, tool: "set_property", ok: false, error: { code: "INVALID_PARAMS", message: "bad value", data: { big: "d".repeat(5000) } } }
        : { index: i, id: `s${i}`, tool: "get_object", ok: true, result: { guid: `guid-${i}`, blob: "b".repeat(8000) } },
    );
  }
  const s = await session({
    handler: (m, p) =>
      m === "run_batch" ? { ok: true, result: { completed: 29, attempted: 30, total: 30, failedStep: 7, note: "kept", results } } : defaultHandler(m, p),
  });
  try {
    const r = await s.client.callTool({ name: "run_batch", arguments: { steps: [{ tool: "get_object", params: { guid: "g" } }] } });
    assert.notEqual(r.isError, true);
    const { note, json, body } = splitNote(r.content[0].text);
    assert.match(note, /^\[Step results omitted because the reply was too large/);
    assert.match(note, /smaller batches/);
    assert.ok(body.length <= MAX);
    assert.equal(json.completed, 29);
    assert.equal(json.total, 30);
    assert.equal(json.failedStep, 7);
    assert.equal(json.note, "kept");
    assert.equal(json.results.length, 30);
    assert.deepEqual(json.results[0], { index: 0, id: "s0", tool: "get_object", ok: true, guid: "guid-0" });
    assert.deepEqual(json.results[7], { index: 7, tool: "set_property", ok: false, error: { code: "INVALID_PARAMS", message: "bad value" } });
    assert.ok(json.results.every((x) => x.result === undefined));
  } finally {
    await s.close();
  }
});

test("run_batch still over budget after reducing: keeps the rows that fit and says how many were dropped", async () => {
  const results = [];
  for (let i = 0; i < 100; i++) {
    results.push({ index: i, tool: "set_property", ok: false, error: { code: "INVALID_PARAMS", message: "m".repeat(2000) } });
  }
  const s = await session({
    handler: (m, p) => (m === "run_batch" ? { ok: true, result: { completed: 0, attempted: 100, total: 100, results } } : defaultHandler(m, p)),
  });
  try {
    const r = await s.client.callTool({ name: "run_batch", arguments: { steps: [{ tool: "get_object", params: { guid: "g" } }] } });
    const { note, json, body } = splitNote(r.content[0].text);
    assert.ok(body.length <= MAX);
    assert.ok(json.stepRowsDropped > 0 && json.stepRowsDropped < 100, String(json.stepRowsDropped));
    assert.equal(json.results.length + json.stepRowsDropped, 100);
    assert.equal(json.droppedFromIndex, json.results.length);
    assert.match(note, new RegExp(`${json.stepRowsDropped} of 100 step rows did not fit`));
    assert.match(note, /do not repeat those steps blindly/);
  } finally {
    await s.close();
  }
});

test("get_hierarchy over budget: deepest levels pruned (children null, depthLimited, childCount kept), valid JSON, note first", async () => {
  let n = 0;
  const build = (depth) => {
    const node = { guid: `g${n++}`, name: "Node ".repeat(6), active: true, components: ["ModProp", "ModBoxCollider"], childCount: 0, children: [] };
    if (depth > 0) {
      for (let i = 0; i < 5; i++) node.children.push(build(depth - 1));
      node.childCount = 5;
    }
    return node;
  };
  const tree = { guid: null, name: "HierarchyRoot", context: "scene", childCount: 5, children: build(6).children };
  const full = JSON.stringify(tree);
  assert.ok(full.length > MAX, `test tree is ${full.length} chars`);
  const s = await session({ handler: (m, p) => (m === "get_hierarchy" ? { ok: true, result: tree } : defaultHandler(m, p)) });
  try {
    const r = await s.client.callTool({ name: "get_hierarchy", arguments: {} });
    const { note, json, body } = splitNote(r.content[0].text);
    assert.match(note, /^\[tree pruned to fit: call get_hierarchy with rootGuid on a pruned node \(/);
    assert.match(note, /maxDepth/);
    assert.ok(body.length <= MAX);
    const pruned = [];
    const walk = (nd, d) => {
      if (nd.depthLimited) {
        assert.equal(nd.children, null);
        assert.equal(nd.childCount, 5);
        pruned.push(d);
      } else if (nd.children) nd.children.forEach((c) => walk(c, d + 1));
    };
    walk(json, 0);
    assert.ok(pruned.length > 0);
    assert.equal(new Set(pruned).size, 1, "only the deepest kept level is pruned");
    assert.ok(pruned[0] >= 2, "shallow levels stay");
  } finally {
    await s.close();
  }
});

test("get_hierarchy: a flat root with 2,000 leaf children keeps a prefix of its children (never just the root), valid JSON, honest note", async () => {
  const children = Array.from({ length: 2000 }, (_, i) => ({
    guid: `guid-${i}-` + "x".repeat(30), name: `Leaf object number ${i} `.repeat(2), active: true, components: ["ModProp"], childCount: 0, children: [],
  }));
  const tree = { guid: null, name: "HierarchyRoot", context: "scene", childCount: 2000, children, truncated: false, nodeCount: 2000 };
  assert.ok(JSON.stringify(tree).length > MAX);
  const s = await session({ handler: (m, p) => (m === "get_hierarchy" ? { ok: true, result: tree } : defaultHandler(m, p)) });
  try {
    const r = await s.client.callTool({ name: "get_hierarchy", arguments: {} });
    const { note, json, body } = splitNote(r.content[0].text);
    assert.ok(body.length <= MAX);
    assert.match(note, /^\[tree pruned to fit: the root has 2000 direct children and only the first \d+/);
    assert.match(note, /no paging/);
    assert.match(note, /rootGuid/);
    assert.equal(json.childCount, 2000);
    assert.equal(json.childrenTruncated, true);
    assert.ok(json.childrenShown >= 100 && json.childrenShown < 2000);
    assert.equal(json.children.length, json.childrenShown);
    assert.equal(json.nodesShown, json.childrenShown);
    assert.equal(json.nodeCount, 2000);
    assert.equal(json.children[0].depthLimited, undefined, "leaves are not marked depthLimited");
  } finally {
    await s.close();
  }
});

test("get_hierarchy: wide and deep tree: grandchildren are pruned first, then the direct children are cut; only nodes that lost children are depthLimited", async () => {
  const kid = (i) => ({
    guid: `g${i}-` + "y".repeat(30), name: `Branch ${i} `.repeat(2), active: true, components: ["ModProp"], childCount: 2,
    children: [
      { guid: `l${i}a`, name: "leaf a", active: true, components: [], childCount: 0, children: [] },
      { guid: `l${i}b`, name: "hidden kids", active: true, components: [], childCount: 0, childrenHidden: true, children: [] },
    ],
  });
  const tree = { guid: null, name: "HierarchyRoot", context: "scene", childCount: 1500, children: Array.from({ length: 1500 }, (_, i) => kid(i)), truncated: false, nodeCount: 4500 };
  assert.ok(JSON.stringify(tree).length > MAX);
  const s = await session({ handler: (m, p) => (m === "get_hierarchy" ? { ok: true, result: tree } : defaultHandler(m, p)) });
  try {
    const r = await s.client.callTool({ name: "get_hierarchy", arguments: {} });
    const { note, json, body } = splitNote(r.content[0].text);
    assert.ok(body.length <= MAX);
    assert.match(note, /^\[tree pruned to fit: the root has 1500 direct children/);
    assert.ok(json.childrenShown >= 1 && json.childrenShown < 1500);
    assert.equal(json.children.length, json.childrenShown);
    for (const c of json.children) {
      assert.equal(c.children, null);
      assert.equal(c.depthLimited, true);
      assert.equal(c.childCount, 2);
    }
    assert.equal(json.nodesShown, json.childrenShown);
  } finally {
    await s.close();
  }
});

test("get_hierarchy: a tree that fits after pruning deep levels keeps all direct children and marks only nodes that lost children", async () => {
  const leaf = (n) => ({ guid: `leaf${n}`, name: "Leaf ".repeat(8), active: true, components: ["ModProp"], childCount: 0, children: [] });
  const mid = (i) => ({
    guid: `m${i}`, name: "Mid", active: true, components: [], childCount: 30,
    children: Array.from({ length: 30 }, (_, j) => ({
      guid: `s${i}-${j}`, name: "Sub", active: true, components: [], childCount: 3, children: [leaf(`${i}-${j}-0`), leaf(`${i}-${j}-1`), leaf(`${i}-${j}-2`)],
    })),
  });
  const tree = { guid: "root", name: "R", active: true, components: [], childCount: 12, children: Array.from({ length: 12 }, (_, i) => mid(i)), nodeCount: 12 * 121 + 1 };
  assert.ok(JSON.stringify(tree).length > MAX);
  const s = await session({ handler: (m, p) => (m === "get_hierarchy" ? { ok: true, result: tree } : defaultHandler(m, p)) });
  try {
    const r = await s.client.callTool({ name: "get_hierarchy", arguments: {} });
    const { json, body } = splitNote(r.content[0].text);
    assert.ok(body.length <= MAX);
    assert.equal(json.children.length, 12);
    assert.equal(json.childrenTruncated, undefined);
    assert.equal(json.nodesShown, 1 + 12 + 360);
    const check = (n) => {
      if (n.depthLimited) assert.ok(n.childCount > 0 && n.children === null);
      else if (n.children) n.children.forEach(check);
      if (n.children && n.children.length === 0) assert.equal(n.depthLimited, undefined);
    };
    check(json);
  } finally {
    await s.close();
  }
});

// Read tools with potentially large output must carry a specific narrow hint (or be listed here as small by design).
const SMALL_READ_TOOLS = ["usage_guide", "ping", "get_status", "screenshot"];

test("every read tool has a narrow hint for oversized replies (or is in the explicit small-output list)", async () => {
  const huge = { ok: true, result: { items: ["z".repeat(150000)] } };
  const s = await session({ handler: (m, p) => (m === "ping" ? defaultHandler(m, p) : huge) });
  try {
    const { tools } = await s.client.listTools();
    const reads = tools.filter((t) => t.annotations?.readOnlyHint === true && !SMALL_READ_TOOLS.includes(t.name));
    assert.ok(reads.length >= 15, `${reads.length} read tools`);
    for (const t of reads) {
      const args = (t.inputSchema.required ?? []).includes("guid") || t.name === "get_action_graph" ? { guid: "g" } : {};
      const r = await s.client.callTool({ name: t.name, arguments: args });
      assert.notEqual(r.isError, true, t.name);
      const first = r.content[0].text.split("\n")[0];
      assert.match(first, /^\[Output truncated/, `${t.name}: the note comes first`);
      assert.match(first, /Narrow the query: /, t.name);
      assert.doesNotMatch(first, /unexpectedly large|filter\/limit parameters/, `${t.name} needs a specific narrow hint`);
    }
  } finally {
    await s.close();
  }
});

// ---------------------------------------------------------------------------
// Screenshots
// ---------------------------------------------------------------------------

test("screenshot: a base64 string over 4.5 million characters becomes a text error", async () => {
  let size = 4_600_000;
  const s = await session({ handler: (m, p) => (m === "screenshot" ? { ok: true, result: { pngBase64: "A".repeat(size), width: 7680, height: 4320 } } : defaultHandler(m, p)) });
  try {
    const big = await s.client.callTool({ name: "screenshot", arguments: { maxWidth: 7680 } });
    assert.equal(big.isError, true);
    assert.ok(big.content.every((c) => c.type === "text"));
    assert.match(big.content[0].text, /^screenshot too large \(4\.6 MB\): call again with a smaller maxWidth, e\.g\. 1280$/);
    size = 4_500_000;
    const ok = await s.client.callTool({ name: "screenshot", arguments: {} });
    assert.notEqual(ok.isError, true);
    assert.equal(ok.content[0].type, "image");
  } finally {
    await s.close();
  }
});

test("a screenshot reply over the 8 MB limit gets a screenshot-specific hint", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  const raw = (req, res) => {
    res.writeHead(200, { "Content-Type": "application/json", "Content-Length": String(9 * 1024 * 1024) });
    res.write("{");
    return true;
  };
  const fake = await startFake({ token: "t", raw });
  try {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("screenshot", {}, 5000), (e) => {
      assert.match(e.message, /larger than 8 MB/);
      assert.match(e.message, /smaller maxWidth \(e\.g\. 1280\)/);
      assert.doesNotMatch(e.message, /maxDepth|limit/);
      return true;
    });
    await assert.rejects(c.call("get_hierarchy", {}, 5000), /maxDepth and\/or rootGuid/);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

// ---------------------------------------------------------------------------
// HTTP error status without an envelope
// ---------------------------------------------------------------------------

test("an HTTP error status without a JSON envelope: write tools get the 'may or may not have been applied' note", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  let status = 500;
  const raw = (req, res) => {
    res.writeHead(status, { "Content-Type": "text/plain" });
    res.end("oops");
    return true;
  };
  const fake = await startFake({ token: "t", raw });
  try {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("set_object", {}, 5000, { write: true }), (e) => {
      assert.match(e.message, /HTTP 500/);
      assert.match(e.message, /may or may not have been applied/);
      return true;
    });
    await assert.rejects(c.call("get_object", {}, 5000), (e) => {
      assert.match(e.message, /HTTP 500/);
      assert.doesNotMatch(e.message, /may or may not/);
      return true;
    });
    status = 503; // a refusal before execution: nothing ambiguous
    await assert.rejects(c.call("set_object", {}, 5000, { write: true }), (e) => {
      assert.match(e.message, /HTTP 503/);
      assert.doesNotMatch(e.message, /may or may not/);
      return true;
    });
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

// ---------------------------------------------------------------------------
// Plugin api version check
// ---------------------------------------------------------------------------

const pingWith = (apiVersion, extra = {}) => (m, p) =>
  m === "ping"
    ? { ok: true, result: { pong: true, pluginVersion: "9.9.9", gameVersion: "g", apiVersion, sessionId: "s1", ...extra } }
    : m === "get_status"
      ? { ok: true, result: { editorState: "Editing" } }
      : defaultHandler(m, p);

const methods = (fake) => fake.requests.map((r) => JSON.parse(r.body).method);

test("api version match: one ping before the first plugin call, none after; ping and get_status show serverVersion and requiredApiVersion", async () => {
  const s = await session({ handler: pingWith(2), ownPing: true });
  try {
    const a = await s.client.callTool({ name: "get_object", arguments: { guid: "g" } });
    assert.notEqual(a.isError, true);
    assert.deepEqual(methods(s.fake), ["ping", "get_object"]);
    await s.client.callTool({ name: "get_object", arguments: { guid: "h" } });
    await s.client.callTool({ name: "save_mod", arguments: {} });
    assert.deepEqual(methods(s.fake), ["ping", "get_object", "get_object", "save_mod"], "no round trip added to every call");
    const ping = JSON.parse((await s.client.callTool({ name: "ping", arguments: {} })).content[0].text);
    assert.equal(ping.pong, true);
    assert.equal(ping.serverVersion, pkgVersion);
    assert.equal(ping.requiredApiVersion, 2);
    assert.equal(ping.apiMismatch, undefined);
    const st = await s.client.callTool({ name: "get_status", arguments: {} });
    const stj = JSON.parse(st.content[0].text);
    assert.equal(stj.serverVersion, pkgVersion);
    assert.equal(stj.requiredApiVersion, 2);
  } finally {
    await s.close();
  }
});

test("api version: plugin newer than the server is refused for every plugin tool (usage_guide, ping and get_status still work)", async () => {
  const s = await session({ handler: pingWith(3), ownPing: true });
  try {
    for (const [name, args] of [["get_object", { guid: "g" }], ["save_mod", {}], ["list_mods", {}], ["set_object", { guid: "g", active: true }]]) {
      const r = await s.client.callTool({ name, arguments: args });
      assert.equal(r.isError, true, name);
      assert.equal(
        r.content[0].text,
        `plugin api v3, this server (${pkgVersion}) needs api v2: update the server (npx -y pummel-party-mcp@latest).`,
      );
    }
    assert.deepEqual(methods(s.fake), ["ping"], "the mismatch is cached: one ping, no tool call reached the plugin");
    const guide = await s.client.callTool({ name: "usage_guide", arguments: {} });
    assert.notEqual(guide.isError, true);
    const ping = JSON.parse((await s.client.callTool({ name: "ping", arguments: {} })).content[0].text);
    assert.match(ping.apiMismatch, /update the server/);
    assert.equal(ping.requiredApiVersion, 2);
  } finally {
    await s.close();
  }
});

test("api version: plugin older than the server asks for a plugin update", async () => {
  const s = await session({ handler: pingWith(1), ownPing: true });
  try {
    const r = await s.client.callTool({ name: "get_hierarchy", arguments: {} });
    assert.equal(r.isError, true);
    assert.match(r.content[0].text, new RegExp(`^plugin api v1, this server \\(${pkgVersion.replace(/\./g, "\\.")}\\) needs api v2: update the plugin`));
    assert.doesNotMatch(r.content[0].text, /update the server/);
  } finally {
    await s.close();
  }
});

test("api version: a ping that fails (game not running) gives the normal error and is not cached", async () => {
  // the first request (the api-version ping) is dropped by the listener; later ones are answered
  const raw = (req, res, rec, state) => {
    if (state.requests.length === 1) {
      res.socket.destroy();
      return true;
    }
    return false;
  };
  const s = await session({ raw });
  try {
    const first = await s.client.callTool({ name: "get_object", arguments: { guid: "g" } });
    assert.equal(first.isError, true);
    assert.match(first.content[0].text, /pummel-party-mcp plugin/);
    assert.deepEqual(methods(s.fake), ["ping"], "the call itself never reached the plugin");
    const second = await s.client.callTool({ name: "get_object", arguments: { guid: "g" } });
    assert.notEqual(second.isError, true);
    assert.deepEqual(methods(s.fake), ["ping", "ping", "get_object"], "the failure was not cached: pinged again");
  } finally {
    await s.close();
  }
});

test("client: ensureApi caches, and checks again after a connection failure or another sessionId", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  let apiVersion = 2;
  let sessionId = "a";
  const handler = (m, p) =>
    m === "ping"
      ? { ok: true, result: { pong: true, apiVersion, sessionId } }
      : m === "get_log"
        ? { ok: true, result: { entries: [], sessionId } }
        : defaultHandler(m, p);
  const fake = await startFake({ token: "t", handler, ownPing: true });
  try {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await c.ensureApi(2, "x");
    await c.ensureApi(2, "x");
    assert.equal(methods(fake).length, 1);
    // the game restarted with a newer plugin: a reply with another sessionId invalidates the cache
    apiVersion = 3;
    sessionId = "b";
    await c.call("get_log");
    await assert.rejects(c.ensureApi(2, "x"), (e) => e.code === "API_MISMATCH" && /plugin api v3/.test(e.message));
    assert.equal(methods(fake).filter((m) => m === "ping").length, 2);
    // a failed connection also resets it: the SAME client pings again once a plugin is back on the port
    apiVersion = 2;
    const port = fake.port;
    await fake.close();
    await assert.rejects(c.call("get_object", { guid: "g" }));
    const fake2 = await startFake({ token: "t", handler, ownPing: true, port });
    try {
      await c.ensureApi(2, "x");
      assert.deepEqual(methods(fake2), ["ping"]);
      await c.ensureApi(2, "x");
      assert.deepEqual(methods(fake2), ["ping"]);
    } finally {
      await fake2.close();
    }
  } finally {
    removeDir(dir);
  }
});

test("client: ensureApi pings again when the last successful reply is more than 10 s old (injected clock), concurrent calls share one ping", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  let apiVersion = 2;
  const handler = (m, p) => (m === "ping" ? { ok: true, result: { pong: true, apiVersion } } : defaultHandler(m, p));
  const fake = await startFake({ token: "t", handler, ownPing: true });
  try {
    let now = 1_000_000;
    const c = new PluginClient({ port: fake.port, tokenFile: tf, now: () => now });
    await c.ensureApi(2, "x");
    now += 9_000;
    await c.ensureApi(2, "x");
    assert.equal(methods(fake).filter((m) => m === "ping").length, 1);
    // any successful reply refreshes the timer
    await c.call("get_object", { guid: "g" });
    now += 9_000;
    await c.ensureApi(2, "x");
    assert.equal(methods(fake).filter((m) => m === "ping").length, 1);
    // more than 10 s since the last reply: ping again, and notice a game restarted with another plugin
    now += 10_001;
    apiVersion = 3;
    await assert.rejects(Promise.all([c.ensureApi(2, "x"), c.ensureApi(2, "x"), c.ensureApi(2, "x")]), (e) => e.code === "API_MISMATCH");
    assert.equal(methods(fake).filter((m) => m === "ping").length, 2);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("client: a failed version check says the tool call was not sent (writes: nothing was applied)", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  const fake = await startFake({ token: "t" });
  const port = fake.port;
  await fake.close();
  try {
    const c = new PluginClient({ port, tokenFile: tf });
    await assert.rejects(c.ensureApi(2, "x"), (e) => /version check/.test(e.message) && /not sent/.test(e.message) && !/nothing was applied/.test(e.message));
    await assert.rejects(c.ensureApi(2, "x", { write: true }), (e) => /version check/.test(e.message) && /nothing was applied/.test(e.message));
  } finally {
    removeDir(dir);
  }
});

test("the fixture ping still matches the version the server requires", () => {
  assert.equal(defaultHandler("ping", {}).result.apiVersion, 2);
});
