using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Duplicate an existing view, preserving its view template, filters, and
/// graphic overrides (per the chosen duplicate option).  Useful for creating a
/// throwaway inspection copy of an already-filtered view before applying a
/// section box or isolate to it — the original stays untouched.
///
/// Params:
///   - viewId:          long, required — the source view to duplicate.
///   - duplicateOption: string, optional — "Duplicate" (default) | "WithDetailing" | "AsDependent".
///   - newName:         string, optional — rename the new view. Applied exactly, or the command
///                      fails (invalid_chars 400 / name_collision 409) and no copy is left behind.
/// </summary>
public sealed class DuplicateViewCommand : IRevitCommand
{
    public string Name => "duplicate_view";
    public bool IsReadOnly => false;
    public string RiskLevel => "low"; // creates a new view (easily deleted)

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var srcId = new ElementId(P.Long(p, "viewId"));
        var src = doc.GetElement(srcId) as View
            ?? throw new RevitCommandException("not_found", $"View {srcId.Value} not found.");

        var optStr = P.StrOrNull(p, "duplicateOption") ?? "Duplicate";
        if (!Enum.TryParse<ViewDuplicateOption>(optStr, true, out var opt))
            throw new RevitCommandException("invalid_parameter",
                $"Unknown duplicateOption '{optStr}'. Use Duplicate, WithDetailing, or AsDependent.");

        if (!src.CanViewBeDuplicated(opt))
            throw new RevitCommandException("invalid_parameter",
                $"View '{src.Name}' cannot be duplicated with option '{opt}'.");

        var newId = src.Duplicate(opt);
        var newView = doc.GetElement(newId) as View;

        var newName = P.StrOrNull(p, "newName");
        // Not `try { newView.Name = newName; } catch { }`: on a clash that kept the auto name
        // ("Level 1 Copy 1") and still reported success, so the caller lost track of its copy.
        if (newView != null && !string.IsNullOrWhiteSpace(newName))
            NameRules.ApplyViewName(newView, newName!, "newName");

        return new JsonObject
        {
            ["id"] = newId.Value,
            ["name"] = newView?.Name,
            ["viewType"] = newView?.ViewType.ToString(),
            ["sourceViewId"] = srcId.Value,
        };
    }
}
