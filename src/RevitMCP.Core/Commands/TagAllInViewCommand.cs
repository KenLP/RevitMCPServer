using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Tag all (untagged) elements of a given category in a view.
/// Mirrors Revit's "Tag All Not Tagged" workflow.
///
/// Params:
///   - category:    string, required — category display name, e.g. "Doors", "Windows"
///   - viewId:      long, optional (defaults to active view)
///   - leader:      bool, default false
///   - skipTagged:  bool, default true — skip elements that already have a tag
/// </summary>
public sealed class TagAllInViewCommand : IRevitCommand
{
    public string Name => "tag_all_in_view";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var viewId = p["viewId"] is not null
            ? new ElementId(P.Long(p, "viewId"))
            : doc.ActiveView?.Id
            ?? throw new RevitCommandException("not_found", "No active view.");

        var view = doc.GetElement(viewId) as View
            ?? throw new RevitCommandException("not_found", $"View {viewId.Value} not found.");

        var categoryName = P.Str(p, "category");
        var addLeader = P.BoolOr(p, "leader", false);
        var skipTagged = P.BoolOr(p, "skipTagged", true);

        // Resolve category by display name
        var cat = doc.Settings.Categories.Cast<Category>()
            .FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase))
            ?? throw new RevitCommandException("not_found",
                $"Category '{categoryName}' not found. Use list_categories to see valid names.");

        // Collect already-tagged element IDs in this view (to honour skipTagged)
        var alreadyTagged = new HashSet<long>();
        if (skipTagged)
        {
            foreach (var tag in new FilteredElementCollector(doc, viewId)
                         .OfClass(typeof(IndependentTag))
                         .Cast<IndependentTag>())
            {
                try
                {
                    var ids = tag.GetTaggedLocalElementIds();
                    if (ids is not null)
                        foreach (var tid in ids)
                            if (tid != ElementId.InvalidElementId)
                                alreadyTagged.Add(tid.Value);
                }
                catch { }
            }
        }

        var taggedArr = new JsonArray();
        int skipped = 0, failed = 0;

        foreach (var element in new FilteredElementCollector(doc, viewId)
                     .OfCategoryId(cat.Id)
                     .WhereElementIsNotElementType()
                     .ToElements())
        {
            if (skipTagged && alreadyTagged.Contains(element.Id.Value))
            {
                skipped++;
                continue;
            }

            try
            {
                var bbox = element.get_BoundingBox(view);
                var loc = bbox is not null ? (bbox.Min + bbox.Max) / 2.0 : XYZ.Zero;

                var tag = IndependentTag.Create(
                    doc, viewId, new Reference(element), addLeader,
                    TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, loc);

                taggedArr.Add(new JsonObject
                {
                    ["tagId"] = tag.Id.Value,
                    ["elementId"] = element.Id.Value,
                });
            }
            catch
            {
                failed++;
            }
        }

        return new JsonObject
        {
            ["tagged"] = taggedArr.Count,
            ["skipped"] = skipped,
            ["failed"] = failed,
            ["tags"] = taggedArr,
        };
    }
}
