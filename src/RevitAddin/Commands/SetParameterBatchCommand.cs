using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Set the same parameter on multiple elements in one call.
///
/// Params:
///   - ids:            long[], required
///   - parameterName:  string, required
///   - value:          any, required (same coercion rules as set_parameter)
/// </summary>
public sealed class SetParameterBatchCommand : IRevitCommand
{
    public string Name => "set_parameter_batch";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var ids = P.Arr(p, "ids");
        var paramName = P.Str(p, "parameterName");
        var valueNode = p["value"]
            ?? throw new ArgumentException("Missing required parameter 'value'.");

        var succeeded = 0;
        var failed = 0;
        var errors = new JsonArray();

        foreach (var idNode in ids)
        {
            if (idNode is null) continue;
            var idValue = idNode.GetValue<long>();
            try
            {
                var element = doc.GetElement(new ElementId(idValue))
                    ?? throw new InvalidOperationException($"Element {idValue} not found.");

                var param = element.LookupParameter(paramName)
                    ?? throw new InvalidOperationException($"Parameter '{paramName}' not found.");

                if (param.IsReadOnly)
                    throw new InvalidOperationException($"Parameter '{paramName}' is read-only.");

                SetValue(param, valueNode);
                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add(new JsonObject
                {
                    ["id"] = idValue,
                    ["error"] = ex.Message,
                });
            }
        }

        return new JsonObject
        {
            ["total"] = ids.Count,
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["errors"] = errors,
            ["changeSummary"] = $"Set '{paramName}' on {succeeded}/{ids.Count} elements" +
                                (failed > 0 ? $" ({failed} failed)" : ""),
        };
    }

    private static void SetValue(Parameter param, JsonNode valueNode)
    {
        switch (param.StorageType)
        {
            case StorageType.String:
                param.Set(valueNode.GetValue<string>());
                break;
            case StorageType.Integer:
                if (valueNode.GetValueKind() is System.Text.Json.JsonValueKind.True
                    or System.Text.Json.JsonValueKind.False)
                    param.Set(valueNode.GetValue<bool>() ? 1 : 0);
                else
                    param.Set(valueNode.GetValue<int>());
                break;
            case StorageType.Double:
                param.Set(valueNode.GetValue<double>());
                break;
            case StorageType.ElementId:
                var idVal = valueNode is JsonObject obj && obj["id"] is JsonNode idn
                    ? idn.GetValue<long>()
                    : valueNode.GetValue<long>();
                param.Set(new ElementId(idVal));
                break;
            default:
                throw new InvalidOperationException($"Unsupported storage type: {param.StorageType}");
        }
    }
}
