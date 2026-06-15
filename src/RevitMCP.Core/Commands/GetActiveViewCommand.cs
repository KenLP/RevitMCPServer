using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class GetActiveViewCommand : IRevitCommand
{
    public string Name => "get_active_view";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var uiDoc = ctx.RequireUIDoc();
        var view = uiDoc.ActiveView;
        return new JsonObject
        {
            ["id"] = view.Id.Value,
            ["name"] = view.Name,
            ["viewType"] = view.ViewType.ToString(),
            ["scale"] = view.Scale,
            ["detailLevel"] = view.DetailLevel.ToString(),
            ["levelId"] = (view as ViewPlan)?.GenLevel?.Id.Value,
            ["levelName"] = (view as ViewPlan)?.GenLevel?.Name,
        };
    }
}
