using System;
using System.Linq;
using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using RevitMCPAddin.Packs;
using Xunit;

namespace RevitMCPAddin.Tests;

// Fakes must be public with a public parameterless constructor: that is the pack contract.
public sealed class FakePackRead : IRevitCommand
{
    public string Name => "fakepack_read";
    public bool IsReadOnly => true;
    public JsonNode? Execute(CommandContext ctx) => null;
}

public sealed class FakePackWrite : IRevitCommand
{
    public string Name => "fakepack_write";
    public bool IsReadOnly => false;
    public JsonNode? Execute(CommandContext ctx) => null;
}

public sealed class FakePackShadowsBuiltin : IRevitCommand
{
    public string Name => "ping";            // a built-in name
    public bool IsReadOnly => true;
    public JsonNode? Execute(CommandContext ctx) => null;
}

public sealed class FakePackBadName : IRevitCommand
{
    public string Name => "Bad-Name";
    public bool IsReadOnly => true;
    public JsonNode? Execute(CommandContext ctx) => null;
}

public sealed class FakePackThrowingCtor : IRevitCommand
{
    public FakePackThrowingCtor() => throw new InvalidOperationException("boom");
    public string Name => "fakepack_throws";
    public bool IsReadOnly => true;
    public JsonNode? Execute(CommandContext ctx) => null;
}

public abstract class FakePackAbstract : IRevitCommand
{
    public string Name => "fakepack_abstract";
    public bool IsReadOnly => true;
    public JsonNode? Execute(CommandContext ctx) => null;
}

public sealed class FakePackNoDefaultCtor : IRevitCommand
{
    public FakePackNoDefaultCtor(int x) { }
    public string Name => "fakepack_noctor";
    public bool IsReadOnly => true;
    public JsonNode? Execute(CommandContext ctx) => null;
}

public class CommandPacksTests
{
    private static CommandRegistry Defaults()
    {
        var reg = new CommandRegistry();
        reg.RegisterDefaults();
        return reg;
    }

    [Fact]
    public void Registers_pack_commands_and_records_their_origin()
    {
        var reg = Defaults();
        var before = reg.Count;
        var r = CommandPacks.RegisterTypes("My.dll", new[] { typeof(FakePackRead), typeof(FakePackWrite) }, reg);

        Assert.Null(r.Error);
        Assert.Equal(new[] { "fakepack_read", "fakepack_write" }, r.Commands.OrderBy(x => x));
        Assert.Equal(before + 2, reg.Count);
        Assert.Equal(2, reg.PackCommandCount);
        Assert.Equal("My.dll", reg.PackOf("fakepack_write"));
        Assert.Null(reg.PackOf("ping"));
        Assert.True(reg.TryGet("fakepack_read", out var cmd));
        Assert.IsType<FakePackRead>(cmd);
    }

    [Fact]
    public void A_pack_can_never_replace_a_builtin_and_the_rest_still_loads()
    {
        var reg = Defaults();
        var builtinPing = reg.TryGet("ping", out var p) ? p : null;
        var r = CommandPacks.RegisterTypes("My.dll",
            new[] { typeof(FakePackShadowsBuiltin), typeof(FakePackRead) }, reg);

        Assert.Null(r.Error);
        Assert.Equal(new[] { "fakepack_read" }, r.Commands);
        Assert.Contains(r.Skipped, s => s.StartsWith("ping:") && s.Contains("built-in"));
        Assert.True(reg.TryGet("ping", out var after));
        Assert.Same(builtinPing, after);
        Assert.Null(reg.PackOf("ping"));
    }

    [Fact]
    public void A_second_pack_cannot_take_a_name_the_first_pack_holds()
    {
        var reg = Defaults();
        CommandPacks.RegisterTypes("First.dll", new[] { typeof(FakePackRead) }, reg);
        var r = CommandPacks.RegisterTypes("Second.dll", new[] { typeof(FakePackRead) }, reg);

        Assert.Empty(r.Commands);
        Assert.Contains(r.Skipped, s => s.Contains("First.dll"));
        Assert.Equal("First.dll", reg.PackOf("fakepack_read"));
    }

    [Fact]
    public void Invalid_name_is_skipped_not_registered()
    {
        var reg = Defaults();
        var r = CommandPacks.RegisterTypes("My.dll", new[] { typeof(FakePackBadName) }, reg);
        Assert.Empty(r.Commands);
        Assert.Contains(r.Skipped, s => s.Contains("invalid name"));
        Assert.False(reg.TryGet("Bad-Name", out _));
    }

    [Fact]
    public void A_throwing_constructor_rejects_the_whole_pack()
    {
        var reg = Defaults();
        var before = reg.Count;
        var r = CommandPacks.RegisterTypes("My.dll",
            new[] { typeof(FakePackRead), typeof(FakePackThrowingCtor) }, reg);

        Assert.NotNull(r.Error);
        Assert.Contains("boom", r.Error);
        Assert.Empty(r.Commands);
        Assert.Equal(before, reg.Count);        // nothing half-loaded
    }

    [Fact]
    public void Abstract_and_non_default_constructible_types_are_ignored()
    {
        var reg = Defaults();
        var r = CommandPacks.RegisterTypes("My.dll",
            new[] { typeof(FakePackAbstract), typeof(FakePackNoDefaultCtor), typeof(string) }, reg);
        Assert.NotNull(r.Error);
        Assert.Contains("no public IRevitCommand", r.Error);
    }

    [Fact]
    public void Builtin_registration_is_unaffected_by_pack_support()
    {
        var reg = Defaults();
        Assert.Equal(0, reg.PackCommandCount);
        Assert.Empty(reg.Packs);
        Assert.All(reg.Names, n => Assert.Null(reg.PackOf(n)));
    }
}

public class PackConfigTests
{
    private const string Base = @"C:\Addins\2027";

    [Fact]
    public void Relative_paths_resolve_against_the_addins_folder()
    {
        var r = PackConfig.Parse(@"{ ""packs"": [ ""RevitMCP.Packs\\My.dll"" ] }", Base);
        Assert.Empty(r.Errors);
        var (entry, full) = Assert.Single(r.Packs);
        Assert.Equal(@"RevitMCP.Packs\My.dll", entry);
        Assert.Equal(@"C:\Addins\2027\RevitMCP.Packs\My.dll", full);
    }

    [Fact]
    public void Absolute_paths_are_kept()
    {
        var r = PackConfig.Parse(@"{ ""packs"": [ ""D:\\packs\\My.dll"" ] }", Base);
        Assert.Equal(@"D:\packs\My.dll", Assert.Single(r.Packs).FullPath);
    }

    [Theory]
    [InlineData(@"{ ""enabled"": false, ""packs"": [ ""My.dll"" ] }")]
    [InlineData(@"{ ""packs"": [] }")]
    [InlineData(@"{ }")]
    public void Disabled_empty_or_missing_list_loads_nothing_and_is_not_an_error(string json)
    {
        var r = PackConfig.Parse(json, Base);
        Assert.Empty(r.Packs);
        Assert.Empty(r.Errors);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData(@"[ ""My.dll"" ]")]
    [InlineData(@"{ ""packs"": ""My.dll"" }")]
    public void Malformed_config_loads_nothing_and_says_why(string json)
    {
        var r = PackConfig.Parse(json, Base);
        Assert.Empty(r.Packs);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public void Bad_entries_are_reported_and_good_ones_still_load()
    {
        var r = PackConfig.Parse(@"{ ""packs"": [ 5, """", ""notes.txt"", ""Good.dll"" ] }", Base);
        Assert.Equal(@"C:\Addins\2027\Good.dll", Assert.Single(r.Packs).FullPath);
        Assert.Equal(3, r.Errors.Count);
        Assert.Contains(r.Errors, e => e.StartsWith("packs[0]"));
        Assert.Contains(r.Errors, e => e.StartsWith("packs[2]") && e.Contains(".dll"));
    }

    [Fact]
    public void The_same_pack_listed_twice_loads_once()
    {
        var r = PackConfig.Parse(@"{ ""packs"": [ ""My.dll"", ""my.DLL"", "".\\My.dll"" ] }", Base);
        Assert.Single(r.Packs);
    }
}
