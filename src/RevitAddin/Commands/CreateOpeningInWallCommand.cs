using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a rectangular opening in a wall.
///
/// Params:
///   - wallId:  long, required
///   - lower:   { x, y, z }, bottom-left corner of opening
///   - upper:   { x, y, z }, top-right corner of opening
///   - units:   "meters"|"feet"
/// </summary>
public sealed class CreateOpeningInWallCommand : IRevitCommand
{
    public string Name => "create_opening_in_wall";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var wallId = new ElementId(P.Long(p, "wallId"));
        var wall = doc.GetElement(wallId) as Wall
            ?? throw new System.InvalidOperationException($"Element {wallId.Value} is not a Wall.");

        var lower = P.Xyz(p, "lower", units);
        var upper = P.Xyz(p, "upper", units);

        var opening = doc.Create.NewOpening(wall, lower, upper);

        return new JsonObject
        {
            ["openingId"] = opening.Id.Value,
            ["wallId"] = wallId.Value,
        };
    }
}
