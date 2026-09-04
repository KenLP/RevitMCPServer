using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Copy one or more elements by a translation vector.
///
/// Params:
///   - ids:         long[], required
///   - translation: { x, y, z? }
///   - units:       "meters"|"feet"
/// </summary>
public sealed class CopyElementCommand : IRevitCommand
{
    public string Name => "copy_element";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var idsArr = P.Arr(p, "ids");
        var ids = new List<ElementId>();
        for (var i = 0; i < idsArr.Count; i++)
            ids.Add(new ElementId(P.LongFrom(idsArr[i], $"ids[{i}]")));

        var translation = P.Xyz(p, "translation", units);
        var newIds = ElementTransformUtils.CopyElements(doc, ids, translation);

        var arr = new JsonArray();
        foreach (var id in newIds) arr.Add(id.Value);

        return new JsonObject
        {
            ["copiedCount"] = newIds.Count,
            ["newIds"] = arr,
        };
    }
}
