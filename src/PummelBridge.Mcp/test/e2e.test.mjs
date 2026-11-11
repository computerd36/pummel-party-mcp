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
  "bridge_guide", "get_action_graph", "get_hierarchy", "get_log", "get_object", "get_open_mod",
  "get_status", "list_assets", "ping", "screenshot",
  "list_mods", "create_test_mod", "open_test_mod", "save_mod",
  "list_component_types", "create_object", "duplicate_object", "delete_object", "reparent_object", "set_object",
  "set_transform", "add_component", "remove_component", "set_property", "set_properties", "undo",
  "list_action_types", "list_trigger_types", "add_action", "remove_action", "move_action", "set_action",
  "add_trigger", "remove_trigger", "set_trigger",
  "validate_mod", "start_playtest", "stop_playtest", "run_batch",
].sort();
const WRITES = [
  "create_test_mod", "open_test_mod", "save_mod",
  "create_object", "duplicate_object", "delete_object", "reparent_object", "set_object", "set_transform",
  "add_component", "remove_component", "set_property", "set_properties", "undo",
  "add_action", "remove_action", "move_action", "set_action", "add_trigger", "remove_trigger", "set_trigger",
  "start_playtest", "stop_playtest", "run_batch",
];
const IDEMPOTENT = ["save_mod", "set_transform", "set_object", "set_property", "set_properties", "set_action", "set_trigger", "stop_playtest"];
const DESTRUCTIVE = ["delete_object", "remove_component", "remove_action", "remove_trigger", "undo", "run_batch"];

test("end to end over stdio MCP", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "e2etoken");
  const fake = await startFake({ token: "e2etoken" });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [entry],
    env: { ...process.env, PUMMELBRIDGE_PORT: String(fake.port), PUMMELBRIDGE_TOKEN_FILE: tf },
    stderr: "pipe",
  });
  let stderrText = "";
  transport.stderr?.on("data", (d) => (stderrText += d));
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
    assert.equal(tools.length, 39);
    for (const t of tools) {
      if (WRITES.includes(t.name)) {
        assert.equal(t.annotations.readOnlyHint, false, t.name);
        assert.equal(t.annotations.destructiveHint, DESTRUCTIVE.includes(t.name), t.name);
        assert.equal(t.annotations.idempotentHint, IDEMPOTENT.includes(t.name), t.name);
        assert.match(t.description, /bridge_guide/);
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

    // ---- editing tools ----
    const byName = Object.fromEntries(tools.map((t) => [t.name, t]));
    assert.equal(byName.list_component_types.annotations.readOnlyHint, true);
    assert.deepEqual([...byName.reparent_object.inputSchema.required].sort(), ["guid", "newParentGuid"]);
    assert.deepEqual([...byName.set_property.inputSchema.required].sort(), ["component", "guid", "property", "value"]);
    assert.deepEqual([...byName.set_properties.inputSchema.required].sort(), ["component", "guid", "values"]);
    assert.equal(byName.create_object.inputSchema.properties.kind.enum.length, 19);
    assert.equal(byName.undo.inputSchema.properties.steps.maximum, 20);
    assert.equal(byName.undo.inputSchema.properties.steps.minimum, 1);

    const rejects = async (name, args, why) => {
      const n0 = fake.requests.length;
      let rej = false;
      try {
        rej = (await client.callTool({ name, arguments: args })).isError === true;
      } catch {
        rej = true;
      }
      assert.ok(rej, `${name} ${why} should be rejected`);
      assert.equal(fake.requests.length, n0, `${name} ${why} must not reach plugin`);
    };
    const lastReq = () => JSON.parse(fake.requests.at(-1).body);
    const echoed = (r) => JSON.parse(r.content[0].text).echoed;

    await rejects("set_object", { name: "x" }, "missing guid");
    await rejects("delete_object", {}, "missing guid");
    await rejects("set_transform", { position: [0, 0, 0] }, "missing guid");
    await rejects("create_object", { kind: "banana" }, "invalid kind");
    await rejects("create_object", { kind: "cube", position: [1, 2] }, "bad vector");
    await rejects("undo", { steps: 0 }, "steps 0");
    await rejects("undo", { steps: 21 }, "steps 21");
    await rejects("undo", { steps: 1.5 }, "fractional steps");
    await rejects("reparent_object", { guid: "g" }, "missing newParentGuid");
    await rejects("reparent_object", { guid: "g", newParentGuid: "p", siblingIndex: -1 }, "negative siblingIndex");
    await rejects("set_property", { guid: "g", component: "Light", property: "type" }, "missing value");
    await rejects("set_properties", { guid: "g", component: "Light", values: {} }, "empty values");

    const rp = await client.callTool({ name: "reparent_object", arguments: { guid: "g", newParentGuid: null } });
    assert.notEqual(rp.isError, true);
    assert.deepEqual(lastReq(), { method: "reparent_object", params: { guid: "g", newParentGuid: null } });
    await client.callTool({ name: "reparent_object", arguments: { guid: "g", newParentGuid: "p", siblingIndex: 0 } });
    assert.deepEqual(lastReq().params, { guid: "g", newParentGuid: "p", siblingIndex: 0 });

    const nested = {
      type: "Spot",
      color: { r: 1, g: 0.5, b: 0, a: 1 },
      list: [1, "two", { three: [3, null] }],
      curve: { mode: "TwoConstants", constantMin: 0, constantMax: 2.5 },
      ref: null,
      flag: false,
    };
    const sps = await client.callTool({ name: "set_properties", arguments: { guid: "g", component: "Light", values: nested } });
    assert.notEqual(sps.isError, true);
    assert.deepEqual(lastReq().params.values, nested);
    assert.deepEqual(echoed(sps).values, nested);
    const sp = await client.callTool({ name: "set_property", arguments: { guid: "g", component: "Light", property: "ref", value: null } });
    assert.notEqual(sp.isError, true);
    assert.deepEqual(lastReq().params, { guid: "g", component: "Light", property: "ref", value: null });
    await client.callTool({ name: "set_property", arguments: { guid: "g", component: "Light", property: "c", value: { r: 1, g: 0, b: 0 } } });
    assert.deepEqual(lastReq().params.value, { r: 1, g: 0, b: 0 });

    // destructive tool round trip
    const del = await client.callTool({ name: "delete_object", arguments: { guid: "abc" } });
    assert.notEqual(del.isError, true);
    assert.deepEqual(lastReq(), { method: "delete_object", params: { guid: "abc" } });
    assert.equal(JSON.parse(del.content[0].text).undoable, true);
    await client.callTool({ name: "remove_component", arguments: { guid: "abc", component: "ModLight" } });
    assert.deepEqual(lastReq(), { method: "remove_component", params: { guid: "abc", component: "ModLight" } });

    const co = await client.callTool({
      name: "create_object",
      arguments: { kind: "prop", name: "Crate", parentGuid: null, position: { x: 0, y: 1, z: 0 }, scale: [1, 1, 1], prop: "p1" },
    });
    assert.notEqual(co.isError, true);
    assert.deepEqual(lastReq().params, { kind: "prop", name: "Crate", parentGuid: null, position: { x: 0, y: 1, z: 0 }, scale: [1, 1, 1], prop: "p1" });
    await client.callTool({ name: "set_transform", arguments: { guid: "g", rotation: { x: 0, y: 45, z: 0 } } });
    assert.deepEqual(lastReq().params, { guid: "g", rotation: { x: 0, y: 45, z: 0 } });
    await client.callTool({ name: "undo", arguments: {} });
    assert.deepEqual(lastReq(), { method: "undo", params: {} });
    await client.callTool({ name: "undo", arguments: { steps: 20 } });
    assert.deepEqual(lastReq().params, { steps: 20 });
    await client.callTool({ name: "list_component_types", arguments: { guid: "g" } });
    assert.deepEqual(lastReq(), { method: "list_component_types", params: { guid: "g" } });
    await client.callTool({ name: "duplicate_object", arguments: { guid: "g" } });
    await client.callTool({ name: "set_object", arguments: { guid: "g", active: false } });
    assert.deepEqual(lastReq().params, { guid: "g", active: false });
    await client.callTool({ name: "add_component", arguments: { guid: "g", type: "Light" } });
    assert.deepEqual(lastReq().params, { guid: "g", type: "Light" });

    // ---- action / trigger tools ----
    for (const t of tools) assert.equal(t.inputSchema.type, "object", `${t.name} schema must be type object`);
    for (const n of ["list_action_types", "list_trigger_types"]) assert.equal(byName[n].annotations.readOnlyHint, true, n);
    assert.deepEqual([...byName.add_action.inputSchema.properties.path.items.required].sort(), ["action", "choice"]);
    assert.equal(byName.add_action.inputSchema.properties.path.maxItems, 8);
    assert.equal(byName.add_action.inputSchema.properties.index.minimum, 0);
    assert.ok(byName.add_action.inputSchema.required.includes("type"));
    assert.match(byName.add_action.description, /list_action_types/);
    assert.match(byName.add_action.description, /bridge_guide/);
    const instructions = client.getInstructions();
    assert.match(instructions, /APPEND only/);
    assert.match(instructions, /editable/);
    assert.match(instructions, /NOT undoable/);
    assert.match(instructions, /PB_TEST_/);
    assert.match(instructions, /Workshop publishing is blocked/);

    const objAddr = { guid: "g", component: "ModTrigger", trigger: "OnEnterActions" };
    const psAddr = { scope: "playerSettings", trigger: "onDeathTrigger" };
    const nestedParams = {
      m_seconds: { valueType: "RandomBetweenTwoValues", min: 1, max: 2.5 },
      m_choices: [{ m_buttonText: "Yes", m_costType: "Keys", m_costAmount: 3 }, null, { buttonText: "No" }],
      m_color: { r: 1, g: 0.5, b: 0, a: 1 },
      list: [1, "two", { three: [3, null] }],
      flag: false,
    };

    // both address forms validate and arrive unchanged
    const aaArgs = { ...objAddr, type: "Wait", index: 0, parameters: nestedParams, targetFlags: "Source, Advanced", targets: ["a", { guid: "b" }, null] };
    const aa = await client.callTool({ name: "add_action", arguments: aaArgs });
    assert.notEqual(aa.isError, true);
    assert.deepEqual(lastReq(), { method: "add_action", params: aaArgs });
    assert.deepEqual(echoed(aa).parameters, nestedParams);
    await client.callTool({ name: "add_action", arguments: { ...psAddr, type: "Wait" } });
    assert.deepEqual(lastReq().params, { ...psAddr, type: "Wait" });
    const steps8 = Array.from({ length: 8 }, (_, i) => ({ action: i, choice: 0 }));
    await client.callTool({ name: "set_action", arguments: { ...objAddr, path: steps8, index: 2, targetFlags: ["Source", "Advanced"] } });
    assert.deepEqual(lastReq().params, { ...objAddr, path: steps8, index: 2, targetFlags: ["Source", "Advanced"] });
    await client.callTool({ name: "set_action", arguments: { ...psAddr, index: 0, parameters: nestedParams } });
    assert.deepEqual(lastReq().params.parameters, nestedParams);
    await client.callTool({ name: "set_action", arguments: { ...objAddr, index: 1, targetFlags: 5 } });
    assert.equal(lastReq().params.targetFlags, 5);
    await client.callTool({ name: "remove_action", arguments: { ...objAddr, index: 1 } });
    assert.deepEqual(lastReq(), { method: "remove_action", params: { ...objAddr, index: 1 } });
    await client.callTool({ name: "move_action", arguments: { ...psAddr, index: 2, newIndex: 0 } });
    assert.deepEqual(lastReq(), { method: "move_action", params: { ...psAddr, index: 2, newIndex: 0 } });
    await client.callTool({ name: "add_trigger", arguments: { guid: "g", type: "Timer", parameters: { m_interval: 2 } } });
    assert.deepEqual(lastReq(), { method: "add_trigger", params: { guid: "g", type: "Timer", parameters: { m_interval: 2 } } });
    await client.callTool({ name: "remove_trigger", arguments: { guid: "g", trigger: "Triggers[1]" } });
    assert.deepEqual(lastReq(), { method: "remove_trigger", params: { guid: "g", trigger: "Triggers[1]" } });
    const stArgs = { guid: "g", trigger: "Triggers[0]", type: "Hit", parameters: { hitType: "CollisionEnter, CollisionStay" } };
    await client.callTool({ name: "set_trigger", arguments: stArgs });
    assert.deepEqual(lastReq().params, stArgs);
    await client.callTool({ name: "set_trigger", arguments: { scope: "playerSettings", trigger: "tickTrigger", parameters: { m_interval: { min: 0.5 } } } });
    assert.deepEqual(lastReq().params.parameters, { m_interval: { min: 0.5 } });

    // list tools: with and without address
    await client.callTool({ name: "list_action_types", arguments: {} });
    assert.deepEqual(lastReq(), { method: "list_action_types", params: {} });
    await client.callTool({ name: "list_action_types", arguments: objAddr });
    assert.deepEqual(lastReq().params, objAddr);
    await client.callTool({ name: "list_action_types", arguments: psAddr });
    assert.deepEqual(lastReq().params, psAddr);
    await client.callTool({ name: "list_trigger_types", arguments: {} });
    assert.deepEqual(lastReq(), { method: "list_trigger_types", params: {} });
    await rejects("list_action_types", { guid: "g" }, "address key without trigger");
    await rejects("list_action_types", { scope: "playerSettings" }, "scope without trigger");
    await rejects("list_action_types", { trigger: "OnEnterActions" }, "trigger without guid or scope");

    // get_action_graph: both forms
    await client.callTool({ name: "get_action_graph", arguments: { guid: "g" } });
    assert.deepEqual(lastReq(), { method: "get_action_graph", params: { guid: "g" } });
    await client.callTool({ name: "get_action_graph", arguments: { guid: "g", component: "ModLogic" } });
    assert.deepEqual(lastReq().params, { guid: "g", component: "ModLogic" });
    await client.callTool({ name: "get_action_graph", arguments: { scope: "playerSettings" } });
    assert.deepEqual(lastReq().params, { scope: "playerSettings" });
    await rejects("get_action_graph", {}, "neither form");
    await rejects("get_action_graph", { scope: "object" }, "object scope without guid");
    await rejects("get_action_graph", { scope: "playerSettings", guid: "g" }, "playerSettings with guid");
    await rejects("get_action_graph", { scope: "bogus" }, "bad scope");

    // rejected before reaching the plugin
    await rejects("add_action", { type: "Wait" }, "no address at all");
    await rejects("add_action", { guid: "g", type: "Wait" }, "guid without trigger");
    await rejects("add_action", { trigger: "OnEnterActions", type: "Wait" }, "trigger without guid or scope");
    await rejects("add_action", { scope: "playerSettings", guid: "g", trigger: "tickTrigger", type: "Wait" }, "both address forms mixed");
    await rejects("add_action", { ...objAddr }, "missing type");
    await rejects("add_action", { ...objAddr, type: "" }, "empty type");
    await rejects("add_action", { ...objAddr, type: "Wait", index: -1 }, "negative index");
    await rejects("add_action", { ...objAddr, type: "Wait", index: 1.5 }, "fractional index");
    await rejects("add_action", { ...objAddr, type: "Wait", path: [...steps8, { action: 0, choice: 0 }] }, "path of 9 steps");
    await rejects("add_action", { ...objAddr, type: "Wait", path: [{ action: 0 }] }, "path step without choice");
    await rejects("add_action", { ...objAddr, type: "Wait", path: [{ action: -1, choice: 0 }] }, "negative path action");
    await client.callTool({ name: "add_action", arguments: { ...objAddr, type: "Wait", parameters: {} } });
    assert.deepEqual(lastReq().params.parameters, {}, "empty parameters object is allowed");
    await rejects("add_action", { ...objAddr, type: "Wait", targetFlags: 8 }, "targetFlags 8");
    await rejects("remove_action", { ...objAddr }, "remove_action without index");
    await rejects("remove_action", { ...objAddr, index: -1 }, "remove_action negative index");
    await rejects("move_action", { ...objAddr, index: 0 }, "move_action without newIndex");
    await rejects("move_action", { ...objAddr, index: 0, newIndex: -1 }, "move_action negative newIndex");
    await rejects("set_action", { ...objAddr, index: 0 }, "set_action with nothing to set");
    await rejects("set_action", { ...objAddr, parameters: { a: 1 } }, "set_action without index");
    await rejects("add_trigger", {}, "add_trigger without guid");
    await rejects("remove_trigger", { guid: "g" }, "remove_trigger without trigger");
    await rejects("set_trigger", { guid: "g", trigger: "Triggers[0]" }, "set_trigger with nothing to set");

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
  assert.ok(!stderrText.includes("e2etoken") && !stderrText.includes("rotated"), "token must not appear on stderr");
  assert.match(stderrText, /ready/);
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
