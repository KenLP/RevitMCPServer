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
        foreach (var n in idsArr) { if (n is not null) ids.Add(new ElementId(n.GetValue<long>())); }

        uiDoc.ShowElements(ids);

        return new JsonObject { ["zoomed"] = ids.Count };
    }
}
