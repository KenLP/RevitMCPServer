using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class ListFloorTypesCommand : IRevitCommand
{
    public string Name => "list_floor_types";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(FloorType))
            .Cast<FloorType>()
            .OrderBy(t => t.Name)
            .ToList();

        var arr = new JsonArray();
        foreach (var t in types)
        {
            arr.Add(new JsonObject
            {
                ["id"] = t.Id.Value,
                ["name"] = t.Name,
                ["familyName"] = t.FamilyName,
            });
        }

        return new JsonObject
        {
            ["count"] = types.Count,
            ["floorTypes"] = arr,
        };
    }
}
