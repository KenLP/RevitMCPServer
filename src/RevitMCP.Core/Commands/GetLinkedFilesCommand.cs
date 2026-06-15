using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class GetLinkedFilesCommand : IRevitCommand
{
    public string Name => "get_linked_files";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var links = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .ToList();

        var arr = new JsonArray();
        foreach (var link in links)
        {
            var linkDoc = link.GetLinkDocument();
            arr.Add(new JsonObject
            {
                ["id"] = link.Id.Value,
                ["name"] = link.Name,
                ["linkedDocTitle"] = linkDoc?.Title,
                ["linkedDocPath"] = linkDoc?.PathName,
                ["isLoaded"] = linkDoc != null,
            });
        }

        return new JsonObject { ["count"] = links.Count, ["links"] = arr };
    }
}
