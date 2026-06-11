using System;
using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

public class ParamUtilTests
{
    private static JsonObject J(string key, JsonNode? value)
    {
        var o = new JsonObject();
        o[key] = value;
        return o;
    }

    private static JsonObject Empty() => new();

    // ── Str ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Str_returns_value()
        => Assert.Equal("hello", P.Str(J("name", "hello"), "name"));

    [Fact]
    public void Str_throws_when_missing()
        => Assert.Throws<RevitCommandException>(() => P.Str(Empty(), "name"));

    [Fact]
    public void StrOrNull_returns_value_when_present()
        => Assert.Equal("abc", P.StrOrNull(J("x", "abc"), "x"));

    [Fact]
    public void StrOrNull_returns_null_when_absent()
        => Assert.Null(P.StrOrNull(Empty(), "x"));

    // ── Dbl ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dbl_returns_value()
        => Assert.Equal(3.14, P.Dbl(J("v", (JsonNode)3.14), "v"), 10);

    [Fact]
    public void Dbl_throws_when_missing()
        => Assert.Throws<RevitCommandException>(() => P.Dbl(Empty(), "v"));

    [Fact]
    public void DblOr_returns_value_when_present()
        => Assert.Equal(2.0, P.DblOr(J("v", (JsonNode)2.0), "v", 9.9), 10);

    [Fact]
    public void DblOr_returns_default_when_absent()
        => Assert.Equal(1.5, P.DblOr(Empty(), "v", 1.5), 10);

    // ── Int ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Int_returns_value()
        => Assert.Equal(42, P.Int(J("n", (JsonNode)42), "n"));

    [Fact]
    public void Int_throws_when_missing()
        => Assert.Throws<RevitCommandException>(() => P.Int(Empty(), "n"));

    [Fact]
    public void IntOr_returns_value_when_present()
        => Assert.Equal(5, P.IntOr(J("n", (JsonNode)5), "n", 0));

    [Fact]
    public void IntOr_returns_default_when_absent()
        => Assert.Equal(7, P.IntOr(Empty(), "n", 7));

    // ── ColorByte ────────────────────────────────────────────────────────────

    [Fact]
    public void ColorByte_valid_returns_byte()
        => Assert.Equal((byte)128, P.ColorByte(J("r", (JsonNode)128), "r", 0));

    [Fact]
    public void ColorByte_boundary_zero_is_valid()
        => Assert.Equal((byte)0, P.ColorByte(J("r", (JsonNode)0), "r", 255));

    [Fact]
    public void ColorByte_boundary_255_is_valid()
        => Assert.Equal((byte)255, P.ColorByte(J("r", (JsonNode)255), "r", 0));

    [Fact]
    public void ColorByte_over_255_throws()
    {
        var ex = Assert.Throws<RevitCommandException>(() =>
            P.ColorByte(J("r", (JsonNode)256), "r", 0));
        Assert.Equal("bad_request", ex.Code);
        Assert.Contains("0-255", ex.Message);
        Assert.Contains("256", ex.Message);
    }

    [Fact]
    public void ColorByte_negative_throws()
    {
        var ex = Assert.Throws<RevitCommandException>(() =>
            P.ColorByte(J("r", (JsonNode)(-1)), "r", 0));
        Assert.Contains("0-255", ex.Message);
    }

    [Fact]
    public void ColorByte_uses_default_when_absent()
        => Assert.Equal((byte)200, P.ColorByte(Empty(), "r", 200));

    // ── Long ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Long_returns_value()
        => Assert.Equal(123456789L, P.Long(J("id", (JsonNode)123456789L), "id"));

    [Fact]
    public void Long_throws_when_missing()
        => Assert.Throws<RevitCommandException>(() => P.Long(Empty(), "id"));

    // ── Bool ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BoolOr_returns_true_when_set()
        => Assert.True(P.BoolOr(J("flag", (JsonNode)true), "flag", false));

    [Fact]
    public void BoolOr_returns_false_default_when_absent()
        => Assert.False(P.BoolOr(Empty(), "flag", false));

    // ── Obj / Arr ────────────────────────────────────────────────────────────

    [Fact]
    public void Obj_returns_nested_object()
    {
        var inner = new JsonObject { ["a"] = 1 };
        var obj = P.Obj(J("inner", inner), "inner");
        Assert.Equal(1, obj["a"]?.GetValue<int>());
    }

    [Fact]
    public void Obj_throws_when_missing()
        => Assert.Throws<RevitCommandException>(() => P.Obj(Empty(), "inner"));

    [Fact]
    public void Arr_returns_array()
    {
        var arr = new JsonArray(1, 2, 3);
        var result = P.Arr(J("items", arr), "items");
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Arr_throws_when_missing()
        => Assert.Throws<RevitCommandException>(() => P.Arr(Empty(), "items"));

    // ── Constants & Units ────────────────────────────────────────────────────

    [Fact]
    public void MetersToFeet_is_correct()
        => Assert.Equal(1.0 / 0.3048, P.MetersToFeet, precision: 10);

    [Fact]
    public void FeetToMeters_is_correct()
        => Assert.Equal(0.3048, P.FeetToMeters, precision: 10);

    [Fact]
    public void Units_returns_meters_by_default()
        => Assert.Equal("meters", P.Units(Empty()));

    [Fact]
    public void Units_returns_provided_value_lowercased()
        => Assert.Equal("feet", P.Units(J("units", "FEET")));
}
