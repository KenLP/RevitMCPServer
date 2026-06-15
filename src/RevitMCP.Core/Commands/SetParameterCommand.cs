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
            ?? throw new RevitCommandException("bad_request", "Missing required parameter 'value'.");
        var units = P.StrOrNull(p, "units") ?? "internal";

        var element = doc.GetElement(new ElementId(idValue))
            ?? throw new RevitCommandException("not_found", $"No element with id {idValue}.");

        var param = element.LookupParameter(paramName)
            ?? throw new RevitCommandException("not_found",
                $"Element {idValue} has no parameter named '{paramName}'.");

        if (param.IsReadOnly)
            throw new RevitCommandException("read_only_parameter", $"Parameter '{paramName}' is read-only.");

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
                throw new RevitCommandException("invalid_parameter",
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
    /// Convert a user-supplied double to Revit internal units based on the
    /// parameter's spec type.  The branching decision (which unit, or fail
    /// closed) lives in the pure, unit-tested <see cref="UnitConversionPolicy"/>;
    /// this method only does the Revit-coupled work of classifying the
    /// <c>ForgeTypeId</c> and calling <c>UnitUtils.ConvertToInternalUnits</c>.
    /// </summary>
    internal static double ConvertToInternal(Parameter param, double value, string units,
        out bool conversionApplied)
    {
        conversionApplied = false;

        var spec = ClassifySpec(param.Definition.GetDataType());
        var choice = UnitConversionPolicy.Decide(spec, units, param.Definition.Name);
        if (choice == UnitChoice.NoConversion) return value;

        conversionApplied = true;
        return UnitUtils.ConvertToInternalUnits(value, ToUnitTypeId(choice));
    }

    /// <summary>Maps a Revit <c>ForgeTypeId</c> spec to the testable <see cref="UnitSpec"/>.</summary>
    private static UnitSpec ClassifySpec(ForgeTypeId specType)
    {
        if (!UnitUtils.IsMeasurableSpec(specType)) return UnitSpec.Dimensionless;
        if (specType == SpecTypeId.Length) return UnitSpec.Length;
        if (specType == SpecTypeId.Area)   return UnitSpec.Area;
        if (specType == SpecTypeId.Volume) return UnitSpec.Volume;
        return UnitSpec.OtherMeasurable;
    }

    private static ForgeTypeId ToUnitTypeId(UnitChoice choice) => choice switch
    {
        UnitChoice.Meters       => UnitTypeId.Meters,
        UnitChoice.Feet         => UnitTypeId.Feet,
        UnitChoice.SquareMeters => UnitTypeId.SquareMeters,
        UnitChoice.SquareFeet   => UnitTypeId.SquareFeet,
        UnitChoice.CubicMeters  => UnitTypeId.CubicMeters,
        UnitChoice.CubicFeet    => UnitTypeId.CubicFeet,
        _ => throw new RevitCommandException("invalid_parameter",
            $"Internal error: unmapped UnitChoice '{choice}'."),
    };

    private static string? SafeAsValueString(Parameter p)
    {
        try { return p.AsValueString(); } catch { return null; }
    }
}
