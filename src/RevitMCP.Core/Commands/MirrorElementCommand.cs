using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Mirror elements across a plane. The plane is defined by a point on the
/// plane and a normal vector.
///
/// Params:
///   - ids:      long[], required
///   - origin:   { x, y, z? }, point on the mirror plane
///   - normal:   { x, y, z }, normal direction of the plane (e.g. {1,0,0} for YZ mirror)
///   - copy:     bool, default true (false = move, true = copy + mirror)
///   - units:    "meters"|"feet"
/// </summary>
public sealed class MirrorElementCommand : IRevitCommand
{
    public string Name => "mirror_element";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var idsArr = P.Arr(p, "ids");
        var ids = new List<ElementId>();
        for (var i = 0; i < idsArr.Count; i++)
            ids.Add(new ElementId(P.LongFrom(idsArr[i], $"ids[{i}]")));

        var origin = P.Xyz(p, "origin", units);
        var normal = P.Xyz(p, "normal", "feet"); // unitless direction
        if (normal.GetLength() < 1e-9)
            throw new RevitCommandException("invalid_parameter", "Normal vector must be non-zero.");

        var plane = Plane.CreateByNormalAndOrigin(normal.Normalize(), origin);
        var copy = P.BoolOr(p, "copy", true);

        if (copy)
        {
            var newIds = ElementTransformUtils.MirrorElements(doc, ids, plane, true);
            var arr = new JsonArray();
            foreach (var id in newIds) arr.Add(id.Value);
            return new JsonObject
            {
                ["mirrored"] = ids.Count,
                ["copied"] = true,
                ["newIds"] = arr,
            };
        }
        else
        {
            ElementTransformUtils.MirrorElements(doc, ids, plane, false);
            return new JsonObject
            {
                ["mirrored"] = ids.Count,
                ["copied"] = false,
            };
        }
    }
}
