using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a Ceiling from a closed polygonal profile.
///
/// Params:
///   - profile:        [{x,y,z?}, ...], >=3 points
///   - levelName:      string, optional
///   - ceilingTypeName: string, optional
///   - units:          "meters"|"feet"
/// </summary>
public sealed class CreateCeilingCommand : IRevitCommand
{
    public string Name => "create_ceiling";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var pts = P.XyzList(p, "profile", units);
        if (pts.Count < 3)
            throw new System.ArgumentException("Ceiling profile must have at least 3 points.");

        var level = CreateWallCommand.ResolveLevel(doc, P.StrOrNull(p, "levelName"));
        var ceilingType = ResolveCeilingType(doc, P.StrOrNull(p, "ceilingTypeName"));

        var loop = new CurveLoop();
        for (var i = 0; i < pts.Count; i++)
        {
            var a = new XYZ(pts[i].X, pts[i].Y, level.Elevation);
            var bRaw = pts[(i + 1) % pts.Count];
            var b = new XYZ(bRaw.X, bRaw.Y, level.Elevation);
            if (a.DistanceTo(b) < 1e-6)
                throw new System.ArgumentException($"Profile segment {i} has zero length.");
            loop.Append(Line.CreateBound(a, b));
        }

        var ceiling = Ceiling.Create(doc, new List<CurveLoop> { loop }, ceilingType.Id, level.Id);

        return new JsonObject
        {
            ["id"] = ceiling.Id.Value,
            ["levelName"] = level.Name,
            ["ceilingTypeName"] = ceilingType.Name,
        };
    }

    private static CeilingType ResolveCeilingType(Document doc, string? name)
    {
        var query = new FilteredElementCollector(doc)
            .OfClass(typeof(CeilingType))
            .Cast<CeilingType>();
        if (string.IsNullOrWhiteSpace(name))
            return query.FirstOrDefault()
                ?? throw new System.InvalidOperationException("No CeilingType in the document.");
        return query.FirstOrDefault(t => t.Name == name)
            ?? throw new System.InvalidOperationException($"CeilingType '{name}' not found.");
    }
}
