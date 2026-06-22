# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.8.3] — 2026-06-22: Model health — worksets, imports/links, warning ratio

### Added

- **`revit_get_worksets`** — list user worksets with per-workset element counts.
  Flags empty worksets (no instances) and the un-renamed default `"Workset1"`.
  Returns `{ isWorkshared, count, emptyCount, worksets: [{id, name, elementCount,
  isEmpty, isOpen, isEditable, isVisibleByDefault, owner, isDefaultName}] }`.
  Non-workshared models return `isWorkshared=false`.
  Tested live on Revit 2027 (4 worksets, "Workset1" correctly flagged).

### Changed

- **`revit_get_model_health`** enhanced:
  - New **`imports`** section: imported vs linked CAD (with "in views" subcount),
    imported PDFs + raster images (`imagesAndPdfs`), RVT link instances/types,
    point clouds. Imported CAD (not linked) is flagged.
  - **`warnings.perThousandElements`** — warnings-per-1000-elements ratio, reported
    for context (no published industry standard, so not scored).
  - **`file.worksets` / `file.emptyWorksets`** — workset summary; empty worksets flagged.
  - **`file.isModelInCloud`** — makes it explicit when file size is `null` because the
    model is cloud-hosted (the Revit API exposes no on-disk size for cloud models).
  - Warning threshold raised 100 → **300** (`warnings_high`), aligning with the common
    "keep warnings under 300" performance guidance; `warnings_critical` stays 1000.
  - Per-family file sizes are intentionally **not** measured (no Revit API for a loaded
    family's size; would require EditFamily+save per family) — noted in `notes`.

---

## [0.8.2] — 2026-06-22: Model health report

### Added

- **`revit_get_model_health`** — one-shot, read-only model quality report. Aggregates
  the metrics a BIM manager checks when judging a model, in a single call:
  - **Warnings**: total, error count, and top-N groups by description.
  - **File**: size (MB), worksharing status, workset count. Size is `null` for cloud
    models (`IsModelInCloud`) — reported in `notes`.
  - **Elements**: total count, distinct categories, top-N categories by instance count.
  - **Families**: loadable vs in-place, imported vs linked CAD instances, raster images.
  - **Groups**: model/detail group instances, single-instance group types.
  - **Views**: total views, sheets, placeable views, views not placed on any sheet.
  - **Complexity**: levels, grids, design options, reference planes (+ unnamed).
  - **Purgeable**: `Document.GetUnusedElements` count — only when `deep=true`
    (single-pass estimate; skipped by default as it is slow on large models).
  - **Scorecard**: letter grade (A–F), 0–100 score, and a list of flagged issues
    with severity. Thresholds are tunable constants in `GetModelHealthCommand`.

  Params: `deep` (bool, default false), `topN` (int, default 10).
  Tested live on Revit 2027 (Snowdon Towers sample): grade A/95, 90 warnings,
  42,901 elements, 633 purgeable.

---

## [0.8.1] — 2026-06-21: Annotation — tagging

### Added

- **`revit_tag_all_in_view`** — tag all untagged elements of a category in a view
  (mirrors Revit's "Tag All Not Tagged"). Accepts `category` (display name, e.g. `"Doors"`),
  optional `viewId`, `leader` (bool), `skipTagged` (bool, default true).
  Returns `{ tagged, skipped, failed, tags: [{tagId, elementId}] }`.
  Tested live on Revit 2026 (tagged 3 Doors in L1 - Architectural).
- **`revit_get_tags_in_view`** — list all `IndependentTag` elements in a view.
  Optional `category` filter. Returns `{ viewId, count, tags: [{tagId, elementId,
  category, hasLeader, tagText, location}] }`.
  Tested live: correctly returned 3 Door tags with tagText `"100"`, `"101"`, `"102"`.

### Notes

- `create_aligned_dimension` and `create_spot_elevation` are implemented in C# but
  hidden from the MCP tool surface pending Revit API reference fixes. They remain
  callable via direct HTTP if needed. Dimension works wall-to-wall; grid references
  in mixed `ReferenceArray` are not yet resolved.

---

## [0.8.0] — 2026-06-19: Type change, view templates, parameter copy, schedule/PDF/level, room containment

### Added

- **`revit_change_element_type`** — swap the type of any element (wall type, floor
  type, family symbol) using `Element.ChangeTypeId`. Validates against the element's
  own allowed types; returns old and new type info. Error code `wrong_element_type` → 400.
- **`revit_apply_view_template`** — apply or remove a view template from a view.
  Accepts `templateId` (ElementId) or `templateName` (case-insensitive lookup). Pass
  `templateId: -1` to remove the current template. Use `revit_list_view_templates` to
  discover available templates.
- **`revit_copy_parameters`** — copy parameter values from a source element to N target
  elements in one call. Matches by name and StorageType; only writable, non-None
  parameters are copied. Returns per-target success/failure detail.
- **`revit_configure_schedule`** — add filters and sort/group fields to an existing
  ViewSchedule; supports `clearFilters` / `clearSortFields` to reset first. Optional
  `exportCsv: true` exports the schedule and returns CSV content in the response.
  Uses `ScheduleField.FieldId` for phase-aware resolution; fields are added as hidden
  columns automatically when first referenced.
- **`revit_set_level_elevation`** — change the elevation of a Level element. Supports
  `"meters"`, `"feet"`, `"mm"`, and `"internal"` units. Returns old and new elevation
  in both m and ft.
- **`revit_export_view_pdf`** — export any view or sheet to PDF on disk via
  `Document.Export(folder, viewIds, PDFExportOptions)`. Accepts `outputFolder`,
  `fileName`, `rasterQuality` (Low / Medium / High / Presentation), `colorMode`
  (Color / Grayscale / BlackLine). Returns output path and file size.
- **`revit_get_element_rooms`** — get room containment for one or more family instances
  in a single batch call. Uses `FamilyInstance.get_Room(Phase)` / `get_FromRoom(Phase)`
  / `get_ToRoom(Phase)` — phase-dependent and authoritative, not centroid-in-bbox.
  `fromRoom` + `toRoom` for boundary connectors (Doors, Windows); `room` for
  point-located elements (Furniture, Fixtures, lighting, plumbing, …). Each room is
  `{ id, name, number }` or null. Phase resolved from the element's `PHASE_CREATED`
  parameter. Verified live on Revit 2027 Snowdon Towers Architectural.
- **`RevitMCP.Core` class library** extracted from `RevitMCPAddin` — all
  `IRevitCommand` implementations now live in a separate classlib for cleaner project
  boundaries and faster incremental builds.
- `wrong_element_type` added to the 400 group in `StatusForResult()`.

---

## [0.7.0] — 2026-06-11: Linked-file clash detection, view image export, R2025

### Added

- **`revit_get_linked_elements`** — read elements from inside a linked RVT file.
  Bounding boxes are automatically transformed to host-model coordinates.
  Accepts `linkId` (from `revit_get_linked_files`), optional `category`, optional `limit`.
- **`revit_check_clearance`** — detect hard clashes or clearance violations between two
  element sets. Supports host-vs-host (uses Revit's native `ElementIntersectsElementFilter`
  for exact solid-based detection) and cross-linked-file checks (AABB with clearance inflation).
  Parameters: `setA`, `setB` (each specifying `source: host|link`, optional `categories`),
  `clearanceMm` (0 = hard clash), `maxResults`.
- **`revit_get_view_image`** — export any Revit view (or the active view) to PNG and return
  it as an MCP `Image` content block. Accepts optional `viewId` and `dpi` (72/150/300).
- **Revit 2025 support** — added Nice3point reference assemblies for R2025 (`net8.0-windows`,
  port 7890). CI test matrix and release artifacts now include R2025 alongside R2026/R2027.
  Build/deploy works without installing R2025 locally.
- **7 new `CheckClearanceCommand.BboxIntersects` unit tests** covering overlap, separation,
  face-touch, containment, single-axis separation, and clearance inflation.

---

## [0.6.0] — 2026-06-11: Correctness, API hardening, CI R2027

### Breaking

- **Batch policy**: batches that mix `ModelWrite` and `UiAction` commands are
  now rejected with `bad_request`. Previously, UI actions could silently run
  inside a model transaction. Submit model writes and UI actions as separate
  batches.
- **Unit conversion extended**: `set_parameter` / `set_parameter_batch` now
  require spec-matched unit strings for area and volume parameters.
  - Length: `"meters"` / `"feet"` (unchanged)
  - Area: `"square_meters"` / `"square_feet"` (new)
  - Volume: `"cubic_meters"` / `"cubic_feet"` (new)
  - Passing `"meters"` for an area parameter now returns `invalid_parameter`
    instead of silently applying the wrong conversion.

### Added

- `revit_apply_view_filter` MCP schema now exposes `reuseExisting` (boolean,
  optional) — the C# command already supported it but the field was missing
  from the TypeScript schema.
- RGB channel validation in MCP schema: `r`, `g`, `b` now bounded `[0, 255]`
  for both `revit_apply_view_filter` and `revit_color_override_by_param`.
- `executionKind` field added to every entry in `GET /commands` response
  (`ReadOnly` / `ModelWrite` / `UiAction`). Clients can now distinguish model
  mutation from UI mutation.
- `RevitCommandException(code, message)` — commands now throw typed domain
  exceptions; the dispatcher preserves the `code` field in the error envelope
  instead of collapsing everything to `command_failed`.
- New HTTP status mappings in `StatusForResult`:
  - `invalid_parameter`, `read_only_parameter`, `unsupported_view` → 400
  - `ambiguous_selection` → 409
- CI matrix: `csharp` CI job now builds and tests both R2026 (.NET 8) and
  R2027 (.NET 10) on every pull request.
- `BatchPolicy.ValidateBatchKinds` — pure static helper, testable without Revit.
- New xUnit tests: `StatusForResultTests` (14 cases), `BatchPolicyTests` (7),
  `RevitCommandExceptionTests` (4), `CommandRegistryTests` extended with
  `executionKind` assertions. Total: **83 C# tests** (up from 46).

### Fixed

- README version badge updated from `v0.5.0` to `v0.6.0`; health example
  updated from `0.5.0` to `0.6.0`.
- `check-version.mjs` now validates the README badge and health example so
  future version drift is caught by CI.
- `vitest run` test script now uses explicit `--config ./vitest.config.ts
  --root .` to avoid path-resolution failures on Windows paths with spaces.
- MSB3277 warning suppressed in test project (harmless .NET 8 / Revit assembly
  version conflict that polluted CI output).
- Nice3point reference packages pinned to exact versions (`2026.4.10`,
  `2027.0.20`) for reproducible CI builds.
- Old stale review files (`project_review.md`, `docs/PROJECT_REVIEW.md`) removed.

## [0.5.0] — 2026-06-10: Safety, tests, CI, and production hardening

### Added — Command execution classification
- New `ExecutionKind` enum: `ReadOnly`, `ModelWrite`, `UiAction`.
- `open_view`, `select_elements`, `zoom_to_elements` marked `UiAction` —
  no longer wrapped in a model transaction; dry-run returns a no-op instead
  of silently reverting UI state.

### Added — Unit conversion for numeric parameters
- `set_parameter` and `set_parameter_batch` accept `units:"meters"|"feet"|"internal"`.
  When a Double parameter has measurable units (length, area, volume …),
  `UnitUtils.ConvertToInternalUnits` is called automatically.
  Dimensionless parameters (ratio, slope, etc.) are never converted.
  Response echoes `inputUnits` and `unitConversionApplied`.

### Added — View filter hardening
- `apply_view_filter` checks `AreGraphicsOverridesAllowed()` before
  creating the filter; raises a clear error for schedule/legend views.
- Duplicate filter names now detected before `ParameterFilterElement.Create()`
  — error includes the conflicting filter id. Set `reuseExisting:true` to
  re-apply an existing filter to the view instead.

### Added — `color_override_by_param` view guard
- `AreGraphicsOverridesAllowed()` check with view type in the error message.

### Added — Unambiguous family instance placement
- `place_family_instance` returns `placed:false` + a candidate list when
  both `familyName` and `familyTypeName` are omitted and multiple types
  match. When a partial filter still yields >1 match, places the first but
  includes a `warning` field and `familyTypeId` in the response.

### Added — Auth token auto-refresh
- `revitClient.ts` promotes `AUTH_TOKEN` to a mutable `_authToken` and
  re-reads the token file on `unauthorized` responses — handles Revit
  generating a new token on restart without requiring a server restart.

### Added — Startup health check
- On startup `index.ts` probes `/health` and logs: Revit not reachable,
  auth mismatch (enabled but no token), or successful connection with version.

### Added — Test suite (Phase 2)
- **13 TypeScript tests** (Vitest): `callRevit`, `callRevitBatch`,
  `envelopeToToolResult`, error codes, auth header.
- **46 C# tests** (xUnit): `JsonResult`, `ParamUtil` (all methods + bounds),
  `CommandRegistry` (register, replace, TryGet, ExecutionKind, RiskLevel).
- `Nice3point.Revit.Api` NuGet stubs used as fallback so `dotnet build`
  succeeds on CI machines without a Revit install.
- `scripts/check-version.mjs` — exits non-zero when version strings drift
  across `package.json`, `index.ts`, `McpHttpServer.cs`, and `CHANGELOG.md`.

### Added — GitHub Actions CI
- Three jobs: TypeScript build+test (ubuntu-latest), C# build+test
  (windows-latest, `DeployToRevit=false`), version consistency check.
- Release job on `v*` tags: builds R2026 + R2027 artifacts and uploads
  versioned zip files as GitHub Release assets.

### Added — Release tooling
- `scripts/build-release.ps1` — full release pipeline: version check,
  tests, multi-version C# build, TypeScript build, zip packaging.
- `scripts/install.ps1` — copies addin DLL + manifest to the per-user
  Revit Addins folder; runs `npm install --production` for the MCP server.
- `scripts/uninstall.ps1` — removes addin files from Revit Addins folder.

### Added — Compatibility matrix & troubleshooting guide
- `docs/COMPATIBILITY.md` — Revit × .NET × Node.js matrix with known limits.
- `docs/TROUBLESHOOTING.md` — diagnostic checklist for common failure modes.

### Fixed — HTTP error semantics
- `StatusForResult()` maps error codes to proper HTTP statuses:
  `bad_request`/`bad_json` → 400, `unauthorized` → 401,
  `unknown_command` → 404, name collision → 409, timeout → 408, else 500.

### Fixed — `set_parameter_batch` partial failure
- `partialFailure:true` added to top-level response when any element fails.
- New `atomic` option: `true` → any element failure rolls back the entire batch.

### Fixed — RGB color validation
- `P.ColorByte()` rejects values outside 0-255 with a clear error instead
  of silently wrapping via `(byte)` cast.

### Fixed — Version drift
- `package.json`, `index.ts`, `McpHttpServer.cs` all report `0.5.0`.

## [0.4.2] — 2026-06-09: Revit 2027 support + Family rename

### Added
- **Revit 2027 build** — csproj auto-selects `net10.0-windows` when
  `RevitVersion >= 2027`, `net8.0-windows` for R2025–2026.
  Build with `dotnet build -p:RevitVersion=2027`.
- **Auto-port assignment** — each Revit version gets its own port
  automatically (R2026=7891, R2027=7892, ...). No manual port config
  needed for side-by-side use.
- **Family & FamilySymbol rename** — `revit_rename_element` now handles
  `Family.Name` and `FamilySymbol.Name` (direct property setters), not
  just parameter-based renames. Validates system families, illegal
  characters, and name collisions. Returns `instancesAffected` count.
- Multi-version Claude Desktop config guide.
- Beginner-friendly installation walk-through in README (Step 0–6 +
  troubleshooting table).

### Changed
- `Revit_MCP_Server_Build_Plan.md` moved to `docs/internal/` (gitignored).
- README + docs fully updated for R2026/R2027 dual support.

## [0.4.1] — 2026-05-27

### Fixed — UTF-8 request decoding
- `McpHttpServer.ReadJsonObjectAsync` was decoding incoming bodies with
  `HttpListenerRequest.ContentEncoding`, which falls back to
  `Encoding.Default` when the client omits `charset` in `Content-Type`.
  Under Revit's hosting that produced mojibake for non-ASCII characters
  (e.g. em-dash `—` arrived as `â€"`, section sign `§` as `Â§`),
  corrupting audit-trail Comments written by the bim-orchestrator.
- Force `Encoding.UTF8` on the read path — JSON is canonically UTF-8
  per RFC 8259, so the client's charset declaration is moot.
- Also set `response.ContentEncoding = Encoding.UTF8` on the write
  path for symmetry; the actual bytes were already UTF-8.

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
