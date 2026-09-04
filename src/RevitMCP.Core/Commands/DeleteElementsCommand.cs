using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Delete one or more elements by id.
///
/// Parameters:
///   - ids: number[]   required, ElementId.Value list
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
        for (var i = 0; i < arr.Count; i++)
            ids.Add(new ElementId(P.LongFrom(arr[i], $"ids[{i}]")));
        if (ids.Count == 0)
            throw new RevitCommandException("invalid_parameter", "'ids' must contain at least one ElementId.");

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
