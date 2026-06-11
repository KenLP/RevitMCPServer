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
///   - reuseExisting:  bool, optional, default false. When true, a filter with
///                     the same name is reused rather than raising an error.
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

        // Views like schedules, legends, and some 3-D views do not support
        // parameter filters or graphic overrides.
        if (!view.AreGraphicsOverridesAllowed())
            throw new RevitCommandException("unsupported_view",
                $"View '{view.Name}' (type: {view.ViewType}) does not support " +
                "graphic overrides or parameter filters.");

        var filterName = P.Str(p, "filterName");
        var reuseExisting = P.BoolOr(p, "reuseExisting", false);

        // Detect name collision early — ParameterFilterElement.Create throws an
        // unhelpful exception otherwise.
        var existingFilter = new FilteredElementCollector(doc)
            .OfClass(typeof(ParameterFilterElement))
            .Cast<ParameterFilterElement>()
            .FirstOrDefault(f => f.Name.Equals(filterName, StringComparison.OrdinalIgnoreCase));

        var catName = P.Str(p, "category");
        if (!Enum.TryParse<BuiltInCategory>(catName, true, out var bic))
            throw new RevitCommandException("invalid_parameter", $"Unknown BuiltInCategory '{catName}'.");

        ParameterFilterElement filter;
        bool reused = false;

        if (existingFilter != null)
        {
            if (!reuseExisting)
                throw new RevitCommandException("name_collision",
                    $"A filter named '{filterName}' already exists " +
                    $"(id: {existingFilter.Id.Value}). " +
                    "Set reuseExisting:true to apply it to this view, or choose a different name.");

            filter = existingFilter;
            reused = true;
        }
        else
        {
            var paramName = P.Str(p, "parameterName");
            var matchValue = P.Str(p, "value");

            // Find the parameter id from an existing element of the target category.
            var sampleElement = new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .FirstOrDefault()
                ?? throw new RevitCommandException("not_found",
                    $"No elements of category '{catName}' found in the document. " +
                    $"The filter needs at least one existing element to resolve the " +
                    $"parameter '{paramName}'. Place an element of that category first.");

            var param = sampleElement.LookupParameter(paramName)
                ?? throw new RevitCommandException("not_found",
                    $"Parameter '{paramName}' not found on any '{catName}' element. " +
                    "Verify the parameter name and category.");

            var paramId = param.Id;
            var categories = new List<ElementId> { new ElementId(bic) };
            var rule = ParameterFilterRuleFactory.CreateEqualsRule(paramId, matchValue);
            var elementFilter = new ElementParameterFilter(rule);
            filter = ParameterFilterElement.Create(doc, filterName, categories, elementFilter);
        }

        // Apply to view.
        if (!view.GetFilters().Contains(filter.Id))
            view.AddFilter(filter.Id);

        var visible = P.BoolOr(p, "visible", true);

        var overrides = new OverrideGraphicSettings();
        if (p["colorRGB"] is JsonObject rgb)
        {
            var color = new Color(
                P.ColorByte(rgb, "r", 255),
                P.ColorByte(rgb, "g", 0),
                P.ColorByte(rgb, "b", 0));
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
            ["reused"] = reused,
        };
    }
}
