using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPAddin.Panel;

/// <summary>
/// Ribbon entry point: shows the optional second dockable pane. If the pane was never registered
/// (startup failure), fall back to opening the configured URL in the default browser — the app
/// lives at that URL, the pane is just the closest window to it. The button only exists when
/// <c>revit-mcp-extra-panel.json</c> configured the pane, so "not configured" here means the file
/// changed under a running Revit.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ShowExtraPanelCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var pane = commandData.Application.GetDockablePane(ExtraPaneProvider.PaneId);
            pane.Show();
            return Result.Succeeded;
        }
        catch (Exception)
        {
            try
            {
                var version = commandData.Application.Application.VersionNumber;
                var settings = ExtraPanelConfig.Resolve(version);
                if (settings is null)
                {
                    message = "Extra panel is not configured (revit-mcp-extra-panel.json).";
                    return Result.Failed;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = settings.Url,
                    UseShellExecute = true,
                });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "Extra panel unavailable: " + ex.Message;
                return Result.Failed;
            }
        }
    }
}
