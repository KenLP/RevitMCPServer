using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;

namespace RevitMCPAddin.Commands;

/// <summary>
/// List MEP Spaces (OST_MEPSpaces) in the host document.
///
/// Params:
///   - levelId: long, optional — filter to a specific level (Element id from list_levels).
///   - limit:   int, optional, default 500.
/// </summary>
public sealed class ListSpacesCommand : IRevitCommand
{
    public string Name => "list_spaces";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p   = ctx.Parameters;

        long? filterLevelId = null;
        if (p["levelId"] is JsonNode lvNode)
            filterLevelId = lvNode.GetValue<long>();

        var limit = Math.Clamp(P.IntOr(p, "limit", 500), 1, 2000);

        var spaces = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_MEPSpaces)
            .WhereElementIsNotElementType()
            .Cast<Space>()
            .Where(s => s.Area > 0)
            .Where(s => filterLevelId == null || s.LevelId?.Value == filterLevelId)
            .OrderBy(s => s.LevelId?.Value)
            .ThenBy(s => s.Number)
            .Take(limit)
            .ToList();

        var arr = new JsonArray();
        foreach (var s in spaces)
        {
            arr.Add(new JsonObject
            {
                ["id"]          = s.Id.Value,
                ["name"]        = s.Name,
                ["number"]      = s.Number,
                ["levelId"]     = s.LevelId?.Value,
                ["levelName"]   = s.Level?.Name,
                ["area"]        = s.Area,
                ["areaM2"]      = Math.Round(s.Area * P.FeetToMeters * P.FeetToMeters, 3),
                ["volume"]      = s.Volume,
                ["volumeM3"]    = Math.Round(s.Volume * P.FeetToMeters * P.FeetToMeters * P.FeetToMeters, 3),
                ["spaceType"]   = s.SpaceType.ToString(),
            });
        }

        return new JsonObject
        {
            ["count"]        = spaces.Count,
            ["truncated"]    = spaces.Count >= limit,
            ["filterLevelId"]= filterLevelId,
            ["spaces"]       = arr,
        };
    }
}
