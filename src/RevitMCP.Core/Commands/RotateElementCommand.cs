using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Rotate an element around a vertical axis through a point.
///
/// Params:
///   - id:         long, required
///   - center:     { x, y, z? }, rotation axis origin
///   - angleDeg:   number, rotation angle in degrees (counter-clockwise)
///   - units:      "meters"|"feet"
/// </summary>
public sealed class RotateElementCommand : IRevitCommand
{
    public string Name => "rotate_element";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var id = new ElementId(P.Long(p, "id"));
        var element = doc.GetElement(id)
            ?? throw new RevitCommandException("not_found", $"No element with id {id.Value}.");

        var center = P.Xyz(p, "center", units);
        var angleDeg = P.Dbl(p, "angleDeg");
        var angleRad = angleDeg * Math.PI / 180.0;

        var axis = Line.CreateBound(center, center + XYZ.BasisZ);
        ElementTransformUtils.RotateElement(doc, id, axis, angleRad);
        return new JsonObject
        {
            ["id"] = id.Value,
            ["angleDeg"] = angleDeg,
            ["changeSummary"] = $"Rotated element {id.Value} ('{element.Name}') by {angleDeg:F1}° around ({center.X:F2}, {center.Y:F2}) ft",
        };
    }
}
