using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using RevitMCPAddin.Commands;

namespace RevitMCPAddin.Packs;

/// <summary>
/// Loads the packs named in <see cref="PackConfig"/> into the add-in's own load context, so a pack's
/// reference to <c>RevitMCP.Core</c> binds to the Core that is already loaded and its
/// <see cref="IRevitCommand"/> is the same type the dispatcher knows. Every pack is isolated in its
/// own try/catch: a bad pack is reported in <see cref="CommandRegistry.Packs"/> and skipped, never
/// allowed to stop the server or another pack.
///
/// Compatibility: a pack compiled against a different Core may fail to load (reported here) or,
/// if it calls a Core member that no longer exists, fail at call time with a normal error envelope.
/// Rebuild packs against the Core version they run with.
/// </summary>
internal static class PackLoader
{
    private static readonly HashSet<string> ResolverDirs = new(StringComparer.OrdinalIgnoreCase);

    internal static void LoadAll(string revitVersion, CommandRegistry registry, Action<string> log)
    {
        var cfg = PackConfig.Load(revitVersion);
        if (cfg is null) return;                         // no config file: the normal case

        foreach (var err in cfg.Errors)
        {
            registry.Packs.Add(new PackReport(PackConfig.FileName) { Error = err });
            log($"[RevitMCP] Packs: {err}");
        }

        foreach (var (entry, fullPath) in cfg.Packs)
        {
            var report = LoadOne(entry, fullPath, registry);
            registry.Packs.Add(report);
            log(report.Error is not null
                ? $"[RevitMCP] Pack {entry}: NOT loaded — {report.Error}"
                : $"[RevitMCP] Pack {entry}: {report.Commands.Count} command(s)" +
                  (report.Skipped.Count > 0 ? $", {report.Skipped.Count} skipped" : ""));
        }
    }

    private static PackReport LoadOne(string entry, string fullPath, CommandRegistry registry)
    {
        if (!File.Exists(fullPath))
            return new PackReport(entry) { Error = $"file not found: {fullPath}" };

        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(typeof(CommandRegistry).Assembly)
                      ?? AssemblyLoadContext.Default;
            AddResolver(alc, Path.GetDirectoryName(fullPath)!);

            var asm = alc.LoadFromAssemblyPath(fullPath);

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var first = ex.LoaderExceptions.FirstOrDefault(e => e is not null);
                return new PackReport(entry)
                {
                    Error = "types could not be loaded (built against a different RevitMCP.Core?): " +
                            (first?.Message ?? ex.Message),
                };
            }

            return CommandPacks.RegisterTypes(entry, types, registry);
        }
        catch (Exception ex)
        {
            return new PackReport(entry) { Error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    // A pack may ship its own dependencies next to it; resolve them from the pack's folder.
    private static void AddResolver(AssemblyLoadContext alc, string dir)
    {
        lock (ResolverDirs)
        {
            if (!ResolverDirs.Add(dir)) return;
        }
        alc.Resolving += (ctx, name) =>
        {
            var candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };
    }
}
