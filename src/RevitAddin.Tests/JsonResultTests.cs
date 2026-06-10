using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

public class JsonResultTests
{
    [Fact]
    public void Success_no_data_has_ok_true()
    {
        var result = JsonResult.Success();
        Assert.Equal(true, result["ok"]?.GetValue<bool>());
        Assert.Null(result["error"]);
    }

    [Fact]
    public void Success_with_data_includes_data()
    {
        var data = new JsonObject { ["x"] = 1 };
        var result = JsonResult.Success(data);
        Assert.Equal(true, result["ok"]?.GetValue<bool>());
        Assert.NotNull(result["data"]);
        Assert.Equal(1, (result["data"] as JsonObject)?["x"]?.GetValue<int>());
    }

    [Fact]
    public void Error_without_type_has_code_and_message()
    {
        var result = JsonResult.Error("not_found", "Element missing");
        Assert.Equal(false, result["ok"]?.GetValue<bool>());
        var error = Assert.IsType<JsonObject>(result["error"]);
        Assert.Equal("not_found", error["code"]?.GetValue<string>());
        Assert.Equal("Element missing", error["message"]?.GetValue<string>());
        Assert.Null(error["type"]);
    }

    [Fact]
    public void Error_with_type_includes_type()
    {
        var result = JsonResult.Error("server_error", "Boom", "System.InvalidOperationException");
        var error = Assert.IsType<JsonObject>(result["error"]);
        Assert.Equal("System.InvalidOperationException", error["type"]?.GetValue<string>());
    }

    [Fact]
    public void Error_has_ok_false()
    {
        var result = JsonResult.Error("bad_request", "Invalid input");
        Assert.Equal(false, result["ok"]?.GetValue<bool>());
    }

    [Fact]
    public void Success_null_data_is_allowed()
    {
        var result = JsonResult.Success((JsonNode?)null);
        Assert.Equal(true, result["ok"]?.GetValue<bool>());
    }
}
