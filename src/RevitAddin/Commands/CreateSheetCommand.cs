using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a ViewSheet.
///
/// Params:
///   - sheetNumber: string, optional (Revit auto-assigns if omitted)
///   - sheetName:   string, optional
///   - titleBlockName: string, optional (defaults to first loaded title block family type)
/// </summary>
public sealed class CreateSheetCommand : IRevitCommand
{
    public string Name => "create_sheet";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var tbName = P.StrOrNull(p, "titleBlockName");
        var tbSymbol = ResolveTitleBlock(doc, tbName);

        var sheet = ViewSheet.Create(doc, tbSymbol?.Id ?? ElementId.InvalidElementId);

        var number = P.StrOrNull(p, "sheetNumber");
        if (!string.IsNullOrWhiteSpace(number))
        {
            try { sheet.SheetNumber = number; } catch { }
        }

        var name = P.StrOrNull(p, "sheetName");
        if (!string.IsNullOrWhiteSpace(name))
            sheet.Name = name;

        return new JsonObject
        {
            ["id"] = sheet.Id.Value,
            ["sheetNumber"] = sheet.SheetNumber,
            ["name"] = sheet.Name,
        };
    }

    private static FamilySymbol? ResolveTitleBlock(Document doc, string? name)
    {
        var query = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>();

        if (!string.IsNullOrWhiteSpace(name))
            return query.FirstOrDefault(s => s.Name == name || s.FamilyName == name);

        return query.FirstOrDefault();
    }
}
