using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Shared, deterministic support for the where-based query/update commands.
///
/// Two reliability cornerstones live here:
///   1. <see cref="ResolveParam"/> reads a parameter at the correct SCOPE —
///      instance first, then the element's TYPE (via GetTypeId). Many params
///      (e.g. "Fire Rating") live on the type; instance-only lookups return null
///      and silently match nothing.
///   2. <see cref="Compare"/> is the single, predictable operator set (eq, contains,
///      ends_with, regex, gt, is_empty, …) matching against the DISPLAY value for
///      text and the RAW number for numeric compares.
/// </summary>
public static class WhereSupport
{
    public sealed record WhereCond(string Param, string Op, JsonNode? Value, string Scope);

    // ── category ─────────────────────────────────────────────────────────────

    public static BuiltInCategory ResolveCategory(string name)
    {
        if (Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var bic))
            return bic;
        throw new RevitCommandException("invalid_parameter", $"Unknown BuiltInCategory '{name}'.");
    }

    public static IList<Element> CollectInstances(Document doc, BuiltInCategory bic, long? viewId = null)
    {
        var collector = viewId.HasValue && viewId.Value > 0
            ? new FilteredElementCollector(doc, new ElementId(viewId.Value))
            : new FilteredElementCollector(doc);
        return collector.OfCategory(bic).WhereElementIsNotElementType().ToElements();
    }

    // ── where parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a where array. Tolerant of the key/operator variants small models emit
    /// (parameter|parameterName|param|name|field, operator|op|Operator, value|Value,
    /// and symbol operators "="/">"/"!=").
    /// </summary>
    public static List<WhereCond> ParseWhere(JsonArray? where)
    {
        var list = new List<WhereCond>();
        if (where is null) return list;
        foreach (var w in where)
        {
            if (w is not JsonObject o) continue;
            var param = StrAny(o, "parameter", "parameterName", "param", "name", "field");
            if (string.IsNullOrWhiteSpace(param)) continue;
            var op = NormalizeOp((StrAny(o, "operator", "op") ?? "eq").Trim().ToLowerInvariant());
            var scope = (StrAny(o, "scope") ?? "auto").Trim().ToLowerInvariant();
            var value = o["value"] ?? o["Value"];
            list.Add(new WhereCond(param!, op, value, scope));
        }
        return list;
    }

    // ── scope-aware parameter resolution ──────────────────────────────────────

    /// <summary>
    /// Resolve a parameter on an element honouring scope. Returns the Parameter and
    /// where it was found ("instance" | "type" | null). scope: auto|instance|type.
    /// </summary>
    public static (Parameter? Param, string? Scope) ResolveParam(
        Document doc, Element el, string name, string scope)
    {
        if (scope is "instance" or "auto")
        {
            var ip = el.LookupParameter(name);
            if (ip != null) return (ip, "instance");
        }
        if (scope is "type" or "auto")
        {
            var typeId = el.GetTypeId();
            if (typeId != ElementId.InvalidElementId &&
                doc.GetElement(typeId) is Element t && t.LookupParameter(name) is { } tp)
                return (tp, "type");
        }
        return (null, null);
    }

    public static bool Matches(Document doc, Element el, IReadOnlyList<WhereCond> conds)
    {
        foreach (var c in conds)
        {
            var (p, _) = ResolveParam(doc, el, c.Param, c.Scope);
            if (!Compare(p, c.Op, c.Value)) return false;   // AND
        }
        return true;
    }

    // ── comparison ─────────────────────────────────────────────────────────────

    public static bool Compare(Parameter? p, string op, JsonNode? value)
    {
        var text = GetText(p);                 // null = absent / no value
        var valStr = AsStr(value) ?? "";

        switch (op)
        {
            case "is_empty": return string.IsNullOrWhiteSpace(text);
            case "not_empty": return !string.IsNullOrWhiteSpace(text);
            case "regex": return text is not null && SafeRegex(text, valStr);
            case "not_regex": return text is null || !SafeRegex(text, valStr);
        }

        if (text is null) return false;        // remaining ops never match an absent value (neq incl.)

        switch (op)
        {
            case "eq":
            {
                var (a, b) = (GetNum(p), TryDouble(value));
                if (a is not null && b is not null) return Math.Abs(a.Value - b.Value) < 1e-9;
                return string.Equals(text, valStr, StringComparison.OrdinalIgnoreCase);
            }
            case "neq":
            {
                var (a, b) = (GetNum(p), TryDouble(value));
                if (a is not null && b is not null) return Math.Abs(a.Value - b.Value) >= 1e-9;
                return !string.Equals(text, valStr, StringComparison.OrdinalIgnoreCase);
            }
            case "contains": return text.Contains(valStr, StringComparison.OrdinalIgnoreCase);
            case "starts_with": return text.StartsWith(valStr, StringComparison.OrdinalIgnoreCase);
            case "ends_with": return text.EndsWith(valStr, StringComparison.OrdinalIgnoreCase);
            case "gt": case "lt": case "gte": case "lte":
            {
                var a = GetNum(p); var b = TryDouble(value);
                if (a is null || b is null) return false;
                return op switch
                {
                    "gt" => a > b, "lt" => a < b, "gte" => a >= b, "lte" => a <= b, _ => false,
                };
            }
            default: return string.Equals(text, valStr, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeOp(string op) => op switch
    {
        "equals" => "eq",
        "not_equals" or "ne" => "neq",
        "greater" => "gt",
        "less" => "lt",
        "greater_equal" or "greater_than_or_equal" => "gte",
        "less_equal" or "less_than_or_equal" => "lte",
        "startswith" or "starts" => "starts_with",
        "endswith" or "ends" => "ends_with",
        "matches" or "regexp" => "regex",
        "not_match" or "notmatch" or "not_matches" or "notregex" => "not_regex",
        "empty" or "isempty" or "is_null" or "not_exists" or "notexists" => "is_empty",
        "notempty" or "isnotempty" or "exists" or "has_value" => "not_empty",
        "=" or "==" => "eq",
        "!=" or "<>" => "neq",
        ">" => "gt",
        "<" => "lt",
        ">=" => "gte",
        "<=" => "lte",
        _ => op,
    };

    // ── value access on a resolved Parameter ─────────────────────────────────

    /// <summary>Display text for matching ("60 MIN"): AsValueString first, then AsString.</summary>
    public static string? GetText(Parameter? p)
    {
        if (p is null || !p.HasValue) return null;
        string? s = null;
        try { s = p.AsValueString(); } catch { /* ignore */ }
        if (!string.IsNullOrEmpty(s)) return s;
        return p.StorageType switch
        {
            StorageType.String => p.AsString(),
            StorageType.Integer => p.AsInteger().ToString(CultureInfo.InvariantCulture),
            StorageType.Double => p.AsDouble().ToString(CultureInfo.InvariantCulture),
            StorageType.ElementId => p.AsElementId().Value.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    public static double? GetNum(Parameter? p)
    {
        if (p is null || !p.HasValue) return null;
        switch (p.StorageType)
        {
            case StorageType.Double: return p.AsDouble();
            case StorageType.Integer: return p.AsInteger();
        }
        var text = GetText(p);
        if (text is not null)
        {
            var m = Regex.Match(text, @"[-+]?\d+(?:[.,]\d+)?");
            if (m.Success && double.TryParse(m.Value.Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        return null;
    }

    private static bool SafeRegex(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }
        catch { return false; }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>First non-empty string among several candidate keys (case-insensitive).</summary>
    public static string? StrAny(JsonObject o, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (Str(o, k) is { } s && !string.IsNullOrWhiteSpace(s)) return s;
            // case-insensitive fallback
            foreach (var kv in o)
                if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase) &&
                    kv.Value is JsonValue jv && jv.TryGetValue<string>(out var s2) &&
                    !string.IsNullOrWhiteSpace(s2))
                    return s2;
        }
        return null;
    }

    private static string? AsStr(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return n.ToString();
    }

    private static double? TryDouble(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<double>(); } catch { }
        try { return n.GetValue<long>(); } catch { }
        try { return n.GetValue<int>(); } catch { }
        if (n is JsonValue v && v.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }
}
