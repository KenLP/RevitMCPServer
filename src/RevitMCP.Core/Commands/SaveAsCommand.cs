using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Save the active document to a new file. Afterwards the open document IS that file.
///
/// Params:
///   - path:       string — target .rvt; its folder must exist
///   - overwrite:  bool, default false — replace an existing file
///   - maxBackups: int, default 1 — SaveAsOptions.MaximumBackups, so no chain of .0001.rvt builds up
///
/// Local, non-workshared files only.
/// </summary>
public sealed class SaveAsCommand : IRevitCommand
{
    public string Name => "save_as";
    public bool IsReadOnly => false;
    public string RiskLevel => "high"; // can overwrite a file on disk
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        DocumentSaving.RequireLocal(doc);
        var request = SaveAsRequest.From(ctx.Parameters);
        var overwritten = File.Exists(request.Path);

        using var watch = DialogWatch.Start(ctx);
        var sw = Stopwatch.StartNew();
        try
        {
            doc.SaveAs(request.Path, new SaveAsOptions
            {
                OverwriteExistingFile = request.Overwrite,
                MaximumBackups = request.MaxBackups,
            });
        }
        catch (Exception ex) when (ex is not RevitCommandException)
        {
            throw DocumentSaving.Failed("save_failed", $"save to {request.Path}", ex, watch);
        }
        sw.Stop();

        return new JsonObject
        {
            ["title"] = doc.Title,
            ["pathName"] = doc.PathName,
            ["overwritten"] = overwritten,
            ["saveMs"] = sw.ElapsedMilliseconds,
            ["dialogs"] = watch.EventsJson(),
        };
    }
}
