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
];

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
