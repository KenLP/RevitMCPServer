using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Apply per-element graphic overrides (color, transparency) to specific elements in a view.
/// Params:
///   viewId      long, required — ElementId of the view.
///   elementIds  long[], required — elements to override.
///   color       { r, g, b } object, optional — projection + surface fill color. Default red (255,0,0).
///   transparency int 0–100, optional — surface transparency %. Default 0.
///   reset       bool, optional — if true, clear overrides instead of applying. Default false.
/// </summary>
public sealed class OverrideElementGraphicsCommand : IRevitCommand
{
    public string Name => "override_element_graphics";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";
    public ExecutionKind Execution => ExecutionKind.ModelWrite;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var viewIdVal = p["viewId"]?.GetValue<long>()
            ?? throw new RevitCommandException("bad_request", "Missing required parameter 'viewId'.");
        var idsNode = p["elementIds"] as JsonArray
            ?? throw new RevitCommandException("bad_request", "Missing required parameter 'elementIds'.");

        var view = doc.GetElement(new ElementId(viewIdVal)) as View
            ?? throw new RevitCommandException("not_found", $"No View with id {viewIdVal}.");

        var reset = p["reset"]?.GetValue<bool>() ?? false;

        // Parse color (default red)
        int r = 255, g = 0, b = 0;
        if (p["color"] is JsonObject colorNode)
        {
            r = colorNode["r"]?.GetValue<int>() ?? 255;
            g = colorNode["g"]?.GetValue<int>() ?? 0;
            b = colorNode["b"]?.GetValue<int>() ?? 0;
        }
        var transparency = Math.Clamp(p["transparency"]?.GetValue<int>() ?? 0, 0, 100);

        // Find solid fill pattern
        ElementId? solidFillId = null;
        if (!reset)
        {
            solidFillId = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill)?.Id;
        }

        var overridden = new List<long>();
        var skipped = new List<long>();

        foreach (var node in idsNode)
        {
            if (node == null) continue;
            var elId = new ElementId(node.GetValue<long>());
            var el = doc.GetElement(elId);
            if (el == null) { skipped.Add(elId.Value); continue; }

            if (reset)
            {
                view.SetElementOverrides(elId, new OverrideGraphicSettings());
            }
            else
            {
                var ogs = new OverrideGraphicSettings();
                var color = new Color((byte)r, (byte)g, (byte)b);
                ogs.SetProjectionLineColor(color);
                ogs.SetCutLineColor(color);
                if (solidFillId != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solidFillId);
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetCutForegroundPatternId(solidFillId);
                    ogs.SetCutForegroundPatternColor(color);
                }
                ogs.SetSurfaceTransparency(transparency);
                view.SetElementOverrides(elId, ogs);
            }
            overridden.Add(elId.Value);
        }

        return new JsonObject
        {
            ["viewId"] = viewIdVal,
            ["overridden"] = overridden.Count,
            ["skipped"] = skipped.Count,
            ["reset"] = reset,
            ["color"] = reset ? null : new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b },
            ["changeSummary"] = reset
                ? $"Cleared overrides for {overridden.Count} elements in view {viewIdVal}"
                : $"Applied color ({r},{g},{b}) to {overridden.Count} elements in view {viewIdVal}",
        };
    }
}
