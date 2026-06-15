using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class ListViewTemplatesCommand : IRevitCommand
{
    public string Name => "list_view_templates";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var templates = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => v.IsTemplate)
            .OrderBy(v => v.Name)
            .ToList();

        var arr = new JsonArray();
        foreach (var t in templates)
        {
            arr.Add(new JsonObject
            {
                ["id"] = t.Id.Value,
                ["name"] = t.Name,
                ["viewType"] = t.ViewType.ToString(),
            });
        }

        return new JsonObject { ["count"] = templates.Count, ["viewTemplates"] = arr };
    }
}
