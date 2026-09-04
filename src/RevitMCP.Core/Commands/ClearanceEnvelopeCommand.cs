using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Spatial-QC pack (HTTP-only; command name prefixed <c>spatial_</c>, not exposed as an MCP tool).
///
/// Volumetric headroom / clear-height check over a room footprint — the general MEP-aware
/// clearance primitive. Extrudes the room footprint into a "required clear volume" (floor+ε up to
/// maxHeight) and boolean-intersects it against EVERY overhead element in the host model AND in
/// every loaded Revit link (MEP ducts/pipes/trays/conduit + fittings + insulation + sprinklers +
/// lighting + mechanical equipment, plus structural framing/columns and ceilings/floors/roofs).
///
/// Unlike a centerline raycast, this is a solid-solid interference: it catches an element hanging
/// over ANY part of the footprint (a duct at the edge of a parking bay, a pipe crossing diagonally)
/// regardless of position, and it names the offending element (category + id + link) with the exact
/// clear height it leaves. See ClearanceEnvelopeBatchCommand for the many-room batched variant.
///
/// Params: loops, floorZ (m), requiredHeight (m), maxHeight (m, def 8), minObstacleHeight (m, def
///         0.5), categories (string[] optional), units. Returns { minClearance, minLocation, clear,
///         obstructions:[{id,category,source,link,clearance,x,y,...}], samples, ... }.
/// </summary>
public sealed class ClearanceEnvelopeCommand : IRevitCommand
{
    public string Name => "spatial_clearance_envelope";
    public bool IsReadOnly => true;

    internal static readonly BuiltInCategory[] DefaultCats =
    {
        BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory,
        BuiltInCategory.OST_DuctInsulations, BuiltInCategory.OST_DuctTerminal,
        BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
        BuiltInCategory.OST_PipeInsulations, BuiltInCategory.OST_Sprinklers,
        BuiltInCategory.OST_PlumbingFixtures,
        BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting,
        BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs,
        BuiltInCategory.OST_GenericModel,
    };

    /// <summary>A cached overhead element: its solids (host coords) + XY bounds for quick reject.</summary>
    internal sealed class Cand
    {
        public long Id; public string Cat = "?"; public string Src = "host"; public string? Link;
        public IList<Solid> Solids = new List<Solid>();
        public double MinX, MinY, MaxX, MaxY;
    }

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        double scale = units.Equals("feet", StringComparison.OrdinalIgnoreCase) ? 1.0 : P.MetersToFeet;

        double floorZ = P.Dbl(p, "floorZ") * scale;
        double required = P.DblOr(p, "requiredHeight", 2.03) * scale;
        double maxH = P.DblOr(p, "maxHeight", 8.0) * scale;
        double minObs = P.DblOr(p, "minObstacleHeight", 0.5) * scale;
        double eps = 0.05 * P.MetersToFeet;

        var cats = ParseCats(p) ?? DefaultCats;
        var clearSolid = BuildClearVolume(P.Arr(p, "loops"), scale, floorZ + eps, maxH - eps);
        var cands = CollectCandidates(doc, cats, new[] { clearSolid });
        return BuildResult(clearSolid, cands, floorZ, minObs, scale, required / scale, maxH / scale);
    }

    // ── candidate collection (host + every loaded link) ───────────────────────
    // `probes` are the per-room clear solids; a union filter selects any element intersecting ANY
    // of them, so the same collected+extracted geometry can serve every room (the batch win).
    internal static List<Cand> CollectCandidates(Document doc, BuiltInCategory[] cats, IList<Solid> probes)
    {
        var catFilter = new ElementMulticategoryFilter(cats);
        var outp = new List<Cand>();

        void Harvest(Document d, Transform? tf, string src, string? link)
        {
            var filters = new List<ElementFilter>();
            foreach (var pr in probes)
            {
                Solid s = pr;
                if (tf != null) { try { s = SolidUtils.CreateTransformed(pr, tf.Inverse); } catch { continue; } }
                filters.Add(new ElementIntersectsSolidFilter(s));
            }
            if (filters.Count == 0) return;
            ElementFilter probe = filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters);
            foreach (var el in new FilteredElementCollector(d)
                         .WherePasses(catFilter).WhereElementIsNotElementType().WherePasses(probe))
            {
                var solids = ElementSolids(el, tf);
                if (solids.Count == 0) continue;
                var c = new Cand { Id = el.Id.Value, Cat = el.Category?.Name ?? "?", Src = src, Link = link, Solids = solids };
                (c.MinX, c.MinY, c.MaxX, c.MaxY) = SolidsXYBounds(solids);
                outp.Add(c);
            }
        }

        Harvest(doc, null, "host", null);
        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var ld = link.GetLinkDocument();
            if (ld == null) continue;
            Harvest(ld, link.GetTotalTransform(), "link", link.Name);
        }
        return outp;
    }

    // ── measure a room's clear volume against the candidate set + format the result ────────────
    internal static JsonObject BuildResult(Solid clearSolid, List<Cand> cands, double floorZ,
                                           double minObs, double scale, double reqM, double maxM)
    {
        var (rMinX, rMinY, rMaxX, rMaxY) = SolidXYBounds(clearSolid);
        var obstructions = new List<(long id, string cat, string src, string? link, double clr,
                                     double x, double y, double minx, double miny, double maxx, double maxy)>();
        foreach (var cand in cands)
        {
            if (cand.MaxX < rMinX || cand.MinX > rMaxX || cand.MaxY < rMinY || cand.MinY > rMaxY)
                continue;                                     // XY bbox disjoint from this room — skip
            double bestClr = double.MaxValue, bx = 0, by = 0;
            double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
            bool hit = false;
            foreach (var s in cand.Solids)
            {
                Solid? inter;
                try { inter = BooleanOperationsUtils.ExecuteBooleanOperation(clearSolid, s, BooleanOperationsType.Intersect); }
                catch { continue; }
                if (inter == null || inter.Volume < 1e-7) continue;
                hit = true;
                double minZ = double.MaxValue; double cx = 0, cy = 0; int n = 0;
                foreach (Edge e in inter.Edges)
                {
                    var c = e.AsCurve();
                    foreach (var pt in new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                    {
                        if (pt.Z < minZ) minZ = pt.Z;
                        cx += pt.X; cy += pt.Y; n++;
                        mnx = Math.Min(mnx, pt.X); mny = Math.Min(mny, pt.Y);
                        mxx = Math.Max(mxx, pt.X); mxy = Math.Max(mxy, pt.Y);
                    }
                }
                double clr = minZ - floorZ;
                if (clr < bestClr) { bestClr = clr; bx = n > 0 ? cx / n : 0; by = n > 0 ? cy / n : 0; }
            }
            if (!hit || bestClr < minObs) continue;
            obstructions.Add((cand.Id, cand.Cat, cand.Src, cand.Link, bestClr / scale, bx / scale, by / scale,
                              mnx / scale, mny / scale, mxx / scale, mxy / scale));
        }

        obstructions.Sort((a, b) => a.clr.CompareTo(b.clr));
        double minClr = obstructions.Count > 0 ? obstructions[0].clr : maxM;
        var minLoc = obstructions.Count > 0
            ? new JsonObject { ["x"] = obstructions[0].x, ["y"] = obstructions[0].y } : null;

        // ALWAYS keep the lowest obstruction per category first (every discipline represented even
        // when one dense low run dominates), then fill to the cap with the next-lowest overall.
        const int cap = 250;
        var chosen = new List<(long id, string cat, string src, string? link, double clr,
                               double x, double y, double minx, double miny, double maxx, double maxy)>();
        var seenCat = new HashSet<string>(); var seenId = new HashSet<long>();
        foreach (var o in obstructions) if (seenCat.Add(o.cat)) { chosen.Add(o); seenId.Add(o.id); }
        foreach (var o in obstructions) { if (chosen.Count >= cap) break; if (seenId.Add(o.id)) chosen.Add(o); }
        chosen.Sort((a, b) => a.clr.CompareTo(b.clr));

        var obsArr = new JsonArray();
        var samples = new JsonArray();
        foreach (var o in chosen)
        {
            obsArr.Add(new JsonObject
            {
                ["id"] = o.id, ["category"] = o.cat, ["source"] = o.src, ["link"] = o.link,
                ["clearance"] = Math.Round(o.clr, 3), ["x"] = Math.Round(o.x, 3), ["y"] = Math.Round(o.y, 3),
                ["bboxMin"] = new JsonObject { ["x"] = Math.Round(o.minx, 3), ["y"] = Math.Round(o.miny, 3) },
                ["bboxMax"] = new JsonObject { ["x"] = Math.Round(o.maxx, 3), ["y"] = Math.Round(o.maxy, 3) },
                ["below"] = o.clr < reqM,
            });
            samples.Add(new JsonObject { ["x"] = Math.Round(o.x, 3), ["y"] = Math.Round(o.y, 3), ["h"] = Math.Round(o.clr, 3) });
        }
        return new JsonObject
        {
            ["minClearance"] = Math.Round(minClr, 3),
            ["minLocation"] = minLoc,
            ["requiredHeight"] = Math.Round(reqM, 3),
            ["maxHeight"] = Math.Round(maxM, 3),
            ["clear"] = minClr >= reqM,
            ["nObstructions"] = obstructions.Count,
            ["nBelowRequired"] = obstructions.Count(o => o.clr < reqM),
            ["samples"] = samples,
            ["obstructions"] = obsArr,
        };
    }

    // ── geometry helpers (shared with the batch command) ──────────────────────
    internal static (double, double, double, double) SolidsXYBounds(IList<Solid> solids)
    {
        double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
        foreach (var s in solids)
        {
            var (a, b, c, d) = SolidXYBounds(s);
            mnx = Math.Min(mnx, a); mny = Math.Min(mny, b); mxx = Math.Max(mxx, c); mxy = Math.Max(mxy, d);
        }
        return (mnx, mny, mxx, mxy);
    }

    internal static (double, double, double, double) SolidXYBounds(Solid s)
    {
        double mnx = double.MaxValue, mny = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue;
        foreach (Edge e in s.Edges)
        {
            var cv = e.AsCurve();
            foreach (var pt in new[] { cv.GetEndPoint(0), cv.GetEndPoint(1) })
            {
                mnx = Math.Min(mnx, pt.X); mny = Math.Min(mny, pt.Y);
                mxx = Math.Max(mxx, pt.X); mxy = Math.Max(mxy, pt.Y);
            }
        }
        return (mnx, mny, mxx, mxy);
    }

    internal static Solid BuildClearVolume(JsonArray loopsArr, double scale, double zBase, double height)
    {
        var loops = new List<CurveLoop>();
        int idx = 0;
        foreach (var loopNode in loopsArr)
        {
            if (loopNode is not JsonArray raw) { idx++; continue; }
            var pts = new List<XYZ>();
            foreach (var ptNode in raw)
            {
                if (ptNode is not JsonArray xy || xy.Count < 2) continue;
                var q = new XYZ(P.DblFrom(xy[0], "loops[][][0]") * scale, P.DblFrom(xy[1], "loops[][][1]") * scale, zBase);
                if (pts.Count == 0 || pts[^1].DistanceTo(q) > 1e-4) pts.Add(q);
            }
            if (pts.Count >= 2 && pts[0].DistanceTo(pts[^1]) < 1e-4) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) { idx++; continue; }
            bool wantCcw = idx == 0;
            if (SignedArea(pts) < 0 == wantCcw) pts.Reverse();
            var cl = new CurveLoop();
            for (int i = 0; i < pts.Count; i++) cl.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
            loops.Add(cl);
            idx++;
        }
        if (loops.Count == 0)
            throw new RevitCommandException("bad_request", "No usable footprint loop in 'loops'.");
        return GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, height);
    }

    internal static double SignedArea(IList<XYZ> pts)
    {
        double a = 0;
        for (int i = 0; i < pts.Count; i++) { var j = (i + 1) % pts.Count; a += pts[i].X * pts[j].Y - pts[j].X * pts[i].Y; }
        return a / 2.0;
    }

    internal static IList<Solid> ElementSolids(Element el, Transform? tf)
    {
        var opts = new Options { DetailLevel = ViewDetailLevel.Medium, ComputeReferences = false, IncludeNonVisibleObjects = false };
        var outp = new List<Solid>();
        var geo = el.get_Geometry(opts);
        if (geo == null) return outp;
        CollectSolids(geo, tf, outp);
        return outp;
    }

    internal static void CollectSolids(GeometryElement geo, Transform? tf, List<Solid> outp)
    {
        foreach (var go in geo)
        {
            if (go is Solid s && s.Volume > 1e-7)
                outp.Add(tf == null ? s : SolidUtils.CreateTransformed(s, tf));
            else if (go is GeometryInstance gi)
                CollectSolids(gi.GetInstanceGeometry(), tf, outp);
        }
    }

    internal static BuiltInCategory[]? ParseCats(JsonObject p)
    {
        if (p["categories"] is not JsonArray arr || arr.Count == 0) return null;
        var list = new List<BuiltInCategory>();
        foreach (var n in arr)
            if (n != null && Enum.TryParse<BuiltInCategory>(P.StrFrom(n, "categories[]"), ignoreCase: true, out var bic))
                list.Add(bic);
        return list.Count > 0 ? list.ToArray() : null;
    }
}
