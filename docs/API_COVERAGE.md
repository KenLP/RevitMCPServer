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
| **Generic query DSL** (e.g. JSON-encoded filtered element queries with category + parameter predicates) | Covers a lot of read-side use cases with one tool | Have to design the DSL, easy to footgun | ⚠️ Phase 3 (`find_elements`) |
| **Code eval** (accept arbitrary C# / Python and run inside Revit) | Total coverage in one tool | ⚠️ Massive blast radius — AI errors can corrupt models, no validation, no undo grouping, no rate limiting, hard to audit | ❌ Out of scope |

The original `revit-mcp` did the eval approach. We deliberately didn't —
**every command in this repo opens a transaction with a known name and a
known schema**. That means: review-able, undoable, and safe to whitelist in
Claude Desktop / Claude Code.

## What "good enough" coverage looks like

Phase 2 covers ~15 commands. The 80/20 sweet spot for AEC workflows is
roughly **40-60 curated commands** + introspection. Here's the roadmap of
the next operations to wrap, in priority order:

### Phase 3 — high-value writes (next)

- `set_parameter_batch` — set the same parameter on N elements in one call.
- `find_elements` — generic query: filter by category + parameter equality
  / range, return ids and a small projection. Single command that covers
  most read use cases.
- `place_family_instance` — `Document.Create.NewFamilyInstance(...)`. Big
  one. Must handle the host/level/view variants.
- `create_sheet` + `place_view_on_sheet` (`ViewSheet.Create`,
  `Viewport.Create`).
- `apply_view_filter` (`ParameterFilterElement` + `View.SetFilterOverrides`).
- `color_override_by_param` — split a category by a parameter and apply
  per-bucket `OverrideGraphicSettings`.
- `create_room` (`Document.Create.NewRoom`) + `tag_rooms`.
- `create_dimension` (linear dim from two element references).
- `transform_elements` — bulk move/rotate/array.
- `purge_unused` — equivalent of the Purge dialog for cleanup.

### Phase 4 — discoverability / introspection

- `get_categories_schema` — for a given category, return its parameter
  definitions (name, group, storage type, unit type, builtin/instance).
  Lets the AI know what `set_parameter` calls are valid before trying.
- `get_views` — list all views with type / template / discipline.
- `get_active_view` — what view is the user looking at right now.
- `get_selected_elements` — pull from `UIDocument.Selection`. Closes the
  loop with the user's manual interaction.
- `get_element_geometry` — extract solids/curves as JSON. Enables
  geometric reasoning without round-tripping the whole DWG/IFC.

### Phase 5 — analysis-side

- Schedules (`ViewSchedule.CreateSchedule`).
- Energy / area analysis hooks.
- Linked-file enumeration.

## Cross-version (2025 / 2026 / 2027)

Revit API often nudges between versions — `ElementId(int)` → `ElementId(long)`
in 2024+, slot tweaks in `Wall.Create` overloads, etc. This MVP only targets
**Revit 2026**. The build plan calls for `#if REVIT2025 / REVIT2026 /
REVIT2027` shims and a CI matrix; that's Phase 3 in
[`Revit_MCP_Server_Build_Plan.md`](../Revit_MCP_Server_Build_Plan.md).

The cleanest way to add a new version target is:

1. Add a new MSBuild Configuration (e.g. `Release R27`) in
   `RevitMCPAddin.csproj` with `<DefineConstants>REVIT2027</DefineConstants>`.
2. Wrap any signature differences with `#if REVIT2027 ... #endif`.
3. Add a CI job that builds each configuration. The reference upstream
   `mcp-servers-for-revit` already has this layout — fork it if you need a
   battle-tested build matrix.

## Why we still ship a thin tool surface

Even though Revit has thousands of APIs, an LLM **doesn't need most of
them**. Empirically, ~80% of "AI co-pilot for Revit" prompts boil down to:

1. *"What's in the model?"* → introspection commands.
2. *"Add / change / delete this element"* → creation + edit commands.
3. *"Bulk update parameters"* → set_parameter loops.
4. *"Make sheets, views, schedules, tags"* → Phase 3.

So the priority is **correctness, undoability, and observability** for the
small set of commands that the AI actually reaches for, not raw count.
