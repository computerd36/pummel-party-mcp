import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PluginClient, PluginError, optionsFromEnv } from "../dist/client.js";
import {
  parseVdf,
  libraryPathsFromVdf,
  installDirFromAcf,
  findGameDir,
  resolveTokenFile,
  NO_GAME_FOUND_MESSAGE,
} from "../dist/steam.js";
import { startFake, makeTempDir, removeDir } from "./fake-plugin.mjs";

/** VDF string literal for a path: backslashes doubled, like Steam writes them. */
const q = (s) => `"${s.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;

function acf(installdir) {
  return `"AppState"\n{\n\t"appid"\t\t"880940"\n\t"name"\t\t"Pummel Party"\n\t"installdir"\t\t${q(installdir)}\n}\n`;
}

function libraryfolders(paths) {
  const body = paths
    .map((p, i) => `\t${q(String(i))}\n\t{\n\t\t"path"\t\t${q(p)}\n\t\t"label"\t\t""\n\t\t"apps"\n\t\t{\n\t\t\t"730"\t\t"123"\n\t\t}\n\t}\n`)
    .join("");
  return `"libraryfolders"\n{\n${body}}\n`;
}

/** Steam root + extra library (both under a temp dir, with spaces in the names) holding the game manifest. */
function makeSteam(dir, { manifestIn = "library", installdir = "Pummel Party", createGame = true } = {}) {
  const root = join(dir, "Program Files (x86)", "Steam");
  const lib = join(dir, "Games Library", "Lib Two");
  for (const base of [root, lib]) mkdirSync(join(base, "steamapps", "common"), { recursive: true });
  writeFileSync(join(root, "steamapps", "libraryfolders.vdf"), libraryfolders([root, lib]));
  const where = manifestIn === "library" ? lib : root;
  writeFileSync(join(where, "steamapps", "appmanifest_880940.acf"), acf(installdir));
  if (createGame) mkdirSync(join(where, "steamapps", "common", installdir), { recursive: true });
  return { root, lib, game: join(where, "steamapps", "common", installdir) };
}

test("parseVdf: nesting, escapes, comments, tabs", () => {
  const t = parseVdf(
    `// comment\n"a"\n{\n\t"k"\t\t"C:\\\\Program Files (x86)\\\\Steam"\n\t"quote"\t"say \\"hi\\""\n\t"sub" { "x" "1" } // trailing\n}\n`,
  );
  assert.equal(t.a.k, "C:\\Program Files (x86)\\Steam");
  assert.equal(t.a.quote, 'say "hi"');
  assert.equal(t.a.sub.x, "1");
});

test("parseVdf: malformed input does not throw", () => {
  for (const bad of ["", '"a" {', '"a" { "b"', '"a', "}}}", '"a" "b" "c"', "{"]) {
    assert.doesNotThrow(() => parseVdf(bad), bad);
  }
});

test("libraryPathsFromVdf: new layout, old layout, non-numeric keys ignored", () => {
  const nl = libraryfolders(["C:\\Program Files (x86)\\Steam", "D:\\Game Libs\\Lib Two"]);
  assert.deepEqual(libraryPathsFromVdf(nl), ["C:\\Program Files (x86)\\Steam", "D:\\Game Libs\\Lib Two"]);
  const old = `"LibraryFolders"\n{\n\t"TimeNextStatsReport"\t"1"\n\t"ContentStatsID"\t"2"\n\t"1"\t\t"D:\\\\Games\\\\Steam Lib"\n}\n`;
  assert.deepEqual(libraryPathsFromVdf(old), ["D:\\Games\\Steam Lib"]);
  assert.deepEqual(libraryPathsFromVdf("garbage"), []);
});

test("installDirFromAcf: plain names only", () => {
  assert.equal(installDirFromAcf(acf("Pummel Party")), "Pummel Party");
  assert.equal(installDirFromAcf(acf("..\\evil")), undefined);
  assert.equal(installDirFromAcf(acf("a/b")), undefined);
  assert.equal(installDirFromAcf(acf("..")), undefined);
  assert.equal(installDirFromAcf('"AppState" { }'), undefined);
});

test("findGameDir: manifest in a second library listed by libraryfolders.vdf (paths with spaces)", () => {
  const dir = makeTempDir();
  try {
    const s = makeSteam(dir);
    assert.equal(findGameDir([s.root]), s.game);
  } finally {
    removeDir(dir);
  }
});

test("findGameDir: manifest in the Steam root itself, custom installdir", () => {
  const dir = makeTempDir();
  try {
    const s = makeSteam(dir, { manifestIn: "root", installdir: "Pummel Party Copy" });
    assert.equal(findGameDir([s.root]), s.game);
  } finally {
    removeDir(dir);
  }
});

test("findGameDir: nothing found when manifest missing, game folder missing, or no steam root", () => {
  const dir = makeTempDir();
  try {
    const s = makeSteam(dir, { createGame: false });
    assert.equal(findGameDir([s.root]), undefined); // manifest but no game folder
    assert.equal(findGameDir([join(dir, "nowhere")]), undefined);
    assert.equal(findGameDir([]), undefined);
  } finally {
    removeDir(dir);
  }
});

test("resolveTokenFile order: TOKEN_FILE, then GAME_DIR, then Steam discovery", () => {
  const dir = makeTempDir();
  try {
    const s = makeSteam(dir);
    const deps = { platform: "win32", steamRoots: [s.root] };
    const tail = join("BepInEx", "config", "PummelPartyMcp.token");
    assert.equal(resolveTokenFile({ PUMMEL_PARTY_MCP_TOKEN_FILE: "T", PUMMEL_PARTY_MCP_GAME_DIR: "G" }, deps), "T");
    assert.equal(resolveTokenFile({ PUMMEL_PARTY_MCP_GAME_DIR: join(dir, "My Game") }, deps), join(dir, "My Game", tail));
    assert.equal(resolveTokenFile({}, deps), join(s.game, tail));
  } finally {
    removeDir(dir);
  }
});

test("resolveTokenFile: nothing found, or not Windows, names both environment variables", () => {
  const dir = makeTempDir();
  try {
    const s = makeSteam(dir);
    for (const deps of [
      { platform: "win32", steamRoots: [join(dir, "none")] },
      { platform: "linux", steamRoots: [s.root] },
      { platform: "darwin", steamRoots: [s.root] },
    ]) {
      assert.throws(() => resolveTokenFile({}, deps), (e) => {
        assert.equal(e.message, NO_GAME_FOUND_MESSAGE);
        assert.match(e.message, /PUMMEL_PARTY_MCP_GAME_DIR/);
        assert.match(e.message, /PUMMEL_PARTY_MCP_TOKEN_FILE/);
        return true;
      });
    }
  } finally {
    removeDir(dir);
  }
});

test("optionsFromEnv never throws without configuration (detection is lazy) and keeps explicit settings eager", () => {
  const o = optionsFromEnv({}, { platform: "win32", steamRoots: [] });
  assert.equal(o.tokenFile, undefined);
  assert.equal(typeof o.resolveTokenFile, "function");
  assert.throws(() => o.resolveTokenFile(), /PUMMEL_PARTY_MCP_GAME_DIR/);
  assert.equal(optionsFromEnv({ PUMMEL_PARTY_MCP_TOKEN_FILE: "T" }).tokenFile, "T");
  assert.equal(optionsFromEnv({ PUMMEL_PARTY_MCP_GAME_DIR: "G" }).resolveTokenFile, undefined);
});

test("client: lazy detection, error names both variables, re-detects after a failure, then works", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: "tok-abcdef-123456" });
  try {
    const s = makeSteam(dir, { createGame: false });
    const deps = { platform: "win32", steamRoots: [s.root] };
    const c = new PluginClient({ ...optionsFromEnv({}, deps), port: fake.port });
    // game folder does not exist yet: clear error, nothing thrown at construction
    await assert.rejects(c.call("ping"), (e) => {
      assert.ok(e instanceof PluginError);
      assert.match(e.message, /PUMMEL_PARTY_MCP_GAME_DIR/);
      assert.match(e.message, /PUMMEL_PARTY_MCP_TOKEN_FILE/);
      return true;
    });
    // the game appears (folder + token): the next call detects again and succeeds
    const cfg = join(s.game, "BepInEx", "config");
    mkdirSync(cfg, { recursive: true });
    writeFileSync(join(cfg, "PummelPartyMcp.token"), "tok-abcdef-123456\n");
    assert.equal((await c.call("ping")).pong, true);
    assert.equal(fake.requests.length, 1);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});

test("client: detected path is cached (no second detection after success)", async () => {
  const dir = makeTempDir();
  const fake = await startFake({ token: "tok-abcdef-123456" });
  try {
    const f = join(dir, "t.token");
    writeFileSync(f, "tok-abcdef-123456");
    let n = 0;
    const c = new PluginClient({ port: fake.port, resolveTokenFile: () => (n++, f) });
    await c.call("ping");
    await c.call("ping");
    assert.equal(n, 1);
  } finally {
    await fake.close();
    removeDir(dir);
  }
});
