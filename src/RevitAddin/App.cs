using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Autodesk.Revit.UI;
using RevitMCPAddin.Commands;
using RevitMCPAddin.Panel;
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
/// Token auth is unconditional — there is no switch to disable it (the old
/// REVIT_MCP_AUTH=false escape hatch was removed in 0.8.17).
/// </summary>
public sealed class App : IExternalApplication
{
    // Default base port.  When multiple Revit versions run side-by-side each
    // needs its own port.  The addin auto-assigns:
    //   Revit 2026 → 7891,  2027 → 7892,  2028 → 7893, …
    // Override with env var REVIT_MCP_PORT before launching Revit.
    private const int DefaultBasePort = 7891;
    private const int BaseRevitYear = 2026;

    private RevitMCPExternalEventHandler? _handler;
    private ExternalEvent? _externalEvent;
    private McpHttpServer? _httpServer;
    private AutoAuditPanelView? _panelView;
    private AutoAuditPanelView? _spatialQcPanelView;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            var registry = new CommandRegistry();
            registry.RegisterDefaults();

            _handler = new RevitMCPExternalEventHandler(registry);
            _externalEvent = ExternalEvent.Create(_handler);
            _handler.AttachExternalEvent(_externalEvent);

            var revitVersion = application.ControlledApplication.VersionNumber;
            var port = ResolvePort(revitVersion);
            var authToken = ResolveAuthToken(revitVersion);

            _httpServer = new McpHttpServer(port, _handler, authToken);
            _httpServer.Start();

            // Log the actual build so the Revit journal / DebugView shows which dll
            // was loaded — the fastest way to spot a stale dll shadowing a newer one
            // without hitting /health.
            LogToConsole(
                $"[RevitMCP] Build {BuildInfo.Version} " +
                $"({BuildInfo.GitBranch}@{BuildInfo.GitCommit}, {BuildInfo.GitState}, " +
                $"{BuildInfo.BuildTimestampUtc}) — {registry.Count} commands, " +
                $"capability {BuildInfo.CapabilityHash(registry.Names)}");

            LogToConsole($"[RevitMCP] Listening on http://127.0.0.1:{port}/ (auth=ON)");

            // AutoAudit dockable panel (P3-4). Just a browser onto the AutoAudit
            // UI URL — a failure here must NEVER take down the MCP server above,
            // so it gets its own try/catch.
            try
            {
                RegisterAutoAuditPanel(application, revitVersion);
            }
            catch (Exception ex)
            {
                LogToConsole($"[RevitMCP] AutoAudit panel unavailable: {ex.Message}");
            }

            // Spatial-QC pane — same browser-only pattern, independent of AutoAudit (its own
            // try/catch: a failure of either pane must never take the MCP server, or the other
            // pane, down).
            try
            {
                RegisterSpatialQcPanel(application, revitVersion);
            }
            catch (Exception ex)
            {
                LogToConsole($"[RevitMCP] Spatial QC panel unavailable: {ex.Message}");
            }

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Revit MCP Addin",
                "Failed to start MCP HTTP server:\n\n" + ex);
            return Result.Failed;
        }
    }

    private void RegisterAutoAuditPanel(
        UIControlledApplication application, string revitVersion)
    {
        // Belt-and-braces: make sure our loose dependencies (the WebView2
        // managed assemblies deployed next to this dll) resolve from the
        // addin folder even if Revit's addin load-context probing misses
        // them. First live run failed exactly there (silent TypeLoad at the
        // call site).
        var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(
            Assembly.GetExecutingAssembly());
        var addinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        if (alc is not null)
        {
            alc.Resolving += (ctx, name) =>
            {
                var candidate = Path.Combine(addinDir, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
        }

        _panelView = new AutoAuditPanelView(revitVersion);
        application.RegisterDockablePane(
            AutoAuditPaneProvider.PaneId, "AutoAudit",
            new AutoAuditPaneProvider(_panelView));

        // Revit's model-upgrade dialog can wedge WebView2's interop queue
        // (archi-lab.net/webview2-and-revits-dockable-panel) — dispose the
        // browser before a document transition, recreate after.
        application.ControlledApplication.DocumentClosing +=
            (_, _) => _panelView?.Suspend();
        application.ControlledApplication.DocumentOpened +=
            (_, _) => _panelView?.Resume();

        var tab = "AutoAudit";
        application.CreateRibbonTab(tab);
        var ribbonPanel = application.CreateRibbonPanel(tab, "AutoAudit");
        var button = new PushButtonData(
            "AutoAuditShowPanel", "AutoAudit\nPanel",
            Assembly.GetExecutingAssembly().Location,
            typeof(ShowAutoAuditPanelCommand).FullName)
        {
            ToolTip = "Show the AutoAudit audit panel (WebView2). "
                + "If the embedded view is unavailable it opens in your browser.",
        };
        ribbonPanel.AddItem(button);
    }

    private void RegisterSpatialQcPanel(
        UIControlledApplication application, string revitVersion)
    {
        // Same WebView2 assembly-resolution shim as AutoAudit — added independently so this pane
        // works even if AutoAudit registration bailed before installing its own (idempotent: the
        // first resolver to return non-null wins).
        var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(
            Assembly.GetExecutingAssembly());
        var addinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        if (alc is not null)
        {
            alc.Resolving += (ctx, name) =>
            {
                var candidate = Path.Combine(addinDir, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
        }

        // Distinct URL (revit-mcp-spatialqc-panel.json, default :8602) and a DISTINCT WebView2
        // user-data folder so the two panes' browser profiles don't lock each other.
        var url = SpatialQcPanelConfig.ResolveUrl(revitVersion);
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitMCPAddin", "WebView2", "SpatialQc", revitVersion);
        _spatialQcPanelView = new AutoAuditPanelView(url, userDataFolder);
        application.RegisterDockablePane(
            SpatialQcPaneProvider.PaneId, "Spatial QC",
            new SpatialQcPaneProvider(_spatialQcPanelView));

        application.ControlledApplication.DocumentClosing +=
            (_, _) => _spatialQcPanelView?.Suspend();
        application.ControlledApplication.DocumentOpened +=
            (_, _) => _spatialQcPanelView?.Resume();

        // Own ribbon tab — decoupled from AutoAudit's tab (either registration may fail
        // independently, so neither may assume the other created a tab).
        var tab = "Spatial QC";
        application.CreateRibbonTab(tab);
        var ribbonPanel = application.CreateRibbonPanel(tab, "Spatial QC");
        var button = new PushButtonData(
            "SpatialQcShowPanel", "Spatial QC\nPanel",
            Assembly.GetExecutingAssembly().Location,
            typeof(ShowSpatialQcPanelCommand).FullName)
        {
            ToolTip = "Show the Spatial QC panel (WebView2). Run corridor/headroom/egress checks "
                + "on the live model and click a finding to navigate to it. If the embedded view "
                + "is unavailable it opens in your browser.",
        };
        ribbonPanel.AddItem(button);
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

    private static int ResolvePort(string revitVersion)
    {
        // Explicit env var always wins.
        var raw = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
        if (int.TryParse(raw, out var p) && p > 0 && p < 65536)
            return p;

        // Auto-assign: 2026 → 7891, 2027 → 7892, 2028 → 7893, …
        if (int.TryParse(revitVersion, out var year) && year >= BaseRevitYear)
            return DefaultBasePort + (year - BaseRevitYear);

        return DefaultBasePort;
    }

    /// <summary>
    /// Generates a random auth token and writes it to the addins folder.
    /// Always returns a token — auth cannot be disabled (the REVIT_MCP_AUTH=false
    /// escape hatch was removed in 0.8.17: the listener is loopback-only, but an
    /// unauthenticated loopback port would still let any local process drive Revit).
    /// </summary>
    private static string ResolveAuthToken(string revitVersion)
    {
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
