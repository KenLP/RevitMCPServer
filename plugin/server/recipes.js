/** Recommended remediation per model-health flag code. */
const ACTION = {
    warnings_critical: "Open Manage ▸ Warnings and clear the largest groups (duplicates, overlaps). Re-run with deep=true for the full breakdown.",
    warnings_high: "Review Manage ▸ Warnings; resolve the biggest groups to keep the count under ~300.",
    file_large: "Split the model into linked files and purge unused content (>400–500 MB hurts open/sync).",
    imported_cad: "Replace imported CAD with LINKED CAD and remove embedded DWG/DXF geometry.",
    imported_images_pdfs: "Review imported images/PDFs; delete any that are not needed.",
    inplace_families_high: "Convert in-place families to loadable families where practical.",
    single_instance_groups: "Ungroup group types that are used only once.",
    empty_worksets: "Delete worksets that contain no elements.",
    unused_views_high: "Place useful views on sheets and delete the rest to declutter the browser.",
    purgeable_high: "Run Manage ▸ Purge Unused to remove unused families, types, and materials.",
};
const SEVERITY_RANK = { critical: 0, warning: 1, info: 2 };
/**
 * RECIPE (read-only): run a coordination clash sweep across multiple element-set pairs
 * (host vs host, or host vs linked RVT) and return a consolidated, prioritized report.
 * Each pair is a check_clearance input — links are supported via setB.source='link' + linkId.
 * Composes check_clearance; a pair that errors is recorded and the sweep continues.
 */
export async function clashReview(call, pairs) {
    if (!Array.isArray(pairs) || pairs.length === 0)
        return { ok: false, error: { code: "bad_request", message: "clash_review requires a non-empty 'pairs' array." } };
    const pairReports = [];
    const offenders = [];
    let totalHard = 0;
    let totalClear = 0;
    for (let i = 0; i < pairs.length; i++) {
        const p = pairs[i];
        const label = p.label ?? `pair ${i + 1}`;
        const params = { setA: p.setA, setB: p.setB };
        for (const k of ["axis", "direction", "clearanceMm", "viewId", "sampleCount", "maxResults"])
            if (p[k] !== undefined)
                params[k] = p[k];
        const env = await call("check_clearance", params);
        if (!env.ok) {
            pairReports.push({ label, error: env.error?.code ?? "failed", message: env.error?.message });
            continue;
        }
        const d = (env.data ?? {});
        const clashes = (d.clashes ?? []);
        let hard = 0;
        let clear = 0;
        for (const c of clashes) {
            const isHard = c.type === "hard_clash";
            if (isHard)
                hard++;
            else
                clear++;
            offenders.push({
                pair: label,
                severity: isHard ? "hard_clash" : "clearance_violation",
                clearanceMm: c.clearanceActualMm ?? null,
                a: c.elementA?.id, aCategory: c.elementA?.category, aSource: c.elementA?.source,
                b: c.elementB?.id, bCategory: c.elementB?.category, bSource: c.elementB?.source, bLinkId: c.elementB?.linkId,
            });
        }
        totalHard += hard;
        totalClear += clear;
        pairReports.push({ label, hardClashes: hard, clearanceWarnings: clear, limited: d.limited === true });
    }
    // Prioritise: hard clashes first, then clearance violations by smallest measured gap.
    offenders.sort((x, y) => {
        if (x.severity !== y.severity)
            return x.severity === "hard_clash" ? -1 : 1;
        if (x.severity === "clearance_violation")
            return (Number(x.clearanceMm ?? Infinity)) - (Number(y.clearanceMm ?? Infinity));
        return 0;
    });
    return {
        ok: true,
        data: {
            summary: `${totalHard} hard clash(es), ${totalClear} clearance warning(s) across ${pairs.length} pair(s).`,
            totalHardClashes: totalHard,
            totalClearanceWarnings: totalClear,
            offenderCount: offenders.length,
            pairs: pairReports,
            topOffenders: offenders.slice(0, 50),
        },
    };
}
/**
 * RECIPE (read-only): run a model-health scan and return a prioritized, actionable
 * triage list. Composes get_model_health; pure synthesis on top.
 */
export async function modelHealthTriage(call, deep = false) {
    // Precondition + data: a single health scan.
    const env = await call("get_model_health", { deep });
    if (!env.ok)
        return env; // propagate the error envelope unchanged
    const d = (env.data ?? {});
    const findings = [];
    for (const f of (d.scorecard?.flags ?? [])) {
        findings.push({
            severity: f.severity,
            code: f.code,
            issue: f.message,
            recommendedAction: ACTION[f.code] ?? "Review this metric and decide whether action is needed.",
        });
    }
    // Derived advisory that the scorecard does not flag on its own.
    const refUnnamed = Number(d.complexity?.referencePlanesUnnamed ?? 0);
    if (refUnnamed > 50) {
        findings.push({
            severity: "info",
            code: "unnamed_reference_planes",
            issue: `${refUnnamed} unnamed reference planes clutter the model.`,
            recommendedAction: "Name the reference planes you keep and delete the stray ones.",
        });
    }
    findings.sort((a, b) => (SEVERITY_RANK[a.severity] ?? 9) - (SEVERITY_RANK[b.severity] ?? 9));
    const grade = d.scorecard?.grade;
    const score = d.scorecard?.score;
    return {
        ok: true,
        data: {
            grade,
            score,
            findingCount: findings.length,
            findings,
            summary: findings.length === 0
                ? `Model health ${grade} (${score}/100) — no flagged issues.`
                : `Model health ${grade} (${score}/100) — ${findings.length} issue(s) to triage, most severe first.`,
            keyMetrics: {
                warnings: d.warnings?.total,
                warningsPerThousandElements: d.warnings?.perThousandElements,
                elements: d.elements?.total,
                importedCad: d.imports?.cadImported,
                inPlaceFamilies: d.families?.inPlace,
                viewsNotOnSheet: d.views?.notOnSheet,
            },
        },
    };
}
