using System;
using System.IO;
using System.Text.Json;

namespace RevitMCPAddin.Panel;

/// <summary>What the optional second pane shows and how its ribbon entry is named.</summary>
internal sealed record ExtraPanelSettings(string Url, string Tab, string Label);

/// <summary>
/// Reads the optional second pane from
/// <c>%APPDATA%\Autodesk\Revit\Addins\{version}\revit-mcp-extra-panel.json</c>:
/// <code>{ "url": "http://127.0.0.1:9000/ui/", "tab": "My Tool", "label": "My Tool" }</code>
/// Unlike <see cref="PanelConfig"/> (AutoAudit, always on), this pane is opt-in: no file, no
/// usable <c>url</c>, or <c>"enabled": false</c> means neither the pane nor its ribbon tab is
/// ever registered. The add-in ships nothing for the pane to show — it is only a browser onto
/// whatever local service the file names, so which tool sits behind it is the installing user's
/// business, not this repo's.
/// </summary>
internal static class ExtraPanelConfig
{
    internal const string DefaultLabel = "Extra Panel";

    internal static string ConfigPath(string revitVersion) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "Revit", "Addins", revitVersion, "revit-mcp-extra-panel.json");

    /// <summary>Null means "do not register the pane".</summary>
    internal static ExtraPanelSettings? Resolve(string revitVersion)
    {
        try
        {
            var path = ConfigPath(revitVersion);
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("enabled", out var enabled) &&
                enabled.ValueKind == JsonValueKind.False)
                return null;

            var url = ReadString(root, "url");
            if (url is null ||
                !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                return null;

            var label = ReadString(root, "label") ?? DefaultLabel;
            var tab = ReadString(root, "tab") ?? label;
            return new ExtraPanelSettings(url, tab, label);
        }
        catch
        {
            // Corrupt config must never take the panel (or Revit) down.
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }
}
