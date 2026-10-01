using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitMCPAddin.Commands;

/// <summary>What happened to one command pack at start-up. Surfaced by <c>GET /commands</c>.</summary>
public sealed class PackReport
{
    public PackReport(string file) => File = file;

    /// <summary>The pack DLL as named in the config (not necessarily the resolved full path).</summary>
    public string File { get; }

    /// <summary>Commands this pack registered.</summary>
    public List<string> Commands { get; } = new();

    /// <summary>Commands this pack offered but that were not registered, each with the reason.</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>Set when the whole pack was rejected; <see cref="Commands"/> is then empty.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Registers the commands a pack assembly offers. A pack is any assembly with public, non-abstract
/// <see cref="IRevitCommand"/> classes that have a public parameterless constructor.
///
/// Rules, in the order they apply:
/// <list type="number">
///   <item>All commands are instantiated first; if any constructor throws, the whole pack is
///   rejected — a pack never loads half-way.</item>
///   <item>A pack can never replace a command that is already registered (built-in or from an
///   earlier pack): that command is skipped and reported, the rest of the pack still loads. This
///   is what lets a command move from the built-in set into a pack without a gap.</item>
///   <item>A name outside <c>[a-z0-9_]</c> is skipped and reported.</item>
/// </list>
/// Pack commands are reachable over HTTP (<c>/mcp</c>, <c>/mcp/batch</c>) only; the MCP tool
/// surface is a fixed list on the Node side and never includes them.
/// </summary>
public static class CommandPacks
{
    private static readonly Regex ValidName = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    public static PackReport RegisterTypes(string packFile, IEnumerable<Type> types, CommandRegistry registry)
    {
        var report = new PackReport(packFile);

        var candidates = types
            .Where(t => t.IsClass && !t.IsAbstract && (t.IsPublic || t.IsNestedPublic) &&
                        typeof(IRevitCommand).IsAssignableFrom(t) &&
                        t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            report.Error = "no public IRevitCommand class with a parameterless constructor";
            return report;
        }

        var instances = new List<IRevitCommand>(candidates.Count);
        foreach (var t in candidates)
        {
            try
            {
                instances.Add((IRevitCommand)Activator.CreateInstance(t)!);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                report.Error = $"constructor of {t.FullName} threw {inner.GetType().Name}: {inner.Message}";
                return report;
            }
        }

        foreach (var cmd in instances)
        {
            string name;
            try { name = cmd.Name; }
            catch (Exception ex) { report.Skipped.Add($"{cmd.GetType().FullName}: Name threw {ex.GetType().Name}"); continue; }

            if (string.IsNullOrEmpty(name) || !ValidName.IsMatch(name))
            {
                report.Skipped.Add($"{name}: invalid name (allowed: a-z 0-9 _)");
                continue;
            }
            if (!registry.TryRegisterFromPack(cmd, packFile, out var owner))
            {
                report.Skipped.Add($"{name}: already registered by {owner}");
                continue;
            }
            report.Commands.Add(name);
        }
        return report;
    }
}
