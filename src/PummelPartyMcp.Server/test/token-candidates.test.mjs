import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, writeFileSync, utimesSync } from "node:fs";
import { join } from "node:path";
import { PluginClient, PluginError, optionsFromEnv, WRITE_AMBIGUITY_NOTE } from "../dist/client.js";
import { appDataDir, modManagerTokenFiles, resolveTokenCandidates, NO_GAME_FOUND_MESSAGE, NO_SEARCH_MESSAGE } from "../dist/steam.js";
import { startFake, makeTempDir, removeDir, defaultHandler } from "./fake-plugin.mjs";

// The candidate search is told it runs on "win32" and reads APPDATA (or USERPROFILE) from the injected env, so these tests use temp
// folders only and behave the same on every OS (node:path joins them with the local separator).
const WIN = { platform: "win32" };
const TAIL = ["BepInEx", "config", "PummelPartyMcp.token"];

/** A well-formed token (64 lowercase hex characters) made of one repeated hex digit. */
const tok = (c) => c.repeat(64);
const API = { required: 2, serverVersion: "test" };

/** Writes <base>/<...parts>/BepInEx/config/PummelPartyMcp.token and returns its path. */
function tokenAt(base, parts, token = tok("0"), mtimeSec) {
  const dir = join(base, ...parts, "BepInEx", "config");
  mkdirSync(dir, { recursive: true });
  const file = join(dir, TAIL[2]);
  writeFileSync(file, token + "\n");
  if (mtimeSec !== undefined) utimesSync(file, mtimeSec, mtimeSec);
  return file;
}

const TSMM = (p) => ["Thunderstore Mod Manager", "DataFolder", "PummelParty", "profiles", p];
const R2 = (p) => ["r2modmanPlus-local", "PummelParty", "profiles", p];

/** No message may contain a token (or anything token-shaped). */
function assertNoToken(message) {
  assert.doesNotMatch(message, /[0-9a-f]{64}/);
}

test("modManagerTokenFiles: Thunderstore Mod Manager and r2modman, several profiles, existing files only", () => {
  const dir = makeTempDir();
  try {
    const a = tokenAt(dir, TSMM("Default"));
    const b = tokenAt(dir, TSMM("Second profile"));
    mkdirSync(join(dir, ...TSMM("NoToken"), "BepInEx", "config"), { recursive: true }); // no token file: ignored
    const c = tokenAt(dir, R2("Default"));
    tokenAt(dir, ["com.kesomannen.gale", "pummel-party", "profiles", "Default"]); // Gale is not searched
    tokenAt(dir, ["Thunderstore Mod Manager", "DataFolder", "LethalCompany", "profiles", "Default"]); // another game: ignored
    const found = modManagerTokenFiles({ APPDATA: dir }, WIN);
    assert.deepEqual([...found].sort(), [a, b, c].sort());
  } finally {
    removeDir(dir);
  }
});

test("modManagerTokenFiles: each manager on its own; no APPDATA, other platforms and unreadable folders give nothing and never throw", () => {
  const dir = makeTempDir();
  try {
    for (const parts of [TSMM("Default"), R2("Default")]) {
      const d = makeTempDir();
      const f = tokenAt(d, parts);
      try {
        assert.deepEqual(modManagerTokenFiles({ APPDATA: d }, WIN), [f]);
        assert.deepEqual(modManagerTokenFiles({ APPDATA: d }, { platform: "linux" }), []);
        assert.deepEqual(modManagerTokenFiles({}, WIN), []);
      } finally {
        removeDir(d);
      }
    }
    assert.deepEqual(modManagerTokenFiles({ APPDATA: join(dir, "missing") }, WIN), []);
    const boom = () => {
      throw new Error("denied");
    };
    assert.deepEqual(modManagerTokenFiles({ APPDATA: dir }, { ...WIN, listDirs: boom }), []);
    tokenAt(dir, TSMM("Default"));
    assert.deepEqual(modManagerTokenFiles({ APPDATA: dir }, { ...WIN, fs: { existsSync: boom, readFileSync: boom } }), []);
  } finally {
    removeDir(dir);
  }
});

test("appDataDir: an absolute APPDATA, else <USERPROFILE>/AppData/Roaming; a relative APPDATA is ignored", () => {
  const home = makeTempDir();
  try {
    const roaming = join(home, "AppData", "Roaming");
    assert.equal(appDataDir({ APPDATA: home, USERPROFILE: "elsewhere" }), home);
    assert.equal(appDataDir({ USERPROFILE: home }), roaming);
    assert.equal(appDataDir({ APPDATA: "relative", USERPROFILE: home }), roaming);
    assert.equal(appDataDir({ APPDATA: "relative" }), undefined);
    assert.equal(appDataDir({ USERPROFILE: "relative" }), undefined);
    assert.equal(appDataDir({}), undefined);
    const f = tokenAt(roaming, R2("Default"));
    assert.deepEqual(modManagerTokenFiles({ USERPROFILE: home }, WIN), [f]);
    assert.deepEqual(modManagerTokenFiles({ APPDATA: "relative", USERPROFILE: home }, WIN), [f]);
  } finally {
    removeDir(home);
  }
});

function makeSteamGame(dir, withToken) {
  const root = join(dir, "Steam");
  const game = join(root, "steamapps", "common", "Pummel Party");
  mkdirSync(game, { recursive: true });
  writeFileSync(join(root, "steamapps", "appmanifest_880940.acf"), `"AppState"\n{\n\t"appid"\t"880940"\n\t"installdir"\t"Pummel Party"\n}\n`);
  return { root, game, file: withToken ? tokenAt(game, []) : join(game, ...TAIL) };
}

test("resolveTokenCandidates: Steam token first, then mod-manager tokens; a missing Steam token is only the fallback", () => {
  const dir = makeTempDir();
  const apd = join(dir, "appdata");
  try {
    const s = makeSteamGame(dir, true);
    const m = tokenAt(apd, TSMM("Default"));
    const deps = { ...WIN, steamRoots: [s.root] };
    assert.deepEqual(resolveTokenCandidates({ APPDATA: apd }, deps), [s.file, m]);
    assert.deepEqual(resolveTokenCandidates({}, deps), [s.file]);
    const d2 = makeTempDir();
    try {
      const s2 = makeSteamGame(d2, false); // game found, token missing
      assert.deepEqual(resolveTokenCandidates({}, { ...WIN, steamRoots: [s2.root] }), [s2.file], "kept alone so the error can name it");
      const withMods = resolveTokenCandidates({ APPDATA: apd }, { ...WIN, steamRoots: [s2.root] });
      assert.deepEqual(withMods, [m], "a missing Steam token is dropped when mod-manager tokens exist");
    } finally {
      removeDir(d2);
    }
  } finally {
    removeDir(dir);
  }
});

test("resolveTokenCandidates: explicit settings give one file and no search", () => {
  const dir = makeTempDir();
  try {
    const m = tokenAt(dir, TSMM("Default"));
    const deps = { ...WIN, steamRoots: [] };
    assert.deepEqual(modManagerTokenFiles({ APPDATA: dir }, WIN), [m]);
    assert.deepEqual(resolveTokenCandidates({ APPDATA: dir, PUMMEL_PARTY_MCP_TOKEN_FILE: "T" }, deps), ["T"]);
    assert.deepEqual(resolveTokenCandidates({ APPDATA: dir, PUMMEL_PARTY_MCP_GAME_DIR: join(dir, "G") }, deps), [join(dir, "G", ...TAIL)]);
    assert.deepEqual(resolveTokenCandidates({ APPDATA: dir, PUMMEL_PARTY_MCP_TOKEN_FILE: "T", PUMMEL_PARTY_MCP_GAME_DIR: "G" }, deps), ["T"]);
  } finally {
    removeDir(dir);
  }
});

test("resolveTokenCandidates: nothing found says where it looked; off Windows it says nothing is searched; both name the variables", () => {
  const dir = makeTempDir();
  try {
    assert.throws(() => resolveTokenCandidates({ APPDATA: dir }, { ...WIN, steamRoots: [] }), (e) => {
      assert.equal(e.message, NO_GAME_FOUND_MESSAGE);
      assert.match(e.message, /Steam game folder/);
      assert.match(e.message, /Thunderstore Mod Manager and r2modman/);
      assert.doesNotMatch(e.message, /Gale/);
      assert.match(e.message, /Mod Editor was opened once/);
      return true;
    });
    assert.throws(() => resolveTokenCandidates({ APPDATA: dir }, { platform: "linux" }), (e) => {
      assert.equal(e.message, NO_SEARCH_MESSAGE);
      assert.match(e.message, /only runs on Windows/);
      return true;
    });
    for (const m of [NO_GAME_FOUND_MESSAGE, NO_SEARCH_MESSAGE]) {
      assert.match(m, /PUMMEL_PARTY_MCP_TOKEN_FILE/);
      assert.match(m, /PUMMEL_PARTY_MCP_GAME_DIR/);
    }
  } finally {
    removeDir(dir);
  }
});

// ---- selection against the fake plugin (ephemeral ports) ----

/** tokens: [[profile name, token, mtime in seconds]] -> one token file per profile folder. */
function setup(dir, tokens) {
  return tokens.map(([name, token, mtime]) => tokenAt(dir, ["profiles", name], token, mtime));
}
const sent = (fake) => fake.requests.map((r) => r.headers["x-pummelpartymcp-token"]);
const methods = (fake) => fake.requests.map((r) => JSON.parse(r.body).method);

/** Counts the executed (authorised) calls per method; `ping` keeps the default reply unless `api` is given. */
function counting(api) {
  const counts = {};
  const handler = (method, params) => {
    counts[method] = (counts[method] ?? 0) + 1;
    if (method === "ping" && api) return { ok: true, result: { pong: true, pluginVersion: "0.0.1", apiVersion: api() } };
    return defaultHandler(method, params);
  };
  return { counts, handler };
}

test("several candidates: the newest file is tried first and an accepted token is used", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("a") });
  try {
    const files = setup(dir, [
      ["old", tok("b"), 1_000_000],
      ["new", tok("a"), 2_000_000],
      ["mid", tok("c"), 1_500_000],
    ]);
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => files });
    assert.equal((await c.call("ping")).pong, true);
    assert.deepEqual(sent(fake), [tok("a"), tok("a")], "probe with the newest, then the call");
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: a 401 moves on to the next file, newest to oldest", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("b") });
  try {
    const files = setup(dir, [
      ["old", tok("b"), 1_000_000],
      ["new", tok("a"), 2_000_000],
      ["mid", tok("c"), 1_500_000],
    ]);
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => files });
    assert.equal((await c.call("ping")).pong, true);
    assert.deepEqual(sent(fake), [tok("a"), tok("c"), tok("b"), tok("b")]);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: equal modification times keep the search order and still find the accepted file", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("b") });
  try {
    const files = setup(dir, [
      ["x", tok("a"), 1_000_000],
      ["y", tok("b"), 1_000_000],
      ["z", tok("c"), 1_000_000],
    ]);
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => files });
    assert.equal((await c.call("ping")).pong, true);
    assert.deepEqual(sent(fake), [tok("a"), tok("b"), tok("b")]);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: the accepted file is cached, later calls do not probe again", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("b") });
  try {
    const files = setup(dir, [
      ["old", tok("b"), 1_000_000],
      ["new", tok("a"), 2_000_000],
    ]);
    let n = 0;
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => (n++, files) });
    await c.call("ping");
    const after = fake.requests.length; // rejected probe, accepted probe, the call
    assert.equal(after, 3);
    await c.call("ping");
    await c.call("get_object", { guid: "G" });
    assert.equal(n, 1);
    assert.deepEqual(sent(fake).slice(after), [tok("b"), tok("b")]);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: calls made at the same time share one detection", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("a") });
  try {
    const files = setup(dir, [
      ["a", tok("a"), 2_000_000],
      ["b", tok("b"), 1_000_000],
    ]);
    let n = 0;
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => (n++, files) });
    const results = await Promise.all([1, 2, 3].map((i) => c.call("get_object", { guid: `G${i}` })));
    assert.deepEqual(results.map((r) => r.guid), ["G1", "G2", "G3"]);
    assert.equal(n, 1, "one search");
    assert.deepEqual(methods(fake).sort(), ["get_object", "get_object", "get_object", "ping"], "one probe");
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: all rejected lists the paths and both variables, never a token, and detects again next time", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("d") });
  try {
    const files = setup(dir, [
      ["a", tok("a"), 1_000_000],
      ["b", tok("b"), 2_000_000],
    ]);
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => files });
    await assert.rejects(c.call("ping"), (e) => {
      assert.ok(e instanceof PluginError);
      assert.equal(e.maybeApplied, false);
      for (const f of files) assert.ok(e.message.includes(f), e.message);
      assert.match(e.message, /PUMMEL_PARTY_MCP_TOKEN_FILE/);
      assert.match(e.message, /PUMMEL_PARTY_MCP_GAME_DIR/);
      assert.match(e.message, /the tool call was not sent/);
      assertNoToken(e.message);
      return true;
    });
    assert.equal(fake.requests.length, 2);
    // the right token appears (the game wrote it): the next call finds it
    writeFileSync(files[0], tok("d") + "\n");
    utimesSync(files[0], 3_000_000, 3_000_000);
    assert.equal((await c.call("ping")).pong, true);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: the error lists at most 5 paths, then how many more", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("f") });
  try {
    const files = setup(
      dir,
      ["1", "2", "3", "4", "5", "6", "7"].map((d, i) => [`p${d}`, tok(d), 1_000_000 + i]),
    );
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => files });
    await assert.rejects(c.call("ping"), (e) => {
      assert.equal(files.filter((f) => e.message.includes(f)).length, 5);
      assert.match(e.message, /and 2 more/);
      assertNoToken(e.message);
      return true;
    });
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: malformed files are skipped without being sent; a valid older file is used", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("a") });
  try {
    const files = setup(dir, [
      ["newline", tok("b").slice(0, 32) + "\n" + tok("b").slice(32), 5_000_000],
      ["nul", tok("c").slice(0, 63) + "\0", 4_000_000],
      ["latin", "ü".repeat(64), 3_000_000],
      ["upper", tok("A"), 2_500_000],
      ["empty", "", 2_000_000],
      ["valid", tok("a"), 1_000_000],
    ]);
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => files });
    assert.equal((await c.call("ping")).pong, true);
    assert.deepEqual(sent(fake), [tok("a"), tok("a")], "only the well-formed token was ever sent");
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: all malformed or empty gives an error with the paths, no contents and no request", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("a") });
  try {
    const files = setup(dir, [
      ["one", "secret-content-one", 2_000_000],
      ["two", "", 1_000_000],
    ]);
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => files });
    await assert.rejects(c.call("set_property", { guid: "G" }, undefined, { write: true }), (e) => {
      assert.equal(e.maybeApplied, false);
      assert.match(e.message, /empty, malformed or unreadable/);
      assert.match(e.message, /Mod Editor/);
      assert.match(e.message, /the tool call was not sent/);
      for (const f of files) assert.ok(e.message.includes(f), e.message);
      assert.ok(!e.message.includes("secret-content"));
      assert.ok(!e.message.includes(WRITE_AMBIGUITY_NOTE));
      return true;
    });
    assert.equal(fake.requests.length, 0);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

/** A plugin that accepts connections and drops them (it does not answer) while `down` is true. */
function droppingFake(opts) {
  return startFake({
    ...opts,
    raw: (req, _res, _rec, state) => {
      if (!state.down) return false;
      req.socket.destroy();
      return true;
    },
  });
}

test("several candidates: no answer from the plugin keeps the candidates (no new search), chooses nothing and says the call was not sent", async () => {
  const dir = makeTempDir();
  const fake = await droppingFake({ token: tok("a") });
  fake.down = true;
  try {
    const files = setup(dir, [
      ["a", tok("a"), 1_000_000],
      ["b", tok("b"), 2_000_000],
    ]);
    let n = 0;
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => (n++, files) });
    for (let i = 1; i <= 3; i++) {
      await assert.rejects(c.call("set_property", { guid: "G" }, undefined, { write: true }), (e) => {
        assert.equal(e.maybeApplied, false);
        assert.match(e.message, /the tool call was not sent/);
        assert.ok(!e.message.includes(WRITE_AMBIGUITY_NOTE));
        assertNoToken(e.message);
        return true;
      });
    }
    assert.equal(n, 1, "the search ran once");
    assert.deepEqual(sent(fake), [tok("b"), tok("b"), tok("b")], "each call probed the newest file again");
    // the game comes up: the same client now chooses the file
    fake.down = false;
    assert.equal((await c.call("ping")).pong, true);
    assert.equal(n, 1);
    assert.deepEqual(sent(fake).slice(3), [tok("b"), tok("a"), tok("a")]);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("one candidate: connection failures do not run the search again", async () => {
  const dir = makeTempDir();
  const fake = await droppingFake({ token: tok("a") });
  fake.down = true;
  try {
    const [file] = setup(dir, [["only", tok("a"), 1_000_000]]);
    let n = 0;
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => (n++, [file]) });
    for (let i = 0; i < 3; i++) await assert.rejects(c.call("ping"));
    assert.equal(n, 1);
    fake.down = false;
    assert.equal((await c.call("ping")).pong, true);
    assert.equal(n, 1);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: a probe answered with 503 chooses nothing, says the call was not sent, and the next call probes again", async () => {
  const dir = makeTempDir();
  let busy = 1;
  const fake = await startFake({
    token: tok("a"),
    raw: (_req, res, rec) => {
      if (busy > 0 && JSON.parse(rec.body).method === "ping") {
        busy--;
        res.writeHead(503, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: false, error: { code: "SERVER_BUSY", message: "too many concurrent requests" } }));
        return true;
      }
      return false;
    },
  });
  try {
    const files = setup(dir, [
      ["a", tok("a"), 2_000_000],
      ["b", tok("b"), 1_000_000],
    ]);
    let n = 0;
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => (n++, files) });
    await assert.rejects(c.call("set_property", { guid: "G" }, undefined, { write: true }), (e) => {
      assert.equal(e.maybeApplied, false);
      assert.match(e.message, /HTTP 503/);
      assert.match(e.message, /The plugin is busy/);
      assert.match(e.message, /the tool call was not sent/);
      return true;
    });
    assert.equal(fake.requests.length, 1, "no other candidate was tried");
    assert.equal((await c.call("ping")).pong, true);
    assert.deepEqual(sent(fake), [tok("a"), tok("a"), tok("a")], "probed again, then the call");
    assert.equal(n, 1);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: a 200 that is not a ping reply is not accepted", async () => {
  const dir = makeTempDir();
  const fake = await startFake({
    token: tok("a"),
    raw: (_req, res) => {
      res.writeHead(200, { "Content-Type": "text/html" }).end("<html>hello</html>");
      return true;
    },
  });
  try {
    const files = setup(dir, [
      ["a", tok("a"), 2_000_000],
      ["b", tok("b"), 1_000_000],
    ]);
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => files });
    await assert.rejects(c.call("ping"), (e) => {
      assert.match(e.message, /HTTP 200/);
      assert.match(e.message, /something else is answering/);
      assert.match(e.message, /the tool call was not sent/);
      return true;
    });
    assert.equal(fake.requests.length, 1);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("several candidates: a later 401 chooses the file again (another profile runs now)", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("a") });
  try {
    const files = setup(dir, [
      ["a", tok("a"), 2_000_000],
      ["b", tok("b"), 1_000_000],
    ]);
    let n = 0;
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => (n++, files) });
    await c.call("ping");
    assert.equal(n, 1);
    fake.token = tok("b"); // the game was restarted from the other profile
    assert.equal((await c.call("ping")).pong, true);
    assert.equal(n, 2);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("a token file that appears later is found without a restart (after a 401, and from remembered candidates)", async () => {
  const dir = makeTempDir();
  const fake = await droppingFake({ token: tok("a") });
  try {
    const deps = { ...WIN, steamRoots: [] };
    const c = new PluginClient({ ...optionsFromEnv({ APPDATA: dir }, deps), port: fake.port });
    tokenAt(dir, TSMM("A"), tok("a"), 1_000_000);
    assert.equal((await c.call("ping")).pong, true, "the only candidate");
    // profile B is started for the first time: its token file appears and the plugin uses it
    tokenAt(dir, R2("B"), tok("b"), 2_000_000);
    fake.token = tok("b");
    assert.equal((await c.call("ping")).pong, true, "found after the 401");
    // a new client while the game is closed: it remembers the candidates A and B, the probe gets no answer
    const o = optionsFromEnv({ APPDATA: dir }, deps);
    let n = 0;
    const search = o.resolveTokenFile;
    const c2 = new PluginClient({ ...o, resolveTokenFile: () => (n++, search()), port: fake.port });
    fake.down = true;
    await assert.rejects(c2.call("ping"));
    await assert.rejects(c2.call("ping"));
    assert.equal(n, 1);
    // profile C is started for the first time: the remembered candidates are rejected, and the search runs again in the same call
    tokenAt(dir, TSMM("C"), tok("c"), 3_000_000);
    fake.token = tok("c");
    fake.down = false;
    assert.equal((await c2.call("ping")).pong, true);
    assert.equal(n, 2);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("401 then re-detection: the write is executed exactly once, and the new plugin's api version is checked first", async () => {
  const dir = makeTempDir();
  let apiVersion = 2;
  const { counts, handler } = counting(() => apiVersion);
  const fake = await startFake({ token: tok("a"), handler, ownPing: true });
  try {
    const files = setup(dir, [
      ["a", tok("a"), 2_000_000],
      ["b", tok("b"), 1_000_000],
    ]);
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => files });
    await c.ensureApi(API.required, API.serverVersion, { write: true });
    fake.token = tok("b"); // restarted from profile b
    const r = await c.call("set_property", { guid: "G" }, undefined, { write: true, api: API });
    assert.equal(r.changed, true);
    assert.equal(counts.set_property, 1);
    // restarted from profile a with a plugin that has another api version: refused before the write is resent
    apiVersion = 3;
    fake.token = tok("a");
    await assert.rejects(c.call("set_property", { guid: "G" }, undefined, { write: true, api: API }), (e) => {
      assert.equal(e.code, "API_MISMATCH");
      assert.equal(e.maybeApplied, false);
      return true;
    });
    assert.equal(counts.set_property, 1, "the write was not executed again");
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("a probe timeout inside a write's 401 retry is not ambiguous and the write is never executed", async () => {
  const dir = makeTempDir();
  let hold = false;
  const { counts, handler } = counting();
  const fake = await startFake({
    token: tok("a"),
    handler,
    raw: (_req, _res, rec) => hold && JSON.parse(rec.body).method === "ping", // never answers a ping while holding
  });
  try {
    const files = setup(dir, [
      ["a", tok("a"), 2_000_000],
      ["b", tok("b"), 1_000_000],
    ]);
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => files, timeoutMs: 300 });
    await c.call("ping");
    fake.token = tok("b");
    hold = true;
    await assert.rejects(c.call("set_property", { guid: "G" }, undefined, { write: true, api: API }), (e) => {
      assert.equal(e.maybeApplied, false);
      assert.match(e.message, /timed out/);
      assert.match(e.message, /the tool call was not sent/);
      assert.ok(!e.message.includes(WRITE_AMBIGUITY_NOTE));
      return true;
    });
    assert.equal(counts.set_property, undefined);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("explicit environment variable disables the search (one file, no probing, a 401 is a plain mismatch)", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("d") });
  try {
    tokenAt(dir, TSMM("Default"), tok("d"), 2_000_000); // would be accepted, but must not be looked at
    const mine = tokenAt(dir, ["mine"], "tok-wrong-eeeeeee", 1_000_000);
    const o = optionsFromEnv({ APPDATA: dir, PUMMEL_PARTY_MCP_TOKEN_FILE: mine }, WIN);
    assert.equal(o.resolveTokenFile, undefined);
    const c = new PluginClient({ ...o, port: fake.port });
    await assert.rejects(c.call("ping"), (e) => {
      assert.match(e.message, /Token mismatch/);
      assert.ok(e.message.includes(mine));
      return true;
    });
    assert.deepEqual(sent(fake), ["tok-wrong-eeeeeee", "tok-wrong-eeeeeee"], "only the configured file was ever used");
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("optionsFromEnv without settings finds mod-manager profiles through the injected APPDATA", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: tok("f") });
  try {
    tokenAt(dir, TSMM("Default"), tok("e"), 1_000_000);
    tokenAt(dir, R2("Default"), tok("f"), 2_000_000);
    const c = new PluginClient({ ...optionsFromEnv({ APPDATA: dir }, { ...WIN, steamRoots: [] }), port: fake.port });
    assert.equal((await c.call("ping")).pong, true);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});
