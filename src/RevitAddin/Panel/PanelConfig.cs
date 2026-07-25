using System;
using System.IO;
using System.Text.Json;

namespace RevitMCPAddin.Panel;

/// <summary>
/// Reads the AutoAudit panel URL from
/// <c>%APPDATA%\Autodesk\Revit\Addins\{version}\revit-mcp-panel.json</c>
/// (<c>{ "url": "http://..." }</c>). Missing/corrupt file falls back to the
/// AuditHub service default. The panel is ONLY a browser pointing at that
/// URL — flipping it (e.g. back to the Streamlit console :8501) is a config
/// change, never an addin change.
/// </summary>
internal static class PanelConfig
{
    // AuditHub service (bim-orchestrator P3-2) serves the AutoAudit UI here.
    internal const string DefaultUrl = "http://127.0.0.1:8601/ui/";

    internal static string ConfigPath(string revitVersion) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "Revit", "Addins", revitVersion, "revit-mcp-panel.json");

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
