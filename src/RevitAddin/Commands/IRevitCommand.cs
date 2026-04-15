using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

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

    JsonNode? Execute(CommandContext ctx);
}
