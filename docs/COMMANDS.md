# Command Reference

All commands share the same envelope:

```jsonc
// success
{ "ok": true, "data": { /* command-specific */ } }
// failure
{ "ok": false, "error": { "code": "...", "message": "...", "type": "..." } }
```

The MCP tool name is the command name with the `revit_` prefix.
The HTTP command name is the name without the prefix (used in
`POST /mcp` `command` field and inside `revit_batch` steps).

> **v0.8.13 — 86 commands + 1 batch + 2 recipes = 89 MCP tools** (5 hidden: `create_spot_elevation` + the 4-command spatial-QC HTTP pack `spatial_*`; 91 C# commands registered; workflow recipes are Node-only).
>
> **Pagination:** `list_elements` and `find_elements` accept `offset` (default 0) +
> `limit` (default 200, max 5000) and return `total`, `hasMore`, and `nextOffset`.
> Page through any size by repeating with `offset = nextOffset`; there is no 5000 ceiling
> on total reach, only on per-page size.
> This table shows a representative subset; see [`API_COVERAGE.md`](API_COVERAGE.md) for the full list.

| MCP tool                          | HTTP command            | Read-only | Purpose                                                   |
| --------------------------------- | ----------------------- | --------- | --------------------------------------------------------- |
| `revit_ping`                      | `ping`                  | ✅        | Health check + active document title                      |
| `revit_get_version`               | `get_revit_version`     | ✅        | Revit version, build, language, user                      |
| `revit_get_document_info`         | `get_document_info`     | ✅        | File path, phases, project info                           |
| `revit_list_elements`             | `list_elements`         | ✅        | List instances, optionally filtered by category           |
| `revit_get_element_info`          | `get_element_info`      | ✅        | All parameters + bbox of one element                      |
| `revit_get_element_geometry`      | `get_element_geometry`  | ✅        | Solid/curve geometry, volume, surface area                |
| `revit_get_parameter`             | `get_parameter`         | ✅        | Single parameter value                                    |
| `revit_find_elements`             | `find_elements`         | ✅        | Query: category + parameter predicates                    |
| `revit_list_levels`               | `list_levels`           | ✅        | All Levels, sorted by elevation                           |
| `revit_list_wall_types`           | `list_wall_types`       | ✅        | All WallTypes                                             |
| `revit_list_floor_types`          | `list_floor_types`      | ✅        | All FloorTypes                                            |
| `revit_list_categories`           | `list_categories`       | ✅        | Categories actually used in the doc, with counts          |
| `revit_list_families`             | `list_families`         | ✅        | Loaded Families, optionally by category                   |
| `revit_list_family_types`         | `list_family_types`     | ✅        | FamilySymbols by family or category                       |
| `revit_list_sheets`               | `list_sheets`           | ✅        | All sheets with number, name, viewport count              |
| `revit_list_rooms`                | `list_rooms`            | ✅        | All placed rooms with area, number, level                 |
| `revit_list_spaces`               | `list_spaces`           | ✅        | All placed MEP Spaces with area, volume, number, level    |
| `revit_list_materials`            | `list_materials`        | ✅        | All materials                                             |
| `revit_list_phases`               | `list_phases`           | ✅        | All phases                                                |
| `revit_list_view_templates`       | `list_view_templates`   | ✅        | All view templates                                        |
| `revit_get_views`                 | `get_views`             | ✅        | All non-template views with type, level, scale            |
| `revit_get_active_view`           | `get_active_view`       | ✅        | Current active view info                                  |
| `revit_get_selected_elements`     | `get_selected_elements` | ✅        | Elements currently selected in Revit UI                   |
| `revit_get_linked_files`          | `get_linked_files`      | ✅        | List Revit link instances (metadata)                      |
| `revit_get_linked_elements`       | `get_linked_elements`   | ✅        | Elements inside a linked RVT (bboxes in host coords)      |
| `revit_check_clearance`           | `check_clearance`       | ✅        | Hard clash + clearance check, host and cross-linked-file  |
| `revit_get_view_image`            | `get_view_image`        | ✅        | Export view to PNG; returns MCP Image content block       |
| `revit_get_element_rooms`         | `get_element_rooms`     | ✅        | Room containment for family instances — Room/FromRoom/ToRoom per element (batch) |
| `revit_get_model_health`          | `get_model_health`      | ✅        | One-shot model health scorecard: warnings (+ /1000-element ratio), file size, imports/links (CAD, PDF, RVT, point cloud), in-place families, groups, unused views, worksets, purgeable |
| `revit_get_worksets`              | `get_worksets`          | ✅        | User worksets with per-workset element counts; flags empty worksets and default 'Workset1' name |
| `revit_get_schedule_data`         | `get_schedule_data`     | ✅        | Read rendered ViewSchedule cell text (calc fields/units applied); paginated by row |
| `revit_get_doors`                 | `get_doors`             | ✅        | All doors with width, plan XY, level, and swing geometry (facing/hand orientation vectors) for ADA/egress checks |
| `revit_load_family`               | `load_family`           | ❌        | Load a family (.rfa) from disk; returns family id + types |
| `revit_duplicate_family_type`     | `duplicate_family_type` | ❌        | Duplicate a FamilySymbol under a new name |
| `revit_create_detail_line`        | `create_detail_line`    | ❌        | View-specific detail line in a 2D view (projected onto view plane) |
| `revit_create_filled_region`      | `create_filled_region`  | ❌        | Filled region from a closed boundary in a 2D view |
| `revit_create_wall`               | `create_wall`           | ❌        | Single straight wall                                      |
| `revit_create_floor`              | `create_floor`          | ❌        | Floor from a closed polygonal profile                     |
| `revit_create_ceiling`            | `create_ceiling`        | ❌        | Ceiling from a closed polygonal profile                   |
| `revit_create_level`              | `create_level`          | ❌        | Level at given elevation                                  |
| `revit_create_grid`               | `create_grid`           | ❌        | Straight grid line                                        |
| `revit_create_column`             | `create_column`         | ❌        | Structural or architectural column                        |
| `revit_create_beam`               | `create_beam`           | ❌        | Structural beam between two points                        |
| `revit_create_room`               | `create_room`           | ❌        | Room by point on level                                    |
| `revit_create_sheet`              | `create_sheet`          | ❌        | New sheet with title block                                |
| `revit_create_schedule`           | `create_schedule`       | ❌        | ViewSchedule for a category                               |
| `revit_create_3d_view`            | `create_3d_view`        | ❌        | Named 3D view                                             |
| `revit_create_floor_plan_view`    | `create_floor_plan_view`| ❌        | Floor plan for a level                                    |
| `revit_create_section_view`       | `create_section_view`   | ❌        | Section view                                              |
| `revit_create_text_note`          | `create_text_note`      | ❌        | Text note in a view                                       |
| `revit_create_opening_in_wall`    | `create_opening_in_wall`| ❌        | Rectangular opening in a wall                             |
| `revit_place_family_instance`     | `place_family_instance` | ❌        | Place a loaded family instance                            |
| `revit_place_view_on_sheet`       | `place_view_on_sheet`   | ❌        | Add viewport to sheet                                     |
| `revit_set_parameter`             | `set_parameter`         | ❌        | Set one parameter on one element (with unit conversion)   |
| `revit_set_parameter_batch`       | `set_parameter_batch`   | ❌        | Same parameter on N elements (atomic or best-effort)      |
| `revit_rename_element`            | `rename_element`        | ❌        | Family, FamilySymbol, or generic element                  |
| `revit_change_element_type`       | `change_element_type`   | ❌        | Swap element type (wall type, floor type, family symbol)  |
| `revit_apply_view_template`       | `apply_view_template`   | ❌        | Apply or remove a view template; lookup by id or name     |
| `revit_copy_parameters`           | `copy_parameters`       | ❌        | Copy parameter values from source element to N targets    |
| `revit_configure_schedule`        | `configure_schedule`    | ❌        | Add filters, sort/group fields to a schedule; CSV export  |
| `revit_set_level_elevation`       | `set_level_elevation`   | ❌        | Change Level elevation (meters / feet / mm / internal)    |
| `revit_export_view_pdf`           | `export_view_pdf`       | ✅        | Export view or sheet to PDF on disk                       |
| `revit_delete_elements`           | `delete_elements`       | ❌        | Delete by ids                                             |
| `revit_move_element`              | `move_element`          | ❌        | Translate by vector                                       |
| `revit_copy_element`              | `copy_element`          | ❌        | Copy with offset                                          |
| `revit_rotate_element`            | `rotate_element`        | ❌        | Rotate around axis                                        |
| `revit_mirror_element`            | `mirror_element`        | ❌        | Mirror across axis                                        |
| `revit_array_linear`              | `array_linear`          | ❌        | Linear array                                              |
| `revit_group_elements`            | `group_elements`        | ❌        | Group selection                                           |
| `revit_ungroup_elements`          | `ungroup_elements`      | ❌        | Ungroup                                                   |
| `revit_tag_element`               | `tag_element`           | ❌        | Tag an element in a view                                  |
| `revit_tag_all_in_view`           | `tag_all_in_view`       | ❌        | Tag all untagged elements of a category in a view         |
| `revit_get_tags_in_view`          | `get_tags_in_view`      | ✅        | List all IndependentTag elements in a view (optional category filter) |
| `revit_create_aligned_dimension`  | `create_aligned_dimension` | ❌     | Aligned dimension chain: Grids, Walls (centreline/face), columns, beams |
| `revit_apply_view_filter`         | `apply_view_filter`     | ❌        | ParameterFilterElement + SetFilterOverrides               |
| `revit_color_override_by_param`   | `color_override_by_param`| ❌       | Per-bucket color overrides by parameter value             |
| `revit_hide_elements_in_view`     | `hide_elements_in_view` | ❌        | Hide by ids in view                                       |
| `revit_unhide_elements_in_view`   | `unhide_elements_in_view`| ❌       | Unhide by ids in view                                     |
| `revit_duplicate_view`            | `duplicate_view`        | ❌        | Duplicate a view (with or without detailing / as dependent) |
| `revit_set_section_box`           | `set_section_box`       | ❌        | Set and activate the section box on a 3D view             |
| `revit_open_view`                 | `open_view`             | UI        | Activate a view in the UI                                 |
| `revit_select_elements`           | `select_elements`       | UI        | Set UIDocument selection                                  |
| `revit_zoom_to_elements`          | `zoom_to_elements`      | UI        | Fit view to element bounding box                          |
| `revit_set_view_detail_level`     | `set_view_detail_level` | UI        | Set view detail level                                     |
| `revit_isolate_elements_in_view`  | `isolate_elements_in_view` | UI     | Isolate (or reset) host elements in the active or given view |
| `revit_batch`                     | (POST `/mcp/batch`)     | ❌*       | Run multiple sub-commands in one Transaction              |

\* Batch is read-only iff every step is read-only. UI-only batches skip the Transaction.

---

## Dry-run mode

Every write command accepts `"dryRun": true` (in the JSON body) or
`?dryRun=true` (query string). The command executes normally inside a
Transaction, but the transaction is rolled back instead of committed.

```jsonc
// success (dry-run)
{ "ok": true, "dryRun": true, "committed": false, "data": { /* same as real run */ } }
```

This lets the AI preview what would happen without modifying the model.

## Structured diffs

Write commands include a `changeSummary` one-liner in `data`:

```jsonc
{ "ok": true, "data": { ..., "changeSummary": "Set 'Comments' on element 184239: '' → 'Reviewed'" } }
```

Modify commands (`set_parameter`, `rename_element`, `move_element`) also
include a `changes` object with `before`/`after` values for detailed review.

---

## Units

Every command that accepts a length defaults to **meters** for input/output
convenience. Pass `"units": "feet"` to switch to Revit's internal unit. Length
fields in the response are always reported in **feet** (Revit internal),
labelled with the `Feet` suffix, e.g. `lengthFeet`, `elevationFeet`. The
`set_parameter` command is the exception — see below.

---

## Diagnostics

### `ping`
Params: none.
Data:
```jsonc
{ "pong": true, "hasActiveDocument": true, "activeDocumentTitle": "Project1.rvt" }
```

### `get_revit_version`
Params: none.
Data: `versionName`, `versionNumber`, `versionBuild`, `subVersionNumber`, `language`, `username`.

---

## Inspection

### `list_elements`
Params:
- `category` *(string, optional)* — `BuiltInCategory` enum name like `OST_Walls`.
- `onlyInstances` *(bool, optional, default true)* — exclude element types.
- `limit` *(int, optional, default 200, max 5000)*.

Data:
```jsonc
{
  "count": 12, "limit": 200, "truncated": false,
  "elements": [
    { "id": 184239, "name": "Generic - 200mm", "category": "Walls", "categoryEnum": "OST_Walls", "typeId": 184227 },
    ...
  ]
}
```

### `get_element_info`
Params: `id` *(long, required)*.
Data: identity, `levelId`, `boundingBox.{min,max}`, full `parameters[]` with
`storageType`, raw `value`, and human-readable `valueString`.

### `list_levels`, `list_wall_types`, `list_floor_types`
Params: none. Returns name + id + family/elevation info as relevant.

### `list_categories`
Params: none. Returns only categories with at least one instance, sorted by
descending instance count. Cheaper than dumping every BuiltInCategory.

### `list_spaces`
List all **placed MEP Spaces** (`OST_MEPSpaces`) in the host document.

Params:
- `levelId` *(long, optional)* — filter to a single level (use `list_levels` to get ids).
- `limit` *(int, optional, default 500, max 2000)*.

Data:
```jsonc
{
  "count": 14,
  "spaces": [
    {
      "id": 838291,
      "name": "P01 Parking Garage",
      "number": "P01",
      "levelId": 838100,
      "levelName": "L1/Parking",
      "area": 1948.5,
      "areaM2": 181.07,
      "volume": 17536.5,
      "volumeM3": 496.72,
      "spaceType": "NormalSpace"
    }
  ]
}
```

> Only spaces with `Area > 0` (placed, not unplaced) are returned.
> `area` / `volume` are in Revit internal units (ft² / ft³); `areaM2` / `volumeM3` are in m².

---

## Creation

### `create_wall`
Params:
- `start`, `end` *({x,y,z?}, required)*
- `height` *(number, optional, default 3.0)*
- `levelName` *(string, optional)*
- `wallTypeName` *(string, optional)*
- `structural` *(bool, optional)*
- `units` *(`"meters"|"feet"`, optional)*

Data: `id`, `levelName`, `wallTypeName`, `lengthFeet`, `heightFeet`.

### `create_floor`
Params:
- `profile` *(array of ≥3 {x,y,z?}, required)* — z is ignored, points are
  flattened to the level elevation.
- `levelName`, `floorTypeName`, `units` — same conventions as above.

Data: `id`, `levelName`, `floorTypeName`, `pointCount`.

### `create_level`
Params:
- `elevation` *(number, required)*
- `name` *(string, optional)* — name is applied after creation; if Revit
  rejects it (duplicate, invalid chars) the level is still created and the
  message is returned in `renameWarning`.
- `units` *("meters"|"feet")*

Data: `id`, `elevationFeet`, `name`.

### `create_grid`
Params: `start`, `end` *({x,y,z?}, required)*, `name` *(optional)*, `units`.
Z is forced to 0 — grids are flat in plan.
Data: `id`, `name`, `lengthFeet`, optionally `renameWarning`.

---

## Edit

### `set_parameter`
Params:
- `id` *(long, required)*
- `parameterName` *(string, required)*
- `value` *(string | number | bool | { id: long }, required)*
- `units` *(`"internal"` | `"meters"` | `"feet"` | `"square_meters"` | `"square_feet"` | `"cubic_meters"` | `"cubic_feet"`, optional, default `"internal"`)*

The value is auto-coerced to the parameter's `StorageType`:

| Storage type | Accepts                        |
|--------------|--------------------------------|
| `String`     | string                         |
| `Integer`    | int — or bool (Yes/No params)  |
| `Double`     | number, with optional unit conversion (see below) |
| `ElementId`  | long, or `{ "id": long }`      |

**Unit conversion for `Double` parameters:**

When `units` is not `"internal"`, the value is converted to Revit internal units
(decimal feet for length) using the parameter's spec type:

| Parameter spec | Accepted `units` values |
|----------------|------------------------|
| Length         | `"meters"`, `"feet"` |
| Area           | `"square_meters"`, `"square_feet"` |
| Volume         | `"cubic_meters"`, `"cubic_feet"` |
| Dimensionless  | conversion never applied |
| Other measurable (angle, force, …) | must use `"internal"` — `invalid_parameter` otherwise |

Data: `written` (Revit's `Parameter.Set` return value), `newValueString`,
`inputUnits` (the `units` value used), `unitConversionApplied` (bool).

### `delete_elements`
Params: `ids` *(long[], required, ≥1)*.
Data: `requested`, `deleted`, `deletedIds`. Note: Revit may delete more than
you asked because of dependent cleanup.

### `move_element`
Params: `id`, `translation` *({x,y,z?}, required)*, `units`.
Data: `id`, `name`, `translationFeet`.

### `rename_element`
Params:
- `id` *(long, required)* — element id (Family, FamilySymbol, or any element).
- `name` *(string, required)* — new name.

Handles three distinct rename paths automatically:

| Element type | Rename mechanism | Validation |
|---|---|---|
| `Family` | Direct property `family.Name` | System family check, illegal chars, name collision |
| `FamilySymbol` (Type) | Direct property `symbol.Name` | Illegal chars, duplicate type within family |
| Everything else | `Element.Name` virtual setter | Standard Revit rules |

Data (Family / FamilySymbol):
```jsonc
{
  "id": 12345,
  "elementType": "Family",
  "oldName": "OLD_Door_Ext",
  "newName": "SRA_Door_Ext_Sgl",
  "instancesAffected": 42,
  "changeSummary": "Renamed Family 12345: 'OLD_Door_Ext' → 'SRA_Door_Ext_Sgl' (42 instances affected)",
  "changes": { "before": "OLD_Door_Ext", "after": "SRA_Door_Ext_Sgl" }
}
```

Error codes:
- `system_family` — cannot rename built-in families (Basic Wall, Floor, etc.).
- `name_collision` — a Family/Type with the same name already exists.
- `invalid_chars` — name contains `\ : { } [ ] | ; < > ? * ~`.

---

## Room containment

### `get_element_rooms`

Get room containment for one or more family instances using Revit's phase-aware API —
authoritative for wall-hosted elements (doors/windows) whose centroid lies inside the
wall between two rooms.

Params:
- `ids` *(long[], required)* — ElementIds to query. Accepts 1–N ids in one call.

Data:
```jsonc
{
  "count": 2,
  "elements": [
    {
      "id": 631418,
      "phaseId": 118390,
      "room":     null,
      "fromRoom": { "id": 830936, "name": "Parking Garage P01", "number": "P01" },
      "toRoom":   { "id": 826376, "name": "Stair S1",           "number": "S1"  }
    },
    {
      "id": 738550,
      "phaseId": 118390,
      "room":     { "id": 828508, "name": "Café 101", "number": "101" },
      "fromRoom": null,
      "toRoom":   null
    }
  ]
}
```

- `fromRoom` + `toRoom` apply to **boundary connectors** (Doors, Windows, openings).
- `room` applies to **point-located elements** (Furniture, Plumbing Fixtures,
  Lighting Fixtures, Mechanical/Electrical Equipment, etc.).
- Any field is `null` when the side is exterior, the region is unbounded, or the
  family's Room Calculation Point is disabled.
- `phaseId` is the phase used for resolution (element's `Phase Created` parameter,
  falling back to the document's last phase).
- Non-FamilyInstance elements (walls, floors, …) return all fields null.

> Use `revit_list_rooms` to discover room ids, names, and numbers in the model.

---

## Element type, templates & parameter copy

### `change_element_type`

Swap the type of an element (e.g. change a wall from Generic-200mm to Generic-300mm,
or change a door family symbol).

Params:
- `id` *(long, required)* — ElementId of the element to change.
- `typeId` *(long, required)* — ElementId of the target type. Use
  `revit_list_wall_types`, `revit_list_floor_types`, or `revit_list_family_types`
  to find valid type ids.
- `dryRun` *(bool, optional)*.

Data: `id`, `oldTypeId`, `oldTypeName`, `newTypeId`, `newTypeName`, `changeSummary`.

Error codes:
- `wrong_element_type` → 400 — the target type is not valid for this element.

### `apply_view_template`

Apply or remove a view template from a view.

Params:
- `viewId` *(long, required)*.
- `templateId` *(long, optional)* — ElementId of the view template. Pass `-1` to remove.
- `templateName` *(string, optional)* — case-insensitive name lookup (alternative to `templateId`).
- `dryRun` *(bool, optional)*.

Data: `viewId`, `viewName`, `oldTemplateId`, `newTemplateId`, `newTemplateName`, `changeSummary`.

### `copy_parameters`

Copy parameter values from a source element to N target elements in one call.

Params:
- `sourceId` *(long, required)* — element to copy from.
- `targetIds` *(long[], required)* — elements to copy to.
- `parameterNames` *(string[], optional)* — names to copy. Omit to copy all writable
  parameters with matching name and StorageType on both elements.
- `dryRun` *(bool, optional)*.

Data: `sourceId`, `paramsCopied`, `targets[]` (each: `targetId`, `ok`, `paramsCopied`, `failures?`).

---

## Schedule configuration

### `configure_schedule`

Add filters and sort/group fields to an existing ViewSchedule.

Params:
- `scheduleId` *(long, required)* — use `revit_list_sheets` or `revit_get_views` to find ids.
- `clearFilters` *(bool, optional)* — remove all existing filters first.
- `clearSortFields` *(bool, optional)* — remove all existing sort/group fields first.
- `filters` *(array, optional)* — each item: `{ field, operator?, value? }`.
  `operator`: `equals` | `not_equals` | `greater` | `greater_equal` | `less` |
  `less_equal` | `contains` | `not_contains` | `begins_with` | `ends_with` |
  `has_value` | `has_no_value`. Default `"equals"`.
- `sortFields` *(array, optional)* — each item: `{ field, ascending?, groupBy? }`.
  `groupBy: true` adds a group-header row for each distinct value.
- `exportCsv` *(bool, optional)* — export the schedule to CSV and return content in response.
- `dryRun` *(bool, optional)*.

Data: `scheduleId`, `scheduleName`, `filtersAdded`, `sortFieldsAdded`, optional `csvContent`, optional `warnings`.

---

## Level elevation

### `set_level_elevation`

Change the elevation of a Level element.

Params:
- `id` *(long, required)* — use `revit_list_levels` to find level ids.
- `elevation` *(number, required)* — new elevation value.
- `units` *(`"meters"` | `"feet"` | `"mm"` | `"internal"`, optional, default `"meters"`)*.

Data: `id`, `name`, `oldElevationM`, `newElevationM`, `oldElevationFt`, `newElevationFt`, `changeSummary`.

---

## PDF export

### `export_view_pdf`

Export a view or sheet to a PDF file on disk.

Params:
- `viewId` *(long, optional)* — defaults to the active view.
- `outputFolder` *(string, optional)* — defaults to the user's Documents folder.
- `fileName` *(string, optional)* — base name without `.pdf` extension. Defaults to
  view name + timestamp.
- `rasterQuality` *(`"Low"` | `"Medium"` | `"High"` | `"Presentation"`, optional, default `"Medium"`)*.
- `colorMode` *(`"Color"` | `"Grayscale"` | `"BlackLine"`, optional, default `"Color"`)*.

Data: `viewId`, `viewName`, `outputPath`, `fileSizeBytes`, `rasterQuality`, `colorMode`.

---

## Linked files

### `get_linked_elements`

Read elements from inside a linked Revit file without opening it separately.
Bounding boxes are transformed into host-document coordinates.

Params:
- `linkId` *(long, required)* — element id of the `RevitLinkInstance`. Use
  `get_linked_files` to list available links and their ids.
- `category` *(string, optional)* — `BuiltInCategory` name (e.g. `OST_Ducts`).
  Omit to return all categories.
- `limit` *(int, optional, default 200, max 2000)*.

Data:
```jsonc
{
  "linkId": 391028,
  "linkName": "MEP_SERVICES",
  "linkedDocTitle": "Snowdon_MEP.rvt",
  "category": "OST_Ducts",
  "count": 47,
  "truncated": false,
  "elements": [
    {
      "id": 120045,
      "name": "Rectangular Duct : 400x300",
      "category": "Ducts",
      "bboxMin": { "x": 1.23, "y": 4.56, "z": 3.10 },
      "bboxMax": { "x": 6.78, "y": 5.10, "z": 3.40 }
    }
  ]
}
```

> All coordinate values are in Revit internal units (decimal feet), in the
> host document's coordinate system.

---

## Coordination / clash

### `check_clearance`

Detect hard clashes or minimum-clearance violations between two sets of
elements. Each set can come from the host document or from a linked file.

**Algorithm — selected with `axis` param:**

| `axis` | Method | Best for |
|---|---|---|
| `"bbox"` *(default)* | AABB inflation loop — fast, conservative | Quick cross-category sweep; may false-positive on rotated elements |
| `"Z"` | `ReferenceIntersector` vertical raycast from element cross-section | MEP vs structural (ducts/pipes below floor slabs); XY-accurate, handles multi-block buildings |

Params:
- `setA` *(object, required)* — first element set (see below).
- `setB` *(object, required)* — second element set (see below).
- `clearanceMm` *(number, optional, default 0)* — required gap in mm.
  `0` = hard clash only. E.g. `50` = report any pair closer than 50 mm.
- `axis` *(`"bbox"` | `"Z"`, optional, default `"bbox"`)* — algorithm selector.
- `direction` *(`"below"` | `"above"`, optional, default `"below"`)* — raycast direction when `axis="Z"`. Use `"above"` to check clearance above an element (e.g. duct vs ceiling).
- `viewId` *(long, required when `axis="Z"`)* — element id of a 3D view whose visibility determines what the raycast can hit. The view must have both element sets visible.
- `sampleCount` *(int 1–10, optional, default 3)* — `axis="Z"` only: number of points sampled along each element's centreline (`LocationCurve`). Default `3` (start/mid/end). Increase to `5` for long sloped elements spanning multiple floor slabs.
- `maxResults` *(int, optional, default 200)* — cap on violations returned.

Each element-set object:
```jsonc
{
  "source":     "host" | "link",  // required
  "linkId":     391028,           // required when source = "link"
  "categories": ["OST_Ducts", "OST_PipeCurves"],  // optional filter
  "limit":      500               // optional, default 200, max 2000
}
```

Data (`axis="bbox"`):
```jsonc
{
  "clearanceMm": 50,
  "algorithm": "aabb_inflation",
  "setACount": 47,
  "setBCount": 23,
  "clashCount": 3,
  "clashes": [
    {
      "elementA": { "id": 120045, "name": "Rectangular Duct : 400x300", "source": "link", "linkId": 391028 },
      "elementB": { "id": 284710, "name": "Basic Wall : Generic - 200mm", "source": "host" },
      "gapMm": -12.4
    }
  ]
}
```

Data (`axis="Z"`):
```jsonc
{
  "clearanceMm": 150,
  "algorithm": "raycast_Z",
  "method": "ReferenceIntersectorZ",
  "sampleCount": 3,
  "setACount": 84,
  "setBCount": 12,
  "clashCount": 16,
  "clashes": [
    {
      "elementA": { "id": 580423, "name": "Rectangular Duct : 600x400", "source": "host" },
      "elementB": { "id": 312880, "name": "Floor : 200mm Concrete", "source": "host" },
      "clearanceActualMm": 87.3
    }
  ]
}
```

`gapMm` (bbox mode) is negative for hard clashes (overlap depth in mm).
`clearanceActualMm` (Z-raycast mode) is the measured gap between the element's
cross-section face and the hit surface.

> **axis=Z notes:**
> - Vertical elements (`dZ / max(dX,dY) > 1.5`) are automatically excluded.
> - Requires a 3D view (`viewId`) that has both element sets visible — the
>   raycast only hits geometry visible in that view.
> - Uses `RBS_CURVE_HEIGHT_PARAM` / `RBS_CURVE_DIAMETER_PARAM` for accurate
>   cross-section half-height on MEP curves, regardless of duct slope.
>
> **axis=bbox note:** Uses AABB approximations — may produce false positives
> for rotated or curved elements. A solid-based upgrade is planned.

---

## View manipulation

### `duplicate_view`

Duplicate a view, optionally renaming it.

Params:
- `viewId` *(long, required)* — id of the view to duplicate.
- `duplicateOption` *(`"Duplicate"` | `"WithDetailing"` | `"AsDependent"`, optional, default `"WithDetailing"`)* — Revit duplicate mode.
  - `"Duplicate"` — geometry only (no annotations/tags).
  - `"WithDetailing"` — geometry + view-specific annotations and tags.
  - `"AsDependent"` — creates a dependent child view (crop region subset).
- `newName` *(string, optional)* — rename the copy after creation. If omitted, Revit assigns a default name ("Copy of …").

Data:
```jsonc
{
  "originalViewId": 1550525,
  "newViewId": 1551820,
  "newViewName": "3D HVAC Clearance Check",
  "duplicateOption": "WithDetailing"
}
```

Error codes:
- `not_found` — `viewId` does not exist.
- `cannot_duplicate` — the view type does not support the requested option (e.g. `AsDependent` on a 3D view).

---

### `set_section_box`

Set (and activate) the section-box crop on a 3D view. Pass `enable: false` to
turn the section box off without changing its bounds.

Params:
- `viewId` *(long, required)* — must be a `View3D`.
- `min` *({x,y,z}, required)* — minimum corner of the box.
- `max` *({x,y,z}, required)* — maximum corner of the box.
- `units` *(`"feet"` | `"mm"` | `"meters"`, optional, default `"feet"`)* — coordinate units.
- `paddingMm` *(number, optional, default 0)* — uniform outward padding applied to all six faces after unit conversion.
- `enable` *(bool, optional, default `true`)* — set `false` to disable the section box (bounds are updated but `IsSectionBoxActive` is set to `false`).

Data:
```jsonc
{
  "viewId": 1551820,
  "viewName": "3D HVAC Clearance Check",
  "sectionBoxActive": true,
  "minFeet": { "x": 10.5, "y": 22.1, "z": -1.0 },
  "maxFeet": { "x": 28.3, "y": 45.0, "z": 12.5 }
}
```

> **Linked elements:** the section box crops geometry spatially — it affects
> both host and linked content within the cropped volume. You cannot isolate
> individual elements inside a linked file using this command; use the section
> box as a spatial workaround.

---

### `isolate_elements_in_view`

Isolate (or reset) elements in a view using Revit's built-in temporary
isolation (equivalent to the "Isolate Element" UI action).

Params:
- `ids` *(long[], required unless `reset=true`)* — host element ids to isolate.
- `viewId` *(long, optional)* — view to apply isolation in. Defaults to the
  currently active view if omitted.
- `reset` *(bool, optional, default `false`)* — pass `true` to exit temporary
  isolation mode (equivalent to "Reset Temporary Hide/Isolate").

Data:
```jsonc
{
  "viewId": 1551820,
  "isolatedCount": 5,
  "reset": false
}
```

> **Limitation:** isolation is host-document elements only. Individual elements
> inside a linked RVT cannot be isolated — use `set_section_box` to spatially
> crop the view to the area of interest instead.

> **ExecutionKind:** `UiAction` — no model transaction is opened; the change
> is immediately visible in the UI but is not part of the undo stack.

---

## View image

### `get_view_image`

Export any Revit view to PNG and return it as a base64-encoded MCP Image
content block. The image appears inline in the AI chat.

Params:
- `viewId` *(long, optional)* — element id of the view to export. Defaults to
  the currently active view if omitted.
- `dpi` *(int, optional, default 72)* — export resolution. Typical values:
  72 (screen), 150 (review), 300 (print).

Data (text portion of the MCP response):
```jsonc
{
  "ok": true,
  "data": {
    "viewId": 140023,
    "viewName": "{3D}",
    "viewType": "ThreeD",
    "dpi": 72,
    "fileSizeBytes": 184320,
    "mimeType": "image/png"
  }
}
```

The MCP response also includes an `Image` content block containing the raw
PNG bytes (base64). Claude will render it directly in the conversation.

> **Large views:** high-dpi exports of complex models can take several seconds
> and produce large payloads. Keep `dpi ≤ 150` for interactive use.

---

## Batch

`POST /mcp/batch` (or MCP tool `revit_batch`):

```jsonc
{
  "stopOnError": true,
  "steps": [
    { "command": "create_level", "params": { "elevation": 4, "name": "L4" } },
    { "command": "create_grid",  "params": { "start": {"x":0,"y":0}, "end": {"x":30,"y":0}, "name": "1" } },
    { "command": "create_wall",  "params": { "start": {"x":0,"y":0}, "end": {"x":5,"y":0}, "height": 3, "levelName": "L4" } }
  ]
}
```

Response (success):
```jsonc
{
  "ok": true,
  "committed": true,
  "count": 3,
  "hadFailures": false,
  "results": [
    { "ok": true, "data": { ... }, "index": 0, "command": "create_level" },
    { "ok": true, "data": { ... }, "index": 1, "command": "create_grid"  },
    { "ok": true, "data": { ... }, "index": 2, "command": "create_wall"  }
  ]
}
```

Response (rolled back):
```jsonc
{
  "ok": false,
  "error": { "code": "batch_aborted", "message": "Batch aborted at step 2 ('create_wall'): ..." },
  "committed": false,
  "results": [
    { "ok": true,  "data": {...}, "index": 0, "command": "create_level" },
    { "ok": true,  "data": {...}, "index": 1, "command": "create_grid"  },
    { "ok": false, "error": {...}, "index": 2, "command": "create_wall" }
  ]
}
```

`stopOnError: false` continues after failures, commits the successful steps,
and reports `hadFailures: true`.

---

## HTTP introspection

### `GET /health`
Returns `{ ok, service, version }`. No active document required.

### `GET /commands`
Lists every registered command with `isReadOnly` flag, `riskLevel`
(`read` | `low` | `medium` | `high`), and `executionKind`
(`ReadOnly` | `ModelWrite` | `UiAction`) — useful for AI clients that want
to discover the available surface and build per-tool permission policies
at runtime.

```jsonc
{
  "ok": true,
  "data": {
    "count": 66,
    "commands": [
      { "name": "ping",           "isReadOnly": true,  "riskLevel": "read",   "executionKind": "ReadOnly"   },
      { "name": "set_parameter",  "isReadOnly": false, "riskLevel": "medium", "executionKind": "ModelWrite" },
      { "name": "open_view",      "isReadOnly": false, "riskLevel": "low",    "executionKind": "UiAction"   }
    ]
  }
}
```
