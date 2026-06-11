using System.Text.Json.Nodes;
using RevitMCPAddin.Server;
using Xunit;

namespace RevitMCPAddin.Tests;

public class StatusForResultTests
{
    private static JsonObject Ok() => new() { ["ok"] = true };

    private static JsonObject Err(string code) => new()
    {
        ["ok"] = false,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = "test" },
    };

    [Fact]
    public void Success_returns_200()
        => Assert.Equal(200, McpHttpServer.StatusForResult(Ok()));

    [Theory]
    [InlineData("bad_request",         400)]
    [InlineData("bad_json",            400)]
    [InlineData("invalid_parameter",   400)]
    [InlineData("read_only_parameter", 400)]
    [InlineData("unsupported_view",    400)]
    [InlineData("invalid_chars",       400)]
    public void Client_errors_return_400(string code, int expected)
        => Assert.Equal(expected, McpHttpServer.StatusForResult(Err(code)));

    [Fact]
    public void Unauthorized_returns_401()
        => Assert.Equal(401, McpHttpServer.StatusForResult(Err("unauthorized")));

    [Theory]
    [InlineData("unknown_command", 404)]
    [InlineData("not_found",       404)]
    public void NotFound_codes_return_404(string code, int expected)
        => Assert.Equal(expected, McpHttpServer.StatusForResult(Err(code)));

    [Theory]
    [InlineData("timeout",    408)]
    [InlineData("cancelled",  408)]
    public void Timeout_codes_return_408(string code, int expected)
        => Assert.Equal(expected, McpHttpServer.StatusForResult(Err(code)));

    [Theory]
    [InlineData("name_collision",    409)]
    [InlineData("system_family",     409)]
    [InlineData("ambiguous_selection", 409)]
    public void Conflict_codes_return_409(string code, int expected)
        => Assert.Equal(expected, McpHttpServer.StatusForResult(Err(code)));

    [Theory]
    [InlineData("command_failed")]
    [InlineData("step_failed")]
    [InlineData("dispatch_failed")]
    [InlineData("server_error")]
    [InlineData("batch_aborted")]
    [InlineData("something_unexpected")]
    public void Unknown_error_codes_return_500(string code)
        => Assert.Equal(500, McpHttpServer.StatusForResult(Err(code)));
}
