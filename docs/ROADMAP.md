# Roadmap

This file tracks the actual delivery status for each phase.

## Status

- ✅ **Phase 1 — Foundation (MVP)** — `v0.1.0`
  - C# addin (HttpListener + ExternalEvent + Transaction-per-command)
  - TypeScript MCP stdio server
  - 5 commands: ping, get_version, list_elements, get_element_info, create_wall
- ✅ **Phase 2 — Edit + Batch transactions** — `v0.2.0`
  - Refactor: dispatcher owns Transactions; commands are stateless `IRevitCommand`
  - +6 write commands: create_floor, create_level, create_grid,
    set_parameter, delete_elements, move_element
  - +4 introspection commands: list_levels, list_wall_types, list_floor_types,
    list_categories
  - Batch endpoint (`POST /mcp/batch` + `revit_batch` MCP tool) — single
    Transaction across N steps, atomic rollback on failure
  - `GET /commands` for runtime introspection
- 🟡 **Phase 3 — Cross-version (2025 / 2026 / 2027)** — planned
- ✅ **Phase 4 — 60 commands** — `v0.3.0`
  - +12 introspection: get_document_info, find_elements, get_parameter,
    get_views, get_active_view, get_selected_elements, list_families,
    list_family_types, list_sheets, list_rooms, list_materials, list_phases,
    list_view_templates, get_linked_files, get_element_geometry
  - +6 arch creation: create_room, create_column, create_beam, create_ceiling,
    create_opening_in_wall, place_family_instance
  - +8 documentation: create_sheet, place_view_on_sheet, create_floor_plan_view,
    create_section_view, create_3d_view, create_schedule, tag_element,
    create_text_note
  - +2 param edits: set_parameter_batch, rename_element
  - +4 transforms: rotate_element, copy_element, mirror_element, array_linear
  - +2 grouping: group_elements, ungroup_elements
  - +8 view manipulation: open_view, set_view_detail_level, hide/unhide elements,
    select_elements, zoom_to_elements, apply_view_filter, color_override_by_param
- ✅ **Phase 5a — Advanced: Security + Preview** — `v0.4.0`
  - Dry-run mode (`?dryRun=true` / body `dryRun: true`)
  - Structured diffs (`changeSummary` + `changes` before/after)
  - Auth token (random per-session Bearer token, token file)
  - Per-tool risk levels (`read` / `low` / `medium` / `high`)
- ✅ **Phase 3a — Revit 2027 support** — `v0.4.1`
  - Conditional `TargetFramework`: `net10.0-windows` for R2027+, `net8.0-windows` for R2025–2026
  - Multi-version Claude Desktop config (separate ports + `REVIT_MCP_VERSION`)
- ⚪ **Phase 5b — Advanced / remaining** — backlog

## Phase 3 — Cross-version build matrix

Goal: same source tree compiles for Revit 2025, 2026, and 2027 (and beyond)
with no per-fork divergence.

- [ ] Add `Release R25` / `Release R26` / `Release R27` MSBuild configurations.
- [ ] `<DefineConstants>REVIT2025;REVIT2026;REVIT2027</DefineConstants>` per config.
- [ ] Wrap any breaking API call sites with `#if REVITxxxx`.
- [ ] CI workflow (GitHub Actions) that builds all three configurations on
      windows-latest with the appropriate `RevitInstallDir` from secrets, or
      with stub reference assemblies.
- [ ] Smoke-test integration runner that boots a known fixture model and
      replays a canned batch script per version.

## Phase 4 — High-value AEC writes

Roughly in priority order (see `API_COVERAGE.md` for rationale):

- [ ] `set_parameter_batch` — same parameter, many elements, one call.
- [ ] `find_elements` — generic query DSL: category + parameter predicates.
- [ ] `place_family_instance` — `Document.Create.NewFamilyInstance(...)`.
- [ ] `create_sheet` + `place_view_on_sheet`.
- [ ] `apply_view_filter` — `ParameterFilterElement` + view overrides.
- [ ] `color_override_by_param` — split a category by a parameter and apply
      `OverrideGraphicSettings` per bucket.
- [ ] `create_room` + `tag_rooms`.
- [ ] `create_dimension` (linear).
- [ ] `transform_elements` — bulk move / rotate / array.
- [ ] `purge_unused`.

## Phase 5 — Schedules + analysis

- [ ] `create_schedule` (`ViewSchedule.CreateSchedule`).
- [ ] `get_schedule_data` — read a schedule's rendered rows.
- [ ] Linked-file enumeration + cross-link queries.
- [ ] Energy / area analysis hooks.

## Cross-cutting nice-to-haves

- [x] **Dry-run mode** — `?dryRun=true` runs every step, captures the
      result, then rolls the whole transaction back. Lets the AI preview
      effects safely. *(v0.4.0)*
- [x] **Structured diffs** — write commands return `changeSummary` +
      `changes` (before/after) so the client can show a changelog to the
      user. *(v0.4.0)*
- [ ] **WebSocket transport** option alongside HTTP for streaming long-running
      ops (e.g. progress while iterating thousands of elements).
- [x] **Auth token** for the HTTP listener (still loopback, but defends
      against malicious local processes). *(v0.4.0)*
- [ ] **Hot reload** — watch the addin DLL and reload without restarting
      Revit (probably impossible without an AppDomain trick — research
      task).
- [x] **Per-tool permission allowlist surfaced via `GET /commands`** — now
      returns `riskLevel` per command so the MCP client can make per-tool
      permission decisions. *(v0.4.0)*
