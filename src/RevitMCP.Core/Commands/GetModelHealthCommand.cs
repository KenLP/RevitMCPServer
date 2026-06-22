using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// One-shot model health report — aggregates the metrics a BIM manager checks
/// when judging model quality: warnings, file size, imported/linked CAD, RVT
/// links, point clouds, in-place families, groups, unused views, worksets,
/// purgeable elements, and complexity counts.
///
/// Read-only.  Returns a structured report plus a scorecard (grade + flags).
/// Every metric is wrapped defensively: a failure in one section degrades to a
/// note instead of failing the whole report.
///
/// Thresholds below are seeded from published Revit best-practice guidance
/// (file size &lt; 300-500 MB; warnings &lt; 300 for performance, 0 critical;
/// in-place families minimised; empty worksets are a smell).  There is no
/// industry-standard warnings/element ratio, so that value is reported for
/// context but not scored.
///
/// Params:
///   - deep:  bool (default false).  When true, also runs the purge scan
///            (Document.GetUnusedElements), which can be slow on large models.
///   - topN:  int (default 10, 1-50).  How many top warning groups / categories
///            to list.
/// </summary>
public sealed class GetModelHealthCommand : IRevitCommand
{
    public string Name => "get_model_health";
    public bool IsReadOnly => true;

    // ── Scorecard thresholds (tune here) ─────────────────────────────────────
    private const int    WarningsHigh        = 300;   // > this → -10 (common "keep under 300" guidance)
    private const int    WarningsCritical    = 1000;  // > this → -25
    private const int    InPlaceHigh         = 20;    // > this → -10
    private const double UnusedViewRatioHigh = 0.50;  // notOnSheet/placeable > this → -10
    private const double FileLargeMB         = 300.0; // > this → -10 (split into linked models beyond ~300-500 MB)
    private const int    PurgeableHigh       = 1000;  // > this → -5 (deep only)

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        bool deep = P.BoolOr(p, "deep", false);
        int topN = Math.Clamp(P.IntOr(p, "topN", 10), 1, 50);

        var notes = new JsonArray();
        var flags = new JsonArray();
        int score = 100;

        void Flag(string code, string severity, string message, int penalty)
        {
            flags.Add(new JsonObject
            {
                ["code"] = code,
                ["severity"] = severity,
                ["message"] = message,
            });
            score -= penalty;
        }

        // ── File & worksets ──────────────────────────────────────────────────
        bool inCloud = false;
        try { inCloud = doc.IsModelInCloud; } catch { /* property best-effort */ }

        double? sizeMB = null;
        try
        {
            if (!string.IsNullOrEmpty(doc.PathName) && !inCloud && File.Exists(doc.PathName))
                sizeMB = Math.Round(new FileInfo(doc.PathName).Length / 1048576.0, 1);
        }
        catch { /* size best-effort */ }
        if (sizeMB is null)
            notes.Add(inCloud
                ? "File size unavailable: model is cloud-hosted (Autodesk Docs / BIM 360) — the Revit API exposes no on-disk size for cloud models. Open a local .rvt to see size."
                : "File size unavailable: model not saved to a local/resolvable path.");

        int? worksetCount = null;
        int? worksetEmpty = null;
        if (doc.IsWorkshared)
        {
            try
            {
                var wsList = new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset).ToWorksets();
                worksetCount = wsList.Count;
                int empty = 0;
                foreach (var ws in wsList)
                {
                    int n = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType()
                        .WherePasses(new ElementWorksetFilter(ws.Id))
                        .GetElementCount();
                    if (n == 0) empty++;
                }
                worksetEmpty = empty;
            }
            catch { /* workset enumeration best-effort */ }
        }

        var fileObj = new JsonObject
        {
            ["title"] = doc.Title,
            ["sizeMB"] = sizeMB,
            ["isModelInCloud"] = inCloud,
            ["isWorkshared"] = doc.IsWorkshared,
            ["worksets"] = worksetCount,
            ["emptyWorksets"] = worksetEmpty,
        };

        if (sizeMB is double mb && mb > FileLargeMB)
            Flag("file_large", "warning", $"File is {mb} MB (> {FileLargeMB} MB — consider splitting into linked models).", 10);
        if (worksetEmpty is int we && we > 0)
            Flag("empty_worksets", "info", $"{we} workset(s) contain no elements — candidates to remove.", 5);

        // ── Warnings (the single most important health signal) ─────────────────
        var warnings = doc.GetWarnings();
        int warnTotal = warnings.Count;
        int warnErrors = warnings.Count(w => w.GetSeverity() == FailureSeverity.Error);
        var topWarnings = new JsonArray();
        foreach (var g in warnings
                     .GroupBy(w => w.GetDescriptionText())
                     .Select(grp => new { Text = grp.Key, Count = grp.Count() })
                     .OrderByDescending(x => x.Count)
                     .Take(topN))
        {
            topWarnings.Add(new JsonObject { ["text"] = g.Text, ["count"] = g.Count });
        }

        var warnObj = new JsonObject
        {
            ["total"] = warnTotal,
            ["errors"] = warnErrors,
            ["top"] = topWarnings,
        };

        if (warnTotal > WarningsCritical)
            Flag("warnings_critical", "critical", $"{warnTotal} warnings (> {WarningsCritical}).", 25);
        else if (warnTotal > WarningsHigh)
            Flag("warnings_high", "warning", $"{warnTotal} warnings (> {WarningsHigh}).", 10);

        // ── Elements by category ───────────────────────────────────────────────
        var catCounts = new Dictionary<long, (string Name, int Count)>();
        int totalElements = 0;
        foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            totalElements++;
            var cat = el.Category;
            if (cat is null) continue;
            var id = cat.Id.Value;
            if (catCounts.TryGetValue(id, out var ex))
                catCounts[id] = (ex.Name, ex.Count + 1);
            else
                catCounts[id] = (cat.Name, 1);
        }
        var topCats = new JsonArray();
        foreach (var kv in catCounts.OrderByDescending(k => k.Value.Count).Take(topN))
            topCats.Add(new JsonObject { ["category"] = kv.Value.Name, ["count"] = kv.Value.Count });

        // Warnings-per-element ratio — reported for context (no published standard).
        warnObj["perThousandElements"] = totalElements > 0
            ? Math.Round(warnTotal * 1000.0 / totalElements, 2)
            : 0;

        var elemObj = new JsonObject
        {
            ["total"] = totalElements,
            ["distinctCategories"] = catCounts.Count,
            ["topCategories"] = topCats,
        };

        // ── Families ────────────────────────────────────────────────────────────
        var families = new FilteredElementCollector(doc)
            .OfClass(typeof(Family)).Cast<Family>().ToList();
        int famTotal = families.Count;
        int famInPlace = families.Count(f => f.IsInPlace);

        var famObj = new JsonObject
        {
            ["totalFamilies"] = famTotal,
            ["loadable"] = famTotal - famInPlace,
            ["inPlace"] = famInPlace,
        };
        if (famInPlace > InPlaceHigh)
            Flag("inplace_families_high", "warning",
                $"{famInPlace} in-place families (> {InPlaceHigh}).", 10);

        // ── Imports & links (CAD, PDF/images, RVT links, point clouds) ───────────
        var imports = new FilteredElementCollector(doc)
            .OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        int cadLinked = imports.Count(i => i.IsLinked);
        int cadImported = imports.Count(i => !i.IsLinked);
        int cadImportedInViews = imports.Count(i => !i.IsLinked && i.ViewSpecific);

        int imagesAndPdfs = 0;   // imported raster images AND imported PDFs both become ImageInstance
        try { imagesAndPdfs = new FilteredElementCollector(doc).OfClass(typeof(ImageInstance)).GetElementCount(); }
        catch { /* image enumeration best-effort */ }

        int rvtLinkInstances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount();
        int rvtLinkTypes = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).GetElementCount();

        int pointClouds = 0;
        try
        {
            pointClouds = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PointClouds)
                .WhereElementIsNotElementType()
                .GetElementCount();
        }
        catch { /* point cloud enumeration best-effort */ }

        var importsObj = new JsonObject
        {
            ["cadImported"] = cadImported,
            ["cadImportedInViews"] = cadImportedInViews,
            ["cadLinked"] = cadLinked,
            ["imagesAndPdfs"] = imagesAndPdfs,
            ["rvtLinkInstances"] = rvtLinkInstances,
            ["rvtLinkTypes"] = rvtLinkTypes,
            ["pointClouds"] = pointClouds,
        };

        if (cadImported > 0)
            Flag("imported_cad", "warning",
                $"{cadImported} imported CAD instance(s) (not linked) — embeds DWG/DXF geometry in the model.", 15);

        // ── Groups ───────────────────────────────────────────────────────────────
        var groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().ToList();
        long modelGroupCat = (long)BuiltInCategory.OST_IOSModelGroups;
        long detailGroupCat = (long)BuiltInCategory.OST_IOSDetailGroups;
        int modelGroups = groups.Count(g => g.Category?.Id.Value == modelGroupCat);
        int detailGroups = groups.Count(g => g.Category?.Id.Value == detailGroupCat);
        int singleInstanceTypes = groups
            .GroupBy(g => g.GetTypeId().Value)
            .Count(g => g.Count() == 1);

        var groupObj = new JsonObject
        {
            ["modelGroupInstances"] = modelGroups,
            ["detailGroupInstances"] = detailGroups,
            ["singleInstanceGroupTypes"] = singleInstanceTypes,
        };
        if (singleInstanceTypes > 0)
            Flag("single_instance_groups", "info",
                $"{singleInstanceTypes} group type(s) used only once — consider ungrouping.", 5);

        // ── Views & sheets ─────────────────────────────────────────────────────
        var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
        int sheets = allViews.Count(v => v is ViewSheet);

        var placedIds = new HashSet<long>();
        foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            placedIds.Add(vp.ViewId.Value);
        foreach (var ssi in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            placedIds.Add(ssi.ScheduleId.Value);

        var placeable = allViews.Where(IsPlaceableOnSheet).ToList();
        int notOnSheet = placeable.Count(v => !placedIds.Contains(v.Id.Value));

        var viewObj = new JsonObject
        {
            ["totalViews"] = allViews.Count(v => !v.IsTemplate && v is not ViewSheet),
            ["sheets"] = sheets,
            ["placeableViews"] = placeable.Count,
            ["notOnSheet"] = notOnSheet,
        };
        if (placeable.Count > 0 && (double)notOnSheet / placeable.Count > UnusedViewRatioHigh)
            Flag("unused_views_high", "info",
                $"{notOnSheet}/{placeable.Count} placeable views are not on any sheet.", 10);

        // ── Other complexity metrics ─────────────────────────────────────────────
        int levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();
        int grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).GetElementCount();
        int designOptions = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).GetElementCount();
        var refPlanes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
        int refPlanesUnnamed = refPlanes.Count(rp => string.IsNullOrWhiteSpace(rp.Name) || rp.Name == "Reference Plane");

        var complexityObj = new JsonObject
        {
            ["levels"] = levels,
            ["grids"] = grids,
            ["designOptions"] = designOptions,
            ["referencePlanes"] = refPlanes.Count,
            ["referencePlanesUnnamed"] = refPlanesUnnamed,
        };

        // ── Purgeable (deep only) ─────────────────────────────────────────────────
        int? purgeable = null;
        if (deep)
        {
            try { purgeable = doc.GetUnusedElements(new HashSet<ElementId>()).Count; }
            catch (Exception ex) { notes.Add($"Purge scan failed: {ex.Message}"); }
            if (purgeable is int pc && pc > PurgeableHigh)
                Flag("purgeable_high", "info", $"{pc} purgeable elements (single-pass estimate).", 5);
        }
        else
        {
            notes.Add("Purgeable scan skipped (pass deep=true to include — can be slow on large models).");
        }

        // Family file sizes (>1-3 MB) are NOT measured: the Revit API exposes no
        // size for a loaded family; the only way is EditFamily + save per family,
        // which is far too slow to run over a whole model. Spot-check externally.
        notes.Add("Per-family file sizes are not measured (no Revit API for loaded-family size; would require EditFamily+save per family).");

        // ── Scorecard ──────────────────────────────────────────────────────────────
        score = Math.Clamp(score, 0, 100);
        string grade = score >= 90 ? "A" : score >= 80 ? "B" : score >= 70 ? "C" : score >= 60 ? "D" : "F";

        return new JsonObject
        {
            ["scorecard"] = new JsonObject
            {
                ["grade"] = grade,
                ["score"] = score,
                ["flags"] = flags,
            },
            ["file"] = fileObj,
            ["warnings"] = warnObj,
            ["elements"] = elemObj,
            ["families"] = famObj,
            ["imports"] = importsObj,
            ["groups"] = groupObj,
            ["views"] = viewObj,
            ["complexity"] = complexityObj,
            ["purgeable"] = purgeable,
            ["notes"] = notes,
        };
    }

    /// <summary>Whether a view can be placed on a sheet (excludes sheets and system views).</summary>
    private static bool IsPlaceableOnSheet(View v)
    {
        if (v.IsTemplate) return false;
        if (v is ViewSchedule s && s.IsTitleblockRevisionSchedule) return false;
        switch (v.ViewType)
        {
            case ViewType.Internal:
            case ViewType.ProjectBrowser:
            case ViewType.SystemBrowser:
            case ViewType.Undefined:
            case ViewType.DrawingSheet:
                return false;
            default:
                return true;
        }
    }
}
