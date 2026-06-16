using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Apply or remove a view template from a view.
///
/// Params:
///   - viewId:       long, required — the view to modify.
///   - templateId:   long, optional — ElementId of the template. Pass -1 to remove.
///   - templateName: string, optional — template name, looked up by exact (case-insensitive) match.
///                   If both templateId and templateName are omitted, the template is removed.
/// </summary>
public sealed class ApplyViewTemplateCommand : IRevitCommand
{
    public string Name => "apply_view_template";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var viewIdValue = P.Long(p, "viewId");
        var view = doc.GetElement(new ElementId(viewIdValue)) as View
            ?? throw new RevitCommandException("not_found", $"No view with id {viewIdValue}.");

        var oldTemplateId   = view.ViewTemplateId;
        var oldTemplateName = oldTemplateId != ElementId.InvalidElementId
            ? doc.GetElement(oldTemplateId)?.Name
            : null;

        // Resolve the new template id.
        ElementId newTemplateId;
        var templateIdNode = p["templateId"];
        var templateName   = P.StrOrNull(p, "templateName");

        if (templateIdNode != null)
        {
            var raw = templateIdNode.GetValue<long>();
            if (raw < 0)
            {
                newTemplateId = ElementId.InvalidElementId;
            }
            else
            {
                newTemplateId = new ElementId(raw);
                var tmpl = doc.GetElement(newTemplateId) as View;
                if (tmpl == null || !tmpl.IsTemplate)
                    throw new RevitCommandException("not_found",
                        $"No view template with id {raw}.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(templateName))
        {
            var tmpl = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => v.IsTemplate &&
                    string.Equals(v.Name, templateName, StringComparison.OrdinalIgnoreCase))
                ?? throw new RevitCommandException("not_found",
                    $"No view template named '{templateName}'. Use revit_list_view_templates to list available templates.");
            newTemplateId = tmpl.Id;
        }
        else
        {
            newTemplateId = ElementId.InvalidElementId;
        }

        try
        {
            view.ViewTemplateId = newTemplateId;
        }
        catch (Exception ex) when (ex is not RevitCommandException)
        {
            throw new RevitCommandException("unsupported_view",
                $"Cannot apply view template to view '{view.Name}' (type: {view.ViewType}): {ex.Message}");
        }

        var newTemplateName = newTemplateId != ElementId.InvalidElementId
            ? doc.GetElement(newTemplateId)?.Name
            : null;

        return new JsonObject
        {
            ["viewId"]          = viewIdValue,
            ["viewName"]        = view.Name,
            ["oldTemplateId"]   = oldTemplateId.Value,
            ["oldTemplateName"] = oldTemplateName,
            ["newTemplateId"]   = newTemplateId.Value,
            ["newTemplateName"] = newTemplateName,
            ["changeSummary"]   = newTemplateName != null
                ? $"Applied template '{newTemplateName}' to view '{view.Name}'"
                : $"Removed template from view '{view.Name}' (was '{oldTemplateName ?? "none"}')",
        };
    }
}
