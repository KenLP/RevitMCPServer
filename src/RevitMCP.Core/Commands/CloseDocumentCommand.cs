using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Close an open document that is not the active one. The Revit API cannot close the active document,
/// so a caller opens the next model first; to end a session, stop Revit by process id.
///
/// Params:
///   - path: string, required — the document to close; there is no default to the active document
///   - save: bool, default false — save changes first
///
/// Idempotent: a path that is not open returns closed:false, wasOpen:false.
/// Local, non-workshared files only.
/// </summary>
public sealed class CloseDocumentCommand : IRevitCommand
{
    public string Name => "close_document";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var request = CloseRequest.From(ctx.Parameters);
        var target = ctx.App.Application.Documents.Cast<Document>()
            .FirstOrDefault(d => !d.IsLinked && OpenRequest.SamePath(d.PathName, request.Path));
        if (target is null)
            return Result(request.Path, closed: false, wasOpen: false, saved: false, new JsonArray());

        if (ctx.App.ActiveUIDocument?.Document is { } active && active.Equals(target))
            throw new RevitCommandException("active_document",
                "Revit's API cannot close the active document. Use open_document on another model first; " +
                "to end the session, stop Revit by process id.");
        DocumentSaving.RequireLocal(target);

        var saved = request.Save && target.IsModified;
        using var watch = DialogWatch.Start(ctx);
        bool closed;
        try
        {
            closed = target.Close(request.Save);
        }
        catch (Exception ex) when (ex is not RevitCommandException)
        {
            throw DocumentSaving.Failed("close_failed", "close the document", ex, watch);
        }
        if (!closed)
            throw new RevitCommandException("close_failed",
                $"Revit did not close the document: saving failed or an add-in cancelled the close.{watch.ErrorSuffix()}");

        return Result(request.Path, closed: true, wasOpen: true, saved, watch.EventsJson());
    }

    private static JsonObject Result(string path, bool closed, bool wasOpen, bool saved, JsonArray dialogs) => new()
    {
        ["pathName"] = path,
        ["closed"] = closed,
        ["wasOpen"] = wasOpen,
        ["saved"] = saved,
        ["dialogs"] = dialogs,
    };
}
