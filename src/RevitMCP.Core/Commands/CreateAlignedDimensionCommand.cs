using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create an aligned dimension chain between two or more element references in a view.
///
/// Reference strategy (see The Building Coder, "Rebar, Wall Centreline, Core and
/// Grid Dimensioning", 2018):
///   - Grid : use the GRID ELEMENT reference (<c>new Reference(grid)</c>), NOT the
///            grid curve's geometry reference. The curve reference is a surface and
///            does not resolve in NewDimension.
///   - Wall : prefer the wall CENTRELINE / core face via the undocumented
///            ":-9999:" stable representation. Trailing index:
///              1 = overall centreline, 2 = core exterior face,
///              3 = core interior face, 4 = core centre.
///            Falls back to HostObjectUtils.GetSideFaces (works for plain walls).
///   - Wall + grid references CAN be mixed in ONE ReferenceArray.
///
/// Params:
///   - references: array of { elementId: long, side?: "exterior"|"interior"|"centre"|"core" }
///       Wall: "centre"/"auto" (default) = centreline; "exterior"/"interior" = face;
///             "core" = core centre.
///       Other elements (Grid, column, FamilyInstance, etc.): element reference used directly.
///   - line:    { start: {x,y,z}, end: {x,y,z} } — position and direction of the dimension line
///   - viewId:  long, optional (defaults to active view)
///   - units:   "meters"|"feet", default "meters"
/// </summary>
public sealed class CreateAlignedDimensionCommand : IRevitCommand
{
    public string Name => "create_aligned_dimension";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var viewId = p["viewId"] is not null
            ? new ElementId(P.Long(p, "viewId"))
            : doc.ActiveView?.Id
            ?? throw new RevitCommandException("not_found", "No active view.");

        var view = doc.GetElement(viewId) as View
            ?? throw new RevitCommandException("not_found", $"View {viewId.Value} not found.");

        // Dimension line
        var lineObj = p["line"] as JsonObject
            ?? throw new RevitCommandException("bad_request", "'line' with start/end is required.");
        var lineStart = P.Xyz(lineObj, "start", units);
        var lineEnd = P.Xyz(lineObj, "end", units);

        if (lineStart.DistanceTo(lineEnd) < 1e-6)
            throw new RevitCommandException("bad_request", "'line' start and end must not be the same point.");

        var dimLine = Line.CreateBound(lineStart, lineEnd);

        // References
        var refsArr = p["references"] as JsonArray
            ?? throw new RevitCommandException("bad_request", "'references' array is required.");

        if (refsArr.Count < 2)
            throw new RevitCommandException("bad_request", "At least 2 references are required.");

        var refArray = new ReferenceArray();
        var methods = new JsonArray();
        foreach (var refNode in refsArr)
        {
            var refObj = refNode as JsonObject
                ?? throw new RevitCommandException("bad_request", "Each reference entry must be a JSON object.");

            var eid = new ElementId(refObj["elementId"]!.GetValue<long>());
            var element = doc.GetElement(eid)
                ?? throw new RevitCommandException("not_found", $"Element {eid.Value} not found.");

            var side = refObj["side"]?.GetValue<string>() ?? "auto";
            refArray.Append(GetReference(doc, element, side, view, methods));
        }

        var dim = doc.Create.NewDimension(view, dimLine, refArray);
        if (dim is null)
            throw new RevitCommandException("command_failed",
                "NewDimension returned null — the references are not all visible in the target view, " +
                "or the dimension line does not cross them.");

        return new JsonObject
        {
            ["dimensionId"] = dim.Id.Value,
            ["value"] = dim.Value,
            ["segments"] = dim.Segments?.Size ?? 1,
            ["viewId"] = viewId.Value,
            ["references"] = methods,
        };
    }

    private static Reference GetReference(Document doc, Element element, string side, View view, JsonArray methods)
    {
        // GRID — element reference, NOT the curve's geometry reference.
        if (element is Grid grid)
        {
            methods.Add($"grid {grid.Id.Value}: element-ref");
            return new Reference(grid);
        }

        if (element is Wall wall)
            return GetWallReference(doc, wall, side, methods);

        if (element is ReferencePlane rp)
        {
            methods.Add($"refplane {rp.Id.Value}: plane-ref");
            return rp.GetReference();
        }

        // FamilyInstance / column / beam — face reference.
        var opts = new Options
        {
            View = view,
            ComputeReferences = true,
        };
        var geom = element.get_Geometry(opts);
        if (geom is not null)
        {
            foreach (var gObj in geom)
            {
                if (gObj is Solid solid)
                    foreach (Face face in solid.Faces)
                        if (face.Reference is not null)
                        {
                            methods.Add($"elem {element.Id.Value}: face-ref");
                            return face.Reference;
                        }
                if (gObj is GeometryInstance gi)
                    foreach (var g2 in gi.GetInstanceGeometry())
                        if (g2 is Solid s2)
                            foreach (Face f in s2.Faces)
                                if (f.Reference is not null)
                                {
                                    methods.Add($"elem {element.Id.Value}: inst-face-ref");
                                    return f.Reference;
                                }
            }
        }

        methods.Add($"elem {element.Id.Value}: element-ref (fallback)");
        return new Reference(element);
    }

    /// <summary>
    /// Wall reference. Prefers the centreline / core via the undocumented
    /// ":-9999:" stable representation; falls back to GetSideFaces.
    /// </summary>
    private static Reference GetWallReference(Document doc, Wall wall, string side, JsonArray methods)
    {
        bool wantFace = string.Equals(side, "exterior", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(side, "interior", StringComparison.OrdinalIgnoreCase);

        // Explicit exterior/interior → the proven GetSideFaces path first.
        if (wantFace)
        {
            var face = TryGetSideFace(wall, side);
            if (face is not null)
            {
                methods.Add($"wall {wall.Id.Value}: sideface-{side}");
                return face;
            }
        }

        // Centreline / core via stable representation.
        //   1 = overall centreline, 2 = core exterior, 3 = core interior, 4 = core centre
        int index = side.ToLowerInvariant() switch
        {
            "exterior" => 2,
            "interior" => 3,
            "core" => 4,
            _ => 1, // auto / centre / centreline
        };
        try
        {
            var r = Reference.ParseFromStableRepresentation(doc, $"{wall.UniqueId}:-9999:{index}");
            if (r is not null)
            {
                methods.Add($"wall {wall.Id.Value}: stablerep-{index}");
                return r;
            }
        }
        catch (Exception ex)
        {
            methods.Add($"wall {wall.Id.Value}: stablerep-{index} failed ({ex.Message})");
        }

        // Final fallback: any side face.
        var fb = TryGetSideFace(wall, "exterior") ?? TryGetSideFace(wall, "interior");
        if (fb is not null)
        {
            methods.Add($"wall {wall.Id.Value}: sideface-fallback");
            return fb;
        }

        throw new RevitCommandException("not_found",
            $"Wall {wall.Id.Value}: no usable reference (stable-rep :-9999:{index} and side faces all failed).");
    }

    private static Reference? TryGetSideFace(Wall wall, string side)
    {
        var shell = string.Equals(side, "interior", StringComparison.OrdinalIgnoreCase)
            ? ShellLayerType.Interior
            : ShellLayerType.Exterior;
        try { return HostObjectUtils.GetSideFaces(wall, shell).FirstOrDefault(); }
        catch { return null; }
    }
}
