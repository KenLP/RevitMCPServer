using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Spatial-QC pack (HTTP-only; command name prefixed <c>spatial_</c>, not exposed as an MCP tool).
///
/// Vertical headroom raycast (the Revit-native equivalent of spatial-QC's trimesh raycast).
/// For each (x,y) point, fires a ray UP from the floor and returns the height of the lowest
/// overhead soffit above `minObstacleHeight` — ceilings, floors-above, roofs, structural
/// framing. Stairs are EXCLUDED (not in the category filter), so stair landings don't create
/// false low hits. Uses ReferenceIntersector against a temporary isometric 3D view.
///
/// Params: points:[{x,y}], floorZ (number), maxHeight (default 8), minObstacleHeight (default 0.5),
///         units ("meters"|"feet"). Returns { headrooms: [m, ...] } aligned to points.
/// </summary>
public sealed class RaycastHeadroomCommand : IRevitCommand
{
    public string Name => "spatial_raycast_headroom";
    public bool IsReadOnly => false;   // creates + deletes a temporary 3D view (in the txn)

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        double scale = units.Equals("feet", StringComparison.OrdinalIgnoreCase) ? 1.0 : P.MetersToFeet;

        var pts = P.Arr(p, "points");
        double floorZ = P.Dbl(p, "floorZ") * scale;
        double maxH = P.DblOr(p, "maxHeight", 8.0) * scale;
        double minH = P.DblOr(p, "minObstacleHeight", 0.5) * scale;
        double eps = 0.05 * P.MetersToFeet;

        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional)
            ?? throw new RevitCommandException("not_found", "No 3D ViewFamilyType in the document.");
        var v3d = View3D.CreateIsometric(doc, vft.Id);
        doc.Regenerate();

        try
        {
            var cats = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Roofs, BuiltInCategory.OST_StructuralFraming,
            };
            var ri = new ReferenceIntersector(new ElementMulticategoryFilter(cats),
                                              FindReferenceTarget.Face, v3d)
            { FindReferencesInRevitLinks = false };

            var headrooms = new JsonArray();
            foreach (var node in pts)
            {
                var o = node as JsonObject;
                double x = (o is null ? 0 : P.DblOr(o, "x", 0)) * scale;
                double y = (o is null ? 0 : P.DblOr(o, "y", 0)) * scale;
                var origin = new XYZ(x, y, floorZ + eps);

                double h = maxH;   // no soffit found -> "open" to max
                foreach (var rc in ri.Find(origin, XYZ.BasisZ))
                {
                    double hh = rc.Proximity + eps;          // height above floor (ft)
                    if (hh >= minH && hh <= maxH && hh < h) h = hh;
                }
                headrooms.Add(JsonValue.Create(h / scale));  // back to metres
            }
            return new JsonObject { ["headrooms"] = headrooms };
        }
        finally
        {
            doc.Delete(v3d.Id);   // remove the temporary view
        }
    }
}
