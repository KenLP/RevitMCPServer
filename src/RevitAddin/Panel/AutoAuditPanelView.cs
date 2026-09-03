using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RevitMCPAddin.Panel;

/// <summary>
/// WPF content of the AutoAudit dockable pane: a WebView2 pointed at the
/// AutoAudit UI URL, with a small toolbar (reload / open-in-browser) and a
/// fallback overlay used whenever WebView2 cannot run (runtime missing,
/// environment init failure, renderer crash). Built code-only — no XAML.
///
/// Known WebView2-in-Revit gotchas handled here:
///  * user-data folder MUST be explicit — the default (next to Revit.exe)
///    is not writable and init fails;
///  * Revit's model-upgrade dialog can wedge the WebView2 interop queue —
///    App.cs calls <see cref="Suspend"/>/<see cref="Resume"/> around
///    DocumentClosing/DocumentOpened to dispose/recreate the browser;
///  * renderer crashes (CEF/WebView2 clashes on some machines) surface via
///    CoreWebView2.ProcessFailed → we tear down and show the fallback with
///    a Retry button. The panel must NEVER take Revit down with it.
/// </summary>
public sealed class AutoAuditPanelView : UserControl
{
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly Grid _root;
    private readonly StackPanel _fallback;
    private readonly TextBlock _fallbackMessage;
    private WebView2? _webView;
    private bool _suspended;
    private readonly string _label;

    public AutoAuditPanelView(string revitVersion)
        : this(PanelConfig.ResolveUrl(revitVersion),
               Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                   "RevitMCPAddin", "WebView2", revitVersion),
               "AutoAudit")
    {
    }

    /// <summary>
    /// URL + user-data-folder overload so a SECOND dockable pane (Spatial QC) reuses this WebView2
    /// host — and every gotcha fix in it — instead of duplicating ~250 lines. Each pane MUST pass a
    /// DISTINCT userDataFolder: two WebView2 profiles sharing one folder can lock each other out.
    /// </summary>
    /// <param name="label">
    /// Pane name used in every message the user reads. Required because BOTH
    /// dockable panes are instances of this class: with "AutoAudit" hardcoded,
    /// the Spatial QC pane told users "AutoAudit panel is paused" and pointed
    /// them at AutoAudit while showing :8602.
    /// </param>
    public AutoAuditPanelView(string url, string userDataFolder, string label)
    {
        _url = url;
        _userDataFolder = userDataFolder;
        _label = label;

        // Explicit surface colors: the pane host gives WPF a transparent
        // backdrop that renders BLACK under Revit's dark theme — the first
        // live run showed an unreadable black-on-black fallback.
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x21, 0x27));

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _root.Children.Add(BuildToolbar());

        _fallbackMessage = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0xEB, 0xF0)),
        };
        _fallback = BuildFallback();
        Grid.SetRow(_fallback, 1);
        _root.Children.Add(_fallback);

        Content = _root;
        Loaded += (_, _) => EnsureWebView();
    }

    // ── lifecycle hooks (App.cs wires these to Revit document events) ──────

    /// <summary>Dispose the browser before a document transition.</summary>
    public void Suspend()
    {
        _suspended = true;
        TearDownWebView();
        ShowFallback($"{_label} panel is paused while Revit switches documents.");
    }

    /// <summary>Recreate the browser after the document transition ends.</summary>
    public void Resume()
    {
        _suspended = false;
        // Back on the UI thread queue — recreate lazily so a burst of
        // document events collapses into one rebuild.
        Dispatcher.BeginInvoke(new Action(EnsureWebView));
    }

    // ── UI construction ─────────────────────────────────────────────────────

    private UIElement BuildToolbar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4),
        };
        var reload = new Button { Content = "Reload", Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 2, 8, 2) };
        reload.Click += (_, _) =>
        {
            if (_webView?.CoreWebView2 is not null) _webView.CoreWebView2.Reload();
            else ForceRebuild();
        };
        var external = new Button { Content = "Open in browser", Padding = new Thickness(8, 2, 8, 2) };
        external.Click += (_, _) => OpenExternal();
        bar.Children.Add(reload);
        bar.Children.Add(external);
        return bar;
    }

    private StackPanel BuildFallback()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_fallbackMessage);

        var open = new Button
        {
            Content = $"Open {_label} in browser",
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4),
        };
        open.Click += (_, _) => OpenExternal();
        panel.Children.Add(open);

        var retry = new Button
        {
            Content = "Retry embedded view",
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        retry.Click += (_, _) => ForceRebuild();
        panel.Children.Add(retry);
        return panel;
    }

    // ── WebView2 management ────────────────────────────────────────────────

    /// <summary>
    /// JIT-isolation shim: this method touches NO WebView2 types, so it
    /// always JITs. The body lives in <see cref="EnsureWebViewCore"/> —
    /// jitting THAT can itself throw FileNotFound/TypeLoad when the
    /// WebView2 assemblies fail to resolve inside Revit's addin load
    /// context, and that throw happens at the CALL SITE. Caught here, it
    /// lands in the log + fallback instead of being swallowed by Revit's
    /// dispatcher (the exact silent-black-panel failure seen live
    /// 2026-07-12).
    /// </summary>
    /// <summary>
    /// A user pressing "Retry embedded view" or "Reload" IS the statement that
    /// the document transition is over, so it clears the suspend flag before
    /// rebuilding. <see cref="EnsureWebViewCore"/> returns early while
    /// <c>_suspended</c> is set, so routing these buttons straight there left
    /// the only affordance offered for the paused state unable to leave it —
    /// an inert button with no message, for the rest of the Revit session.
    /// </summary>
    private void ForceRebuild()
    {
        _suspended = false;
        EnsureWebView();
    }

    private void EnsureWebView()
    {
        try
        {
            EnsureWebViewCore();
        }
        catch (Exception ex)
        {
            LogPanel("call-site failure (assembly load?): " + ex);
            ShowFallback(
                "The embedded browser component could not load: "
                + ex.GetType().Name + " — " + ex.Message
                + $"\n(full details: {LogPath})");
        }
    }

    private async void EnsureWebViewCore()
    {
        if (_suspended || _webView is not null) return;
        try
        {
            // Throws when the Evergreen WebView2 Runtime is not installed —
            // cheapest possible probe, before we touch any control state.
            CoreWebView2Environment.GetAvailableBrowserVersionString();

            var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
            Grid.SetRow(view, 1);
            _webView = view;
            // The control MUST be in the visual tree BEFORE awaiting
            // EnsureCoreWebView2Async: the WPF WebView2 only completes
            // initialization once it has an HWND, and it only gets one by
            // being loaded. Adding it after the await parks the await
            // forever — no exception, no log, fallback never collapses
            // (the exact silent hang seen live 2026-07-12, round 2).
            _root.Children.Add(view);

            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: _userDataFolder);
            await view.EnsureCoreWebView2Async(env);

            view.CoreWebView2.ProcessFailed += (_, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TearDownWebView();
                    ShowFallback($"The embedded browser crashed ({e.ProcessFailedKind}).");
                }));
            };
            // The panel is a BROWSER, nothing more: no host objects, no
            // script injection — the web app talks to the AuditHub service
            // over HTTP exactly as it does in a normal browser tab.
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            view.CoreWebView2.Navigate(_url);

            _fallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            TearDownWebView();
            LogPanel(ex.ToString());
            ShowFallback(
                "The embedded view is unavailable: "
                + ex.GetType().Name + " — " + ex.Message
                + $"\n(full details: {LogPath})");
        }
    }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCPAddin", "panel.log");

    private static void LogPanel(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:s}] {text}\n");
        }
        catch
        {
            // Diagnostics must never throw into Revit.
        }
    }

    private void TearDownWebView()
    {
        if (_webView is null) return;
        try
        {
            _root.Children.Remove(_webView);
            _webView.Dispose();
        }
        catch
        {
            // Disposal must never throw into Revit.
        }
        _webView = null;
    }

    private void ShowFallback(string message)
    {
        _fallbackMessage.Text = message + $"\n\n{_label} keeps working at {_url}";
        _fallback.Visibility = Visibility.Visible;
    }

    private void OpenExternal()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowFallback("Could not launch the default browser: " + ex.Message);
        }
    }
}
