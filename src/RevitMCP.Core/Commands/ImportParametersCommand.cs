using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Batch per-element parameter update for spreadsheet import.
/// Each item names one element + one parameter + one value.
/// All items execute in the single transaction opened by the dispatcher,
/// so the entire import is one Revit undo step.
///
/// Params:
///   items: required — array of { elementId: long, parameterName: string,
///                                value: string,   units?: string }
///
/// Returns: { applied: int, failed: int,
///            results: [{ elementId, ok, error? }] }
/// </summary>
public sealed class ImportParametersCommand : IRevitCommand
{
    public string Name => "import_parameters";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var items = ctx.Parameters["items"] as JsonArray
            ?? throw new RevitCommandException("bad_request",
                "Missing required parameter 'items' (array).");

        var applied = 0;
        var failed = 0;
        var results = new JsonArray();

        foreach (var item in items.OfType<JsonObject>())
        {
            var elementId = P.Long(item, "elementId");
            var paramName = P.StrOrNull(item, "parameterName")
                ?? throw new RevitCommandException("bad_request",
                    "Each item requires 'parameterName'.");
            var valueNode = item["value"]
                ?? throw new RevitCommandException("bad_request",
                    "Each item requires 'value'.");
            var units = P.StrOrNull(item, "units") ?? "internal";

            try
            {
                var el = doc.GetElement(new ElementId(elementId))
                    ?? throw new InvalidOperationException($"Element {elementId} not found.");

                var param = el.LookupParameter(paramName)
                    ?? throw new InvalidOperationException(
                        $"Param '{paramName}' not found on element {elementId}.");

                if (param.IsReadOnly)
                    throw new InvalidOperationException($"Param '{paramName}' is read-only.");

                SetValue(param, valueNode, units);
                applied++;
                results.Add(new JsonObject { ["elementId"] = elementId, ["ok"] = true });
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new JsonObject
                {
                    ["elementId"] = elementId,
                    ["ok"] = false,
                    ["error"] = ex.Message,
                });
            }
        }

        return new JsonObject
        {
            ["applied"] = applied,
            ["failed"] = failed,
            ["results"] = results,
        };
    }

    // Value always arrives as a string from CSV/Excel; we coerce to the
    // parameter's StorageType here.
    private static void SetValue(Parameter param, JsonNode valueNode, string units)
    {
        // Prefer the string representation that came from the spreadsheet cell.
        var raw = valueNode is JsonValue jv && jv.TryGetValue<string>(out var sv)
            ? sv
            : valueNode.ToString();

        switch (param.StorageType)
        {
            case StorageType.String:
                param.Set(raw);
                break;

            case StorageType.Integer:
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    throw new InvalidOperationException(
                        $"Cannot parse '{raw}' as integer for '{param.Definition.Name}'.");
                param.Set(i);
                break;

            case StorageType.Double:
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new InvalidOperationException(
                        $"Cannot parse '{raw}' as number for '{param.Definition.Name}'.");
                var converted = SetParameterCommand.ConvertToInternal(param, d, units, out _);
                param.Set(converted);
                break;

            case StorageType.ElementId:
                if (!long.TryParse(raw, out var eid))
                    throw new InvalidOperationException(
                        $"Cannot parse '{raw}' as ElementId for '{param.Definition.Name}'.");
                param.Set(new ElementId(eid));
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported StorageType '{param.StorageType}' for '{param.Definition.Name}'.");
        }
    }
}
