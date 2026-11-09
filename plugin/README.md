# RevitMCP plugin

Lets Claude read, create and edit elements in the Revit model you have open: walls, rooms,
views, sheets, schedules, parameters, IFC export and more, about 100 tools in all. The plugin
holds the MCP bridge and a skill on how to use it. The work happens in the **RevitMCP add-in**,
which runs inside Revit and which you install separately.

RevitMCP is an independent open-source project. It is not made, endorsed or supported by
Autodesk. Revit is a trademark of Autodesk, Inc.

## Requirements

- Windows with Revit 2025, 2026 or 2027
- [Node.js 18 or later](https://nodejs.org/) on your PATH
- Claude Code (terminal, IDE or the desktop app's Code tab), or Cowork on the same computer.
  Chat on claude.ai does not run local servers.

## Install

1. **Install the add-in.** Download `RevitMCPServer-v<version>.zip` from the
   [latest release](https://github.com/KenLP/RevitMCPServer/releases/latest), close Revit,
   extract the zip, then run in PowerShell:

   ```powershell
   .\install.ps1 -NoClientConfig
   ```

   `-NoClientConfig` installs only the add-in and leaves your MCP client configuration alone,
   since this plugin provides the server.
2. **Install the plugin** in Claude Code:

   ```text
   /plugin marketplace add KenLP/RevitMCPServer
   /plugin install revit-mcp-bimlynx@bimlynx
   ```

   When asked, enter the **Revit version** you run (default 2026). Leave **Tool groups** empty
   to expose every tool.
3. **Start Revit**, open a model, and ask Claude something like "list the levels in this model".

Use the plugin version that matches the add-in's release. Check the add-in with
`Invoke-RestMethod http://127.0.0.1:7891/health` (port 7890 for 2025, 7892 for 2027).

## What it runs and where data goes

- **One local process.** Claude Code starts `node server/index.js` from this plugin. Its
  dependencies (`@modelcontextprotocol/sdk`, `zod`) are pinned in `package-lock.json` and installed
  by Claude Code with install scripts disabled.
- **Loopback only.** The bridge talks to the add-in at `127.0.0.1` on the port for your Revit
  version and refuses any other host. It authenticates with a token the add-in writes to
  `%APPDATA%\Autodesk\Revit\Addins\<version>\revit-mcp-token.txt` each time Revit starts.
- **No external calls.** Neither the bridge nor the add-in sends anything to the internet,
  collects telemetry or calls an AI model. What Claude receives is what each tool returns. The
  add-in's ribbon panel is a browser view of a URL you configure, by default a local service at
  `127.0.0.1:8601`; with nothing there it shows a connection error.
- **Files on your machine.** The add-in keeps an audit log of the commands it ran (parameters are
  stored as hashes) under `%APPDATA%\RevitMCP\audit`, and a request log under
  `%LOCALAPPDATA%\RevitMCP\logs`. Tools that write files (IFC, PDF, CSV, saving the model) write
  only to the path the call names.
- **Changes to your model** happen only through tools marked as writes, inside Revit transactions
  you can undo. Every write tool accepts `dryRun` to preview the change first.

## Privacy

This plugin collects no personal data and sends nothing to the plugin's author or to third
parties. All processing stays on your computer, between Claude Code, the bridge and Revit.

## Configuration

| Option | Default | Meaning |
| --- | --- | --- |
| Revit version | `2026` | Picks the port: 2025 → 7890, 2026 → 7891, 2027 → 7892 |
| Tool groups | empty | Comma-separated groups to expose; empty exposes all |

Further settings (timeouts, an explicit port) are environment variables, listed in the
[project README](https://github.com/KenLP/RevitMCPServer#configuration-env-vars).

## Support

Issues and questions: <https://github.com/KenLP/RevitMCPServer/issues>

## License

MIT, see [LICENSE](LICENSE).
