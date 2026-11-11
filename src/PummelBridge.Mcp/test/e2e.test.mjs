import { test } from "node:test";
import assert from "node:assert/strict";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { startFake, makeTempDir, removeDir, writeToken, PNG_B64 } from "./fake-plugin.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const entry = join(here, "..", "dist", "index.js");

const EXPECTED = [
  "get_action_graph", "get_hierarchy", "get_log", "get_object", "get_open_mod",
  "get_status", "list_assets", "ping", "screenshot",
  "list_mods", "create_test_mod", "open_test_mod", "save_mod",
].sort();
const WRITES = ["create_test_mod", "open_test_mod", "save_mod"];

test("end to end over stdio MCP", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "e2etoken");
  const fake = await startFake({ token: "e2etoken" });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [entry],
    env: { ...process.env, PUMMELBRIDGE_PORT: String(fake.port), PUMMELBRIDGE_TOKEN_FILE: tf },
    stderr: "ignore",
  });
  const client = new Client({ name: "test", version: "0.0.0" });
  try {
    await client.connect(transport);
    assert.equal(client.getServerVersion()?.name, "pummelbridge");

    const { tools } = await client.listTools();
    assert.deepEqual(tools.map((t) => t.name).sort(), EXPECTED);
    for (const t of tools) {
      assert.equal(t.inputSchema.type, "object");
      assert.ok(t.description && t.description.length > 20);
    }
    assert.equal(tools.length, 13);
    for (const t of tools) {
      if (WRITES.includes(t.name)) {
        assert.equal(t.annotations.readOnlyHint, false, t.name);
        assert.equal(t.annotations.destructiveHint, false, t.name);
        assert.equal(t.annotations.idempotentHint, t.name === "save_mod", t.name);
        assert.match(t.description, /PB_TEST_/);
        assert.match(t.description, /Workshop publishing is blocked/);
      } else {
        assert.equal(t.annotations.readOnlyHint, true, t.name);
      }
    }
    const obj = tools.find((t) => t.name === "get_object");
    assert.deepEqual(obj.inputSchema.required, ["guid"]);

    const ping = await client.callTool({ name: "ping", arguments: {} });
    assert.notEqual(ping.isError, true);
    assert.equal(JSON.parse(ping.content[0].text).pong, true);

    // missing required guid must be rejected by schema validation (error result or protocol error)
    let rejected = false;
    const before = fake.requests.length;
    try {
      const r = await client.callTool({ name: "get_object", arguments: {} });
      rejected = r.isError === true;
    } catch {
      rejected = true;
    }
    assert.ok(rejected, "missing guid should be rejected");
    assert.equal(fake.requests.length, before, "invalid call must not reach the plugin");

    const ok = await client.callTool({ name: "get_object", arguments: { guid: "X" } });
    assert.equal(JSON.parse(ok.content[0].text).guid, "X");

    const shot = await client.callTool({ name: "screenshot", arguments: {} });
    assert.equal(shot.content[0].type, "image");
    assert.equal(shot.content[0].mimeType, "image/png");
    assert.equal(shot.content[0].data, PNG_B64);
    assert.match(shot.content[1].text, /1 x 1/);

    // client-side name validation: invalid names never reach the plugin
    const badNames = ["MyMod", "pb_test_x", "PB_TEST_", "PB_TEST_a/b", "PB_TEST_" + "a".repeat(62), "PB_TEST_a ", "PB_TEST_a\n"];
    for (const name of badNames) {
      for (const tool of ["create_test_mod", "open_test_mod"]) {
        const n0 = fake.requests.length;
        let rej = false;
        try {
          rej = (await client.callTool({ name: tool, arguments: { name } })).isError === true;
        } catch {
          rej = true;
        }
        assert.ok(rej, `${tool} ${JSON.stringify(name)} should be rejected`);
        assert.equal(fake.requests.length, n0, "must not reach plugin");
      }
    }
    const create = await client.callTool({ name: "create_test_mod", arguments: { name: "PB_TEST_My Mod-1", type: "Board" } });
    assert.notEqual(create.isError, true);
    assert.deepEqual(JSON.parse(fake.requests.at(-1).body), { method: "create_test_mod", params: { name: "PB_TEST_My Mod-1", type: "Board" } });
    await client.callTool({ name: "open_test_mod", arguments: { name: "PB_TEST_x", discardUnsaved: true } });
    assert.deepEqual(JSON.parse(fake.requests.at(-1).body), { method: "open_test_mod", params: { name: "PB_TEST_x", discardUnsaved: true } });
    await client.callTool({ name: "save_mod", arguments: {} });
    assert.deepEqual(JSON.parse(fake.requests.at(-1).body), { method: "save_mod", params: {} });
    const lm = await client.callTool({ name: "list_mods", arguments: {} });
    assert.equal(JSON.parse(lm.content[0].text)[0].isTestMod, true);
    const max = await client.callTool({ name: "create_test_mod", arguments: { name: "PB_TEST_" + "a".repeat(56) } });
    assert.notEqual(max.isError, true, "64-char name is valid");

    // plugin error becomes isError result
    const bad = await client.callTool({ name: "get_status", arguments: {} });
    assert.equal(bad.isError, true);
    assert.match(bad.content[0].text, /UNKNOWN_METHOD/);

    // restart simulation: token changes, server recovers
    fake.token = "rotated";
    writeToken(tf, "rotated");
    const again = await client.callTool({ name: "ping", arguments: {} });
    assert.notEqual(again.isError, true);
  } finally {
    await client.close();
    await fake.close();
    removeDir(dir);
  }
});

test("plugin down yields isError result, process survives", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "x");
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [entry],
    env: { ...process.env, PUMMELBRIDGE_PORT: "1", PUMMELBRIDGE_TOKEN_FILE: tf },
    stderr: "ignore",
  });
  const client = new Client({ name: "test", version: "0.0.0" });
  try {
    await client.connect(transport);
    const r = await client.callTool({ name: "ping", arguments: {} });
    assert.equal(r.isError, true);
    assert.match(r.content[0].text, /not running or the PummelBridge plugin is not loaded/);
    const r2 = await client.callTool({ name: "ping", arguments: {} });
    assert.equal(r2.isError, true);
  } finally {
    await client.close();
    removeDir(dir);
  }
});
