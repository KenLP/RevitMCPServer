using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Place a spot elevation symbol on the top face of an element in a view.
///
/// Root-cause fix (see The Building Coder, "Spot Elevation Creation on Top of
/// Beam"): the prior code projected onto a solid face manually, so the supplied
/// point did not lie exactly on the face → "Spot Dimension does not lie on its
/// reference". Instead we cast a ray straight DOWN through the element at the
/// user's (x, y) using <see cref="ReferenceIntersector"/> on a 3D view. The hit
/// gives both the face reference AND a point guaranteed to lie on that face
/// (origin + (-Z) * proximity).
///
/// Params:
///   - elementId:    long, required — element with an upward-facing face (Floor, Slab, Roof, beam, etc.)
///   - point:        {x, y, z?} — (x, y) is where the ray drops; z is ignored (the face Z is found by raycast)
///   - textOffset:   {x, y} optional — leader/symbol offset. Default {0.5, 0} m
///   - hasLeader:    bool, default true
///   - viewId:       long, optional (defaults to active view)
///   - units:        "meters"|"feet", default "meters"
/// </summary>
public sealed class CreateSpotElevationCommand : IRevitCommand
{
    public string Name => "create_spot_elevation";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        double scale = units.Equals("feet", StringComparison.OrdinalIgnoreCase)
            ? 1.0
            : P.MetersToFeet;

        var viewId = p["viewId"] is not null
            ? new ElementId(P.Long(p, "viewId"))
            : doc.ActiveView?.Id
            ?? throw new RevitCommandException("not_found", "No active view.");

        var view = doc.GetElement(viewId) as View
            ?? throw new RevitCommandException("not_found", $"View {viewId.Value} not found.");

        var elementId = new ElementId(P.Long(p, "elementId"));
        var element = doc.GetElement(elementId)
            ?? throw new RevitCommandException("not_found", $"Element {elementId.Value} not found.");

        var ptObj = p["point"] as JsonObject
            ?? throw new RevitCommandException("bad_request", "'point' {x, y, z?} is required.");
        double px = P.DblOr(ptObj, "x", 0) * scale;
        double py = P.DblOr(ptObj, "y", 0) * scale;

        var bbox = element.get_BoundingBox(null)
            ?? throw new RevitCommandException("not_found",
                $"Element {elementId.Value} has no bounding box to raycast against.");

        // Raycast straight down from above the element at (px, py). A fresh
        // temporary isometric 3D view avoids section-box / visibility surprises
        // from any existing 3D view; it is deleted after the spot is placed.
        var vft = CreateFloorPlanViewCommand.GetViewFamilyType(doc, ViewFamily.ThreeDimensional);
        var view3d = View3D.CreateIsometric(doc, vft.Id);

        SpotDimension spot;
        bool hasLeader;
        double hitZ;
        try
        {
            double startZ = bbox.Max.Z + 10.0; // 10 ft above the top of the element
            var originAbove = new XYZ(px, py, startZ);

            var intersector = new ReferenceIntersector(elementId, FindReferenceTarget.Face, view3d);
            var hit = intersector.FindNearest(originAbove, XYZ.BasisZ.Negate())
                ?? throw new RevitCommandException("not_found",
                    $"Downward ray at ({px:F2}, {py:F2}) ft hit no face on element {elementId.Value}. " +
                    "Check that (x, y) is within the element's footprint.");

            var faceRef = hit.GetReference();
            double proximity = hit.Proximity;
            // Exact point ON the face: originAbove + (-Z) * proximity
            var hitPoint = originAbove.Subtract(XYZ.BasisZ.Multiply(proximity));
            hitZ = hitPoint.Z;

            // textOffset → leader bend/end (offset sideways and up)
            double ox = 0.5 * scale, oy = 0;
            if (p["textOffset"] is JsonObject offObj)
            {
                ox = P.DblOr(offObj, "x", 0.5) * scale;
                oy = P.DblOr(offObj, "y", 0) * scale;
            }
            hasLeader = P.BoolOr(p, "hasLeader", true);

            double lift = Math.Max(Math.Abs(ox), 1.0); // leader rises at least 1 ft
            var origin = hitPoint;
            var refPt = hitPoint;
            var bend = new XYZ(hitPoint.X + ox / 2, hitPoint.Y + oy / 2, hitPoint.Z + lift);
            var end = new XYZ(hitPoint.X + ox, hitPoint.Y + oy, hitPoint.Z + lift);

            // Signature: view, reference, origin, bend, end, refPt, hasLeader
            spot = doc.Create.NewSpotElevation(view, faceRef, origin, bend, end, refPt, hasLeader)
                ?? throw new RevitCommandException("command_failed",
                    "NewSpotElevation returned null — the view type may not support spot elevations.");
        }
        finally
        {
            // Reference is to the element geometry (document-level), so deleting
            // the temporary view does not invalidate the placed spot dimension.
            try { doc.Delete(view3d.Id); } catch { }
        }

        return new JsonObject
        {
            ["spotId"] = spot.Id.Value,
            ["elevationMeters"] = hitZ * P.FeetToMeters,
            ["hasLeader"] = hasLeader,
            ["viewId"] = viewId.Value,
        };
    }
}
