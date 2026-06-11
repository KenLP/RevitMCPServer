using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

public class RevitCommandExceptionTests
{
    [Fact]
    public void Code_is_preserved_on_construction()
    {
        var ex = new RevitCommandException("not_found", "Element 42 not found.");
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public void Message_is_preserved_on_construction()
    {
        var ex = new RevitCommandException("read_only_parameter", "Param is read-only.");
        Assert.Equal("Param is read-only.", ex.Message);
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("invalid_parameter")]
    [InlineData("read_only_parameter")]
    [InlineData("unsupported_view")]
    [InlineData("name_collision")]
    [InlineData("ambiguous_selection")]
    public void All_well_known_codes_round_trip(string code)
    {
        var ex = new RevitCommandException(code, "msg");
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Is_Exception_subtype()
        => Assert.IsAssignableFrom<System.Exception>(
               new RevitCommandException("not_found", "test"));
}
