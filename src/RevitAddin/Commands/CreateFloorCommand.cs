using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a Floor from a closed polygonal profile.
///
/// Parameters:
///   - profile:        [{x,y,z?}, ...]   required, at least 3 points, closed automatically
///   - levelName:      string             optional, defaults to lowest level
///   - floorTypeName:  string             optional, defaults to first FloorType
///   - units:          "meters"|"feet"    optional, default "meters"
/// </summary>
public sealed class CreateFloorCommand : IRevitCommand
{
    public string Name => "create_floor";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var pts = P.XyzList(p, "profile", units);
        if (pts.Count < 3)
            throw new System.ArgumentException("Floor profile must have at least 3 points.");

        var level = CreateWallCommand.ResolveLevel(doc, P.StrOrNull(p, "levelName"));
        var floorType = ResolveFloorType(doc, P.StrOrNull(p, "floorTypeName"));

        // Project all points to the level elevation and build a CurveLoop.
        var loop = new CurveLoop();
        for (var i = 0; i < pts.Count; i++)
        {
            var a = new XYZ(pts[i].X, pts[i].Y, level.Elevation);
            var bRaw = pts[(i + 1) % pts.Count];
            var b = new XYZ(bRaw.X, bRaw.Y, level.Elevation);
            if (a.DistanceTo(b) < 1e-6)
                throw new System.ArgumentException($"Profile segment {i} has zero length.");
            loop.Append(Line.CreateBound(a, b));
        }

        var loops = new List<CurveLoop> { loop };
        var floor = Floor.Create(doc, loops, floorType.Id, level.Id);

        return new JsonObject
        {
            ["id"] = floor.Id.Value,
            ["levelName"] = level.Name,
            ["floorTypeName"] = floorType.Name,
            ["pointCount"] = pts.Count,
        };
    }

    private static FloorType ResolveFloorType(Document doc, string? name)
    {
        var query = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return query.FirstOrDefault()
                ?? throw new System.InvalidOperationException("No FloorType exists in the document.");
        }
        return query.FirstOrDefault(t => t.Name == name)
            ?? throw new System.InvalidOperationException($"FloorType '{name}' not found.");
    }
}
