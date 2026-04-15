using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class ListSheetsCommand : IRevitCommand
{
    public string Name => "list_sheets";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .OrderBy(s => s.SheetNumber)
            .ToList();

        var arr = new JsonArray();
        foreach (var s in sheets)
        {
            arr.Add(new JsonObject
            {
                ["id"] = s.Id.Value,
                ["sheetNumber"] = s.SheetNumber,
                ["name"] = s.Name,
                ["titleBlockId"] = s.GetDependentElements(new ElementClassFilter(typeof(FamilyInstance)))
                    .FirstOrDefault()?.Value,
                ["viewportCount"] = s.GetAllViewports()?.Count ?? 0,
            });
        }

        return new JsonObject { ["count"] = sheets.Count, ["sheets"] = arr };
    }
}
