using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Place a view on a sheet (creates a Viewport).
///
/// Params:
///   - sheetId:  long, required
///   - viewId:   long, required
///   - location: { x, y }, optional (center point on sheet in sheet coordinates)
/// </summary>
public sealed class PlaceViewOnSheetCommand : IRevitCommand
{
    public string Name => "place_view_on_sheet";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var sheetId = new ElementId(P.Long(p, "sheetId"));
        var viewId = new ElementId(P.Long(p, "viewId"));

        if (!Viewport.CanAddViewToSheet(doc, sheetId, viewId))
            throw new System.InvalidOperationException(
                "Cannot add this view to the sheet — it may already be on another sheet or be a parent view.");

        var location = p["location"] is JsonObject loc
            ? new XYZ(P.DblOr(loc, "x", 0), P.DblOr(loc, "y", 0), 0)
            : new XYZ(1.0, 0.75, 0); // default to roughly sheet center

        var viewport = Viewport.Create(doc, sheetId, viewId, location);

        return new JsonObject
        {
            ["viewportId"] = viewport.Id.Value,
            ["sheetId"] = sheetId.Value,
            ["viewId"] = viewId.Value,
        };
    }
}
