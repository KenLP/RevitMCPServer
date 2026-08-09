using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Spatial-QC pack (HTTP-only; command name prefixed <c>spatial_</c>, not exposed as an MCP tool —
/// consumed programmatically by AutomatedSpatialQC over /mcp, not by LLM tool routing).
///
/// PathOfTravel elements the user (or Revit's own Analyze &gt; Path of Travel UI) already placed,
/// with Revit's OWN computed route length/time — the READ side of `bim-nav benchmark-pot`
/// (SPEC_pot-parity.md Block C): the consumer reruns the same (from, to) pair through its
/// occupancy-grid router and prints both numbers side by side. Nothing here is modified or
/// recomputed; the point is Revit's numbers verbatim.
///
/// `from`/`to` are the first/last vertex of the element's computed route curves — NOT necessarily
/// the exact points the user clicked (Revit may snap them). Elements whose route failed to compute
/// (GetCurves() empty) are skipped: emitting a 0-length row would read as a real measurement.
///
/// A PathOfTravel lives in exactly one floor plan view, so one levelName per element — read from
/// the element's own PATH_OF_TRAVEL_LEVEL_NAME parameter (what Revit's Properties palette shows),
/// falling back to the owning view's GenLevel.
///
/// Output:
///   { count, paths: [ { id, levelName, from: {x,y,z}, to: {x,y,z},
///                       lengthMeters, timeSeconds } ] }   // metres, world XYZ, Revit frame
/// </summary>
public sealed class GetPathsOfTravelCommand : IRevitCommand
{
    public string Name => "spatial_get_paths_of_travel";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();

        var pots = new FilteredElementCollector(doc)
            .OfClass(typeof(PathOfTravel))
            .WhereElementIsNotElementType()
            .Cast<PathOfTravel>()
            .ToList();

        var arr = new JsonArray();
        foreach (var pot in pots)
        {
            // Route curves of the computed path. Empty/null when Revit failed to find a route
            // (obstructed, endpoints outside bounds, ...) — skip, see the class doc.
            var curves = pot.GetCurves();
            if (curves is null || curves.Count == 0) continue;

            var from = curves[0].GetEndPoint(0);
            var to = curves[curves.Count - 1].GetEndPoint(1);

            // "Level" in the Properties palette is the element's own string parameter, not
            // Element.LevelId (which is invalid for PathOfTravel). Fall back to the owning
            // floor-plan view's GenLevel if the parameter is somehow empty.
            var levelName = pot.get_Parameter(BuiltInParameter.PATH_OF_TRAVEL_LEVEL_NAME)?.AsString();
            if (string.IsNullOrEmpty(levelName))
                levelName = (doc.GetElement(pot.OwnerViewId) as View)?.GenLevel?.Name;

            // Revit's own numbers, verbatim. Length: PathOfTravel derives from Element (not
            // CurveElement), but its UI "Length" parameter is still CURVE_ELEM_LENGTH; if a
            // build ever drops it, the route curves themselves are the same measurement.
            var lengthParam = pot.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            double lengthFt = lengthParam is not null && lengthParam.HasValue
                ? lengthParam.AsDouble()
                : curves.Sum(c => c.Length);
            // Internal time unit is seconds. Emitted as null when absent — the consumer treats
            // a missing measurement as "unmeasured", never as 0 s.
            var timeParam = pot.get_Parameter(BuiltInParameter.PATH_OF_TRAVEL_TIME);

            arr.Add(new JsonObject
            {
                ["id"] = pot.Id.Value,
                ["levelName"] = levelName,
                ["from"] = XyzToJson(from),
                ["to"] = XyzToJson(to),
                ["lengthMeters"] = JsonValue.Create(lengthFt * P.FeetToMeters),
                ["timeSeconds"] = timeParam is not null && timeParam.HasValue
                    ? JsonValue.Create(timeParam.AsDouble())
                    : null,
            });
        }

        return new JsonObject { ["count"] = arr.Count, ["paths"] = arr };
    }

    private static JsonObject XyzToJson(XYZ p) => new()
    {
        ["x"] = JsonValue.Create(p.X * P.FeetToMeters),
        ["y"] = JsonValue.Create(p.Y * P.FeetToMeters),
        ["z"] = JsonValue.Create(p.Z * P.FeetToMeters),
    };
}
