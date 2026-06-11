using System.Linq;
using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

public class CommandRegistryTests
{
    // Minimal fakes that implement IRevitCommand without Revit types.
    private sealed class ReadCmd : IRevitCommand
    {
        public string Name => "test_read";
        public bool IsReadOnly => true;
        public JsonNode? Execute(CommandContext ctx) => null;
    }

    private sealed class WriteCmd : IRevitCommand
    {
        public string Name => "test_write";
        public bool IsReadOnly => false;
        public JsonNode? Execute(CommandContext ctx) => null;
    }

    private sealed class UiCmd : IRevitCommand
    {
        public string Name => "test_ui";
        public bool IsReadOnly => false;
        public ExecutionKind Execution => ExecutionKind.UiAction;
        public JsonNode? Execute(CommandContext ctx) => null;
    }

    [Fact]
    public void Register_and_TryGet_round_trip()
    {
        var reg = new CommandRegistry();
        reg.Register(new ReadCmd());
        Assert.True(reg.TryGet("test_read", out var cmd));
        Assert.IsType<ReadCmd>(cmd);
    }

    [Fact]
    public void TryGet_returns_false_for_unknown_name()
    {
        var reg = new CommandRegistry();
        Assert.False(reg.TryGet("nonexistent", out _));
    }

    [Fact]
    public void Names_contains_registered_commands()
    {
        var reg = new CommandRegistry();
        reg.Register(new ReadCmd());
        reg.Register(new WriteCmd());
        Assert.Contains("test_read", reg.Names);
        Assert.Contains("test_write", reg.Names);
    }

    [Fact]
    public void Register_replaces_existing_command_with_same_name()
    {
        var reg = new CommandRegistry();
        reg.Register(new ReadCmd());
        reg.Register(new ReadCmd());
        Assert.Single(reg.Names, n => n == "test_read");
    }

    // Default interface members are only accessible via the interface type.
    [Fact]
    public void Execution_defaults_to_ReadOnly_for_IsReadOnly_true()
        => Assert.Equal(ExecutionKind.ReadOnly, ((IRevitCommand)new ReadCmd()).Execution);

    [Fact]
    public void Execution_defaults_to_ModelWrite_for_IsReadOnly_false()
        => Assert.Equal(ExecutionKind.ModelWrite, ((IRevitCommand)new WriteCmd()).Execution);

    [Fact]
    public void Execution_overrides_to_UiAction()
        => Assert.Equal(ExecutionKind.UiAction, ((IRevitCommand)new UiCmd()).Execution);

    [Fact]
    public void RiskLevel_is_read_for_readonly_command()
        => Assert.Equal("read", ((IRevitCommand)new ReadCmd()).RiskLevel);

    [Fact]
    public void RiskLevel_is_low_for_write_command()
        => Assert.Equal("low", ((IRevitCommand)new WriteCmd()).RiskLevel);

    [Fact]
    public void Describe_returns_info_for_each_registered_command()
    {
        var reg = new CommandRegistry();
        reg.Register(new ReadCmd());
        reg.Register(new WriteCmd());
        var entries = reg.Describe().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "test_read" && e.IsReadOnly);
        Assert.Contains(entries, e => e.Name == "test_write" && !e.IsReadOnly);
    }

    [Fact]
    public void Describe_includes_executionKind()
    {
        var reg = new CommandRegistry();
        reg.Register(new ReadCmd());
        reg.Register(new WriteCmd());
        reg.Register(new UiCmd());
        var entries = reg.Describe().ToList();
        Assert.Contains(entries, e => e.Name == "test_read"  && e.ExecutionKind == "ReadOnly");
        Assert.Contains(entries, e => e.Name == "test_write" && e.ExecutionKind == "ModelWrite");
        Assert.Contains(entries, e => e.Name == "test_ui"    && e.ExecutionKind == "UiAction");
    }
}
