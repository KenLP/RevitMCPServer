using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create an isometric 3D view.
///
/// Params:
///   - viewName: string, optional
/// </summary>
public sealed class Create3DViewCommand : IRevitCommand
{
    public string Name => "create_3d_view";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var vft = CreateFloorPlanViewCommand.GetViewFamilyType(doc, ViewFamily.ThreeDimensional);
        var view = View3D.CreateIsometric(doc, vft.Id);

        var viewName = P.StrOrNull(ctx.Parameters, "viewName");
        if (!string.IsNullOrWhiteSpace(viewName))
        {
            try { view.Name = viewName; } catch { }
        }

        return new JsonObject
        {
            ["id"] = view.Id.Value,
            ["name"] = view.Name,
            ["viewType"] = view.ViewType.ToString(),
        };
    }
}
