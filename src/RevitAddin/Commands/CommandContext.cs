using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Everything a command needs at execution time.  The dispatcher constructs
/// one of these per request and hands it to <see cref="IRevitCommand.Execute"/>.
///
/// Commands MUST NOT manage their own <see cref="Transaction"/>.  The dispatcher
/// opens a single transaction per single-command call, and a single transaction
/// per batch call — this is what makes the batch transaction pattern possible.
/// </summary>
public sealed class CommandContext
{
    public required UIApplication App { get; init; }
    public required JsonObject Parameters { get; init; }

    /// <summary>
    /// May be null if no project is open.  Use <see cref="RequireDoc"/> when
    /// the command needs an active document.
    /// </summary>
    public Document? Doc { get; init; }

    /// <summary>When true, the dispatcher will roll back the transaction after execution.</summary>
    public bool DryRun { get; init; }

    public Document RequireDoc() =>
        Doc ?? throw new InvalidOperationException(
            "This command requires an active Revit document, but none is open.");

    public UIDocument RequireUIDoc() =>
        App.ActiveUIDocument ?? throw new InvalidOperationException(
            "This command requires an active UI document, but none is open.");
}
