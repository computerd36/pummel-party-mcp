import { readFileSync } from "node:fs";
import { join } from "node:path";

export const HOST = "127.0.0.1"; // fixed on purpose; not configurable
export const DEFAULT_PORT = 38917;
export const DEFAULT_GAME_DIR = "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Pummel Party";

/** Error whose message is safe to show to the agent (never contains the token). */
export class BridgeError extends Error {
  readonly code: string | undefined;
  constructor(message: string, code?: string) {
    super(message);
    this.name = "BridgeError";
    this.code = code;
  }
}

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
  return { port, tokenFile };
}

const HINTS: Record<string, string> = {
  EDITOR_NOT_OPEN: "Open the Mod Editor from the game's main menu, then retry.",
  NO_MOD_OPEN: "The editor is open but no mod is loaded; open a mod in the editor, then retry.",
  BUSY_LOADING: "The game is still loading; retry in a few seconds.",
};

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

  private async post(method: string, params: unknown, timeoutMs: number): Promise<Response> {
    const token = this.getToken();
    const ac = new AbortController();
    const timer = setTimeout(() => ac.abort(), timeoutMs);
    try {
      return await fetch(`http://${HOST}:${this.opts.port}/rpc`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-PummelBridge-Token": token },
        body: JSON.stringify({ method, params }),
        signal: ac.signal,
      });
    } catch {
      this.reread = true; // game may have restarted with a new token
      if (ac.signal.aborted) {
        throw new BridgeError(
          `Request to the PummelBridge plugin timed out after ${timeoutMs / 1000}s (the game may be busy or frozen).`,
          "TIMEOUT",
        );
      }
      throw new BridgeError(
        `Pummel Party is not running or the PummelBridge plugin is not loaded (no listener on ${HOST}:${this.opts.port}).`,
      );
    } finally {
      clearTimeout(timer);
    }
  }

  /** Calls a plugin method and returns its `result`. Throws BridgeError with a user-facing message. */
  async call(method: string, params: Record<string, unknown> = {}, timeoutMs = 15000): Promise<unknown> {
    let res = await this.post(method, params, timeoutMs);
    if (res.status === 401) {
      this.reread = true;
      res = await this.post(method, params, timeoutMs);
      if (res.status === 401) {
        throw new BridgeError(
          `Token mismatch: the plugin rejected the token read from "${this.opts.tokenFile}" (HTTP 401). Check PUMMELBRIDGE_TOKEN_FILE / PUMMELBRIDGE_GAME_DIR and that the file belongs to the running game.`,
        );
      }
    }
    if (res.status === 403) {
      throw new BridgeError("The plugin rejected the request (HTTP 403: origin/host not allowed).");
    }
    let body: any;
    try {
      body = await res.json();
    } catch {
      throw new BridgeError(`Unexpected response from plugin (HTTP ${res.status}, not valid JSON).`);
    }
    if (body && body.ok === true) return body.result;
    if (body && body.ok === false && body.error) {
      const code = String(body.error.code ?? "INTERNAL");
      const hint = HINTS[code];
      throw new BridgeError(`${code}: ${body.error.message ?? ""}${hint ? `\nHint: ${hint}` : ""}`, code);
    }
    throw new BridgeError(`Unexpected response from plugin (HTTP ${res.status}).`);
  }
}
