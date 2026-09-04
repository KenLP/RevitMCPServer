using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class GroupElementsCommand : IRevitCommand
{
    public string Name => "group_elements";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var idsArr = P.Arr(ctx.Parameters, "ids");
        var ids = new List<ElementId>();
        for (var i = 0; i < idsArr.Count; i++)
            ids.Add(new ElementId(P.LongFrom(idsArr[i], $"ids[{i}]")));

        var group = doc.Create.NewGroup(ids);
        var groupName = P.StrOrNull(ctx.Parameters, "name");
        if (!string.IsNullOrWhiteSpace(groupName))
        {
            try { group.GroupType.Name = groupName; } catch { }
        }

        return new JsonObject
        {
            ["groupId"] = group.Id.Value,
            ["groupTypeName"] = group.GroupType?.Name,
            ["memberCount"] = ids.Count,
        };
    }
}
