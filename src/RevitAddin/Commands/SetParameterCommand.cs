using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Set a single parameter on a single element by name.
///
/// Parameters:
///   - id:             long, required
///   - parameterName:  string, required
///   - value:          any, required (string / number / bool / { id: long })
///   - units:          "meters"|"feet"|"internal", optional, default "internal".
///                     Applies unit conversion for Double storage-type parameters
///                     that carry measurable units (length, area, volume, etc.).
///                     "internal" = raw Revit internal units (feet for length).
///                     Pass "meters" or "feet" for automatic conversion via
///                     UnitUtils.ConvertToInternalUnits.  Dimensionless doubles
///                     (ratio, percentage, etc.) are never converted regardless
///                     of this field.
/// </summary>
public sealed class SetParameterCommand : IRevitCommand
{
    public string Name => "set_parameter";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var idValue = P.Long(p, "id");
        var paramName = P.Str(p, "parameterName");
        var valueNode = p["value"]
            ?? throw new ArgumentException("Missing required parameter 'value'.");
        var units = P.StrOrNull(p, "units") ?? "internal";

        var element = doc.GetElement(new ElementId(idValue))
            ?? throw new InvalidOperationException($"No element with id {idValue}.");

        var param = element.LookupParameter(paramName)
            ?? throw new InvalidOperationException(
                $"Element {idValue} has no parameter named '{paramName}'.");

        if (param.IsReadOnly)
            throw new InvalidOperationException($"Parameter '{paramName}' is read-only.");

        var previousValueString = SafeAsValueString(param);

        bool wrote;
        bool unitConversionApplied = false;

        switch (param.StorageType)
        {
            case StorageType.String:
                wrote = param.Set(valueNode.GetValue<string>());
                break;

            case StorageType.Integer:
                wrote = valueNode.GetValueKind() is System.Text.Json.JsonValueKind.True
                                                  or System.Text.Json.JsonValueKind.False
                    ? param.Set(valueNode.GetValue<bool>() ? 1 : 0)
                    : param.Set(valueNode.GetValue<int>());
                break;

            case StorageType.Double:
            {
                var raw = valueNode.GetValue<double>();
                var converted = ConvertToInternal(param, raw, units, out unitConversionApplied);
                wrote = param.Set(converted);
                break;
            }

            case StorageType.ElementId:
            {
                long target;
                if (valueNode is JsonObject obj && obj["id"] is JsonNode idn)
                    target = idn.GetValue<long>();
                else
                    target = valueNode.GetValue<long>();
                wrote = param.Set(new ElementId(target));
                break;
            }

            default:
                throw new InvalidOperationException(
                    $"Unsupported StorageType '{param.StorageType}' for '{paramName}'.");
        }

        var newValueString = SafeAsValueString(param);

        var result = new JsonObject
        {
            ["id"] = element.Id.Value,
            ["parameterName"] = paramName,
            ["storageType"] = param.StorageType.ToString(),
            ["written"] = wrote,
            ["newValueString"] = newValueString,
            ["changes"] = new JsonObject
            {
                ["before"] = previousValueString,
                ["after"] = newValueString,
            },
            ["changeSummary"] = $"Set '{paramName}' on element {idValue}: '{previousValueString}' → '{newValueString}'",
        };

        if (param.StorageType == StorageType.Double)
        {
            result["inputUnits"] = units;
            result["unitConversionApplied"] = unitConversionApplied;
        }

        return result;
    }

    /// <summary>
    /// Convert a user-supplied double to Revit internal units when the parameter
    /// carries measurable units (e.g. length, area, volume).  Dimensionless specs
    /// (ratio, slope, etc.) are never converted.
    /// </summary>
    internal static double ConvertToInternal(Parameter param, double value, string units,
        out bool conversionApplied)
    {
        conversionApplied = false;
        if (units == "internal") return value;

        var specType = param.Definition.GetDataType();
        if (!UnitUtils.IsMeasurableSpec(specType)) return value;

        var inputUnit = units.Equals("feet", StringComparison.OrdinalIgnoreCase)
            ? UnitTypeId.Feet
            : UnitTypeId.Meters;

        conversionApplied = true;
        return UnitUtils.ConvertToInternalUnits(value, inputUnit);
    }

    private static string? SafeAsValueString(Parameter p)
    {
        try { return p.AsValueString(); } catch { return null; }
    }
}
