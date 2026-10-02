using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Delete one or more elements by id.
///
/// Parameters:
///   - ids: number[]   required, ElementId.Value list. Repeats are ignored. Every id must exist:
///                     if any does not, the call fails with not_found (404) naming the missing ids
///                     and nothing is deleted — the request is all-or-nothing.
/// Returns:
///   - requested:  count
///   - deleted:    count actually deleted by Revit (Document.Delete returns
///                 the elements truly removed, which may be larger than the
///                 requested set because of dependent cleanup)
///   - deletedIds: long[]
/// </summary>
public sealed class DeleteElementsCommand : IRevitCommand
{
    public string Name => "delete_elements";
    public bool IsReadOnly => false;
    public string RiskLevel => "high";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var arr = P.Arr(ctx.Parameters, "ids");

        var ids = new List<ElementId>(arr.Count);
        var seen = new HashSet<long>();
        for (var i = 0; i < arr.Count; i++)
        {
            var v = P.LongFrom(arr[i], $"ids[{i}]");
            if (seen.Add(v)) ids.Add(new ElementId(v));
        }
        if (ids.Count == 0)
            throw new RevitCommandException("invalid_parameter", "'ids' must contain at least one ElementId.");

        // Checked up front: Document.Delete rejects the WHOLE set when one id is missing, and that
        // surfaced as a bare command_failed 500 with Revit's message — no way for the caller to
        // tell which id, and easy to mistake for a server fault.
        var missing = ids.Where(id => doc.GetElement(id) is null).Select(id => id.Value).ToList();
        if (missing.Count > 0)
        {
            const int show = 20;
            var list = string.Join(", ", missing.Take(show)) + (missing.Count > show ? $", … (+{missing.Count - show})" : "");
            throw new RevitCommandException("not_found",
                $"{missing.Count} of {ids.Count} element id(s) do not exist in the document: {list}. Nothing was deleted.");
        }

        var deleted = doc.Delete(ids);

        var deletedIds = new JsonArray();
        foreach (var id in deleted) deletedIds.Add(id.Value);

        return new JsonObject
        {
            ["requested"] = ids.Count,
            ["deleted"] = deleted.Count,
            ["deletedIds"] = deletedIds,
            ["changeSummary"] = $"Deleted {deleted.Count} elements (requested {ids.Count})",
        };
    }
}
