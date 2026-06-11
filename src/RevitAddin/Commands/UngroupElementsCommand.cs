using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Ungroup a Group, returning its members as individual elements.
///
/// Params:
///   - groupId: long, required
/// </summary>
public sealed class UngroupElementsCommand : IRevitCommand
{
    public string Name => "ungroup_elements";
    public bool IsReadOnly => false;
    public string RiskLevel => "high";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var groupId = new ElementId(P.Long(ctx.Parameters, "groupId"));
        var group = doc.GetElement(groupId) as Group
            ?? throw new RevitCommandException("invalid_parameter", $"Element {groupId.Value} is not a Group.");

        var memberIds = group.UngroupMembers();
        var arr = new JsonArray();
        foreach (var id in memberIds) arr.Add(id.Value);

        return new JsonObject
        {
            ["ungrouped"] = memberIds.Count,
            ["memberIds"] = arr,
        };
    }
}
