using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Switch the active view to a specified view.
///
/// Params:
///   - viewId: long, required
/// </summary>
public sealed class OpenViewCommand : IRevitCommand
{
    public string Name => "open_view";
    public bool IsReadOnly => false;
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var uiDoc = ctx.RequireUIDoc();
        var doc = ctx.RequireDoc();
        var viewId = new ElementId(P.Long(ctx.Parameters, "viewId"));

        var view = doc.GetElement(viewId) as View
            ?? throw new System.InvalidOperationException($"View {viewId.Value} not found.");

        uiDoc.ActiveView = view;

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["name"] = view.Name,
            ["viewType"] = view.ViewType.ToString(),
        };
    }
}
