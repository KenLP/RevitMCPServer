# Troubleshooting Guide

## Diagnostic checklist

Before diving into specific symptoms, verify:

1. Revit is open with a project loaded.
2. The addin DLL and `.addin` manifest are in `%APPDATA%\Autodesk\Revit\Addins\<version>\`.
3. Revit was restarted after the addin was installed.
4. Node.js 18+ is in `PATH` (`node --version`).
5. Claude Desktop is running and the MCP config references the correct `index.js` path.

---

## "Revit addin not reachable"

**Symptom:** The MCP server logs `WARNING: Revit addin not reachable at http://127.0.0.1:7891`.

**Causes and fixes:**

| Cause | Fix |
|---|---|
| Revit not open | Open Revit |
| Addin not installed | Run `.\scripts\install.ps1 -RevitVersion 2026` |
| Addin failed to load | Check the Revit journal file (`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2026\Journals\`) for load errors |
| Wrong port | Verify `REVIT_MCP_PORT` in Claude Desktop config matches the Revit version (2026→7891, 2027→7892) |
| Firewall blocking loopback | Add an exception for `127.0.0.1:7891` or disable the firewall rule for local traffic |

---

## "401 Unauthorized" on every call

**Symptom:** Every tool call returns `{"ok":false,"error":{"code":"unauthorized",...}}`.

**Causes and fixes:**

| Cause | Fix |
|---|---|
| Token file not readable | Ensure `%APPDATA%\Autodesk\Revit\Addins\<version>\revit-mcp-token.txt` exists and is readable by the current user |
| Stale token after Revit restart | The MCP server auto-refreshes the token on a 401. If it keeps failing, restart Claude Desktop |
| `REVIT_MCP_AUTH_TOKEN` set to old value | Remove the env var from Claude Desktop config and let the server read the file |
| Addin reports `authEnabled:false` on `/health` | You are running an addin build older than 0.8.17 with auth disabled. Upgrade the addin — from 0.8.17 token auth is always on and `REVIT_MCP_AUTH=false` is ignored |

---

## "No active document"

**Symptom:** Commands that need a model return `This command requires an active Revit document`.

**Fix:** Open a Revit project (`.rvt`) — the addin requires an active document for model commands.
Diagnostic-only commands (`ping`, `get_revit_version`) work without an open project.

---

## "Parameter not found"

**Symptom:** `set_parameter` returns `Element 12345 has no parameter named 'Xyz'`.

**Fix:**
- Use `revit_get_element_info` to list all parameters on the element and verify the exact name.
- Parameter names are case-sensitive and locale-dependent. The name you see in the Revit UI may differ from the internal name.
- Built-in parameters have English names regardless of the Revit UI language.

---

## "Cannot find parameter on any category element" (apply_view_filter)

**Symptom:** `apply_view_filter` fails with "No elements of category X found in the document."

**Fix:** The filter creation requires at least one placed element of the target category to resolve the parameter. Place a sample element first, then create the filter.

---

## "View does not support graphic overrides"

**Symptom:** `apply_view_filter` or `color_override_by_param` fails with "does not support graphic overrides."

**Fix:** Schedules, legends, and some 3D perspective views do not support graphic overrides. Use a floor plan, elevation, section, or isometric view.

---

## DLL lock error when rebuilding

**Symptom:** `dotnet build` fails with `Cannot access file RevitMCPAddin.dll because it is being used by another process`.

**Fix:** Close Revit completely before rebuilding. Revit holds the DLL open while it is loaded.

---

## Addin doesn't appear in Revit

**Symptom:** No "Revit MCP" entry in the Revit Add-ins tab; commands return `transport_error`.

**Checklist:**
1. Verify `RevitMCPAddin.dll` AND `RevitMCPAddin.addin` are both in `%APPDATA%\Autodesk\Revit\Addins\<version>\` (not just one of them).
2. Check the `.addin` file XML is valid and `<Assembly>RevitMCPAddin.dll</Assembly>` matches the actual DLL filename.
3. Review the Revit journal for `AddinError` or `AddinLoadException` entries.
4. Ensure the DLL targets the correct .NET version: net8.0-windows for R2026, net10.0-windows for R2027.

---

## Node.js: `fetch is not a function`

**Symptom:** MCP server crashes with `TypeError: fetch is not a function`.

**Fix:** Upgrade Node.js to 18 or later. Native `fetch` was added in Node 18.

---

## Multiple Revit versions conflict

**Symptom:** Commands from one version affect the wrong Revit session.

**Fix:** Each Revit version uses a different port (7891 for R2026, 7892 for R2027). Ensure each Claude Desktop MCP config entry sets both `REVIT_MCP_VERSION` and `REVIT_MCP_PORT` correctly. See [COMPATIBILITY.md](COMPATIBILITY.md) for the port table.

---

## Collecting logs for bug reports

- **Revit addin logs:** Check the Revit journal file at `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <version>\Journals\`.
- **MCP server logs:** Claude Desktop captures stderr. Find logs in `%APPDATA%\Claude\logs\` (Windows).
- **Health check:** `curl http://127.0.0.1:7891/health` returns `{"ok":true,"service":"revit-mcp-addin","version":"...","authEnabled":true/false}` when the addin is running.
- **Command list:** `curl http://127.0.0.1:7891/commands` (with auth header) lists all registered commands with `name`, `isReadOnly`, `riskLevel`, and `executionKind` (`ReadOnly` / `ModelWrite` / `UiAction`).
- **Structured request log:** one JSON line per request at `%LOCALAPPDATA%\RevitMCP\logs\revit-mcp-<date>.log` (`ts`, `requestId`, `method`, `path`, `status`, `ok`, `durationMs`, `inFlight`). Correlate with a response's `X-Request-Id` header.
- **Server stats:** `GET /stats` (with auth header) returns total / success / failed / rejected request counts, current in-flight, and average / peak duration.
- **Live smoke test:** run `scripts/smoke-test.ps1 -Version <year>` against a running Revit to confirm the whole HTTP surface end-to-end (see [SMOKE_TESTING.md](SMOKE_TESTING.md)).

---

## Fewer tools than expected / a tool is missing

**Symptom:** The client only sees some tools (e.g. no `revit_create_*`).

**Fix:** Check the `REVIT_MCP_PROFILE` env var in your MCP client config. When set, only the
listed groups (+ `core`) are exposed. Remove it to expose all 93 tools, or add the group you
need (`inspection`, `architecture`, `documentation`, `editing`, `view`, `model-health`,
`coordination`). The MCP server logs the active profiles and tool count to stderr on startup.

---

## HTTP 413 / 429-style 503 / 400 "too_many_steps"

These are the server's load-shedding limits, not bugs:

- **413 `payload_too_large`** — request body exceeds 1 MB. Split the work.
- **503 `overloaded`** (with `Retry-After`) — more than 32 requests in flight. Retry shortly.
- **400 `too_many_steps`** — a batch has more than 200 steps. Split into smaller batches.
