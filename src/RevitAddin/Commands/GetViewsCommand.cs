using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class GetViewsCommand : IRevitCommand
{
    public string Name => "get_views";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var views = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate)
            .OrderBy(v => v.ViewType.ToString())
            .ThenBy(v => v.Name)
            .ToList();

        var arr = new JsonArray();
        foreach (var v in views)
        {
            arr.Add(new JsonObject
            {
                ["id"] = v.Id.Value,
                ["name"] = v.Name,
                ["viewType"] = v.ViewType.ToString(),
                ["levelId"] = (v as ViewPlan)?.GenLevel?.Id.Value,
                ["levelName"] = (v as ViewPlan)?.GenLevel?.Name,
                ["scale"] = v.Scale,
                ["detailLevel"] = v.DetailLevel.ToString(),
                ["isTemplate"] = v.IsTemplate,
                ["templateId"] = v.ViewTemplateId?.Value,
            });
        }

        return new JsonObject { ["count"] = views.Count, ["views"] = arr };
    }
}
