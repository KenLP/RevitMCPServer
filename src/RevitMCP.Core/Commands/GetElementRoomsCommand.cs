using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Get room containment for one or more FamilyInstance elements.
///
/// Uses the Revit API's phase-dependent room resolution (FamilyInstance.get_Room /
/// get_FromRoom / get_ToRoom) — NOT centroid-in-bbox. This is authoritative for
/// wall-hosted elements (doors/windows) whose centroid lies inside the wall, between
/// two rooms.
///
/// Params:
///   - ids: long[], required — ElementIds to query. 1–N elements per call.
///
/// Returns per element:
///   - id, phaseId (phase used to resolve)
///   - room:     { id, name, number } or null — point-located elements (Furniture, Fixtures, ...)
///   - fromRoom: { id, name, number } or null — boundary connectors (Doors, Windows, ...)
///   - toRoom:   { id, name, number } or null — boundary connectors; null for exterior side
///
/// Non-FamilyInstance elements (walls, floors, ...) return all room fields null.
/// </summary>
public sealed class GetElementRoomsCommand : IRevitCommand
{
    public string Name => "get_element_rooms";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var ids = P.Arr(ctx.Parameters, "ids");

        var results = new JsonArray();

        foreach (var idNode in ids)
        {
            var idValue = idNode!.GetValue<long>();
            var element = doc.GetElement(new ElementId(idValue));

            if (element is not FamilyInstance fi)
            {
                results.Add(new JsonObject
                {
                    ["id"]       = idValue,
                    ["phaseId"]  = (JsonNode?)null,
                    ["room"]     = (JsonNode?)null,
                    ["fromRoom"] = (JsonNode?)null,
                    ["toRoom"]   = (JsonNode?)null,
                });
                continue;
            }

            // Resolve phase: prefer element's Phase Created, fall back to last doc phase.
            Phase? phase = null;
            var phaseParam = fi.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (phaseParam?.AsElementId() is ElementId phaseEid && phaseEid.Value > 0)
                phase = doc.GetElement(phaseEid) as Phase;

            phase ??= new FilteredElementCollector(doc)
                .OfClass(typeof(Phase))
                .Cast<Phase>()
                .LastOrDefault();

            var phaseId = phase?.Id?.Value;

            // Phase-dependent room resolution. Each wrapped in try/catch:
            // Room Calculation Point may be disabled on some families → returns null or throws.
            Room? room = null, fromRoom = null, toRoom = null;
            try { room     = phase is not null ? fi.get_Room(phase)     : fi.Room;     } catch { }
            try { fromRoom = phase is not null ? fi.get_FromRoom(phase) : fi.FromRoom; } catch { }
            try { toRoom   = phase is not null ? fi.get_ToRoom(phase)   : fi.ToRoom;   } catch { }

            results.Add(new JsonObject
            {
                ["id"]       = idValue,
                ["phaseId"]  = phaseId,
                ["room"]     = RoomInfo(room),
                ["fromRoom"] = RoomInfo(fromRoom),
                ["toRoom"]   = RoomInfo(toRoom),
            });
        }

        return new JsonObject
        {
            ["count"]    = results.Count,
            ["elements"] = results,
        };
    }

    private static JsonObject? RoomInfo(Room? room)
    {
        if (room is null) return null;
        return new JsonObject
        {
            ["id"]     = room.Id.Value,
            ["name"]   = room.Name,
            ["number"] = room.Number,
        };
    }
}
