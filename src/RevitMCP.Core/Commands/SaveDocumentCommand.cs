using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Save the active document to its own file. A UI action: Revit refuses to save while the API holds a
/// transaction, so this runs outside one, and a dry-run does nothing.
///
/// Local, non-workshared files only.
/// </summary>
public sealed class SaveDocumentCommand : IRevitCommand
{
    public string Name => "save_document";
    public bool IsReadOnly => false;
    public string RiskLevel => "high"; // overwrites a file on disk
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        DocumentSaving.RequireLocal(doc);
        if (string.IsNullOrEmpty(doc.PathName))
            throw new RevitCommandException("never_saved",
                "This document has never been saved, so it has no file yet. Use save_as.");

        using var watch = DialogWatch.Start(ctx);
        var sw = Stopwatch.StartNew();
        try
        {
            doc.Save();
        }
        catch (Exception ex) when (ex is not RevitCommandException)
        {
            throw DocumentSaving.Failed("save_failed", "save the document", ex, watch);
        }
        sw.Stop();

        return new JsonObject
        {
            ["title"] = doc.Title,
            ["pathName"] = doc.PathName,
            ["saveMs"] = sw.ElapsedMilliseconds,
            ["dialogs"] = watch.EventsJson(),
        };
    }
}

/// <summary>Rules shared by the document lifecycle commands.</summary>
internal static class DocumentSaving
{
    /// <summary>Workshared and cloud models have their own save semantics, which these commands do not cover.</summary>
    internal static void RequireLocal(Document doc)
    {
        if (doc.IsWorkshared || doc.IsModelInCloud)
            throw new RevitCommandException("not_supported",
                "This command supports local, non-workshared files only; this document is workshared or in the cloud.");
    }

    internal static RevitCommandException Failed(string code, string action, Exception ex, DialogWatch watch) =>
        new(code, $"Revit could not {action} ({ex.GetType().Name}): {ex.Message}{watch.ErrorSuffix()}");
}
