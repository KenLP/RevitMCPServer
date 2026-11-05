using System;
using System.Text.Json.Nodes;
using RevitMCPAddin;
using Xunit;

namespace RevitMCPAddin.Tests;

public class CommitFailuresTests
{
    private const string Rooms = "Multiple Rooms are in the same enclosed region.";
    private static readonly string Id = Guid.Empty.ToString();

    private static CommitFailure Warning(string message, params long[] ids) =>
        new(false, message, Id, null, ids);

    [Fact]
    public void Annotate_clean_commit_adds_nothing()
    {
        var data = new JsonObject();
        CommitFailures.Annotate(data, Array.Empty<CommitFailure>());
        Assert.Empty(data);
    }

    [Fact]
    public void Annotate_groups_repeats_and_merges_element_ids()
    {
        var data = new JsonObject();
        CommitFailures.Annotate(data, new[] { Warning(Rooms, 1, 2), Warning(Rooms, 2, 3) });

        var entry = Assert.Single(data["revitWarnings"]!.AsArray())!;
        Assert.Equal(Rooms, entry["message"]!.GetValue<string>());
        Assert.Equal(2, entry["count"]!.GetValue<int>());
        Assert.Equal(Id, entry["failureId"]!.GetValue<string>());
        Assert.Equal("[1,2,3]", entry["elementIds"]!.ToJsonString());
    }

    [Fact]
    public void Annotate_names_the_resolution_Revit_offers()
    {
        var data = new JsonObject();
        CommitFailures.Annotate(data, new[] { new CommitFailure(false, Rooms, Id, "Delete Room(s)", new long[] { 1 }) });
        Assert.Equal("Delete Room(s)", data["revitWarnings"]![0]!["resolution"]!.GetValue<string>());
    }

    [Fact]
    public void Caption_drops_the_accelerator()
    {
        Assert.Equal("Delete Room(s)", CommitFailures.Caption("Delete &Room(s)"));
        Assert.Null(CommitFailures.Caption(null));
    }

    [Fact]
    public void Annotate_leaves_errors_out_of_the_warnings()
    {
        var data = new JsonObject();
        CommitFailures.Annotate(data, new[] { new CommitFailure(true, "boom", Id, null, new long[] { 1 }) });
        Assert.Empty(data);
    }

    [Fact]
    public void Rejected_carries_the_error_text_and_the_warnings_seen_with_it()
    {
        var result = CommitFailures.Rejected("RolledBack", new[]
        {
            new CommitFailure(true, "Can't keep elements joined.", Id, "Unjoin Elements", new long[] { 7 }),
            Warning(Rooms, 1),
        });

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("commit_failed", result["error"]!["code"]!.GetValue<string>());
        Assert.Contains("Can't keep elements joined.", result["error"]!["message"]!.GetValue<string>());
        Assert.Contains("unchanged", result["error"]!["message"]!.GetValue<string>());
        Assert.Equal("Unjoin Elements", result["data"]!["revitErrors"]![0]!["resolution"]!.GetValue<string>());
        Assert.Single(result["data"]!["revitWarnings"]!.AsArray());
    }

    [Fact]
    public void Rejected_without_a_recorded_error_still_says_why()
    {
        var result = CommitFailures.Rejected("RolledBack", Array.Empty<CommitFailure>());
        Assert.Contains("status RolledBack", result["error"]!["message"]!.GetValue<string>());
        Assert.Empty(result["data"]!["revitErrors"]!.AsArray());
    }
}
