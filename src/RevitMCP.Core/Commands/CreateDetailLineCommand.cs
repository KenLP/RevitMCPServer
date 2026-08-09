using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a view-specific detail line in a 2D view (plan, section, elevation,
/// drafting, or detail view).  The endpoints are projected onto the view's plane,
/// so any input z is tolerated.
///
/// Params:
///   - start, end: {x, y, z?} — endpoints (projected onto the view plane).
///   - viewId:     long, optional (defaults to active view).
///   - units:      "meters"|"feet", default "meters".
///   - color:      {r, g, b}, optional — projection line colour override, applied in the same
///                 transaction that creates the curve.
///   - weight:     int 1-16, optional — projection line weight override. Independent of
///                 <c>color</c>: either one alone sets just that aspect.
///
/// Returns: { id, detailLineId, viewId, lengthMeters }.  <c>id</c> is the primary key every other
/// create_* command returns; <c>detailLineId</c> is kept as an alias for existing callers.
/// </summary>
public sealed class CreateDetailLineCommand : IRevitCommand
{
    public string Name => "create_detail_line";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var viewId = p["viewId"] is not null
            ? new ElementId(P.Long(p, "viewId"))
            : doc.ActiveView?.Id ?? throw new RevitCommandException("not_found", "No active view.");
        var view = doc.GetElement(viewId) as View
            ?? throw new RevitCommandException("not_found", $"View {viewId.Value} not found.");

        if (view.ViewType is ViewType.ThreeD or ViewType.Schedule or ViewType.DrawingSheet
            or ViewType.Legend or ViewType.Internal or ViewType.Undefined)
            throw new RevitCommandException("unsupported_view",
                $"Detail lines need a 2D view (plan, section, elevation, drafting, or detail), not '{view.ViewType}'.");

        var start = ViewPlane.Project(view, P.Xyz(p, "start", units));
        var end = ViewPlane.Project(view, P.Xyz(p, "end", units));
        if (start.DistanceTo(end) < 1e-7)
            throw new RevitCommandException("bad_request",
                "start and end project to the same point in the view plane.");

        var curve = doc.Create.NewDetailCurve(view, Line.CreateBound(start, end));

        // Colour/weight are view-specific graphic overrides, not properties of the curve, so they
        // go through SetElementOverrides — but in THIS transaction, so one call still yields one
        // finished line rather than a line plus a follow-up override_element_graphics round-trip.
        var colorNode = p["color"] as JsonObject;
        var weightNode = p["weight"];
        if (colorNode is not null || weightNode is not null)
        {
            var ogs = new OverrideGraphicSettings();
            if (colorNode is not null)
                ogs.SetProjectionLineColor(new Color(
                    P.ColorByte(colorNode, "r", 0),
                    P.ColorByte(colorNode, "g", 0),
                    P.ColorByte(colorNode, "b", 0)));
            if (weightNode is not null)
            {
                // Revit's line weights are pen numbers 1-16; anything else throws deep inside
                // SetProjectionLineWeight with a message that never names 'weight'.
                var weight = P.Int(p, "weight");
                if (weight < 1 || weight > 16)
                    throw new RevitCommandException("bad_request",
                        $"'weight' must be a Revit line weight 1-16, got {weight}.");
                ogs.SetProjectionLineWeight(weight);
            }
            view.SetElementOverrides(curve.Id, ogs);
        }

        return new JsonObject
        {
            ["id"] = curve.Id.Value,
            ["detailLineId"] = curve.Id.Value,
            ["viewId"] = viewId.Value,
            ["lengthMeters"] = curve.GeometryCurve.Length * P.FeetToMeters,
        };
    }
}

/// <summary>Projects model points onto a view's sketch plane (for view-specific geometry).</summary>
internal static class ViewPlane
{
    public static XYZ Project(View view, XYZ q)
    {
        var origin = view.Origin;
        var normal = view.ViewDirection;
        var d = (q - origin).DotProduct(normal);
        return q - d * normal;
    }
}
