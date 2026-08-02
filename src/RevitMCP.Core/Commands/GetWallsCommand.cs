using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Spatial-QC pack (HTTP-only; command name prefixed <c>spatial_</c>, not exposed as an MCP tool —
/// consumed programmatically by AutomatedSpatialQC over /mcp, not by LLM tool routing).
///
/// Wall plan footprints (centerline offset by half the wall width) + Z range + the DECLARED
/// Interior/Exterior Function value, in world metres. Feeds spatial-QC's storey envelope: a
/// flood fill that decides which part of a storey is truly outdoors, which is an enclosed void
/// (shaft/courtyard) and which is solid. That drives exterior-door detection for the egress rules
/// (the old "door touches &lt;= 1 room" heuristic breaks on thick or curtain walls) and the
/// wall-declaration audit rule.
///
/// <c>isExternal</c> is emitted VERBATIM: it is a user declaration, and the consumer's whole point
/// is to audit it against the geometry — never to trust it.
///
/// Output:
///   { count, walls: [ { id, name, levelName, z0, z1, isExternal,
///                       loops: [ [ [x,y], ... ] ] } ] }   // metres, world XY, one outer ring
/// </summary>
public sealed class GetWallsCommand : IRevitCommand
{
    public string Name => "spatial_get_walls";
    public bool IsReadOnly => true;

    // Curtain walls report Width ~= 0 (their thickness lives in the panels/mullions); give them a
    // nominal footprint so the envelope's flood fill still sees a facade there rather than a gap.
    private const double CurtainWallWidthM = 0.15;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();

        var walls = new FilteredElementCollector(doc)
            .OfClass(typeof(Wall))
            .WhereElementIsNotElementType()
            .Cast<Wall>()
            .ToList();

        var arr = new JsonArray();
        foreach (var w in walls)
        {
            // Curtain-wall-by-face and in-place walls have no location curve — nothing to offset.
            if ((w.Location as LocationCurve)?.Curve is not Curve curve) continue;

            double halfWidthFt = w.Width > 1e-6
                ? w.Width / 2.0
                : CurtainWallWidthM / P.FeetToMeters / 2.0;

            var ring = OffsetRing(curve, halfWidthFt);
            if (ring is null) continue;

            // A wall with no bounding box has no Z range, so the consumer could not band it to a
            // storey anyway — skip it rather than emit null z0/z1 (the consumer reads those with
            // float(), which would throw on null).
            var bbox = w.get_BoundingBox(null);
            if (bbox is null) continue;

            var lvl = doc.GetElement(w.LevelId) as Level;
            // Function (Interior/Exterior/...) lives on the wall TYPE in Revit, but check the
            // instance first so an overriding instance parameter, if any, still wins.
            var fn = w.get_Parameter(BuiltInParameter.FUNCTION_PARAM)
                     ?? doc.GetElement(w.GetTypeId())?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);

            arr.Add(new JsonObject
            {
                ["id"] = w.Id.Value,
                ["name"] = w.Name,
                ["levelName"] = lvl?.Name,
                ["z0"] = JsonValue.Create(bbox.Min.Z * P.FeetToMeters),
                ["z1"] = JsonValue.Create(bbox.Max.Z * P.FeetToMeters),
                ["isExternal"] = fn is not null && fn.HasValue
                    ? JsonValue.Create(fn.AsInteger() == (int)WallFunction.Exterior)
                    : null,
                ["loops"] = new JsonArray { ring },
            });
        }

        return new JsonObject { ["count"] = arr.Count, ["walls"] = arr };
    }

    /// <summary>
    /// Closed plan ring for a wall: tessellate the location curve (so arcs/curved walls become
    /// polylines), offset every vertex perpendicular to the local tangent by half the width, then
    /// walk the far side back. Interior vertices use the neighbours' chord as tangent so corners
    /// stay put. Returns null when the curve degenerates to a point.
    /// </summary>
    private static JsonArray? OffsetRing(Curve curve, double halfWidthFt)
    {
        var tess = curve.Tessellate();
        if (tess is null || tess.Count < 2) return null;

        // Flatten to plan and drop repeated vertices (a duplicate kills the tangent).
        var pts = new List<XYZ>();
        foreach (var p in tess)
        {
            var flat = new XYZ(p.X, p.Y, 0.0);
            if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(flat) > 1e-9) pts.Add(flat);
        }
        if (pts.Count < 2) return null;

        var left = new List<XYZ>();
        var right = new List<XYZ>();
        for (int i = 0; i < pts.Count; i++)
        {
            XYZ dir = i == 0 ? pts[1] - pts[0]
                    : i == pts.Count - 1 ? pts[i] - pts[i - 1]
                    : pts[i + 1] - pts[i - 1];
            double len = dir.GetLength();
            if (len < 1e-9) continue;
            dir = dir / len;
            var offset = new XYZ(-dir.Y, dir.X, 0.0) * halfWidthFt;
            left.Add(pts[i] + offset);
            right.Add(pts[i] - offset);
        }
        if (left.Count < 2) return null;

        right.Reverse();
        var ring = new JsonArray();
        foreach (var p in left.Concat(right))
            ring.Add(new JsonArray { p.X * P.FeetToMeters, p.Y * P.FeetToMeters });
        return ring;
    }
}
