using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a straight Grid line.
///
/// Parameters:
///   - start: { x, y, z? }    required
///   - end:   { x, y, z? }    required
///   - name:  string          optional, e.g. "A", "1"
///   - units: "meters"|"feet" optional, default "meters"
/// </summary>
public sealed class CreateGridCommand : IRevitCommand
{
    public string Name => "create_grid";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var start = P.Xyz(p, "start", units);
        var end = P.Xyz(p, "end", units);
        if (start.DistanceTo(end) < 1e-6)
            throw new RevitCommandException("invalid_parameter", "Start and end points are coincident.");

        // Grids are flat in plan — force Z=0.
        var line = Line.CreateBound(
            new XYZ(start.X, start.Y, 0),
            new XYZ(end.X, end.Y, 0));

        var grid = Grid.Create(doc, line);

        var name = P.StrOrNull(p, "name");
        string? renameWarning = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { grid.Name = name; }
            catch (System.Exception ex) { renameWarning = ex.Message; }
        }

        var result = new JsonObject
        {
            ["id"] = grid.Id.Value,
            ["name"] = grid.Name,
            ["lengthFeet"] = line.Length,
        };
        if (renameWarning != null) result["renameWarning"] = renameWarning;
        return result;
    }
}
