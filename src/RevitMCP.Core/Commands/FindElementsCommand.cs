using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Generic element query with category + parameter filters.
///
/// Params:
///   - category:   BuiltInCategory name, required
///   - view_id:    long, optional — scope the query to elements visible in that
///                 view (must be a non-template View). Omit for the whole document.
///   - filters:    [{parameterName, operator, value}], optional
///                 operator: "equals", "not_equals", "contains", "greater", "less"
///   - limit:      int, default 200, max 5000 (page size)
///   - offset:     int, default 0 — page start; page through all matches (no 5000 ceiling)
///   - fields:     string[] of parameter names to project (optional).
///                 Values resolve on the instance first, then fall back to the element Type.
///
/// Returns a paginated envelope: count (this page), total (all matches after filters),
/// offset, limit, hasMore, nextOffset. truncated is kept as an alias of hasMore.
/// </summary>
public sealed class FindElementsCommand : IRevitCommand
{
    public string Name => "find_elements";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var categoryName = P.Str(p, "category");
        if (!Enum.TryParse<BuiltInCategory>(categoryName, true, out var bic))
            throw new RevitCommandException("invalid_parameter", $"Unknown BuiltInCategory '{categoryName}'.");

        // Optional view scoping (parity with the spatial-QC fork). Validate up front
        // so a bad id surfaces as a clear domain error, not a raw ArgumentException
        // from the FilteredElementCollector constructor.
        var viewId = P.LongOrNull(p, "view_id");
        FilteredElementCollector collector;
        if (viewId is > 0)
        {
            var vid = new ElementId(viewId.Value);
            if (doc.GetElement(vid) is not View view)
                throw new RevitCommandException("invalid_parameter",
                    $"view_id {viewId.Value} is not a View.");
            if (view.IsTemplate)
                throw new RevitCommandException("invalid_parameter",
                    $"view_id {viewId.Value} is a view template — element collection needs a real view.");
            collector = new FilteredElementCollector(doc, vid);
        }
        else
        {
            collector = new FilteredElementCollector(doc);
        }
        collector = collector
            .OfCategory(bic)
            .WhereElementIsNotElementType();

        var limit = Math.Clamp(P.IntOr(p, "limit", 200), 1, 5000);
        var offset = Math.Max(0, P.IntOr(p, "offset", 0));
        var filtersArr = p["filters"] as JsonArray;
        var fieldsArr = p["fields"] as JsonArray;

        // Element.LookupParameter resolves INSTANCE parameters only. Many BIM parameters
        // (Fire Rating, door Width, assembly codes...) live on the element TYPE, so fall
        // back to the type when the instance lookup misses. Cache per (typeId, name) so N
        // elements sharing a type cost one type lookup, not N.
        var typeParamCache = new System.Collections.Generic.Dictionary<(long, string), Parameter?>();
        Parameter? LookupInstanceOrType(Element el, string fname)
        {
            var pr = el.LookupParameter(fname);
            if (pr is { HasValue: true }) return pr;

            var tid = el.GetTypeId();
            if (tid is null || tid == ElementId.InvalidElementId) return pr;
            var key = (tid.Value, fname);
            if (!typeParamCache.TryGetValue(key, out var cached))
            {
                cached = doc.GetElement(tid)?.LookupParameter(fname);
                typeParamCache[key] = cached;
            }
            return cached is { HasValue: true } ? cached : pr;
        }

        var elements = collector.ToList();

        // Apply parameter filters in-memory (simple approach — fast enough for <10k elements).
        if (filtersArr is { Count: > 0 })
        {
            foreach (var filterNode in filtersArr)
            {
                if (filterNode is not JsonObject fo) continue;
                var paramName = P.Str(fo, "parameterName");
                var op = P.StrOrNull(fo, "operator")?.ToLowerInvariant() ?? "equals";
                var matchValue = fo["value"];

                elements = elements.Where(el =>
                {
                    var param = LookupInstanceOrType(el, paramName);
                    if (param is null || !param.HasValue) return op == "not_equals";
                    return MatchParam(param, op, matchValue);
                }).ToList();
            }
        }

        // total = matches after filters, before paging — so the caller knows how
        // many exist and can page through all of them (no 5000 ceiling).
        var total = elements.Count;
        var page = elements.Skip(offset).Take(limit).ToList();

        var arr = new JsonArray();
        foreach (var el in page)
        {
            var obj = ListElementsCommand.SummarizeElement(el);

            // Project requested fields.
            if (fieldsArr is { Count: > 0 })
            {
                var fieldValues = new JsonObject();
                foreach (var fn in fieldsArr)
                {
                    var fname = fn?.GetValue<string>();
                    if (fname is null) continue;
                    var param = LookupInstanceOrType(el, fname);
                    if (param is not null && param.HasValue)
                    {
                        fieldValues[fname] = ReadValueNode(param);
                        fieldValues[fname + "_display"] = SafeValueString(param);
                    }
                }
                obj["fields"] = fieldValues;
            }
            arr.Add(obj);
        }

        var nextOffset = offset + page.Count;
        var hasMore = nextOffset < total;

        return new JsonObject
        {
            ["count"] = arr.Count,
            ["total"] = total,
            ["offset"] = offset,
            ["limit"] = limit,
            ["hasMore"] = hasMore,
            ["nextOffset"] = hasMore ? nextOffset : null,
            ["truncated"] = hasMore,
            ["elements"] = arr,
        };
    }

    private static bool MatchParam(Parameter param, string op, JsonNode? matchValue)
    {
        var strVal = SafeValueString(param) ?? "";
        var matchStr = matchValue?.ToString() ?? "";

        return op switch
        {
            "equals" or "eq" => string.Equals(strVal, matchStr, StringComparison.OrdinalIgnoreCase),
            "not_equals" or "neq" => !string.Equals(strVal, matchStr, StringComparison.OrdinalIgnoreCase),
            "contains" => strVal.Contains(matchStr, StringComparison.OrdinalIgnoreCase),
            "greater" or "gt" => double.TryParse(strVal, out var a) && double.TryParse(matchStr, out var b) && a > b,
            "less" or "lt" => double.TryParse(strVal, out var c) && double.TryParse(matchStr, out var d) && c < d,
            "greater_equal" or "gte" => double.TryParse(strVal, out var e) && double.TryParse(matchStr, out var f) && e >= f,
            "less_equal" or "lte" => double.TryParse(strVal, out var g) && double.TryParse(matchStr, out var h) && g <= h,
            _ => throw new RevitCommandException("invalid_parameter", $"Unknown operator '{op}'."),
        };
    }

    private static JsonNode? ReadValueNode(Parameter p)
    {
        return p.StorageType switch
        {
            StorageType.String => JsonValue.Create(p.AsString()),
            StorageType.Integer => JsonValue.Create(p.AsInteger()),
            StorageType.Double => JsonValue.Create(p.AsDouble()),
            StorageType.ElementId => JsonValue.Create(p.AsElementId()?.Value),
            _ => null,
        };
    }

    private static string? SafeValueString(Parameter p)
    {
        try { return p.AsValueString(); } catch { return p.AsString(); }
    }
}
