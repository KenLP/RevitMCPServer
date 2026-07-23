using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPAddin.Panel;

/// <summary>
/// Ribbon entry point: shows the Spatial-QC dockable pane. If the pane was never registered
/// (startup failure), fall back to opening the panel URL in the default browser — the app lives at
/// a URL (the local <c>spatial-qc panel</c> service), the pane is just the closest window to it.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ShowSpatialQcPanelCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var pane = commandData.Application.GetDockablePane(SpatialQcPaneProvider.PaneId);
            pane.Show();
            return Result.Succeeded;
        }
        catch (Exception)
        {
            try
            {
                var version = commandData.Application.Application.VersionNumber;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = SpatialQcPanelConfig.ResolveUrl(version),
                    UseShellExecute = true,
                });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "Spatial QC panel unavailable: " + ex.Message;
                return Result.Failed;
            }
        }
    }
}
