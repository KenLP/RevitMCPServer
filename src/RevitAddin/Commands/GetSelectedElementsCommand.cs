using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class GetSelectedElementsCommand : IRevitCommand
{
    public string Name => "get_selected_elements";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var uiDoc = ctx.RequireUIDoc();
        var doc = ctx.RequireDoc();
        var ids = uiDoc.Selection.GetElementIds();

        var arr = new JsonArray();
        foreach (var id in ids)
        {
            var el = doc.GetElement(id);
            if (el is null) continue;
            arr.Add(ListElementsCommand.SummarizeElement(el));
        }

        return new JsonObject { ["count"] = arr.Count, ["elements"] = arr };
    }
}
