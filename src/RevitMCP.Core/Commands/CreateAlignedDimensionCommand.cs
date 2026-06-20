using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create an aligned dimension chain between two or more element references in a view.
///
/// Params:
///   - references: array of { elementId: long, side?: "exterior"|"interior"|"auto" }
///       Wall elements: "exterior" (default) or "interior" picks the matching face.
///       Other elements (Grid, column, FamilyInstance, etc.): element reference used directly.
///   - line:    { start: {x,y,z}, end: {x,y,z} } — position and direction of the dimension line
///   - viewId:  long, optional (defaults to active view)
///   - units:   "meters"|"feet", default "meters"
/// </summary>
public sealed class CreateAlignedDimensionCommand : IRevitCommand
{
    public string Name => "create_aligned_dimension";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var viewId = p["viewId"] is not null
            ? new ElementId(P.Long(p, "viewId"))
            : doc.ActiveView?.Id
            ?? throw new RevitCommandException("not_found", "No active view.");

        var view = doc.GetElement(viewId) as View
            ?? throw new RevitCommandException("not_found", $"View {viewId.Value} not found.");

        // Dimension line
        var lineObj = p["line"] as JsonObject
            ?? throw new RevitCommandException("bad_request", "'line' with start/end is required.");
        var lineStart = P.Xyz(lineObj, "start", units);
        var lineEnd = P.Xyz(lineObj, "end", units);

        if (lineStart.DistanceTo(lineEnd) < 1e-6)
            throw new RevitCommandException("bad_request", "'line' start and end must not be the same point.");

        var dimLine = Line.CreateBound(lineStart, lineEnd);

        // References
        var refsArr = p["references"] as JsonArray
            ?? throw new RevitCommandException("bad_request", "'references' array is required.");

        if (refsArr.Count < 2)
            throw new RevitCommandException("bad_request", "At least 2 references are required.");

        var refArray = new ReferenceArray();
        foreach (var refNode in refsArr)
        {
            var refObj = refNode as JsonObject
                ?? throw new RevitCommandException("bad_request", "Each reference entry must be a JSON object.");

            var eid = new ElementId(refObj["elementId"]!.GetValue<long>());
            var element = doc.GetElement(eid)
                ?? throw new RevitCommandException("not_found", $"Element {eid.Value} not found.");

            var side = refObj["side"]?.GetValue<string>() ?? "auto";
            refArray.Append(GetReference(element, side));
        }

        var dim = doc.Create.NewDimension(view, dimLine, refArray);
        if (dim is null)
            throw new RevitCommandException("command_failed", "Revit returned null — check that references are visible in the view.");

        return new JsonObject
        {
            ["dimensionId"] = dim.Id.Value,
            ["value"] = dim.Value,
            ["segments"] = dim.Segments?.Size ?? 1,
            ["viewId"] = viewId.Value,
        };
    }

    private static Reference GetReference(Element element, string side)
    {
        if (element is Wall wall)
        {
            var shellLayer = string.Equals(side, "interior", StringComparison.OrdinalIgnoreCase)
                ? ShellLayerType.Interior
                : ShellLayerType.Exterior;
            var faces = HostObjectUtils.GetSideFaces(wall, shellLayer);
            return faces.FirstOrDefault()
                ?? throw new RevitCommandException("not_found",
                    $"Wall {wall.Id.Value} has no {side} face reference.");
        }

        // Grid, column, FamilyInstance, structural framing, etc.
        return new Reference(element);
    }
}
