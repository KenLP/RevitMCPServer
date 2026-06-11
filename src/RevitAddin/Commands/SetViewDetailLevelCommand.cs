using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Set the detail level of a view.
///
/// Params:
///   - viewId:      long, optional (defaults to active view)
///   - detailLevel: "Coarse"|"Medium"|"Fine"
/// </summary>
public sealed class SetViewDetailLevelCommand : IRevitCommand
{
    public string Name => "set_view_detail_level";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var view = ResolveView(doc, ctx, p);
        var levelStr = P.Str(p, "detailLevel");
        if (!Enum.TryParse<ViewDetailLevel>(levelStr, true, out var level))
            throw new ArgumentException($"Unknown detail level '{levelStr}'. Use Coarse, Medium, or Fine.");

        view.DetailLevel = level;

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["name"] = view.Name,
            ["detailLevel"] = view.DetailLevel.ToString(),
        };
    }

    internal static View ResolveView(Document doc, CommandContext ctx, JsonObject p)
    {
        if (p["viewId"] is not null)
        {
            var id = new ElementId(P.Long(p, "viewId"));
            return doc.GetElement(id) as View
                ?? throw new RevitCommandException("not_found", $"View {id.Value} not found.");
        }
        return doc.ActiveView ?? throw new RevitCommandException("not_found", "No active view.");
    }
}
