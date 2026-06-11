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
///   - filters:    [{parameterName, operator, value}], optional
///                 operator: "equals", "not_equals", "contains", "greater", "less"
///   - limit:      int, default 200, max 5000
///   - fields:     string[] of parameter names to project (optional)
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

        var collector = new FilteredElementCollector(doc)
            .OfCategory(bic)
            .WhereElementIsNotElementType();

        var limit = Math.Clamp(P.IntOr(p, "limit", 200), 1, 5000);
        var filtersArr = p["filters"] as JsonArray;
        var fieldsArr = p["fields"] as JsonArray;

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
                    var param = el.LookupParameter(paramName);
                    if (param is null || !param.HasValue) return op == "not_equals";
                    return MatchParam(param, op, matchValue);
                }).ToList();
            }
        }

        elements = elements.Take(limit).ToList();

        var arr = new JsonArray();
        foreach (var el in elements)
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
                    var param = el.LookupParameter(fname);
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

        return new JsonObject
        {
            ["count"] = arr.Count,
            ["limit"] = limit,
            ["truncated"] = elements.Count == limit,
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
