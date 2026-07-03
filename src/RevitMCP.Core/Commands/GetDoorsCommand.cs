using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// All placed doors with nominal width (metres), location (world XY, metres), level, and the swing
/// geometry (FacingOrientation / HandOrientation as resolved world unit vectors) — the data spatial-QC
/// needs to check door clear width, associate doors to egress routes, and test maneuvering clearance,
/// directly from the live Revit model (mirrors IfcDoor.OverallWidth + placement axes on the IFC path).
///
/// Note: FacingOrientation / HandOrientation are geometry, not parameters, so find_elements cannot
/// return them — this command exists to expose door swing orientation for ADA/egress checks.
/// </summary>
public sealed class GetDoorsCommand : IRevitCommand
{
    public string Name => "get_doors";
    public bool IsReadOnly => true;

    private static double? WidthFeet(FamilyInstance d)
    {
        foreach (var src in new Element?[] { d, d.Symbol })
        {
            if (src is null) continue;
            var bip = src.get_Parameter(BuiltInParameter.DOOR_WIDTH);
            if (bip is not null && bip.HasValue && bip.AsDouble() > 0) return bip.AsDouble();
            var named = src.LookupParameter("Width");
            if (named is not null && named.StorageType == StorageType.Double
                && named.HasValue && named.AsDouble() > 0) return named.AsDouble();
        }
        return null;
    }

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var doors = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Doors)
            .WhereElementIsNotElementType()
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .ToList();

        JsonNode? M(double? ft) => ft.HasValue ? JsonValue.Create(ft.Value * P.FeetToMeters) : null;

        var arr = new JsonArray();
        foreach (var d in doors)
        {
            var pt = (d.Location as LocationPoint)?.Point;
            if (pt is null)
            {
                var bb = d.get_BoundingBox(null);
                if (bb is not null) pt = (bb.Min + bb.Max) * 0.5;
            }
            var lvl = doc.GetElement(d.LevelId) as Level;
            // FacingOrientation = normal to the door (the swing / pull side); HandOrientation = along
            // the wall. Both are already resolved world unit vectors (flips applied). XY only (plan).
            var fo = d.FacingOrientation;
            var ho = d.HandOrientation;
            arr.Add(new JsonObject
            {
                ["id"] = d.Id.Value,
                ["name"] = d.Name,
                ["width"] = M(WidthFeet(d)),
                ["x"] = pt is not null ? JsonValue.Create(pt.X * P.FeetToMeters) : null,
                ["y"] = pt is not null ? JsonValue.Create(pt.Y * P.FeetToMeters) : null,
                ["levelName"] = lvl?.Name,
                ["facingX"] = JsonValue.Create(fo.X),
                ["facingY"] = JsonValue.Create(fo.Y),
                ["handX"] = JsonValue.Create(ho.X),
                ["handY"] = JsonValue.Create(ho.Y),
                ["facingFlipped"] = JsonValue.Create(d.FacingFlipped),
                ["handFlipped"] = JsonValue.Create(d.HandFlipped),
            });
        }
        return new JsonObject { ["count"] = doors.Count, ["doors"] = arr };
    }
}
