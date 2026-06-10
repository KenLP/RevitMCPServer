# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
