# Roadmap

## Current status — v0.8.0

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
| View manipulation (duplicate_view, set_section_box, isolate_elements_in_view) + check_clearance Z-raycast with multi-point centreline sampling + linked-file setB support + list_spaces (MEP Spaces) + create_3d_view duplicate-active-3D behaviour | v0.8.0 | ✅ Done |

## Near-term backlog

### Smoke / integration tests against a fixture model
- Headless Revit test runner (e.g. `xunit` inside Revit via `RevitTestRunner`)
  playing back a canned batch script against a known `.rvt` fixture.
- Covers the command surface that cannot be tested without a live Revit API.

### Parameter unit coverage improvements
- Current unit conversion is opt-in (`units:"meters"`). Investigate inferring
  user project units from `Document.GetUnits()` and applying automatically.
- Add `get_parameter_units` introspection command.

### Structured log with request IDs
- Request ID generated per HTTP call, threaded through dispatcher, Revit
  command, and response — enables correlation across MCP ↔ HTTP ↔ addin logs.

### WebSocket transport (long-running ops)
- Streaming progress for bulk operations (thousands of elements, export jobs).

## Longer-term ideas

- `create_dimension` (linear), `create_detail_line`
- `purge_unused` (with dry-run preview of what would be purged)
- `get_schedule_data` — read rendered schedule rows as JSON
- MEP element creation (`create_duct`, `create_pipe`, `create_mep_system`)
- IFC export
- Solid-based clearance check (upgrade `check_clearance` `axis="bbox"` cross-doc path from AABB to geometry for fewer false positives)
- Hot-reload without restarting Revit (AppDomain research)
