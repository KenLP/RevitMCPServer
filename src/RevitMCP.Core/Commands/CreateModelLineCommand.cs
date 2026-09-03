using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Spatial-QC pack (HTTP-only; command name prefixed <c>spatial_</c>, not exposed as an MCP tool —
/// consumed programmatically by AutomatedSpatialQC over /mcp, not by LLM tool routing).
///
/// Draws a straight <c>ModelCurve</c> between two world points. Unlike
/// <c>create_detail_line</c>, which makes a view-specific <c>DetailCurve</c> and refuses to run in a
/// 3D view at all, a model curve lives in the model and therefore shows up in every view that cuts
/// through it — including a 3D view opened later. That is what spatial-QC needs to draw the measured
/// min-width chord inside the 3D view its panel opens.
///
/// The returned <c>id</c> is a real element with a usable <c>GetReference()</c>, so it can later be
/// fed to <c>create_aligned_dimension</c>, which takes element references rather than bare points.
///
/// Params:
///   - start, end: {x,y,z}, required. Must not be the same point.
///   - units:      "meters" | "feet", optional, default "meters".
///   - viewId:     long, optional — ONLY used to apply <c>color</c>. It does not decide where the
///                 line appears; a model curve is not owned by a view.
///   - color:      {r,g,b}, optional — a per-view graphic override, applied in this transaction.
///                 Ignored (with a warning) when viewId is absent.
///   - lineStyle:  string, optional — GraphicsStyle name. Reported in warnings when not found.
///
/// Returns: { id, length }  — length always in METRES regardless of the input units, plus
/// <c>warnings</c> when an optional step was skipped.
/// </summary>
public sealed class CreateModelLineCommand : IRevitCommand
{
    public string Name => "spatial_create_model_line";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        // P.Xyz treats anything that is not "feet" as metres, so an unsupported unit such as "mm"
        // would silently scale by 1000. Reject it rather than draw a line the caller did not ask for.
        var units = P.Units(p);
        if (units is not ("meters" or "feet"))
            throw new RevitCommandException("invalid_parameter",
                $"units must be 'meters' or 'feet', got '{units}'.");

        var start = P.Xyz(p, "start", units);
        var end = P.Xyz(p, "end", units);
        if (start.DistanceTo(end) < 1e-7)
            throw new RevitCommandException("bad_request", "start and end are the same point.");

        var line = Line.CreateBound(start, end);

        // A model curve needs a SketchPlane that CONTAINS the line. Build one from the line
        // direction and any axis not parallel to it, so a vertical chord works as well as a
        // horizontal one.
        var dir = (end - start).Normalize();
        var helper = Math.Abs(dir.DotProduct(XYZ.BasisZ)) > 0.99 ? XYZ.BasisX : XYZ.BasisZ;
        var normal = dir.CrossProduct(helper).Normalize();
        var sketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal, start));

        var curve = doc.Create.NewModelCurve(line, sketchPlane);

        var warnings = new JsonArray();

        if (p["color"] is JsonObject colorNode)
        {
            var viewId = P.LongOrNull(p, "viewId");
            if (viewId is null)
            {
                warnings.Add("color ignored: viewId is required, because a graphic override is "
                           + "per view and a model curve belongs to no single view.");
            }
            else if (doc.GetElement(new ElementId(viewId.Value)) is not View view)
            {
                throw new RevitCommandException("not_found", $"No view with id {viewId.Value}.");
            }
            else
            {
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(new Color(
                    P.ColorByte(colorNode, "r", 0),
                    P.ColorByte(colorNode, "g", 0),
                    P.ColorByte(colorNode, "b", 0)));
                view.SetElementOverrides(curve.Id, ogs);
            }
        }

        var styleName = P.StrOrNull(p, "lineStyle");
        if (!string.IsNullOrWhiteSpace(styleName))
        {
            var style = new FilteredElementCollector(doc)
                .OfClass(typeof(GraphicsStyle))
                .Cast<GraphicsStyle>()
                .FirstOrDefault(g => string.Equals(g.Name, styleName, StringComparison.OrdinalIgnoreCase));

            // Reported rather than swallowed: a silently ignored style looks identical to a
            // successfully applied one from the caller's side.
            if (style is null) warnings.Add($"lineStyle '{styleName}' not found; left at default.");
            else curve.LineStyle = style;
        }

        var result = new JsonObject
        {
            ["id"] = curve.Id.Value,
            ["length"] = curve.GeometryCurve.Length * P.FeetToMeters,
        };
        if (warnings.Count > 0) result["warnings"] = warnings;
        return result;
    }
}
