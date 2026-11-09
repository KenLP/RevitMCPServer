/**
 * Thin HTTP client to the in-Revit MCP addin.  All MCP tools go through here.
 *
 * Auth: reads the auth token from REVIT_MCP_AUTH_TOKEN env var or from the
 * well-known file at %APPDATA%/Autodesk/Revit/Addins/<version>/revit-mcp-token.txt.
 *
 * On 401 responses the client automatically re-reads the token file once (Revit
 * regenerates the token on restart) and retries the request.
 */
import { readFileSync } from "fs";
import { join } from "path";
import { randomUUID } from "node:crypto";
import { traceHeaders } from "./trace.js";
// The addin's HttpListener is hard-bound to http://127.0.0.1:<port>/, so a
// non-loopback REVIT_MCP_HOST can never reach a real addin — the only thing it
// could do is hand the bearer token and the full command stream to whatever is
// listening there, over plaintext HTTP. Refuse to start instead.
/** An env var's value, with unset and blank alike meaning "not given" (a plugin passes "" for an empty option). */
function envValue(name) {
    const value = process.env[name]?.trim();
    return value ? value : undefined;
}
const RAW_HOST = envValue("REVIT_MCP_HOST") ?? "127.0.0.1";
const LOOPBACK_HOSTS = new Set(["127.0.0.1", "localhost", "::1", "[::1]"]);
if (!LOOPBACK_HOSTS.has(RAW_HOST)) {
    throw new Error(`REVIT_MCP_HOST="${RAW_HOST}" is not a loopback address. The Revit addin ` +
        `only listens on 127.0.0.1, so a remote host can never be a real addin. ` +
        `Remove REVIT_MCP_HOST or set it to 127.0.0.1.`);
}
const REVIT_HOST = RAW_HOST === "::1" ? "[::1]" : RAW_HOST;
export const REVIT_VERSION = envValue("REVIT_MCP_VERSION") ?? "2026";
if (!/^20\d\d$/.test(REVIT_VERSION)) {
    throw new Error(`REVIT_MCP_VERSION="${REVIT_VERSION}" is not a Revit year such as 2025, 2026 or 2027.`);
}
// Auto-assign port by version: 2026 → 7891, 2027 → 7892, …
// Explicit REVIT_MCP_PORT always wins.
const DEFAULT_PORT = 7891 + (parseInt(REVIT_VERSION, 10) - 2026);
const REVIT_PORT = Number(envValue("REVIT_MCP_PORT") ?? String(DEFAULT_PORT));
const BASE = `http://${REVIT_HOST}:${REVIT_PORT}`;
const TIMEOUT_MS = Number(process.env.REVIT_MCP_TIMEOUT_MS ?? "30000");
// Opening a model takes tens of seconds, minutes on a first cloud download; the addin imposes no
// timeout of its own, so open_document gets a long client-side one.
export const OPEN_TIMEOUT_MS = Number(process.env.REVIT_MCP_OPEN_TIMEOUT_MS ?? "900000");
/** Resolve the auth token: explicit env var, then the addin's token file. */
function resolveAuthToken() {
    const explicit = process.env.REVIT_MCP_AUTH_TOKEN;
    if (explicit)
        return explicit;
    return readTokenFile();
}
/** Read the token from the well-known per-session file written by the addin. */
function readTokenFile() {
    try {
        const appData = process.env.APPDATA ?? "";
        const tokenPath = join(appData, "Autodesk", "Revit", "Addins", REVIT_VERSION, "revit-mcp-token.txt");
        return readFileSync(tokenPath, "utf-8").trim() || undefined;
    }
    catch {
        return undefined;
    }
}
// Mutable — refreshed automatically on 401 (Revit regenerates token on restart).
let _authToken = resolveAuthToken();
export function hasAuthToken() {
    return _authToken !== undefined;
}
export function buildHeaders() {
    const headers = {
        "content-type": "application/json",
    };
    if (_authToken) {
        headers["authorization"] = `Bearer ${_authToken}`;
    }
    return headers;
}
async function postJson(path, body, _retried = false, timeoutMs = TIMEOUT_MS) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
        const headers = { ...buildHeaders(), ...traceHeaders() };
        headers["x-request-id"] = randomUUID();
        const res = await fetch(`${BASE}${path}`, {
            method: "POST",
            headers,
            body: JSON.stringify(body),
            signal: controller.signal,
        });
        const text = await res.text();
        let envelope;
        try {
            envelope = JSON.parse(text);
        }
        catch {
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
        if (!_retried && envelope.error?.code === "unauthorized") {
            const fresh = readTokenFile();
            if (fresh && fresh !== _authToken) {
                _authToken = fresh;
                return postJson(path, body, true, timeoutMs);
            }
        }
        return envelope;
    }
    catch (err) {
        const name = err?.name;
        const message = err instanceof Error ? err.message : String(err);
        return {
            ok: false,
            error: {
                code: name === "AbortError" ? "timeout" : "transport_error",
                message: `Failed to reach Revit addin at ${BASE}: ${message}`,
            },
        };
    }
    finally {
        clearTimeout(timer);
    }
}
// approvalToken (preview_required mode): sent beside dryRun, never inside params — the token is
// bound to the exact params of the dry-run that issued it.
export function callRevit(command, params, dryRun = false, approvalToken, timeoutMs) {
    const body = { command, params };
    if (dryRun)
        body.dryRun = true;
    if (approvalToken)
        body.approvalToken = approvalToken;
    return postJson("/mcp", body, false, timeoutMs);
}
export function callRevitBatch(steps, stopOnError = true, dryRun = false, approvalToken) {
    const body = { stopOnError, steps };
    if (dryRun)
        body.dryRun = true;
    if (approvalToken)
        body.approvalToken = approvalToken;
    return postJson("/mcp/batch", body);
}
/** Common helper to convert a Revit envelope into the MCP tool result shape. */
export function envelopeToToolResult(envelope) {
    const text = JSON.stringify(envelope, null, 2);
    return {
        content: [{ type: "text", text }],
        isError: !envelope.ok,
    };
}
/**
 * Probe the addin's /health endpoint.  Never throws — returns a structured
 * result so callers can log diagnostics without crashing startup.
 */
export async function checkRevitHealth() {
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
        const data = (await res.json());
        return {
            reachable: true,
            version: data.version,
            authEnabled: data.authEnabled,
            authTokenPresent: hasAuthToken(),
        };
    }
    catch {
        return { reachable: false, authTokenPresent: hasAuthToken() };
    }
    finally {
        clearTimeout(timer);
    }
}
export const REVIT_BASE_URL = BASE;
