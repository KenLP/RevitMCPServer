using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a project from a template and make it the active document. Revit can only activate a saved
/// file, so the new document is saved to <c>path</c>, closed and reopened.
///
/// Params:
///   - template:  string — .rte file
///   - path:      string — target .rvt; its folder must exist
///   - overwrite: bool, default false — replace an existing file
/// </summary>
public sealed class NewDocumentCommand : IRevitCommand
{
    public string Name => "new_document";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var request = NewDocumentRequest.From(ctx.Parameters);

        using var watch = DialogWatch.Start(ctx);
        var sw = Stopwatch.StartNew();
        Document? created = null;
        Document active;
        try
        {
            created = ctx.App.Application.NewProjectDocument(request.Template);
            created.SaveAs(request.Path, new SaveAsOptions { OverwriteExistingFile = request.Overwrite, MaximumBackups = 1 });
            created.Close(false); // allowed: a new document is not active
            created = null;
            active = ctx.App.OpenAndActivateDocument(request.Path).Document;
        }
        catch (Exception ex) when (ex is not RevitCommandException)
        {
            try { created?.Close(false); } catch { }
            throw DocumentSaving.Failed("create_failed", "create the document", ex, watch);
        }
        sw.Stop();

        return new JsonObject
        {
            ["title"] = active.Title,
            ["pathName"] = active.PathName,
            ["createMs"] = sw.ElapsedMilliseconds,
            ["dialogs"] = watch.EventsJson(),
        };
    }
}
