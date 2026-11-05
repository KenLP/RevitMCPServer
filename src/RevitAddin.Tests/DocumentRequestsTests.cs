using System;
using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using RevitMCPAddin.Server;
using Xunit;

namespace RevitMCPAddin.Tests;

public class DocumentRequestsTests
{
    private static readonly Func<string, bool> Yes = _ => true;
    private static readonly Func<string, bool> No = _ => false;
    private static readonly string[] Versions = { "IFC2x3", "IFC4", "IFC4x3", "Default" };

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Code(Action act) => Assert.Throws<RevitCommandException>(act).Code;

    [Theory]
    [InlineData(typeof(SaveDocumentCommand), "save_document", "high")]
    [InlineData(typeof(SaveAsCommand), "save_as", "high")]
    [InlineData(typeof(CloseDocumentCommand), "close_document", "medium")]
    [InlineData(typeof(NewDocumentCommand), "new_document", "medium")]
    [InlineData(typeof(ExportIfcCommand), "export_ifc", "low")]
    public void Commands_are_ui_actions_so_a_dry_run_writes_nothing(Type type, string name, string risk)
    {
        var cmd = (IRevitCommand)Activator.CreateInstance(type)!;
        Assert.Equal(name, cmd.Name);
        Assert.Equal(ExecutionKind.UiAction, cmd.Execution);
        Assert.False(cmd.IsReadOnly);
        Assert.Equal(risk, cmd.RiskLevel);
    }

    [Theory]
    [InlineData("not_supported")]
    [InlineData("never_saved")]
    [InlineData("already_exists")]
    [InlineData("active_document")]
    public void Refusals_map_to_409(string code)
        => Assert.Equal(409, McpHttpServer.StatusForResult(JsonResult.Error(code, "x")));

    [Theory]
    [InlineData("save_failed")]
    [InlineData("close_failed")]
    [InlineData("create_failed")]
    [InlineData("export_failed")]
    public void Failures_map_to_500(string code)
        => Assert.Equal(500, McpHttpServer.StatusForResult(JsonResult.Error(code, "x")));

    // ── save_as ──────────────────────────────────────────────────────────────

    [Fact]
    public void Save_as_defaults_to_no_overwrite_and_one_backup()
    {
        var r = SaveAsRequest.From(Json("""{"path":"C:\\out\\a.rvt"}"""), No, Yes);
        Assert.Equal(@"C:\out\a.rvt", r.Path);
        Assert.False(r.Overwrite);
        Assert.Equal(1, r.MaxBackups);
    }

    [Fact]
    public void Save_as_accepts_a_file_that_does_not_exist_yet()
        => Assert.Equal(@"C:\out\new.rvt", SaveAsRequest.From(Json("""{"path":"C:\\out\\new.rvt"}"""), No, Yes).Path);

    [Fact]
    public void Save_as_needs_a_path() => Assert.Equal("invalid_parameter", Code(() => SaveAsRequest.From(Json("{}"), No, Yes)));

    [Fact]
    public void Save_as_needs_the_rvt_extension()
        => Assert.Equal("invalid_parameter", Code(() => SaveAsRequest.From(Json("""{"path":"C:\\out\\a.txt"}"""), No, Yes)));

    [Fact]
    public void Save_as_extension_ignores_case()
        => Assert.Equal(@"C:\out\A.RVT", SaveAsRequest.From(Json("""{"path":"C:\\out\\A.RVT"}"""), No, Yes).Path);

    [Fact]
    public void Save_as_needs_the_folder()
        => Assert.Equal("not_found", Code(() => SaveAsRequest.From(Json("""{"path":"C:\\nope\\a.rvt"}"""), No, No)));

    [Fact]
    public void Save_as_refuses_an_existing_file_unless_told()
    {
        var json = """{"path":"C:\\out\\a.rvt"}""";
        Assert.Equal("already_exists", Code(() => SaveAsRequest.From(Json(json), Yes, Yes)));
        Assert.True(SaveAsRequest.From(Json("""{"path":"C:\\out\\a.rvt","overwrite":true}"""), Yes, Yes).Overwrite);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Save_as_refuses_a_backup_count_below_one(int n)
        => Assert.Equal("invalid_parameter", Code(() => SaveAsRequest.From(
            new JsonObject { ["path"] = @"C:\out\a.rvt", ["maxBackups"] = n }, No, Yes)));

    // ── close_document ───────────────────────────────────────────────────────

    [Fact]
    public void Close_has_no_default_target() => Assert.Equal("invalid_parameter", Code(() => CloseRequest.From(Json("{}"))));

    [Fact]
    public void Close_defaults_to_not_saving()
    {
        var r = CloseRequest.From(Json("""{"path":"C:\\out\\gone-from-disk.rvt"}"""));
        Assert.Equal(@"C:\out\gone-from-disk.rvt", r.Path);
        Assert.False(r.Save);
    }

    [Fact]
    public void Close_path_matches_an_open_document_however_it_was_written()
    {
        var r = CloseRequest.From(Json("""{"path":" C:\\out\\sub\\..\\a.rvt "}"""));
        Assert.True(OpenRequest.SamePath(@"c:\OUT\a.rvt", r.Path));
    }

    // ── new_document ─────────────────────────────────────────────────────────

    [Fact]
    public void New_document_needs_template_and_path()
    {
        Assert.Equal("invalid_parameter", Code(() => NewDocumentRequest.From(Json("""{"path":"C:\\o\\a.rvt"}"""), Yes, Yes)));
        Assert.Equal("invalid_parameter", Code(() => NewDocumentRequest.From(Json("""{"template":"C:\\t.rte"}"""), Yes, Yes)));
    }

    [Fact]
    public void New_document_reports_a_missing_template()
        => Assert.Equal("not_found", Code(() => NewDocumentRequest.From(
            Json("""{"template":"C:\\t.rte","path":"C:\\o\\a.rvt"}"""), No, Yes)));

    [Fact]
    public void New_document_refuses_an_existing_target_unless_told()
    {
        var json = """{"template":"C:\\t.rte","path":"C:\\o\\a.rvt"}""";
        Assert.Equal("already_exists", Code(() => NewDocumentRequest.From(Json(json), Yes, Yes)));
        Assert.True(NewDocumentRequest.From(Json("""{"template":"C:\\t.rte","path":"C:\\o\\a.rvt","overwrite":true}"""), Yes, Yes).Overwrite);
    }

    // ── export_ifc ───────────────────────────────────────────────────────────

    [Fact]
    public void Export_defaults()
    {
        var r = ExportIfcRequest.From(Json("""{"outputPath":"C:\\o\\m.ifc"}"""), Versions, Yes, Yes);
        Assert.Equal(@"C:\o\m.ifc", r.OutputPath);
        Assert.Equal("IFC4", r.Version);
        Assert.Null(r.ViewId);
        Assert.True(r.BaseQuantities);
        Assert.Equal(1, r.SpaceBoundaryLevel);
        Assert.False(r.WallAndColumnSplitting);
        Assert.True(r.Overwrite);
    }

    [Fact]
    public void Export_reads_every_param()
    {
        var r = ExportIfcRequest.From(Json("""
            {"outputPath":"C:\\o\\m.ifc","ifcVersion":"ifc2x3","viewId":12345,"exportBaseQuantities":false,
             "spaceBoundaryLevel":2,"wallAndColumnSplitting":true,"overwrite":false}
            """), Versions, No, Yes);
        Assert.Equal("IFC2x3", r.Version); // normalised to Revit's spelling
        Assert.Equal(12345, r.ViewId);
        Assert.False(r.BaseQuantities);
        Assert.Equal(2, r.SpaceBoundaryLevel);
        Assert.True(r.WallAndColumnSplitting);
        Assert.False(r.Overwrite);
    }

    [Fact]
    public void Export_checks_the_version_against_what_this_revit_defines()
    {
        var ex = Assert.Throws<RevitCommandException>(() => ExportIfcRequest.From(
            Json("""{"outputPath":"C:\\o\\m.ifc","ifcVersion":"IFC4x3RV"}"""), Versions, Yes, Yes));
        Assert.Equal("invalid_parameter", ex.Code);
        Assert.Contains("IFC4x3", ex.Message);
    }

    [Fact]
    public void Export_refuses_a_numeric_version_string()
        => Assert.Equal("invalid_parameter", Code(() => ExportIfcRequest.From(
            Json("""{"outputPath":"C:\\o\\m.ifc","ifcVersion":"3"}"""), Versions, Yes, Yes)));

    [Fact]
    public void Export_path_rules()
    {
        Assert.Equal("invalid_parameter", Code(() => ExportIfcRequest.From(Json("{}"), Versions, Yes, Yes)));
        Assert.Equal("invalid_parameter", Code(() => ExportIfcRequest.From(Json("""{"outputPath":"C:\\o\\m.rvt"}"""), Versions, Yes, Yes)));
        Assert.Equal("not_found", Code(() => ExportIfcRequest.From(Json("""{"outputPath":"C:\\o\\m.ifc"}"""), Versions, No, No)));
        Assert.Equal("already_exists", Code(() => ExportIfcRequest.From(Json("""{"outputPath":"C:\\o\\m.ifc","overwrite":false}"""), Versions, Yes, Yes)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Export_refuses_an_unknown_boundary_level(int level)
        => Assert.Equal("invalid_parameter", Code(() => ExportIfcRequest.From(
            new JsonObject { ["outputPath"] = @"C:\o\m.ifc", ["spaceBoundaryLevel"] = level }, Versions, Yes, Yes)));
}
