/**
 * Thin HTTP client to the in-Revit MCP addin.  All MCP tools go through here.
 *
 * Auth: reads the auth token from REVIT_MCP_AUTH_TOKEN env var or from the
 * well-known file at %APPDATA%/Autodesk/Revit/Addins/<version>/revit-mcp-token.txt.
 * Set REVIT_MCP_AUTH=false to skip auth entirely.
 */

import { readFileSync } from "fs";
import { join } from "path";

const REVIT_HOST = process.env.REVIT_MCP_HOST ?? "127.0.0.1";
const REVIT_VERSION = process.env.REVIT_MCP_VERSION ?? "2026";
// Auto-assign port by version: 2026 → 7891, 2027 → 7892, …
// Explicit REVIT_MCP_PORT always wins.
const DEFAULT_PORT = 7891 + (parseInt(REVIT_VERSION, 10) - 2026);
const REVIT_PORT = Number(process.env.REVIT_MCP_PORT ?? String(DEFAULT_PORT));
const BASE = `http://${REVIT_HOST}:${REVIT_PORT}`;
const TIMEOUT_MS = Number(process.env.REVIT_MCP_TIMEOUT_MS ?? "30000");

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
  const authFlag = process.env.REVIT_MCP_AUTH;
  if (authFlag?.toLowerCase() === "false") return undefined;

  // Explicit token takes precedence.
  const explicit = process.env.REVIT_MCP_AUTH_TOKEN;
  if (explicit) return explicit;

  // Try reading from the well-known token file.
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
    return readFileSync(tokenPath, "utf-8").trim();
  } catch {
    // Token file not found — run without auth.
    return undefined;
  }
}

const AUTH_TOKEN = resolveAuthToken();

function buildHeaders(): Record<string, string> {
  const headers: Record<string, string> = {
    "content-type": "application/json",
  };
  if (AUTH_TOKEN) {
    headers["authorization"] = `Bearer ${AUTH_TOKEN}`;
  }
  return headers;
}

async function postJson(path: string, body: unknown): Promise<RevitEnvelope> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
  try {
    const res = await fetch(`${BASE}${path}`, {
      method: "POST",
      headers: buildHeaders(),
      body: JSON.stringify(body),
      signal: controller.signal,
    });
    const text = await res.text();
    try {
      return JSON.parse(text) as RevitEnvelope;
    } catch {
      return {
        ok: false,
        error: {
          code: "bad_response",
          message: `Non-JSON response from Revit addin (status ${res.status}): ${text.slice(0, 500)}`,
        },
      };
    }
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

export const REVIT_BASE_URL = BASE;
