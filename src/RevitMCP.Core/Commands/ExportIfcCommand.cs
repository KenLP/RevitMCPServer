using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Export the active document to an IFC file.
///
/// Params:
///   - outputPath:             string — absolute target .ifc; its folder must exist
///   - ifcVersion:             string, default "IFC4" — a name of Revit's IFCVersion enum
///   - viewId:                 long, optional — export what this view shows; omit for the whole model
///   - exportBaseQuantities:   bool, default true
///   - spaceBoundaryLevel:     int 0-2, default 1
///   - wallAndColumnSplitting: bool, default false
///   - overwrite:              bool, default true — repeated exports replace the file
///
/// Revit's exporter carries the rooms of one phase only (the view's phase, else the last phase) and
/// says nothing about the rest, so the result reports phase, roomsInPhase and roomsInOtherPhases.
///
/// A UI action, not read-only: it writes a file, and a read-only command would still run on a dry-run.
/// Revit requires an open transaction for IFC export, so this is the one command that opens its own;
/// it is always rolled back, leaving the model untouched. The export goes to a temporary name in the
/// target folder, so a failed export can neither pass for a success nor damage an earlier file.
/// </summary>
public sealed class ExportIfcCommand : IRevitCommand
{
    public string Name => "export_ifc";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var request = ExportIfcRequest.From(ctx.Parameters, Enum.GetNames(typeof(IFCVersion)));

        var options = new IFCExportOptions
        {
            FileVersion = (IFCVersion)Enum.Parse(typeof(IFCVersion), request.Version),
            ExportBaseQuantities = request.BaseQuantities,
            SpaceBoundaryLevel = request.SpaceBoundaryLevel,
            WallAndColumnSplitting = request.WallAndColumnSplitting,
        };
        // The exporter reads this from its named options; the property alone produced no quantities.
        options.AddOption("ExportBaseQuantities", request.BaseQuantities ? "true" : "false");
        View? filterView = null;
        if (request.ViewId is { } viewId)
        {
            filterView = doc.GetElement(new ElementId(viewId)) is View { IsTemplate: false } view
                ? view
                : throw new RevitCommandException("invalid_parameter", $"'viewId' {viewId} is not a view of this model.");
            options.FilterViewId = filterView.Id;
            options.AddOption("ExportRoomsInView", "true"); // without it a view export has no rooms at all
        }

        var phase = ExportedPhase(doc, filterView);
        var (roomsInPhase, roomsElsewhere) = CountRooms(doc, phase);

        var folder = Path.GetDirectoryName(request.OutputPath)!;
        var tempName = $"{Path.GetFileNameWithoutExtension(request.OutputPath)}.tmp-{Guid.NewGuid():N}.ifc";
        var tempPath = Path.Combine(folder, tempName);

        using var watch = DialogWatch.Start(ctx);
        var sw = Stopwatch.StartNew();
        try
        {
            bool exported;
            using (var tx = new Transaction(doc, "MCP export_ifc"))
            {
                tx.Start();
                try { exported = doc.Export(folder, tempName, options); }
                finally { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); }
            }

            var size = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
            if (!exported || size == 0)
                throw new RevitCommandException("export_failed",
                    $"Revit produced no IFC file (Export returned {exported}).{watch.ErrorSuffix()}");

            File.Move(tempPath, request.OutputPath, overwrite: true);
            sw.Stop();

            return new JsonObject
            {
                ["outputPath"] = request.OutputPath,
                ["sizeBytes"] = size,
                ["ifcVersion"] = request.Version,
                ["viewId"] = request.ViewId,
                ["phase"] = phase is null ? null : new JsonObject { ["id"] = phase.Id.Value, ["name"] = phase.Name },
                ["roomsInPhase"] = roomsInPhase,
                ["roomsInOtherPhases"] = roomsElsewhere,
                ["exportMs"] = sw.ElapsedMilliseconds,
                ["dialogs"] = watch.EventsJson(),
            };
        }
        catch (Exception ex) when (ex is not RevitCommandException)
        {
            throw DocumentSaving.Failed("export_failed", "export to IFC", ex, watch);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>The phase whose rooms the exporter keeps: the view's, else the last one.</summary>
    private static Phase? ExportedPhase(Document doc, View? view)
    {
        var viewPhase = view?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
        if (viewPhase is not null && doc.GetElement(viewPhase) is Phase fromView) return fromView;
        return doc.Phases.Cast<Phase>().LastOrDefault();
    }

    /// <summary>Placed rooms in the exported phase, and those the export leaves out.</summary>
    private static (int InPhase, int Elsewhere) CountRooms(Document doc, Phase? phase)
    {
        var placed = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType().OfType<SpatialElement>().Where(r => r.Area > 0).ToList();
        var inPhase = phase is null ? 0 : placed.Count(r =>
            r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() is { } id && id.Equals(phase.Id));
        return (inPhase, placed.Count - inPhase);
    }
}
