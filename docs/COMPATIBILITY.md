# Compatibility Matrix

## Revit × .NET × Node.js

| Revit Version | .NET Framework | Status | Notes |
|---|---|---|---|
| Revit 2025 | .NET 8 (windows) | ✅ CI-tested | Nice3point ref assemblies (v2025.2.0), port 7890, release artifact included |
| Revit 2026 | .NET 8 (windows) | ✅ Tested | Primary development target |
| Revit 2027 | .NET 10 (windows) | ✅ Tested | Auto-selected via MSBuild condition |
| Revit 2028+ | .NET 10+ (windows) | ⚠️ Unknown | Extend `TargetFramework` condition in `.csproj` |

## Node.js

| Node.js version | Status | Notes |
|---|---|---|
| 18.x | ✅ Minimum | Requires native `fetch` (added in Node 18) |
| 20.x | ✅ Tested | LTS — recommended |
| 22.x | ✅ Tested | Current LTS — used in CI |
| < 18 | ❌ Not supported | No native `fetch`; use Node 18+ |

## Claude Desktop

| Version | Status |
|---|---|
| Any current release | ✅ Compatible |

## Side-by-side multi-version

Running Revit 2026 and 2027 simultaneously requires separate Claude Desktop
config entries pointing to different ports:

| Revit | Default port |
|---|---|
| 2025 | 7890 |
| 2026 | 7891 |
| 2027 | 7892 |
| 2028 | 7893 |

Port is derived automatically: `7891 + (RevitVersion - 2026)`.  
Override with `REVIT_MCP_PORT` env var in the Claude Desktop config.

## Known limits

- **Windows only.** The C# addin requires Windows (Revit is Windows-only).
  The TypeScript MCP server runs cross-platform but is only useful alongside
  a running Revit instance.
- **One instance per Revit process.** The HTTP listener binds to
  `127.0.0.1:<port>`. Each Revit instance occupies one port.
- **DLL lock during Revit.** You cannot rebuild the C# addin while Revit
  has the DLL loaded. Close Revit, rebuild, reopen.
- **R2025 not smoke-tested against a live Revit 2025 install.** CI confirms
  the build compiles and all unit tests pass, but runtime behaviour has not
  been verified against an actual Revit 2025 process.
