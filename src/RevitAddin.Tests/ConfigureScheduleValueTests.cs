using System.Globalization;
using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>
/// Pure tests for how configure_schedule reads a filter value off the wire.
/// No Revit runtime needed — the ScheduleFilter ctor ladder itself can only be exercised live.
/// </summary>
public class ConfigureScheduleValueTests
{
    private static JsonNode? Parse(string json) => JsonNode.Parse(json);

    [Fact]
    public void Json_number_is_read_without_throwing()
    {
        // The old code called GetValue<string>() here and threw InvalidOperationException,
        // outside the per-filter try — failing the whole command.
        var (wasNumber, text, isNumeric, number) = ConfigureScheduleCommand.ReadFilterValue(
            Parse("2.9527559055118114"));

        Assert.True(wasNumber);
        Assert.True(isNumeric);
        Assert.Equal(2.9527559055118114, number, 12);
        Assert.Equal("2.9527559055118114", text);
    }

    [Fact]
    public void Numeric_string_parses_to_the_same_number()
    {
        var (wasNumber, text, isNumeric, number) = ConfigureScheduleCommand.ReadFilterValue(
            Parse("\"2.9527559055118114\""));

        Assert.False(wasNumber);
        Assert.True(isNumeric);
        Assert.Equal(2.9527559055118114, number, 12);
        Assert.Equal("2.9527559055118114", text);
    }

    [Fact]
    public void Text_value_is_unquoted_and_not_numeric()
    {
        var (wasNumber, text, isNumeric, _) = ConfigureScheduleCommand.ReadFilterValue(
            Parse("\"S10\""));

        Assert.False(wasNumber);
        Assert.False(isNumeric);
        Assert.Equal("S10", text);   // no surrounding quotes — the string ctor gets S10, not "S10"
    }

    [Fact]
    public void Missing_value_is_empty_and_not_numeric()
    {
        var (wasNumber, text, isNumeric, number) = ConfigureScheduleCommand.ReadFilterValue(null);

        Assert.False(wasNumber);
        Assert.False(isNumeric);
        Assert.Equal("", text);
        Assert.Equal(0d, number);
    }

    [Fact]
    public void Integer_json_number_keeps_its_literal_text()
    {
        var (wasNumber, text, _, number) = ConfigureScheduleCommand.ReadFilterValue(Parse("90"));

        Assert.True(wasNumber);
        Assert.Equal(90d, number);
        Assert.Equal("90", text);
    }

    [Theory]
    [InlineData("de-DE")]   // decimal comma: current-culture parsing reads 2.95… as 2.95e16
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void Numeric_string_parses_the_same_under_any_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var (_, _, isNumeric, number) = ConfigureScheduleCommand.ReadFilterValue(
                Parse("\"2.9527559055118114\""));

            Assert.True(isNumeric);
            Assert.Equal(2.9527559055118114, number, 12);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
