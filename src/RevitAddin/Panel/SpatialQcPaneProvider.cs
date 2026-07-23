using Autodesk.Revit.UI;

namespace RevitMCPAddin.Panel;

/// <summary>
/// Registers the Spatial-QC WebView2 view as a Revit dockable pane. Its own stable pane id —
/// distinct from <see cref="AutoAuditPaneProvider"/> — so persisted window layouts keep the two
/// panes apart across sessions.
/// </summary>
public sealed class SpatialQcPaneProvider : IDockablePaneProvider
{
    public static readonly DockablePaneId PaneId =
        new(new System.Guid("3F2A7C10-8D4B-4E96-B1A2-9C7E5D3160AF"));

    private readonly AutoAuditPanelView _view;

    public SpatialQcPaneProvider(AutoAuditPanelView view) => _view = view;

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
