using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands;

public sealed class ListRoomsCommand : IRevitCommand
{
    public string Name => "list_rooms";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var rooms = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(r => r.Area > 0)
            .OrderBy(r => r.Number)
            .ToList();

        var arr = new JsonArray();
        foreach (var r in rooms)
        {
            arr.Add(new JsonObject
            {
                ["id"] = r.Id.Value,
                ["name"] = r.Name,
                ["number"] = r.Number,
                ["levelId"] = r.LevelId?.Value,
                ["levelName"] = r.Level?.Name,
                ["area"] = r.Area,
                ["areaMetric"] = r.Area * P.FeetToMeters * P.FeetToMeters,
                ["perimeter"] = r.Perimeter,
                ["department"] = r.LookupParameter("Department")?.AsString(),
            });
        }

        return new JsonObject { ["count"] = rooms.Count, ["rooms"] = arr };
    }
}
