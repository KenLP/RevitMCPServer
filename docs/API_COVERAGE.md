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

## Current command surface (v0.8.22)

**94 commands** registered (87 exposed as MCP tools + 7 hidden) across read, write, UI, and
coordination categories. With the batch transport tool and two Node-only workflow recipes
(`recipe_model_health_triage`, `recipe_clash_review`), that is **90 MCP tools**.

The 7 hidden commands are registered in C# (HTTP-callable via `/mcp`) but deliberately off the MCP
tool surface: `create_spot_elevation` (pending a reliable face-reference approach) and the
**6-command spatial-QC pack** (`spatial_get_room_boundary`, `spatial_clearance_envelope`,
`spatial_clearance_envelope_batch`, `spatial_raycast_headroom`, `spatial_get_walls`,
`spatial_get_stairs`) — pure-geometry primitives consumed programmatically by the AutomatedSpatialQC
client, not by LLM tool routing (see the Spatial-QC pack section below).

### Implemented — Read / Introspection

| Command | Notes |
|---|---|
| `ping` | Health check + active doc title |
| `get_revit_version` | Revit version, build, language |
| `get_document_info` | File path, phases, project info |
| `list_elements` | Filter by category, optional limit |
| `get_element_info` | All parameters + bbox, plus `uniqueId` — the identifier ACC / BIM 360 reports as `externalId` |
| `find_element_by_unique_id` | Resolve an element from its `UniqueId` via `Document.GetElement(string)`. `linkId` searches one link only; `searchLinks=true` sweeps every loaded link when the host has no match. Returns `foundIn` ("host"/"link") + link context. **Use this instead of deriving an ElementId from the string** — ElementId is numbered per document, so an id lifted from a linked model can address a different element in the host |
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
| `get_linked_elements` | Read elements **inside** a linked RVT; bboxes in host coords. Each element carries `uniqueId` (stable across documents) alongside `id` (valid **only** inside that link) |
| `get_view_image` | Export any view to PNG; returns base64 + MCP Image content |
| `get_element_rooms` | Phase-aware Room/FromRoom/ToRoom containment for family instances (batch) |
| `export_view_pdf` | Export a view or sheet to PDF on disk |
| `get_tags_in_view` | List IndependentTag elements in a view (optional category filter) |
| `get_model_health` | One-shot health scorecard: warnings (+ /1000-element ratio), file size, imports/links, families, groups, unused views, worksets, purgeable |
| `get_worksets` | User worksets with per-workset element counts; flags empty worksets and default "Workset1" |
| `get_schedule_data` | Read rendered ViewSchedule cell text (calculated fields/units applied); paginated by row |
| `get_doors` | All doors with width, plan XY, level, and swing geometry (facing/hand orientation) for ADA/egress checks |

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

### Spatial-QC pack — HTTP-only

Registered in C# (callable via HTTP `/mcp`) but **not exposed as MCP tools** — they are consumed
programmatically by the [AutomatedSpatialQC](https://github.com/KenLP/AutomatedSpatialQC) client (its
inputs, e.g. `loops`/`points`, are produced by other calls in the pipeline), so surfacing them to LLM
tool routing would only dilute the tool list. Prefixed `spatial_` to namespace them apart from the
curated command surface and avoid any future collision. Pure geometry, no dependency on newer infra.

| Command | Notes |
|---|---|
| `spatial_get_room_boundary` | Room boundary loops (outer ring + holes) at the **finish** face as world-XY polylines in metres (net clear area, matches `IfcSpace`). Params: `id` or `number` (optional) to target one room. |
| `spatial_clearance_envelope` | Volumetric MEP-aware clear-height check: extrudes a room footprint to a required clear volume and boolean-intersects every overhead element in host **and every linked RVT**; names each obstruction (category/id/link) with the clear height it leaves. |
| `spatial_clearance_envelope_batch` | Same check for many rooms in one call; collects + extracts candidate geometry **once** over the union of all footprints and reuses it per room (removes repeated extraction, the dominant cost). |
| `spatial_raycast_headroom` | Vertical headroom raycast: fires a ray up from each `(x,y)` on the floor, returns the lowest overhead soffit height (ceilings/floors-above/roofs/framing; stairs excluded). |
| `spatial_get_walls` | Wall plan footprints (centreline offset by half the width) + Z range + the **declared** Interior/Exterior `Function`, in world metres. Feeds the storey-envelope flood fill that decides what is truly outdoors vs. an enclosed void — which in turn drives exterior-door detection (the old "door touches ≤ 1 room" heuristic breaks on thick and curtain walls). `isExternal` is emitted **verbatim**: it is a user declaration the consumer audits against geometry, never trusts. Curtain walls (Width ≈ 0) get a nominal 0.15 m footprint so the facade is not a gap. |
| `spatial_get_stairs` | Placed stairs with Revit's own as-built riser height / tread depth / riser count, plus plan centroid and base level — the live-model equivalent of what the IFC path re-measures from the stair mesh, so max-riser / min-tread rules run without an IFC export. No per-riser breakdown exists in the API, so there is deliberately no `riserVariation`: the consumer reads a missing field as "unmeasured" (INFO) rather than a false PASS. |

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
