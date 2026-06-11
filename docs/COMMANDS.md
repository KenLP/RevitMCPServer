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

| MCP tool                | HTTP command         | Read-only | Purpose                                              |
| ----------------------- | -------------------- | --------- | ---------------------------------------------------- |
| `revit_ping`            | `ping`               | ✅        | Health check + active document title                 |
| `revit_get_version`     | `get_revit_version`  | ✅        | Revit version, build, language, user                 |
| `revit_list_elements`   | `list_elements`      | ✅        | List instances, optionally filtered by category      |
| `revit_get_element_info`| `get_element_info`   | ✅        | All parameters + bbox of one element                 |
| `revit_list_levels`     | `list_levels`        | ✅        | All Levels, sorted by elevation                      |
| `revit_list_wall_types` | `list_wall_types`    | ✅        | All WallTypes                                        |
| `revit_list_floor_types`| `list_floor_types`   | ✅        | All FloorTypes                                       |
| `revit_list_categories` | `list_categories`    | ✅        | Categories actually used in the doc, with counts     |
| `revit_create_wall`     | `create_wall`        | ❌        | Single straight wall                                 |
| `revit_create_floor`    | `create_floor`       | ❌        | Floor from a closed polygonal profile                |
| `revit_create_level`    | `create_level`       | ❌        | Level at given elevation                             |
| `revit_create_grid`     | `create_grid`        | ❌        | Straight grid line                                   |
| `revit_set_parameter`   | `set_parameter`      | ❌        | Set one parameter on one element                     |
| `revit_delete_elements` | `delete_elements`    | ❌        | Delete by id                                         |
| `revit_move_element`    | `move_element`       | ❌        | Translate by vector                                  |
| `revit_batch`           | (POST `/mcp/batch`)  | ❌*       | Run multiple sub-commands in one Transaction          |

*Batch is read-only iff every step is read-only.

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
    "count": 60,
    "commands": [
      { "name": "ping",           "isReadOnly": true,  "riskLevel": "read",   "executionKind": "ReadOnly"   },
      { "name": "set_parameter",  "isReadOnly": false, "riskLevel": "medium", "executionKind": "ModelWrite" },
      { "name": "open_view",      "isReadOnly": false, "riskLevel": "low",    "executionKind": "UiAction"   }
    ]
  }
}
```
