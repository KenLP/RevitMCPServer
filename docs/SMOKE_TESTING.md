# Live-Revit smoke testing

Unit tests (xUnit / Vitest) mock the Revit API, so they cannot catch regressions in
real Revit behaviour. `scripts/smoke-test.ps1` fills that gap: it drives the **running**
addin over HTTP and asserts behaviour end-to-end.

It is a **local/manual** test (CI has no Revit). Run it after deploying a new addin
build, before tagging a release.

## Prerequisites

- Revit is open with the addin loaded **and a document open**.
- You know the version you're testing (port is derived: 2025→7890, 2026→7891, 2027→7892).

## Run

```powershell
# from the repo root
& .\scripts\smoke-test.ps1 -Version 2027
```

Options:

| Flag | Effect |
|---|---|
| `-Version <year>` | Revit version (default 2027); sets the port automatically. |
| `-Port <n>` | Override the port explicitly. |
| `-NoWrites` | Skip the real create→delete round-trip (read/dry-run only). |
| `-Snapshot <file>` | Capture a golden fingerprint of the current model to JSON. |
| `-Golden <file>` | Compare the current model against a saved fingerprint; fail on drift. |

Exit code is non-zero if any check fails.

## What it checks

1. **Connectivity** — ping, health, `/commands` (≥79), `/stats`.
2. **Read surface** — document info, levels, categories, views, model health, worksets.
3. **Observability (P1)** — `X-Request-Id` minted + echoed; `/stats` counter advances.
4. **Dry-run write** — `create_level` with `dryRun` reports `committed=false` (no mutation).
5. **Real write round-trip** — create a uniquely-named Level → verify → delete → confirm
   gone. Self-cleaning, with an orphan-cleanup safety net; the open model is left as found.
6. **Batch** — read-only batch returns the expected step count.
7. **Limits (P1)** — batch > 200 steps → 400; body > 1 MB → 413.

## Golden fixtures

For true regression detection, designate a **fixed** `.rvt` fixture, open it, and capture
a baseline once:

```powershell
& .\scripts\smoke-test.ps1 -Version 2027 -Snapshot tests\fixtures\snowdon-arch.json
```

Later, after changes, open the same fixture and compare:

```powershell
& .\scripts\smoke-test.ps1 -Version 2027 -Golden tests\fixtures\snowdon-arch.json
```

The fingerprint covers document title, level/category counts, total elements, total
warnings, and the health grade — enough to catch unintended model-wide changes.
