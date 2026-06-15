using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Set (or clear) the section box of a 3D view, cropping it to a bounding
/// region.  Unlike HideElements, a section box crops EVERYTHING in the volume —
/// including linked-file geometry — so it is the reliable way to isolate a
/// clearance-violation region that spans host elements + linked MEP.
///
/// Params:
///   - viewId:    long, optional — defaults to active view; must resolve to a View3D.
///   - min:       {x,y,z} — lower corner (required unless enable=false).
///   - max:       {x,y,z} — upper corner (required unless enable=false).
///   - units:     "feet" (default) | "mm" | "meters" — units of min/max.
///   - paddingMm: double, optional (default 0) — expand the box outward on all sides.
///   - enable:    bool, optional (default true) — false deactivates the section box.
/// </summary>
public sealed class SetSectionBoxCommand : IRevitCommand
{
    public string Name => "set_section_box";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium"; // modifies view state

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var view = SetViewDetailLevelCommand.ResolveView(doc, ctx, p) as View3D
            ?? throw new RevitCommandException("invalid_parameter",
                "set_section_box requires a 3D view (View3D).");

        if (!P.BoolOr(p, "enable", true))
        {
            view.IsSectionBoxActive = false;
            return new JsonObject { ["viewId"] = view.Id.Value, ["sectionBoxActive"] = false };
        }

        // Resolve units → scale to Revit internal feet.
        var units = (P.StrOrNull(p, "units") ?? "feet").ToLowerInvariant();
        double scale = units switch
        {
            "feet" => 1.0,
            "mm" => 1.0 / 304.8,
            "meters" or "m" => P.MetersToFeet,
            _ => throw new RevitCommandException("invalid_parameter",
                     $"Unknown units '{units}'. Use feet, mm, or meters.")
        };

        var minObj = P.Obj(p, "min");
        var maxObj = P.Obj(p, "max");
        var min = new XYZ(P.DblOr(minObj, "x", 0) * scale, P.DblOr(minObj, "y", 0) * scale, P.DblOr(minObj, "z", 0) * scale);
        var max = new XYZ(P.DblOr(maxObj, "x", 0) * scale, P.DblOr(maxObj, "y", 0) * scale, P.DblOr(maxObj, "z", 0) * scale);

        var padFt = P.DblOr(p, "paddingMm", 0) / 304.8;
        var lo = new XYZ(Math.Min(min.X, max.X) - padFt, Math.Min(min.Y, max.Y) - padFt, Math.Min(min.Z, max.Z) - padFt);
        var hi = new XYZ(Math.Max(min.X, max.X) + padFt, Math.Max(min.Y, max.Y) + padFt, Math.Max(min.Z, max.Z) + padFt);

        var bb = new BoundingBoxXYZ { Min = lo, Max = hi };
        view.SetSectionBox(bb);
        view.IsSectionBoxActive = true;

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["name"] = view.Name,
            ["sectionBoxActive"] = true,
            ["minFt"] = new JsonObject { ["x"] = lo.X, ["y"] = lo.Y, ["z"] = lo.Z },
            ["maxFt"] = new JsonObject { ["x"] = hi.X, ["y"] = hi.Y, ["z"] = hi.Z },
        };
    }
}
