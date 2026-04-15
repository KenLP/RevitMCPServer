using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class ListWallTypesCommand : IRevitCommand
{
    public string Name => "list_wall_types";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .OrderBy(w => w.Name)
            .ToList();

        var arr = new JsonArray();
        foreach (var t in types)
        {
            arr.Add(new JsonObject
            {
                ["id"] = t.Id.Value,
                ["name"] = t.Name,
                ["familyName"] = t.FamilyName,
                ["kind"] = t.Kind.ToString(),
                ["widthFeet"] = SafeWidth(t),
            });
        }

        return new JsonObject
        {
            ["count"] = types.Count,
            ["wallTypes"] = arr,
        };
    }

    private static double? SafeWidth(WallType w)
    {
        try { return w.Width; } catch { return null; }
    }
}
