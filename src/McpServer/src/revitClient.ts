/**
 * Thin HTTP client to the in-Revit MCP addin.  All MCP tools go through here.
 *
 * Auth: reads the auth token from REVIT_MCP_AUTH_TOKEN env var or from the
 * well-known file at %APPDATA%/Autodesk/Revit/Addins/<version>/revit-mcp-token.txt.
 * Set REVIT_MCP_AUTH=false to skip auth entirely.
 *
 * On 401 responses the client automatically re-reads the token file once (Revit
 * regenerates the token on restart) and retries the request.
 */

import { readFileSync } from "fs";
import { join } from "path";
import { randomUUID } from "node:crypto";

const REVIT_HOST = process.env.REVIT_MCP_HOST ?? "127.0.0.1";
const REVIT_VERSION = process.env.REVIT_MCP_VERSION ?? "2026";
// Auto-assign port by version: 2026 → 7891, 2027 → 7892, …
// Explicit REVIT_MCP_PORT always wins.
const DEFAULT_PORT = 7891 + (parseInt(REVIT_VERSION, 10) - 2026);
const REVIT_PORT = Number(process.env.REVIT_MCP_PORT ?? String(DEFAULT_PORT));
const BASE = `http://${REVIT_HOST}:${REVIT_PORT}`;
const TIMEOUT_MS = Number(process.env.REVIT_MCP_TIMEOUT_MS ?? "30000");
const AUTH_DISABLED = process.env.REVIT_MCP_AUTH?.toLowerCase() === "false";

export interface RevitEnvelope {
  ok: boolean;
  data?: unknown;
  error?: { code: string; message: string; type?: string };
  // Dry-run responses:
  dryRun?: boolean;
  committed?: boolean;
  // Batch responses also carry these:
  count?: number;
  hadFailures?: boolean;
  results?: unknown;
}

export interface BatchStep {
  command: string;
  params?: Record<string, unknown>;
}

/** Resolve the auth token. Returns undefined if auth is disabled. */
function resolveAuthToken(): string | undefined {
  if (AUTH_DISABLED) return undefined;

  // Explicit env var takes precedence.
  const explicit = process.env.REVIT_MCP_AUTH_TOKEN;
  if (explicit) return explicit;

  return readTokenFile();
}

/** Read the token from the well-known per-session file written by the addin. */
function readTokenFile(): string | undefined {
  try {
    const appData = process.env.APPDATA ?? "";
    const tokenPath = join(
      appData,
      "Autodesk",
      "Revit",
      "Addins",
      REVIT_VERSION,
      "revit-mcp-token.txt",
    );
    return readFileSync(tokenPath, "utf-8").trim() || undefined;
  } catch {
    return undefined;
  }
}

// Mutable — refreshed automatically on 401 (Revit regenerates token on restart).
let _authToken: string | undefined = resolveAuthToken();

export function hasAuthToken(): boolean {
  return _authToken !== undefined;
}

export function buildHeaders(): Record<string, string> {
  const headers: Record<string, string> = {
    "content-type": "application/json",
  };
  if (_authToken) {
    headers["authorization"] = `Bearer ${_authToken}`;
  }
  return headers;
}

async function postJson(
  path: string,
  body: unknown,
  _retried = false,
): Promise<RevitEnvelope> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
  try {
    const headers = buildHeaders();
    headers["x-request-id"] = randomUUID();
    const res = await fetch(`${BASE}${path}`, {
      method: "POST",
      headers,
      body: JSON.stringify(body),
      signal: controller.signal,
    });
    const text = await res.text();
    let envelope: RevitEnvelope;
    try {
      envelope = JSON.parse(text) as RevitEnvelope;
    } catch {
      return {
        ok: false,
        error: {
          code: "bad_response",
          message: `Non-JSON response from Revit addin (status ${res.status}): ${text.slice(0, 500)}`,
        },
      };
    }

    // On 401, try refreshing the token from disk once — Revit regenerates it
    // on restart and env-var tokens might be stale.
    if (!_retried && envelope.error?.code === "unauthorized" && !AUTH_DISABLED) {
      const fresh = readTokenFile();
      if (fresh && fresh !== _authToken) {
        _authToken = fresh;
        return postJson(path, body, true);
      }
    }

    return envelope;
  } catch (err) {
    const name = (err as { name?: string })?.name;
    const message = err instanceof Error ? err.message : String(err);
    return {
      ok: false,
      error: {
        code: name === "AbortError" ? "timeout" : "transport_error",
        message: `Failed to reach Revit addin at ${BASE}: ${message}`,
      },
    };
  } finally {
    clearTimeout(timer);
  }
}

export function callRevit(
  command: string,
  params: Record<string, unknown>,
  dryRun = false,
): Promise<RevitEnvelope> {
  const body: Record<string, unknown> = { command, params };
  if (dryRun) body.dryRun = true;
  return postJson("/mcp", body);
}

export function callRevitBatch(
  steps: BatchStep[],
  stopOnError = true,
  dryRun = false,
): Promise<RevitEnvelope> {
  const body: Record<string, unknown> = { stopOnError, steps };
  if (dryRun) body.dryRun = true;
  return postJson("/mcp/batch", body);
}

/** Common helper to convert a Revit envelope into the MCP tool result shape. */
export function envelopeToToolResult(envelope: RevitEnvelope) {
  const text = JSON.stringify(envelope, null, 2);
  return {
    content: [{ type: "text" as const, text }],
    isError: !envelope.ok,
  };
}

export interface HealthInfo {
  reachable: boolean;
  version?: string;
  authEnabled?: boolean;
  authTokenPresent: boolean;
}

/**
 * Probe the addin's /health endpoint.  Never throws — returns a structured
 * result so callers can log diagnostics without crashing startup.
 */
export async function checkRevitHealth(): Promise<HealthInfo> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), 5000);
  try {
    const res = await fetch(`${BASE}/health`, {
      headers: buildHeaders(),
      signal: ctrl.signal,
    });
    if (!res.ok) {
      return { reachable: false, authTokenPresent: hasAuthToken() };
    }
    const data = (await res.json()) as {
      version?: string;
      authEnabled?: boolean;
    };
    return {
      reachable: true,
      version: data.version,
      authEnabled: data.authEnabled,
      authTokenPresent: hasAuthToken(),
    };
  } catch {
    return { reachable: false, authTokenPresent: hasAuthToken() };
  } finally {
    clearTimeout(timer);
  }
}

export const REVIT_BASE_URL = BASE;
