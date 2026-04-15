# Revit MCP Server

> A custom **Model Context Protocol** server that lets Claude (Desktop, Code,
> or any MCP client) drive **Autodesk Revit 2026** — read the model, create &
> edit elements, and run multi-step operations as a single undoable
> Transaction.

> Author: KenLP

```
Claude Desktop / Claude Code  ──stdio──▶  revit-mcp-server (Node)  ──HTTP──▶  RevitMCPAddin  ──ExternalEvent + Transaction──▶  Revit 2026
```

- **C# .NET 8 addin** runs inside Revit, owns transactions, marshals work
  onto the main UI thread via `IExternalEventHandler`.
- **TypeScript MCP server** is a thin stdio bridge: every tool just forwards
  to a small HTTP API on the addin.
- **Curated, schema-validated tool surface** — no eval-style escape hatch,
  every write is a named `Transaction` you can review and undo.
- **Batch transaction pattern** — fold N steps into one atomic Revit undo
  entry; rollback on first failure.

For the design rationale and the answer to *"why not just wrap every Revit
API?"*, read [`docs/API_COVERAGE.md`](docs/API_COVERAGE.md).

## Status

**v0.4.0** — Phase 5a complete. 60 C# commands + 1 batch = **61 MCP tools**.
New in v0.4.0: **dry-run mode**, **structured diffs**, **auth token**,
**per-tool risk levels**.
Cross-version (R25/R27) is next; see
[`docs/ROADMAP.md`](docs/ROADMAP.md).

| Layer        | Build target            | Status |
| ------------ | ----------------------- | ------ |
| Revit addin  | Revit 2026 / .NET 8     | ✅      |
| MCP server   | Node 22 / TypeScript 5  | ✅      |
| Revit 2025   | (planned)               | ⏳      |
| Revit 2027   | (planned)               | ⏳      |

## Tool surface (60 commands + 1 batch = 61 MCP tools)

### Diagnostics (3)
`revit_ping` | `revit_get_version` | `revit_get_document_info`

### Inspection / Introspection (21 read-only)
`revit_list_elements` | `revit_get_element_info` | `revit_find_elements` | `revit_get_parameter` | `revit_list_levels` | `revit_list_wall_types` | `revit_list_floor_types` | `revit_list_categories` | `revit_list_families` | `revit_list_family_types` | `revit_list_sheets` | `revit_list_rooms` | `revit_list_materials` | `revit_list_phases` | `revit_list_view_templates` | `revit_get_views` | `revit_get_active_view` | `revit_get_selected_elements` | `revit_get_linked_files` | `revit_get_element_geometry`

### Creation: Architecture (10 write)
`revit_create_wall` | `revit_create_floor` | `revit_create_level` | `revit_create_grid` | `revit_create_room` | `revit_create_column` | `revit_create_beam` | `revit_create_ceiling` | `revit_create_opening_in_wall` | `revit_place_family_instance`

### Creation: Documentation (8 write)
`revit_create_sheet` | `revit_place_view_on_sheet` | `revit_create_floor_plan_view` | `revit_create_section_view` | `revit_create_3d_view` | `revit_create_schedule` | `revit_tag_element` | `revit_create_text_note`

### Edit: Parameters & Naming (3 write)
`revit_set_parameter` | `revit_set_parameter_batch` | `revit_rename_element`

### Edit: Transform (5 write)
`revit_move_element` | `revit_rotate_element` | `revit_copy_element` | `revit_mirror_element` | `revit_array_linear`

### Edit: Delete & Group (3 write)
`revit_delete_elements` | `revit_group_elements` | `revit_ungroup_elements`

### View Manipulation (8 write)
`revit_open_view` | `revit_set_view_detail_level` | `revit_hide_elements_in_view` | `revit_unhide_elements_in_view` | `revit_select_elements` | `revit_zoom_to_elements` | `revit_apply_view_filter` | `revit_color_override_by_param`

### Batch (1)
`revit_batch` — run multiple commands inside ONE Revit Transaction (single undo entry).

Full schemas and examples: [`docs/COMMANDS.md`](docs/COMMANDS.md).

## Repo layout

```
RevitMCPServer/
├── README.md                       ← you are here
├── LICENSE                         ← MIT
├── CHANGELOG.md
├── docs/
│   ├── ARCHITECTURE.md             ← three-layer design + threading model
│   ├── COMMANDS.md                 ← every command's schema + envelope
│   ├── API_COVERAGE.md             ← what we wrap, what we don't, why
│   └── ROADMAP.md                  ← phase tracker
└── src/
    ├── RevitAddin/                 ← C# .NET 8 addin (in-Revit)
    │   ├── App.cs
    │   ├── RevitMCPExternalEventHandler.cs
    │   ├── Server/McpHttpServer.cs
    │   ├── Commands/               ← one IRevitCommand per tool
    │   ├── RevitMCPAddin.csproj
    │   └── RevitMCPAddin.addin
    └── McpServer/                  ← TypeScript MCP stdio server
        ├── src/index.ts
        ├── src/revitClient.ts
        ├── package.json
        └── tsconfig.json
```

## Install — beginner walk-through

If you've never built a dev project before, follow this section top-to-bottom.
Every step is copy-paste into **PowerShell** on Windows.

### Step 0 — What you'll install

| Tool                  | Why                                        | Download                                                       |
| --------------------- | ------------------------------------------ | -------------------------------------------------------------- |
| **Autodesk Revit 2026** | The app the addin plugs into             | Autodesk account (you already have this)                       |
| **.NET 8 SDK**        | Compiles the C# addin (`.dll`)             | <https://dotnet.microsoft.com/download/dotnet/8.0>             |
| **Node.js 22 (LTS)**  | Runs the MCP bridge                        | <https://nodejs.org/>                                          |
| **Git**               | Downloads this repo                        | <https://git-scm.com/download/win>                             |
| **Claude Desktop** *(or Claude Code)* | Your MCP client              | <https://claude.ai/download>                                   |

Install each of the above with the default options. Reboot is not required,
but **open a fresh PowerShell window** after installing them so the `PATH`
picks up `dotnet`, `node`, and `git`.

**Sanity check** — paste this in PowerShell:

```powershell
dotnet --version   # should print 8.x or 10.x
node --version     # should print v22.x (or v18+)
git --version      # should print git version 2.x
```

If any command says "not recognized", re-install that tool and re-open
PowerShell.

### Step 1 — Download the repo

Pick a folder (e.g. `C:\Dev\`) and clone:

```powershell
mkdir C:\Dev -Force
cd C:\Dev
git clone https://github.com/<your-fork>/RevitMCPServer.git
cd RevitMCPServer
```

> Replace `<your-fork>` with the actual repo path. If you downloaded a ZIP
> instead, unzip it to `C:\Dev\RevitMCPServer\`.

### Step 2 — Build the Revit addin (C#)

**Close Revit first** — if Revit is running it locks the DLL and the build
will fail.

```powershell
cd C:\Dev\RevitMCPServer\src\RevitAddin
dotnet build
```

What this does:
1. Compiles `RevitMCPAddin.dll`.
2. Auto-copies the DLL **and** the `.addin` manifest file into
   `%APPDATA%\Autodesk\Revit\Addins\2026\` so Revit finds it next start.

You should see `Build succeeded` at the end. If you see red errors, check:
- Is Revit closed?
- Is Revit 2026 installed at `C:\Program Files\Autodesk\Revit 2026\`? If
  not, override: `dotnet build -p:RevitInstallDir="D:\Your\Path"`.

### Step 3 — Build the MCP bridge (TypeScript)

```powershell
cd C:\Dev\RevitMCPServer\src\McpServer
npm install
npm run build
```

This produces `dist/index.js` — the small Node program Claude will launch.

### Step 4 — Start Revit and verify the addin loaded

1. Open Revit 2026 → open any project (even a blank one).
2. In PowerShell, run:

   ```powershell
   Invoke-RestMethod http://127.0.0.1:7891/health
   ```

   Expected output:
   ```
   ok        : True
   service   : revit-mcp-addin
   version   : 0.4.0
   authEnabled : True
   ```

3. The first time Revit starts after the build, the addin generates a
   random token at:
   ```
   %APPDATA%\Autodesk\Revit\Addins\2026\revit-mcp-token.txt
   ```
   You don't need to read it yourself — the MCP bridge does that
   automatically.

### Step 5 — Tell Claude Desktop about the server

Open (or create) the Claude Desktop config file. The easy way:

```powershell
notepad "$env:APPDATA\Claude\claude_desktop_config.json"
```

Paste this (adjust the path if you cloned somewhere other than `C:\Dev`):

```json
{
  "mcpServers": {
    "revit-mcp-mvp": {
      "command": "node",
      "args": [
        "C:\\Dev\\RevitMCPServer\\src\\McpServer\\dist\\index.js"
      ],
      "env": { "REVIT_MCP_PORT": "7891" }
    }
  }
}
```

> **Important:** the path uses double backslashes (`\\`) because it's JSON.

Save, then **fully quit and restart Claude Desktop** (right-click tray icon →
Quit). When it relaunches, click the 🔨 tools icon — you should see **61
`revit_*` tools**.

### Step 6 — Try your first prompt

In Claude Desktop, with a Revit project open, send:

> *Ping Revit and tell me what version is running.*

Claude will ask permission to run `revit_ping` — click **Allow** (or
**Always allow** to skip future prompts for that tool). You should see a
JSON response confirming Revit 2026.

Next try:

> *List 5 walls in the active document.*

🎉 You're in.

### Troubleshooting

| Symptom                                                            | Fix                                                                                  |
| ------------------------------------------------------------------ | ------------------------------------------------------------------------------------ |
| `dotnet build` → `CS0246: 'Autodesk' could not be found`           | Revit 2026 not installed at default path. Use `-p:RevitInstallDir="..."`.            |
| `dotnet build` → `MSB3027: Could not copy ... file is being used`  | Revit is running. Close it first.                                                    |
| `/health` → "Unable to connect"                                    | Revit is not running, or the addin didn't load. Check `Add-Ins → External Tools` menu. |
| Claude Desktop shows 0 tools                                       | Wrong path in config, or Node not in PATH. Try running `node "C:\Dev\RevitMCPServer\src\McpServer\dist\index.js"` manually — it should wait on stdin without erroring. |
| Tools listed but every call says "401 unauthorized"                | Revit was restarted after Claude read the token. Quit + restart Claude Desktop.      |
| Port 7891 already in use                                           | Another service is on that port. Set `REVIT_MCP_PORT=8123` on both ends (env var in Revit launch + in `claude_desktop_config.json`). |

---

## Build (reference)

Quick-reference for developers.

### 1. C# Revit addin (Revit 2026)

Requires **.NET 8 SDK** (or newer) and a local Revit 2026 install at
`C:\Program Files\Autodesk\Revit 2026\` (override with `-p:RevitInstallDir=...`).

```bash
cd src/RevitAddin
dotnet build
```

Post-build copies `RevitMCPAddin.dll` and the `.addin` manifest into
`%APPDATA%\Autodesk\Revit\Addins\2026\`. Skip auto-deploy with
`-p:DeployToRevit=false`.

> **Heads up:** close Revit before rebuilding — it holds the DLL open.

To target a different Revit version override `RevitVersion`:

```bash
dotnet build -p:RevitVersion=2027
```

> **Revit 2025** uses the same .NET 8 runtime as 2026, so the source *should*
> compile with `-p:RevitVersion=2025`. This is **untested by the maintainer**
> (no 2025 install on the dev machine). Cross-version `#if` shims are
> planned — see [`docs/ROADMAP.md`](docs/ROADMAP.md) Phase 3.

### 2. TypeScript MCP server

Requires **Node 18+** (Node 22 tested).

```bash
cd src/McpServer
npm install
npm run build
```

Outputs `dist/index.js`, runnable as `node dist/index.js`.

## Run

### 1. Start Revit 2026

Open any project. The addin auto-loads and starts the HTTP listener on
`http://127.0.0.1:7891/`. On first start it generates a random auth token
at `%APPDATA%\Autodesk\Revit\Addins\2026\revit-mcp-token.txt`.

Sanity check:

```powershell
Invoke-RestMethod http://127.0.0.1:7891/health
# → ok=True, service=revit-mcp-addin, version=0.4.0, authEnabled=True

# Authenticated request (read the token first):
$token = Get-Content "$env:APPDATA\Autodesk\Revit\Addins\2026\revit-mcp-token.txt"
Invoke-RestMethod http://127.0.0.1:7891/commands -Headers @{ Authorization = "Bearer $token" }
# → list of all registered commands + isReadOnly + riskLevel
```

> 💡 **PowerShell quoting tip.** Don't use `curl.exe` with `-d "{\"...\"}"`
> in PowerShell — Windows arg marshalling eats the quotes. Use
> `Invoke-RestMethod -Body '{"command":"ping","params":{}}'` instead.

### 2. Wire up your MCP client

**Claude Desktop** (`%APPDATA%\Claude\claude_desktop_config.json`) or
**Claude Code** (`~/.claude/settings.json` `mcpServers` block):

```json
{
  "mcpServers": {
    "revit-mcp-mvp": {
      "command": "node",
      "args": [
        "C:\\path\\to\\RevitMCPServer\\src\\McpServer\\dist\\index.js"
      ],
      "env": { "REVIT_MCP_PORT": "7891" }
    }
  }
}
```

Restart the client. You should see 61 `revit_*` tools appear.

### 3. Try it

> *Ping Revit, then list 5 walls in the active document.*

> *Create level "L4" at 12 m, then a grid line "1" from (0,0) to (30,0),
> then 3 walls along the grid — all in one batch so I can undo it as one
> step.*

> *Find all walls on level "Level 1" and set their `Comments` parameter to
> "Reviewed by AI".*

The batch flow uses `revit_batch`, which runs every step in **one** Revit
`Transaction`. Ctrl+Z in Revit reverts the whole batch.

## How the threading + transaction model works

Revit's API can only be called on the main UI thread, and writes must be
inside a `Transaction`. We hide both rules from command authors:

1. `McpHttpServer` receives a request on a thread-pool thread.
2. It enqueues a `PendingRequest` (with a `TaskCompletionSource`), raises an
   `ExternalEvent`, and awaits the TCS.
3. Revit invokes `Execute(UIApplication)` on the main thread; the dispatcher
   drains the queue.
4. For each request:
   - **Read-only** commands run with no transaction.
   - **Single write** commands are wrapped in `Transaction(doc, "MCP: <cmd>")`.
   - **Batches** open one `Transaction(doc, "MCP: Batch (n ops)")` and run
     every sub-command inside it.
5. The TCS completes; the HTTP handler resumes and writes the response.

Commands themselves are stateless `IRevitCommand` implementations — they
just call the Revit API and return JSON. They never see a `Transaction`.
Full details in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Response envelope

Every command — single or batched — returns the same shape:

```jsonc
// success
{ "ok": true, "data": { /* command-specific */ } }

// failure
{ "ok": false, "error": { "code": "not_found", "message": "Level 'L99' not found." } }
```

Batches add `committed`, `count`, `hadFailures`, `results[]`. See
[`docs/COMMANDS.md`](docs/COMMANDS.md#batch).

## Configuration env vars

| Variable                | Where         | Default     | Purpose                                    |
| ----------------------- | ------------- | ----------- | ------------------------------------------ |
| `REVIT_MCP_PORT`        | Revit + Node  | `7891`      | HTTP port the addin listens on             |
| `REVIT_MCP_HOST`        | Node only     | `127.0.0.1` | Host the MCP server connects to            |
| `REVIT_MCP_TIMEOUT_MS`  | Node only     | `30000`     | Per-tool-call HTTP timeout                 |
| `REVIT_MCP_AUTH`        | Revit + Node  | (enabled)   | Set `false` to disable auth token          |
| `REVIT_MCP_AUTH_TOKEN`  | Node only     | (auto-read) | Override: use this token instead of file    |
| `REVIT_MCP_VERSION`     | Node only     | `2026`      | Revit version (for token file path)         |

## Dry-run mode

Every write tool accepts `"dryRun": true`. The command executes normally
but the transaction is **rolled back** — the model is unchanged.

```jsonc
// HTTP
POST /mcp?dryRun=true
{ "command": "create_wall", "params": { ... } }

// MCP tool
revit_create_wall({ ..., dryRun: true })
```

Response includes `"dryRun": true, "committed": false` alongside the normal
data payload so the AI can preview the result and ask the user to confirm
before running it for real.

## Structured diffs

Write commands return a `changeSummary` one-liner and, where applicable, a
`changes` object with `before`/`after` values:

```jsonc
{
  "ok": true,
  "data": {
    "id": 184239,
    "parameterName": "Comments",
    "changeSummary": "Set 'Comments' on element 184239: '' → 'Reviewed by AI'",
    "changes": { "before": "", "after": "Reviewed by AI" }
  }
}
```

The AI should show the concise `changeSummary` by default, and only expand
the full `changes` diff when the user asks for details.

## Auth token

On startup the addin generates a cryptographically random token and writes
it to `%APPDATA%\Autodesk\Revit\Addins\<version>\revit-mcp-token.txt`.
The TypeScript MCP server reads this file automatically.

- `GET /health` is **exempt** from auth (so clients can detect the addin).
- All other endpoints require `Authorization: Bearer <token>`.
- Set `REVIT_MCP_AUTH=false` (env var on Revit) to disable auth entirely.
- Or pass `REVIT_MCP_AUTH_TOKEN=<token>` to the MCP server to use a
  fixed token.

## Per-tool risk levels

`GET /commands` now returns a `riskLevel` for each command:

| Level    | Meaning                                      | Examples                            |
| -------- | -------------------------------------------- | ----------------------------------- |
| `read`   | Read-only, no model changes                  | `ping`, `list_elements`             |
| `low`    | Creates new elements (easily undoable)        | `create_wall`, `create_level`       |
| `medium` | Modifies existing elements                   | `set_parameter`, `move_element`     |
| `high`   | Deletes or hard-to-reverse                    | `delete_elements`, `ungroup`        |

MCP clients can use this to decide which tools need per-call confirmation.

## Security model

- The HTTP listener binds **only** to `127.0.0.1`.
- **Auth token**: a random Bearer token is generated each Revit session and
  required on all endpoints except `/health`. Disable with
  `REVIT_MCP_AUTH=false`.
- **Dry-run mode**: lets you preview AI actions without committing.
- **Risk levels**: `GET /commands` exposes per-tool risk, so clients can
  auto-allow `read` tools and prompt on `high` tools.
- Never run this addin on a machine where untrusted local processes might
  reach `127.0.0.1:7891` (e.g. a multi-tenant build agent).

## Inspirations

- [`mcp-servers-for-revit/mcp-servers-for-revit`](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)
  — best-organised fork of the original PiggyYan project, multi-version CI.
- [`revit-mcp/revit-mcp`](https://github.com/revit-mcp/revit-mcp) — original
  TypeScript + C# project. We deliberately do **not** ship its arbitrary
  C# eval tool.
- [`revit-mcp/revit-mcp-python`](https://github.com/revit-mcp/revit-mcp-python)
  — pyRevit-based, Python-first.

The full comparison and the project's roadmap rationale live in
[`docs/ROADMAP.md`](docs/ROADMAP.md) and [`docs/API_COVERAGE.md`](docs/API_COVERAGE.md).

## License

[MIT](LICENSE).
