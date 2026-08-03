using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Read elements that live INSIDE a specific linked RVT file.
/// Bounding boxes are transformed to host-model coordinates.
///
/// Params:
///   - linkId:    long, required — ElementId of the RevitLinkInstance.
///   - category:  string, optional — BuiltInCategory enum name (e.g. "OST_DuctCurves").
///                Omit to retrieve all element types.
///   - limit:     int, optional, default 200, max 2000.
/// </summary>
public sealed class GetLinkedElementsCommand : IRevitCommand
{
    public string Name => "get_linked_elements";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var linkId = new ElementId(P.Long(p, "linkId"));
        var categoryStr = P.StrOrNull(p, "category");
        var limit = Math.Clamp(P.IntOr(p, "limit", 200), 1, 2000);

        var linkInstance = doc.GetElement(linkId) as RevitLinkInstance
            ?? throw new RevitCommandException("not_found",
                $"No RevitLinkInstance with id {linkId.Value}.");

        var linkedDoc = linkInstance.GetLinkDocument()
            ?? throw new RevitCommandException("invalid_parameter",
                $"Linked file '{linkInstance.Name}' is not loaded. Load it in Revit first.");

        var transform = linkInstance.GetTotalTransform();

        FilteredElementCollector collector;
        if (!string.IsNullOrEmpty(categoryStr))
        {
            if (!Enum.TryParse<BuiltInCategory>(categoryStr, ignoreCase: true, out var bic))
                throw new RevitCommandException("invalid_parameter",
                    $"Unknown BuiltInCategory '{categoryStr}'.");
            collector = new FilteredElementCollector(linkedDoc)
                .OfCategory(bic)
                .WhereElementIsNotElementType();
        }
        else
        {
            collector = new FilteredElementCollector(linkedDoc)
                .WhereElementIsNotElementType();
        }

        var arr = new JsonArray();
        var count = 0;

        foreach (var el in collector)
        {
            if (count >= limit) break;

            var rawBbox = el.get_BoundingBox(null);
            JsonObject? bboxObj = null;

            if (rawBbox != null)
            {
                var p1 = transform.OfPoint(rawBbox.Min);
                var p2 = transform.OfPoint(rawBbox.Max);
                bboxObj = new JsonObject
                {
                    ["min"] = new JsonObject
                    {
                        ["x"] = Math.Min(p1.X, p2.X),
                        ["y"] = Math.Min(p1.Y, p2.Y),
                        ["z"] = Math.Min(p1.Z, p2.Z),
                    },
                    ["max"] = new JsonObject
                    {
                        ["x"] = Math.Max(p1.X, p2.X),
                        ["y"] = Math.Max(p1.Y, p2.Y),
                        ["z"] = Math.Max(p1.Z, p2.Z),
                    },
                };
            }

            arr.Add(new JsonObject
            {
                ["id"] = el.Id.Value,
                // Stable across documents, unlike id — which is numbered per document
                // and therefore meaningless (or worse, wrong) outside this link.
                // Matches the externalId ACC / BIM 360 reports for the same element.
                ["uniqueId"] = el.UniqueId,
                ["name"] = el.Name,
                ["category"] = el.Category?.Name,
                ["categoryEnum"] = el.Category?.BuiltInCategory.ToString(),
                ["typeId"] = el.GetTypeId()?.Value,
                ["bboxInHostCoords"] = bboxObj,
            });
            count++;
        }

        return new JsonObject
        {
            ["linkId"] = linkId.Value,
            ["linkName"] = linkInstance.Name,
            ["linkedDocTitle"] = linkedDoc.Title,
            ["category"] = categoryStr,
            ["count"] = arr.Count,
            ["truncated"] = count == limit,
            ["elements"] = arr,
        };
    }
}
