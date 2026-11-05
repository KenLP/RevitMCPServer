using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

/// <summary>Path rules shared by the commands that write a file (pure apart from the injected checks).</summary>
internal static class OutputPath
{
    /// <summary>Trims, expands environment variables and makes the path absolute.</summary>
    internal static string Full(string raw, string key)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim())); }
        catch (Exception ex)
        {
            throw new RevitCommandException("invalid_parameter", $"'{key}' is not a valid file path: {ex.Message}");
        }
    }

    /// <summary>A file the command is about to write: right extension, parent folder present, not in the way.</summary>
    internal static string ForWrite(JsonObject p, string key, string extension, bool overwrite,
        Func<string, bool>? fileExists, Func<string, bool>? folderExists)
    {
        var raw = P.StrOrNull(p, key)
            ?? throw new RevitCommandException("invalid_parameter", $"'{key}' is required.");
        var full = Full(raw, key);
        if (!full.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new RevitCommandException("invalid_parameter", $"'{key}' must end in {extension}: {full}");

        var folder = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(folder) || !(folderExists ?? Directory.Exists)(folder))
            throw new RevitCommandException("not_found", $"Folder not found: {folder}");
        if (!overwrite && (fileExists ?? File.Exists)(full))
            throw new RevitCommandException("already_exists", $"File already exists and overwrite is false: {full}");
        return full;
    }
}

/// <summary>Validated params of <c>save_as</c>.</summary>
internal sealed record SaveAsRequest(string Path, bool Overwrite, int MaxBackups)
{
    internal static SaveAsRequest From(JsonObject p, Func<string, bool>? fileExists = null,
        Func<string, bool>? folderExists = null)
    {
        var overwrite = P.BoolOr(p, "overwrite", false);
        var maxBackups = P.IntOr(p, "maxBackups", 1);
        if (maxBackups < 1)
            throw new RevitCommandException("invalid_parameter", "'maxBackups' must be 1 or more.");
        return new SaveAsRequest(OutputPath.ForWrite(p, "path", ".rvt", overwrite, fileExists, folderExists),
            overwrite, maxBackups);
    }
}

/// <summary>Validated params of <c>close_document</c>. The file need not exist: this names an open document.</summary>
internal sealed record CloseRequest(string Path, bool Save)
{
    internal static CloseRequest From(JsonObject p)
    {
        var raw = P.StrOrNull(p, "path")
            ?? throw new RevitCommandException("invalid_parameter",
                "'path' is required: close_document never defaults to the active document.");
        return new CloseRequest(OutputPath.Full(raw, "path"), P.BoolOr(p, "save", false));
    }
}

/// <summary>Validated params of <c>new_document</c>.</summary>
internal sealed record NewDocumentRequest(string Template, string Path, bool Overwrite)
{
    internal static NewDocumentRequest From(JsonObject p, Func<string, bool>? fileExists = null,
        Func<string, bool>? folderExists = null)
    {
        var raw = P.StrOrNull(p, "template")
            ?? throw new RevitCommandException("invalid_parameter", "'template' is required.");
        var template = OutputPath.Full(raw, "template");
        if (!(fileExists ?? File.Exists)(template))
            throw new RevitCommandException("not_found", $"Template not found: {template}");

        var overwrite = P.BoolOr(p, "overwrite", false);
        return new NewDocumentRequest(template,
            OutputPath.ForWrite(p, "path", ".rvt", overwrite, fileExists, folderExists), overwrite);
    }
}

/// <summary>
/// Validated params of <c>export_ifc</c>. <paramref name="Version"/> is checked against the names the
/// running Revit defines: the list differs between versions, so it is never hard-coded.
/// </summary>
internal sealed record ExportIfcRequest(string OutputPath, string Version, long? ViewId, bool BaseQuantities,
    int SpaceBoundaryLevel, bool WallAndColumnSplitting, bool Overwrite)
{
    internal static ExportIfcRequest From(JsonObject p, string[] knownVersions, Func<string, bool>? fileExists = null,
        Func<string, bool>? folderExists = null)
    {
        var requested = P.StrOrNull(p, "ifcVersion")?.Trim() ?? "IFC4";
        var version = knownVersions.FirstOrDefault(v => string.Equals(v, requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new RevitCommandException("invalid_parameter",
                $"'ifcVersion' must be one of {string.Join(", ", knownVersions)}.");

        var level = P.IntOr(p, "spaceBoundaryLevel", 1);
        if (level is < 0 or > 2)
            throw new RevitCommandException("invalid_parameter", "'spaceBoundaryLevel' must be 0, 1 or 2.");

        var overwrite = P.BoolOr(p, "overwrite", true);
        return new ExportIfcRequest(
            Commands.OutputPath.ForWrite(p, "outputPath", ".ifc", overwrite, fileExists, folderExists),
            version, P.LongOrNull(p, "viewId"), P.BoolOr(p, "exportBaseQuantities", true), level,
            P.BoolOr(p, "wallAndColumnSplitting", false), overwrite);
    }
}
