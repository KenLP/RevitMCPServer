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

> **v0.7.0 — 63 commands + 1 batch = 64 MCP tools.**
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
| `revit_delete_elements`           | `delete_elements`       | ❌        | Delete by ids                                             |
| `revit_move_element`              | `move_element`          | ❌        | Translate by vector                                       |
| `revit_copy_element`              | `copy_element`          | ❌        | Copy with offset                                          |
| `revit_rotate_element`            | `rotate_element`        | ❌        | Rotate around axis                                        |
| `revit_mirror_element`            | `mirror_element`        | ❌        | Mirror across axis                                        |
| `revit_array_linear`              | `array_linear`          | ❌        | Linear array                                              |
| `revit_group_elements`            | `group_elements`        | ❌        | Group selection                                           |
| `revit_ungroup_elements`          | `ungroup_elements`      | ❌        | Ungroup                                                   |
| `revit_tag_element`               | `tag_element`           | ❌        | Tag an element in a view                                  |
| `revit_apply_view_filter`         | `apply_view_filter`     | ❌        | ParameterFilterElement + SetFilterOverrides               |
| `revit_color_override_by_param`   | `color_override_by_param`| ❌       | Per-bucket color overrides by parameter value             |
| `revit_hide_elements_in_view`     | `hide_elements_in_view` | ❌        | Hide by ids in view                                       |
| `revit_unhide_elements_in_view`   | `unhide_elements_in_view`| ❌       | Unhide by ids in view                                     |
| `revit_open_view`                 | `open_view`             | UI        | Activate a view in the UI                                 |
| `revit_select_elements`           | `select_elements`       | UI        | Set UIDocument selection                                  |
| `revit_zoom_to_elements`          | `zoom_to_elements`      | UI        | Fit view to element bounding box                          |
| `revit_set_view_detail_level`     | `set_view_detail_level` | UI        | Set view detail level                                     |
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

**Algorithm:**

| Scenario | Method |
|---|---|
| Both sets are host elements **and** `clearanceMm = 0` | `ElementIntersectsElementFilter` (solid-based, exact) |
| Any other case (cross-doc, clearance > 0) | AABB inflation loop — fast, conservative |

Params:
- `setA` *(object, required)* — first element set (see below).
- `setB` *(object, required)* — second element set (see below).
- `clearanceMm` *(number, optional, default 0)* — required gap in mm.
  `0` = hard clash only. E.g. `50` = report any pair closer than 50 mm.

Each element-set object:
```jsonc
{
  "source":     "host" | "link",  // required
  "linkId":     391028,           // required when source = "link"
  "categories": ["OST_Ducts", "OST_PipeCurves"],  // optional filter
  "limit":      500               // optional, default 200, max 2000
}
```

Data:
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

`gapMm` is negative for hard clashes (overlap depth in mm), zero at exact
contact, positive when a clearance gap exists (should not appear in results).

> **Note:** The AABB path uses axis-aligned bounding-box approximations —
> it may produce false positives for rotated or curved elements. A solid-based
> upgrade is planned for a future release.

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
    "count": 63,
    "commands": [
      { "name": "ping",           "isReadOnly": true,  "riskLevel": "read",   "executionKind": "ReadOnly"   },
      { "name": "set_parameter",  "isReadOnly": false, "riskLevel": "medium", "executionKind": "ModelWrite" },
      { "name": "open_view",      "isReadOnly": false, "riskLevel": "low",    "executionKind": "UiAction"   }
    ]
  }
}
```
