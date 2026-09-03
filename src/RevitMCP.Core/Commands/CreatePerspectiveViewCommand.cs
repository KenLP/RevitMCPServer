using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create one or more perspective 3D views with the camera placed at explicit
/// coordinates.
///
/// <c>create_3d_view</c> cannot do this: it only makes an isometric view (or
/// duplicates the active one), and nothing in that path lets a caller say where
/// the camera stands. Placing the eye matters when several renders have to share
/// one viewpoint — a world-model reconstruction fed images shot from different
/// eye heights folds the floor plan diagonally.
///
/// Params:
///   - eye:            {x,y,z}, required — camera position.
///   - target:         {x,y,z}, required — the point it looks at. Must differ from eye.
///   - units:          "meters" | "feet", optional, default "meters".
///   - azimuthsDeg:    number[], optional — create one view per angle, all sharing the
///                     same eye, with the view direction rotated about world Z.
///                     0 = the eye→target direction; positive = clockwise seen from above.
///   - viewName:       string, optional. With azimuthsDeg each view gets " - {az}deg".
///   - viewTemplateId: long, optional.
///   - detailLevel:    "coarse" | "medium" | "fine", optional.
///
/// Returns { views: [{ id, name, azimuthDeg, eye, forward }], units } — eye and
/// forward echoed back in the REQUESTED units so a caller can verify placement
/// without a unit round-trip of its own.
/// </summary>
public sealed class CreatePerspectiveViewCommand : IRevitCommand
{
    public string Name => "create_perspective_view";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";   // creates views only; touches no model geometry

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        // P.Xyz scales anything that is not "feet" as metres, so an unsupported unit
        // would silently misplace the camera. Reject instead of guessing.
        var units = P.Units(p);
        if (units is not ("meters" or "feet"))
            throw new RevitCommandException("invalid_parameter",
                $"units must be 'meters' or 'feet', got '{units}'.");

        var eye = P.Xyz(p, "eye", units);
        var target = P.Xyz(p, "target", units);
        if (eye.DistanceTo(target) < 1e-7)
            throw new RevitCommandException("bad_request", "eye and target are the same point.");

        var baseForward = (target - eye).Normalize();

        var azimuths = new List<double>();
        if (p["azimuthsDeg"] is JsonArray azArr)
        {
            for (var i = 0; i < azArr.Count; i++)
                azimuths.Add(P.DblFrom(azArr[i], $"azimuthsDeg[{i}]"));
            if (azimuths.Count == 0)
                throw new RevitCommandException("bad_request", "azimuthsDeg is present but empty.");
        }
        else
        {
            azimuths.Add(0.0);          // single view along eye→target
        }

        var vft = CreateFloorPlanViewCommand.GetViewFamilyType(doc, ViewFamily.ThreeDimensional);
        var baseName = P.StrOrNull(p, "viewName");
        var templateId = P.LongOrNull(p, "viewTemplateId");
        var detail = ParseDetailLevel(P.StrOrNull(p, "detailLevel"));

        var scale = units == "feet" ? 1.0 : 1.0 / P.FeetToMeters;   // internal ft -> requested
        var views = new JsonArray();
        var warnings = new JsonArray();

        foreach (var azDeg in azimuths)
        {
            // Positive azimuth = clockwise viewed from above, so rotate by -angle
            // about +Z under Revit's right-handed convention.
            var rot = Transform.CreateRotation(XYZ.BasisZ, -azDeg * Math.PI / 180.0);
            var forward = rot.OfVector(baseForward).Normalize();

            // Up must be perpendicular to forward or ViewOrientation3D throws. Project
            // world Z onto the plane normal to forward; when the camera looks straight
            // up or down that projection collapses, so fall back to a fixed axis.
            var up = XYZ.BasisZ - forward.DotProduct(XYZ.BasisZ) * forward;
            up = up.GetLength() < 1e-6 ? XYZ.BasisY : up.Normalize();

            var view = View3D.CreatePerspective(doc, vft.Id);
            view.SetOrientation(new ViewOrientation3D(eye, up, forward));

            if (detail is not null) view.DetailLevel = detail.Value;

            if (templateId is not null)
            {
                var tv = doc.GetElement(new ElementId(templateId.Value)) as View;
                if (tv is null || !tv.IsTemplate)
                    throw new RevitCommandException("invalid_parameter",
                        $"viewTemplateId {templateId.Value} is not a view template.");
                view.ViewTemplateId = tv.Id;
            }

            if (!string.IsNullOrWhiteSpace(baseName))
            {
                var wanted = azimuths.Count > 1 || p["azimuthsDeg"] is not null
                    ? $"{baseName} - {azDeg:0.###}deg"
                    : baseName!;
                // A duplicate name throws; the view itself is still valid and useful, so
                // report the clash rather than losing the whole call to it.
                try { view.Name = wanted; }
                catch (Exception ex)
                {
                    warnings.Add($"could not name view '{wanted}': {ex.Message}");
                }
            }

            views.Add(new JsonObject
            {
                ["id"] = view.Id.Value,
                ["name"] = view.Name,
                ["azimuthDeg"] = azDeg,
                ["eye"] = Vec(eye, scale),
                ["forward"] = Vec(forward, 1.0),   // unit vector: no unit conversion
            });
        }

        var result = new JsonObject { ["views"] = views, ["units"] = units };
        if (warnings.Count > 0) result["warnings"] = warnings;
        return result;
    }

    private static JsonObject Vec(XYZ v, double scale) => new()
    {
        ["x"] = v.X * scale,
        ["y"] = v.Y * scale,
        ["z"] = v.Z * scale,
    };

    private static ViewDetailLevel? ParseDetailLevel(string? s) => s?.ToLowerInvariant() switch
    {
        null => null,
        "coarse" => ViewDetailLevel.Coarse,
        "medium" => ViewDetailLevel.Medium,
        "fine" => ViewDetailLevel.Fine,
        _ => throw new RevitCommandException("invalid_parameter",
            $"detailLevel must be 'coarse', 'medium' or 'fine', got '{s}'."),
    };
}
