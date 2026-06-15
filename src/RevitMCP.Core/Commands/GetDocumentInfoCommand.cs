using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

public sealed class GetDocumentInfoCommand : IRevitCommand
{
    public string Name => "get_document_info";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var info = doc.ProjectInformation;
        return new JsonObject
        {
            ["title"] = doc.Title,
            ["pathName"] = doc.PathName,
            ["isWorkshared"] = doc.IsWorkshared,
            ["isModified"] = doc.IsModified,
            ["projectName"] = info?.Name,
            ["projectNumber"] = info?.Number,
            ["projectAddress"] = info?.Address,
            ["projectStatus"] = info?.Status,
            ["organizationName"] = info?.OrganizationName,
            ["buildingName"] = info?.BuildingName,
            ["author"] = info?.Author,
            ["activeViewName"] = doc.ActiveView?.Name,
            ["activeViewType"] = doc.ActiveView?.ViewType.ToString(),
            ["displayUnitSystem"] = doc.DisplayUnitSystem.ToString(),
        };
    }
}
