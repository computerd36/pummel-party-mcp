import { test } from "node:test";
import assert from "node:assert/strict";
import net from "node:net";
import http from "node:http";
import { join } from "node:path";
import { PluginClient, PluginError, MAX_RESPONSE_BYTES, optionsFromEnv, WRITE_AMBIGUITY_NOTE } from "../dist/client.js";
import { startFake, makeTempDir, removeDir, writeToken } from "./fake-plugin.mjs";

const NOTE = /may or may not have been applied/;

async function withFake(fakeOpts, fn, token = "tok") {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, token);
  const fake = await startFake({ token, ...fakeOpts });
  try {
    await fn({ fake, tf, dir });
  } finally {
    await fake.close();
    removeDir(dir);
  }
}

async function freePort() {
  return new Promise((resolve) => {
    const s = net.createServer().listen(0, "127.0.0.1", () => {
      const p = s.address().port;
      s.close(() => resolve(p));
    });
  });
}

test("a redirect is refused and nothing is forwarded to the redirect target", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "redirsecret");
  const target = await startFake({ token: "redirsecret" });
  const redirector = http.createServer((req, res) => {
    req.resume();
    res.writeHead(307, { Location: `http://127.0.0.1:${target.port}/rpc` });
    res.end();
  });
  await new Promise((r) => redirector.listen(0, "127.0.0.1", r));
  const port = redirector.address().port;
  try {
    const c = new PluginClient({ port, tokenFile: tf });
    await assert.rejects(c.call("save_mod", {}, 2000, { write: true }), (e) => {
      assert.equal(
        e.message,
        `the listener on 127.0.0.1:${port} answered with a redirect; it is not the pummel-party-mcp plugin.`,
      );
      assert.ok(!e.message.includes("redirsecret"));
      return true;
    });
    await new Promise((r) => setTimeout(r, 200));
    assert.equal(target.requests.length, 0, "the redirect target must receive nothing");
  } finally {
    await target.close();
    redirector.closeAllConnections?.();
    await new Promise((r) => redirector.close(r));
    removeDir(dir);
  }
});

test("write tool: client-side timeout sends the request exactly once and warns", async () => {
  await withFake({ delayMs: 1500 }, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("set_object", { guid: "g" }, 300, { write: true }), (e) => {
      assert.equal(e.code, "TIMEOUT");
      assert.match(e.message, /timed out/);
      assert.ok(e.message.includes(WRITE_AMBIGUITY_NOTE));
      return true;
    });
    assert.equal(fake.requests.length, 1);
  });
});

test("read tool: timeout has no ambiguity note", async () => {
  await withFake({ delayMs: 1500 }, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("get_object", { guid: "g" }, 300, { write: false }), (e) => {
      assert.match(e.message, /timed out/);
      assert.doesNotMatch(e.message, NOTE);
      return true;
    });
  });
});

test("write tool: connection reset sends once and warns; a read tool does not", async () => {
  const raw = (req) => {
    req.socket.destroy();
    return true;
  };
  await withFake({ raw }, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("delete_object", { guid: "g" }, 2000, { write: true }), (e) => {
      assert.match(e.message, /connection to the pummel-party-mcp plugin was lost/);
      assert.match(e.message, NOTE);
      return true;
    });
    assert.equal(fake.requests.length, 1, "no automatic retry of a write");
    await assert.rejects(c.call("get_object", { guid: "g" }, 2000, { write: false }), (e) => {
      assert.match(e.message, /was lost/);
      assert.doesNotMatch(e.message, NOTE);
      return true;
    });
  });
});

test("write tool: body-read failure after the headers warns", async () => {
  const raw = (req, res) => {
    res.writeHead(200, { "Content-Type": "application/json", "Content-Length": "1000" });
    res.write('{"ok":tr');
    setTimeout(() => req.socket.destroy(), 50);
    return true;
  };
  await withFake({ raw }, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("set_object", { guid: "g" }, 2000, { write: true }), (e) => {
      assert.match(e.message, /while reading the response/);
      assert.match(e.message, NOTE);
      return true;
    });
    assert.equal(fake.requests.length, 1);
  });
});

test("write tool: connection refused has no ambiguity note", async () => {
  const dir = makeTempDir();
  const tf = join(dir, "tok");
  writeToken(tf, "t");
  const port = await freePort();
  try {
    const c = new PluginClient({ port, tokenFile: tf });
    await assert.rejects(c.call("set_object", { guid: "g" }, 1000, { write: true }), (e) => {
      assert.match(e.message, /not running/);
      assert.doesNotMatch(e.message, NOTE);
      return true;
    });
  } finally {
    removeDir(dir);
  }
});

test("write tool: 401 then 200 retries once after the token re-read and executes once", async () => {
  await withFake({}, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await c.call("ping");
    fake.token = "rotated";
    writeToken(tf, "rotated");
    const before = fake.requests.length;
    const execBefore = fake.executions;
    const r = await c.call("set_object", { guid: "g" }, 2000, { write: true });
    assert.equal(r.changed, true);
    assert.equal(fake.requests.length - before, 2, "401, then one retry");
    assert.equal(fake.executions - execBefore, 1, "the write executed exactly once");
  });
});

test("oversized response is aborted with a clear error", async () => {
  const raw = (req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    const chunk = Buffer.alloc(1024 * 1024, 0x61);
    let n = 0;
    const iv = setInterval(() => {
      if (res.destroyed || n++ > MAX_RESPONSE_BYTES / chunk.length + 4) {
        clearInterval(iv);
        res.end();
        return;
      }
      res.write(chunk);
    }, 1);
    res.on("close", () => clearInterval(iv));
    return true;
  };
  await withFake({ raw }, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("get_hierarchy", {}, 20000), (e) => {
      assert.match(e.message, /larger than 8 MB/);
      return true;
    });
  });
});

test("oversized Content-Length is refused before reading", async () => {
  const raw = (req, res) => {
    res.writeHead(200, { "Content-Type": "application/json", "Content-Length": String(MAX_RESPONSE_BYTES + 1) });
    res.write("{");
    return true;
  };
  await withFake({ raw }, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("get_hierarchy", {}, 5000), /larger than 8 MB/);
  });
});

test("malformed body: not JSON, and JSON that is not an envelope", async () => {
  let mode = "text";
  const raw = (req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(mode === "text" ? "<html>nope</html>" : '{"hello":1}');
    return true;
  };
  await withFake({ raw }, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    await assert.rejects(c.call("ping"), /not valid JSON/);
    mode = "json";
    await assert.rejects(c.call("ping"), /Unexpected response from plugin \(HTTP 200\)/);
    // a write whose answer cannot be understood may have been applied
    await assert.rejects(c.call("set_object", {}, 2000, { write: true }), NOTE);
  });
});

test("non-string error.message and error.code are stringified", async () => {
  let err;
  await withFake({ handler: () => ({ ok: false, error: err }) }, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    err = { code: "INVALID_PARAMS", message: { field: "x", n: 2 } };
    await assert.rejects(c.call("ping"), (e) => {
      assert.ok(e.message.startsWith('INVALID_PARAMS: {"field":"x","n":2}'), e.message);
      return true;
    });
    err = { code: "INTERNAL" };
    await assert.rejects(c.call("ping"), (e) => {
      assert.ok(e.message.startsWith("INTERNAL: "), e.message);
      assert.doesNotMatch(e.message, /undefined|\[object/);
      return true;
    });
    err = { code: 42, message: ["a", "b"] };
    await assert.rejects(c.call("ping"), (e) => {
      assert.ok(e.message.startsWith('42: ["a","b"]'), e.message);
      return true;
    });
  });
});

test("HTTP status hints: 503, 413, 408, 431 and the 403 body text", async () => {
  let status = 503;
  let body = "";
  const raw = (req, res) => {
    res.writeHead(status, { "Content-Type": "application/json" });
    res.end(body);
    return true;
  };
  await withFake({ raw }, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    const cases = [
      [503, JSON.stringify({ ok: false, error: { code: "SERVER_BUSY", message: "busy" } }), /SERVER_BUSY: busy[\s\S]*retry/],
      [503, "", /HTTP 503[\s\S]*busy[\s\S]*retry/],
      [413, JSON.stringify({ ok: false, error: { code: "PAYLOAD_TOO_LARGE", message: "big" } }), /PAYLOAD_TOO_LARGE: big[\s\S]*1 MB/],
      [413, "", /HTTP 413[\s\S]*1 MB/],
      [408, JSON.stringify({ ok: false, error: { code: "REQUEST_TIMEOUT", message: "slow" } }), /REQUEST_TIMEOUT: slow/],
      [408, "", /HTTP 408/],
      [431, "", /HTTP 431/],
      [403, JSON.stringify({ ok: false, error: { code: "FORBIDDEN", message: "origin not allowed" } }), /HTTP 403: origin not allowed/],
      [403, "plain forbidden text", /HTTP 403: plain forbidden text/],
    ];
    for (const [st, b, re] of cases) {
      status = st;
      body = b;
      await assert.rejects(c.call("ping"), (e) => {
        assert.match(e.message, re, `${st} ${b}`);
        return true;
      });
    }
  });
});

test("a listener that echoes the token cannot make it appear in errors", async () => {
  const raw = (req, res) => {
    const t = req.headers["x-pummelpartymcp-token"];
    res.writeHead(403, { "Content-Type": "text/plain" });
    res.end(`bad request, token was ${t}`);
    return true;
  };
  await withFake(
    { raw },
    async ({ fake, tf }) => {
      const c = new PluginClient({ port: fake.port, tokenFile: tf });
      await assert.rejects(c.call("ping"), (e) => {
        assert.ok(!e.message.includes("echoedsecret"), e.message);
        return true;
      });
    },
    "echoedsecret",
  );
});

test("timeout is observable: client option and PUMMEL_PARTY_MCP_TIMEOUT_MS", async () => {
  assert.equal(optionsFromEnv({ PUMMEL_PARTY_MCP_TIMEOUT_MS: "250" }).timeoutMs, 250);
  assert.equal(optionsFromEnv({ PUMMEL_PARTY_MCP_TIMEOUT_MS: "abc" }).timeoutMs, undefined);
  assert.equal(optionsFromEnv({}).timeoutMs, undefined);
  await withFake({ delayMs: 2000 }, async ({ fake, tf }) => {
    // the option overrides the per-call value
    const c = new PluginClient({ port: fake.port, tokenFile: tf, timeoutMs: 300 });
    const t0 = Date.now();
    await assert.rejects(c.call("ping", {}, 30000), (e) => {
      assert.ok(e instanceof PluginError);
      assert.match(e.message, /timed out after 0\.3s/);
      return true;
    });
    const dt = Date.now() - t0;
    assert.ok(dt >= 250 && dt < 1500, `elapsed ${dt} ms`);
  });
});

test("the abort timer stays armed while the body is read (slow body times out)", async () => {
  const raw = (req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.write('{"ok":true,');
    return true; // never finishes
  };
  await withFake({ raw }, async ({ fake, tf }) => {
    const c = new PluginClient({ port: fake.port, tokenFile: tf });
    const t0 = Date.now();
    await assert.rejects(c.call("get_log", {}, 400), /timed out/);
    assert.ok(Date.now() - t0 < 3000);
  });
});
