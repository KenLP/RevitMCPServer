using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Deterministic element query: count + list elements matching where-conditions,
/// resolving each condition/field at the correct scope (instance OR type). This is
/// the read half of the "LLM-at-edge" design — the model emits the spec, this code
/// does the exact matching.
///
/// Params:
///   - category:  BuiltInCategory name, required.
///   - where:     [{ parameter, operator, value?, scope? }], optional (AND).
///                operators: eq,neq,contains,starts_with,ends_with,regex,not_regex,
///                gt,lt,gte,lte,is_empty,not_empty. scope: auto|instance|type.
///   - select:    [string] parameter names to return per row (scope auto), optional.
///   - limit:     int, max rows returned (default 100). count is the TRUE total.
/// </summary>
public sealed class QueryWhereCommand : IRevitCommand
{
    public string Name => "query_where";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var bic = WhereSupport.ResolveCategory(P.Str(p, "category"));
        var conds = WhereSupport.ParseWhere(p["where"] as JsonArray);
        var select = (p["select"] as JsonArray)?
            .Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!).ToList() ?? new System.Collections.Generic.List<string>();
        var limit = System.Math.Clamp(P.IntOr(p, "limit", 100), 1, 1000);
        var viewId = P.LongOrNull(p, "view_id");

        var rows = new JsonArray();
        var count = 0;

        foreach (var el in WhereSupport.CollectInstances(doc, bic, viewId))
        {
            if (!WhereSupport.Matches(doc, el, conds)) continue;
            count++;
            if (rows.Count >= limit) continue;

            var row = new JsonObject { ["id"] = el.Id.Value, ["name"] = el.Name };
            foreach (var s in select)
            {
                var (param, scope) = WhereSupport.ResolveParam(doc, el, s, "auto");
                row[s] = WhereSupport.GetText(param);
                if (scope == "type") row[s + "_scope"] = "type";
            }
            rows.Add(row);
        }

        return new JsonObject
        {
            ["category"] = bic.ToString(),
            ["count"] = count,
            ["returned"] = rows.Count,
            ["truncated"] = count > rows.Count,
            ["rows"] = rows,
        };
    }
}
