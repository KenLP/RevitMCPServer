using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Renames any element.  Handles three distinct Revit rename paths:
///
///   1. <see cref="Family"/>       — direct property setter (<c>family.Name</c>).
///   2. <see cref="FamilySymbol"/> — direct property setter (<c>symbol.Name</c>).
///   3. Everything else            — <c>Element.Name</c> (virtual setter, dispatches
///      to either a property or a built-in parameter depending on element type).
///
/// For Families and FamilySymbols, the command validates:
///   • System families (<c>Family.IsEditable == false</c>) → error.
///   • Illegal characters in the new name → error.
///   • Duplicate name within the same category → error.
///
/// Returns <c>changeSummary</c> and <c>changes</c> for structured diffs.
/// </summary>
public sealed class RenameElementCommand : IRevitCommand
{
    public string Name => "rename_element";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    /// <summary>Characters Revit disallows in Family / FamilySymbol names.</summary>
    private static readonly char[] IllegalChars = @"\:{}[]|;<>?*~".ToCharArray();

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var id = new ElementId(P.Long(ctx.Parameters, "id"));
        var newName = P.Str(ctx.Parameters, "name");

        var element = doc.GetElement(id)
            ?? throw new RevitCommandException("not_found", $"Element {id.Value} not found.");

        // ── Family rename ───────────────────────────────────────────────
        if (element is Family family)
            return RenameFamily(family, newName, doc);

        // ── FamilySymbol (Type) rename ──────────────────────────────────
        if (element is FamilySymbol symbol)
            return RenameFamilySymbol(symbol, newName, doc);

        // ── Default path — Element.Name virtual setter ──────────────────
        return RenameGeneric(element, newName);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Family
    // ────────────────────────────────────────────────────────────────────

    private static JsonNode RenameFamily(Family family, string newName, Document doc)
    {
        if (!family.IsEditable)
            throw new RevitCommandException("system_family",
                $"Cannot rename system family '{family.Name}' (IsEditable=false). " +
                "System families (Basic Wall, Floor, etc.) have fixed names.");

        ValidateNameChars(newName);

        // Check for duplicate family name in the same category.
        var cat = family.FamilyCategory;
        if (cat is not null)
        {
            var duplicate = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Any(f => f.Id != family.Id
                       && f.FamilyCategory?.Id == cat.Id
                       && string.Equals(f.Name, newName, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
                throw new RevitCommandException("name_collision",
                    $"A family named '{newName}' already exists in category '{cat.Name}'.");
        }

        var oldName = family.Name;

        // Count affected instances before rename.
        var symbolIds = family.GetFamilySymbolIds();
        var instanceCount = 0;
        foreach (var symId in symbolIds)
        {
            instanceCount += new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WherePasses(new FamilyInstanceFilter(doc, symId))
                .GetElementCount();
        }

        family.Name = newName;

        return BuildResult(family.Id, "Family", oldName, family.Name, instanceCount);
    }

    // ────────────────────────────────────────────────────────────────────
    //  FamilySymbol (Type)
    // ────────────────────────────────────────────────────────────────────

    private static JsonNode RenameFamilySymbol(FamilySymbol symbol, string newName, Document doc)
    {
        ValidateNameChars(newName);

        // Check for duplicate type name within the same Family.
        var family = symbol.Family;
        var duplicate = family.GetFamilySymbolIds()
            .Select(doc.GetElement)
            .OfType<FamilySymbol>()
            .Any(s => s.Id != symbol.Id
                   && string.Equals(s.Name, newName, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
            throw new RevitCommandException("name_collision",
                $"A type named '{newName}' already exists in family '{family.Name}'.");

        var oldName = symbol.Name;

        // Count affected instances.
        var instanceCount = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .WherePasses(new FamilyInstanceFilter(doc, symbol.Id))
            .GetElementCount();

        symbol.Name = newName;

        return BuildResult(symbol.Id, "FamilySymbol", oldName, symbol.Name, instanceCount);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Generic element (existing path)
    // ────────────────────────────────────────────────────────────────────

    private static JsonNode RenameGeneric(Element element, string newName)
    {
        var oldName = element.Name;
        element.Name = newName;

        return new JsonObject
        {
            ["id"] = element.Id.Value,
            ["elementType"] = element.GetType().Name,
            ["oldName"] = oldName,
            ["newName"] = element.Name,
            ["changes"] = new JsonObject
            {
                ["before"] = oldName,
                ["after"] = element.Name,
            },
            ["changeSummary"] = $"Renamed element {element.Id.Value}: '{oldName}' → '{element.Name}'",
        };
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    private static void ValidateNameChars(string name)
    {
        foreach (var ch in IllegalChars)
        {
            if (name.Contains(ch))
                throw new RevitCommandException("invalid_chars",
                    $"Name contains illegal character '{ch}'. " +
                    @"Revit disallows: \ : { } [ ] | ; < > ? * ~");
        }
    }

    private static JsonObject BuildResult(
        ElementId id, string elementType, string oldName, string newName, int instanceCount)
    {
        return new JsonObject
        {
            ["id"] = id.Value,
            ["elementType"] = elementType,
            ["oldName"] = oldName,
            ["newName"] = newName,
            ["instancesAffected"] = instanceCount,
            ["changes"] = new JsonObject
            {
                ["before"] = oldName,
                ["after"] = newName,
            },
            ["changeSummary"] = $"Renamed {elementType} {id.Value}: '{oldName}' → '{newName}' ({instanceCount} instances affected)",
        };
    }
}
