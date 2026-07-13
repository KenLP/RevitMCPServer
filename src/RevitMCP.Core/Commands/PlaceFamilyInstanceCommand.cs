using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Place a FamilyInstance at a point.
///
/// Params:
///   - location:       { x, y, z? }
///   - familyName:     string, optional
///   - familyTypeName: string, optional
///   - category:       BuiltInCategory name, optional (helps narrow the search)
///   - levelName:      string, optional
///   - hostId:         long, optional — element id of host wall/face; when
///                     present uses the hosted overload so Revit auto-cuts
///                     the opening (required for doors/windows)
///   - flipFacing:     bool, optional — flip the door/window facing side
///   - flipHand:       bool, optional — flip the door/window hinge side
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
            throw new RevitCommandException("not_found",
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
        var flipFacing = P.BoolOr(p, "flipFacing", false);
        var flipHand   = P.BoolOr(p, "flipHand",   false);

        bool hasHost = p.ContainsKey("hostId") && p["hostId"] is not null;

        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

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

        FamilyInstance instance;
        long? returnedHostId = null;
        string? hostWarning = null;

        if (hasHost)
        {
            var rawHostId = P.Long(p, "hostId");
            var hostElem = doc.GetElement(new ElementId(rawHostId))
                ?? throw new RevitCommandException("not_found",
                    $"Host element id {rawHostId} not found in document.");

            if (hostElem is Wall)
            {
                // Hosted overload: Revit auto-cuts the opening in the wall.
                instance = doc.Create.NewFamilyInstance(pt, symbol, hostElem, level, stype);

                // Phase must match host or Revit raises an infill warning and the wall cut fails.
                var hostPhaseId = hostElem.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId();
                if (hostPhaseId != null && hostPhaseId != ElementId.InvalidElementId)
                    instance.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(hostPhaseId);

                if (flipFacing) instance.flipFacing();
                if (flipHand)   instance.flipHand();

                returnedHostId = hostElem.Id.Value;
            }
            else
            {
                // hostId supplied but not a wall: fall back to non-hosted instead of
                // letting the hosted overload throw. Warn so the caller knows the
                // instance is free-standing (Host = -1), not silently mis-hosted.
                instance = doc.Create.NewFamilyInstance(pt, symbol, level, stype);
                hostWarning = $"hostId {rawHostId} is a {hostElem.GetType().Name}, not a Wall — " +
                              "placed non-hosted (no opening cut). Only wall hosting is supported.";
            }
        }
        else
        {
            instance = doc.Create.NewFamilyInstance(pt, symbol, level, stype);
        }

        var result = new JsonObject
        {
            ["placed"] = true,
            ["id"] = instance.Id.Value,
            ["familyName"] = symbol.FamilyName,
            ["familyTypeName"] = symbol.Name,
            ["familyTypeId"] = symbol.Id.Value,
            ["levelName"] = level.Name,
        };

        if (returnedHostId.HasValue)
            result["hostId"] = returnedHostId.Value;

        if (hostWarning != null)
            result["hostWarning"] = hostWarning;

        if (usedFirstMatch)
            result["warning"] = $"Multiple types matched — used first result '{symbol.FamilyName} : {symbol.Name}'. " +
                                 "Provide both familyName and familyTypeName for deterministic placement.";

        return result;
    }
}
