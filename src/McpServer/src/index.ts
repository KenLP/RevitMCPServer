#!/usr/bin/env node
/**
 * Revit MCP Server v0.5.0 (stdio).
 *
 * 60 tools covering diagnostics, inspection, creation, editing,
 * transform, view manipulation, and batch operations.
 *
 * v0.4.0 additions:
 *   - dryRun parameter on every write tool (preview without committing).
 *   - Auth token (reads from token file or env var).
 *   - Structured diffs (changeSummary + changes) in write responses.
 *   - Per-tool risk levels surfaced via GET /commands.
 */

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import {
  callRevit,
  callRevitBatch,
  envelopeToToolResult,
  checkRevitHealth,
  REVIT_BASE_URL,
} from "./revitClient.js";

const server = new McpServer({ name: "revit-mcp-server", version: "0.5.0" });

// ── Common schemas ──────────────────────────────────────────────────────────
const xyz = z.object({ x: z.number(), y: z.number(), z: z.number().optional() });
const unitsField = z.enum(["meters", "feet"]).optional().describe("Units. Default 'meters'.");
const idsField = z.array(z.number().int()).min(1).describe("Array of ElementId values.");
const dryRunField = z.boolean().optional().describe("Preview mode: execute but rollback — model is not modified. Default false.");

// Helper: forward a single command (read-only, no dryRun)
const fwd = (cmd: string) => async (params: Record<string, unknown>) =>
  envelopeToToolResult(await callRevit(cmd, params));

// Helper: forward a write command (supports dryRun)
const fwdWrite = (cmd: string) => async (params: Record<string, unknown>) => {
  const { dryRun, ...rest } = params;
  return envelopeToToolResult(await callRevit(cmd, rest, dryRun === true));
};

// ═══════════════════════════════════════════════════════════════════════════
// DIAGNOSTICS
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_ping", "Health check. Reports active doc title.", {}, fwd("ping"));
server.tool("revit_get_version", "Get Revit version, build, language, user.", {}, fwd("get_revit_version"));
server.tool("revit_get_document_info", "Get project info: title, path, worksharing, active view, project metadata.", {}, fwd("get_document_info"));

// ═══════════════════════════════════════════════════════════════════════════
// INSPECTION / INTROSPECTION
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_list_elements", "List elements filtered by BuiltInCategory.", {
  category: z.string().optional().describe("BuiltInCategory, e.g. 'OST_Walls'."),
  onlyInstances: z.boolean().optional(),
  limit: z.number().int().min(1).max(5000).optional(),
}, fwd("list_elements"));

server.tool("revit_get_element_info", "Full parameters + bbox of one element.", {
  id: z.number().int(),
}, fwd("get_element_info"));

server.tool("revit_find_elements", "Query elements by category + parameter filters. Returns matched elements with optional field projections.", {
  category: z.string().describe("BuiltInCategory name (required)."),
  filters: z.array(z.object({
    parameterName: z.string(),
    operator: z.enum(["equals", "eq", "not_equals", "neq", "contains", "greater", "gt", "less", "lt", "greater_equal", "gte", "less_equal", "lte"]).optional(),
    value: z.union([z.string(), z.number(), z.boolean()]),
  })).optional().describe("Parameter filters."),
  fields: z.array(z.string()).optional().describe("Parameter names to project in results."),
  limit: z.number().int().min(1).max(5000).optional(),
}, fwd("find_elements"));

server.tool("revit_get_parameter", "Get one parameter's value from an element.", {
  id: z.number().int(),
  parameterName: z.string(),
}, fwd("get_parameter"));

server.tool("revit_list_levels", "All Levels sorted by elevation.", {}, fwd("list_levels"));
server.tool("revit_list_wall_types", "All WallTypes.", {}, fwd("list_wall_types"));
server.tool("revit_list_floor_types", "All FloorTypes.", {}, fwd("list_floor_types"));
server.tool("revit_list_categories", "Categories actually used in the doc, with instance counts.", {}, fwd("list_categories"));

server.tool("revit_list_families", "List loaded Families, optionally by category.", {
  category: z.string().optional(),
  limit: z.number().int().min(1).max(5000).optional(),
}, fwd("list_families"));

server.tool("revit_list_family_types", "List FamilySymbols (types), optionally by family or category.", {
  familyName: z.string().optional(),
  category: z.string().optional(),
  limit: z.number().int().min(1).max(5000).optional(),
}, fwd("list_family_types"));

server.tool("revit_list_sheets", "All sheets with number, name, viewport count.", {}, fwd("list_sheets"));
server.tool("revit_list_rooms", "All placed rooms with area, number, level.", {}, fwd("list_rooms"));
server.tool("revit_list_materials", "All materials with class, category, color.", {}, fwd("list_materials"));
server.tool("revit_list_phases", "All phases.", {}, fwd("list_phases"));
server.tool("revit_list_view_templates", "All view templates.", {}, fwd("list_view_templates"));

server.tool("revit_get_views", "All non-template views with type, level, scale.", {}, fwd("get_views"));
server.tool("revit_get_active_view", "Current active view info.", {}, fwd("get_active_view"));
server.tool("revit_get_selected_elements", "Elements currently selected by the user in Revit UI.", {}, fwd("get_selected_elements"));
server.tool("revit_get_linked_files", "List Revit link instances.", {}, fwd("get_linked_files"));

server.tool("revit_get_element_geometry", "Bounding box, centroid, volume, surface area, face/solid counts.", {
  id: z.number().int(),
}, fwd("get_element_geometry"));

// ═══════════════════════════════════════════════════════════════════════════
// CREATION — ARCHITECTURE
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_create_wall", "Create a single straight wall. Returns changeSummary for diff review.", {
  start: xyz, end: xyz,
  height: z.number().positive().optional().describe("Default 3.0 m."),
  levelName: z.string().optional(),
  wallTypeName: z.string().optional(),
  structural: z.boolean().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_wall"));

server.tool("revit_create_floor", "Create a Floor from a closed polygonal profile (>=3 points).", {
  profile: z.array(xyz).min(3),
  levelName: z.string().optional(),
  floorTypeName: z.string().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_floor"));

server.tool("revit_create_level", "Create a Level at given elevation.", {
  elevation: z.number(),
  name: z.string().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_level"));

server.tool("revit_create_grid", "Create a straight Grid line.", {
  start: xyz, end: xyz,
  name: z.string().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_grid"));

server.tool("revit_create_room", "Place a Room at a given point.", {
  location: xyz,
  levelName: z.string().optional(),
  name: z.string().optional(),
  number: z.string().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_room"));

server.tool("revit_create_column", "Place a structural column.", {
  location: xyz,
  levelName: z.string().optional(),
  familyName: z.string().optional(),
  familyTypeName: z.string().optional(),
  structural: z.boolean().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_column"));

server.tool("revit_create_beam", "Place a structural beam between two points.", {
  start: xyz, end: xyz,
  levelName: z.string().optional(),
  familyName: z.string().optional(),
  familyTypeName: z.string().optional(),
  structural: z.boolean().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_beam"));

server.tool("revit_create_ceiling", "Create a Ceiling from a closed polygonal profile.", {
  profile: z.array(xyz).min(3),
  levelName: z.string().optional(),
  ceilingTypeName: z.string().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_ceiling"));

server.tool("revit_create_opening_in_wall", "Create a rectangular opening in a wall.", {
  wallId: z.number().int(),
  lower: xyz.describe("Bottom-left corner."),
  upper: xyz.describe("Top-right corner."),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_opening_in_wall"));

server.tool("revit_place_family_instance", "Place a FamilyInstance at a point (non-hosted). When selection is ambiguous (no familyName/familyTypeName given) returns a candidate list with placed:false — pick from the list and retry with both fields.", {
  location: xyz,
  familyName: z.string().optional(),
  familyTypeName: z.string().optional(),
  category: z.string().optional().describe("BuiltInCategory to narrow the search."),
  levelName: z.string().optional(),
  structural: z.boolean().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("place_family_instance"));

// ═══════════════════════════════════════════════════════════════════════════
// CREATION — DOCUMENTATION
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_create_sheet", "Create a ViewSheet.", {
  sheetNumber: z.string().optional(),
  sheetName: z.string().optional(),
  titleBlockName: z.string().optional(),
  dryRun: dryRunField,
}, fwdWrite("create_sheet"));

server.tool("revit_place_view_on_sheet", "Place a view on a sheet (Viewport).", {
  sheetId: z.number().int(),
  viewId: z.number().int(),
  location: xyz.optional().describe("Center point on sheet. Defaults to roughly center."),
  dryRun: dryRunField,
}, fwdWrite("place_view_on_sheet"));

server.tool("revit_create_floor_plan_view", "Create a floor plan view for a level.", {
  levelName: z.string(),
  viewName: z.string().optional(),
  dryRun: dryRunField,
}, fwdWrite("create_floor_plan_view"));

server.tool("revit_create_section_view", "Create a section view.", {
  origin: xyz, direction: xyz,
  depth: z.number().optional().describe("Cut depth, default 10 m."),
  width: z.number().optional().describe("Half-width, default 10 m."),
  height: z.number().optional().describe("Half-height, default 5 m."),
  viewName: z.string().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_section_view"));

server.tool("revit_create_3d_view", "Create an isometric 3D view.", {
  viewName: z.string().optional(),
  dryRun: dryRunField,
}, fwdWrite("create_3d_view"));

server.tool("revit_create_schedule", "Create a ViewSchedule for a category, optionally adding field columns.", {
  category: z.string().describe("BuiltInCategory, e.g. 'OST_Walls'."),
  name: z.string().optional(),
  fields: z.array(z.string()).optional().describe("Parameter names to add as schedule columns."),
  dryRun: dryRunField,
}, fwdWrite("create_schedule"));

server.tool("revit_tag_element", "Place a tag on an element in the active view.", {
  elementId: z.number().int(),
  location: xyz.optional(),
  addLeader: z.boolean().optional(),
  viewId: z.number().int().optional(),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("tag_element"));

server.tool("revit_create_text_note", "Create a TextNote in a view.", {
  text: z.string(),
  location: xyz,
  viewId: z.number().int().optional(),
  width: z.number().optional().describe("Text wrap width, default 0.5 feet."),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_text_note"));

// ═══════════════════════════════════════════════════════════════════════════
// EDIT — PARAMETERS
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_set_parameter", "Set a parameter on an element. Returns before/after diff. For numeric (Double) parameters with measurable units pass units:'meters' or 'feet' for automatic conversion; default 'internal' means raw Revit feet.", {
  id: z.number().int(),
  parameterName: z.string(),
  value: z.union([z.string(), z.number(), z.boolean(), z.object({ id: z.number().int() })]),
  units: z.enum(["meters", "feet", "internal"]).optional().describe("Unit for numeric double parameters. 'meters' or 'feet' triggers UnitUtils conversion. Default 'internal' (raw Revit feet)."),
  dryRun: dryRunField,
}, fwdWrite("set_parameter"));

server.tool("revit_set_parameter_batch", "Set the same parameter on multiple elements. Returns changeSummary + partialFailure flag. Set atomic:true for all-or-nothing (any failure rolls back the whole call); default is best-effort. Pass units:'meters'/'feet' for automatic double-parameter conversion.", {
  ids: idsField,
  parameterName: z.string(),
  value: z.union([z.string(), z.number(), z.boolean(), z.object({ id: z.number().int() })]),
  units: z.enum(["meters", "feet", "internal"]).optional().describe("Unit for numeric double parameters. Default 'internal' (raw Revit feet)."),
  atomic: z.boolean().optional().describe("All-or-nothing. If true, any element failure rolls back the entire batch. Default false (best-effort)."),
  dryRun: dryRunField,
}, fwdWrite("set_parameter_batch"));

server.tool("revit_rename_element", "Rename an element — supports Family, FamilySymbol (type name), and any other element. For Family/FamilySymbol uses direct property setter; validates system families, illegal chars, and name collisions. Returns before/after diff with instancesAffected count.", {
  id: z.number().int(),
  name: z.string(),
  dryRun: dryRunField,
}, fwdWrite("rename_element"));

// ═══════════════════════════════════════════════════════════════════════════
// EDIT — TRANSFORM
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_move_element", "Translate an element by a vector. Returns before/after position.", {
  id: z.number().int(),
  translation: xyz,
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("move_element"));

server.tool("revit_rotate_element", "Rotate an element around a vertical axis.", {
  id: z.number().int(),
  center: xyz,
  angleDeg: z.number().describe("Rotation in degrees, counter-clockwise."),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("rotate_element"));

server.tool("revit_copy_element", "Copy elements by a translation vector.", {
  ids: idsField,
  translation: xyz,
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("copy_element"));

server.tool("revit_mirror_element", "Mirror elements across a plane.", {
  ids: idsField,
  origin: xyz,
  normal: xyz.describe("Normal of the mirror plane."),
  copy: z.boolean().optional().describe("Default true = copy+mirror; false = move."),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("mirror_element"));

server.tool("revit_array_linear", "Copy elements N times along a vector (linear array).", {
  ids: idsField,
  count: z.number().int().min(1),
  spacing: xyz.describe("Translation per copy."),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("array_linear"));

server.tool("revit_delete_elements", "Delete elements by id. Returns changeSummary.", {
  ids: idsField,
  dryRun: dryRunField,
}, fwdWrite("delete_elements"));

// ═══════════════════════════════════════════════════════════════════════════
// EDIT — GROUPING
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_group_elements", "Group elements together.", {
  ids: idsField,
  name: z.string().optional().describe("Group type name."),
  dryRun: dryRunField,
}, fwdWrite("group_elements"));

server.tool("revit_ungroup_elements", "Ungroup a Group, returning its member ids.", {
  groupId: z.number().int(),
  dryRun: dryRunField,
}, fwdWrite("ungroup_elements"));

// ═══════════════════════════════════════════════════════════════════════════
// VIEW MANIPULATION
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_open_view", "Switch the active view.", {
  viewId: z.number().int(),
  dryRun: dryRunField,
}, fwdWrite("open_view"));

server.tool("revit_set_view_detail_level", "Set detail level: Coarse, Medium, or Fine.", {
  viewId: z.number().int().optional(),
  detailLevel: z.enum(["Coarse", "Medium", "Fine"]),
  dryRun: dryRunField,
}, fwdWrite("set_view_detail_level"));

server.tool("revit_hide_elements_in_view", "Hide elements in a view.", {
  ids: idsField,
  viewId: z.number().int().optional(),
  dryRun: dryRunField,
}, fwdWrite("hide_elements_in_view"));

server.tool("revit_unhide_elements_in_view", "Unhide elements in a view.", {
  ids: idsField,
  viewId: z.number().int().optional(),
  dryRun: dryRunField,
}, fwdWrite("unhide_elements_in_view"));

server.tool("revit_select_elements", "Set the selection in Revit UI.", {
  ids: idsField,
}, fwdWrite("select_elements"));

server.tool("revit_zoom_to_elements", "Zoom/pan the active view to show elements.", {
  ids: idsField,
}, fwdWrite("zoom_to_elements"));

server.tool("revit_apply_view_filter", "Create a parameter filter rule and apply to a view with optional color override.", {
  viewId: z.number().int().optional(),
  filterName: z.string(),
  category: z.string(),
  parameterName: z.string(),
  value: z.string().describe("Equality match value."),
  colorRGB: z.object({ r: z.number().int(), g: z.number().int(), b: z.number().int() }).optional(),
  visible: z.boolean().optional().describe("False = hide matching elements."),
  dryRun: dryRunField,
}, fwdWrite("apply_view_filter"));

server.tool("revit_color_override_by_param", "Color-code elements in a view by a parameter value.", {
  viewId: z.number().int().optional(),
  category: z.string(),
  parameterName: z.string(),
  colorMap: z.record(z.object({ r: z.number().int(), g: z.number().int(), b: z.number().int() }))
    .describe('Map of parameter value → RGB color. E.g. {"Fire Rated": {r:255,g:0,b:0}}'),
  dryRun: dryRunField,
}, fwdWrite("color_override_by_param"));

// ═══════════════════════════════════════════════════════════════════════════
// BATCH
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_batch",
  "Run multiple commands inside ONE Revit Transaction. All steps = one undo entry. Each step: { command, params }. Supports dryRun for preview.",
  {
    stopOnError: z.boolean().optional().describe("Default true: rollback on first failure."),
    dryRun: dryRunField,
    steps: z.array(z.object({
      command: z.string(),
      params: z.record(z.unknown()).optional(),
    })).min(1),
  },
  async (params) => {
    const steps = params.steps.map((s) => ({
      command: s.command,
      params: (s.params ?? {}) as Record<string, unknown>,
    }));
    return envelopeToToolResult(
      await callRevitBatch(steps, params.stopOnError ?? true, params.dryRun === true),
    );
  },
);

// ═══════════════════════════════════════════════════════════════════════════
// BOOT
// ═══════════════════════════════════════════════════════════════════════════

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error(`[revit-mcp-server] v0.5.0 connected to Revit addin at ${REVIT_BASE_URL}`);

  // Startup connectivity probe — log diagnostics but never crash.
  const health = await checkRevitHealth();
  if (!health.reachable) {
    console.error(
      `[revit-mcp-server] WARNING: Revit addin not reachable at ${REVIT_BASE_URL}. ` +
      "Ensure Revit is running with RevitMCPAddin loaded.",
    );
  } else if (health.authEnabled && !health.authTokenPresent) {
    console.error(
      "[revit-mcp-server] WARNING: Revit addin requires auth but no token was found. " +
      "Set REVIT_MCP_AUTH_TOKEN or ensure the token file is readable at " +
      `%APPDATA%/Autodesk/Revit/Addins/${process.env.REVIT_MCP_VERSION ?? "2026"}/revit-mcp-token.txt`,
    );
  } else {
    console.error(
      `[revit-mcp-server] Revit addin reachable (v${health.version ?? "?"}, ` +
      `auth: ${health.authEnabled ? "enabled" : "disabled"})`,
    );
  }
}

main().catch((err) => {
  console.error("[revit-mcp-server] fatal:", err);
  process.exit(1);
});
