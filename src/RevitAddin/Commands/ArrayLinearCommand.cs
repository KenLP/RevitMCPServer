using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Copy element(s) N times along a vector (linear array).
///
/// Params:
///   - ids:       long[], required
///   - count:     int, required (number of copies, ≥1)
///   - spacing:   { x, y, z? }, translation per copy
///   - units:     "meters"|"feet"
/// </summary>
public sealed class ArrayLinearCommand : IRevitCommand
{
    public string Name => "array_linear";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var idsArr = P.Arr(p, "ids");
        var ids = new List<ElementId>();
        foreach (var n in idsArr)
        {
            if (n is null) continue;
            ids.Add(new ElementId(n.GetValue<long>()));
        }

        var count = P.Int(p, "count");
        if (count < 1) throw new RevitCommandException("invalid_parameter", "Count must be >= 1.");

        var spacing = P.Xyz(p, "spacing", units);
        var allNewIds = new JsonArray();

        for (var i = 1; i <= count; i++)
        {
            var offset = new XYZ(spacing.X * i, spacing.Y * i, spacing.Z * i);
            var newIds = ElementTransformUtils.CopyElements(doc, ids, offset);
            foreach (var id in newIds) allNewIds.Add(id.Value);
        }

        return new JsonObject
        {
            ["copies"] = count,
            ["totalNewElements"] = allNewIds.Count,
            ["newIds"] = allNewIds,
        };
    }
}
