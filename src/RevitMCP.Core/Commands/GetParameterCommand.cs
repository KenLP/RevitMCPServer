using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Get a single parameter's value from an element.
/// Simpler than get_element_info when you just need one value.
///
/// Params: id (long), parameterName (string).
/// </summary>
public sealed class GetParameterCommand : IRevitCommand
{
    public string Name => "get_parameter";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var id = new ElementId(P.Long(ctx.Parameters, "id"));
        var paramName = P.Str(ctx.Parameters, "parameterName");

        var element = doc.GetElement(id)
            ?? throw new RevitCommandException("not_found", $"No element with id {id.Value}.");

        var param = element.LookupParameter(paramName)
            ?? throw new RevitCommandException("not_found", $"Parameter '{paramName}' not found on element {id.Value}.");

        return new JsonObject
        {
            ["id"] = id.Value,
            ["parameterName"] = paramName,
            ["storageType"] = param.StorageType.ToString(),
            ["hasValue"] = param.HasValue,
            ["value"] = param.HasValue ? ReadValue(param) : null,
            ["valueString"] = param.HasValue ? SafeValueString(param) : null,
            ["isReadOnly"] = param.IsReadOnly,
        };
    }

    private static JsonNode? ReadValue(Parameter p) => p.StorageType switch
    {
        StorageType.String => JsonValue.Create(p.AsString()),
        StorageType.Integer => JsonValue.Create(p.AsInteger()),
        StorageType.Double => JsonValue.Create(p.AsDouble()),
        StorageType.ElementId => JsonValue.Create(p.AsElementId()?.Value),
        _ => null,
    };

    private static string? SafeValueString(Parameter p)
    { try { return p.AsValueString(); } catch { return null; } }
}
