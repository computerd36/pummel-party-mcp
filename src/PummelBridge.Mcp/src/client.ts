import { readFileSync } from "node:fs";
import { join } from "node:path";

export const HOST = "127.0.0.1"; // fixed on purpose; not configurable
export const DEFAULT_PORT = 38917;
export const DEFAULT_GAME_DIR = "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Pummel Party";

/** Error whose message is safe to show to the agent (never contains the token). */
export class BridgeError extends Error {
  readonly code: string | undefined;
  /** True when the request may have reached the plugin (so a write may or may not have been applied). */
  readonly maybeApplied: boolean;
  constructor(message: string, code?: string, maybeApplied = false) {
    super(message);
    this.name = "BridgeError";
    this.code = code;
    this.maybeApplied = maybeApplied;
  }
}

export const MAX_RESPONSE_BYTES = 8 * 1024 * 1024;
export const WRITE_AMBIGUITY_NOTE =
  "The edit may or may not have been applied; check with get_status/get_object/get_hierarchy before retrying.";

export interface ClientOptions {
  port: number;
  tokenFile: string;
  timeoutMs?: number;
}

export function optionsFromEnv(env: NodeJS.ProcessEnv = process.env): ClientOptions {
  const portRaw = env.PUMMELBRIDGE_PORT;
  const port = portRaw && /^\d+$/.test(portRaw) ? Number(portRaw) : DEFAULT_PORT;
  const tokenFile =
    env.PUMMELBRIDGE_TOKEN_FILE ||
    join(env.PUMMELBRIDGE_GAME_DIR || DEFAULT_GAME_DIR, "BepInEx", "config", "PummelBridge.token");
  // Optional override of every request timeout (ms); mainly for tests.
  const tRaw = env.PUMMELBRIDGE_TIMEOUT_MS;
  const t = tRaw && /^\d+$/.test(tRaw) ? Number(tRaw) : 0;
  const timeoutMs = t >= 1 && t <= 600000 ? t : undefined;
  return { port, tokenFile, timeoutMs };
}

const HINTS: Record<string, string> = {
  EDITOR_NOT_OPEN: "Open the Mod Editor from the game's main menu, then retry.",
  NO_MOD_OPEN: "The editor is open but no mod is loaded; open a mod in the editor, then retry.",
  BUSY_LOADING: "The game is still loading; retry in a few seconds.",
  WRITES_DISABLED: "Write tools are switched off in the plugin config (Safety.EnableWrites); the human must enable it and restart the game.",
  PUBLISH_BLOCK_INCOMPLETE:
    "The plugin could not install its Workshop safety patches (probably after a game update), so write tools stay disabled. Tell the human.",
  GUARD_NOT_TEST_MOD: "Only mods whose name starts with PB_TEST_ can be modified; do not retry with a different name for a real mod.",
  INVALID_STATE:
    "See the message for the required state (e.g. save first, close the open editor dialog, release the mouse button, leave play mode, or return to the scene hierarchy), then retry.",
  INVALID_PARAMS: "A parameter or value was refused; the message names the property/rule. Fix the call (see get_object / list_component_types / list_assets; for actions and triggers get_action_graph / list_action_types / list_trigger_types, e.g. a non-editable slot, a hidden target flag, an action the call site does not offer, an unknown parameter name) and retry.",
  NOT_FOUND: "No such object, component, asset or mod. Re-fetch guids with get_hierarchy / get_object (the object may have been deleted or be in another editing context). For triggers the message lists the object's trigger names; a popup path index may be out of range.",
  TIMEOUT: "The plugin message says which case. \"nothing was changed\" / \"safe to retry\" (the call timed out or was refused before its commit point): nothing was applied and it never runs later, so retrying is safe. A create_test_mod / open_test_mod message saying the mod was created or is being opened means it exists and may still finish loading: check get_status and list_mods, do not retry blindly (a retry of create gets ALREADY_EXISTS). \"the change was applied, but the call did not finish\" means the edit happened (main thread stalled): check get_status / get_object first. A client-side timeout (no plugin reply) is ambiguous: check get_status before retrying a write.",
  INTERNAL: "Unexpected plugin error (possibly a game version change). Check get_log; an edit may have partly applied, so verify with get_object.",
  UNKNOWN_METHOD: "The running plugin does not implement this method; it may be older than this MCP server.",
  ALREADY_EXISTS: "A mod with that name already exists; choose another name or use open_test_mod.",
  SERVER_BUSY: "The plugin is busy (too many requests in flight); wait a moment and retry.",
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
  save_mod: 30000,
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
  // action/trigger write tools (the plugin's own limit is 15 s)
  add_action: 20000,
  remove_action: 20000,
  move_action: 20000,
  set_action: 20000,
  add_trigger: 20000,
  remove_trigger: 20000,
  set_trigger: 20000,
};

/** Same rule the plugin enforces for sandbox mod names. */
export const TEST_MOD_NAME_RE = /^PB_TEST_[A-Za-z0-9_ -]+$/;
export function isValidTestModName(name: string): boolean {
  return name.length <= 64 && TEST_MOD_NAME_RE.test(name) && !name.startsWith(" ") && !name.endsWith(" ");
}

const NOT_RUNNING_CODES = new Set(["ECONNREFUSED", "ENOTFOUND", "EHOSTUNREACH", "ENETUNREACH", "EADDRNOTAVAIL"]);

interface RawResponse {
  status: number;
  text: string;
}

export class BridgeClient {
  private token: string | undefined;
  private reread = false;

  constructor(private readonly opts: ClientOptions) {}

  private loadToken(): string {
    let t = "";
    try {
      t = readFileSync(this.opts.tokenFile, "utf8").trim();
    } catch {
      /* fall through */
    }
    if (!t) {
      throw new BridgeError(
        `Token file not found or empty at "${this.opts.tokenFile}". The game must have been started at least once with the PummelBridge plugin installed (the plugin creates this file).`,
      );
    }
    return t;
  }

  private getToken(): string {
    if (this.token === undefined || this.reread) {
      this.token = this.loadToken();
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
  private async readBody(res: Response, ac: AbortController): Promise<string> {
    const tooBig = () =>
      new BridgeError(
        `The plugin response is larger than ${MAX_RESPONSE_BYTES / (1024 * 1024)} MB and was discarded. Narrow the query (smaller limit/maxDepth, search, a subtree).`,
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
  private async post(method: string, params: unknown, timeoutMs: number): Promise<RawResponse> {
    const token = this.getToken();
    const ac = new AbortController();
    const timer = setTimeout(() => ac.abort(), timeoutMs);
    const timedOut = () =>
      new BridgeError(
        `Request to the PummelBridge plugin timed out after ${timeoutMs / 1000}s (the game may be busy or frozen).`,
        "TIMEOUT",
        true,
      );
    try {
      let res: Response;
      try {
        res = await fetch(`http://${HOST}:${this.opts.port}/rpc`, {
          method: "POST",
          headers: { "Content-Type": "application/json", "X-PummelBridge-Token": token },
          body: JSON.stringify({ method, params }),
          signal: ac.signal,
          redirect: "error", // never forward the token or body to another address
        });
      } catch (e) {
        this.reread = true; // game may have restarted with a new token
        if (ac.signal.aborted) throw timedOut();
        const cause = (e as { cause?: { message?: string; code?: string } })?.cause;
        if (/redirect/i.test(String(cause?.message ?? ""))) {
          throw new BridgeError(
            `the listener on ${HOST}:${this.opts.port} answered with a redirect; it is not the PummelBridge plugin.`,
          );
        }
        if (cause?.code === undefined || NOT_RUNNING_CODES.has(cause.code)) {
          throw new BridgeError(
            `Pummel Party is not running or the PummelBridge plugin is not loaded (no listener on ${HOST}:${this.opts.port}).`,
          );
        }
        throw new BridgeError(
          `The connection to the PummelBridge plugin was lost before a response arrived (${cause.code}).`,
          undefined,
          true,
        );
      }
      try {
        return { status: res.status, text: await this.readBody(res, ac) };
      } catch (e) {
        if (e instanceof BridgeError) throw e;
        this.reread = true;
        if (ac.signal.aborted) throw timedOut();
        throw new BridgeError("The connection to the PummelBridge plugin was lost while reading the response.", undefined, true);
      }
    } finally {
      clearTimeout(timer);
    }
  }

  /**
   * Calls a plugin method and returns its `result`. Throws BridgeError with a user-facing message.
   * `write` marks write tools: failures that leave the outcome unknown get an explanatory note.
   */
  async call(
    method: string,
    params: Record<string, unknown> = {},
    timeoutMs?: number,
    callOpts: { write?: boolean } = {},
  ): Promise<unknown> {
    const t = this.opts.timeoutMs ?? timeoutMs ?? 15000;
    try {
      return await this.callInner(method, params, t);
    } catch (e) {
      if (callOpts.write && e instanceof BridgeError && e.maybeApplied) {
        throw new BridgeError(`${e.message}
${WRITE_AMBIGUITY_NOTE}`, e.code, true);
      }
      throw e;
    }
  }

  private async callInner(method: string, params: Record<string, unknown>, timeoutMs: number): Promise<unknown> {
    let res = await this.post(method, params, timeoutMs);
    if (res.status === 401) {
      this.reread = true; // a 401 never executes the call, so one retry with a fresh token is safe
      res = await this.post(method, params, timeoutMs);
      if (res.status === 401) {
        throw new BridgeError(
          `Token mismatch: the plugin rejected the token read from "${this.opts.tokenFile}" (HTTP 401). Check PUMMELBRIDGE_TOKEN_FILE / PUMMELBRIDGE_GAME_DIR and that the file belongs to the running game.`,
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
      throw new BridgeError(`The plugin rejected the request (HTTP 403${detail ? `: ${detail}` : ""}).`);
    }
    if (envErr) {
      const code = text(envErr.code ?? "INTERNAL") || "INTERNAL";
      const hint = HINTS[code] ?? HTTP_HINTS[res.status];
      // An ok:false envelope from the plugin means it answered; nothing is left ambiguous.
      throw new BridgeError(`${code}: ${text(envErr.message)}${hint ? `
Hint: ${hint}` : ""}`, code);
    }
    if (res.status !== 200) {
      const hint = HTTP_HINTS[res.status];
      throw new BridgeError(`Unexpected response from plugin (HTTP ${res.status}).${hint ? `
Hint: ${hint}` : ""}`);
    }
    if (!parsed) throw new BridgeError(`Unexpected response from plugin (HTTP ${res.status}, not valid JSON).`, undefined, true);
    if (body && body.ok === true) return body.result;
    throw new BridgeError(`Unexpected response from plugin (HTTP ${res.status}).`, undefined, true);
  }
}
