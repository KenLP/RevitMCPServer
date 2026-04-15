using System;
using System.IO;
using System.Security.Cryptography;
using Autodesk.Revit.UI;
using RevitMCPAddin.Commands;
using RevitMCPAddin.Server;

namespace RevitMCPAddin;

/// <summary>
/// Entry point for the Revit MCP Addin.  Loaded by Revit at startup via the
/// .addin manifest.  Starts an in-process HTTP server that the external MCP
/// server (TypeScript / stdio) talks to, and wires up an ExternalEvent so all
/// Revit API work runs on the main UI thread.
///
/// Auth: on startup a random 32-byte token is generated and written to
/// <c>%APPDATA%\Autodesk\Revit\Addins\{version}\revit-mcp-token.txt</c>.
/// The TypeScript MCP server reads that file to authenticate HTTP requests.
/// Set env var <c>REVIT_MCP_AUTH=false</c> to disable auth entirely.
/// </summary>
public sealed class App : IExternalApplication
{
    // Default port — can be overridden via environment variable REVIT_MCP_PORT
    // before launching Revit.
    private const int DefaultPort = 7891;

    private RevitMCPExternalEventHandler? _handler;
    private ExternalEvent? _externalEvent;
    private McpHttpServer? _httpServer;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            var registry = new CommandRegistry();
            registry.RegisterDefaults();

            _handler = new RevitMCPExternalEventHandler(registry);
            _externalEvent = ExternalEvent.Create(_handler);
            _handler.AttachExternalEvent(_externalEvent);

            var port = ResolvePort();
            var authToken = ResolveAuthToken(application.ControlledApplication.VersionNumber);

            _httpServer = new McpHttpServer(port, _handler, authToken);
            _httpServer.Start();

            var authStatus = authToken is not null ? "auth=ON" : "auth=OFF";
            LogToConsole($"[RevitMCP] Listening on http://127.0.0.1:{port}/ ({authStatus})");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Revit MCP Addin",
                "Failed to start MCP HTTP server:\n\n" + ex);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            _httpServer?.Stop();
        }
        catch
        {
            // best effort
        }
        return Result.Succeeded;
    }

    private static int ResolvePort()
    {
        var raw = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
        if (int.TryParse(raw, out var p) && p > 0 && p < 65536)
            return p;
        return DefaultPort;
    }

    /// <summary>
    /// Generates a random auth token and writes it to the addins folder.
    /// Returns null if auth is explicitly disabled via REVIT_MCP_AUTH=false.
    /// </summary>
    private static string? ResolveAuthToken(string revitVersion)
    {
        var authEnv = Environment.GetEnvironmentVariable("REVIT_MCP_AUTH");
        if (string.Equals(authEnv, "false", StringComparison.OrdinalIgnoreCase))
            return null;

        // Generate a cryptographically secure random token.
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(bytes);
        var token = Convert.ToBase64String(bytes);

        // Write to a well-known location so the MCP server can read it.
        try
        {
            var addinsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", revitVersion);
            var tokenPath = Path.Combine(addinsDir, "revit-mcp-token.txt");
            File.WriteAllText(tokenPath, token);
            LogToConsole($"[RevitMCP] Auth token written to {tokenPath}");
        }
        catch (Exception ex)
        {
            LogToConsole($"[RevitMCP] Warning: could not write auth token file: {ex.Message}");
        }

        return token;
    }

    private static void LogToConsole(string message)
    {
        try { System.Diagnostics.Debug.WriteLine(message); } catch { }
    }
}
