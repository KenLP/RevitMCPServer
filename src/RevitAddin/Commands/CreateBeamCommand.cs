using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Place a structural beam between two points.
///
/// Params:
///   - start, end:     { x, y, z? }
///   - levelName:      string, optional
///   - familyTypeName: string, optional
///   - familyName:     string, optional
///   - structural:     bool, default true
///   - units:          "meters"|"feet"
/// </summary>
public sealed class CreateBeamCommand : IRevitCommand
{
    public string Name => "create_beam";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var start = P.Xyz(p, "start", units);
        var end = P.Xyz(p, "end", units);
        if (start.DistanceTo(end) < 1e-6)
            throw new ArgumentException("Start and end points are coincident.");

        var level = CreateWallCommand.ResolveLevel(doc, P.StrOrNull(p, "levelName"));
        var structural = P.BoolOr(p, "structural", true);

        var symbol = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .Cast<FamilySymbol>()
            .Where(s =>
                (string.IsNullOrWhiteSpace(P.StrOrNull(p, "familyName"))
                 || s.FamilyName.Equals(P.StrOrNull(p, "familyName"), StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(P.StrOrNull(p, "familyTypeName"))
                    || s.Name.Equals(P.StrOrNull(p, "familyTypeName"), StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No beam FamilySymbol found.");

        if (!symbol.IsActive) symbol.Activate();

        var line = Line.CreateBound(
            new XYZ(start.X, start.Y, level.Elevation),
            new XYZ(end.X, end.Y, level.Elevation));
        var stype = structural ? StructuralType.Beam : StructuralType.NonStructural;
        var instance = doc.Create.NewFamilyInstance(line, symbol, level, stype);

        return new JsonObject
        {
            ["id"] = instance.Id.Value,
            ["familyTypeName"] = symbol.Name,
            ["familyName"] = symbol.FamilyName,
            ["levelName"] = level.Name,
            ["lengthFeet"] = line.Length,
        };
    }
}
