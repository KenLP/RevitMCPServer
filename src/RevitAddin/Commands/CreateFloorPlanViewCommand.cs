using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a new floor plan view for a given level.
///
/// Params:
///   - levelName: string, required
///   - viewName:  string, optional (renames after creation)
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
        if (!string.IsNullOrWhiteSpace(viewName))
        {
            try { view.Name = viewName; } catch { }
        }

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
            ?? throw new System.InvalidOperationException(
                $"No ViewFamilyType for {family} found.");
    }
}
