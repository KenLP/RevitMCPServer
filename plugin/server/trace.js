/**
 * Evaluation-harness hooks (all optional, all off unless their env var is set):
 *
 *   REVIT_MCP_CLIENT    → sent as `X-MCP-Client` (≤64 chars) on every call to the add-in
 *   REVIT_MCP_TRACE_ID  → sent as `X-MCP-Trace`  (≤128 chars); the add-in copies both into its
 *                         audit log, so a harness run can be joined to what the server did
 *   REVIT_MCP_TRACE_FILE→ append one JSON line per MCP tool call:
 *                         {ts, tool, durationMs, ok, errorCode, reqBytes, respBytes, trace}
 *
 * `respBytes` is the server-side stand-in for tokens; counting real tokens is the harness's job.
 * Writing the trace file can never fail a tool call: errors are reported once on stderr.
 */
import { appendFileSync } from "node:fs";
const MAX_CLIENT = 64;
const MAX_TRACE = 128;
/** Trim, drop control characters, cut to `max`. Empty → undefined. */
export function cleanHeaderValue(raw, max) {
    if (!raw)
        return undefined;
    // eslint-disable-next-line no-control-regex
    const clean = raw.trim().replace(/[\u0000-\u001f\u007f]/g, "");
    if (!clean)
        return undefined;
    return clean.length <= max ? clean : clean.slice(0, max);
}
/** Headers for the add-in, read from the environment at call time. */
export function traceHeaders() {
    const headers = {};
    const client = cleanHeaderValue(process.env.REVIT_MCP_CLIENT, MAX_CLIENT);
    const trace = cleanHeaderValue(process.env.REVIT_MCP_TRACE_ID, MAX_TRACE);
    if (client)
        headers["x-mcp-client"] = client;
    if (trace)
        headers["x-mcp-trace"] = trace;
    return headers;
}
let traceErrorReported = false;
/** The add-in error code carried in a tool result's JSON text, if any. */
export function errorCodeOf(result) {
    if (!result?.isError)
        return null;
    const text = result.content?.find((c) => c.type === "text")?.text;
    if (!text)
        return null;
    try {
        const env = JSON.parse(text);
        return env.error?.code ?? null;
    }
    catch {
        return null;
    }
}
/** One trace line (pure; unit-tested). */
export function traceLine(tool, args, result, durationMs, now = new Date()) {
    return {
        ts: now.toISOString(),
        tool,
        durationMs,
        ok: !result?.isError,
        errorCode: errorCodeOf(result),
        reqBytes: Buffer.byteLength(JSON.stringify(args ?? {}), "utf8"),
        respBytes: Buffer.byteLength(JSON.stringify(result?.content ?? []), "utf8"),
        trace: cleanHeaderValue(process.env.REVIT_MCP_TRACE_ID, MAX_TRACE) ?? null,
    };
}
/**
 * Wraps an MCP tool handler so each call appends a trace line when REVIT_MCP_TRACE_FILE is set.
 * The handler's result is returned untouched; a throwing handler is traced and re-thrown.
 */
export function withTrace(tool, handler) {
    return async (...args) => {
        const file = process.env.REVIT_MCP_TRACE_FILE;
        if (!file)
            return handler(...args);
        const started = Date.now();
        let result;
        let thrown;
        try {
            result = await handler(...args);
            return result;
        }
        catch (err) {
            thrown = err;
            throw err;
        }
        finally {
            const asResult = thrown
                ? { isError: true, content: [{ type: "text", text: JSON.stringify({ ok: false, error: { code: "handler_threw" } }) }] }
                : result;
            try {
                appendFileSync(file, JSON.stringify(traceLine(tool, args[0], asResult, Date.now() - started)) + "\n", "utf8");
            }
            catch (e) {
                if (!traceErrorReported) {
                    traceErrorReported = true;
                    console.error(`[revit-mcp-server] trace file write failed (tool calls are unaffected): ${String(e)}`);
                }
            }
        }
    };
}
