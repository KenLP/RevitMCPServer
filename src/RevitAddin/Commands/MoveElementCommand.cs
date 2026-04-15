using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Translate one element by a vector.
///
/// Parameters:
///   - id:          long, required
///   - translation: { x, y, z? }, required (in user units)
///   - units:       "meters"|"feet", default "meters"
/// </summary>
public sealed class MoveElementCommand : IRevitCommand
{
    public string Name => "move_element";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var id = new ElementId(P.Long(p, "id"));
        var element = doc.GetElement(id)
            ?? throw new System.InvalidOperationException($"No element with id {id.Value}.");

        var translation = P.Xyz(p, "translation", units);

        // Capture before-position from bounding box.
        var bbBefore = element.get_BoundingBox(null);
        var beforeCenter = bbBefore is not null
            ? new JsonObject { ["x"] = (bbBefore.Min.X + bbBefore.Max.X) / 2, ["y"] = (bbBefore.Min.Y + bbBefore.Max.Y) / 2, ["z"] = (bbBefore.Min.Z + bbBefore.Max.Z) / 2 }
            : null;

        ElementTransformUtils.MoveElement(doc, id, translation);

        var bbAfter = element.get_BoundingBox(null);
        var afterCenter = bbAfter is not null
            ? new JsonObject { ["x"] = (bbAfter.Min.X + bbAfter.Max.X) / 2, ["y"] = (bbAfter.Min.Y + bbAfter.Max.Y) / 2, ["z"] = (bbAfter.Min.Z + bbAfter.Max.Z) / 2 }
            : null;

        return new JsonObject
        {
            ["id"] = id.Value,
            ["name"] = element.Name,
            ["translationFeet"] = new JsonObject
            {
                ["x"] = translation.X,
                ["y"] = translation.Y,
                ["z"] = translation.Z,
            },
            ["changes"] = new JsonObject
            {
                ["beforeCenter"] = beforeCenter,
                ["afterCenter"] = afterCenter,
            },
            ["changeSummary"] = $"Moved element {id.Value} ('{element.Name}') by ({translation.X:F2}, {translation.Y:F2}, {translation.Z:F2}) ft",
        };
    }
}
