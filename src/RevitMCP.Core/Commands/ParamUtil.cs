using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Tiny helper layer for plucking strongly-typed values out of the loosely-typed
/// JsonObject parameters dictionary.  Centralises "missing required parameter"
/// error messages so every command reports them consistently.
/// </summary>
public static class P
{
    // ── Coercion layer ────────────────────────────────────────────────────
    // JsonNode.GetValue<T>() throws InvalidOperationException on a type
    // mismatch. That escapes the dispatcher's error mapping and reaches the
    // caller as a bare HTTP 500 with no {ok,error} envelope — so a client that
    // sent "5" where 5 was wanted learns nothing about what went wrong, and an
    // LLM caller cannot self-correct. Everything below funnels through these
    // helpers so a mismatch becomes "invalid_parameter", which McpHttpServer
    // maps to 400 and reports with the offending key and the expected type.
    //
    // Numeric strings ("5") and integral doubles (5.0) are accepted: those are
    // unambiguous and lossless, and rejecting them buys nothing. Anything that
    // does not convert exactly is rejected rather than guessed at.

    private static RevitCommandException Invalid(string key, string expected, JsonNode? node)
    {
        var got = node is null ? "null" : node.ToJsonString();
        if (got.Length > 60) got = got.Substring(0, 60) + "...";
        return new RevitCommandException("invalid_parameter",
            $"Parameter '{key}' must be {expected}, got {got}.");
    }

    private static string AsString(JsonNode node, string key)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            return v.ToString();          // number / bool -> its JSON text
        }
        throw Invalid(key, "a string", node);
    }

    private static double AsDouble(JsonNode node, string key)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) &&
                double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands,
                                CultureInfo.InvariantCulture, out d))
                return d;
        }
        throw Invalid(key, "a number", node);
    }

    private static long AsLong(JsonNode node, string key)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            // A JsonValue built in code (not parsed) can be int-backed.
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<string>(out var s) &&
                long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                return l;
            // 5.0 is an integer that happens to be written as a double; 5.5 is not.
            if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) &&
                d >= long.MinValue && d <= long.MaxValue)
                return (long)d;
        }
        throw Invalid(key, "an integer", node);
    }

    private static int AsInt(JsonNode node, string key)
    {
        var l = AsLong(node, key);
        if (l < int.MinValue || l > int.MaxValue)
            throw Invalid(key, $"an integer within {int.MinValue}..{int.MaxValue}", node);
        return (int)l;
    }

    private static bool AsBool(JsonNode node, string key)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
        }
        throw Invalid(key, "a boolean", node);
    }

    private static JsonNode Require(JsonObject obj, string key) =>
        obj[key] ?? throw new RevitCommandException(
            "bad_request", $"Missing required parameter '{key}'.");

    // ── Public accessors ──────────────────────────────────────────────────

    public static string Str(JsonObject obj, string key) =>
        AsString(Require(obj, key), key);

    public static string? StrOrNull(JsonObject obj, string key) =>
        obj[key] is { } n ? AsString(n, key) : null;

    public static double Dbl(JsonObject obj, string key) =>
        AsDouble(Require(obj, key), key);

    public static double DblOr(JsonObject obj, string key, double @default) =>
        obj[key] is { } n ? AsDouble(n, key) : @default;

    public static int Int(JsonObject obj, string key) =>
        AsInt(Require(obj, key), key);

    public static int IntOr(JsonObject obj, string key, int @default) =>
        obj[key] is { } n ? AsInt(n, key) : @default;

    /// <summary>
    /// Reads an RGB channel value, validating it is within 0-255.  Throws a
    /// clear error instead of silently wrapping when a caller passes e.g. 300.
    /// </summary>
    public static byte ColorByte(JsonObject obj, string key, int @default)
    {
        var v = IntOr(obj, key, @default);
        if (v < 0 || v > 255)
            throw new RevitCommandException("bad_request",
                $"Color channel '{key}' must be 0-255, got {v}.");
        return (byte)v;
    }

    public static long Long(JsonObject obj, string key) =>
        AsLong(Require(obj, key), key);

    public static long? LongOrNull(JsonObject obj, string key) =>
        obj[key] is { } n ? AsLong(n, key) : null;

    /// <summary>
    /// Coerce one element of an array (or any bare node) to long with the same
    /// rules as <see cref="Long"/>. Array elements used to call GetValue&lt;long&gt;()
    /// directly, which throws past the error mapping and reaches callers as a
    /// bare 500 — the same defect fixed for object keys in 0.8.28.
    /// <paramref name="label"/> names the source in the error, e.g. "ids[2]".
    /// </summary>
    public static long LongFrom(JsonNode? node, string label) =>
        node is null
            ? throw new RevitCommandException("invalid_parameter", $"'{label}' is null.")
            : AsLong(node, label);

    public static bool BoolOr(JsonObject obj, string key, bool @default) =>
        obj[key] is { } n ? AsBool(n, key) : @default;

    public static JsonObject Obj(JsonObject obj, string key)
    {
        var node = obj[key] as JsonObject
            ?? throw new RevitCommandException("bad_request", $"Missing required object parameter '{key}'.");
        return node;
    }

    public static JsonArray Arr(JsonObject obj, string key)
    {
        var node = obj[key] as JsonArray
            ?? throw new RevitCommandException("bad_request", $"Missing required array parameter '{key}'.");
        return node;
    }

    /// <summary>
    /// Reads a {x, y, z} object and converts from the user's units to Revit's
    /// internal feet.  Default units are meters; pass "feet" to skip the
    /// conversion.
    /// </summary>
    public static XYZ Xyz(JsonObject parent, string key, string units = "meters")
    {
        var obj = Obj(parent, key);
        var scale = units.Equals("feet", StringComparison.OrdinalIgnoreCase)
            ? 1.0
            : MetersToFeet;
        return new XYZ(
            DblOr(obj, "x", 0) * scale,
            DblOr(obj, "y", 0) * scale,
            DblOr(obj, "z", 0) * scale);
    }

    /// <summary>
    /// Reads an array of {x, y, z} objects, useful for polyline / profile
    /// inputs (e.g. floor profile).
    /// </summary>
    public static IList<XYZ> XyzList(JsonObject parent, string key, string units = "meters")
    {
        var arr = Arr(parent, key);
        var scale = units.Equals("feet", StringComparison.OrdinalIgnoreCase)
            ? 1.0
            : MetersToFeet;
        var list = new List<XYZ>(arr.Count);
        foreach (var node in arr)
        {
            if (node is not JsonObject obj)
                throw new RevitCommandException("bad_request", $"Each entry of '{key}' must be a {{x,y,z}} object.");
            list.Add(new XYZ(
                DblOr(obj, "x", 0) * scale,
                DblOr(obj, "y", 0) * scale,
                DblOr(obj, "z", 0) * scale));
        }
        return list;
    }

    public const double MetersToFeet = 1.0 / 0.3048;
    public const double FeetToMeters = 0.3048;

    public static string Units(JsonObject parameters) =>
        StrOrNull(parameters, "units")?.ToLowerInvariant() ?? "meters";
}
