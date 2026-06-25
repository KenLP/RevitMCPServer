import { describe, it, expect } from "vitest";
import { modelHealthTriage, clashReview } from "../recipes.js";

const sampleHealth = {
  ok: true,
  data: {
    scorecard: {
      grade: "B",
      score: 85,
      flags: [
        { code: "imported_cad", severity: "warning", message: "3 imported CAD instance(s)." },
        { code: "warnings_critical", severity: "critical", message: "1200 warnings (> 1000)." },
        { code: "single_instance_groups", severity: "info", message: "4 group type(s) used once." },
      ],
    },
    warnings: { total: 1200, perThousandElements: 4.2 },
    elements: { total: 280000 },
    families: { inPlace: 5 },
    imports: { cadImported: 3 },
    views: { notOnSheet: 100 },
    complexity: { referencePlanesUnnamed: 80 },
  },
};

describe("modelHealthTriage", () => {
  it("synthesizes prioritized findings with recommended actions", async () => {
    const res = await modelHealthTriage(async () => sampleHealth as any);
    expect(res.ok).toBe(true);
    const d = res.data as any;
    expect(d.grade).toBe("B");
    // 3 scorecard flags + 1 derived (unnamed ref planes > 50)
    expect(d.findingCount).toBe(4);
    // sorted most-severe first
    expect(d.findings[0].severity).toBe("critical");
    expect(d.findings.every((f: any) => typeof f.recommendedAction === "string" && f.recommendedAction.length > 0)).toBe(true);
    const cad = d.findings.find((f: any) => f.code === "imported_cad");
    expect(cad.recommendedAction).toMatch(/LINKED CAD/);
    expect(d.keyMetrics.warnings).toBe(1200);
  });

  it("reports no findings for a clean model", async () => {
    const res = await modelHealthTriage(async () =>
      ({ ok: true, data: { scorecard: { grade: "A", score: 100, flags: [] }, complexity: {} } } as any));
    const d = res.data as any;
    expect(d.findingCount).toBe(0);
    expect(d.summary).toMatch(/no flagged issues/);
  });

  it("propagates an error envelope unchanged", async () => {
    const res = await modelHealthTriage(async () =>
      ({ ok: false, error: { code: "not_found", message: "no active document" } } as any));
    expect(res.ok).toBe(false);
    expect(res.error?.code).toBe("not_found");
  });

  it("passes deep through to get_model_health", async () => {
    let seen: any = null;
    await modelHealthTriage(async (_cmd, params) => { seen = params; return sampleHealth as any; }, true);
    expect(seen.deep).toBe(true);
  });
});

describe("clashReview", () => {
  it("aggregates and prioritizes across pairs (hard first, then smallest gap)", async () => {
    const queue = [
      { ok: true, data: { limited: false, clashes: [
        { type: "hard_clash", elementA: { id: 1, category: "Ducts", source: "host" }, elementB: { id: 2, category: "Framing", source: "link" } },
        { type: "hard_clash", elementA: { id: 3 }, elementB: { id: 4 } },
      ] } },
      { ok: true, data: { clashes: [
        { type: "clearance_violation", clearanceActualMm: 120, elementA: { id: 5 }, elementB: { id: 6 } },
        { type: "clearance_violation", clearanceActualMm: 40, elementA: { id: 7 }, elementB: { id: 8 } },
      ] } },
    ];
    let i = 0;
    const res = await clashReview(async () => queue[i++] as any, [
      { label: "Ducts × Struct", setA: {}, setB: {} },
      { label: "Ducts × Floors", setA: {}, setB: {} },
    ]);
    const d = res.data as any;
    expect(d.totalHardClashes).toBe(2);
    expect(d.totalClearanceWarnings).toBe(2);
    expect(d.topOffenders[0].severity).toBe("hard_clash");
    const clears = d.topOffenders.filter((o: any) => o.severity === "clearance_violation");
    expect(clears[0].clearanceMm).toBe(40); // smallest gap first
  });

  it("records a failing pair and continues the sweep", async () => {
    const res = await clashReview(async (_cmd, params: any) =>
      (params.setA.bad
        ? { ok: false, error: { code: "not_found", message: "x" } }
        : { ok: true, data: { clashes: [{ type: "hard_clash", elementA: { id: 1 }, elementB: { id: 2 } }] } }) as any,
      [{ setA: { bad: true }, setB: {} }, { setA: {}, setB: {} }]);
    const d = res.data as any;
    expect(d.pairs[0].error).toBe("not_found");
    expect(d.totalHardClashes).toBe(1);
  });

  it("rejects an empty pairs array", async () => {
    const res = await clashReview(async () => ({ ok: true }) as any, []);
    expect(res.ok).toBe(false);
  });
});
