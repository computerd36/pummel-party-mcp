import { readFileSync, statSync } from "node:fs";
import { resolveTokenCandidates, type ResolveDeps } from "./steam.js";

export const HOST = "127.0.0.1"; // fixed on purpose; not configurable
export const DEFAULT_PORT = 38917;

/** Error whose message is safe to show to the agent (never contains the token). */
export class PluginError extends Error {
  readonly code: string | undefined;
  /** True when the request may have reached the plugin (so a write may or may not have been applied). */
  readonly maybeApplied: boolean;
  constructor(message: string, code?: string, maybeApplied = false) {
    super(message);
    this.name = "PluginError";
    this.code = code;
    this.maybeApplied = maybeApplied;
  }
}

/** An error of the token file search or of a probe. It is raised before the tool call is sent, so it is never ambiguous. */
class DetectionError extends PluginError {
  constructor(message: string, code?: string) {
    super(`${message}\nThis happened while finding the plugin's token file; the tool call was not sent.`, code, false);
  }
}

const NO_TOKEN_FILES_MESSAGE =
  "No plugin token file was found. The token file appears after the Mod Editor was opened once with the plugin installed. " +
  "Set PUMMEL_PARTY_MCP_TOKEN_FILE to its full path, or PUMMEL_PARTY_MCP_GAME_DIR to the game folder.";
const NO_CONFIG_MESSAGE = "No token file configured (set PUMMEL_PARTY_MCP_GAME_DIR or PUMMEL_PARTY_MCP_TOKEN_FILE).";
const SET_VARIABLES =
  "Start the game from the install whose plugin you want to use, or set PUMMEL_PARTY_MCP_TOKEN_FILE to its token file " +
  "(or PUMMEL_PARTY_MCP_GAME_DIR to its game folder).";

/** The plugin's token rule (TokenAuth.IsWellFormed, after trimming): 64 lowercase hex characters. A candidate that breaks it is never sent. */
const TOKEN_RE = /^[0-9a-f]{64}$/;

/** At most this many paths are listed in an error. */
const MAX_LISTED_PATHS = 5;
function listPaths(files: string[]): string {
  const shown = files.slice(0, MAX_LISTED_PATHS).map((f) => `"${f}"`).join(", ");
  return files.length > MAX_LISTED_PATHS ? `${shown} and ${files.length - MAX_LISTED_PATHS} more` : shown;
}

export const MAX_ERROR_DATA_CHARS = 4000;
export const MAX_RESPONSE_BYTES = 8 * 1024 * 1024;
export const WRITE_AMBIGUITY_NOTE =
  "The edit may or may not have been applied; check with get_status/get_object/get_hierarchy before retrying.";

export interface ClientOptions {
  port: number;
  /** Fixed token file path (tests, or set through the environment). */
  tokenFile?: string;
  /**
   * Used when `tokenFile` is unset: the search for the token file (auto-detection), run lazily; may throw. Several files mean several
   * candidates (e.g. one per mod-manager profile): the plugin decides which token it accepts.
   */
  resolveTokenFile?: () => string | string[];
  timeoutMs?: number;
  /** Clock in ms (tests inject a fake); default Date.now. */
  now?: () => number;
}

/** The api version check is repeated when the last successful plugin reply is older than this (a game restart between calls). */
export const API_RECHECK_MS = 10_000;

export function optionsFromEnv(env: NodeJS.ProcessEnv = process.env, deps: ResolveDeps = {}): ClientOptions {
  const portRaw = env.PUMMEL_PARTY_MCP_PORT;
  const port = portRaw && /^\d+$/.test(portRaw) ? Number(portRaw) : DEFAULT_PORT;
  // Order: TOKEN_FILE, GAME_DIR (each alone, no search), then the Steam game folder and the mod-manager profiles on Windows.
  // Discovery runs lazily on the first call that needs the token.
  const explicit = Boolean(env.PUMMEL_PARTY_MCP_TOKEN_FILE || env.PUMMEL_PARTY_MCP_GAME_DIR);
  const tokenFile = explicit ? resolveTokenCandidates(env)[0] : undefined;
  const resolver = explicit ? undefined : () => resolveTokenCandidates(env, deps);
  // Optional override of every request timeout (ms); mainly for tests.
  const tRaw = env.PUMMEL_PARTY_MCP_TIMEOUT_MS;
  const t = tRaw && /^\d+$/.test(tRaw) ? Number(tRaw) : 0;
  const timeoutMs = t >= 1 && t <= 600000 ? t : undefined;
  return { port, tokenFile, resolveTokenFile: resolver, timeoutMs };
}

const HINTS: Record<string, string> = {
  EDITOR_NOT_OPEN: "Open the Mod Editor from the game's main menu, then retry.",
  EDITOR_CLOSED_DURING_CALL:
    "The Mod Editor was closed while this call was running, so a write may have partly applied (a read changed nothing). Do not retry blindly: ask the human to open the Mod Editor, then check get_status / get_object (list_mods after create_test_mod or open_test_mod) before retrying.",
  NO_MOD_OPEN: "The editor is open but no mod is loaded; open a mod in the editor, then retry.",
  BUSY_LOADING: "The game is still loading; retry in a few seconds.",
  WRITES_DISABLED: "Write tools are switched off in the plugin config (Safety.EnableWrites); the human must enable it and restart the game.",
  PUBLISH_BLOCK_INCOMPLETE:
    "The plugin could not install its Workshop safety patches (probably after a game update), so the editor and mod tools stay off (ping, get_status, get_log and self_test still answer). Tell the human.",
  GAME_VERSION_UNTESTED:
    "The game was updated and this plugin version was not tested with it, so write tools are refused (read tools work). Tell the human; do NOT retry. They can run self_test and, if it is clean, set [Safety] AcceptedGameVersion in the plugin config (no restart).",
  GUARD_NOT_TEST_MOD: "Only mods whose name starts with MCP_ can be modified; do not retry with a different name for a real mod.",
  INVALID_STATE:
    "See the message for the required state (e.g. save first, close the open editor dialog, release the mouse button, leave play mode, or close_prefab when a prefab is open), then retry.",
  INVALID_PARAMS: "A parameter or value was refused; the message names the property/rule. Fix the call (see get_object / list_component_types / list_assets; for actions and triggers get_action_graph / list_action_types / list_trigger_types, e.g. a non-editable slot, a hidden target flag, an action the call site does not offer, an unknown parameter name) and retry.",
  NOT_FOUND: "No such object, component, asset or mod. Re-fetch guids with get_hierarchy / get_object (the object may have been deleted or be in another editing context). For triggers the message lists the object's trigger names; a popup path index may be out of range.",
  TIMEOUT: "The plugin message says which case. \"nothing was changed\" / \"safe to retry\" (the call timed out or was refused before its commit point): nothing was applied and it never runs later, so retrying is safe. A create_test_mod / open_test_mod message saying the mod was created or is being opened means it exists and may still finish loading: check get_status and list_mods, do not retry blindly (a retry of create gets ALREADY_EXISTS). \"the change was applied, but the call did not finish\" means the edit happened (main thread stalled): check get_status / get_object first. A client-side timeout (no plugin reply) is ambiguous: check get_status before retrying a write.",
  INTERNAL: "Unexpected plugin error (possibly a game version change). Check get_log; an edit may have partly applied, so verify with get_object.",
  UNKNOWN_METHOD: "The running plugin does not implement this method; it may be older than this MCP server.",
  ALREADY_EXISTS: "A mod with that name already exists (create_test_mod: choose another name or use open_test_mod). For import_asset/create_material the name is taken: pass a different name (nothing was created).",
  ASSET_IN_USE: "The asset is still used (error.data lists the references) or is the preview image. Clear the references first (set_property / remove_action / set_mod_settings / set_material), or for remove_asset retry with force true if the references may become null; changing a texture's compression needs its component/action/preview references cleared.",
  IMPORT_FAILED: "The game could not import the copied file; the plugin removed its copy (error.data.cleanedUp) and the source file was not touched. Check get_log, try a smaller or re-exported file.",
  LINK_REFUSED:
    "A path the call would write or delete runs through a junction, symlink or mount point inside the sandbox mod, or is a hard-linked file (the message names it); nothing was written or deleted. Links inside a mod folder are never written through: do not create them. Tell the human to remove the link; reading (list_assets/get_asset show linked: true) still works.",
  SERVER_BUSY:"The plugin is busy (too many requests in flight); wait a moment and retry.",
  PAYLOAD_TOO_LARGE: "The request was larger than the plugin's 1 MB limit; send less data (fewer or smaller values) per call.",
  REQUEST_TIMEOUT: "The plugin did not receive the whole request within 10 s; retry.",
  HEADERS_TOO_LARGE: "The request headers were refused as too large (HTTP 431); this should not happen, check for a proxy on the port.",
};

const HTTP_HINTS: Record<number, string> = {
  408: HINTS.REQUEST_TIMEOUT!,
  413: HINTS.PAYLOAD_TOO_LARGE!,
  431: HINTS.HEADERS_TOO_LARGE!,
  503: HINTS.SERVER_BUSY!,
};

/** Per-tool request timeouts (ms); tools not listed use the client default. */
export const TOOL_TIMEOUTS_MS: Record<string, number> = {
  screenshot: 30000,
  create_test_mod: 45000,
  open_test_mod: 45000,
  save_mod: 30000, // plugin limit 20 s (Core/CommitBudgets.cs)
  // edit tools: the plugin's own limit is 15 s
  create_object: 20000,
  duplicate_object: 20000,
  delete_object: 20000,
  reparent_object: 20000,
  set_object: 20000,
  set_transform: 20000,
  add_component: 20000,
  remove_component: 20000,
  set_property: 20000,
  set_properties: 20000,
  undo: 30000,
  set_mod_settings: 20000,
  capture_preview_image: 30000,
  // asset tools: the plugin's own limits are 120 s (import: copy, checks on the copy and the game's decode on its main thread) and 30 s
  // (Core/CommitBudgets.cs), plus the 4 s commit grace and a network margin
  import_asset: 130000,
  set_import_settings: 40000,
  create_material: 40000,
  set_material: 40000,
  remove_asset: 40000,
  // prefab tools: the plugin's own limit is 30 s
  create_prefab: 40000,
  open_prefab: 40000,
  close_prefab: 40000,
  // action/trigger write tools (the plugin's own limit is 15 s)
  add_action: 20000,
  remove_action: 20000,
  move_action: 20000,
  set_action: 20000,
  add_trigger: 20000,
  remove_trigger: 20000,
  set_trigger: 20000,
  // playtest/batch: the plugin's own limits are 60 / 30 / 120 s, plus up to 4 s commit grace and network margin
  start_playtest: 70000,
  stop_playtest: 40000,
  run_batch: 130000,
};

export interface ApiInfo {
  apiVersion: number | undefined;
  pluginVersion: string | undefined;
  sessionId: string | undefined;
}

/** The error text for a plugin whose api version differs from the one this server needs; undefined when they match. */
export function apiMismatchMessage(api: ApiInfo, required: number, serverVersion: string): string | undefined {
  const v = api.apiVersion;
  if (v === required) return undefined;
  const head = `plugin api ${v === undefined ? "unknown (ping gave no apiVersion)" : `v${v}`}, this server (${serverVersion}) needs api v${required}`;
  return v !== undefined && v > required
    ? `${head}: update the server (npx -y pummel-party-mcp@latest).`
    : `${head}: update the plugin (install a pummel-party-mcp plugin release that has api v${required}).`;
}

/** Same rule the plugin enforces for sandbox mod names. */
export const TEST_MOD_NAME_RE = /^MCP_[A-Za-z0-9_ -]+$/;
export function isValidTestModName(name: string): boolean {
  return name.length <= 64 && TEST_MOD_NAME_RE.test(name) && !name.startsWith(" ") && !name.endsWith(" ");
}

/** What to do when a reply is over the 8 MB limit, per tool. */
function tooLargeHint(method: string): string {
  switch (method) {
    case "screenshot":
      return "Call screenshot again with a smaller maxWidth (e.g. 1280).";
    case "run_batch":
      return "Run fewer steps per batch (the steps may have been applied: check with get_hierarchy / get_object); avoid read steps with large results.";
    case "get_hierarchy":
      return "Use maxDepth and/or rootGuid (a subtree).";
    case "list_assets":
      return "Use a smaller limit, plus search and/or type.";
    case "get_log":
    case "get_playtest_events":
      return "Use a smaller max, or sinceId.";
    default:
      return "Narrow the query (this tool's limit/max/maxDepth/search/section/rootGuid parameters, where it has them).";
  }
}

const NOT_RUNNING_CODES =new Set(["ECONNREFUSED", "ENOTFOUND", "EHOSTUNREACH", "ENETUNREACH", "EADDRNOTAVAIL"]);

interface RawResponse {
  status: number;
  text: string;
}

/** The result of a successful `ping` reply, or undefined. */
function pongOf(res: RawResponse): Record<string, unknown> | undefined {
  if (res.status !== 200) return undefined;
  try {
    const body = JSON.parse(res.text);
    return body?.ok === true && body.result?.pong === true ? body.result : undefined;
  } catch {
    return undefined;
  }
}

export interface CallOptions {
  /** A write tool: failures that leave the outcome unknown get an explanatory note. */
  write?: boolean;
  /** The api version check (as in ensureApi) that is repeated before the resend after a 401. */
  api?: { required: number; serverVersion: string };
}

export class PluginClient {
  private token: string | undefined;
  /** The file `token` was read from. */
  private tokenFrom: string | undefined;
  private reread = false;
  /** From the last `ping`: reset on a connection failure or 401 (a restarted game may run another plugin), or when a reply shows another sessionId. */
  private api: ApiInfo | undefined;
  private apiPending: Promise<ApiInfo> | undefined;
  /** Time of the last successful plugin reply (any tool). */
  private lastOkAt = 0;

  private readonly now: () => number;

  constructor(private readonly opts: ClientOptions) {
    this.now = opts.now ?? Date.now;
  }

  /*
   * Auto-detection state (unused when opts.tokenFile is set). `candidates`: the files the last search found; the search runs only
   * while it is undefined. `detectedTokenFile`: the file in use (the only candidate, or the one the plugin accepted). Both are
   * dropped after a 401, after a detection that found nothing usable and when the file in use is missing or empty. A connection
   * failure, a timeout or a busy plugin keeps them.
   */
  private candidates: string[] | undefined;
  private detectedTokenFile: string | undefined;
  private detectPending: Promise<string> | undefined;

  /** The token file to use: the fixed path, the detected one, or the result of a detection (concurrent callers share one). */
  private tokenFile(): Promise<string> {
    const f = this.opts.tokenFile ?? this.detectedTokenFile;
    if (f) return Promise.resolve(f);
    this.detectPending ??= this.detect().finally(() => {
      this.detectPending = undefined;
    });
    return this.detectPending;
  }

  private forgetDetection(): void {
    this.candidates = undefined;
    this.detectedTokenFile = undefined;
  }

  /**
   * Picks the token file from the remembered candidates, or from a new search. When remembered candidates give nothing usable, the
   * search runs once more right away (a profile may have been started for the first time since). Every error is a DetectionError.
   */
  private async detect(): Promise<string> {
    try {
      if (!this.opts.resolveTokenFile) throw new PluginError(NO_CONFIG_MESSAGE);
      const remembered = this.candidates;
      let r = await this.tryCandidates(remembered ?? this.search());
      if (typeof r !== "string" && remembered) r = await this.tryCandidates(this.search());
      if (typeof r === "string") return r;
      this.forgetDetection();
      throw new PluginError(r.error);
    } catch (e) {
      throw new DetectionError(e instanceof Error ? e.message : String(e), e instanceof PluginError ? e.code : undefined);
    }
  }

  /** Runs the search and remembers its candidates. Throws when there are none. */
  private search(): string[] {
    this.forgetDetection();
    const r = this.opts.resolveTokenFile!();
    const files = Array.isArray(r) ? r : [r];
    if (files.length === 0) throw new PluginError(NO_TOKEN_FILES_MESSAGE);
    this.candidates = files;
    return files;
  }

  /**
   * One candidate is used as it is. Of several, those whose content is not a well-formed token are skipped, and the rest are tried
   * with `ping` (most recently written first, which only saves probes): a 401 moves on, a successful ping reply chooses the file.
   * Returns the chosen file, or the error text when none is usable. Throws (choosing nothing) when the plugin does not answer, times
   * out or gives any other reply.
   */
  private async tryCandidates(files: string[]): Promise<string | { error: string }> {
    if (files.length === 1) {
      this.detectedTokenFile = files[0]!; // a missing or empty file is reported when the token is read
      return files[0]!;
    }
    const usable: { file: string; token: string; mtime: number }[] = [];
    for (const file of files) {
      try {
        const token = readFileSync(file, "utf8").trim();
        if (TOKEN_RE.test(token)) usable.push({ file, token, mtime: statSync(file).mtimeMs });
      } catch {
        /* unreadable: skipped */
      }
    }
    if (usable.length === 0) {
      return {
        error:
          `None of the ${files.length} token files found holds a well-formed token (64 lowercase hex characters); they are empty, malformed or unreadable: ${listPaths(files)}. ` +
          `When the Mod Editor is opened with the plugin installed, the plugin replaces an empty or malformed token file of its own install or profile with a new token: ` +
          `open the Mod Editor from the install or profile you want to use, then retry. Or set PUMMEL_PARTY_MCP_TOKEN_FILE to its token file ` +
          `(or PUMMEL_PARTY_MCP_GAME_DIR to its game folder).`,
      };
    }
    usable.sort((a, b) => b.mtime - a.mtime); // stable: equal times keep the search order
    const probeMs = Math.min(this.opts.timeoutMs ?? 5000, 5000);
    for (const c of usable) {
      const res = await this.post("ping", {}, probeMs, c.token);
      if (res.status === 401) continue;
      const pong = pongOf(res);
      if (!pong) {
        throw new PluginError(
          res.status === 503
            ? `The plugin answered HTTP 503 while the server checked which token file it accepts. ${HINTS.SERVER_BUSY}`
            : `Unexpected answer (HTTP ${res.status}) while the server checked which token file the plugin accepts: the plugin is busy or something else is answering on ${HOST}:${this.opts.port}.`,
        );
      }
      this.detectedTokenFile = c.file;
      this.token = c.token;
      this.tokenFrom = c.file;
      this.reread = false;
      this.noteApi(pong); // a real ping reply of the plugin that accepted the token
      this.lastOkAt = this.now();
      return c.file;
    }
    return {
      error:
        `The running plugin accepted none of the ${files.length} token files found (it rejected them, or they are empty, malformed or unreadable): ${listPaths(files)}. ` +
        `They probably belong to another game session or install. ${SET_VARIABLES}`,
    };
  }

  private loadToken(file: string): string {
    let t = "";
    try {
      t = readFileSync(file, "utf8").trim();
    } catch {
      /* fall through */
    }
    if (!t) {
      this.forgetDetection(); // search again next time (the game may have been moved or installed since)
      throw new PluginError(
        `Token file not found or empty at "${file}". The Mod Editor must have been opened at least once with the pummel-party-mcp plugin installed (the plugin creates this file then). ` +
          `Without PUMMEL_PARTY_MCP_TOKEN_FILE or PUMMEL_PARTY_MCP_GAME_DIR the server also searches the profiles in the default data folders of Thunderstore Mod Manager and r2modman (Windows); ` +
          `for a moved data folder or another manager, set PUMMEL_PARTY_MCP_TOKEN_FILE to <profile>\\BepInEx\\config\\PummelPartyMcp.token.`,
      );
    }
    return t;
  }

  /** The cached token, read again from `file` when it is another file or after a 401 or a connection failure. */
  private getToken(file: string): string {
    if (this.token === undefined || this.reread || this.tokenFrom !== file) {
      this.token = this.loadToken(file);
      this.tokenFrom = file;
      this.reread = false;
    }
    return this.token;
  }

  /** Replaces the token in text that came from the network, in case a listener echoes it. */
  private scrub(text: string): string {
    const t = this.token;
    // real tokens are 64 hex chars; very short test tokens would mangle ordinary text
    return t && t.length >= 8 ? text.split(t).join("[token]") : text;
  }

  /** Reads the body as a stream, aborting beyond MAX_RESPONSE_BYTES. */
  private async readBody(res: Response, ac: AbortController, method: string): Promise<string> {
    const tooBig = () =>
      new PluginError(
        `The plugin response is larger than ${MAX_RESPONSE_BYTES / (1024 * 1024)} MB and was discarded. ${tooLargeHint(method)}`,
        undefined,
        true,
      );
    const cl = Number(res.headers.get("content-length"));
    if (Number.isFinite(cl) && cl > MAX_RESPONSE_BYTES) {
      ac.abort();
      throw tooBig();
    }
    if (!res.body) return "";
    const reader = res.body.getReader();
    const chunks: Uint8Array[] = [];
    let total = 0;
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > MAX_RESPONSE_BYTES) {
        ac.abort();
        void reader.cancel().catch(() => {});
        throw tooBig();
      }
      chunks.push(value);
    }
    return Buffer.concat(chunks).toString("utf8");
  }

  /** One HTTP round trip including the full body; the abort timer stays armed until the body is read. */
  private async post(method: string, params: unknown, timeoutMs: number, tokenOverride?: string): Promise<RawResponse> {
    const token = tokenOverride ?? this.getToken(await this.tokenFile());
    const ac = new AbortController();
    const timer = setTimeout(() => ac.abort(), timeoutMs);
    const timedOut = () =>
      new PluginError(
        `Request to the pummel-party-mcp plugin timed out after ${timeoutMs / 1000}s (the game may be busy or frozen).`,
        "TIMEOUT",
        true,
      );
    try {
      let res: Response;
      try {
        res = await fetch(`http://${HOST}:${this.opts.port}/rpc`, {
          method: "POST",
          headers: { "Content-Type": "application/json", "X-PummelPartyMcp-Token": token },
          body: JSON.stringify({ method, params }),
          signal: ac.signal,
          redirect: "error", // never forward the token or body to another address
        });
      } catch (e) {
        this.reread = true; // game may have restarted with a new token
        this.api = undefined; // ... or with another plugin version: check the api version again
        if (ac.signal.aborted) throw timedOut();
        const cause = (e as { cause?: { message?: string; code?: string } })?.cause;
        if (/redirect/i.test(String(cause?.message ?? ""))) {
          throw new PluginError(
            `the listener on ${HOST}:${this.opts.port} answered with a redirect; it is not the pummel-party-mcp plugin.`,
          );
        }
        if (cause?.code === undefined || NOT_RUNNING_CODES.has(cause.code)) {
          throw new PluginError(
            `No answer from the pummel-party-mcp plugin on ${HOST}:${this.opts.port}. Start Pummel Party and open the Mod Editor from the main menu: the plugin only listens while the Mod Editor is open.`,
          );
        }
        throw new PluginError(
          `The connection to the pummel-party-mcp plugin was lost before a response arrived (${cause.code}).`,
          undefined,
          true,
        );
      }
      try {
        return { status: res.status, text: await this.readBody(res, ac, method) };
      } catch (e) {
        if (e instanceof PluginError) throw e;
        this.reread = true;
        this.api = undefined;
        if (ac.signal.aborted) throw timedOut();
        throw new PluginError("The connection to the pummel-party-mcp plugin was lost while reading the response.", undefined, true);
      }
    } finally {
      clearTimeout(timer);
    }
  }

  /** Reads apiVersion/pluginVersion/sessionId from a `ping` result and caches them. */
  noteApi(result: unknown): ApiInfo {
    const r = (result ?? {}) as Record<string, unknown>;
    const info: ApiInfo = {
      apiVersion: typeof r.apiVersion === "number" ? r.apiVersion : undefined,
      pluginVersion: typeof r.pluginVersion === "string" ? r.pluginVersion : undefined,
      sessionId: typeof r.sessionId === "string" ? r.sessionId : undefined,
    };
    this.api = info;
    return info;
  }

  /**
   * Pings (cached) and throws PluginError(API_MISMATCH) when the plugin's api version is not `required`. The check is repeated
   * after a connection failure, a 401 or another sessionId, and when the last successful plugin reply is more than API_RECHECK_MS
   * old (the game may have been restarted between calls). Concurrent callers share one ping. A failed ping is not cached; its
   * error says it was the version check and that the tool call was not sent.
   */
  async ensureApi(required: number, serverVersion: string, opts: { write?: boolean } = {}): Promise<void> {
    let info = this.api;
    if (info && this.now() - this.lastOkAt > API_RECHECK_MS) info = undefined;
    if (!info) {
      this.apiPending ??= this.call("ping", {}, 5000).then(
        (r) => this.noteApi(r),
        (e) => {
          if (!(e instanceof PluginError) || e instanceof DetectionError) throw e; // a DetectionError already says the call was not sent
          throw new PluginError(
            `${e.message}
This was the plugin version check made before the tool call; the tool call itself was not sent${opts.write ? " (nothing was applied)" : ""}.`,
            e.code,
          );
        },
      );
      try {
        info = await this.apiPending;
      } finally {
        this.apiPending = undefined;
      }
    }
    const msg = apiMismatchMessage(info, required, serverVersion);
    if (msg) throw new PluginError(msg, "API_MISMATCH");
  }

  /**
   * Calls a plugin method and returns its `result`. Throws PluginError with a user-facing message.
   * `write` marks write tools: failures that leave the outcome unknown get an explanatory note. `api`: see CallOptions.
   */
  async call(method: string, params: Record<string, unknown> = {}, timeoutMs?: number, callOpts: CallOptions = {}): Promise<unknown> {
    const t = this.opts.timeoutMs ?? timeoutMs ?? 15000;
    try {
      const result = await this.callInner(method, params, t, callOpts);
      this.lastOkAt = this.now();
      // a reply that carries another sessionId (ping, get_log) means the game restarted: check the api version again
      const sid = (result as { sessionId?: unknown } | null)?.sessionId;
      if (this.api && typeof sid === "string" && this.api.sessionId !== undefined && sid !== this.api.sessionId) this.api = undefined;
      return result;
    } catch (e) {
      if (callOpts.write && e instanceof PluginError && e.maybeApplied) {
        throw new PluginError(`${e.message}
${WRITE_AMBIGUITY_NOTE}`, e.code, true);
      }
      throw e;
    }
  }

  private async callInner(method: string, params: Record<string, unknown>, timeoutMs: number, callOpts: CallOptions): Promise<unknown> {
    let res = await this.post(method, params, timeoutMs);
    if (res.status === 401) {
      // A 401 never executes the call, so one resend is safe. A new token means a new game session, maybe started from another
      // install or profile: the token is read again, an auto-detected file is chosen again after a new search (a fixed path stays),
      // and the api version of the plugin that answers now is checked before the resend.
      this.reread = true;
      this.api = undefined;
      this.forgetDetection();
      if (callOpts.api) {
        await this.tokenFile(); // with several candidates, the accepted probe already gives the api version
        await this.ensureApi(callOpts.api.required, callOpts.api.serverVersion, { write: callOpts.write });
      }
      res = await this.post(method, params, timeoutMs);
      if (res.status === 401) {
        throw new PluginError(
          `Token mismatch: the plugin rejected the token read from "${this.opts.tokenFile ?? this.tokenFrom ?? "the detected location"}" (HTTP 401). Check PUMMEL_PARTY_MCP_TOKEN_FILE / PUMMEL_PARTY_MCP_GAME_DIR and that the file belongs to the running game.`,
        );
      }
    }
    let body: any;
    let parsed = false;
    try {
      body = JSON.parse(res.text);
      parsed = true;
    } catch {
      /* handled below */
    }
    const envErr = parsed && body && body.ok === false && body.error ? body.error : undefined;
    const text = (v: unknown): string =>
      this.scrub(typeof v === "string" ? v : v === undefined || v === null ? "" : (JSON.stringify(v) ?? String(v))).slice(0, 2000);

    if (res.status === 403) {
      const detail = envErr ? text(envErr.message) : this.scrub(res.text.trim()).slice(0, 300);
      throw new PluginError(`The plugin rejected the request (HTTP 403${detail ? `: ${detail}` : ""}).`);
    }
    if (envErr) {
      const code = text(envErr.code ?? "INTERNAL") || "INTERNAL";
      const hint = HINTS[code] ?? HTTP_HINTS[res.status];
      // An ok:false envelope from the plugin means it answered; nothing is left ambiguous.
      // error.data (e.g. the validator issues of a refused start_playtest) is appended as compact JSON.
      const data = envErr.data === undefined || envErr.data === null ? "" : this.scrub(JSON.stringify(envErr.data) ?? "");
      const dataLine = data
        ? `
Data: ${data.length > MAX_ERROR_DATA_CHARS ? `${data.slice(0, MAX_ERROR_DATA_CHARS)}[truncated: ${data.length} chars]` : data}`
        : "";
      throw new PluginError(`${code}: ${text(envErr.message)}${dataLine}${hint ? `
Hint: ${hint}` : ""}`, code);
    }
    if (res.status !== 200) {
      const hint = HTTP_HINTS[res.status];
      // The statuses with a hint (408/413/431/503) are refusals before execution; any other status without an envelope is ambiguous
      // (call() adds the "may or may not have been applied" note for write tools only).
      throw new PluginError(`Unexpected response from plugin (HTTP ${res.status}).${hint ? `
Hint: ${hint}` : ""}`, undefined, hint === undefined);
    }
    if (!parsed) throw new PluginError(`Unexpected response from plugin (HTTP ${res.status}, not valid JSON).`, undefined, true);
    if (body && body.ok === true) return body.result;
    throw new PluginError(`Unexpected response from plugin (HTTP ${res.status}).`, undefined, true);
  }
}
