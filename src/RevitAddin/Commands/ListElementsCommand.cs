using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Read-only listing of elements in the active document.
///
/// Parameters (all optional):
///   - category:        BuiltInCategory enum name, e.g. "OST_Walls", "OST_Doors"
///   - onlyInstances:   bool, default true
///   - limit:           int,  default 200, max 5000
/// </summary>
public sealed class ListElementsCommand : IRevitCommand
{
    public string Name => "list_elements";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        FilteredElementCollector collector = new(doc);

        var categoryName = P.StrOrNull(p, "category");
        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            if (!Enum.TryParse<BuiltInCategory>(categoryName, ignoreCase: true, out var bic))
                throw new ArgumentException($"Unknown BuiltInCategory '{categoryName}'.");
            collector = collector.OfCategory(bic);
        }

        var onlyInstances = P.BoolOr(p, "onlyInstances", true);
        if (onlyInstances)
            collector = (FilteredElementCollector)collector.WhereElementIsNotElementType();

        var limit = Math.Clamp(P.IntOr(p, "limit", 200), 1, 5000);

        var items = new JsonArray();
        var count = 0;
        foreach (var el in collector)
        {
            if (count >= limit) break;
            items.Add(SummarizeElement(el));
            count++;
        }

        return new JsonObject
        {
            ["count"] = count,
            ["limit"] = limit,
            ["truncated"] = count == limit,
            ["elements"] = items,
        };
    }

    internal static JsonObject SummarizeElement(Element el)
    {
        return new JsonObject
        {
            ["id"] = el.Id.Value,
            ["name"] = el.Name,
            ["category"] = el.Category?.Name,
            ["categoryEnum"] = el.Category?.BuiltInCategory.ToString(),
            ["typeId"] = el.GetTypeId()?.Value,
        };
    }
}
