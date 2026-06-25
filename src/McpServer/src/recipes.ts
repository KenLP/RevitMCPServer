/**
 * Workflow recipes — the P4 orchestration layer.
 *
 * Recipes live in the Node bridge, ABOVE the deterministic C# kernel: they compose
 * already-verified atomic commands into goal-oriented workflows, with preconditions,
 * postconditions/verification, and (for writes) dry-run preview. They never touch the
 * Revit API directly — they call the kernel over HTTP.
 *
 * Recipes are pure of transport: each takes a `call` function so the orchestration can
 * be unit-tested with a mock and run live with the real HTTP client.
 */
import type { RevitEnvelope } from "./revitClient.js";

export type CallFn = (
  command: string,
  params: Record<string, unknown>,
) => Promise<RevitEnvelope>;

export interface TriageFinding {
  severity: string;
  code: string;
  issue: string;
  recommendedAction: string;
}

/** Recommended remediation per model-health flag code. */
const ACTION: Record<string, string> = {
  warnings_critical:
    "Open Manage ▸ Warnings and clear the largest groups (duplicates, overlaps). Re-run with deep=true for the full breakdown.",
  warnings_high:
    "Review Manage ▸ Warnings; resolve the biggest groups to keep the count under ~300.",
  file_large:
    "Split the model into linked files and purge unused content (>400–500 MB hurts open/sync).",
  imported_cad:
    "Replace imported CAD with LINKED CAD and remove embedded DWG/DXF geometry.",
  imported_images_pdfs:
    "Review imported images/PDFs; delete any that are not needed.",
  inplace_families_high:
    "Convert in-place families to loadable families where practical.",
  single_instance_groups: "Ungroup group types that are used only once.",
  empty_worksets: "Delete worksets that contain no elements.",
  unused_views_high:
    "Place useful views on sheets and delete the rest to declutter the browser.",
  purgeable_high:
    "Run Manage ▸ Purge Unused to remove unused families, types, and materials.",
};

const SEVERITY_RANK: Record<string, number> = { critical: 0, warning: 1, info: 2 };

/**
 * RECIPE (read-only): run a model-health scan and return a prioritized, actionable
 * triage list. Composes get_model_health; pure synthesis on top.
 */
export async function modelHealthTriage(call: CallFn, deep = false): Promise<RevitEnvelope> {
  // Precondition + data: a single health scan.
  const env = await call("get_model_health", { deep });
  if (!env.ok) return env; // propagate the error envelope unchanged

  const d = (env.data ?? {}) as Record<string, any>;
  const findings: TriageFinding[] = [];

  for (const f of (d.scorecard?.flags ?? []) as Array<Record<string, string>>) {
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
      summary:
        findings.length === 0
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
