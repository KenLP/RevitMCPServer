using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Place a Room at a given point.
///
/// Params:
///   - location:   { x, y, z? }, required — point inside the room-bounding walls
///   - levelName:  string, optional (defaults to lowest level)
///   - name:       string, optional
///   - number:     string, optional
///   - units:      "meters"|"feet"
/// </summary>
public sealed class CreateRoomCommand : IRevitCommand
{
    public string Name => "create_room";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var location = P.Xyz(p, "location", units);
        var level = CreateWallCommand.ResolveLevel(doc, P.StrOrNull(p, "levelName"));

        var pt = new UV(location.X, location.Y);
        var room = doc.Create.NewRoom(level, pt);

        var nameVal = P.StrOrNull(p, "name");
        if (!string.IsNullOrWhiteSpace(nameVal))
            room.Name = nameVal;

        var numberVal = P.StrOrNull(p, "number");
        if (!string.IsNullOrWhiteSpace(numberVal))
            room.Number = numberVal;

        return new JsonObject
        {
            ["id"] = room.Id.Value,
            ["name"] = room.Name,
            ["number"] = room.Number,
            ["levelName"] = level.Name,
        };
    }
}
