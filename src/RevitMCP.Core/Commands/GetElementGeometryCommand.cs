using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Get basic geometry info for an element: bounding box, centroid,
/// face count, volume, surface area (if available from the geometry options).
///
/// Params:
///   - id: long, required
/// </summary>
public sealed class GetElementGeometryCommand : IRevitCommand
{
    public string Name => "get_element_geometry";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var id = new ElementId(P.Long(ctx.Parameters, "id"));
        var element = doc.GetElement(id)
            ?? throw new RevitCommandException("not_found", $"Element {id.Value} not found.");

        BoundingBoxXYZ? bbox = null;
        try { bbox = element.get_BoundingBox(null); } catch { }

        double? volume = null;
        double? surfaceArea = null;
        var faceCount = 0;
        var solidCount = 0;

        try
        {
            var opt = new Options { DetailLevel = ViewDetailLevel.Fine };
            var geom = element.get_Geometry(opt);
            if (geom is not null)
            {
                foreach (var obj in geom)
                {
                    if (obj is Solid solid && solid.Volume > 0)
                    {
                        solidCount++;
                        volume = (volume ?? 0) + solid.Volume;
                        surfaceArea = (surfaceArea ?? 0) + solid.SurfaceArea;
                        faceCount += solid.Faces.Size;
                    }
                    else if (obj is GeometryInstance gi)
                    {
                        foreach (var subObj in gi.GetInstanceGeometry())
                        {
                            if (subObj is Solid subSolid && subSolid.Volume > 0)
                            {
                                solidCount++;
                                volume = (volume ?? 0) + subSolid.Volume;
                                surfaceArea = (surfaceArea ?? 0) + subSolid.SurfaceArea;
                                faceCount += subSolid.Faces.Size;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        var result = new JsonObject
        {
            ["id"] = id.Value,
            ["name"] = element.Name,
            ["solidCount"] = solidCount,
            ["faceCount"] = faceCount,
        };

        if (volume.HasValue) result["volumeCubicFeet"] = volume.Value;
        if (surfaceArea.HasValue) result["surfaceAreaSqFeet"] = surfaceArea.Value;

        if (bbox is not null)
        {
            var min = bbox.Min;
            var max = bbox.Max;
            result["boundingBox"] = new JsonObject
            {
                ["min"] = new JsonObject { ["x"] = min.X, ["y"] = min.Y, ["z"] = min.Z },
                ["max"] = new JsonObject { ["x"] = max.X, ["y"] = max.Y, ["z"] = max.Z },
            };
            result["centroid"] = new JsonObject
            {
                ["x"] = (min.X + max.X) / 2,
                ["y"] = (min.Y + max.Y) / 2,
                ["z"] = (min.Z + max.Z) / 2,
            };
        }

        return result;
    }
}
