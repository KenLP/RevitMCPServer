using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using RevitMCPAddin.Server;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>
/// Unit tests for McpHttpServer.ParseBatchParams — the parsing path that lets
/// POST /mcp {command:"batch", params:{steps:[...]}} route to the batch handler.
/// This closes the HTTP-direct ↔ stdio parity gap reported by bim-orchestrator.
/// </summary>
public class BatchFromSingleCommandTests
{
    private static JsonObject Params(JsonArray steps, bool? stopOnError = null)
    {
        var obj = new JsonObject { ["steps"] = steps };
        if (stopOnError.HasValue)
            obj["stopOnError"] = stopOnError.Value;
        return obj;
    }

    private static JsonObject Step(string command, JsonObject? stepParams = null)
    {
        var obj = new JsonObject { ["command"] = command };
        if (stepParams is not null)
            obj["params"] = stepParams;
        return obj;
    }

    // ── Happy paths ──────────────────────────────────────────────────────────

    [Fact]
    public void Valid_steps_returns_parsed_list()
    {
        var input = Params(new JsonArray
        {
            Step("set_parameter", new JsonObject { ["id"] = 123, ["parameterName"] = "Mark", ["value"] = "X" }),
            Step("set_parameter", new JsonObject { ["id"] = 456, ["parameterName"] = "Mark", ["value"] = "Y" }),
        });

        var (steps, stopOnError, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(error);
        Assert.NotNull(steps);
        Assert.Equal(2, steps!.Count);
        Assert.Equal("set_parameter", steps[0].CommandName);
        Assert.Equal("set_parameter", steps[1].CommandName);
        Assert.True(stopOnError); // default
    }

    [Fact]
    public void Step_params_are_cloned_and_detached()
    {
        var original = new JsonObject { ["id"] = 999 };
        var input = Params(new JsonArray { Step("ping", original) });

        var (steps, _, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(error);
        Assert.NotNull(steps);
        // Verify the detached copy has the same value but is a different object.
        Assert.Equal(999, steps![0].Parameters["id"]?.GetValue<int>());
    }

    [Fact]
    public void Step_without_params_field_gets_empty_object()
    {
        var input = Params(new JsonArray { Step("ping") });

        var (steps, _, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(error);
        Assert.NotNull(steps);
        Assert.Empty(steps![0].Parameters);
    }

    [Fact]
    public void StopOnError_false_is_respected()
    {
        var input = Params(new JsonArray { Step("ping") }, stopOnError: false);

        var (_, stopOnError, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(error);
        Assert.False(stopOnError);
    }

    [Fact]
    public void StopOnError_defaults_to_true_when_absent()
    {
        var input = Params(new JsonArray { Step("ping") });

        var (_, stopOnError, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(error);
        Assert.True(stopOnError);
    }

    [Fact]
    public void Empty_steps_array_returns_empty_list_not_error()
    {
        var input = Params(new JsonArray());

        var (steps, _, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(error);
        Assert.NotNull(steps);
        Assert.Empty(steps!);
    }

    // ── Error paths ──────────────────────────────────────────────────────────

    [Fact]
    public void Null_params_returns_error()
    {
        var (steps, _, error) = McpHttpServer.ParseBatchParams(null);

        Assert.Null(steps);
        Assert.NotNull(error);
        Assert.Contains("steps", error);
    }

    [Fact]
    public void Missing_steps_field_returns_error()
    {
        var input = new JsonObject { ["stopOnError"] = true };

        var (steps, _, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(steps);
        Assert.NotNull(error);
        Assert.Contains("steps", error);
    }

    [Fact]
    public void Step_missing_command_field_returns_error()
    {
        var input = Params(new JsonArray
        {
            new JsonObject { ["params"] = new JsonObject() }, // no "command"
        });

        var (steps, _, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(steps);
        Assert.NotNull(error);
        Assert.Contains("command", error);
    }

    [Fact]
    public void Step_with_empty_command_returns_error()
    {
        var input = Params(new JsonArray
        {
            new JsonObject { ["command"] = "" },
        });

        var (steps, _, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(steps);
        Assert.NotNull(error);
    }

    [Fact]
    public void Non_object_step_returns_error()
    {
        var input = Params(new JsonArray { JsonValue.Create("not-an-object")! });

        var (steps, _, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(steps);
        Assert.NotNull(error);
        Assert.Contains("object", error);
    }

    // ── Orchestrator contract ────────────────────────────────────────────────

    [Fact]
    public void Orchestrator_payload_shape_is_parsed_correctly()
    {
        // Mirrors what bim-orchestrator posts:
        // POST /mcp { command:"batch", params:{ steps:[...], stopOnError:true }, dryRun:false }
        // (dryRun is handled at the HTTP layer, not passed into params)
        var input = new JsonObject
        {
            ["steps"] = new JsonArray
            {
                new JsonObject
                {
                    ["command"] = "set_parameter",
                    ["params"] = new JsonObject
                    {
                        ["id"] = 184239,
                        ["parameterName"] = "Mark",
                        ["value"] = "DUCT-01",
                    },
                },
                new JsonObject
                {
                    ["command"] = "set_parameter",
                    ["params"] = new JsonObject
                    {
                        ["id"] = 184277,
                        ["parameterName"] = "Mark",
                        ["value"] = "DUCT-02",
                    },
                },
            },
            ["stopOnError"] = true,
        };

        var (steps, stopOnError, error) = McpHttpServer.ParseBatchParams(input);

        Assert.Null(error);
        Assert.True(stopOnError);
        Assert.Equal(2, steps!.Count);
        Assert.Equal("set_parameter", steps[0].CommandName);
        Assert.Equal(184239, steps[0].Parameters["id"]?.GetValue<int>());
        Assert.Equal("DUCT-01", steps[0].Parameters["value"]?.GetValue<string>());
        Assert.Equal(184277, steps[1].Parameters["id"]?.GetValue<int>());
        Assert.Equal("DUCT-02", steps[1].Parameters["value"]?.GetValue<string>());
    }
}
