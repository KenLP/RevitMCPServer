using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a filled region (2D annotation) from a closed boundary in a 2D view.
/// Boundary points are projected onto the view plane and closed automatically.
///
/// Params:
///   - boundary:           [{x, y, z?}, ...] — at least 3 points, in order.
///   - filledRegionTypeId: long, optional (defaults to the first FilledRegionType).
///   - viewId:             long, optional (defaults to active view).
///   - units:              "meters"|"feet", default "meters".
///
/// Returns: { filledRegionId, viewId, filledRegionTypeId, pointCount }.
/// </summary>
public sealed class CreateFilledRegionCommand : IRevitCommand
{
    public string Name => "create_filled_region";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        double scale = units.Equals("feet", StringComparison.OrdinalIgnoreCase) ? 1.0 : P.MetersToFeet;

        var viewId = p["viewId"] is not null
            ? new ElementId(P.Long(p, "viewId"))
            : doc.ActiveView?.Id ?? throw new RevitCommandException("not_found", "No active view.");
        var view = doc.GetElement(viewId) as View
            ?? throw new RevitCommandException("not_found", $"View {viewId.Value} not found.");

        if (view.ViewType is ViewType.ThreeD or ViewType.Schedule or ViewType.DrawingSheet
            or ViewType.Legend or ViewType.Internal or ViewType.Undefined)
            throw new RevitCommandException("unsupported_view",
                $"Filled regions need a 2D view, not '{view.ViewType}'.");

        var boundary = p["boundary"] as JsonArray
            ?? throw new RevitCommandException("bad_request", "'boundary' array of {x, y} points is required.");
        if (boundary.Count < 3)
            throw new RevitCommandException("bad_request", "'boundary' needs at least 3 points.");

        var pts = new List<XYZ>(boundary.Count);
        foreach (var node in boundary)
        {
            if (node is not JsonObject o)
                throw new RevitCommandException("bad_request", "Each boundary entry must be a {x, y} object.");
            var raw = new XYZ(P.DblOr(o, "x", 0) * scale, P.DblOr(o, "y", 0) * scale, P.DblOr(o, "z", 0) * scale);
            pts.Add(ViewPlane.Project(view, raw));
        }

        var curves = new List<Curve>();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (a.DistanceTo(b) > 1e-7) curves.Add(Line.CreateBound(a, b));
        }
        if (curves.Count < 3)
            throw new RevitCommandException("bad_request", "Boundary collapses to fewer than 3 distinct edges.");

        CurveLoop loop;
        try { loop = CurveLoop.Create(curves); }
        catch (Exception ex)
        {
            throw new RevitCommandException("bad_request",
                $"Boundary is not a valid closed loop: {ex.Message}");
        }

        var frtId = p["filledRegionTypeId"] is not null
            ? new ElementId(P.Long(p, "filledRegionTypeId"))
            : new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).FirstElementId();
        if (frtId == ElementId.InvalidElementId)
            throw new RevitCommandException("not_found", "No FilledRegionType exists in the document.");

        var fr = FilledRegion.Create(doc, frtId, viewId, new List<CurveLoop> { loop });

        return new JsonObject
        {
            ["filledRegionId"] = fr.Id.Value,
            ["viewId"] = viewId.Value,
            ["filledRegionTypeId"] = frtId.Value,
            ["pointCount"] = pts.Count,
        };
    }
}
