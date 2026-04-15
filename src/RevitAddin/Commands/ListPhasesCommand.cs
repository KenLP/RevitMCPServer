using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class ListPhasesCommand : IRevitCommand
{
    public string Name => "list_phases";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var phases = new FilteredElementCollector(doc)
            .OfClass(typeof(Phase))
            .Cast<Phase>()
            .OrderBy(p => p.Id.Value)
            .ToList();

        var arr = new JsonArray();
        foreach (var ph in phases)
        {
            arr.Add(new JsonObject
            {
                ["id"] = ph.Id.Value,
                ["name"] = ph.Name,
            });
        }

        return new JsonObject { ["count"] = phases.Count, ["phases"] = arr };
    }
}
