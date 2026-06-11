using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>
/// Pure tests for the unit-conversion decision — no Revit required.  This is
/// the branching logic that produced wrong conversions before v0.6.0.
/// </summary>
public class UnitConversionPolicyTests
{
    [Theory]
    [InlineData(UnitSpec.Length, "meters",        UnitChoice.Meters)]
    [InlineData(UnitSpec.Length, "feet",          UnitChoice.Feet)]
    [InlineData(UnitSpec.Length, "FEET",          UnitChoice.Feet)]   // case-insensitive
    [InlineData(UnitSpec.Area,   "square_meters", UnitChoice.SquareMeters)]
    [InlineData(UnitSpec.Area,   "square_feet",   UnitChoice.SquareFeet)]
    [InlineData(UnitSpec.Volume, "cubic_meters",  UnitChoice.CubicMeters)]
    [InlineData(UnitSpec.Volume, "cubic_feet",    UnitChoice.CubicFeet)]
    public void Decide_returns_matching_choice(UnitSpec spec, string units, UnitChoice expected)
        => Assert.Equal(expected, UnitConversionPolicy.Decide(spec, units));

    [Theory]
    [InlineData(UnitSpec.Length,          "internal")]
    [InlineData(UnitSpec.Area,            "internal")]
    [InlineData(UnitSpec.Volume,          "internal")]
    [InlineData(UnitSpec.OtherMeasurable, "internal")]  // internal allowed for any spec
    [InlineData(UnitSpec.Dimensionless,   "meters")]    // dimensionless ignores units
    [InlineData(UnitSpec.Dimensionless,   "feet")]
    [InlineData(UnitSpec.Dimensionless,   "internal")]
    public void Decide_returns_NoConversion(UnitSpec spec, string units)
        => Assert.Equal(UnitChoice.NoConversion, UnitConversionPolicy.Decide(spec, units));

    [Theory]
    [InlineData(UnitSpec.Length, "square_meters")]  // right value, wrong spec family
    [InlineData(UnitSpec.Length, "cubic_feet")]
    [InlineData(UnitSpec.Area,   "meters")]
    [InlineData(UnitSpec.Area,   "cubic_meters")]
    [InlineData(UnitSpec.Volume, "feet")]
    [InlineData(UnitSpec.Volume, "square_meters")]
    [InlineData(UnitSpec.Length, "bogus")]
    public void Decide_incompatible_units_throw_invalid_parameter(UnitSpec spec, string units)
    {
        var ex = Assert.Throws<RevitCommandException>(() => UnitConversionPolicy.Decide(spec, units));
        Assert.Equal("invalid_parameter", ex.Code);
    }

    [Theory]
    [InlineData("meters")]
    [InlineData("feet")]
    [InlineData("square_meters")]
    public void Decide_unsupported_measurable_spec_fails_closed(string units)
    {
        var ex = Assert.Throws<RevitCommandException>(
            () => UnitConversionPolicy.Decide(UnitSpec.OtherMeasurable, units));
        Assert.Equal("invalid_parameter", ex.Code);
    }

    [Fact]
    public void Decide_includes_param_name_in_error()
    {
        var ex = Assert.Throws<RevitCommandException>(
            () => UnitConversionPolicy.Decide(UnitSpec.Area, "meters", "Sill Height"));
        Assert.Contains("Sill Height", ex.Message);
    }
}
