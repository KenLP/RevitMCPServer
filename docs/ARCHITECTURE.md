# Architecture

```
┌────────────────────┐  stdio (MCP / JSON-RPC)  ┌────────────────────┐
│  Claude Desktop /  │ ◀──────────────────────▶ │  revit-mcp-server  │
│  Claude Code       │                          │  (Node, this repo) │
└────────────────────┘                          └────────┬───────────┘
                                                         │ HTTP (loopback)
                                                         │ POST /mcp
                                                         │ POST /mcp/batch
                                                         ▼
                                                ┌────────────────────┐
                                                │  RevitMCPAddin     │
                                                │  (C# .NET 8,       │
                                                │  in Revit process) │
                                                │                    │
                                                │  ┌──────────────┐  │
                                                │  │ HttpListener │  │
                                                │  │ (bg thread)  │  │
                                                │  └──────┬───────┘  │
                                                │         │ enqueue  │
                                                │         ▼          │
                                                │  ┌──────────────┐  │
                                                │  │ ExternalEvent│  │
                                                │  │   handler    │  │
                                                │  │ (UI thread)  │  │
                                                │  │  Transaction │  │
                                                │  │  + dispatch  │  │
                                                │  └──────┬───────┘  │
                                                └─────────┼──────────┘
                                                          │
                                                          ▼
                                                  ┌──────────────┐
                                                  │  Revit API   │
                                                  └──────────────┘
```

## Why three layers?

| Layer | Why it exists |
|---|---|
| **MCP server (Node/stdio)** | MCP clients (Claude Desktop, Claude Code) speak JSON-RPC over stdio. The Node process owns that contract: tool names, schemas, descriptions. It is intentionally **dumb** — every tool just forwards to HTTP. Swap clients without touching Revit. |
| **HTTP bridge (in-Revit)** | Revit cannot easily host an MCP transport itself. A tiny `HttpListener` lets *anything* talk to the addin (curl, Postman, integration tests, future GUIs) without depending on the MCP SDK. Auth via random Bearer token generated per session; `GET /health` is exempt. |
| **ExternalEvent + Transaction dispatcher** | Revit API can only be called on the main UI thread. Background HTTP requests are queued and drained inside `IExternalEventHandler.Execute(UIApplication)`. The dispatcher also owns the `Transaction` lifecycle so commands stay free of boilerplate. |

## Threading & transaction lifecycle

A single command call:

1. `McpHttpServer` receives `POST /mcp` on a thread-pool thread.
2. It calls `RevitMCPExternalEventHandler.EnqueueAsync(command, params)`,
   which constructs a `PendingRequest` (carrying a `TaskCompletionSource`),
   pushes it on a `ConcurrentQueue`, raises the `ExternalEvent`, and
   **awaits** the TCS.
3. Revit invokes `Execute(UIApplication)` on the main thread. The handler
   drains the queue.
4. For each request the handler:
   - Builds a `CommandContext` (`App`, `Doc`, `Parameters`).
   - If the command is read-only → just calls `command.Execute(ctx)`.
   - Otherwise opens `using var tx = new Transaction(doc, "MCP: <name>")`,
     calls `command.Execute(ctx)`:
     - **Normal mode**: commits on success / rolls back on exception.
     - **Dry-run mode** (`?dryRun=true`): always rolls back, but still
       returns the result data so the caller can preview what *would*
       have happened.
   - Sets the result on the TCS.
5. The HTTP handler resumes, serialises the envelope, writes the response.

A batch call (`POST /mcp/batch`):

1. Same enqueue / drain mechanism, but the `PendingRequest` carries a
   `IList<BatchStep>`.
2. The handler resolves every step's `IRevitCommand` up-front (errors out
   early on unknown command names).
3. If every step is read-only, no transaction is opened.
4. Otherwise: open one `Transaction` named `"MCP: Batch (n ops)"`, run every
   step inside it, capture per-step results into a `JsonArray`.
5. On the first failing step, if `stopOnError` is true (default), the
   transaction is rolled back and the rest of the steps are skipped.
6. Otherwise the batch continues, individual failures are recorded in
   `results`, and `hadFailures: true` is reported.
7. Successful batches commit as a single undo entry — Ctrl+Z in Revit
   undoes the whole AI action, not just the last sub-step.

## Why a single dispatcher owns transactions

The original MVP had each command opening its own `Transaction`. That broke
two important use cases:

1. **Atomic AI ops**: an AI generating "create level 4, create grid B, place
   3 walls" wants the whole thing to be one undo entry — and to roll back
   cleanly if any step fails.
2. **Performance**: opening N transactions for N walls is much slower than
   one transaction for N walls.

Centralising transactions also keeps individual command files focused on
the actual Revit API call, not boilerplate.

## File map

```
src/RevitAddin/
├── App.cs                              # IExternalApplication entry point
├── RevitMCPExternalEventHandler.cs     # main-thread bridge + transaction owner
├── Server/
│   └── McpHttpServer.cs                # HttpListener, /mcp, /mcp/batch, /commands, /health
└── Commands/
    ├── IRevitCommand.cs                # contract every command implements
    ├── CommandContext.cs               # request scope (App, Doc, Parameters)
    ├── CommandRegistry.cs              # name → IRevitCommand
    ├── JsonResult.cs                   # response envelope helpers
    ├── ParamUtil.cs                    # P.Str / P.Dbl / P.Xyz / …
    ├── PingCommand.cs
    ├── GetVersionCommand.cs
    ├── ListElementsCommand.cs
    ├── GetElementInfoCommand.cs
    ├── ListLevelsCommand.cs
    ├── ListWallTypesCommand.cs
    ├── ListFloorTypesCommand.cs
    ├── ListCategoriesCommand.cs
    ├── CreateWallCommand.cs
    ├── CreateFloorCommand.cs
    ├── CreateLevelCommand.cs
    ├── CreateGridCommand.cs
    ├── SetParameterCommand.cs
    ├── DeleteElementsCommand.cs
    └── MoveElementCommand.cs

src/McpServer/
├── src/
│   ├── index.ts                        # server.tool(...) declarations
│   └── revitClient.ts                  # HTTP client + envelope helpers
├── package.json
└── tsconfig.json
```

## Auth & security

On startup `App.cs` generates a 32-byte random token using
`RandomNumberGenerator` and writes it to
`%APPDATA%\Autodesk\Revit\Addins\<version>\revit-mcp-token.txt`.
`McpHttpServer` checks `Authorization: Bearer <token>` on every request
except `GET /health` (exempt so clients can detect whether the addin is
running). The TypeScript MCP server reads the token file automatically;
override with `REVIT_MCP_AUTH_TOKEN` env var or disable with
`REVIT_MCP_AUTH=false`.

## Dry-run mode

Any POST to `/mcp` or `/mcp/batch` can include `dryRun: true` (in the
JSON body) or `?dryRun=true` (query string). The dispatcher runs the
command inside a Transaction as normal, but **always rolls back** instead
of committing. The response still contains the full result data so the
caller can preview what would happen. The response includes
`"dryRun": true, "committed": false`.

## Risk levels & structured diffs

Each `IRevitCommand` exposes a `RiskLevel` property: `read`, `low`,
`medium`, or `high`. `GET /commands` surfaces this so clients can build
per-tool permission policies.

Write commands return a `changeSummary` one-liner and (where applicable)
a `changes` object with before/after values. The AI should show the
summary by default and only reveal full diffs on demand.

## Adding a new command

1. **C# side**: implement `IRevitCommand` in `src/RevitAddin/Commands/`.
   Set `IsReadOnly` correctly. Don't open a `Transaction`.
2. Register it in `CommandRegistry.RegisterDefaults()`.
3. **TypeScript side**: add a `server.tool(...)` declaration in `index.ts`
   that forwards to `callRevit("your_command", params)`.
4. `dotnet build` (the post-build target deploys to
   `%APPDATA%\Autodesk\Revit\Addins\2026`).
5. `npm run build` in `src/McpServer/`.
6. Restart Revit and the MCP client.
