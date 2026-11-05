using System;
using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using RevitMCPAddin.Server;
using Xunit;

namespace RevitMCPAddin.Tests;

public class OpenDocumentParamsTests
{
    private static readonly Func<string, bool> Exists = _ => true;
    private static readonly Func<string, bool> Missing = _ => false;

    private static RevitCommandException Fails(JsonObject p, Func<string, bool>? exists = null) =>
        Assert.Throws<RevitCommandException>(() => OpenRequest.From(p, exists ?? Exists));

    [Fact]
    public void Command_is_a_ui_action_that_needs_no_document()
    {
        var cmd = new OpenDocumentCommand();
        Assert.Equal("open_document", cmd.Name);
        Assert.Equal(ExecutionKind.UiAction, cmd.Execution);
        Assert.False(cmd.IsReadOnly);
        Assert.Equal("medium", cmd.RiskLevel);
    }

    [Fact]
    public void Path_defaults_to_all_worksets_no_detach_no_audit()
    {
        var r = OpenRequest.From(new JsonObject { ["path"] = @"C:\models\a.rvt" }, Exists);
        Assert.Equal(@"C:\models\a.rvt", r.Path);
        Assert.Null(r.Cloud);
        Assert.Equal("all", r.Worksets);
        Assert.Equal("none", r.Detach);
        Assert.False(r.Audit);
    }

    [Fact]
    public void Path_is_made_absolute()
    {
        var r = OpenRequest.From(new JsonObject { ["path"] = @"C:\models\sub\..\a.rvt" }, Exists);
        Assert.Equal(@"C:\models\a.rvt", r.Path);
    }

    [Theory]
    [InlineData("ALL", "all")]
    [InlineData("none", "none")]
    [InlineData("Last_Viewed", "last_viewed")]
    public void Worksets_choices_are_case_insensitive(string given, string expected)
    {
        var r = OpenRequest.From(new JsonObject { ["path"] = @"C:\a.rvt", ["worksets"] = given }, Exists);
        Assert.Equal(expected, r.Worksets);
    }

    [Fact]
    public void Ask_is_not_a_worksets_choice()
        => Assert.Equal("invalid_parameter",
            Fails(new JsonObject { ["path"] = @"C:\a.rvt", ["worksets"] = "ask" }).Code);

    [Fact]
    public void Detach_rejects_unknown_values()
        => Assert.Equal("invalid_parameter",
            Fails(new JsonObject { ["path"] = @"C:\a.rvt", ["detach"] = "yes" }).Code);

    [Fact]
    public void Exactly_one_of_path_or_cloud()
    {
        Assert.Equal("invalid_parameter", Fails(new JsonObject()).Code);
        Assert.Equal("invalid_parameter", Fails(new JsonObject
        {
            ["path"] = @"C:\a.rvt",
            ["cloud"] = new JsonObject { ["region"] = "US" },
        }).Code);
    }

    [Fact]
    public void Missing_file_is_not_found()
        => Assert.Equal("not_found", Fails(new JsonObject { ["path"] = @"C:\nope.rvt" }, Missing).Code);

    [Fact]
    public void Cloud_parses_region_and_guids()
    {
        var p = Guid.NewGuid();
        var m = Guid.NewGuid();
        var r = OpenRequest.From(new JsonObject
        {
            ["cloud"] = new JsonObject { ["region"] = " EMEA ", ["projectGuid"] = p.ToString(), ["modelGuid"] = m.ToString("B") },
        }, Missing);
        Assert.Null(r.Path);
        Assert.Equal(new CloudRequest("EMEA", p, m), r.Cloud);
    }

    [Fact]
    public void Cloud_needs_region_and_valid_guids()
    {
        var g = Guid.NewGuid().ToString();
        Assert.Equal("invalid_parameter", Fails(new JsonObject
            { ["cloud"] = new JsonObject { ["projectGuid"] = g, ["modelGuid"] = g } }).Code);
        Assert.Equal("invalid_parameter", Fails(new JsonObject
            { ["cloud"] = new JsonObject { ["region"] = "US", ["projectGuid"] = "x", ["modelGuid"] = g } }).Code);
        Assert.Equal("invalid_parameter", Fails(new JsonObject { ["cloud"] = "US" }).Code);
    }

    [Theory]
    [InlineData(@"C:\Models\A.rvt", @"C:\models\a.rvt", true)]
    [InlineData(@"C:\models\a.rvt", @"C:\models\b.rvt", false)]
    [InlineData("", @"C:\models\a.rvt", false)]
    [InlineData(null, @"C:\models\a.rvt", false)]
    public void Same_path_ignores_case_and_rejects_pathless_documents(string? docPath, string requested, bool same)
        => Assert.Equal(same, OpenRequest.SamePath(docPath, requested));

    [Fact]
    public void Not_signed_in_maps_to_403()
        => Assert.Equal(403, McpHttpServer.StatusForResult(new JsonObject
        {
            ["ok"] = false,
            ["error"] = new JsonObject { ["code"] = "not_signed_in", ["message"] = "x" },
        }));
}
