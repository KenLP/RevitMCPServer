# HelloPack — a minimal command pack

A **command pack** is a .NET class library whose public `IRevitCommand` classes the add-in
registers at start-up, alongside its built-in commands. Packs are **opt-in**: nothing is loaded
unless `revit-mcp-packs.json` lists them.

Pack commands are reachable over HTTP only (`POST /mcp`, `POST /mcp/batch`) — the MCP tool list
is fixed on the Node side and never includes them. They run through the same dispatcher, so they
get the same transaction, dry-run, batch, auth and error-envelope behaviour as built-in commands.

## Build

```bash
dotnet build samples/HelloPack/HelloPack.csproj -c Release -p:RevitVersion=2027
```

`RevitVersion` picks the framework the same way the add-in does (2025/2026 → net8, 2027 → net10).
Build the pack against the **same RevitMCP version** as the add-in it will run in.

## Install

1. Copy `bin/Release/HelloPack.dll` — **only that file**, never a `RevitMCP.Core.dll` — to
   `%APPDATA%\Autodesk\Revit\Addins\2027\RevitMCP.Packs\`.
2. Create `%APPDATA%\Autodesk\Revit\Addins\2027\revit-mcp-packs.json`:

   ```json
   { "packs": [ "RevitMCP.Packs/HelloPack.dll" ] }
   ```

   Relative paths resolve against that Addins folder. Use forward slashes (or `\\`): a single
   backslash is an invalid JSON escape and the whole file is rejected. `"enabled": false` turns
   every pack off without deleting the file.
3. Restart Revit, then check:

   ```powershell
   $t = Get-Content "$env:APPDATA\Autodesk\Revit\Addins\2027\revit-mcp-token.txt"
   (Invoke-RestMethod http://127.0.0.1:7892/commands -Headers @{ Authorization = "Bearer $t" }).data.packs
   ```

## Writing a command

```csharp
public sealed class HelloPackCommand : IRevitCommand
{
    public string Name => "hello_pack";          // a-z, 0-9, _ only
    public bool IsReadOnly => true;
    public JsonNode? Execute(CommandContext ctx) => new JsonObject { ["title"] = ctx.Doc?.Title };
}
```

Rules the loader enforces:

- Each command needs a **public parameterless constructor**. If any constructor in the pack throws,
  the whole pack is rejected — a pack never loads half-way.
- A pack **cannot replace** a command that already exists (built-in or from an earlier pack). That
  command is skipped and reported; the rest of the pack still loads.
- Do not open your own `Transaction` — the dispatcher wraps write commands (see `IRevitCommand`).

Everything a pack needs is public in `RevitMCP.Core`: `IRevitCommand`, `ExecutionKind`,
`CommandContext`, the `P` parameter helpers and `RevitCommandException`.

## When a pack does not load

`GET /commands` returns a `packs` array with each pack's `commands`, `skipped` (with reasons) and
`error`. `GET /health` reports `builtinCommandCount` and `packCommandCount`. A missing file, a
malformed config or a pack built against an incompatible Core is reported there and skipped — it
never stops the add-in.
