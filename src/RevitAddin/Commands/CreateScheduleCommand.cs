using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a ViewSchedule for a given category with specified fields.
///
/// Params:
///   - category:   BuiltInCategory name, required (e.g. "OST_Walls")
///   - name:       string, optional
///   - fields:     string[] of parameter names to add as columns, optional
///                 (if omitted, no fields are added — use Revit UI to configure)
/// </summary>
public sealed class CreateScheduleCommand : IRevitCommand
{
    public string Name => "create_schedule";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var catName = P.Str(p, "category");
        if (!Enum.TryParse<BuiltInCategory>(catName, true, out var bic))
            throw new ArgumentException($"Unknown BuiltInCategory '{catName}'.");

        var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(bic));

        var name = P.StrOrNull(p, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { schedule.Name = name; } catch { }
        }

        var fieldsArr = p["fields"] as JsonArray;
        var addedFields = new JsonArray();
        if (fieldsArr is { Count: > 0 })
        {
            var def = schedule.Definition;
            var schedulableFields = def.GetSchedulableFields();

            foreach (var fn in fieldsArr)
            {
                var fieldName = fn?.GetValue<string>();
                if (fieldName is null) continue;

                var sf = schedulableFields.FirstOrDefault(
                    f => f.GetName(doc).Equals(fieldName, StringComparison.OrdinalIgnoreCase));

                if (sf is not null)
                {
                    def.AddField(sf);
                    addedFields.Add(fieldName);
                }
            }
        }

        return new JsonObject
        {
            ["id"] = schedule.Id.Value,
            ["name"] = schedule.Name,
            ["category"] = catName,
            ["addedFields"] = addedFields,
        };
    }
}
