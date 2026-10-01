using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a 3D view.
///
/// Behaviour: duplicates the currently active View3D (WithDetailing — preserves
/// visibility settings, filters, and section-box state). Falls back to a blank
/// isometric view if the active view is not a 3D view or cannot be duplicated.
///
/// Params:
///   - viewName: string, optional — name for the new view. Applied exactly, or the command
///               fails (invalid_chars 400 / name_collision 409) and nothing is created.
/// </summary>
public sealed class Create3DViewCommand : IRevitCommand
{
    public string Name => "create_3d_view";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var viewName = P.StrOrNull(ctx.Parameters, "viewName");

        View3D view;
        bool duplicated = false;

        // Try to duplicate the active View3D
        var active3d = doc.ActiveView as View3D;
        if (active3d != null && !active3d.IsTemplate
            && active3d.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing))
        {
            var newId = active3d.Duplicate(ViewDuplicateOption.WithDetailing);
            view = (View3D)doc.GetElement(newId);
            duplicated = true;
        }
        else
        {
            // Fallback: blank isometric view
            var vft = CreateFloorPlanViewCommand.GetViewFamilyType(doc, ViewFamily.ThreeDimensional);
            view = View3D.CreateIsometric(doc, vft.Id);
        }

        // Applied exactly or the command fails and rolls back (see NameRules) — never the old
        // swallowed setter that kept Revit's placeholder name and still reported success.
        if (!string.IsNullOrWhiteSpace(viewName))
            NameRules.ApplyViewName(view, viewName!, "viewName");

        return new JsonObject
        {
            ["id"] = view.Id.Value,
            ["name"] = view.Name,
            ["viewType"] = view.ViewType.ToString(),
            ["duplicatedFrom"] = duplicated ? active3d!.Id.Value : (long?)null,
        };
    }
}
