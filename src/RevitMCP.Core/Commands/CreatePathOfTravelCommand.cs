using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.UI.Events;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Spatial-QC pack (HTTP-only; command name prefixed <c>spatial_</c>, not exposed as an MCP tool —
/// consumed programmatically by AutomatedSpatialQC over /mcp, not by LLM tool routing).
///
/// Asks Revit to compute and place its OWN native PathOfTravel element between two points, so a
/// reviewer who already trusts <c>Analyze &gt; Path of Travel</c> can see Revit's line sitting next
/// to bim-nav's detail-line route in the same view. The WRITE mirror of
/// <see cref="GetPathsOfTravelCommand"/>.
///
/// Hard API limitation (do not design around it): <c>PathOfTravel.Create</c> takes exactly two
/// endpoints and routes between them with Revit's own path-finding. There is no way to hand Revit
/// an existing polyline, so this can never be how spatial-qc draws its OWN route — that stays
/// detail lines. This is a same-view baseline only.
///
/// Params:
///   viewId  long, required — a floor plan view (PathOfTravel is confined to one).
///   from/to {x, y, z?}, required.
///   units   "meters"|"feet", default "meters".
///
/// Returns: { id, viewId, lengthMeters, timeSeconds } — Revit's own numbers for ITS route, which
/// will generally NOT equal bim-nav's for the same pair. That difference is the point.
/// </summary>
public sealed class CreatePathOfTravelCommand : IRevitCommand
{
    public string Name => "spatial_create_path_of_travel";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";
    public ExecutionKind Execution => ExecutionKind.ModelWrite;

    // The crop-region warning fires at transaction COMMIT (Revit failures processing), not inside
    // PathOfTravel.Create — measured: with a crop-affected route accepted, the commit raised the
    // modal "A crop region is enabled in the view..." box and deadlocked the add-in for 400+s
    // until a human clicked OK. The dispatcher deletes commit warnings for this command; the
    // condition is still reported through the `warning` field below (driven by the out-status).
    public bool SuppressWarningsOnCommit => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var viewId = new ElementId(P.Long(p, "viewId"));
        var view = doc.GetElement(viewId) as View
            ?? throw new RevitCommandException("not_found", $"View {viewId.Value} not found.");
        if (view.ViewType != ViewType.FloorPlan)
            throw new RevitCommandException("unsupported_view",
                $"Path of Travel requires a floor plan view, not '{view.ViewType}'.");

        var from = P.Xyz(p, "from", units);
        var to = P.Xyz(p, "to", units);

        // PathOfTravel.Create can raise a MODAL dialog (measured: a crop-region warning on a view
        // whose crop clips the route). A modal dialog on the Revit UI thread deadlocks the whole
        // add-in — the HTTP request never returns and every later request queues behind it until a
        // human clicks the box, which for an unattended consumer means hung forever. So dialogs are
        // auto-dismissed for the duration of this call and reported as data instead.
        string? dialogId = null, dialogMessage = null;
        void OnDialog(object? sender, DialogBoxShowingEventArgs e)
        {
            dialogId ??= e.DialogId;
            dialogMessage ??= (e as TaskDialogShowingEventArgs)?.Message
                              ?? (e as MessageBoxShowingEventArgs)?.Message;
            e.OverrideResult(1); // IDOK — dismiss and let Create return its status
        }

        // Create reports failure through an out-status, NOT an exception — so a blocked route comes
        // back as a named reason (NoPathOfTravel, PointOutsideActiveCrop, ...) instead of a generic
        // throw. Revit still hands back an element for some non-Success statuses, so the status is
        // checked before the element, and a failed one is deleted rather than left in the model:
        // the transaction commits, and a half-computed PathOfTravel would otherwise survive it.
        PathOfTravel? pot;
        PathOfTravelCalculationStatus status;
        ctx.App.DialogBoxShowing += OnDialog;
        try
        {
            pot = PathOfTravel.Create(view, from, to, out status);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            // Measured: Create does NOT route every failure through the out-status. Coincident
            // endpoints and points outside the view crop both throw
            // Autodesk.Revit.Exceptions.InvalidOperationException instead, never reaching the
            // StartAndEndPointsTooClose / PointOutsideActiveCrop statuses. Caught at the
            // Autodesk.Revit.Exceptions base (which derives from ApplicationException, NOT from the
            // BCL exception types) so the whole family maps to one no_route contract; Revit's own
            // sentence is the most precise thing available, so it is passed through verbatim.
            throw new RevitCommandException("no_route",
                $"Revit could not compute a path of travel in view '{view.Name}': {ex.Message}");
        }
        finally
        {
            ctx.App.DialogBoxShowing -= OnDialog;
        }

        // ResultAffectedByCrop is NOT a failure: Revit computed and placed a real route, it just
        // reports that the view's crop region influenced the result. Measured on R27 Snowdon, the
        // exact endpoints of an EXISTING hand-drawn PathOfTravel come back with this status — so
        // rejecting it would make the command fail on essentially every route in a cropped
        // life-safety view, including ones the user already accepted. It is surfaced as a warning
        // instead, because a clipped route must not be compared as if it were clean.
        var usable = status is PathOfTravelCalculationStatus.Success
                          or PathOfTravelCalculationStatus.ResultAffectedByCrop;
        if (!usable)
        {
            if (pot is not null) doc.Delete(pot.Id);
            var suffix = dialogId is null ? "" :
                $" Revit also raised dialog '{dialogId}'" +
                (dialogMessage is null ? " (auto-dismissed)." : $": {dialogMessage}");
            throw new RevitCommandException("no_route",
                $"Revit could not compute a path of travel in view '{view.Name}': {status}.{suffix}");
        }
        if (pot is null)
            throw new RevitCommandException("no_route",
                $"Revit reported Success but produced no PathOfTravel element in view '{view.Name}'.");

        // Same parameters GetPathsOfTravelCommand reads — verified against RevitAPI.dll: there is
        // no PATH_OF_TRAVEL_LENGTH, length lives on the shared curve-element parameter, and
        // PATH_OF_TRAVEL_TIME is already in seconds.
        var lengthParam = pot.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
        double lengthFt = lengthParam is not null && lengthParam.HasValue
            ? lengthParam.AsDouble()
            : (pot.GetCurves()?.Sum(c => c.Length) ?? 0.0);
        var timeParam = pot.get_Parameter(BuiltInParameter.PATH_OF_TRAVEL_TIME);

        return new JsonObject
        {
            ["id"] = pot.Id.Value,
            ["viewId"] = viewId.Value,
            ["lengthMeters"] = JsonValue.Create(lengthFt * P.FeetToMeters),
            ["timeSeconds"] = timeParam is not null && timeParam.HasValue
                ? JsonValue.Create(timeParam.AsDouble())
                : null,
            // Revit can warn and still succeed. The warning is driven by the out-STATUS, not by
            // dialog capture — the crop warning is a commit-time dialog the dispatcher swallows
            // (see SuppressWarningsOnCommit), so the status is the reliable signal. Silently
            // swallowing it would let a crop-limited route pass as a clean measurement.
            ["warning"] = status == PathOfTravelCalculationStatus.ResultAffectedByCrop
                ? "ResultAffectedByCrop: the route was computed only inside the view's crop "
                  + "region and may not be the globally optimal path."
                : dialogId is null ? null
                : $"Revit raised dialog '{dialogId}' (auto-dismissed)"
                  + (dialogMessage is null ? "." : $": {dialogMessage}"),
        };
    }
}
