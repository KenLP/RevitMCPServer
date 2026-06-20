using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Place a spot elevation symbol on an element face in a view.
/// Uses doc.Create.NewSpotElevation (Revit 2026 API).
///
/// Params:
///   - elementId:    long, required — element with an upward-facing face (Floor, Slab, Roof, etc.)
///   - point:        {x, y, z?} — model-space position; will be projected onto the face automatically
///   - textOffset:   {x, y} optional — offset from point to the symbol/text. Default {0.5, 0} m
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

        // Find top-facing face reference (requires ComputeReferences = true)
        var faceRef = GetUpwardFaceReference(element)
            ?? throw new RevitCommandException("not_found",
                $"No upward-facing planar face found on element {elementId.Value}. " +
                "Provide a Floor, Slab, Roof, or similar host element.");

        // Parse input point
        var ptObj = p["point"] as JsonObject
            ?? throw new RevitCommandException("bad_request", "'point' {x, y, z?} is required.");
        double px = P.DblOr(ptObj, "x", 0) * scale;
        double py = P.DblOr(ptObj, "y", 0) * scale;
        double pz = P.DblOr(ptObj, "z", 0) * scale;

        // origin = measurement point (will be projected to face automatically by Revit)
        // refPt  = same as origin (Revit computes the actual projected point)
        var origin = new XYZ(px, py, pz);
        var refPt = origin;

        // textOffset → end point (where symbol/text appears)
        double defaultOffset = 0.5 * scale;
        double ox = defaultOffset, oy = 0;
        if (p["textOffset"] is JsonObject offObj)
        {
            ox = P.DblOr(offObj, "x", 0.5) * scale;
            oy = P.DblOr(offObj, "y", 0) * scale;
        }

        var end = new XYZ(px + ox, py + oy, pz);
        var bend = new XYZ(px + ox / 2, py + oy / 2, pz);
        var hasLeader = P.BoolOr(p, "hasLeader", true);

        // doc.Create.NewSpotElevation(view, reference, origin, bend, end, refPt, hasLeader)
        var spot = doc.Create.NewSpotElevation(view, faceRef, origin, bend, end, refPt, hasLeader);
        if (spot is null)
            throw new RevitCommandException("command_failed", "NewSpotElevation returned null — check the face reference and view type.");

        // Report elevation in meters (origin z is in feet internally)
        double elevM = pz * P.FeetToMeters;

        return new JsonObject
        {
            ["spotId"] = spot.Id.Value,
            ["elevationMeters"] = elevM,
            ["hasLeader"] = hasLeader,
            ["viewId"] = viewId.Value,
        };
    }

    private static Reference? GetUpwardFaceReference(Element element)
    {
        var opts = new Options
        {
            ComputeReferences = true,
            DetailLevel = ViewDetailLevel.Fine,
            IncludeNonVisibleObjects = false,
        };

        var geomElem = element.get_Geometry(opts);
        if (geomElem is null) return null;

        Reference? best = null;
        double bestZ = double.MinValue;

        foreach (var gObj in geomElem)
        {
            if (gObj is GeometryInstance gi)
            {
                foreach (var go2 in gi.GetInstanceGeometry())
                    TryFace(go2, ref best, ref bestZ);
            }
            else
            {
                TryFace(gObj, ref best, ref bestZ);
            }
        }

        return best;
    }

    private static void TryFace(GeometryObject gObj, ref Reference? best, ref double bestZ)
    {
        if (gObj is not Solid solid) return;
        foreach (Face face in solid.Faces)
        {
            if (face is PlanarFace pf && pf.FaceNormal.Z > 0.9 && pf.Reference is not null)
            {
                if (pf.Origin.Z > bestZ)
                {
                    bestZ = pf.Origin.Z;
                    best = pf.Reference;
                }
            }
        }
    }
}
