using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Set the selection in Revit's UI to the given element ids.
///
/// Params:
///   - ids: long[], required
/// </summary>
public sealed class SelectElementsCommand : IRevitCommand
{
    public string Name => "select_elements";
    public bool IsReadOnly => false; // modifies UI state
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var uiDoc = ctx.RequireUIDoc();
        var idsArr = P.Arr(ctx.Parameters, "ids");
        var ids = new List<ElementId>();
        for (var i = 0; i < idsArr.Count; i++)
            ids.Add(new ElementId(P.LongFrom(idsArr[i], $"ids[{i}]")));

        uiDoc.Selection.SetElementIds(ids);

        return new JsonObject { ["selected"] = ids.Count };
    }
}
