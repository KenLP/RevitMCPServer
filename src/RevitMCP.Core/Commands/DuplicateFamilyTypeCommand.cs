using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Duplicate a family type (FamilySymbol) under a new name.  Set parameters on the
/// new type afterwards with set_parameter (using the returned typeId), which already
/// handles unit conversion.
///
/// Params:
///   - sourceTypeId: long, required — ElementId of the FamilySymbol to copy.
///   - newName:      string, required — name for the new type (must be unique in the family).
///
/// Returns: { typeId, name, familyName }.
/// </summary>
public sealed class DuplicateFamilyTypeCommand : IRevitCommand
{
    public string Name => "duplicate_family_type";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var srcId = new ElementId(P.Long(p, "sourceTypeId"));
        var src = doc.GetElement(srcId) as FamilySymbol
            ?? throw new RevitCommandException("not_found",
                $"Element {srcId.Value} is not a family type (FamilySymbol).");

        var newName = P.Str(p, "newName");

        var dup = src.Duplicate(newName) as FamilySymbol
            ?? throw new RevitCommandException("command_failed",
                "Duplicate did not return a FamilySymbol.");

        return new JsonObject
        {
            ["typeId"] = dup.Id.Value,
            ["name"] = dup.Name,
            ["familyName"] = dup.FamilyName,
        };
    }
}
