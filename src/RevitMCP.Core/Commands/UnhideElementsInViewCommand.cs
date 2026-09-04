using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class UnhideElementsInViewCommand : IRevitCommand
{
    public string Name => "unhide_elements_in_view";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = SetViewDetailLevelCommand.ResolveView(doc, ctx, p);

        var idsArr = P.Arr(p, "ids");
        var ids = new List<ElementId>();
        for (var i = 0; i < idsArr.Count; i++)
            ids.Add(new ElementId(P.LongFrom(idsArr[i], $"ids[{i}]")));

        view.UnhideElements(ids);

        return new JsonObject { ["unhidden"] = ids.Count, ["viewId"] = view.Id.Value };
    }
}
