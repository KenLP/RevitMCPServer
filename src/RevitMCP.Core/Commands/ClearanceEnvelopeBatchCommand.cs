using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Spatial-QC pack (HTTP-only; command name prefixed <c>spatial_</c>, not exposed as an MCP tool).
///
/// Batched clearance envelope — the same volumetric MEP-aware headroom check as
/// <see cref="ClearanceEnvelopeCommand"/>, but for MANY rooms in one call. The expensive work
/// (enumerating loaded links, running the category + solid filters, and EXTRACTING each overhead
/// element's geometry) is done ONCE over the union of all rooms' clear volumes and cached, then
/// reused for every room. That removes the per-room HTTP round-trip and, more importantly, the
/// repeated geometry extraction — the dominant cost when checking headroom across a whole model.
///
/// Params:
///   rooms:          [{ id, loops:[[[x,y],...],...], floorZ, requiredHeight?, maxHeight?, minObstacleHeight? }]
///   requiredHeight / maxHeight / minObstacleHeight: defaults applied to rooms that omit them
///   categories:     string[] (optional, overrides the default obstruction set)
///   units:          "meters" | "feet"
///
/// Returns: { count, results: [ { id, ...same shape as clearance_envelope... } ] }
/// </summary>
public sealed class ClearanceEnvelopeBatchCommand : IRevitCommand
{
    public string Name => "spatial_clearance_envelope_batch";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        double scale = units.Equals("feet", StringComparison.OrdinalIgnoreCase) ? 1.0 : P.MetersToFeet;

        double defReq = P.DblOr(p, "requiredHeight", 2.03) * scale;
        double defMax = P.DblOr(p, "maxHeight", 8.0) * scale;
        double defMinObs = P.DblOr(p, "minObstacleHeight", 0.5) * scale;
        double eps = 0.05 * P.MetersToFeet;
        var cats = ClearanceEnvelopeCommand.ParseCats(p) ?? ClearanceEnvelopeCommand.DefaultCats;

        var roomsArr = P.Arr(p, "rooms");

        // build each room's clear volume + its per-room params
        var built = new List<(JsonNode? id, Solid solid, double floorZ, double req, double max, double minObs)>();
        var probes = new List<Solid>();
        foreach (var rn in roomsArr)
        {
            if (rn is not JsonObject ro) continue;
            var loops = ro["loops"] as JsonArray;
            if (loops == null || loops.Count == 0) continue;
            double floorZ = P.DblOr(ro, "floorZ", 0.0) * scale;
            double req = ro.ContainsKey("requiredHeight") ? P.Dbl(ro, "requiredHeight") * scale : defReq;
            double max = ro.ContainsKey("maxHeight") ? P.Dbl(ro, "maxHeight") * scale : defMax;
            double minObs = ro.ContainsKey("minObstacleHeight") ? P.Dbl(ro, "minObstacleHeight") * scale : defMinObs;
            Solid solid;
            try { solid = ClearanceEnvelopeCommand.BuildClearVolume(loops, scale, floorZ + eps, max - eps); }
            catch { continue; }
            built.Add((ro["id"]?.DeepClone(), solid, floorZ, req, max, minObs));
            probes.Add(solid);
        }

        // collect + extract candidate obstruction geometry ONCE over all rooms
        var cands = ClearanceEnvelopeCommand.CollectCandidates(doc, cats, probes);

        var results = new JsonArray();
        foreach (var (id, solid, floorZ, req, max, minObs) in built)
        {
            var res = ClearanceEnvelopeCommand.BuildResult(solid, cands, floorZ, minObs, scale, req / scale, max / scale);
            res["id"] = id;
            results.Add(res);
        }
        return new JsonObject { ["count"] = results.Count, ["results"] = results };
    }
}
