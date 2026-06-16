using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Copy parameter values from a source element to one or more target elements.
/// Only writable parameters whose storage type matches on both elements are copied.
///
/// Params:
///   - sourceId:       long, required — the element to copy from.
///   - targetIds:      long[], required — elements to copy to.
///   - parameterNames: string[], optional — names to copy. Omit to copy all writable params.
/// </summary>
public sealed class CopyParametersCommand : IRevitCommand
{
    public string Name => "copy_parameters";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var sourceIdValue = P.Long(p, "sourceId");
        var targetIdsArr  = P.Arr(p, "targetIds");

        var sourceElem = doc.GetElement(new ElementId(sourceIdValue))
            ?? throw new RevitCommandException("not_found", $"Source element {sourceIdValue} not found.");

        // Build optional name filter.
        HashSet<string>? filterNames = null;
        if (p["parameterNames"] is JsonArray namesArr)
        {
            filterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in namesArr)
                if (n?.GetValue<string>() is string s) filterNames.Add(s);
        }

        // Collect writable source parameters keyed by name.
        var sourceParams = new Dictionary<string, Parameter>(StringComparer.OrdinalIgnoreCase);
        foreach (Parameter sp in sourceElem.Parameters)
        {
            if (sp.IsReadOnly || sp.StorageType == StorageType.None) continue;
            if (sp.Definition?.Name is not string name) continue;
            if (filterNames != null && !filterNames.Contains(name)) continue;
            sourceParams[name] = sp;
        }

        if (sourceParams.Count == 0)
            throw new RevitCommandException("not_found",
                filterNames != null
                    ? "None of the specified parameters were found (writable, matching storage type) on the source element."
                    : "Source element has no writable parameters.");

        var perTargetResults = new JsonArray();
        int totalCopied = 0;

        foreach (var targetNode in targetIdsArr)
        {
            var targetIdValue = targetNode!.GetValue<long>();
            var targetElem = doc.GetElement(new ElementId(targetIdValue));

            if (targetElem == null)
            {
                perTargetResults.Add(new JsonObject
                {
                    ["targetId"]    = targetIdValue,
                    ["ok"]          = false,
                    ["error"]       = "not_found",
                    ["paramsCopied"] = 0,
                });
                continue;
            }

            int copied = 0;
            var failures = new JsonArray();

            foreach (var (paramName, srcParam) in sourceParams)
            {
                var tgtParam = targetElem.LookupParameter(paramName);
                if (tgtParam == null || tgtParam.IsReadOnly || tgtParam.StorageType != srcParam.StorageType)
                    continue;

                try
                {
                    bool wrote = srcParam.StorageType switch
                    {
                        StorageType.String    => tgtParam.Set(srcParam.AsString()),
                        StorageType.Integer   => tgtParam.Set(srcParam.AsInteger()),
                        StorageType.Double    => tgtParam.Set(srcParam.AsDouble()),
                        StorageType.ElementId => tgtParam.Set(srcParam.AsElementId()),
                        _                     => false,
                    };
                    if (wrote) copied++;
                }
                catch (Exception ex)
                {
                    failures.Add(new JsonObject
                    {
                        ["parameterName"] = paramName,
                        ["error"]         = ex.Message,
                    });
                }
            }

            totalCopied += copied;
            var entry = new JsonObject
            {
                ["targetId"]    = targetIdValue,
                ["ok"]          = true,
                ["paramsCopied"] = copied,
            };
            if (failures.Count > 0) entry["failures"] = failures;
            perTargetResults.Add(entry);
        }

        return new JsonObject
        {
            ["sourceId"]    = sourceIdValue,
            ["paramsCopied"] = totalCopied,
            ["targets"]     = perTargetResults,
            ["changeSummary"] = $"Copied {totalCopied} parameter value(s) from element {sourceIdValue} to {perTargetResults.Count} target(s).",
        };
    }
}
