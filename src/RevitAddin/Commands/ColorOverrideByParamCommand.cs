using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Color-code elements in a view by a parameter value.
///
/// Params:
///   - viewId:         long, optional
///   - category:       BuiltInCategory name
///   - parameterName:  string
///   - colorMap:       { "value1": { r,g,b }, "value2": { r,g,b }, ... }
///                     Values not in the map keep their default appearance.
/// </summary>
public sealed class ColorOverrideByParamCommand : IRevitCommand
{
    public string Name => "color_override_by_param";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var view = SetViewDetailLevelCommand.ResolveView(doc, ctx, p);

        if (!view.AreGraphicsOverridesAllowed())
            throw new RevitCommandException("unsupported_view",
                $"View '{view.Name}' (type: {view.ViewType}) does not support " +
                "element graphic overrides.");

        var catName = P.Str(p, "category");
        if (!Enum.TryParse<BuiltInCategory>(catName, true, out var bic))
            throw new ArgumentException($"Unknown BuiltInCategory '{catName}'.");

        var paramName = P.Str(p, "parameterName");
        var colorMapNode = P.Obj(p, "colorMap");

        var colorMap = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in colorMapNode)
        {
            if (kv.Value is JsonObject rgb)
            {
                colorMap[kv.Key] = new Color(
                    P.ColorByte(rgb, "r", 200),
                    P.ColorByte(rgb, "g", 200),
                    P.ColorByte(rgb, "b", 200));
            }
        }

        var elements = new FilteredElementCollector(doc, view.Id)
            .OfCategory(bic)
            .WhereElementIsNotElementType()
            .ToList();

        var applied = 0;
        foreach (var el in elements)
        {
            var param = el.LookupParameter(paramName);
            var val = param is not null && param.HasValue
                ? (param.AsValueString() ?? param.AsString() ?? "")
                : "";

            if (colorMap.TryGetValue(val, out var color))
            {
                var ogs = new OverrideGraphicSettings();
                ogs.SetSurfaceForegroundPatternColor(color);
                ogs.SetProjectionLineColor(color);
                view.SetElementOverrides(el.Id, ogs);
                applied++;
            }
        }

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["totalElements"] = elements.Count,
            ["overridesApplied"] = applied,
        };
    }
}
