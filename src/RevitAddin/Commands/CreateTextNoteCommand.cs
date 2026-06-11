using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a TextNote in a view.
///
/// Params:
///   - text:     string, required
///   - location: { x, y, z? }, required
///   - viewId:   long, optional (defaults to active view)
///   - width:    number, optional (text wrap width, default 0.5 feet)
///   - units:    "meters"|"feet"
/// </summary>
public sealed class CreateTextNoteCommand : IRevitCommand
{
    public string Name => "create_text_note";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        var scale = units == "feet" ? 1.0 : P.MetersToFeet;

        var text = P.Str(p, "text");
        var location = P.Xyz(p, "location", units);

        var viewId = p["viewId"] is not null
            ? new ElementId(P.Long(p, "viewId"))
            : doc.ActiveView?.Id
            ?? throw new RevitCommandException("not_found", "No active view.");

        var width = P.DblOr(p, "width", 0.5) * scale;

        var textTypeId = new FilteredElementCollector(doc)
            .OfClass(typeof(TextNoteType))
            .FirstElementId();

        if (textTypeId == ElementId.InvalidElementId)
            throw new RevitCommandException("not_found", "No TextNoteType in document.");

        var options = new TextNoteOptions(textTypeId)
        {
            HorizontalAlignment = HorizontalTextAlignment.Left,
        };

        var note = TextNote.Create(doc, viewId, location, width, text, options);

        return new JsonObject
        {
            ["id"] = note.Id.Value,
            ["viewId"] = viewId.Value,
            ["text"] = text,
        };
    }
}
