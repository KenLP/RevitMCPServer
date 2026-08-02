using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Spatial-QC pack (HTTP-only; command name prefixed <c>spatial_</c>, not exposed as an MCP tool —
/// consumed programmatically by AutomatedSpatialQC over /mcp, not by LLM tool routing).
///
/// Placed stairs with Revit's own AS-BUILT riser height / tread depth / riser count, plus a plan
/// centroid and level — the live-model equivalent of what the IFC path re-measures from the stair
/// mesh, so the stair-geometry rules (max riser, min tread) can run without an IFC export.
///
/// The Revit API exposes no per-riser breakdown (only one flight-level ActualRiserHeight), so there
/// is deliberately no riserVariation field: the consumer treats a missing field as "unmeasured" and
/// reports INFO rather than a false PASS. Stairs whose Actual* values are 0 (sketch-based stair with
/// no computed run) are still emitted — the consumer already reads 0/absent as "no measurement".
///
/// Output:
///   { count, stairs: [ { id, name, levelName, x, y, riserHeight, treadDepth, nRisers } ] }
///   (metres; x/y = world-plan centroid of the bounding box, as in spatial_get_walls / get_doors)
/// </summary>
public sealed class GetStairsCommand : IRevitCommand
{
    public string Name => "spatial_get_stairs";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();

        var stairs = new FilteredElementCollector(doc)
            .OfClass(typeof(Stairs))
            .WhereElementIsNotElementType()
            .Cast<Stairs>()
            .ToList();

        var arr = new JsonArray();
        foreach (var s in stairs)
        {
            // Stairs span storeys, so Element.LevelId comes back invalid — the base level lives in
            // STAIRS_BASE_LEVEL_PARAM. (Verified live: LevelId alone reported levelName null for
            // every stair in the R27 model.)
            var lvl = doc.GetElement(s.LevelId) as Level
                      ?? doc.GetElement(new ElementId(
                             s.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)
                              ?.AsElementId()?.Value ?? -1L)) as Level;
            var bbox = s.get_BoundingBox(null);
            JsonNode? cx = null, cy = null;
            if (bbox is not null)
            {
                cx = JsonValue.Create((bbox.Min.X + bbox.Max.X) / 2.0 * P.FeetToMeters);
                cy = JsonValue.Create((bbox.Min.Y + bbox.Max.Y) / 2.0 * P.FeetToMeters);
            }

            // Actual* are computed from the PLACED stair (not the type's nominal target values) —
            // exactly the equivalent of re-measuring the mesh on the IFC path.
            arr.Add(new JsonObject
            {
                ["id"] = s.Id.Value,
                ["name"] = s.Name,
                ["levelName"] = lvl?.Name,
                ["x"] = cx,
                ["y"] = cy,
                ["riserHeight"] = JsonValue.Create(s.ActualRiserHeight * P.FeetToMeters),
                ["treadDepth"] = JsonValue.Create(s.ActualTreadDepth * P.FeetToMeters),
                ["nRisers"] = JsonValue.Create(s.ActualRisersNumber),
            });
        }

        return new JsonObject { ["count"] = arr.Count, ["stairs"] = arr };
    }
}
