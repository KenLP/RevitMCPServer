using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a section view looking in a given direction, cutting through
/// a specified bounding box.
///
/// Params:
///   - origin:     { x, y, z }, section origin (meters by default)
///   - direction:  { x, y, z }, look direction (unit vector recommended)
///   - depth:      number, cut depth (default 10 m)
///   - width:      number, half-width of the section crop (default 10 m)
///   - height:     number, half-height of the section crop (default 5 m)
///   - viewName:   string, optional
///   - units:      "meters"|"feet"
/// </summary>
public sealed class CreateSectionViewCommand : IRevitCommand
{
    public string Name => "create_section_view";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        var scale = units == "feet" ? 1.0 : P.MetersToFeet;

        var origin = P.Xyz(p, "origin", units);
        var dir = P.Xyz(p, "direction", "feet"); // direction is unitless
        if (dir.GetLength() < 1e-9)
            throw new RevitCommandException("invalid_parameter", "Direction vector must be non-zero.");
        dir = dir.Normalize();

        var depth = P.DblOr(p, "depth", 10) * scale;
        var halfW = P.DblOr(p, "width", 10) * scale;
        var halfH = P.DblOr(p, "height", 5) * scale;

        var vft = CreateFloorPlanViewCommand.GetViewFamilyType(doc, ViewFamily.Section);

        // Build the section BoundingBoxXYZ.
        var up = XYZ.BasisZ;
        var right = dir.CrossProduct(up);
        if (right.GetLength() < 1e-9)
        {
            right = XYZ.BasisX;
            up = right.CrossProduct(dir);
        }
        right = right.Normalize();
        up = right.CrossProduct(dir).Normalize();

        var transform = Transform.Identity;
        transform.Origin = origin;
        transform.BasisX = right;
        transform.BasisY = up;
        transform.BasisZ = dir;

        var sectionBox = new BoundingBoxXYZ
        {
            Transform = transform,
            Min = new XYZ(-halfW, -halfH, 0),
            Max = new XYZ(halfW, halfH, depth),
        };

        var view = ViewSection.CreateSection(doc, vft.Id, sectionBox);

        var viewName = P.StrOrNull(p, "viewName");
        if (!string.IsNullOrWhiteSpace(viewName))
        {
            try { view.Name = viewName; } catch { }
        }

        return new JsonObject
        {
            ["id"] = view.Id.Value,
            ["name"] = view.Name,
            ["viewType"] = view.ViewType.ToString(),
        };
    }
}
