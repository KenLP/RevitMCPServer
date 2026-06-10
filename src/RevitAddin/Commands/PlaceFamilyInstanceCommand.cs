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
///
/// When neither familyName nor familyTypeName is supplied (or the query still
/// returns multiple symbols), the command returns a candidate list instead of
/// picking arbitrarily.  Specify both fields for unambiguous placement.
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

        // Cap at 11 to distinguish "many" from "exactly 10".
        var matches = query.Take(11).ToList();

        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"No FamilySymbol found (family='{familyName ?? "*"}', " +
                $"type='{typeName ?? "*"}', category='{catName ?? "*"}').");

        // When the query is still ambiguous (no explicit type selected), return
        // candidates so the caller can make an informed choice.
        bool noExplicitFamily = string.IsNullOrWhiteSpace(familyName);
        bool noExplicitType   = string.IsNullOrWhiteSpace(typeName);

        if (matches.Count > 1 && noExplicitFamily && noExplicitType)
        {
            var shown = matches.Take(10).ToList();
            return new JsonObject
            {
                ["placed"] = false,
                ["action"] = "select_type",
                ["candidatesFound"] = matches.Count > 10 ? "10+" : matches.Count.ToString(),
                ["candidates"] = new JsonArray(shown.Select(s => (JsonNode?)new JsonObject
                {
                    ["familyName"] = s.FamilyName,
                    ["typeName"] = s.Name,
                    ["category"] = s.Category?.Name,
                    ["id"] = s.Id.Value,
                }).ToArray()),
                ["hint"] = "Multiple family types match. Specify familyName and familyTypeName to place an instance.",
            };
        }

        var symbol = matches[0];
        bool usedFirstMatch = matches.Count > 1; // partial filter → first wins, but warn

        var location = P.Xyz(p, "location", units);
        var level = CreateWallCommand.ResolveLevel(doc, P.StrOrNull(p, "levelName"));
        var structural = P.BoolOr(p, "structural", false);

        if (!symbol.IsActive) symbol.Activate();

        var stype = StructuralType.NonStructural;
        if (structural)
        {
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

        var result = new JsonObject
        {
            ["placed"] = true,
            ["id"] = instance.Id.Value,
            ["familyName"] = symbol.FamilyName,
            ["familyTypeName"] = symbol.Name,
            ["familyTypeId"] = symbol.Id.Value,
            ["levelName"] = level.Name,
        };

        if (usedFirstMatch)
            result["warning"] = $"Multiple types matched — used first result '{symbol.FamilyName} : {symbol.Name}'. " +
                                 "Provide both familyName and familyTypeName for deterministic placement.";

        return result;
    }
}
