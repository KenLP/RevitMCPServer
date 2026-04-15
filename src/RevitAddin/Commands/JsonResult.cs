using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Tiny helper for the response envelope used everywhere.  Keeps every command
/// returning the same shape:  { ok: bool, data?: ..., error?: { code, message, type? } }.
/// </summary>
public static class JsonResult
{
    public static JsonObject Success(JsonNode? data) =>
        new()
        {
            ["ok"] = true,
            ["data"] = data
        };

    public static JsonObject Success() =>
        new() { ["ok"] = true };

    public static JsonObject Error(string code, string message, string? type = null)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        };
        if (type is not null) error["type"] = type;
        return new JsonObject { ["ok"] = false, ["error"] = error };
    }
}
