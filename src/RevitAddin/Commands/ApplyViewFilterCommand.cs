using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a ParameterFilterElement with a single equality rule and apply it
/// to a view with overrides.
///
/// Params:
///   - viewId:         long, optional (active view)
///   - filterName:     string, required
///   - category:       BuiltInCategory name, required
///   - parameterName:  string, required
///   - value:          string, required (the equality match value)
///   - colorRGB:       { r, g, b }, optional — projection fill color override
///   - visible:        bool, optional, default true (false = hide matching elements)
/// </summary>
public sealed class ApplyViewFilterCommand : IRevitCommand
{
    public string Name => "apply_view_filter";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var view = SetViewDetailLevelCommand.ResolveView(doc, ctx, p);
        var filterName = P.Str(p, "filterName");

        var catName = P.Str(p, "category");
        if (!Enum.TryParse<BuiltInCategory>(catName, true, out var bic))
            throw new ArgumentException($"Unknown BuiltInCategory '{catName}'.");

        var paramName = P.Str(p, "parameterName");
        var matchValue = P.Str(p, "value");

        // Find the parameter id from an element in this category.
        var sampleElement = new FilteredElementCollector(doc)
            .OfCategory(bic)
            .WhereElementIsNotElementType()
            .FirstOrDefault();

        var param = sampleElement?.LookupParameter(paramName)
            ?? throw new InvalidOperationException(
                $"Cannot find parameter '{paramName}' on any {catName} element.");

        var paramId = param.Id;

        var categories = new List<ElementId> { new ElementId(bic) };

        // Build filter rule (string equals).
        var rule = ParameterFilterRuleFactory.CreateEqualsRule(paramId, matchValue);
        var elementFilter = new ElementParameterFilter(rule);

        var filter = ParameterFilterElement.Create(doc, filterName, categories, elementFilter);

        // Apply to view.
        view.AddFilter(filter.Id);

        var visible = P.BoolOr(p, "visible", true);

        var overrides = new OverrideGraphicSettings();
        if (p["colorRGB"] is JsonObject rgb)
        {
            var color = new Color(
                (byte)P.IntOr(rgb, "r", 255),
                (byte)P.IntOr(rgb, "g", 0),
                (byte)P.IntOr(rgb, "b", 0));
            overrides.SetSurfaceForegroundPatternColor(color);
            overrides.SetProjectionLineColor(color);
        }

        view.SetFilterOverrides(filter.Id, overrides);
        view.SetFilterVisibility(filter.Id, visible);

        return new JsonObject
        {
            ["filterId"] = filter.Id.Value,
            ["filterName"] = filterName,
            ["viewId"] = view.Id.Value,
            ["visible"] = visible,
        };
    }
}
