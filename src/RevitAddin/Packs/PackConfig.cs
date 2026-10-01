using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RevitMCPAddin.Packs;

/// <summary>Resolved pack list, plus anything in the config that could not be used.</summary>
internal sealed record PackConfigResult(IReadOnlyList<(string Entry, string FullPath)> Packs,
                                        IReadOnlyList<string> Errors);

/// <summary>
/// Reads <c>%APPDATA%\Autodesk\Revit\Addins\{version}\revit-mcp-packs.json</c>:
/// <code>{ "packs": [ "RevitMCP.Packs\\MyPack.dll" ] }</code>
/// Opt-in: no file, <c>"enabled": false</c>, or an empty list means no pack is loaded. Relative
/// paths resolve against the same Addins folder. A malformed file loads nothing and reports why —
/// it never takes the add-in down. The installer never writes or deletes this file.
/// </summary>
internal static class PackConfig
{
    internal const string FileName = "revit-mcp-packs.json";

    internal static string AddinsFolder(string revitVersion) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "Revit", "Addins", revitVersion);

    /// <summary>Null when the file does not exist — the normal, pack-free case.</summary>
    internal static PackConfigResult? Load(string revitVersion)
    {
        var folder = AddinsFolder(revitVersion);
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            return Parse(File.ReadAllText(path), folder);
        }
        catch (Exception ex)
        {
            return new PackConfigResult(Array.Empty<(string, string)>(),
                new[] { $"{FileName} could not be read: {ex.Message}" });
        }
    }

    /// <summary>Pure parser (unit-tested): no file system access beyond path arithmetic.</summary>
    internal static PackConfigResult Parse(string json, string baseFolder)
    {
        var packs = new List<(string, string)>();
        var errors = new List<string>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            errors.Add($"{FileName} is not valid JSON: {ex.Message}");
            return new PackConfigResult(packs, errors);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{FileName} must be a JSON object");
                return new PackConfigResult(packs, errors);
            }
            if (root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False)
                return new PackConfigResult(packs, errors);

            if (!root.TryGetProperty("packs", out var list))
                return new PackConfigResult(packs, errors);
            if (list.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"\"packs\" must be an array of DLL paths");
                return new PackConfigResult(packs, errors);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var item in list.EnumerateArray())
            {
                var label = $"packs[{i++}]";
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    errors.Add($"{label} must be a non-empty string");
                    continue;
                }
                var entry = item.GetString()!.Trim();
                if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{label} '{entry}' is not a .dll");
                    continue;
                }
                string full;
                try { full = Path.GetFullPath(Path.IsPathRooted(entry) ? entry : Path.Combine(baseFolder, entry)); }
                catch (Exception ex)
                {
                    errors.Add($"{label} '{entry}' is not a valid path: {ex.Message}");
                    continue;
                }
                if (seen.Add(full)) packs.Add((entry, full));
            }
        }
        return new PackConfigResult(packs, errors);
    }
}
