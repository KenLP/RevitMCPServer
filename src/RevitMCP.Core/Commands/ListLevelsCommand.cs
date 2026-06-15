using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class ListLevelsCommand : IRevitCommand
{
    public string Name => "list_levels";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        var arr = new JsonArray();
        foreach (var l in levels)
        {
            arr.Add(new JsonObject
            {
                ["id"] = l.Id.Value,
                ["name"] = l.Name,
                ["elevationFeet"] = l.Elevation,
                ["elevationMeters"] = l.Elevation * P.FeetToMeters,
            });
        }

        return new JsonObject
        {
            ["count"] = levels.Count,
            ["levels"] = arr,
        };
    }
}
