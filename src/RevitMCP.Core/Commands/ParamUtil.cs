using System;
using System.Collections.Generic;
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
    public static string Str(JsonObject obj, string key)
    {
        var node = obj[key]
            ?? throw new RevitCommandException("bad_request", $"Missing required parameter '{key}'.");
        return node.GetValue<string>();
    }

    public static string? StrOrNull(JsonObject obj, string key) =>
        obj[key]?.GetValue<string>();

    public static double Dbl(JsonObject obj, string key)
    {
        var node = obj[key]
            ?? throw new RevitCommandException("bad_request", $"Missing required parameter '{key}'.");
        return node.GetValue<double>();
    }

    public static double DblOr(JsonObject obj, string key, double @default) =>
        obj[key]?.GetValue<double>() ?? @default;

    public static int Int(JsonObject obj, string key)
    {
        var node = obj[key]
            ?? throw new RevitCommandException("bad_request", $"Missing required parameter '{key}'.");
        return node.GetValue<int>();
    }

    public static int IntOr(JsonObject obj, string key, int @default) =>
        obj[key]?.GetValue<int>() ?? @default;

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

    public static long Long(JsonObject obj, string key)
    {
        var node = obj[key]
            ?? throw new RevitCommandException("bad_request", $"Missing required parameter '{key}'.");
        return node.GetValue<long>();
    }

    public static long? LongOrNull(JsonObject obj, string key) =>
        obj[key] is { } node ? node.GetValue<long>() : null;

    public static bool BoolOr(JsonObject obj, string key, bool @default) =>
        obj[key]?.GetValue<bool>() ?? @default;

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
