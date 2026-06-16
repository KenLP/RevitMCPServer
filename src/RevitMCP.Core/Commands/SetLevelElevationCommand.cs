using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Set the elevation of a Level element.
///
/// Params:
///   - id:        long, required — ElementId of the Level.
///   - elevation: double, required — new elevation value.
///   - units:     "meters"|"feet"|"mm"|"internal", optional. Default "meters".
///                "internal" = Revit internal units (feet).
/// </summary>
public sealed class SetLevelElevationCommand : IRevitCommand
{
    public string Name => "set_level_elevation";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var idValue = P.Long(p, "id");
        var level = doc.GetElement(new ElementId(idValue)) as Level
            ?? throw new RevitCommandException("not_found",
                $"No level with id {idValue}. Use revit_list_levels to find level ids.");

        var elevation = P.Dbl(p, "elevation");
        var units     = (P.StrOrNull(p, "units") ?? "meters").ToLowerInvariant();

        var elevationFt = units switch
        {
            "meters"   => elevation * P.MetersToFeet,
            "feet"     => elevation,
            "mm"       => elevation / 1000.0 * P.MetersToFeet,
            "internal" => elevation,
            _ => throw new RevitCommandException("invalid_parameter",
                $"Unknown units '{units}'. Use 'meters', 'feet', 'mm', or 'internal'.")
        };

        var oldFt = level.Elevation;
        var oldM  = oldFt  * P.FeetToMeters;
        var newM  = elevationFt * P.FeetToMeters;

        level.Elevation = elevationFt;

        return new JsonObject
        {
            ["id"]             = idValue,
            ["name"]           = level.Name,
            ["oldElevationM"]  = Math.Round(oldM,  4),
            ["newElevationM"]  = Math.Round(newM,  4),
            ["oldElevationFt"] = Math.Round(oldFt, 6),
            ["newElevationFt"] = Math.Round(elevationFt, 6),
            ["changeSummary"]  = $"Level '{level.Name}' elevation: {oldM:F3} m → {newM:F3} m",
        };
    }
}
