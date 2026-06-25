using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Read the rendered data of a ViewSchedule — the cell text exactly as the user
/// sees it (calculated fields, formatting, units all applied).  Read-only.
///
/// Technique (The Building Coder, "The Schedule API and Access to Schedule Data"):
/// ViewSchedule.GetTableData().GetSectionData(SectionType.Body) gives the row/column
/// extent; ViewSchedule.GetCellText(SectionType.Body, row, col) gives each cell's
/// displayed text.  The first body row is normally the column headers.
///
/// Params:
///   - scheduleId: long, required — ElementId of a ViewSchedule.
///   - offset:     int, default 0 — first body row to return (pagination).
///   - limit:      int, default 100, max 1000 — rows per page.
///
/// Returns: { scheduleId, name, totalRows, totalColumns, offset, limit, hasMore,
///            nextOffset, rows: [[cellText, ...], ...] }.
/// </summary>
public sealed class GetScheduleDataCommand : IRevitCommand
{
    public string Name => "get_schedule_data";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var scheduleId = new ElementId(P.Long(p, "scheduleId"));
        var schedule = doc.GetElement(scheduleId) as ViewSchedule
            ?? throw new RevitCommandException("not_found",
                $"Element {scheduleId.Value} is not a ViewSchedule.");

        var body = schedule.GetTableData().GetSectionData(SectionType.Body);
        int totalRows = body.NumberOfRows;
        int totalCols = body.NumberOfColumns;
        int firstRow = body.FirstRowNumber;
        int firstCol = body.FirstColumnNumber;

        var limit = Math.Clamp(P.IntOr(p, "limit", 100), 1, 1000);
        var offset = Math.Max(0, P.IntOr(p, "offset", 0));

        var rows = new JsonArray();
        int emitted = 0;
        for (int r = firstRow + offset; r < firstRow + totalRows && emitted < limit; r++, emitted++)
        {
            var rowArr = new JsonArray();
            for (int c = firstCol; c < firstCol + totalCols; c++)
                rowArr.Add(schedule.GetCellText(SectionType.Body, r, c));
            rows.Add(rowArr);
        }

        var nextOffset = offset + emitted;
        var hasMore = nextOffset < totalRows;

        return new JsonObject
        {
            ["scheduleId"] = scheduleId.Value,
            ["name"] = schedule.Name,
            ["totalRows"] = totalRows,
            ["totalColumns"] = totalCols,
            ["offset"] = offset,
            ["limit"] = limit,
            ["hasMore"] = hasMore,
            ["nextOffset"] = hasMore ? nextOffset : null,
            ["rows"] = rows,
        };
    }
}
