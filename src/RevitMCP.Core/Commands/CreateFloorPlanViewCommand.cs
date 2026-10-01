using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a new floor plan view for a given level.
///
/// Params:
///   - levelName: string, required
///   - viewName:  string, optional (renames after creation). Applied exactly, or the command
///                fails (invalid_chars 400 / name_collision 409) and nothing is created.
/// </summary>
public sealed class CreateFloorPlanViewCommand : IRevitCommand
{
    public string Name => "create_floor_plan_view";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var level = CreateWallCommand.ResolveLevel(doc, P.Str(p, "levelName"));
        var vft = GetViewFamilyType(doc, ViewFamily.FloorPlan);

        var view = ViewPlan.Create(doc, vft.Id, level.Id);

        var viewName = P.StrOrNull(p, "viewName");
        // Applied exactly or the command fails and rolls back (see NameRules) — never the old
        // swallowed setter that kept Revit's placeholder name and still reported success.
        if (!string.IsNullOrWhiteSpace(viewName))
            NameRules.ApplyViewName(view, viewName!, "viewName");

        return new JsonObject
        {
            ["id"] = view.Id.Value,
            ["name"] = view.Name,
            ["viewType"] = view.ViewType.ToString(),
            ["levelName"] = level.Name,
        };
    }

    internal static ViewFamilyType GetViewFamilyType(Document doc, ViewFamily family)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == family)
            ?? throw new RevitCommandException("not_found",
                $"No ViewFamilyType for {family} found.");
    }
}
