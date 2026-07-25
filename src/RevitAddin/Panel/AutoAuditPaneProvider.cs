using Autodesk.Revit.UI;

namespace RevitMCPAddin.Panel;

/// <summary>
/// Registers the AutoAudit WebView2 view as a Revit dockable pane. The pane
/// id is stable (persisted window layouts reference it across sessions).
/// </summary>
public sealed class AutoAuditPaneProvider : IDockablePaneProvider
{
    public static readonly DockablePaneId PaneId =
        new(new System.Guid("7A1C64F2-9B3E-4D08-A5C1-52AA0E8D4B21"));

    private readonly AutoAuditPanelView _view;

    public AutoAuditPaneProvider(AutoAuditPanelView view) => _view = view;

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _view;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Tabbed,
            TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser,
        };
    }
}
