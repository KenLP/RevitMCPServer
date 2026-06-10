using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

/// <summary>
/// How a command must be executed, which drives the dispatcher's transaction
/// policy.
/// </summary>
public enum ExecutionKind
{
    /// <summary>Read-only — no transaction, no model or UI mutation.</summary>
    ReadOnly,

    /// <summary>Mutates the Revit model — must run inside a Transaction.</summary>
    ModelWrite,

    /// <summary>
    /// Mutates UI state only (active view, selection, zoom).  Runs on the UI
    /// thread but must NOT be wrapped in a model Transaction — some UI calls
    /// throw while a transaction is open, and a rollback cannot undo the UI
    /// effect anyway.
    /// </summary>
    UiAction,
}

/// <summary>
/// A single Revit command.  Implementations:
///
///   - Run on the Revit main UI thread (the dispatcher arranges that).
///   - MUST NOT open or commit their own <see cref="Autodesk.Revit.DB.Transaction"/>.
///     The dispatcher wraps every write call in a transaction, and a batch
///     call in a single transaction across all sub-commands.
///   - Return a <see cref="JsonNode"/> payload (or null) on success.  Throw on
///     failure — the dispatcher converts exceptions into the error envelope.
///
/// Set <see cref="IsReadOnly"/> to <c>true</c> for inspection-only commands so
/// the dispatcher skips the transaction wrap entirely.
/// </summary>
public interface IRevitCommand
{
    string Name { get; }
    bool IsReadOnly { get; }

    /// <summary>
    /// Risk classification surfaced via <c>GET /commands</c>.
    /// <list type="bullet">
    ///   <item><c>"read"</c> — read-only, no model changes.</item>
    ///   <item><c>"low"</c> — creates new elements (easily undoable).</item>
    ///   <item><c>"medium"</c> — modifies existing elements.</item>
    ///   <item><c>"high"</c> — deletes elements or hard-to-reverse changes.</item>
    /// </list>
    /// Default: <c>"read"</c> if <see cref="IsReadOnly"/>, else <c>"low"</c>.
    /// </summary>
    string RiskLevel => IsReadOnly ? "read" : "low";

    /// <summary>
    /// Execution classification driving the dispatcher's transaction policy.
    /// Defaults from <see cref="IsReadOnly"/> for backward compatibility:
    /// read-only → <see cref="ExecutionKind.ReadOnly"/>, otherwise
    /// <see cref="ExecutionKind.ModelWrite"/>.  UI-only commands
    /// (open_view, select_elements, zoom_to_elements) override this to
    /// <see cref="ExecutionKind.UiAction"/>.
    /// </summary>
    ExecutionKind Execution => IsReadOnly ? ExecutionKind.ReadOnly : ExecutionKind.ModelWrite;

    JsonNode? Execute(CommandContext ctx);
}
