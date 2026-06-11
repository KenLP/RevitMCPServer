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
///   - setA:  { source: "host"|"link", linkId?: long, categories?: string[], limit?: int }
///   - setB:  same structure
///   - clearanceMm: double, optional, default 0 (= hard clash only).
///             When > 0, flags any pair whose bounding boxes overlap after inflating
///             setA bboxes by this margin in all directions.
///   - maxResults: int, optional, default 200.
///
/// Method:
///   - host-vs-host + clearanceMm = 0: Revit's ElementIntersectsElementFilter (exact, solid-based).
///   - all other cases: AABB overlap with clearance inflation (conservative, cross-doc safe).
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

        var setAItems = CollectItems(doc, setANode);
        var setBItems = CollectItems(doc, setBNode);

        var sourceA = setANode["source"]?.GetValue<string>() ?? "host";
        var sourceB = setBNode["source"]?.GetValue<string>() ?? "host";
        var bothHost = sourceA == "host" && sourceB == "host";
        var useNative = bothHost && clearanceMm == 0.0;

        var clashes = new JsonArray();
        var seen = new HashSet<(long, long)>();

        if (useNative)
            RunNativeClash(doc, setAItems, setBItems, maxResults, clashes, seen);
        else
            RunBboxClash(setAItems, setBItems, clearanceFt, maxResults, clashes, seen);

        return new JsonObject
        {
            ["clashCount"] = clashes.Count,
            ["clearanceMm"] = clearanceMm,
            ["limited"] = clashes.Count >= maxResults,
            ["method"] = useNative ? "ElementIntersectsElementFilter" : "BoundingBoxIntersection",
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
