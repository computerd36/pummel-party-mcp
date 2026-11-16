import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { join, win32 } from "node:path";

/** Steam app id of Pummel Party. */
export const PUMMEL_PARTY_APP_ID = "880940";

export const TOKEN_REL_PATH = ["BepInEx", "config", "PummelPartyMcp.token"] as const;

export type VdfNode = { [key: string]: string | VdfNode };

/**
 * Minimal parser for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf):
 * quoted strings with backslash escapes, `{ }` blocks, `//` comments. No dependency. A repeated key keeps the last value.
 * Malformed input never throws: parsing stops and what was read so far is returned.
 */
export function parseVdf(text: string): VdfNode {
  let i = 0;
  const n = text.length;

  const skipSpace = (): void => {
    for (;;) {
      while (i < n && /\s/.test(text[i]!)) i++;
      if (text[i] === "/" && text[i + 1] === "/") {
        while (i < n && text[i] !== "\n") i++;
      } else return;
    }
  };

  const readString = (): string | undefined => {
    skipSpace();
    if (i >= n) return undefined;
    if (text[i] === '"') {
      i++;
      let out = "";
      while (i < n && text[i] !== '"') {
        if (text[i] === "\\" && i + 1 < n) {
          const c = text[i + 1]!;
          out += c === "n" ? "\n" : c === "t" ? "\t" : c; // \\ -> \, \" -> ", others literal
          i += 2;
        } else out += text[i++];
      }
      i++; // closing quote
      return out;
    }
    if (text[i] === "{" || text[i] === "}") return undefined;
    let out = ""; // unquoted token
    while (i < n && !/[\s"{}]/.test(text[i]!)) out += text[i++];
    return out;
  };

  const readBlock = (depth: number): VdfNode => {
    const node: VdfNode = {};
    if (depth > 32) return node;
    for (;;) {
      skipSpace();
      if (i >= n) return node;
      if (text[i] === "}") {
        i++;
        return node;
      }
      const key = readString();
      if (key === undefined) return node;
      skipSpace();
      if (text[i] === "{") {
        i++;
        node[key] = readBlock(depth + 1);
      } else {
        const value = readString();
        if (value === undefined) return node;
        node[key] = value;
      }
    }
  };

  return readBlock(0);
}

/** Library folder paths listed in the text of a libraryfolders.vdf (both the old and the new layout). */
export function libraryPathsFromVdf(text: string): string[] {
  const root = parseVdf(text);
  const folders = root["libraryfolders"] ?? root["LibraryFolders"];
  const out: string[] = [];
  if (!folders || typeof folders === "string") return out;
  for (const [key, entry] of Object.entries(folders)) {
    if (!/^\d+$/.test(key)) continue;
    if (typeof entry === "string") out.push(entry); // old layout: "1" "D:\\Games"
    else if (typeof entry.path === "string") out.push(entry.path);
  }
  return out;
}

/** `installdir` from the text of an appmanifest_*.acf, if it is a plain folder name. */
export function installDirFromAcf(text: string): string | undefined {
  const root = parseVdf(text);
  const state = root["AppState"];
  const dir = state && typeof state !== "string" ? state["installdir"] : undefined;
  if (typeof dir !== "string" || !dir || dir === "." || dir === ".." || /[\\/]/.test(dir)) return undefined;
  return dir;
}

export interface FsLike {
  existsSync(path: string): boolean;
  readFileSync(path: string, enc: "utf8"): string;
}

const realFs: FsLike = { existsSync, readFileSync };

/** Runs `reg query <key> /v <value>` and returns its output, or undefined on any failure. Injectable for tests. */
export type RegQuery = (key: string, value: string) => string | undefined;

// reg.exe is called by full path so a reg.exe earlier on PATH or in the working directory is never run.
// Its output is in the console code page: a Steam path with non-ASCII characters may be read wrongly and then missed;
// detection falls back to the other roots (Program Files, libraryfolders.vdf). PUMMEL_PARTY_MCP_GAME_DIR is the way out.
const realRegQuery: RegQuery = (key, value) => {
  try {
    const regExe = join(process.env.SystemRoot ?? "C:\\Windows", "System32", "reg.exe");
    return execFileSync(regExe, ["query", key, "/v", value], {
      encoding: "utf8",
      timeout: 3000,
      windowsHide: true,
      stdio: ["ignore", "pipe", "ignore"],
    });
  } catch {
    return undefined;
  }
};

/** The data of one value in `reg query` output (a REG_SZ / REG_EXPAND_SZ line), or undefined. */
export function parseRegValue(output: string | undefined, value: string): string | undefined {
  if (!output) return undefined;
  for (const line of output.split(/\r?\n/)) {
    const m = /^\s*(\S+)\s+REG_(?:EXPAND_)?SZ\s+(.+?)\s*$/i.exec(line);
    if (m && m[1]!.toLowerCase() === value.toLowerCase()) return m[2];
  }
  return undefined;
}

/** Steam install folders from the Windows registry (per-user SteamPath, then the machine-wide InstallPath). */
export function registrySteamRoots(regQuery: RegQuery = realRegQuery): string[] {
  const out: string[] = [];
  for (const [key, value] of [
    ["HKCU\\Software\\Valve\\Steam", "SteamPath"],
    ["HKLM\\SOFTWARE\\WOW6432Node\\Valve\\Steam", "InstallPath"],
  ] as const) {
    try {
      const v = parseRegValue(regQuery(key, value), value)?.trim();
      if (v && win32.isAbsolute(v)) out.push(win32.normalize(v)); // SteamPath uses forward slashes
    } catch {
      /* ignore */
    }
  }
  return out;
}

/** Removes duplicate folders (case-insensitive, ignoring a trailing separator), keeping the first. */
export function dedupePaths(paths: string[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const p of paths) {
    const k = p.replace(/[\\/]+$/, "").toLowerCase();
    if (!k || seen.has(k)) continue;
    seen.add(k);
    out.push(p);
  }
  return out;
}

/** Steam install folders on Windows: the registry first (on win32 only), then the default Program Files folders. */
export function defaultSteamRoots(
  env: NodeJS.ProcessEnv = process.env,
  deps: { platform?: NodeJS.Platform; regQuery?: RegQuery } = {},
): string[] {
  const roots: string[] = [];
  if ((deps.platform ?? process.platform) === "win32") roots.push(...registrySteamRoots(deps.regQuery));
  for (const base of [env["ProgramFiles(x86)"], env["ProgramFiles"]]) {
    if (base) roots.push(join(base, "Steam"));
  }
  return dedupePaths(roots);
}

/**
 * Finds the Pummel Party install folder through Steam's library list. `steamRoots` are candidate Steam install
 * folders; each one's steamapps\libraryfolders.vdf lists all library folders, and the library holding
 * steamapps\appmanifest_880940.acf wins (its `installdir` names the game folder). Returns undefined when nothing is found.
 */
export function findGameDir(steamRoots: string[], fs: FsLike = realFs): string | undefined {
  const libraries: string[] = [];
  const add = (p: string): void => {
    if (p && !libraries.includes(p)) libraries.push(p);
  };
  for (const root of steamRoots) {
    add(root);
    try {
      const vdf = join(root, "steamapps", "libraryfolders.vdf");
      if (fs.existsSync(vdf)) for (const p of libraryPathsFromVdf(fs.readFileSync(vdf, "utf8"))) add(p);
    } catch {
      /* unreadable list: ignore */
    }
  }
  for (const lib of libraries) {
    try {
      const acf = join(lib, "steamapps", `appmanifest_${PUMMEL_PARTY_APP_ID}.acf`);
      if (!fs.existsSync(acf)) continue;
      const dir = installDirFromAcf(fs.readFileSync(acf, "utf8"));
      if (!dir) continue;
      const game = join(lib, "steamapps", "common", dir);
      if (fs.existsSync(game)) return game;
    } catch {
      /* try the next library */
    }
  }
  return undefined;
}

export const NO_GAME_FOUND_MESSAGE =
  "Could not find the Pummel Party install folder, so the plugin token file is unknown. Set PUMMEL_PARTY_MCP_GAME_DIR to the game folder " +
  "(the one that contains BepInEx), or PUMMEL_PARTY_MCP_TOKEN_FILE to the full path of BepInEx\\config\\PummelPartyMcp.token. " +
  "Automatic detection (Steam path from the registry or Program Files, then its library list) only works on Windows.";

export interface ResolveDeps {
  platform?: NodeJS.Platform;
  steamRoots?: string[];
  fs?: FsLike;
  /** Registry lookup used when `steamRoots` is not given (tests inject a fake). */
  regQuery?: RegQuery;
}

/**
 * Token file path, in order: PUMMEL_PARTY_MCP_TOKEN_FILE, PUMMEL_PARTY_MCP_GAME_DIR, Steam library discovery (Windows only).
 * Throws an Error with NO_GAME_FOUND_MESSAGE when none applies. The token itself is never read here.
 */
export function resolveTokenFile(env: NodeJS.ProcessEnv = process.env, deps: ResolveDeps = {}): string {
  if (env.PUMMEL_PARTY_MCP_TOKEN_FILE) return env.PUMMEL_PARTY_MCP_TOKEN_FILE;
  if (env.PUMMEL_PARTY_MCP_GAME_DIR) return join(env.PUMMEL_PARTY_MCP_GAME_DIR, ...TOKEN_REL_PATH);
  if ((deps.platform ?? process.platform) === "win32") {
    const game = findGameDir(deps.steamRoots ?? defaultSteamRoots(env, deps), deps.fs);
    if (game) return join(game, ...TOKEN_REL_PATH);
  }
  throw new Error(NO_GAME_FOUND_MESSAGE);
}
