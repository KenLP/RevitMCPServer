using System;
using System.IO;
using System.Text.Json;

namespace RevitMCPAddin.Panel;

/// <summary>
/// Reads the Spatial-QC panel URL from
/// <c>%APPDATA%\Autodesk\Revit\Addins\{version}\revit-mcp-spatialqc-panel.json</c>
/// (<c>{ "url": "http://..." }</c>). Missing/corrupt file falls back to the local
/// <c>spatial-qc panel</c> service default. Sibling of <see cref="PanelConfig"/> (AutoAudit) so the
/// two panes point at independent services; flipping either is a config change, never an addin
/// change. The panel served there is ONLY a browser onto that URL.
/// </summary>
internal static class SpatialQcPanelConfig
{
    // `spatial-qc panel` (AutomatedSpatialQC) serves the panel UI here by default.
    internal const string DefaultUrl = "http://127.0.0.1:8602/ui/";

    internal static string ConfigPath(string revitVersion) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "Revit", "Addins", revitVersion, "revit-mcp-spatialqc-panel.json");

    internal static string ResolveUrl(string revitVersion)
    {
        try
        {
            var path = ConfigPath(revitVersion);
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("url", out var url) &&
                    url.ValueKind == JsonValueKind.String)
                {
                    var value = url.GetString();
                    if (!string.IsNullOrWhiteSpace(value) &&
                        Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
                        (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                        return value!;
                }
            }
        }
        catch
        {
            // Corrupt config must never take the panel (or Revit) down.
        }
        return DefaultUrl;
    }
}
