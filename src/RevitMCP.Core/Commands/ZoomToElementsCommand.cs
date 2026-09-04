using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Zoom/pan the active view to show the given elements.
///
/// Params:
///   - ids: long[], required
/// </summary>
public sealed class ZoomToElementsCommand : IRevitCommand
{
    public string Name => "zoom_to_elements";
    public bool IsReadOnly => false; // modifies UI view state
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var uiDoc = ctx.RequireUIDoc();
        var idsArr = P.Arr(ctx.Parameters, "ids");
        var ids = new List<ElementId>();
        for (var i = 0; i < idsArr.Count; i++)
            ids.Add(new ElementId(P.LongFrom(idsArr[i], $"ids[{i}]")));

        uiDoc.ShowElements(ids);

        return new JsonObject { ["zoomed"] = ids.Count };
    }
}
