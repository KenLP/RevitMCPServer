using System.Linq;
using System.Text.Json.Nodes;
using RevitMCPAddin;
using RevitMCPAddin.Server;
using Xunit;

namespace RevitMCPAddin.Tests;

public class DialogPolicyTests
{
    [Fact]
    public void No_file_means_disabled_so_the_host_subscribes_nothing()
    {
        var s = DialogPolicyConfig.Parse(null);
        Assert.False(s.Enabled);
        Assert.False(s.Log);
        Assert.Empty(s.Rules);
        Assert.Empty(s.Errors);
        Assert.Same(DialogPolicySettings.Disabled, s);
    }

    [Fact]
    public void Missing_file_on_disk_loads_disabled()
    {
        // A version folder that cannot exist: Load must fall back to Disabled, not throw.
        var s = DialogPolicyConfig.Load("no-such-revit-version-0000");
        Assert.False(s.Enabled);
    }

    [Fact]
    public void Empty_or_bare_object_enables_logging_with_no_rules()
    {
        foreach (var json in new[] { "", "{}" })
        {
            var s = DialogPolicyConfig.Parse(json);
            Assert.True(s.Enabled);
            Assert.True(s.Log);
            Assert.Empty(s.Rules);
        }
    }

    [Fact]
    public void Invalid_json_keeps_the_policy_off()
    {
        var s = DialogPolicyConfig.Parse("{ rules: [");
        Assert.False(s.Enabled);
        Assert.Single(s.Errors);
    }

    [Fact]
    public void Enabled_false_and_log_false_are_honoured()
    {
        var s = DialogPolicyConfig.Parse("""{ "enabled": false, "log": false }""");
        Assert.False(s.Enabled);
        Assert.False(s.Log);
    }

    [Fact]
    public void Rules_are_parsed_and_bad_ones_skipped_with_a_reason()
    {
        var s = DialogPolicyConfig.Parse("""
        { "rules": [
            { "dialogId": "TaskDialog_Unresolved_References", "result": 1002 },
            { "messageContains": "closed views", "result": 1 },
            { "result": 1 },
            { "dialogId": "X" },
            { "dialogId": "Y", "result": "ok" },
            "nope"
        ] }
        """);
        Assert.True(s.Enabled);
        Assert.Equal(2, s.Rules.Count);
        Assert.Equal(new DialogRule("TaskDialog_Unresolved_References", null, 1002), s.Rules[0]);
        Assert.Equal(new DialogRule(null, "closed views", 1), s.Rules[1]);
        Assert.Equal(4, s.Errors.Count);
    }

    [Fact]
    public void Match_needs_every_condition_of_a_rule_and_first_match_wins()
    {
        var rules = new[]
        {
            new DialogRule("A", "save", 6),
            new DialogRule("A", null, 1),
            new DialogRule(null, "closed views", 2),
        };
        Assert.Equal(0, DialogPolicyConfig.Match(rules, "a", "Do you want to SAVE?"));
        Assert.Equal(1, DialogPolicyConfig.Match(rules, "A", "something else"));
        Assert.Equal(1, DialogPolicyConfig.Match(rules, "A", null));
        Assert.Equal(2, DialogPolicyConfig.Match(rules, "B", "search Closed Views for …"));
        Assert.Null(DialogPolicyConfig.Match(rules, "B", "unrelated"));
        Assert.Null(DialogPolicyConfig.Match(rules, null, null));
    }

    [Fact]
    public void Monitor_records_into_ring_and_open_captures_only()
    {
        var m = new DialogMonitor(DialogPolicySettings.RecordOnly);
        m.Record("before", "dialog", null, null, null, false);
        using (var scope = m.Capture())
        {
            m.Record("during", "task_dialog", "msg", 0, 1001, true);
            Assert.Equal(new[] { "during" }, scope.Events.Select(e => e.DialogId));
        }
        m.Record("after", "dialog", null, null, null, false);

        var all = m.Recent();
        Assert.Equal(new[] { "before", "during", "after" }, all.Select(e => e.DialogId));
        Assert.Equal(new long[] { 1, 2, 3 }, all.Select(e => e.Seq));
        Assert.Equal(new[] { "after" }, m.Recent(since: 2).Select(e => e.DialogId));
    }

    [Fact]
    public void Ring_keeps_only_the_most_recent_events()
    {
        var m = new DialogMonitor(DialogPolicySettings.RecordOnly);
        for (var i = 0; i < DialogMonitor.RingSize + 5; i++) m.Record($"d{i}", "dialog", null, null, null, false);
        var all = m.Recent();
        Assert.Equal(DialogMonitor.RingSize, all.Count);
        Assert.Equal("d5", all[0].DialogId);
    }

    [Fact]
    public void Dialogs_endpoint_without_policy_reports_disabled_and_empty()
    {
        var body = McpHttpServer.DialogsSnapshot(null, 0);
        Assert.False(body["enabled"]!.GetValue<bool>());
        Assert.Empty(body["dialogs"]!.AsArray());
    }

    [Fact]
    public void Dialogs_endpoint_lists_events_after_since()
    {
        var m = new DialogMonitor(DialogPolicySettings.RecordOnly);
        m.Record("one", "dialog", null, null, null, false);
        m.Record("two", "message_box", "hello", null, null, false);
        var body = McpHttpServer.DialogsSnapshot(m, 1);
        var arr = body["dialogs"]!.AsArray();
        Assert.Single(arr);
        Assert.Equal("two", arr[0]!["dialogId"]!.GetValue<string>());
        Assert.Equal(2, arr[0]!["seq"]!.GetValue<long>());
        Assert.Equal("hello", arr[0]!["message"]!.GetValue<string>());
    }
}
