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
    // Stays UiAction deliberately. IsolateElementsTemporary DOES need an open
    // transaction (the comment here used to claim otherwise, and every call with
    // `ids` failed with "Attempt to modify the model outside of transaction"),
    // but promoting this to ModelWrite would make BatchPolicy reject the natural
    // sequence [open_view, isolate_elements_in_view, zoom_to_elements] — those
    // three are UiAction and a batch may not mix the two kinds. So the command
    // opens its own transaction for the branch that needs one instead, keeping
    // it batchable with the other view-navigation commands and preserving the
    // dry-run no-op the dispatcher applies to UI actions.
    public ExecutionKind Execution => ExecutionKind.UiAction;
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
        for (var i = 0; i < idsArr.Count; i++)
            ids.Add(new ElementId(P.LongFrom(idsArr[i], $"ids[{i}]")));

        // DisableTemporaryViewMode (the reset branch above) does not need a
        // transaction, which is why reset kept working while this branch did not.
        using var tx = new Transaction(doc, "MCP: isolate_elements_in_view");
        tx.Start();
        view.IsolateElementsTemporary(ids);
        tx.Commit();

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["isolated"] = ids.Count,
            ["temporary"] = true,
        };
    }
}
