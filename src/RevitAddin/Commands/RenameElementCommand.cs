using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class RenameElementCommand : IRevitCommand
{
    public string Name => "rename_element";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var id = new ElementId(P.Long(ctx.Parameters, "id"));
        var newName = P.Str(ctx.Parameters, "name");

        var element = doc.GetElement(id)
            ?? throw new System.InvalidOperationException($"Element {id.Value} not found.");

        var oldName = element.Name;
        element.Name = newName;

        return new JsonObject
        {
            ["id"] = id.Value,
            ["oldName"] = oldName,
            ["newName"] = element.Name,
            ["changes"] = new JsonObject
            {
                ["before"] = oldName,
                ["after"] = element.Name,
            },
            ["changeSummary"] = $"Renamed element {id.Value}: '{oldName}' → '{element.Name}'",
        };
    }
}
