using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Set a single parameter on a single element by name.
///
/// Parameters:
///   - id:           long, required
///   - parameterName: string, required
///   - value:         any, required (string / number / bool / { id: long })
///
/// The command coerces value to the parameter's StorageType.  Numeric values
/// for length-like parameters are interpreted as Revit internal units (feet) —
/// no automatic m→ft conversion here, because we don't yet know the parameter's
/// unit type.  Pass already-converted feet, or use specialised commands for
/// length editing.
/// </summary>
public sealed class SetParameterCommand : IRevitCommand
{
    public string Name => "set_parameter";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var idValue = P.Long(p, "id");
        var paramName = P.Str(p, "parameterName");
        var valueNode = p["value"]
            ?? throw new ArgumentException("Missing required parameter 'value'.");

        var element = doc.GetElement(new ElementId(idValue))
            ?? throw new InvalidOperationException($"No element with id {idValue}.");

        var param = element.LookupParameter(paramName)
            ?? throw new InvalidOperationException(
                $"Element {idValue} has no parameter named '{paramName}'.");

        if (param.IsReadOnly)
            throw new InvalidOperationException($"Parameter '{paramName}' is read-only.");

        // Capture before-state for structured diff.
        var previousValueString = SafeAsValueString(param);

        bool wrote;
        switch (param.StorageType)
        {
            case StorageType.String:
                wrote = param.Set(valueNode.GetValue<string>());
                break;

            case StorageType.Integer:
                // Booleans map to 0/1 for Yes/No params.
                wrote = valueNode.GetValueKind() == System.Text.Json.JsonValueKind.True
                     || valueNode.GetValueKind() == System.Text.Json.JsonValueKind.False
                    ? param.Set(valueNode.GetValue<bool>() ? 1 : 0)
                    : param.Set(valueNode.GetValue<int>());
                break;

            case StorageType.Double:
                wrote = param.Set(valueNode.GetValue<double>());
                break;

            case StorageType.ElementId:
                {
                    long target;
                    if (valueNode is JsonObject obj && obj["id"] is JsonNode idn)
                        target = idn.GetValue<long>();
                    else
                        target = valueNode.GetValue<long>();
                    wrote = param.Set(new ElementId(target));
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported StorageType '{param.StorageType}' for '{paramName}'.");
        }

        var newValueString = SafeAsValueString(param);
        return new JsonObject
        {
            ["id"] = element.Id.Value,
            ["parameterName"] = paramName,
            ["storageType"] = param.StorageType.ToString(),
            ["written"] = wrote,
            ["newValueString"] = newValueString,
            ["changes"] = new JsonObject
            {
                ["before"] = previousValueString,
                ["after"] = newValueString,
            },
            ["changeSummary"] = $"Set '{paramName}' on element {idValue}: '{previousValueString}' → '{newValueString}'",
        };
    }

    private static string? SafeAsValueString(Parameter p)
    {
        try { return p.AsValueString(); } catch { return null; }
    }
}
