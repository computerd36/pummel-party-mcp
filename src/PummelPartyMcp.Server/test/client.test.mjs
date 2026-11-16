import { test } from "node:test";
import assert from "node:assert/strict";
import net from "node:net";
import { join } from "node:path";
import { PluginClient, isValidTestModName } from "../dist/client.js";
import { startFake, makeTempDir, removeDir, writeToken } from "./fake-plugin.mjs";

async function freePort() {
  return new Promise((resolve) => {
    const s = net.createServer().listen(0, "127.0.0.1", () => {
      const p = s.address().port;
      s.close(() => resolve(p));
    });
  });
}

test("success round-trip sends method, params, token, no Origin", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "abc123");
  const fake = await startFake({ token: "abc123" });
  try {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    const r = await c.call("get_object", { guid: "G1" });
    assert.deepEqual(r, { guid: "G1", components: [] });
    const req = fake.requests[0];
    assert.equal(req.method, "POST");
    assert.equal(req.url, "/rpc");
    assert.equal(req.headers["x-pummelpartymcp-token"], "abc123");
    assert.equal(req.headers["content-type"], "application/json");
    assert.equal(req.headers.origin, undefined);
    assert.deepEqual(JSON.parse(req.body), { method: "get_object", params: { guid: "G1" } });
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("ok:false maps to CODE: message plus hint", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  const fake = await startFake({
    token: "t",
    handler: () => ({ ok: false, error: { code: "EDITOR_NOT_OPEN", message: "no editor" } }),
  });
  try {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("get_status"), (e) => {
      assert.match(e.message, /^EDITOR_NOT_OPEN: no editor/);
      assert.match(e.message, /Mod Editor from the game's main menu/);
      return true;
    });
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("a write cut off by the editor closing says to check first, not 'then retry'", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  const message = "the Mod Editor was closed while the call was running; a write may have partly applied";
  const fake = await startFake({
    token: "t",
    handler: () => ({ ok: false, error: { code: "EDITOR_CLOSED_DURING_CALL", message } }),
  });
  try {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("set_property", { guid: "g" }, 1000, { write: true }), (e) => {
      assert.equal(e.code, "EDITOR_CLOSED_DURING_CALL");
      assert.ok(e.message.startsWith(`EDITOR_CLOSED_DURING_CALL: ${message}
Hint: `), e.message);
      assert.match(e.message, /Do not retry blindly/);
      assert.doesNotMatch(e.message, /main menu, then retry/);
      return true;
    });
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("401 re-reads token file and retries once", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "old");
  const fake = await startFake({ token: "old" });
  try {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await c.call("ping");
    // simulate game restart: new token
    fake.token = "new";
    writeToken(tf, "new");
    const r = await c.call("ping");
    assert.equal(r.pong, true);
    const last = fake.requests.slice(-2);
    assert.equal(last[0].headers["x-pummelpartymcp-token"], "old");
    assert.equal(last[1].headers["x-pummelpartymcp-token"], "new");
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("persistent 401 reports token mismatch without leaking token", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "secretvalue");
  const fake = await startFake({ token: "other" });
  try {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("ping"), (e) => {
      assert.match(e.message, /Token mismatch/);
      assert.ok(!e.message.includes("secretvalue"));
      return true;
    });
    assert.equal(fake.requests.length, 2);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("connection refused message", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  const port = await freePort();
  try {
    const c = new PluginClient({ port, tokenFile: tf });
    await assert.rejects(c.call("ping"), (e) => {
      assert.equal(
        e.message,
        `No answer from the pummel-party-mcp plugin on 127.0.0.1:${port}. Start Pummel Party and open the Mod Editor from the main menu: the plugin only listens while the Mod Editor is open.`,
      );
      return true;
    });
  } finally {
    removeDir(dir);
  }
});

test("missing token file message names the path", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "does-not-exist.token");
  try {
    const c = new PluginClient({ port: 1, tokenFile: tf });
    await assert.rejects(c.call("ping"), (e) => {
      assert.ok(e.message.includes(tf));
      assert.match(e.message, /Mod Editor must have been opened at least once/);
      return true;
    });
  } finally {
    removeDir(dir);
  }
});

test("timeout", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  const fake = await startFake({ token: "t", delayMs: 1500 });
  try {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("ping", {}, 300), (e) => {
      assert.match(e.message, /timed out/);
      assert.equal(e.code, "TIMEOUT");
      return true;
    });
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("new error codes map to hints", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  let code = "";
  const fake = await startFake({ token: "t", handler: () => ({ ok: false, error: { code, message: "m" } }) });
  const expected = {
    WRITES_DISABLED: /Safety\.EnableWrites/,
    PUBLISH_BLOCK_INCOMPLETE: /safety patches/,
    GUARD_NOT_TEST_MOD: /do not retry with a different name/,
    INVALID_STATE: /required state/,
    ALREADY_EXISTS: /already exists/,
    ASSET_IN_USE: /references/,
    IMPORT_FAILED: /removed its copy/,
    LINK_REFUSED: /never written through/,
    TIMEOUT: /safe to retry.*check get_status and list_mods, do not retry blindly/s,
    EDITOR_CLOSED_DURING_CALL: /may have partly applied.*Do not retry blindly.*get_status \/ get_object/s,
  };
  try {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    for (const [k, re] of Object.entries(expected)) {
      code = k;
      await assert.rejects(c.call("save_mod"), (e) => {
        assert.equal(e.code, k);
        assert.ok(e.message.startsWith(`${k}: m\nHint: `), e.message);
        assert.match(e.message, re);
        return true;
      });
    }
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

// near misses and the old sandbox prefix (PB_TEST_), which is no longer accepted
const BAD_NAMES = ["MyMod", "mcp_x", "Mcp_x", "MCP_", "MCP", "MCPx", " MCP_x", "MCP_x ", "MCP_a/b", "MCP_a\\b", "MCP_a\n", "MCP_" + "a".repeat(61), "\u041CCP_x", "M\u0421P_x", "MC\u0420_x", "\u039CCP_x", "\uFF2DCP_x", "MCP\uFF3Fx", "MCP_\uFF58", "PB_TEST_x", "PB_TEST_Sandbox", "pb_test_x"];

test("name rule", () => {
  for (const n of BAD_NAMES) {
    assert.equal(isValidTestModName(n), false, JSON.stringify(n));
  }
  for (const n of ["MCP_x", "MCP_a b-c_1", "MCP_" + "a".repeat(60)]) assert.equal(isValidTestModName(n), true, n);
});
