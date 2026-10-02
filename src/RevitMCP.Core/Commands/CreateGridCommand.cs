using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a straight Grid line.
///
/// Parameters:
///   - start: { x, y, z? }    required
///   - end:   { x, y, z? }    required
///   - name:  string          optional, e.g. "A", "1". Applied exactly, or the command fails
///                            (invalid_chars 400 / name_collision 409) and no grid is created.
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

        // A taken label used to leave a second grid under an auto name with a renameWarning —
        // a duplicate grid the caller did not ask for. Now the name lands or the call rolls back.
        var name = P.StrOrNull(p, "name");
        if (!string.IsNullOrWhiteSpace(name))
            NameRules.ApplyGridName(grid, name!);

        var result = new JsonObject
        {
            ["id"] = grid.Id.Value,
            ["name"] = grid.Name,
            ["lengthFeet"] = line.Length,
        };
        return result;
    }
}
