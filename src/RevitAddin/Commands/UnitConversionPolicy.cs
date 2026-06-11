using System;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Spec category for a Double parameter, decoupled from Revit's
/// <c>ForgeTypeId</c>/<c>SpecTypeId</c> so the unit-conversion *decision* can be
/// unit-tested without a live Revit process.  The mapping from the real
/// <c>ForgeTypeId</c> to this enum lives in <see cref="SetParameterCommand"/>
/// (it requires RevitAPI), but the branching logic — which is where the v0.6.0
/// "silent wrong conversion" bug lived — is pure and fully testable here.
/// </summary>
public enum UnitSpec
{
    /// <summary>Ratio, slope, number, etc. — units are ignored, never converted.</summary>
    Dimensionless,
    Length,
    Area,
    Volume,
    /// <summary>Angle, force, etc. — must use <c>units:"internal"</c>.</summary>
    OtherMeasurable,
}

/// <summary>
/// The concrete unit the caller's value is expressed in, resolved from the
/// spec category + the <c>units</c> string.  <see cref="NoConversion"/> means
/// the value is already in Revit internal units and must be written as-is.
/// </summary>
public enum UnitChoice
{
    NoConversion,
    Meters,
    Feet,
    SquareMeters,
    SquareFeet,
    CubicMeters,
    CubicFeet,
}

/// <summary>
/// Pure decision: given a parameter's spec category and the requested
/// <c>units</c> string, decide which conversion to apply (or fail closed).
/// No Revit types — safe to unit-test.
/// </summary>
public static class UnitConversionPolicy
{
    public static UnitChoice Decide(UnitSpec spec, string units, string? paramName = null)
    {
        // "internal" always wins, for every spec — write the raw value unchanged.
        if (string.Equals(units, "internal", StringComparison.OrdinalIgnoreCase))
            return UnitChoice.NoConversion;

        // Dimensionless specs (ratio, slope, %) ignore units entirely.
        if (spec == UnitSpec.Dimensionless)
            return UnitChoice.NoConversion;

        var label = paramName is null ? "Parameter" : $"Parameter '{paramName}'";

        switch (spec)
        {
            case UnitSpec.Length:
                if (Eq(units, "meters")) return UnitChoice.Meters;
                if (Eq(units, "feet"))   return UnitChoice.Feet;
                break;
            case UnitSpec.Area:
                if (Eq(units, "square_meters")) return UnitChoice.SquareMeters;
                if (Eq(units, "square_feet"))   return UnitChoice.SquareFeet;
                break;
            case UnitSpec.Volume:
                if (Eq(units, "cubic_meters")) return UnitChoice.CubicMeters;
                if (Eq(units, "cubic_feet"))   return UnitChoice.CubicFeet;
                break;
            case UnitSpec.OtherMeasurable:
                // Fail closed: no auto-conversion table for angle/force/etc.
                throw new RevitCommandException("invalid_parameter",
                    $"{label} has a measurable spec that is not supported for automatic " +
                    "unit conversion (only length, area, and volume are). Use units:\"internal\" " +
                    "to write the raw Revit internal value.");
        }

        // Spec is length/area/volume but the units string doesn't match its family.
        throw new RevitCommandException("invalid_parameter",
            $"{label} has spec '{spec}' but units '{units}' is not compatible. " +
            "Use 'meters'/'feet' for length, 'square_meters'/'square_feet' for area, " +
            "'cubic_meters'/'cubic_feet' for volume, or 'internal'.");
    }

    private static bool Eq(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
