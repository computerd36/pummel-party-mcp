import { test } from "node:test";
import assert from "node:assert/strict";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { startFake, makeTempDir, removeDir, writeToken } from "./fake-plugin.mjs";
import { TOOL_TIMEOUTS_MS, PluginClient, optionsFromEnv } from "../dist/client.js";

const here = dirname(fileURLToPath(import.meta.url));
const entry = join(here, "..", "dist", "index.js");

async function session({ handler, token = "pttoken", env = {} } = {}) {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, token);
  const fake = await startFake({ token, handler });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [entry],
    env: { ...process.env, PUMMEL_PARTY_MCP_PORT: String(fake.port), PUMMEL_PARTY_MCP_TOKEN_FILE: tf, ...env },
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

const call = async (client, name, args) => {
  try {
    return await client.callTool({ name, arguments: args });
  } catch (e) {
    return { isError: true, content: [{ type: "text", text: String(e?.message ?? e) }] };
  }
};

test("start_playtest.players: valid forms pass through, 1 and 9 and junk are rejected", async () => {
  const s = await session();
  try {
    const valid = [2, 8, "4 Players", "4 players", "2v2 Teams", "2V2V2V2 TEAMS", "3v3", "4v4 Teams", "5", "2v2v2"];
    for (const players of valid) {
      const r = await call(s.client, "start_playtest", { players, aiOnly: true, validateFirst: false });
      assert.notEqual(r.isError, true, `${JSON.stringify(players)}: ${r.content?.[0]?.text}`);
      assert.deepEqual(JSON.parse(s.fake.requests.at(-1).body), {
        method: "start_playtest",
        params: { players, aiOnly: true, validateFirst: false },
      });
    }
    const none = await call(s.client, "start_playtest", {});
    assert.notEqual(none.isError, true);
    assert.deepEqual(JSON.parse(s.fake.requests.at(-1).body).params, {});
    for (const players of [1, 9, 2.5, "1 Players", "9", "9 Players", "5v5", "", "four", null, true]) {
      const n0 = s.fake.requests.length;
      const r = await call(s.client, "start_playtest", { players });
      assert.equal(r.isError, true, JSON.stringify(players));
      assert.equal(s.fake.requests.length, n0, `${JSON.stringify(players)} must not reach the plugin`);
    }
    const n1 = s.fake.requests.length;
    assert.equal((await call(s.client, "start_playtest", { aiOnly: true, extra: 1 })).isError, true);
    assert.equal(s.fake.requests.length, n1);
  } finally {
    await s.close();
  }
});

test("run_batch: schema limits, allowed tools and ids are enforced before the plugin", async () => {
  const s = await session();
  try {
    const { tools } = await s.client.listTools();
    const batch = tools.find((t) => t.name === "run_batch");
    const toolEnum = batch.inputSchema.properties.steps.items.properties.tool.enum;
    assert.equal(toolEnum.length, 30);
    for (const bad of ["run_batch", "start_playtest", "stop_playtest", "create_test_mod", "open_test_mod", "screenshot", "ping", "get_status", "get_log", "get_open_mod", "validate_mod"]) {
      assert.ok(!toolEnum.includes(bad), bad);
    }
    assert.equal(batch.inputSchema.properties.steps.minItems, 1);
    assert.equal(batch.inputSchema.properties.steps.maxItems, 100);

    const step = (extra = {}) => ({ tool: "set_object", params: { guid: "g", active: true }, ...extra });
    const bad = {
      "0 steps": { steps: [] },
      "101 steps": { steps: Array.from({ length: 101 }, () => step()) },
      "start_playtest": { steps: [{ tool: "start_playtest" }] },
      "run_batch": { steps: [{ tool: "run_batch", params: { steps: [] } }] },
      "unknown tool": { steps: [{ tool: "nope" }] },
      "id with space": { steps: [step({ id: "a b" })] },
      "id starts with digit": { steps: [step({ id: "1a" })] },
      "id starts with $": { steps: [step({ id: "$a" })] },
      "id 65 chars": { steps: [step({ id: "a".repeat(65) })] },
      "empty id": { steps: [step({ id: "" })] },
      "duplicate ids": { steps: [step({ id: "a" }), step({ id: "a" })] },
      "extra step key": { steps: [step({ stop: true })] },
      "params not an object": { steps: [{ tool: "undo", params: [1] }] },
      "missing steps": {},
      "extra top-level key": { steps: [step()], stop: true },
      "stopOnError not boolean": { steps: [step()], stopOnError: "yes" },
    };
    for (const [why, args] of Object.entries(bad)) {
      const n0 = s.fake.requests.length;
      const r = await call(s.client, "run_batch", args);
      assert.equal(r.isError, true, why);
      assert.equal(s.fake.requests.length, n0, `${why} must not reach the plugin`);
    }

    // boundaries are accepted
    const ok100 = { steps: Array.from({ length: 100 }, () => step()) };
    assert.notEqual((await call(s.client, "run_batch", ok100)).isError, true);
    assert.notEqual((await call(s.client, "run_batch", { steps: [step({ id: "A" + "b".repeat(63) })] })).isError, true);
    assert.notEqual((await call(s.client, "run_batch", { steps: [{ tool: "undo", params: null }] })).isError, true);

    // a valid batch with placeholders arrives unchanged: params are byte-identical; only the three step keys are
    // emitted in schema order (tool, params, id), which the test data already uses
    const valid = {
      steps: [
        { tool: "create_object", params: { kind: "cube", name: "Floor", position: { x: 0, y: -0.5, z: 0 } }, id: "floor" },
        { tool: "create_object", params: { kind: "trigger", parentGuid: "$floor.guid" }, id: "hazard" },
        { tool: "set_property", params: { guid: "$hazard.guid", component: "Trigger", property: "TriggerOnEnter", value: true } },
        { tool: "add_action", params: { guid: "$hazard.guid", trigger: "OnEnterActions", type: "ChangeHealth", parameters: { m_value: 1, list: ["$floor.transform.localPosition", "$$5", "$a.b[0].c[12]"] } } },
        { tool: "get_hierarchy" },
        { tool: "get_object", params: null },
      ],
      stopOnError: false,
    };
    const r = await call(s.client, "run_batch", valid);
    assert.notEqual(r.isError, true, r.content?.[0]?.text);
    assert.equal(s.fake.requests.at(-1).body, JSON.stringify({ method: "run_batch", params: valid }));
  } finally {
    await s.close();
  }
});

test("error.data is appended to error results as compact JSON; the token never appears", async () => {
  const token = "datatoken123456";
  const validation = { valid: false, errorCount: 1, warningCount: 0, infoCount: 0, issues: [{ severity: "error", message: "There are no player spawns in your map.", fixable: true }] };
  const s = await session({
    token,
    handler: (method) => {
      if (method === "start_playtest") {
        return { ok: false, error: { code: "INVALID_STATE", message: "the mod does not pass the editor's validator (1 validator errors: no spawns)", data: { validation, echo: token } } };
      }
      if (method === "stop_playtest") return { ok: false, error: { code: "INVALID_STATE", message: "still entering", data: { big: "x".repeat(10000) } } };
      if (method === "run_batch") return { ok: false, error: { code: "INVALID_PARAMS", message: "steps[0]: bad; nothing was run", data: null } };
      return { ok: true, result: {} };
    },
  });
  try {
    const r = await s.client.callTool({ name: "start_playtest", arguments: {} });
    assert.equal(r.isError, true);
    const text = r.content[0].text;
    assert.match(text, /^INVALID_STATE: the mod does not pass the editor's validator/);
    assert.ok(text.includes(`"issues":[{"severity":"error","message":"There are no player spawns in your map.","fixable":true}]`), text);
    assert.ok(text.includes('"validation":{"valid":false'), text);
    assert.ok(!text.includes(token), "token must be scrubbed from error.data");
    assert.match(text, /\[token\]/);
    assert.match(text, /Hint:/);

    // size limit: long data is cut with a marker
    const big = await s.client.callTool({ name: "stop_playtest", arguments: {} });
    assert.ok(big.content[0].text.length < 6000, String(big.content[0].text.length));
    assert.match(big.content[0].text, /\[truncated: \d+ chars\]/);

    // no data: no Data line
    const none = await s.client.callTool({ name: "run_batch", arguments: { steps: [{ tool: "undo" }] } });
    assert.equal(none.isError, true);
    assert.doesNotMatch(none.content[0].text, /Data:/);
    assert.ok(!JSON.stringify([r, big, none]).includes(token));
  } finally {
    await s.close();
  }
});

test("the three long timeouts are set, and the env override still wins", async () => {
  assert.equal(TOOL_TIMEOUTS_MS.start_playtest, 70000);
  assert.equal(TOOL_TIMEOUTS_MS.stop_playtest, 40000);
  assert.equal(TOOL_TIMEOUTS_MS.run_batch, 130000);
  // each exceeds the plugin limit (60/30/120 s) + 4 s commit grace
  assert.ok(TOOL_TIMEOUTS_MS.start_playtest >= 64000 && TOOL_TIMEOUTS_MS.stop_playtest >= 34000 && TOOL_TIMEOUTS_MS.run_batch >= 124000);

  // the per-tool value reaches the request timer: a delayed reply longer than the default 15 s would
  // fail with the default, but the override option shows the same code path is used for all tools
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "tt");
  const fake = await startFake({ token: "tt", delayMs: 800 });
  try {
    const mk = (timeoutMs) => new PluginClient({ ...optionsFromEnv({}), port: fake.port, tokenFile: tf, timeoutMs });
    // override wins over the per-tool value
    await assert.rejects(mk(200).call("run_batch", { steps: [] }, TOOL_TIMEOUTS_MS.run_batch, { write: true }), /timed out after 0\.2s/);
    // without override the long per-tool value is used and the slow reply succeeds
    const ok = await mk(undefined).call("stop_playtest", {}, TOOL_TIMEOUTS_MS.stop_playtest, { write: true });
    assert.equal(ok.stopped, true);
    assert.equal(optionsFromEnv({ PUMMEL_PARTY_MCP_TIMEOUT_MS: "250" }).timeoutMs, 250);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("annotations of the new tools", async () => {
  const s = await session();
  try {
    const { tools } = await s.client.listTools();
    const by = Object.fromEntries(tools.map((t) => [t.name, t]));
    assert.equal(tools.length, 46);
    assert.equal(by.validate_mod.annotations.readOnlyHint, true);
    assert.deepEqual(by.validate_mod.inputSchema.properties ?? {}, {});
    assert.deepEqual(
      [by.start_playtest.annotations.readOnlyHint, by.start_playtest.annotations.destructiveHint, by.start_playtest.annotations.idempotentHint],
      [false, false, false],
    );
    assert.match(by.start_playtest.description, /SAVES the open sandbox mod/);
    assert.equal(by.stop_playtest.annotations.readOnlyHint, false);
    assert.equal(by.stop_playtest.annotations.idempotentHint, true);
    assert.equal(by.stop_playtest.annotations.destructiveHint, false);
    assert.equal(by.run_batch.annotations.destructiveHint, true);
    assert.equal(by.run_batch.annotations.readOnlyHint, false);
    for (const n of ["start_playtest", "stop_playtest", "run_batch"]) assert.match(by[n].description, /usage_guide/, n);
    const guide = (await s.client.callTool({ name: "usage_guide", arguments: { topic: "playtest" } })).content[0].text + (await s.client.callTool({ name: "usage_guide", arguments: { topic: "batch" } })).content[0].text + (await s.client.callTool({ name: "usage_guide", arguments: { topic: "overview" } })).content[0].text + (await s.client.callTool({ name: "usage_guide", arguments: { topic: "safety" } })).content[0].text;
    assert.match(guide, /PLAYTEST:/);
    assert.match(guide, /BATCH:/);
    assert.match(guide, /MINIMAL VALID MINIGAME/);
  } finally {
    await s.close();
  }
});

test("get_playtest_state and get_playtest_events: read-only, strict schemas, params pass through, guide mentions them", async () => {
  const seen = [];
  const s = await session({
    handler: (method, params) => {
      seen.push({ method, params });
      if (method === "get_playtest_state") return { ok: true, result: { running: true, players: [{ index: 0, name: "Bot 0", alive: true }], useHealth: false, note: "n" } };
      if (method === "get_playtest_events") return { ok: true, result: { entries: [{ id: 1, type: "player_died", player: 0 }], nextId: 1, dropped: false } };
      return { ok: true, result: {} };
    },
  });
  try {
    const { tools } = await s.client.listTools();
    const by = Object.fromEntries(tools.map((t) => [t.name, t]));
    for (const n of ["get_playtest_state", "get_playtest_events"]) {
      assert.equal(by[n].annotations.readOnlyHint, true, n);
      assert.equal(by[n].annotations.openWorldHint, false, n);
      assert.equal(by[n].inputSchema.additionalProperties, false, n);
      assert.equal(by[n].inputSchema.type, "object", n);
    }
    assert.deepEqual(by.get_playtest_state.inputSchema.properties ?? {}, {});
    const ev = by.get_playtest_events.inputSchema.properties;
    assert.deepEqual([ev.max.minimum, ev.max.maximum, ev.sinceId.minimum], [1, 500, 0]);
    assert.match(by.get_playtest_events.description, /nextId/);
    assert.match(by.get_playtest_state.description, /INVALID_STATE/);

    const st = await call(s.client, "get_playtest_state", {});
    assert.notEqual(st.isError, true);
    assert.equal(JSON.parse(st.content[0].text).players[0].name, "Bot 0");
    assert.deepEqual(seen.at(-1), { method: "get_playtest_state", params: {} });
    const e1 = await call(s.client, "get_playtest_events", { max: 500, sinceId: 12 });
    assert.equal(JSON.parse(e1.content[0].text).entries[0].type, "player_died");
    assert.deepEqual(seen.at(-1), { method: "get_playtest_events", params: { max: 500, sinceId: 12 } });
    await call(s.client, "get_playtest_events", {});
    assert.deepEqual(seen.at(-1), { method: "get_playtest_events", params: {} });

    // strict: nothing invalid reaches the plugin
    const n0 = seen.length;
    for (const [name, args] of [
      ["get_playtest_events", { max: 0 }], ["get_playtest_events", { max: 501 }], ["get_playtest_events", { sinceId: -1 }],
      ["get_playtest_events", { max: 1.5 }], ["get_playtest_events", { since: 3 }], ["get_playtest_state", { verbose: true }],
    ]) {
      const r = await call(s.client, name, args);
      assert.equal(r.isError, true, `${name} ${JSON.stringify(args)}`);
    }
    assert.equal(seen.length, n0, "invalid calls must not reach the plugin");

    // the shared guide tells the agent to verify mechanics with them, and the 256 sentinel is not a Workshop/minigame id
    const guide = (await s.client.callTool({ name: "usage_guide", arguments: { topic: "playtest" } })).content[0].text + (await s.client.callTool({ name: "usage_guide", arguments: { topic: "batch" } })).content[0].text + (await s.client.callTool({ name: "usage_guide", arguments: { topic: "overview" } })).content[0].text + (await s.client.callTool({ name: "usage_guide", arguments: { topic: "safety" } })).content[0].text;
    assert.match(guide, /get_playtest_state/);
    assert.match(guide, /get_playtest_events/);
    assert.match(guide, /stop_playtest first and read afterwards/);
    assert.match(guide, /useHealth off/);
    assert.match(guide, /publishedFileId "256"[^.]*never published/);
    assert.match(guide, /not a Workshop item or a minigame id/);
  } finally {
    await s.close();
  }
});

test("a plugin INVALID_STATE for get_playtest_state is shown with its message and hint", async () => {
  const s = await session({
    handler: (method) =>
      method === "get_playtest_state"
        ? { ok: false, error: { code: "INVALID_STATE", message: "no playtest is running (editor state Editing); start one with start_playtest" } }
        : { ok: true, result: {} },
  });
  try {
    const r = await call(s.client, "get_playtest_state", {});
    assert.equal(r.isError, true);
    assert.match(r.content[0].text, /INVALID_STATE/);
    assert.match(r.content[0].text, /no playtest is running/);
  } finally {
    await s.close();
  }
});
