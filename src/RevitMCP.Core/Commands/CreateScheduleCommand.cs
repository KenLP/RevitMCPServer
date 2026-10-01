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
///   - name:       string, optional. Applied exactly or the command fails: a name Revit refuses
///                 (forbidden character -> invalid_chars 400, existing schedule -> name_collision 409)
///                 rolls the whole command back, so no schedule is left under a placeholder name.
///   - fields:     string[] of parameter names to add as columns, optional
///                 (if omitted, no fields are added — use Revit UI to configure). Names that match
///                 no schedulable field are reported in <c>skippedFields</c>, not silently dropped.
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
            throw new RevitCommandException("invalid_parameter", $"Unknown BuiltInCategory '{catName}'.");

        var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(bic));

        // Never `try { schedule.Name = name; } catch { }`: that kept Revit's placeholder
        // ("Door Schedule 3") and still reported ok, so a caller's naming convention silently
        // never landed and it could not find its own schedule again by name.
        var name = P.StrOrNull(p, "name");
        if (!string.IsNullOrWhiteSpace(name))
            ViewNameRules.Apply(schedule, name!);

        var fieldsArr = p["fields"] as JsonArray;
        var addedFields = new JsonArray();
        var skippedFields = new JsonArray();
        if (fieldsArr is { Count: > 0 })
        {
            var def = schedule.Definition;
            var schedulableFields = def.GetSchedulableFields();

            foreach (var fn in fieldsArr)
            {
                var fieldName = fn is null ? null : P.StrFrom(fn, "fields[]");
                if (fieldName is null) continue;

                var sf = schedulableFields.FirstOrDefault(
                    f => f.GetName(doc).Equals(fieldName, StringComparison.OrdinalIgnoreCase));

                if (sf is not null)
                {
                    def.AddField(sf);
                    addedFields.Add(fieldName);
                }
                else
                {
                    skippedFields.Add(fieldName);
                }
            }
        }

        return new JsonObject
        {
            ["id"] = schedule.Id.Value,
            ["name"] = schedule.Name,
            ["category"] = catName,
            ["addedFields"] = addedFields,
            ["skippedFields"] = skippedFields,
        };
    }
}
