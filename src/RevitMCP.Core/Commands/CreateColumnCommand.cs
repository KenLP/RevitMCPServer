using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Place a structural column at a point.
///
/// Params:
///   - location:       { x, y, z? }
///   - levelName:      string, optional
///   - familyTypeName: string, optional (e.g. "W10X49")
///   - familyName:     string, optional
///   - structural:     bool, default true
///   - units:          "meters"|"feet"
/// </summary>
public sealed class CreateColumnCommand : IRevitCommand
{
    public string Name => "create_column";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var location = P.Xyz(p, "location", units);
        var level = CreateWallCommand.ResolveLevel(doc, P.StrOrNull(p, "levelName"));
        var structural = P.BoolOr(p, "structural", true);

        var symbol = ResolveFamilySymbol(doc, P.StrOrNull(p, "familyName"),
            P.StrOrNull(p, "familyTypeName"), BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns);

        if (!symbol.IsActive) symbol.Activate();

        var pt = new XYZ(location.X, location.Y, level.Elevation);
        var stype = structural ? StructuralType.Column : StructuralType.NonStructural;
        var instance = doc.Create.NewFamilyInstance(pt, symbol, level, stype);

        return new JsonObject
        {
            ["id"] = instance.Id.Value,
            ["familyTypeName"] = symbol.Name,
            ["familyName"] = symbol.FamilyName,
            ["levelName"] = level.Name,
        };
    }

    internal static FamilySymbol ResolveFamilySymbol(
        Document doc, string? familyName, string? typeName,
        params BuiltInCategory[] categories)
    {
        var query = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>();

        foreach (var cat in categories)
            query = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(cat)
                .Cast<FamilySymbol>();

        if (!string.IsNullOrWhiteSpace(familyName))
            query = query.Where(s => s.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(typeName))
            query = query.Where(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

        return query.FirstOrDefault()
            ?? throw new RevitCommandException("not_found",
                $"No FamilySymbol found for family='{familyName}', type='{typeName}'.");
    }
}
