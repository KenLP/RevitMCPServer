using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// List all IndependentTag elements in a view, optionally filtered by tagged element category.
///
/// Params:
///   - viewId:    long, optional (defaults to active view)
///   - category:  string, optional — filter by tagged element category name, e.g. "Doors"
/// </summary>
public sealed class GetTagsInViewCommand : IRevitCommand
{
    public string Name => "get_tags_in_view";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var viewId = p["viewId"] is not null
            ? new ElementId(P.Long(p, "viewId"))
            : doc.ActiveView?.Id
            ?? throw new RevitCommandException("not_found", "No active view.");

        if (doc.GetElement(viewId) is not View)
            throw new RevitCommandException("not_found", $"View {viewId.Value} not found.");

        var categoryFilter = P.StrOrNull(p, "category");

        var result = new JsonArray();

        foreach (var tag in new FilteredElementCollector(doc, viewId)
                     .OfClass(typeof(IndependentTag))
                     .Cast<IndependentTag>())
        {
            ElementId? taggedEid = null;
            string? catName = null;

            try
            {
                taggedEid = tag.GetTaggedLocalElementIds()?.FirstOrDefault();
                if (taggedEid is not null && taggedEid != ElementId.InvalidElementId)
                {
                    var taggedEl = doc.GetElement(taggedEid);
                    catName = taggedEl?.Category?.Name;
                }
            }
            catch { }

            // Apply category filter
            if (categoryFilter is not null &&
                !string.Equals(catName, categoryFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            XYZ? headPos = null;
            try { headPos = tag.TagHeadPosition; } catch { }

            result.Add(new JsonObject
            {
                ["tagId"] = tag.Id.Value,
                ["elementId"] = taggedEid?.Value,
                ["category"] = catName,
                ["hasLeader"] = tag.HasLeader,
                ["tagText"] = tag.TagText,
                ["location"] = headPos is null ? null : new JsonObject
                {
                    ["x"] = headPos.X * P.FeetToMeters,
                    ["y"] = headPos.Y * P.FeetToMeters,
                    ["z"] = headPos.Z * P.FeetToMeters,
                },
            });
        }

        return new JsonObject
        {
            ["viewId"] = viewId.Value,
            ["count"] = result.Count,
            ["tags"] = result,
        };
    }
}
