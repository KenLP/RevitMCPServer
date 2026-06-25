# Revit API Coverage — Strategy & Status

## Can we wrap "all of Revit API"?

**No, not as individual MCP tools**, and any project that claims otherwise is
hiding a generic eval-style escape hatch. The Revit API surface is huge:

- ~1,200 public types in `RevitAPI.dll` / `RevitAPIUI.dll` alone.
- Each non-trivial creation API (walls, families, schedules, MEP systems,
  rebars, families, analytical model, energy analysis, …) has its own
  parameter conventions, unit gotchas, and cross-version differences.
- Many entry points are *families of factories* that depend on existing
  document state (which level, which type, which view) — they don't translate
  directly to a stateless "tool".

So the realistic strategies are:

| Strategy | Pros | Cons | Used here? |
|---|---|---|---|
| **Curated tools** (one `IRevitCommand` per common op) | Type-safe, validated, observable, undoable, documented | Linear effort per tool — coverage grows slowly | ✅ Primary |
| **Introspection tools** (`list_categories`, `list_levels`, `list_wall_types`, …) | Lets the AI *discover* what is in the doc instead of guessing names | Doesn't help with operations the AI hasn't seen before | ✅ Secondary |
| **Generic query DSL** (e.g. JSON-encoded filtered element queries with category + parameter predicates) | Covers a lot of read-side use cases with one tool | Have to design the DSL, easy to footgun | ✅ `find_elements` implemented |
| **Code eval** (accept arbitrary C# / Python and run inside Revit) | Total coverage in one tool | ⚠️ Massive blast radius — AI errors can corrupt models, no validation, no undo grouping, no rate limiting, hard to audit | ❌ Out of scope |

The original `revit-mcp` did the eval approach. We deliberately didn't —
**every command in this repo opens a transaction with a known name and a
known schema**. That means: review-able, undoable, and safe to whitelist in
Claude Desktop / Claude Code.

## Current command surface (v0.8.9)

**86 commands** registered (85 exposed as MCP tools + 1 hidden: `create_spot_elevation`)
across read, write, UI, and coordination categories. With the batch transport tool and one
Node-only workflow recipe (`recipe_model_health_triage`), that is **87 MCP tools**.

### Implemented — Read / Introspection

| Command | Notes |
|---|---|
| `ping` | Health check + active doc title |
| `get_revit_version` | Revit version, build, language |
| `get_document_info` | File path, phases, project info |
| `list_elements` | Filter by category, optional limit |
| `get_element_info` | All parameters + bbox |
| `get_element_geometry` | Solid/curve geometry as JSON |
| `get_parameter` | Single parameter read |
| `find_elements` | Generic query: category + parameter predicates |
| `list_levels` | All Levels, sorted by elevation |
| `list_wall_types` | All WallTypes |
| `list_floor_types` | All FloorTypes |
| `list_categories` | Categories used in doc with counts |
| `list_families` | Families by category |
| `list_family_types` | Types within a family |
| `list_materials` | All materials |
| `list_phases` | All phases |
| `list_rooms` | All rooms with area, level, phase |
| `list_spaces` | All placed MEP Spaces (`OST_MEPSpaces`) — id, name, number, level, area (m²), volume (m³), spaceType |
| `list_sheets` | All sheets |
| `list_view_templates` | View templates |
| `get_views` | All views with type/template/discipline |
| `get_active_view` | Current UI view |
| `get_selected_elements` | UIDocument selection |
| `get_linked_files` | Linked Revit files — list instances with metadata |
| `get_linked_elements` | Read elements **inside** a linked RVT; bboxes in host coords |
| `get_view_image` | Export any view to PNG; returns base64 + MCP Image content |
| `get_element_rooms` | Phase-aware Room/FromRoom/ToRoom containment for family instances (batch) |
| `export_view_pdf` | Export a view or sheet to PDF on disk |
| `get_tags_in_view` | List IndependentTag elements in a view (optional category filter) |
| `get_model_health` | One-shot health scorecard: warnings (+ /1000-element ratio), file size, imports/links, families, groups, unused views, worksets, purgeable |
| `get_worksets` | User worksets with per-workset element counts; flags empty worksets and default "Workset1" |
| `get_schedule_data` | Read rendered ViewSchedule cell text (calculated fields/units applied); paginated by row |

### Implemented — Model Writes

| Command | Notes |
|---|---|
| `create_wall` | Single straight wall |
| `create_floor` | From closed polygonal profile |
| `create_ceiling` | From closed polygonal profile |
| `create_level` | Level at given elevation |
| `create_grid` | Straight grid line |
| `create_column` | Structural or architectural column |
| `create_beam` | Structural beam between two points |
| `create_room` | Room by point on level |
| `create_sheet` | New sheet with title block |
| `create_schedule` | ViewSchedule for a category |
| `create_3d_view` | Named 3D view |
| `create_floor_plan_view` | Floor plan for a level |
| `create_section_view` | Section view |
| `create_text_note` | Text note in a view |
| `create_opening_in_wall` | Rectangular opening |
| `set_parameter` | Single param on one element (with unit conversion) |
| `set_parameter_batch` | Same param on N elements |
| `delete_elements` | Delete by ids |
| `move_element` | Translate by vector |
| `copy_element` | Copy with offset |
| `rotate_element` | Rotate around axis |
| `mirror_element` | Mirror across axis |
| `array_linear` | Linear array |
| `rename_element` | Family, FamilySymbol, or generic element |
| `place_family_instance` | `Document.Create.NewFamilyInstance` |
| `place_view_on_sheet` | Add viewport to sheet |
| `group_elements` | Group selection |
| `ungroup_elements` | Ungroup |
| `tag_element` | Tag an element in a view |
| `apply_view_filter` | ParameterFilterElement + View.SetFilterOverrides |
| `color_override_by_param` | Per-bucket color overrides by parameter value |
| `hide_elements_in_view` | Hide by ids in view |
| `unhide_elements_in_view` | Unhide by ids in view |
| `create_opening_in_wall` | Rectangular wall opening |
| `duplicate_view` | Duplicate a view (Duplicate / WithDetailing / AsDependent modes) |
| `set_section_box` | Set and activate section-box crop on a 3D view |

### Implemented — Coordination / Clash Detection

| Command | Notes |
|---|---|
| `check_clearance` | Two algorithms: `axis="bbox"` (AABB inflation, conservative) and `axis="Z"` (`ReferenceIntersector` vertical raycast, XY-accurate). Z-mode supports multi-point centreline sampling (`sampleCount`, default 3) and handles sloped MEP elements and multi-block buildings correctly. |

### Implemented — UI Actions (no model transaction)

| Command | Notes |
|---|---|
| `open_view` | Activate a view in the UI |
| `select_elements` | Set UIDocument selection |
| `zoom_to_elements` | Fit view to element bounding box |
| `set_view_detail_level` | Set view detail level |
| `isolate_elements_in_view` | Isolate (or reset) host elements in a view — UiAction, no transaction |

## Cross-version support

| Version | Status |
|---|---|
| Revit 2025 | ✅ Supported — Nice3point ref assemblies, CI-tested, port 7890 (added v0.7.0) |
| Revit 2026 | ✅ Supported — primary target, CI-tested |
| Revit 2027 | ✅ Supported — CI-tested (R2027/.NET 10 matrix added in v0.6.0) |
| Revit 2024 and earlier | ❌ Not supported (`ElementId(long)` and other API differences) |

The build uses `Nice3point.Revit.Api` reference assemblies (pinned versions)
so CI builds and tests run without Revit installed. All three supported versions
build and run unit tests (116 tests) on CI without any Revit installation.

## Remaining roadmap

### High value — next

- `create_dimension` — linear dimension from two element references.
- `get_categories_schema` — for a given category, return parameter definitions
  (name, group, storage type, unit type, builtin/instance). Lets the AI know
  what `set_parameter` calls are valid before trying.
- `purge_unused` — equivalent of the Purge dialog (with dry-run preview).
- `create_duct` / `create_pipe` — MEP element creation.

### Analysis-side (future)

- Energy / area analysis hooks.
- IFC export.
- MEP system creation and connectivity queries.

## Why we still ship a thin tool surface

Even though Revit has thousands of APIs, an LLM **doesn't need most of
them**. Empirically, ~80% of "AI co-pilot for Revit" prompts boil down to:

1. *"What's in the model?"* → introspection commands.
2. *"Add / change / delete this element"* → creation + edit commands.
3. *"Bulk update parameters"* → set_parameter loops.
4. *"Make sheets, views, schedules, tags"* → fully implemented.

So the priority is **correctness, undoability, and observability** for the
small set of commands that the AI actually reaches for, not raw count.
