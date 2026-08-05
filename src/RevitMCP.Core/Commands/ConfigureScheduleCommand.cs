using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Add filters, sort/group fields to an existing ViewSchedule, and optionally export to CSV.
///
/// Params:
///   - scheduleId:      long, required — ElementId of the ViewSchedule to configure.
///   - clearFilters:    bool, optional — remove all existing filters first. Default false.
///   - clearSortFields: bool, optional — remove all existing sort/group fields first. Default false.
///   - filters:         array of { field, operator, value? }, optional.
///                      operator: equals|not_equals|greater|greater_equal|less|less_equal|
///                                contains|not_contains|begins_with|ends_with|has_value|has_no_value
///   - sortFields:      array of { field, ascending?, groupBy? }, optional.
///                      groupBy=true adds a group header for each distinct value.
///   - exportCsv:       bool, optional — export the schedule to CSV and include content in response.
/// </summary>
public sealed class ConfigureScheduleCommand : IRevitCommand
{
    public string Name => "configure_schedule";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var scheduleId = P.Long(p, "scheduleId");
        var schedule = doc.GetElement(new ElementId(scheduleId)) as ViewSchedule
            ?? throw new RevitCommandException("not_found",
                $"No schedule view with id {scheduleId}. Use revit_list_sheets or revit_get_views to find schedule ids.");

        var def = schedule.Definition;
        var schedulableFields = def.GetSchedulableFields();

        var filtersAdded   = new JsonArray();
        var sortFieldsAdded = new JsonArray();
        var warnings       = new JsonArray();

        // Clear existing state if requested
        if (P.BoolOr(p, "clearFilters", false))
        {
            for (int i = def.GetFilterCount() - 1; i >= 0; i--)
                def.RemoveFilter(i);
        }
        if (P.BoolOr(p, "clearSortFields", false))
        {
            for (int i = def.GetSortGroupFieldCount() - 1; i >= 0; i--)
                def.RemoveSortGroupField(i);
        }

        // Add filters
        if (p["filters"] is JsonArray filtersArr)
        {
            foreach (var filterNode in filtersArr)
            {
                if (filterNode is not JsonObject f) continue;

                var fieldName = P.Str(f, "field");
                var op        = P.StrOrNull(f, "operator") ?? "equals";
                // Raw node, never GetValue<string>(): a JSON number would throw here, outside
                // the per-filter try below, and take the whole command down with it.
                var valueNode = f["value"];

                var fieldId = FindOrAddFieldId(def, doc, schedulableFields, fieldName, warnings);
                if (fieldId is null) continue;

                var filterTypeName = MapOperator(op);
                if (!Enum.TryParse<ScheduleFilterType>(filterTypeName, out var filterType))
                {
                    warnings.Add($"Unknown filter operator '{op}'.");
                    continue;
                }

                try
                {
                    AddFilterUsingBestOverload(def, fieldId, filterType, valueNode);
                    filtersAdded.Add(new JsonObject
                    {
                        ["field"]    = fieldName,
                        ["operator"] = op,
                        ["value"]    = ReadFilterValue(valueNode).Text,
                    });
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not add filter on '{fieldName}': {ex.Message}");
                }
            }
        }

        // Add sort / group fields
        if (p["sortFields"] is JsonArray sortArr)
        {
            foreach (var sortNode in sortArr)
            {
                if (sortNode is not JsonObject s) continue;

                var fieldName = P.Str(s, "field");
                var ascending = P.BoolOr(s, "ascending", true);
                var groupBy   = P.BoolOr(s, "groupBy", false);

                var fieldId = FindOrAddFieldId(def, doc, schedulableFields, fieldName, warnings);
                if (fieldId is null) continue;

                try
                {
                    var order = ascending ? ScheduleSortOrder.Ascending : ScheduleSortOrder.Descending;
                    var sgf   = new ScheduleSortGroupField(fieldId, order);
                    if (groupBy)
                    {
                        sgf.ShowHeader    = true;
                        sgf.ShowFooter    = false;
                        sgf.ShowBlankLine = true;
                    }
                    def.AddSortGroupField(sgf);
                    sortFieldsAdded.Add(new JsonObject
                    {
                        ["field"]     = fieldName,
                        ["ascending"] = ascending,
                        ["groupBy"]   = groupBy,
                    });
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not add sort field '{fieldName}': {ex.Message}");
                }
            }
        }

        // Optional CSV export
        string? csvContent = null;
        if (P.BoolOr(p, "exportCsv", false))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"revitmcp_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var exportOptions = new ViewScheduleExportOptions
                {
                    ColumnHeaders        = ExportColumnHeaders.OneRow,
                    FieldDelimiter       = ",",
                    TextQualifier        = ExportTextQualifier.DoubleQuote,
                    Title                = true,
                    HeadersFootersBlanks = true,
                };
                var csvName = string.Concat(schedule.Name.Split(Path.GetInvalidFileNameChars())) + ".csv";
                schedule.Export(tempDir, csvName, exportOptions);

                var csvPath = Path.Combine(tempDir, csvName);
                if (File.Exists(csvPath))
                    csvContent = File.ReadAllText(csvPath, System.Text.Encoding.UTF8);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        var result = new JsonObject
        {
            ["scheduleId"]      = scheduleId,
            ["scheduleName"]    = schedule.Name,
            ["filtersAdded"]    = filtersAdded,
            ["sortFieldsAdded"] = sortFieldsAdded,
        };
        if (warnings.Count > 0)    result["warnings"]    = warnings;
        if (csvContent is not null) result["csvContent"] = csvContent;
        return result;
    }

    /// <summary>
    /// Reads a filter value node without asserting its JSON kind — <c>GetValue&lt;string&gt;()</c>
    /// throws on a JsonValue holding a number, which is what used to fail the whole command.
    /// Returns the text rendering (used for the response echo and the string overload), whether
    /// the node arrived as a JSON number, and its invariant-culture numeric reading.
    /// </summary>
    public static (bool WasJsonNumber, string Text, bool IsNumeric, double Number) ReadFilterValue(JsonNode? node)
    {
        if (node is null) return (false, "", false, 0d);

        if (node is JsonValue jv && jv.TryGetValue<double>(out var num))
            return (true, jv.ToString(), true, num);

        var text = node is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : node.ToString();

        // InvariantCulture is load-bearing: current-culture parsing on a de-DE machine reads
        // "2.9527559055118114" as 29527559055118114 — the filter would apply and match nothing.
        var isNumeric = double.TryParse(
            text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed);
        return (false, text, isNumeric, parsed);
    }

    /// <summary>
    /// Adds a filter using the value overload the field actually accepts.
    ///
    /// The retry has to wrap <c>AddFilter</c>, NOT the <c>ScheduleFilter</c> constructor: the
    /// constructor happily builds a string filter for a Double field, and Revit only rejects it
    /// when the filter is added — measured, its message ends "Parameter name: filter", naming
    /// AddFilter's argument. A ctor-only ladder never reaches its second rung.
    ///
    /// Overload order is deliberate. A JSON number tries numeric first; a JSON string tries the
    /// string overload first so TEXT fields keep behaving exactly as they did (a Mark of "100"
    /// must stay text). Nothing here resolves the field's storage type — Document/ParameterElement
    /// walks are version-fragile, and AddFilter is the authoritative validator anyway.
    ///
    /// The value arrives in Revit INTERNAL units (feet for length) and is passed straight through —
    /// callers convert before sending, so any unit conversion here would corrupt every filter.
    /// </summary>
    private static void AddFilterUsingBestOverload(
        ScheduleDefinition def,
        ScheduleFieldId fieldId,
        ScheduleFilterType filterType,
        JsonNode? valueNode)
    {
        if (filterType is ScheduleFilterType.HasValue or ScheduleFilterType.HasNoValue)
        {
            def.AddFilter(new ScheduleFilter(fieldId, filterType));
            return;
        }

        var (wasJsonNumber, text, isNumeric, number) = ReadFilterValue(valueNode);

        // The int overload is offered only for an integral value: truncating 2.95 to 2 on an
        // Integer field would produce a filter that "works" and silently means something else.
        var integral = isNumeric && number == Math.Floor(number)
                       && number >= int.MinValue && number <= int.MaxValue;

        var candidates = new List<Func<ScheduleFilter>>();
        if (wasJsonNumber)
        {
            candidates.Add(() => new ScheduleFilter(fieldId, filterType, number));
            if (integral) candidates.Add(() => new ScheduleFilter(fieldId, filterType, (int)number));
            candidates.Add(() => new ScheduleFilter(fieldId, filterType, text));
        }
        else
        {
            candidates.Add(() => new ScheduleFilter(fieldId, filterType, text));
            if (isNumeric)
            {
                candidates.Add(() => new ScheduleFilter(fieldId, filterType, number));
                if (integral) candidates.Add(() => new ScheduleFilter(fieldId, filterType, (int)number));
            }
        }

        // Catch plainly rather than filtering on exception type: Revit throws from
        // Autodesk.Revit.Exceptions, whose ArgumentException does NOT derive from the BCL one, so
        // a type-filtered ladder silently stops at its first rung. A rung can only "succeed" by
        // actually adding the filter, so a broad catch costs nothing but an extra attempt, and the
        // last refusal is rethrown verbatim — same warning surface as before this fix existed.
        Exception? lastRejection = null;
        foreach (var makeFilter in candidates)
        {
            try
            {
                def.AddFilter(makeFilter());
                return;
            }
            catch (Exception ex)
            {
                lastRejection = ex;
            }
        }

        throw lastRejection ?? new InvalidOperationException(
            $"No ScheduleFilter overload accepted the value '{text}'.");
    }

    /// <summary>
    /// Returns the ScheduleFieldId for a named field, adding it as a hidden field if not yet in schedule.
    /// Returns null and appends a warning if the field is not schedulable.
    /// </summary>
    private static ScheduleFieldId? FindOrAddFieldId(
        ScheduleDefinition def,
        Document doc,
        System.Collections.Generic.IList<SchedulableField> schedulableFields,
        string fieldName,
        JsonArray warnings)
    {
        var sf = schedulableFields.FirstOrDefault(
            x => x.GetName(doc).Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (sf is null)
        {
            warnings.Add($"Field '{fieldName}' not found in schedulable fields.");
            return null;
        }

        // Check if already in the schedule definition
        for (int i = 0; i < def.GetFieldCount(); i++)
        {
            var field = def.GetField(i);
            if (field.ParameterId == sf.ParameterId)
                return field.FieldId;
        }

        // Not yet present — add as hidden so filter/sort can reference it
        try
        {
            var added = def.AddField(sf);
            added.IsHidden = true;
            return added.FieldId;
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not add field '{fieldName}' to schedule: {ex.Message}");
            return null;
        }
    }

    private static string MapOperator(string op) => op.ToLowerInvariant() switch
    {
        "equals"        or "eq"  or "="  => "Equal",
        "not_equals"    or "neq" or "!=" => "NotEqual",
        "greater"       or "gt"  or ">"  => "GreaterThan",
        "greater_equal" or "gte" or ">=" => "GreaterOrEqual",
        "less"          or "lt"  or "<"  => "LessThan",
        "less_equal"    or "lte" or "<=" => "LessOrEqual",
        "contains"                       => "Contains",
        "not_contains"                   => "NotContains",
        "begins_with"                    => "BeginsWith",
        "not_begins_with"                => "NotBeginsWith",
        "ends_with"                      => "EndsWith",
        "not_ends_with"                  => "NotEndsWith",
        "has_value"                      => "HasValue",
        "has_no_value"                   => "HasNoValue",
        _                                => op,
    };
}
