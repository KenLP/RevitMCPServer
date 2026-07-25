using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPAddin.Panel;

/// <summary>
/// Ribbon entry point: shows the AutoAudit dockable pane. If the pane was
/// never registered (startup failure), fall back to opening the AutoAudit
/// URL in the default browser — the app lives at a URL, the pane is just
/// the closest window to it.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ShowAutoAuditPanelCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var pane = commandData.Application.GetDockablePane(AutoAuditPaneProvider.PaneId);
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
                    FileName = PanelConfig.ResolveUrl(version),
                    UseShellExecute = true,
                });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "AutoAudit panel unavailable: " + ex.Message;
                return Result.Failed;
            }
        }
    }
}
