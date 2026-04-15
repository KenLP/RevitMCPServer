# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] — Phase 5a: Security + Preview

### Added — Dry-run mode
- Every write command and batch accepts `dryRun: true` (body field or
  `?dryRun=true` query param). The transaction runs normally then rolls
  back — the model is unchanged, but the full result data is returned so
  the AI can preview what *would* happen before committing.

### Added — Structured diffs
- Write commands now return a `changeSummary` one-liner in their data
  payload (e.g. `"Set 'Comments' on element 184239: '' → 'Reviewed'"`).
- Modify commands (`set_parameter`, `rename_element`, `move_element`)
  also return a `changes` object with `before`/`after` values.
- The AI shows the concise summary by default and can expand the full
  diff on request.

### Added — Auth token
- On startup the addin generates a 32-byte random token and writes it to
  `%APPDATA%\Autodesk\Revit\Addins\<version>\revit-mcp-token.txt`.
- All HTTP endpoints (except `GET /health`) require
  `Authorization: Bearer <token>`.
- The TypeScript MCP server reads the token file automatically.
- Disable auth with `REVIT_MCP_AUTH=false` or override with
  `REVIT_MCP_AUTH_TOKEN=<token>`.

### Added — Per-tool risk levels
- `IRevitCommand` now exposes a `RiskLevel` property with default interface
  implementation: `read` (read-only), `low` (creates), `medium` (modifies),
  `high` (deletes/destructive).
- `GET /commands` returns `riskLevel` alongside `isReadOnly` for each
  command, enabling clients to build per-tool permission policies.
- 13 commands classified as `medium` risk, 3 as `high`, rest default to
  `read` or `low`.

### Changed
- `McpHttpServer` version bumped to `0.4.0`.
- TypeScript MCP server v0.4.0 — all write tools now accept `dryRun`
  param; HTTP client sends `Authorization` header when token is available.
- `package.json` version updated to `0.4.0`.

## [0.3.0] — Phase 4: 60 Commands

### Added — Inspection / Introspection (+12 commands)
- `get_document_info` — project metadata, path, worksharing status, active view.
- `find_elements` — generic query DSL: category + parameter filters (equals,
  not_equals, contains, greater, less, etc.) + optional field projections.
- `get_parameter` — single parameter value from one element.
- `get_views` — all non-template views with type, level, scale, detail level.
- `get_active_view` — current active view.
- `get_selected_elements` — elements selected by user in Revit UI.
- `list_families` — loaded Families, optionally by category.
- `list_family_types` — FamilySymbols, optionally by family or category.
- `list_sheets` — all ViewSheets with number, viewport count.
- `list_rooms` — placed rooms with area, perimeter, department.
- `list_materials` — materials with class, category, color.
- `list_phases` — project phases.
- `list_view_templates` — all view templates.
- `get_linked_files` — RevitLinkInstances and their load status.
- `get_element_geometry` — bounding box, centroid, volume, surface area,
  solid/face counts.

### Added — Creation: Architecture (+6 commands)
- `create_room` — NewRoom at a point with optional name/number.
- `create_column` — structural column placement with family type resolution.
- `create_beam` — structural beam between two points.
- `create_ceiling` — Ceiling from closed polygonal profile.
- `create_opening_in_wall` — rectangular opening in a wall.
- `place_family_instance` — generic FamilyInstance placement (non-hosted).

### Added — Creation: Documentation (+8 commands)
- `create_sheet` — ViewSheet with optional title block.
- `place_view_on_sheet` — Viewport placement.
- `create_floor_plan_view` — ViewPlan for a level.
- `create_section_view` — ViewSection with configurable bounding box.
- `create_3d_view` — isometric View3D.
- `create_schedule` — ViewSchedule with optional field columns.
- `tag_element` — IndependentTag on an element in a view.
- `create_text_note` — TextNote in a view.

### Added — Edit: Parameters (+2 commands)
- `set_parameter_batch` — set one parameter on many elements in one call.
- `rename_element` — set Element.Name.

### Added — Edit: Transform (+4 commands)
- `rotate_element` — rotate around vertical axis.
- `copy_element` — copy with translation.
- `mirror_element` — mirror across a plane (copy or move).
- `array_linear` — copy N times along a vector.

### Added — Edit: Grouping (+2 commands)
- `group_elements` — NewGroup.
- `ungroup_elements` — UngroupMembers.

### Added — View Manipulation (+8 commands)
- `open_view` — switch active view.
- `set_view_detail_level` — Coarse / Medium / Fine.
- `hide_elements_in_view` — View.HideElements.
- `unhide_elements_in_view` — View.UnhideElements.
- `select_elements` — set UI selection.
- `zoom_to_elements` — ShowElements.
- `apply_view_filter` — create ParameterFilterElement + apply with overrides.
- `color_override_by_param` — color-code elements by parameter value in a view.

### Summary
- **60 registered C# commands** + 1 batch MCP tool = **61 MCP tools total**.
- C# addin: 0 errors, 3 warnings (MSB3277 noise).
- TypeScript MCP server: 0 errors.

## [0.2.0] — Phase 2: Edit, Create, Batch

### Added
- Batch transaction pattern (`revit_batch` / `POST /mcp/batch`).
- Write commands: `create_floor`, `create_level`, `create_grid`,
  `set_parameter`, `delete_elements`, `move_element`.
- Introspection: `list_levels`, `list_wall_types`, `list_floor_types`,
  `list_categories`.
- `GET /commands` endpoint.

### Changed
- Command framework refactor: `IRevitCommand` + `CommandContext` + dispatcher-
  owned transactions.

## [0.1.0] — MVP

- Initial Revit 2026 addin + TypeScript MCP server.
- 5 commands: `ping`, `get_revit_version`, `list_elements`, `get_element_info`,
  `create_wall`.
