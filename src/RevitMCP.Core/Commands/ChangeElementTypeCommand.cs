using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Swap the type of a single element.
///
/// Params:
///   - id:     long, required — ElementId of the element to change.
///   - typeId: long, required — ElementId of the target type (WallType, FloorType, FamilySymbol, …).
/// </summary>
public sealed class ChangeElementTypeCommand : IRevitCommand
{
    public string Name => "change_element_type";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var idValue    = P.Long(p, "id");
        var typeIdValue = P.Long(p, "typeId");

        var elemId  = new ElementId(idValue);
        var element = doc.GetElement(elemId)
            ?? throw new RevitCommandException("not_found", $"No element with id {idValue}.");

        var newTypeId = new ElementId(typeIdValue);
        var newType   = doc.GetElement(newTypeId)
            ?? throw new RevitCommandException("not_found", $"No element type with id {typeIdValue}.");

        var oldTypeId   = element.GetTypeId();
        var oldTypeName = doc.GetElement(oldTypeId)?.Name;

        var validTypes = element.GetValidTypes();
        if (!validTypes.Contains(newTypeId))
            throw new RevitCommandException("wrong_element_type",
                $"Type {typeIdValue} ('{newType.Name}') is not compatible with element {idValue}. " +
                "Use revit_list_family_types / revit_list_wall_types to find valid types.");

        Element.ChangeTypeId(doc, new List<ElementId> { elemId }, newTypeId);

        return new JsonObject
        {
            ["id"]          = idValue,
            ["oldTypeId"]   = oldTypeId.Value,
            ["oldTypeName"] = oldTypeName,
            ["newTypeId"]   = typeIdValue,
            ["newTypeName"] = newType.Name,
            ["changeSummary"] = $"Changed element {idValue} type: '{oldTypeName}' → '{newType.Name}'",
        };
    }
}
