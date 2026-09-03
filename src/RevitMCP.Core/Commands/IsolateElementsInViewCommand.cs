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
    // Stays UiAction deliberately, even though this command DOES need a
    // transaction. Both API calls it makes are model changes:
    // IsolateElementsTemporary and DisableTemporaryViewMode each throw
    // ModificationOutsideTransactionException without one — measured live on
    // Revit 2027. (An earlier comment here claimed the opposite, and a bug
    // report reproduced it for the `ids` branch; fixing that one exposed the
    // same fault in `reset`, which had never actually been exercised while
    // isolate was broken.)
    //
    // Promoting the command to ModelWrite would be the obvious fix and is the
    // wrong one: BatchPolicy forbids a batch that mixes ModelWrite with
    // UiAction, and open_view / select_elements / zoom_to_elements are all
    // UiAction — so [open_view, isolate_elements_in_view, zoom_to_elements],
    // the natural view-navigation sequence, would start being rejected. The
    // command therefore owns its own transaction, stays batchable with its
    // siblings, and keeps the dispatcher's dry-run no-op for UI actions.
    public ExecutionKind Execution => ExecutionKind.UiAction;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = SetViewDetailLevelCommand.ResolveView(doc, ctx, p);
        var reset = P.BoolOr(p, "reset", false);

        // Parse before opening a transaction so a bad id fails without one.
        List<ElementId>? ids = null;
        if (!reset)
        {
            var idsArr = P.Arr(p, "ids");
            ids = new List<ElementId>(idsArr.Count);
            for (var i = 0; i < idsArr.Count; i++)
                ids.Add(new ElementId(P.LongFrom(idsArr[i], $"ids[{i}]")));
        }

        // BOTH calls below are model changes and throw
        // ModificationOutsideTransactionException without an open transaction —
        // measured on a live Revit 2027, for reset just as much as for isolate.
        using var tx = new Transaction(doc, "MCP: isolate_elements_in_view");
        tx.Start();

        if (reset)
            view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
        else
            view.IsolateElementsTemporary(ids);

        tx.Commit();

        if (reset)
            return new JsonObject { ["viewId"] = view.Id.Value, ["reset"] = true };

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["isolated"] = ids!.Count,
            ["temporary"] = true,
        };
    }
}
