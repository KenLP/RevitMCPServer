using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Deterministic element update with READ-BACK VERIFY. Sets one parameter on every
/// element matching the where-conditions, resolving instance-vs-type scope, then
/// re-reads each written value to confirm it actually took. Runs inside the
/// dispatcher's transaction: on dry-run the dispatcher rolls back (so this is the
/// preview); on a real run, if atomic and any element fails verification this
/// throws so the dispatcher rolls back the whole batch (all-or-nothing).
///
/// Params:
///   - category:  BuiltInCategory name, required.
///   - where:     [{ parameter, operator, value?, scope? }], optional (AND).
///   - set:       { parameter, value, units?, scope? }, required.
///   - atomic:    bool, default true (roll back everything if any verify fails).
/// </summary>
public sealed class UpdateWhereCommand : IRevitCommand
{
    public string Name => "update_where";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var bic = WhereSupport.ResolveCategory(P.Str(p, "category"));
        var conds = WhereSupport.ParseWhere(p["where"] as JsonArray);

        var setObj = p["set"] as JsonObject
            ?? throw new RevitCommandException("bad_request", "Missing required object 'set'.");

        // Tolerant of model variants: {parameter,value}, {parameterName,value}, or the
        // shorthand {"<ParamName>": "<value>"} that small models sometimes emit.
        var setParamName = WhereSupport.StrAny(setObj, "parameter", "parameterName", "param", "name");
        var setValue = setObj["value"] ?? setObj["Value"];
        if (string.IsNullOrWhiteSpace(setParamName) && setValue is null)
        {
            foreach (var kv in setObj)
            {
                if (kv.Key is "units" or "scope" or "parameter" or "parameterName" or "param" or "name") continue;
                setParamName = kv.Key; setValue = kv.Value; break;   // {"Comments":"Đã duyệt"}
            }
        }
        if (string.IsNullOrWhiteSpace(setParamName))
            throw new RevitCommandException("bad_request", "set.parameter is required.");
        if (setValue is null)
            throw new RevitCommandException("bad_request", "set.value is required.");
        var units = WhereSupport.StrAny(setObj, "units", "unit") ?? "internal";
        var setScope = (WhereSupport.StrAny(setObj, "scope") ?? "auto").Trim().ToLowerInvariant();
        var atomic = P.BoolOr(p, "atomic", true);

        var all = WhereSupport.CollectInstances(doc, bic);
        var matched = all.Where(el => WhereSupport.Matches(doc, el, conds)).ToList();

        // Determine the set-param scope from the first match (consistent per category+param).
        string? foundScope = null;
        if (matched.Count > 0)
            (_, foundScope) = WhereSupport.ResolveParam(doc, matched[0], setParamName, setScope);

        // Build the list of distinct ELEMENTS to write to (the type element for type
        // params, deduped — so we don't set the same type N times).
        var isTypeScope = foundScope == "type";
        var warnings = new JsonArray();
        var targets = new List<(Element write, Element subject)>(); // write=where param lives, subject=instance
        var affectedInstances = matched.Count;

        if (isTypeScope)
        {
            var seenTypes = new HashSet<long>();
            foreach (var el in matched)
            {
                var tid = el.GetTypeId();
                if (tid == ElementId.InvalidElementId) continue;
                if (!seenTypes.Add(tid.Value)) continue;
                if (doc.GetElement(tid) is Element t) targets.Add((t, el));
            }
            // collateral: instances of those types that were NOT matched also change.
            affectedInstances = all.Count(el => seenTypes.Contains(el.GetTypeId().Value));
            if (affectedInstances > matched.Count)
                warnings.Add($"'{setParamName}' is a TYPE parameter — changing {targets.Count} type(s) " +
                             $"also affects {affectedInstances - matched.Count} other instance(s) " +
                             $"beyond the {matched.Count} matched.");
        }
        else
        {
            foreach (var el in matched) targets.Add((el, el));
        }

        var results = new JsonArray();
        var applied = 0;
        var failedCount = 0;

        foreach (var (write, subject) in targets)
        {
            var param = write.LookupParameter(setParamName);
            var row = new JsonObject { ["id"] = subject.Id.Value, ["name"] = subject.Name };

            if (param is null)
            {
                row["ok"] = false; row["reason"] = "not_found"; failedCount++; results.Add(row); continue;
            }
            if (param.IsReadOnly)
            {
                row["ok"] = false; row["reason"] = "read_only"; failedCount++; results.Add(row); continue;
            }

            try
            {
                var before = SafeValueString(param);
                SetValue(param, setValue, units);
                var after = SafeValueString(param);
                var ok = VerifyWritten(param, setValue, units);   // ★ READ-BACK
                row["ok"] = ok;
                row["before"] = before;
                row["after"] = after;
                if (ok) applied++; else { failedCount++; row["reason"] = "verify_mismatch"; }
            }
            catch (Exception ex)
            {
                row["ok"] = false; row["reason"] = ex.Message; failedCount++;
            }
            results.Add(row);
        }

        // Real run + atomic + any failure → throw so the dispatcher rolls everything back.
        if (!ctx.DryRun && atomic && failedCount > 0)
            throw new RevitCommandException("verify_failed",
                $"Atomic update aborted: {failedCount}/{targets.Count} element(s) failed to verify; rolled back.");

        return new JsonObject
        {
            ["category"] = bic.ToString(),
            ["setParameter"] = setParamName,
            ["scope"] = foundScope ?? setScope,
            ["matchedCount"] = matched.Count,
            ["targetCount"] = targets.Count,
            ["affectedInstances"] = affectedInstances,
            ["applied"] = applied,
            ["failed"] = failedCount,
            ["atomic"] = atomic,
            ["warnings"] = warnings,
            ["results"] = results,
            ["changeSummary"] =
                $"Set '{setParamName}' on {applied}/{targets.Count} target(s)" +
                (failedCount > 0 ? $" ({failedCount} failed)" : "") +
                (isTypeScope ? $" [TYPE scope, affects {affectedInstances} instances]" : ""),
        };
    }

    // ── write + verify (mirrors SetParameterCommand, reuses its unit policy) ──

    private static void SetValue(Parameter param, JsonNode value, string units)
    {
        switch (param.StorageType)
        {
            case StorageType.String:
                param.Set(P.StrFrom(value, "set.value"));
                break;
            case StorageType.Integer:
                if (value.GetValueKind() is System.Text.Json.JsonValueKind.True
                                         or System.Text.Json.JsonValueKind.False)
                    param.Set(P.BoolFrom(value, "set.value") ? 1 : 0);
                else
                    param.Set(P.IntFrom(value, "set.value"));
                break;
            case StorageType.Double:
                param.Set(SetParameterCommand.ConvertToInternal(param, P.DblFrom(value, "set.value"), units, out _));
                break;
            case StorageType.ElementId:
                var target = value is JsonObject o && o["id"] is JsonNode idn
                    ? P.LongFrom(idn, "set.value.id") : P.LongFrom(value, "set.value");
                param.Set(new ElementId(target));
                break;
            default:
                throw new RevitCommandException("invalid_parameter",
                    $"Unsupported StorageType '{param.StorageType}'.");
        }
    }

    private static bool VerifyWritten(Parameter param, JsonNode value, string units)
    {
        switch (param.StorageType)
        {
            case StorageType.String:
                return string.Equals(param.AsString() ?? "", P.StrFrom(value, "set.value"),
                    StringComparison.Ordinal);
            case StorageType.Integer:
                var want = value.GetValueKind() is System.Text.Json.JsonValueKind.True
                                                or System.Text.Json.JsonValueKind.False
                    ? (P.BoolFrom(value, "set.value") ? 1 : 0) : P.IntFrom(value, "set.value");
                return param.AsInteger() == want;
            case StorageType.Double:
                var converted = SetParameterCommand.ConvertToInternal(param, P.DblFrom(value, "set.value"), units, out _);
                return Math.Abs(param.AsDouble() - converted) < 1e-6;
            case StorageType.ElementId:
                var target = value is JsonObject o && o["id"] is JsonNode idn
                    ? P.LongFrom(idn, "set.value.id") : P.LongFrom(value, "set.value");
                return param.AsElementId().Value == target;
            default:
                return false;
        }
    }

    private static string? SafeValueString(Parameter p)
    {
        try { return p.AsValueString() ?? p.AsString(); } catch { return null; }
    }
}
