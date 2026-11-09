---
name: revit-model
description: How to read and change the open Revit model through the RevitMCP tools (revit_*). Use when the user asks about elements, parameters, views, sheets, rooms, clashes, model health or exports in their Revit model.
---

# Working with the Revit model

The `revit_*` tools act on the model open in the user's Revit, through the RevitMCP add-in.

## Before anything else

Call `revit_ping`. If it fails, Revit is not running, no model is open, or the add-in is not
installed for the configured Revit version. Say so and point to the plugin's README; don't retry
in a loop.

## Finding things

- Prefer `revit_find_elements` (filters, chosen fields, paging) or `revit_query_where` (exact
  counts, regex, instance-vs-type scope) over listing a category and then calling
  `revit_get_element_info` once per element.
- Paged tools return `nextOffset`; pass it as `offset` for the next page.
- Element ids are Revit ElementId integers. Lengths are in meters unless a tool says otherwise or
  takes `units`.
- An id that comes from ACC, BIM 360 or a linked model (a 45-character `externalId` / UniqueId):
  resolve it with `revit_find_element_by_unique_id`. Don't turn it into an ElementId yourself;
  ElementIds are per document and can point at a different element.
- Where ids come from: levels `revit_list_levels`; views and sheets `revit_get_views`,
  `revit_list_sheets`; view templates `revit_list_view_templates`; rooms `revit_list_rooms`;
  types `revit_list_family_types`, `revit_list_wall_types`; links `revit_get_linked_files`.
- `revit_get_view_image` shows what a view looks like when a picture helps.

## Choosing between similar tools

- **Doors.** Swing and facing geometry (egress, maneuvering clearance) comes only from
  `revit_get_doors`. Door parameters such as FireRating, Mark or Width come from
  `revit_find_elements` with category `Doors`; the rooms a door connects from
  `revit_get_element_rooms`.
- **Model health.** For "is this model healthy, what should I fix" use
  `revit_recipe_model_health_triage`; it ranks the issues and suggests a fix for each.
  `revit_get_model_health` gives the raw metrics.
- **Clashes and clearances.** `revit_check_clearance` with `axis: "Z"` finds the nearest element
  above or below on its own: give `setB` a category filter and do not list setB's elements first.
  Use `axis: "bbox"` for a clearance zone in every direction, and `clearanceMm: 0` for hard clashes
  only. To see what a link contains, `revit_get_linked_elements`. Several pairs at once:
  `revit_recipe_clash_review`.
- **Writing parameters.** One parameter on many elements: `revit_set_parameter_batch`. A row per
  element and parameter, as from a spreadsheet: `revit_import_parameters`. Every element that
  matches conditions: `revit_update_where`, with `dryRun: true` first.
- **Showing a region.** `revit_isolate_elements_in_view` works on host elements only and hides a
  whole link. For a region that spans host and linked geometry, set a section box
  (`revit_set_section_box`), ideally on a copy made with `revit_duplicate_view`.

## Changing the model

- Writes change the user's model. Make only the change the user asked for, and confirm before
  deleting elements, saving, saving over a file or closing a document.
- For a change with many elements or one you are unsure of, run it with `dryRun: true` first and
  report what it would do.
- Several related writes go in one `revit_batch`: one transaction, one undo step, all or nothing.
- After every write, read `revitWarnings` in the result (for a batch, at the batch level). Each
  warning has a `message` and `elementIds`; act on it, for example a redundant room. `resolution`
  is only what Revit suggests and is never applied for you.
- `commit_failed` means Revit rejected the change and rolled it back: the model is unchanged. Read
  `revitErrors`, fix the cause, and try again.
- Names you pass (views, sheets, levels, groups) are applied exactly or the call fails with
  `invalid_chars` or `name_collision`; pick another name rather than retrying the same one.

## Documents and exports

- `revit_open_document` is safe to repeat: it reports `alreadyOpen` or `wasOpen`.
- A document with no file yet needs `revit_save_as` rather than `revit_save_document`.
- The active document cannot be closed through the API; open another model first.
- `revit_export_ifc` carries the rooms of one phase only. Check `roomsInPhase` and
  `roomsInOtherPhases` and tell the user when rooms were left out.
