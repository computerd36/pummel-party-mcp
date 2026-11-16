import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { parseRegValue, registrySteamRoots, defaultSteamRoots, dedupePaths, resolveTokenFile } from "../dist/steam.js";
import { makeTempDir, removeDir } from "./fake-plugin.mjs";

const BS = "\\";
const HKCU = ["HKCU", "Software", "Valve", "Steam"].join(BS);
const HKLM = ["HKLM", "SOFTWARE", "WOW6432Node", "Valve", "Steam"].join(BS);
const win = (...parts) => parts.join(BS);

// A fake `reg query`: output in the real format, keyed by "<key> <value>".
const fakeReg = (table, calls = []) => (key, value) => {
  calls.push(`${key} ${value}`);
  const data = table[`${key} ${value}`];
  return data === undefined ? undefined : `\r\n${key}\r\n    ${value}    REG_SZ    ${data}\r\n\r\n`;
};

test("parseRegValue reads REG_SZ / REG_EXPAND_SZ data with spaces and ignores other values", () => {
  const out = `\r\nHKEY_CURRENT_USER\\Software\\Valve\\Steam\r\n    SteamPath    REG_SZ    d:/my games/steam\r\n    SteamExe    REG_SZ    x\r\n\r\n`;
  assert.equal(parseRegValue(out, "SteamPath"), "d:/my games/steam");
  assert.equal(parseRegValue(out, "InstallPath"), undefined);
  assert.equal(parseRegValue("    InstallPath    REG_EXPAND_SZ    C:\\Steam  \n", "InstallPath"), "C:\\Steam");
  assert.equal(parseRegValue(undefined, "SteamPath"), undefined);
  assert.equal(parseRegValue("ERROR: The system was unable to find the specified registry key or value.", "SteamPath"), undefined);
});

test("registrySteamRoots: HKCU SteamPath first (slashes normalised), then HKLM InstallPath; missing keys and throwing lookups are skipped", () => {
  const calls = [];
  const roots = registrySteamRoots(
    fakeReg({ [`${HKCU} SteamPath`]: "d:/my games/steam", [`${HKLM} InstallPath`]: win("C:", "Program Files (x86)", "Steam") }, calls),
  );
  assert.deepEqual(roots, [win("d:", "my games", "steam"), win("C:", "Program Files (x86)", "Steam")]);
  assert.deepEqual(calls, [`${HKCU} SteamPath`, `${HKLM} InstallPath`]);
  assert.deepEqual(registrySteamRoots(() => undefined), []);
  assert.deepEqual(
    registrySteamRoots(() => {
      throw new Error("boom");
    }),
    [],
  );
});

test("registrySteamRoots: values are trimmed; empty, whitespace-only and relative values are ignored", () => {
  const table = (a, b) => ({ [`${HKCU} SteamPath`]: a, [`${HKLM} InstallPath`]: b });
  assert.deepEqual(registrySteamRoots((k, v) => `    ${v}    REG_SZ    ${k.startsWith("HKCU") ? "   " : ""}

`), []);
  assert.deepEqual(registrySteamRoots(fakeReg(table("steam", "..\steam"))), []);
  assert.deepEqual(registrySteamRoots(fakeReg(table("Steam/x", "steam"))), []);
  assert.deepEqual(registrySteamRoots((k, v) => `    ${v}    REG_SZ    	 d:/steam 	
`), [win("d:", "steam"), win("d:", "steam")]);
});

// Windows path semantics (node:path join/normalize on backslash paths): these two run on Windows only.
const WINDOWS_ONLY = { skip: process.platform !== "win32" && "Windows paths" };

test("defaultSteamRoots: registry roots first, then Program Files, de-duplicated case-insensitively; no registry off Windows", WINDOWS_ONLY, () => {
  const env = { "ProgramFiles(x86)": win("C:", "Program Files (x86)"), ProgramFiles: win("C:", "Program Files") };
  const reg = fakeReg({
    [`${HKCU} SteamPath`]: "d:/steam",
    [`${HKLM} InstallPath`]: win("c:", "program files (x86)", "STEAM") + BS, // same folder as the Program Files (x86) default
  });
  assert.deepEqual(defaultSteamRoots(env, { platform: "win32", regQuery: reg }), [
    win("d:", "steam"),
    win("c:", "program files (x86)", "STEAM") + BS,
    join(win("C:", "Program Files"), "Steam"),
  ]);
  const calls = [];
  assert.deepEqual(defaultSteamRoots(env, { platform: "linux", regQuery: fakeReg({}, calls) }), [
    join(win("C:", "Program Files (x86)"), "Steam"),
    join(win("C:", "Program Files"), "Steam"),
  ]);
  assert.deepEqual(calls, [], "the registry is only read on Windows");
  assert.deepEqual(dedupePaths([win("A", "b"), win("a", "B") + BS, "c"]), [win("A", "b"), "c"]);
});

test("resolveTokenFile finds a game through a Steam folder that only the registry knows", WINDOWS_ONLY, () => {
  const dir = makeTempDir();
  try {
    const root = join(dir, "Odd Place", "Steam");
    mkdirSync(join(root, "steamapps", "common", "Pummel Party"), { recursive: true });
    const reg = fakeReg({ [`${HKCU} SteamPath`]: root.replaceAll(BS, "/") });
    const tail = join("BepInEx", "config", "PummelPartyMcp.token");
    writeFileSync(
      join(root, "steamapps", "appmanifest_880940.acf"),
      `"AppState"\n{\n\t"appid"\t\t"880940"\n\t"installdir"\t\t"Pummel Party"\n}\n`,
    );
    assert.equal(resolveTokenFile({}, { platform: "win32", regQuery: reg }), join(root, "steamapps", "common", "Pummel Party", tail));
    assert.throws(() => resolveTokenFile({}, { platform: "win32", regQuery: fakeReg({}) }), /PUMMEL_PARTY_MCP_GAME_DIR/);
  } finally {
    removeDir(dir);
  }
});
