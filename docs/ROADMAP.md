# Roadmap

## Current status — v0.8.35

94 MCP tools (91 commands + 1 batch + 2 workflow recipes); 101 C# commands registered,
10 of them HTTP-only. Live-verified on Revit 2025 / 2026 / 2027.

| Phase | Version | Status |
|---|---|---|
| Foundation (MVP) | v0.1.0 | ✅ Done |
| Edit + Batch transactions | v0.2.0 | ✅ Done |
| 60 commands | v0.3.0 | ✅ Done |
| Security + Preview (dry-run, auth, diffs, risk levels) | v0.4.0 | ✅ Done |
| Revit 2027 support + Family rename | v0.4.2 | ✅ Done |
| Safety corrections (ExecutionKind, HTTP codes, version sync) | v0.5.0 | ✅ Done |
| Test & CI foundation (Vitest, xUnit, GitHub Actions) | v0.5.0 | ✅ Done |
| Revit hardening (unit conversion, view guards, family candidates) | v0.5.0 | ✅ Done |
| Release tooling (build script, install/uninstall, compat matrix, troubleshooting) | v0.5.0 | ✅ Done |
| Correctness & API hardening (batch policy, spec-aware units, domain errors, CI R2027) | v0.6.0 | ✅ Done |
| Linked-file element reading, clash/clearance detection, view image export, R2025 | v0.7.0 | ✅ Done |
| View manipulation (duplicate_view, set_section_box, isolate_elements_in_view) + check_clearance Z-raycast with multi-point centreline sampling + linked-file setB support + list_spaces (MEP Spaces) + create_3d_view + RevitMCP.Core classlib extraction | v0.8.0 | ✅ Done |
| Element type swapping (change_element_type), view template application, parameter copy across elements | v0.8.0 | ✅ Done |
| Schedule config (filters/sort/group/CSV), level elevation editing, PDF export | v0.8.0 | ✅ Done |
| Room containment — phase-aware FromRoom/ToRoom/Room batch lookup for family instances | v0.8.0 | ✅ Done |
| Annotation — tag_all_in_view, get_tags_in_view (create_aligned_dimension / create_spot_elevation hidden pending API fixes) | v0.8.1 | ✅ Done |
| Model health report — get_model_health one-shot scorecard (warnings, file size, imports/links, families, groups, unused views, purgeable) | v0.8.2 | ✅ Done |
| Workset audit (get_worksets) + model-health enrichment (imports/links section, warning/element ratio, worksets, isModelInCloud) | v0.8.3 | ✅ Done |
| Truth gate (version + tool-count drift fail CI) + observability (X-Request-Id, structured log, /stats) + limits (body/batch/in-flight) | v0.8.4 | ✅ Done |
| Live-Revit smoke suite (scripts/smoke-test.ps1) — read/dry-run/real-write/batch/limits + golden fingerprint compare | v0.8.4 | ✅ Done |
| P2-A tool profiles — REVIT_MCP_PROFILE gates the 80-tool surface by group (core always on) for token efficiency | v0.8.4 | ✅ Done |
| P3-fix — un-hide create_aligned_dimension (works); create_spot_elevation re-hidden after raycast approach failed live | v0.8.5/0.8.6 | ✅ Done |
| P2-C pagination — list_elements + find_elements (offset/total/hasMore/nextOffset; no 5000 ceiling) | v0.8.6 | ✅ Done |
| P3 pack 1 — get_schedule_data (read rendered ViewSchedule cells; paginated) | v0.8.7 | ✅ Done |
| P3 pack 2 — Family: load_family, duplicate_family_type | v0.8.8 | ✅ Done |
| P3 pack 3 — Detailing: create_detail_line, create_filled_region | v0.8.8 | ✅ Done |
| P4 pilot — workflow recipe layer + recipe_model_health_triage (read-only) | v0.8.9 | ✅ Done |
| P4 — recipe_clash_review (coordination matrix across host/linked RVT) | v0.8.10 | ✅ Done (live e2e: 20 hard clashes, link×link) |
| Hosted family placement (doors/windows) + `find_elements` type-parameter projection | v0.8.11 | ✅ Done |
| `get_doors` — door swing geometry (facing/hand orientation as world vectors) | v0.8.12 | ✅ Done |
| `spatial_*` command pack — HTTP-only geometry primitives for external clients | v0.8.13 | ✅ Done |
| Build-truth `/health` (version/commit/branch/state from the compiled assembly) | v0.8.14 | ✅ Done |
| `find_elements` view scoping (`view_id`) | v0.8.15 | ✅ Done |
| Release package runs as shipped (artifact completeness gate) | v0.8.16 | ✅ Done |
| Security hardening — loopback clamp, unconditional auth, audit clean | v0.8.17 | ✅ Done |
| One-shot installer for all three Revit versions; Codex / Gemini / Cursor configs | v0.8.18/0.8.19 | ✅ Done |
| AutoAudit dockable panel (WebView2) + installer no longer wipes its config | v0.8.20 | ✅ Done |
| Stable identity — `uniqueId` on `get_element_info`, `find_element_by_unique_id` (host + links) | v0.8.21/0.8.22 | ✅ Done |
| `configure_schedule` numeric filters | v0.8.23 | ✅ Done |
| Path of Travel — read (`spatial_get_paths_of_travel`), polyline, create | v0.8.24–0.8.26 | ✅ Done |
| `create_detail_line` color/weight; `query_where` / `update_where` / `import_parameters` | v0.8.25/0.8.27 | ✅ Done |
| Parameter coercion — a mistyped parameter returns 400 with the offending key, never a bare 500 | v0.8.28/0.8.33 | ✅ Done |
| `isolate_elements_in_view` transaction fix; `spatial_create_model_line` | v0.8.29 | ✅ Done |
| Dockable panes recover from "paused" after a background document closes | v0.8.30 | ✅ Done |
| `get_view_image` `pixelSize`; `create_perspective_view` | v0.8.31 | ✅ Done |
| Guards that refuse instead of faking success — dimensions in a 3D view, `check_clearance` `axis`/`direction` | v0.8.32/0.8.34 | ✅ Done |
| Second ribbon panel is opt-in (config-driven); default install adds one tab | v0.8.35 | ✅ Done |

## Near-term backlog

### Smoke / integration tests against a fixture model
- Headless Revit test runner (e.g. `xunit` inside Revit via `RevitTestRunner`)
  playing back a canned batch script against a known `.rvt` fixture.
- Covers the command surface that cannot be tested without a live Revit API.

### Parameter unit coverage improvements
- Current unit conversion is opt-in (`units:"meters"`). Investigate inferring
  user project units from `Document.GetUnits()` and applying automatically.
- Add `get_parameter_units` introspection command.

### WebSocket transport (long-running ops)
- Streaming progress for bulk operations (thousands of elements, export jobs).

## Longer-term ideas

- `purge_unused` (with dry-run preview of what would be purged)
- MEP element creation (`create_duct`, `create_pipe`, `create_mep_system`)
- IFC export
- Solid-based clearance check (upgrade `check_clearance` `axis="bbox"` cross-doc path from AABB to geometry for fewer false positives)
- Hot-reload without restarting Revit (AppDomain research)
