using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Returns identity + parameter values for a single element.
///
/// Parameters:
///   - id: long, required.  ElementId.Value
/// </summary>
public sealed class GetElementInfoCommand : IRevitCommand
{
    public string Name => "get_element_info";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var idValue = P.Long(ctx.Parameters, "id");

        var element = doc.GetElement(new ElementId(idValue))
            ?? throw new RevitCommandException("not_found", $"No element with id {idValue}.");

        var paramsArray = new JsonArray();
        foreach (Parameter p in element.Parameters)
        {
            paramsArray.Add(new JsonObject
            {
                ["name"] = p.Definition?.Name,
                ["storageType"] = p.StorageType.ToString(),
                ["isReadOnly"] = p.IsReadOnly,
                ["value"] = ReadValue(p),
                ["valueString"] = SafeAsValueString(p),
            });
        }

        BoundingBoxXYZ? bbox = null;
        try { bbox = element.get_BoundingBox(null); } catch { }

        return new JsonObject
        {
            ["id"] = element.Id.Value,
            ["name"] = element.Name,
            ["category"] = element.Category?.Name,
            ["categoryEnum"] = element.Category?.BuiltInCategory.ToString(),
            ["typeId"] = element.GetTypeId()?.Value,
            ["levelId"] = element.LevelId?.Value,
            ["boundingBox"] = bbox is null ? null : new JsonObject
            {
                ["min"] = XyzToJson(bbox.Min),
                ["max"] = XyzToJson(bbox.Max),
            },
            ["parameters"] = paramsArray,
        };
    }

    private static JsonNode? ReadValue(Parameter p)
    {
        if (!p.HasValue) return null;
        return p.StorageType switch
        {
            StorageType.String    => JsonValue.Create(p.AsString()),
            StorageType.Integer   => JsonValue.Create(p.AsInteger()),
            StorageType.Double    => JsonValue.Create(p.AsDouble()),
            StorageType.ElementId => JsonValue.Create(p.AsElementId()?.Value),
            _ => null,
        };
    }

    private static string? SafeAsValueString(Parameter p)
    {
        try { return p.AsValueString(); } catch { return null; }
    }

    private static JsonObject XyzToJson(XYZ p) => new()
    {
        ["x"] = p.X,
        ["y"] = p.Y,
        ["z"] = p.Z,
    };
}
