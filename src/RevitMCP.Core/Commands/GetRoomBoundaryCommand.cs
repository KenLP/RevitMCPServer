using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Spatial-QC pack (HTTP-only; command name prefixed <c>spatial_</c>, not exposed as an MCP tool —
/// consumed programmatically by AutomatedSpatialQC over /mcp, not by LLM tool routing).
///
/// Room boundary loops (outer ring + inner holes) as world-coordinate polylines in METRES,
/// taken at the FINISH face — the net clear room area, matching IFC IfcSpace. This is the
/// missing primitive that lets vendor-neutral spatial-QC geometry (corridor clear width,
/// wheelchair turning circle) run directly on the live Revit model, with no IFC export.
///
/// Output:
///   { count, rooms: [ { id, name, number, levelName, floorZ, topZ,
///                       loops: [ [ [x,y], [x,y], ... ],  // [0] = outer ring (metres)
///                                [ [x,y], ... ] ] } ] }   // [1..] = holes (columns, cores)
///
/// Params (all optional): id (long) OR number (string) to target one room; omit for all rooms.
/// </summary>
public sealed class GetRoomBoundaryCommand : IRevitCommand
{
    public string Name => "spatial_get_room_boundary";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var idFilter = P.LongOrNull(ctx.Parameters, "id");
        var numFilter = P.StrOrNull(ctx.Parameters, "number");

        // Finish face = the inside face of the bounding walls = the clear walkable area.
        var opts = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        var rooms = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(r => r.Area > 0)
            .Where(r => idFilter is null || r.Id.Value == idFilter)
            .Where(r => numFilter is null || r.Number == numFilter)
            .OrderBy(r => r.Number)
            .ToList();

        var roomsArr = new JsonArray();
        foreach (var r in rooms)
        {
            var loops = new JsonArray();
            var segLoops = r.GetBoundarySegments(opts);
            if (segLoops is not null)
            {
                foreach (var loop in segLoops)
                {
                    var pts = new JsonArray();
                    foreach (var seg in loop)
                    {
                        var curve = seg.GetCurve();
                        if (curve is null) continue;
                        // Tessellate so arcs/curved walls become polylines; skip each segment's
                        // last point (the next segment repeats it) to avoid duplicate vertices.
                        var tess = curve.Tessellate();
                        for (int i = 0; i < tess.Count - 1; i++)
                            pts.Add(new JsonArray { tess[i].X * P.FeetToMeters, tess[i].Y * P.FeetToMeters });
                    }
                    if (pts.Count >= 3) loops.Add(pts);
                }
            }

            double floorZ = (r.Level?.Elevation ?? 0.0) * P.FeetToMeters;
            double height = 0.0;
            try { height = r.UnboundedHeight; } catch { /* unplaced/odd room */ }

            roomsArr.Add(new JsonObject
            {
                ["id"] = r.Id.Value,
                ["name"] = r.Name,
                ["number"] = r.Number,
                ["levelName"] = r.Level?.Name,
                ["floorZ"] = floorZ,
                ["topZ"] = floorZ + height * P.FeetToMeters,
                ["loops"] = loops,
            });
        }

        return new JsonObject { ["count"] = rooms.Count, ["rooms"] = roomsArr };
    }
}
