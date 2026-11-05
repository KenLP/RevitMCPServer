using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCPAddin.Commands;

namespace RevitMCPAddin;

/// <summary>One failure Revit posted while an MCP transaction was being committed.</summary>
internal sealed record CommitFailure(bool IsError, string Message, string? FailureId, string? Resolution,
    IReadOnlyList<long> ElementIds);

/// <summary>
/// Reports what Revit raised at commit back to the caller that caused it. Without this a warning
/// ("Multiple Rooms are in the same enclosed region") or an error opens a modal box on the UI thread:
/// every queued MCP call waits until a human clicks it, and the caller never learns what it said.
/// A warning is dismissed — what clicking OK does, the change stays — and returned in
/// <c>revitWarnings</c>. An error cannot be OK'd, so the transaction rolls back and the call fails with
/// <c>commit_failed</c>. A resolution Revit offers (e.g. "Delete Room(s)") is named, never applied:
/// choosing it changes the model, which is the caller's decision.
/// </summary>
internal static class CommitFailures
{
    /// <summary>Adds <c>revitWarnings</c> to <paramref name="target"/>; a clean commit adds nothing.</summary>
    public static void Annotate(JsonObject target, IReadOnlyList<CommitFailure> failures)
    {
        var warnings = failures.Where(f => !f.IsError).ToList();
        if (warnings.Count > 0) target["revitWarnings"] = ToJson(warnings);
    }

    /// <summary>The error envelope of a commit Revit rolled back: nothing changed in the model.</summary>
    public static JsonObject Rejected(string status, IReadOnlyList<CommitFailure> failures)
    {
        var errors = failures.Where(f => f.IsError).ToList();
        var reason = errors.Count > 0
            ? string.Join(" | ", errors.Select(e => e.Message).Distinct())
            : $"Revit did not commit the transaction (status {status}).";
        var result = JsonResult.Error("commit_failed", $"Rolled back, the model is unchanged: {reason}");
        var data = new JsonObject { ["revitErrors"] = ToJson(errors) };
        Annotate(data, failures);
        result["data"] = data;
        return result;
    }

    /// <summary>A resolution caption as a person reads it: Revit's has the accelerator, "Delete &amp;Room(s)".</summary>
    public static string? Caption(string? raw) => raw?.Replace("&", "");

    private static JsonArray ToJson(IEnumerable<CommitFailure> items) => new(items
        .GroupBy(f => (f.Message, f.FailureId))
        .Select(g =>
        {
            var entry = new JsonObject
            {
                ["message"] = g.Key.Message,
                ["count"] = g.Count(),
                ["elementIds"] = new JsonArray(g.SelectMany(f => f.ElementIds).Distinct()
                    .Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
            };
            if (g.Key.FailureId is not null) entry["failureId"] = g.Key.FailureId;
            var resolution = g.Select(f => f.Resolution).FirstOrDefault(r => r is not null);
            if (resolution is not null) entry["resolution"] = resolution;
            return (JsonNode)entry;
        })
        .ToArray());
}

/// <summary>
/// Records every failure posted at commit. Warnings are deleted so the commit goes on; any error rolls
/// the transaction back silently (ProceedWithRollBack) — never a modal dialog.
/// </summary>
internal sealed class CommitFailureRecorder : IFailuresPreprocessor
{
    public List<CommitFailure> Failures { get; } = new();

    public void Install(Transaction tx)
    {
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(this);
        // A rollback with failures still posted would otherwise show them in a dialog.
        options.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(options);
    }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        var hasError = false;
        foreach (var msg in accessor.GetFailureMessages())
        {
            var isError = msg.GetSeverity() != FailureSeverity.Warning;
            hasError |= isError;
            Failures.Add(Read(msg, isError));
            if (!isError) accessor.DeleteWarning(msg);
        }
        return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }

    private static CommitFailure Read(FailureMessageAccessor msg, bool isError)
    {
        var ids = msg.GetFailingElementIds().Concat(msg.GetAdditionalElementIds()).Select(id => id.Value).ToList();
        return new CommitFailure(isError, msg.GetDescriptionText(), msg.GetFailureDefinitionId().Guid.ToString(),
            msg.HasResolutions() ? CommitFailures.Caption(msg.GetDefaultResolutionCaption()) : null, ids);
    }
}
