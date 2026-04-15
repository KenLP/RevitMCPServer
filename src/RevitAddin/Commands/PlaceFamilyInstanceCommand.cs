using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Place a FamilyInstance at a point (non-hosted).
///
/// Params:
///   - location:       { x, y, z? }
///   - familyName:     string, optional
///   - familyTypeName: string, optional
///   - category:       BuiltInCategory name, optional (helps narrow the search)
///   - levelName:      string, optional
///   - structural:     bool, default false
///   - units:          "meters"|"feet"
/// </summary>
public sealed class PlaceFamilyInstanceCommand : IRevitCommand
{
    public string Name => "place_family_instance";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var location = P.Xyz(p, "location", units);
        var level = CreateWallCommand.ResolveLevel(doc, P.StrOrNull(p, "levelName"));
        var structural = P.BoolOr(p, "structural", false);

        var familyName = P.StrOrNull(p, "familyName");
        var typeName = P.StrOrNull(p, "familyTypeName");
        var catName = P.StrOrNull(p, "category");

        var query = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>();

        if (!string.IsNullOrWhiteSpace(catName)
            && Enum.TryParse<BuiltInCategory>(catName, true, out var bic))
            query = query.Where(s => s.Category?.BuiltInCategory == bic);

        if (!string.IsNullOrWhiteSpace(familyName))
            query = query.Where(s => s.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(typeName))
            query = query.Where(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

        var symbol = query.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No FamilySymbol found (family='{familyName}', type='{typeName}', category='{catName}').");

        if (!symbol.IsActive) symbol.Activate();

        var stype = structural ? StructuralType.NonStructural : StructuralType.NonStructural;
        if (structural)
        {
            // Determine structural type from category.
            var bc = symbol.Category?.BuiltInCategory;
            stype = bc switch
            {
                BuiltInCategory.OST_StructuralColumns => StructuralType.Column,
                BuiltInCategory.OST_StructuralFraming => StructuralType.Beam,
                BuiltInCategory.OST_StructuralFoundation => StructuralType.Footing,
                _ => StructuralType.NonStructural,
            };
        }

        var pt = new XYZ(location.X, location.Y, level.Elevation);
        var instance = doc.Create.NewFamilyInstance(pt, symbol, level, stype);

        return new JsonObject
        {
            ["id"] = instance.Id.Value,
            ["familyName"] = symbol.FamilyName,
            ["familyTypeName"] = symbol.Name,
            ["levelName"] = level.Name,
        };
    }
}
