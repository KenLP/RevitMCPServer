using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Place an IndependentTag on an element in the active view.
///
/// Params:
///   - elementId: long, required
///   - location:  { x, y, z? }, optional offset for tag head (default = element bbox center)
///   - addLeader: bool, default false
///   - viewId:    long, optional (defaults to active view)
///   - units:     "meters"|"feet"
/// </summary>
public sealed class TagElementCommand : IRevitCommand
{
    public string Name => "tag_element";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var elementId = new ElementId(P.Long(p, "elementId"));
        var element = doc.GetElement(elementId)
            ?? throw new RevitCommandException("not_found", $"Element {elementId.Value} not found.");

        var viewId = p["viewId"] is not null
            ? new ElementId(P.Long(p, "viewId"))
            : doc.ActiveView?.Id
            ?? throw new RevitCommandException("not_found", "No active view.");

        var view = doc.GetElement(viewId) as View
            ?? throw new RevitCommandException("not_found", $"View {viewId.Value} not found.");

        var addLeader = P.BoolOr(p, "addLeader", false);

        XYZ location;
        if (p["location"] is JsonObject locObj)
        {
            location = P.Xyz(p, "location", units);
        }
        else
        {
            var bbox = element.get_BoundingBox(view);
            location = bbox is not null
                ? (bbox.Min + bbox.Max) / 2.0
                : XYZ.Zero;
        }

        var reference = new Reference(element);
        var tag = IndependentTag.Create(
            doc, viewId, reference, addLeader,
            TagMode.TM_ADDBY_CATEGORY,
            TagOrientation.Horizontal,
            location);

        return new JsonObject
        {
            ["tagId"] = tag.Id.Value,
            ["elementId"] = elementId.Value,
            ["viewId"] = viewId.Value,
        };
    }
}
