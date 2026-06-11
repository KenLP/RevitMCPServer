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
///   - units:          "meters"|"feet"|"internal", optional, default "internal".
///                     See set_parameter for full unit-conversion semantics.
///   - atomic:         bool, optional, default false. When true, ANY per-element
///                     failure throws — the dispatcher's transaction rolls back
///                     and the whole call reports ok:false (all-or-nothing).
///                     When false (best-effort), successful writes are kept and
///                     the result carries partialFailure:true if any failed.
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
        var atomic = P.BoolOr(p, "atomic", false);
        var units = P.StrOrNull(p, "units") ?? "internal";

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
                    ?? throw new RevitCommandException("not_found", $"Element {idValue} not found.");

                var param = element.LookupParameter(paramName)
                    ?? throw new RevitCommandException("not_found", $"Parameter '{paramName}' not found on element {idValue}.");

                if (param.IsReadOnly)
                    throw new RevitCommandException("read_only_parameter", $"Parameter '{paramName}' is read-only.");

                SetValue(param, valueNode, units);
                succeeded++;
            }
            catch (Exception ex)
            {
                // Atomic mode: abort immediately so the surrounding transaction
                // rolls back and the call reports ok:false (all-or-nothing).
                if (atomic)
                    throw new InvalidOperationException(
                        $"Atomic batch aborted: element {idValue}: {ex.Message}");

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
            ["partialFailure"] = failed > 0,
            ["inputUnits"] = units,
            ["errors"] = errors,
            ["changeSummary"] = $"Set '{paramName}' on {succeeded}/{ids.Count} elements" +
                                (failed > 0 ? $" ({failed} failed)" : ""),
        };
    }

    private static void SetValue(Parameter param, JsonNode valueNode, string units)
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
            {
                var raw = valueNode.GetValue<double>();
                var converted = SetParameterCommand.ConvertToInternal(
                    param, raw, units, out _);
                param.Set(converted);
                break;
            }
            case StorageType.ElementId:
                var idVal = valueNode is JsonObject obj && obj["id"] is JsonNode idn
                    ? idn.GetValue<long>()
                    : valueNode.GetValue<long>();
                param.Set(new ElementId(idVal));
                break;
            default:
                throw new RevitCommandException("invalid_parameter", $"Unsupported storage type: {param.StorageType}");
        }
    }
}
