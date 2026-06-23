using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace RevitMCPAddin.Server;

/// <summary>
/// Minimal structured request logger — one JSON line per request, appended to a
/// daily file under <c>%LOCALAPPDATA%\RevitMCP\logs\</c>.  Best-effort: it must
/// never throw into the request path, so every failure is swallowed.
/// </summary>
internal static class RequestLog
{
    private static readonly object _lock = new();
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCP", "logs");

    /// <summary>The directory log files are written to (for diagnostics).</summary>
    public static string Directory => _dir;

    public static void Write(JsonObject entry)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            var file = Path.Combine(_dir, $"revit-mcp-{DateTime.Now:yyyy-MM-dd}.log");
            var line = entry.ToJsonString() + Environment.NewLine;
            lock (_lock)
                File.AppendAllText(file, line, Encoding.UTF8);
        }
        catch
        {
            // Logging must never break a request.
        }
    }
}
