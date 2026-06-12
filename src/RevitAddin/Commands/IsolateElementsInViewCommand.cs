using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Temporarily isolate a set of HOST elements in a view (the cyan "Isolate
/// Element" temporary view mode).  Pass reset=true to clear the temporary
/// hide/isolate state instead.
///
/// LIMITATION: Revit's temporary isolate operates on host-document element ids
/// only.  It CANNOT keep individual elements that live inside a Revit link — a
/// link is all-or-nothing here, so isolating host elements hides the whole link.
/// To isolate a region spanning host + linked geometry (e.g. floors + linked
/// MEP), use set_section_box instead.
///
/// Params:
///   - viewId: long, optional — defaults to active view.
///   - ids:    long[] — host element ids to keep visible (required unless reset=true).
///   - reset:  bool, optional (default false) — clear temporary hide/isolate.
/// </summary>
public sealed class IsolateElementsInViewCommand : IRevitCommand
{
    public string Name => "isolate_elements_in_view";
    public bool IsReadOnly => false;
    public ExecutionKind Execution => ExecutionKind.UiAction; // temporary view mode — no transaction
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = SetViewDetailLevelCommand.ResolveView(doc, ctx, p);

        if (P.BoolOr(p, "reset", false))
        {
            view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            return new JsonObject { ["viewId"] = view.Id.Value, ["reset"] = true };
        }

        var idsArr = P.Arr(p, "ids");
        var ids = new List<ElementId>();
        foreach (var n in idsArr) { if (n is not null) ids.Add(new ElementId(n.GetValue<long>())); }

        view.IsolateElementsTemporary(ids);

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["isolated"] = ids.Count,
            ["temporary"] = true,
        };
    }
}
