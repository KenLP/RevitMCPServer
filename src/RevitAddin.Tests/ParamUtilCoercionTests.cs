using System.Text.Json;
using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>
/// A caller that sends "5" where 5 was expected used to reach
/// JsonNode.GetValue&lt;T&gt;(), which threw InvalidOperationException. That escaped
/// the dispatcher's error mapping and surfaced as a bare HTTP 500 with no
/// {ok,error} envelope — measured against a live add-in on get_element_info,
/// get_element_geometry, list_elements, find_elements and query_where.
///
/// Two rules are locked in here:
///   1. A value that converts EXACTLY (numeric string, integral double) is
///      accepted — that conversion cannot produce a wrong answer.
///   2. Anything else raises "invalid_parameter", which McpHttpServer maps to
///      400, naming the key and the expected type so the caller can fix itself.
///      It is never swallowed into null: silently dropping e.g. view_id would
///      widen a view-scoped query to the whole document with no warning.
/// </summary>
public class ParamUtilCoercionTests
{
    // Params arrive parsed from HTTP, so build nodes the same way rather than
    // via JsonValue.Create — the backing store differs and so does conversion.
    private static JsonObject Parse(string json) =>
        (JsonObject)JsonNode.Parse(json)!;

    private static RevitCommandException Throws(System.Action act) =>
        Assert.Throws<RevitCommandException>(act);

    // ── accepted: exact conversions ──────────────────────────────────────────

    [Fact]
    public void Long_accepts_numeric_string()
        => Assert.Equal(619404L, P.Long(Parse("""{"id":"619404"}"""), "id"));

    [Fact]
    public void Long_accepts_integral_double()
        => Assert.Equal(5L, P.Long(Parse("""{"id":5.0}"""), "id"));

    [Fact]
    public void Long_accepts_plain_number()
        => Assert.Equal(619404L, P.Long(Parse("""{"id":619404}"""), "id"));

    [Fact]
    public void Int_accepts_numeric_string()
        => Assert.Equal(10, P.IntOr(Parse("""{"limit":"10"}"""), "limit", 200));

    [Fact]
    public void Dbl_accepts_numeric_string()
        => Assert.Equal(2.5, P.Dbl(Parse("""{"w":"2.5"}"""), "w"));

    [Fact]
    public void BoolOr_accepts_boolean_string()
        => Assert.True(P.BoolOr(Parse("""{"atomic":"true"}"""), "atomic", false));

    [Fact]
    public void Str_accepts_a_number_as_its_text()
        => Assert.Equal("101", P.Str(Parse("""{"mark":101}"""), "mark"));

    [Fact]
    public void LongOrNull_accepts_numeric_string()
        => Assert.Equal(850623L, P.LongOrNull(Parse("""{"view_id":"850623"}"""), "view_id"));

    // ── rejected: invalid_parameter, never a silent default ─────────────────

    [Fact]
    public void Long_rejects_non_numeric_string_as_invalid_parameter()
    {
        var ex = Throws(() => P.Long(Parse("""{"id":"abc"}"""), "id"));
        Assert.Equal("invalid_parameter", ex.Code);
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public void Long_rejects_fractional_double()
        => Assert.Equal("invalid_parameter",
                        Throws(() => P.Long(Parse("""{"id":5.5}"""), "id")).Code);

    [Fact]
    public void Long_rejects_boolean()
        => Assert.Equal("invalid_parameter",
                        Throws(() => P.Long(Parse("""{"id":true}"""), "id")).Code);

    [Fact]
    public void Long_rejects_object()
        => Assert.Equal("invalid_parameter",
                        Throws(() => P.Long(Parse("""{"id":{"a":1}}"""), "id")).Code);

    [Fact]
    public void Dbl_rejects_non_numeric_string()
        => Assert.Equal("invalid_parameter",
                        Throws(() => P.Dbl(Parse("""{"w":"wide"}"""), "w")).Code);

    [Fact]
    public void BoolOr_rejects_non_boolean_string()
        => Assert.Equal("invalid_parameter",
                        Throws(() => P.BoolOr(Parse("""{"atomic":"yes"}"""), "atomic", false)).Code);

    [Fact]
    public void Int_rejects_value_outside_int_range()
        => Assert.Equal("invalid_parameter",
                        Throws(() => P.Int(Parse("""{"n":9999999999}"""), "n")).Code);

    /// <summary>The whole point: a mistyped optional must NOT become "absent".</summary>
    [Fact]
    public void LongOrNull_rejects_garbage_instead_of_returning_null()
        => Assert.Equal("invalid_parameter",
                        Throws(() => P.LongOrNull(Parse("""{"view_id":"not-an-id"}"""), "view_id")).Code);

    [Fact]
    public void IntOr_rejects_garbage_instead_of_using_the_default()
        => Assert.Equal("invalid_parameter",
                        Throws(() => P.IntOr(Parse("""{"limit":"lots"}"""), "limit", 200)).Code);

    // ── absent stays absent ──────────────────────────────────────────────────

    [Fact]
    public void LongOrNull_returns_null_when_key_absent()
        => Assert.Null(P.LongOrNull(Parse("""{}"""), "view_id"));

    [Fact]
    public void IntOr_uses_default_when_key_absent()
        => Assert.Equal(200, P.IntOr(Parse("""{}"""), "limit", 200));

    [Fact]
    public void Long_still_reports_bad_request_when_key_missing()
        => Assert.Equal("bad_request", Throws(() => P.Long(Parse("""{}"""), "id")).Code);
}
