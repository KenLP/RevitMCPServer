using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Returns the distinct set of categories *actually used* by element instances
/// in the active document.  Cheaper and more useful for the AI than dumping
/// every BuiltInCategory enum value (there are thousands).
/// </summary>
public sealed class ListCategoriesCommand : IRevitCommand
{
    public string Name => "list_categories";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var seen = new Dictionary<long, (string Name, string Enum, int Count)>();

        foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            var cat = el.Category;
            if (cat is null) continue;
            var idValue = cat.Id.Value;
            if (seen.TryGetValue(idValue, out var existing))
                seen[idValue] = (existing.Name, existing.Enum, existing.Count + 1);
            else
                seen[idValue] = (cat.Name, cat.BuiltInCategory.ToString(), 1);
        }

        var arr = new JsonArray();
        foreach (var kv in seen.OrderByDescending(k => k.Value.Count))
        {
            arr.Add(new JsonObject
            {
                ["id"] = kv.Key,
                ["name"] = kv.Value.Name,
                ["builtInCategory"] = kv.Value.Enum,
                ["instanceCount"] = kv.Value.Count,
            });
        }

        return new JsonObject
        {
            ["count"] = arr.Count,
            ["categories"] = arr,
        };
    }
}
