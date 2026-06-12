using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Detect hard clashes or clearance violations between two sets of elements.
/// Supports host-only and cross-linked-file checks.
///
/// Params:
///   - setA:        { source: "host"|"link", linkId?: long, categories?: string[], limit?: int }
///   - setB:        same structure
///   - axis:        "bbox" (default) | "Z"
///   - direction:   "below" (default) | "above"  — only used when axis="Z"
///   - viewId:      ElementId of a View3D for raycast — required when axis="Z"
///   - clearanceMm: double, optional, default 0.
///                  bbox mode: flags pairs whose AABB overlaps after inflating setA by this margin.
///                  Z mode:    flags pairs where measured vertical distance < clearanceMm.
///   - sampleCount: int, optional, default 3 (range 1–10). axis=Z only.
///                  Number of points sampled along the element's centerline (LocationCurve).
///                  Use 3 for most cases; increase to 5 for long sloped elements spanning
///                  multiple floor slabs.  Falls back to a single bbox-centre point for
///                  elements without a LocationCurve.
///   - maxResults:  int, optional, default 200.
///
/// Methods:
///   - axis=bbox, host-vs-host, clearanceMm=0: ElementIntersectsElementFilter (exact solid-based).
///   - axis=bbox, otherwise:                   AABB inflation (conservative, cross-doc safe).
///   - axis=Z:                                 ReferenceIntersector vertical raycast per setA element.
///                                             Samples sampleCount points along the centerline.
///                                             Reports clearanceActualMm for each hit.
///                                             One violation row per (setA, setB) pair that
///                                             falls below the threshold across any sample point.
///                                             setB can be host or one linked file.
/// </summary>
public sealed class CheckClearanceCommand : IRevitCommand
{
    public string Name => "check_clearance";
    public bool IsReadOnly => true;

    private record ElementInfo(long Id, string Name, string? Category, XYZ? BboxMin, XYZ? BboxMax, string Source, long? LinkId);

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var setANode = p["setA"] as JsonObject
            ?? throw new RevitCommandException("bad_request", "Missing required parameter 'setA'.");
        var setBNode = p["setB"] as JsonObject
            ?? throw new RevitCommandException("bad_request", "Missing required parameter 'setB'.");

        var clearanceMm = P.DblOr(p, "clearanceMm", 0.0);
        var maxResults = Math.Clamp(P.IntOr(p, "maxResults", 200), 1, 2000);
        var clearanceFt = clearanceMm / 304.8;
        var axis = p["axis"]?.GetValue<string>() ?? "bbox";
        var direction = p["direction"]?.GetValue<string>() ?? "below";
        var viewIdNode = p["viewId"];
        var sampleCount = Math.Clamp(P.IntOr(p, "sampleCount", 3), 1, 10);

        var setAItems = CollectItems(doc, setANode);
        var setBItems = CollectItems(doc, setBNode);

        var sourceA = setANode["source"]?.GetValue<string>() ?? "host";
        var sourceB = setBNode["source"]?.GetValue<string>() ?? "host";
        var bothHost = sourceA == "host" && sourceB == "host";

        var useRaycast = axis.Equals("Z", StringComparison.OrdinalIgnoreCase);
        var useNative = !useRaycast && bothHost && clearanceMm == 0.0;

        var clashes = new JsonArray();
        var seen = new HashSet<(long, long)>();

        string method;
        if (useRaycast)
        {
            RunRaycastClash(doc, setAItems, setBItems, setBNode, clearanceMm, direction, viewIdNode, maxResults, sampleCount, clashes, seen);
            method = "ReferenceIntersectorZ";
        }
        else if (useNative)
        {
            RunNativeClash(doc, setAItems, setBItems, maxResults, clashes, seen);
            method = "ElementIntersectsElementFilter";
        }
        else
        {
            RunBboxClash(setAItems, setBItems, clearanceFt, maxResults, clashes, seen);
            method = "BoundingBoxIntersection";
        }

        return new JsonObject
        {
            ["clashCount"] = clashes.Count,
            ["clearanceMm"] = clearanceMm,
            ["limited"] = clashes.Count >= maxResults,
            ["method"] = method,
            ["axis"] = axis,
            ["direction"] = useRaycast ? direction : null,
            ["sampleCount"] = useRaycast ? sampleCount : (int?)null,
            ["clashes"] = clashes,
        };
    }

    // ── Element collection ────────────────────────────────────────────────────

    private static List<ElementInfo> CollectItems(Document doc, JsonObject setNode)
    {
        var source = setNode["source"]?.GetValue<string>() ?? "host";
        var limit = Math.Clamp(P.IntOr(setNode, "limit", 500), 1, 2000);
        var catArray = setNode["categories"] as JsonArray;

        Document targetDoc;
        long? linkIdVal = null;
        Transform? transform = null;

        if (source == "link")
        {
            var linkIdNode = setNode["linkId"]
                ?? throw new RevitCommandException("bad_request",
                    "Element set with source='link' requires 'linkId'.");
            var linkId = new ElementId(linkIdNode.GetValue<long>());
            linkIdVal = linkId.Value;

            var linkInst = doc.GetElement(linkId) as RevitLinkInstance
                ?? throw new RevitCommandException("not_found",
                    $"No RevitLinkInstance with id {linkId.Value}.");
            targetDoc = linkInst.GetLinkDocument()
                ?? throw new RevitCommandException("invalid_parameter",
                    $"Linked file '{linkInst.Name}' is not loaded.");
            transform = linkInst.GetTotalTransform();
        }
        else
        {
            targetDoc = doc;
        }

        FilteredElementCollector collector;
        if (catArray != null && catArray.Count > 0)
        {
            var filters = new List<ElementFilter>(catArray.Count);
            foreach (var node in catArray)
            {
                if (node == null) continue;
                var catStr = node.GetValue<string>();
                if (!Enum.TryParse<BuiltInCategory>(catStr, ignoreCase: true, out var bic))
                    throw new RevitCommandException("invalid_parameter",
                        $"Unknown BuiltInCategory '{catStr}'.");
                filters.Add(new ElementCategoryFilter(bic));
            }
            var combined = filters.Count == 1
                ? filters[0]
                : new LogicalOrFilter(filters);
            collector = new FilteredElementCollector(targetDoc)
                .WherePasses(combined)
                .WhereElementIsNotElementType();
        }
        else
        {
            collector = new FilteredElementCollector(targetDoc)
                .WhereElementIsNotElementType();
        }

        var items = new List<ElementInfo>(limit);
        var count = 0;

        foreach (var el in collector)
        {
            if (count >= limit) break;
            var (bMin, bMax) = GetBboxInHostCoords(el, transform);
            items.Add(new ElementInfo(el.Id.Value, el.Name, el.Category?.Name, bMin, bMax, source, linkIdVal));
            count++;
        }

        return items;
    }

    private static (XYZ? min, XYZ? max) GetBboxInHostCoords(Element el, Transform? tf)
    {
        var raw = el.get_BoundingBox(null);
        if (raw == null) return (null, null);
        if (tf == null) return (raw.Min, raw.Max);

        var p1 = tf.OfPoint(raw.Min);
        var p2 = tf.OfPoint(raw.Max);
        return (
            new XYZ(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Min(p1.Z, p2.Z)),
            new XYZ(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), Math.Max(p1.Z, p2.Z))
        );
    }

    // ── Clash algorithms ──────────────────────────────────────────────────────

    private static void RunNativeClash(
        Document doc,
        List<ElementInfo> setA, List<ElementInfo> setB,
        int maxResults, JsonArray clashes, HashSet<(long, long)> seen)
    {
        var setBIdSet = new HashSet<long>(setB.Select(i => i.Id));

        foreach (var aInfo in setA)
        {
            if (clashes.Count >= maxResults) break;
            var elA = doc.GetElement(new ElementId(aInfo.Id));
            if (elA == null) continue;

            try
            {
                var hits = new FilteredElementCollector(doc)
                    .WherePasses(new ElementIntersectsElementFilter(elA))
                    .WhereElementIsNotElementType()
                    .Where(e => setBIdSet.Contains(e.Id.Value) && e.Id.Value != aInfo.Id);

                foreach (var elB in hits)
                {
                    if (clashes.Count >= maxResults) break;
                    if (!seen.Add(MakeKey(aInfo.Id, elB.Id.Value))) continue;
                    clashes.Add(MakeClashResult(
                        aInfo,
                        new ElementInfo(elB.Id.Value, elB.Name, elB.Category?.Name, null, null, "host", null),
                        "hard_clash"));
                }
            }
            catch { /* element has no solid geometry — skip */ }
        }
    }

    private static void RunRaycastClash(
        Document doc,
        List<ElementInfo> setA, List<ElementInfo> setB,
        JsonObject setBNode,
        double clearanceMm, string direction,
        JsonNode? viewIdNode,
        int maxResults,
        int sampleCount,
        JsonArray clashes, HashSet<(long, long)> seen)
    {
        // Resolve View3D — required by ReferenceIntersector.
        View3D? view3d = null;
        if (viewIdNode != null)
            view3d = doc.GetElement(new ElementId(viewIdNode.GetValue<long>())) as View3D;
        if (view3d == null)
            view3d = doc.ActiveView as View3D;
        if (view3d == null)
            view3d = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);
        if (view3d == null)
            throw new RevitCommandException("invalid_parameter",
                "No 3D view found for axis=Z raycast. Provide viewId or open a 3D view.");

        var setBHostItems = setB.Where(i => i.Source == "host").ToList();
        var setBLinkItems = setB.Where(i => i.Source == "link").ToList();
        var hasLinkedB    = setBLinkItems.Count > 0;
        var hasHostB      = setBHostItems.Count > 0;

        if (!hasHostB && !hasLinkedB)
            throw new RevitCommandException("invalid_parameter", "setB has no elements.");

        // ID sets for post-filtering hits
        var setBHostIdSet   = new HashSet<long>(setBHostItems.Select(i => i.Id));
        var setBLinkIdSet   = new HashSet<long>(setBLinkItems.Select(i => i.Id));
        var setBLinkInstIds = new HashSet<long>(
            setBLinkItems.Where(i => i.LinkId.HasValue).Select(i => i.LinkId!.Value));
        var setBLookup = setB.ToDictionary(i => i.Id);

        // Build ReferenceIntersector
        ReferenceIntersector ri;
        if (hasHostB && !hasLinkedB)
        {
            // Original fast path: target specific host element IDs
            var setBHostIds = setBHostItems.Select(i => new ElementId(i.Id)).ToList();
            ri = new ReferenceIntersector(setBHostIds, FindReferenceTarget.Face, view3d);
            ri.FindReferencesInRevitLinks = false;
        }
        else
        {
            // Linked setB (or mixed): use category filter, post-filter by element ID.
            // ReferenceIntersector.FindReferencesInRevitLinks = true lets the raycast
            // penetrate into loaded linked files and return Reference.LinkedElementId.
            var riFilter = BuildCategoryFilter(setBNode["categories"] as JsonArray);
            ri = new ReferenceIntersector(riFilter, FindReferenceTarget.Face, view3d);
            ri.FindReferencesInRevitLinks = hasLinkedB;
        }

        var isDown = direction.Equals("below", StringComparison.OrdinalIgnoreCase);
        var rayDir = isDown ? XYZ.BasisZ.Negate() : XYZ.BasisZ;

        foreach (var aInfo in setA)
        {
            if (clashes.Count >= maxResults) break;
            if (aInfo.BboxMin == null || aInfo.BboxMax == null) continue;

            // Skip vertical elements (shaft-like ducts): dZ >> dXY
            var dX = Math.Abs(aInfo.BboxMax.X - aInfo.BboxMin.X);
            var dY = Math.Abs(aInfo.BboxMax.Y - aInfo.BboxMin.Y);
            var dZ = Math.Abs(aInfo.BboxMax.Z - aInfo.BboxMin.Z);
            var maxXY = Math.Max(dX, dY);
            if (maxXY > 0 && dZ / maxXY > 1.5) continue;

            // Sample sampleCount points along the element centreline (LocationCurve).
            var samplePoints = GetSamplePoints(doc, aInfo, isDown, sampleCount);

            // Collect minimum proximity per hit setB element across all sample points.
            var bestPerFloor = new Dictionary<long, double>();
            foreach (var origin in samplePoints)
            {
                ReferenceWithContext? hit;
                try { hit = ri.FindNearest(origin, rayDir); }
                catch { continue; }
                if (hit == null) continue;

                var reference   = hit.GetReference();
                var proximityMm = hit.Proximity * 304.8;
                long hitElementId;

                if (reference.LinkedElementId != ElementId.InvalidElementId)
                {
                    // Hit landed inside a linked document
                    var linkInstId = reference.ElementId.Value;
                    var linkedElId = reference.LinkedElementId.Value;
                    if (!setBLinkInstIds.Contains(linkInstId) || !setBLinkIdSet.Contains(linkedElId))
                        continue;
                    hitElementId = linkedElId;
                }
                else
                {
                    // Hit is a host element
                    hitElementId = reference.ElementId.Value;
                    if (!setBHostIdSet.Contains(hitElementId)) continue;
                }

                if (!bestPerFloor.TryGetValue(hitElementId, out var prev) || proximityMm < prev)
                    bestPerFloor[hitElementId] = proximityMm;
            }

            // Emit one violation row per (setA, setB) pair under the threshold.
            foreach (var (hitFloorId, proximityMm) in bestPerFloor)
            {
                if (clashes.Count >= maxResults) break;
                if (proximityMm >= clearanceMm) continue;
                if (!seen.Add(MakeKey(aInfo.Id, hitFloorId))) continue;

                setBLookup.TryGetValue(hitFloorId, out var bInfo);
                if (bInfo == null)
                {
                    var el = doc.GetElement(new ElementId(hitFloorId));
                    bInfo = new ElementInfo(hitFloorId, el?.Name ?? "", el?.Category?.Name, null, null, "host", null);
                }

                var clash = MakeClashResult(aInfo, bInfo, "clearance_violation");
                clash["clearanceActualMm"] = Math.Round(proximityMm, 1);
                clashes.Add(clash);
            }
        }
    }

    private static ElementFilter BuildCategoryFilter(JsonArray? catArray)
    {
        if (catArray == null || catArray.Count == 0)
            return new ElementIsElementTypeFilter(inverted: true); // all instances

        var filters = new List<ElementFilter>();
        foreach (var node in catArray)
        {
            if (node == null) continue;
            if (Enum.TryParse<BuiltInCategory>(node.GetValue<string>(), ignoreCase: true, out var bic))
                filters.Add(new ElementCategoryFilter(bic));
        }
        return filters.Count == 0
            ? new ElementIsElementTypeFilter(inverted: true)
            : filters.Count == 1
                ? filters[0]
                : (ElementFilter)new LogicalOrFilter(filters);
    }

    // Returns sample points along the element's centerline (LocationCurve) at the
    // duct's bottom face (isDown=true) or top face (isDown=false).
    // Falls back to a single bbox-centre point for elements without a LocationCurve.
    private static List<XYZ> GetSamplePoints(Document doc, ElementInfo info, bool isDown, int sampleCount)
    {
        var halfH = GetHalfSectionHeight(doc, info);

        try
        {
            var el = doc.GetElement(new ElementId(info.Id));
            if (el?.Location is LocationCurve lc && lc.Curve.IsBound)
            {
                var curve = lc.Curve;
                var pts = new List<XYZ>(sampleCount);
                for (var i = 0; i < sampleCount; i++)
                {
                    var t = sampleCount == 1 ? 0.5 : (double)i / (sampleCount - 1);
                    var pt = curve.Evaluate(t, true); // normalized parameter [0..1]
                    pts.Add(new XYZ(pt.X, pt.Y, isDown ? pt.Z - halfH : pt.Z + halfH));
                }
                return pts;
            }
        }
        catch { /* fall through */ }

        // Fallback: single point from bbox centre / bottom / top.
        var originZ = isDown ? info.BboxMin!.Z : info.BboxMax!.Z;
        return new List<XYZ>
        {
            new XYZ(
                (info.BboxMin!.X + info.BboxMax!.X) / 2.0,
                (info.BboxMin!.Y + info.BboxMax!.Y) / 2.0,
                originZ)
        };
    }

    // Returns the half-height of the element's cross-section (perpendicular to its axis).
    // Reads RBS duct/pipe parameters for MEPCurves; falls back to half the bbox Z extent.
    private static double GetHalfSectionHeight(Document doc, ElementInfo info)
    {
        if (doc.GetElement(new ElementId(info.Id)) is MEPCurve mep)
        {
            var hp = mep.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
            if (hp?.HasValue == true) return hp.AsDouble() / 2.0;

            var dp = mep.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            if (dp?.HasValue == true) return dp.AsDouble() / 2.0;
        }
        return (info.BboxMax!.Z - info.BboxMin!.Z) / 2.0;
    }

    private static void RunBboxClash(
        List<ElementInfo> setA, List<ElementInfo> setB,
        double clearanceFt, int maxResults, JsonArray clashes, HashSet<(long, long)> seen)
    {
        var clashType = clearanceFt > 0 ? "clearance_violation" : "hard_clash";

        foreach (var aInfo in setA)
        {
            if (clashes.Count >= maxResults) break;
            if (aInfo.BboxMin == null || aInfo.BboxMax == null) continue;

            // Inflate setA bbox by the clearance margin
            var minA = new XYZ(aInfo.BboxMin.X - clearanceFt, aInfo.BboxMin.Y - clearanceFt, aInfo.BboxMin.Z - clearanceFt);
            var maxA = new XYZ(aInfo.BboxMax.X + clearanceFt, aInfo.BboxMax.Y + clearanceFt, aInfo.BboxMax.Z + clearanceFt);

            foreach (var bInfo in setB)
            {
                if (clashes.Count >= maxResults) break;
                if (bInfo.BboxMin == null || bInfo.BboxMax == null) continue;
                if (aInfo.Id == bInfo.Id && aInfo.Source == bInfo.Source) continue;
                if (!seen.Add(MakeKey(aInfo.Id, bInfo.Id))) continue;

                if (BboxIntersects(minA, maxA, bInfo.BboxMin, bInfo.BboxMax))
                    clashes.Add(MakeClashResult(aInfo, bInfo, clashType));
            }
        }
    }

    // ── Pure helpers (internal for unit testing) ──────────────────────────────

    // Double overload used by tests (avoids requiring Revit API in test project).
    internal static bool BboxIntersects(
        double minAX, double minAY, double minAZ,
        double maxAX, double maxAY, double maxAZ,
        double minBX, double minBY, double minBZ,
        double maxBX, double maxBY, double maxBZ) =>
        minAX <= maxBX && maxAX >= minBX &&
        minAY <= maxBY && maxAY >= minBY &&
        minAZ <= maxBZ && maxAZ >= minBZ;

    private static bool BboxIntersects(XYZ minA, XYZ maxA, XYZ minB, XYZ maxB) =>
        BboxIntersects(minA.X, minA.Y, minA.Z, maxA.X, maxA.Y, maxA.Z,
                       minB.X, minB.Y, minB.Z, maxB.X, maxB.Y, maxB.Z);

    private static (long, long) MakeKey(long a, long b) =>
        a <= b ? (a, b) : (b, a);

    private static JsonObject MakeClashResult(ElementInfo a, ElementInfo b, string clashType)
    {
        static JsonObject El(ElementInfo e) => new JsonObject
        {
            ["id"] = e.Id,
            ["name"] = e.Name,
            ["category"] = e.Category,
            ["source"] = e.Source,
            ["linkId"] = e.LinkId,
        };
        return new JsonObject
        {
            ["elementA"] = El(a),
            ["elementB"] = El(b),
            ["type"] = clashType,
        };
    }
}
