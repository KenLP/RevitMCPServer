---
name: revit-model
description: How to read and change the open Revit model through the RevitMCP tools (revit_*). Use when the user asks about elements, parameters, views, sheets, rooms or exports in their Revit model.
---

# Working with the Revit model

The `revit_*` tools act on the model open in the user's Revit, through the RevitMCP add-in.

## Before anything else

Call `revit_ping`. If it fails, Revit is not running, no model is open, or the add-in is not
installed for the configured Revit version. Say so and point to the plugin's README; don't retry
in a loop.

## Reading

- Prefer `revit_find_elements` (filters, chosen fields, paging) or `revit_query_where` over
  listing a category and then calling `revit_get_element_info` once per element.
- Element ids are Revit ElementId integers. Lengths are in meters unless a tool says otherwise or
  takes `units`.
- `revit_get_view_image` shows what a view looks like when a picture helps.

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

## Documents and exports

- `revit_open_document` is safe to repeat: it reports `alreadyOpen` or `wasOpen`.
- The active document cannot be closed through the API; open another model first.
- `revit_export_ifc` carries the rooms of one phase only. Check `roomsInPhase` and
  `roomsInOtherPhases` and tell the user when rooms were left out.
