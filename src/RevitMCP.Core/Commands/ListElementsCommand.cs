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
///   - limit:           int,  page size, default 200, max 5000
///   - offset:          int,  page start, default 0 — page through large sets
///                            (no 5000 ceiling: increase offset to fetch the rest)
///
/// Returns a paginated envelope: count (this page), total (all matches), offset,
/// limit, hasMore, nextOffset.  truncated is kept as an alias of hasMore.
/// </summary>
public sealed class ListElementsCommand : IRevitCommand
{
    public string Name => "list_elements";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        BuiltInCategory? bic = null;
        var categoryName = P.StrOrNull(p, "category");
        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            if (!Enum.TryParse<BuiltInCategory>(categoryName, ignoreCase: true, out var parsed))
                throw new RevitCommandException("invalid_parameter", $"Unknown BuiltInCategory '{categoryName}'.");
            bic = parsed;
        }

        var onlyInstances = P.BoolOr(p, "onlyInstances", true);

        // Build a fresh collector each time — a FilteredElementCollector should not
        // be reused after a terminal operation like GetElementCount().
        FilteredElementCollector BuildCollector()
        {
            var c = new FilteredElementCollector(doc);
            if (bic.HasValue) c = c.OfCategory(bic.Value);
            if (onlyInstances) c = c.WhereElementIsNotElementType();
            return c;
        }

        var limit = Math.Clamp(P.IntOr(p, "limit", 200), 1, 5000);
        var offset = Math.Max(0, P.IntOr(p, "offset", 0));
        var total = BuildCollector().GetElementCount();

        var items = new JsonArray();
        var index = 0;
        var emitted = 0;
        foreach (var el in BuildCollector())
        {
            if (index++ < offset) continue;
            if (emitted >= limit) break;
            items.Add(SummarizeElement(el));
            emitted++;
        }

        var nextOffset = offset + emitted;
        var hasMore = nextOffset < total;

        return new JsonObject
        {
            ["count"] = emitted,
            ["total"] = total,
            ["offset"] = offset,
            ["limit"] = limit,
            ["hasMore"] = hasMore,
            ["nextOffset"] = hasMore ? nextOffset : null,
            ["truncated"] = hasMore,
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
