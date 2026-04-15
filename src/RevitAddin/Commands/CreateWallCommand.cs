using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Creates a single straight wall.  Transaction is owned by the dispatcher.
///
/// Parameters (units: meters by default; pass "units":"feet" for imperial):
///   - start:        { x, y, z? }     required
///   - end:          { x, y, z? }     required
///   - height:       number           optional, default 3.0 (in user units)
///   - levelName:    string           optional, defaults to lowest level
///   - wallTypeName: string           optional, defaults to first WallType
///   - structural:   bool             optional, default false
///   - units:        "meters"|"feet"  optional, default "meters"
/// </summary>
public sealed class CreateWallCommand : IRevitCommand
{
    public string Name => "create_wall";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        var toFeet = units == "feet" ? 1.0 : P.MetersToFeet;

        var start = P.Xyz(p, "start", units);
        var end = P.Xyz(p, "end", units);
        if (start.DistanceTo(end) < 1e-6)
            throw new System.ArgumentException("Start and end points are coincident.");

        var height = P.DblOr(p, "height", 3.0) * toFeet;
        var structural = P.BoolOr(p, "structural", false);

        var level = ResolveLevel(doc, P.StrOrNull(p, "levelName"));
        var wallType = ResolveWallType(doc, P.StrOrNull(p, "wallTypeName"));

        var baseline = Line.CreateBound(
            new XYZ(start.X, start.Y, level.Elevation),
            new XYZ(end.X, end.Y, level.Elevation));

        var created = Wall.Create(
            document: doc,
            curve: baseline,
            wallTypeId: wallType.Id,
            levelId: level.Id,
            height: height,
            offset: 0.0,
            flip: false,
            structural: structural);

        return new JsonObject
        {
            ["id"] = created.Id.Value,
            ["levelName"] = level.Name,
            ["wallTypeName"] = wallType.Name,
            ["lengthFeet"] = baseline.Length,
            ["heightFeet"] = height,
        };
    }

    internal static Level ResolveLevel(Document doc, string? name)
    {
        var query = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return query.OrderBy(l => l.Elevation).FirstOrDefault()
                ?? throw new System.InvalidOperationException("No Level exists in the document.");
        }
        return query.FirstOrDefault(l => l.Name == name)
            ?? throw new System.InvalidOperationException($"Level '{name}' not found.");
    }

    internal static WallType ResolveWallType(Document doc, string? name)
    {
        var query = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return query.FirstOrDefault()
                ?? throw new System.InvalidOperationException("No WallType exists in the document.");
        }
        return query.FirstOrDefault(w => w.Name == name)
            ?? throw new System.InvalidOperationException($"WallType '{name}' not found.");
    }
}
