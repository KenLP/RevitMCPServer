using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class ListMaterialsCommand : IRevitCommand
{
    public string Name => "list_materials";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var mats = new FilteredElementCollector(doc)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .OrderBy(m => m.Name)
            .ToList();

        var arr = new JsonArray();
        foreach (var m in mats)
        {
            arr.Add(new JsonObject
            {
                ["id"] = m.Id.Value,
                ["name"] = m.Name,
                ["materialClass"] = m.MaterialClass,
                ["materialCategory"] = m.MaterialCategory,
                ["color"] = m.Color is { IsValid: true } c
                    ? $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}" : null,
                ["transparency"] = m.Transparency,
            });
        }

        return new JsonObject { ["count"] = mats.Count, ["materials"] = arr };
    }
}
