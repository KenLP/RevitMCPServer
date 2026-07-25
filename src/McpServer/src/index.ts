#!/usr/bin/env node
/**
 * Revit MCP Server v0.8.20 (stdio).
 *
 * 89 tools covering diagnostics, inspection, creation, editing, family,
 * transform, view manipulation, annotation, model health, batch operations, and coordination/clash detection.
 *
 * v0.8.0 additions:
 *   - change_element_type: swap wall/floor/family type of any element.
 *   - apply_view_template: apply or remove a view template from a view.
 *   - copy_parameters: copy parameter values from one element to many targets.
 *   - configure_schedule: add filters, sort/group fields, export to CSV.
 *   - set_level_elevation: change level elevation (meters / feet / mm).
 *   - export_view_pdf: export a view or sheet to PDF.
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
import { modelHealthTriage, clashReview } from "./recipes.js";

const server = new McpServer({ name: "revit-mcp-server", version: "0.8.20" });

// ── Common schemas ──────────────────────────────────────────────────────────
const xyz = z.object({ x: z.number(), y: z.number(), z: z.number().optional() });
const unitsField = z.enum(["meters", "feet"]).optional().describe("Units. Default 'meters'.");
const idsField = z.array(z.number().int()).min(1).describe("Array of ElementId values.");
const dryRunField = z.boolean().optional().describe("Preview mode: execute but rollback — model is not modified. Default false.");
const colorChannel = z.number().int().min(0).max(255);
const rgbSchema = z.object({ r: colorChannel, g: colorChannel, b: colorChannel });

// Helper: forward a single command (read-only, no dryRun)
const fwd = (cmd: string) => async (params: Record<string, unknown>) =>
  envelopeToToolResult(await callRevit(cmd, params));

// Helper: forward a write command (supports dryRun)
const fwdWrite = (cmd: string) => async (params: Record<string, unknown>) => {
  const { dryRun, ...rest } = params;
  return envelopeToToolResult(await callRevit(cmd, rest, dryRun === true));
};

// ── Tool profiles (P2-A) ──────────────────────────────────────────────────────
// With 89 tools, loading the whole catalog into every conversation wastes tokens
// and hurts tool-selection accuracy. Set REVIT_MCP_PROFILE to a comma-separated
// list (e.g. "documentation,view") to expose only those groups; "core" is always
// included. Unset = all tools (default, fully backward compatible).
const PROFILES: Record<string, string[]> = {
  core: [
    "revit_ping", "revit_get_version", "revit_get_document_info",
    "revit_find_elements", "revit_get_element_info", "revit_get_parameter",
    "revit_batch",
  ],
  inspection: [
    "revit_list_elements", "revit_list_levels", "revit_list_wall_types",
    "revit_list_floor_types", "revit_list_categories", "revit_list_families",
    "revit_list_family_types", "revit_list_sheets", "revit_list_rooms",
    "revit_list_spaces", "revit_list_materials", "revit_list_phases",
    "revit_list_view_templates", "revit_get_views", "revit_get_active_view",
    "revit_get_selected_elements", "revit_get_linked_files",
    "revit_get_linked_elements", "revit_get_element_geometry",
    "revit_get_view_image", "revit_get_element_rooms", "revit_get_schedule_data",
    "revit_get_doors",
  ],
  "model-health": ["revit_get_model_health", "revit_get_worksets"],
  recipes: ["revit_recipe_model_health_triage", "revit_recipe_clash_review"],
  coordination: ["revit_check_clearance"],
  architecture: [
    "revit_create_wall", "revit_create_floor", "revit_create_level",
    "revit_create_grid", "revit_create_room", "revit_create_column",
    "revit_create_beam", "revit_create_ceiling", "revit_create_opening_in_wall",
    "revit_place_family_instance",
  ],
  documentation: [
    "revit_create_sheet", "revit_place_view_on_sheet",
    "revit_create_floor_plan_view", "revit_create_section_view",
    "revit_create_3d_view", "revit_create_schedule", "revit_configure_schedule",
    "revit_tag_element", "revit_tag_all_in_view", "revit_get_tags_in_view",
    "revit_create_text_note", "revit_export_view_pdf", "revit_duplicate_view",
    "revit_create_aligned_dimension", "revit_create_detail_line", "revit_create_filled_region",
  ],
  editing: [
    "revit_set_parameter", "revit_set_parameter_batch", "revit_rename_element",
    "revit_change_element_type", "revit_apply_view_template",
    "revit_copy_parameters", "revit_set_level_elevation", "revit_move_element",
    "revit_rotate_element", "revit_copy_element", "revit_mirror_element",
    "revit_array_linear", "revit_delete_elements", "revit_group_elements",
    "revit_ungroup_elements", "revit_load_family", "revit_duplicate_family_type",
  ],
  view: [
    "revit_open_view", "revit_set_view_detail_level",
    "revit_hide_elements_in_view", "revit_unhide_elements_in_view",
    "revit_isolate_elements_in_view", "revit_set_section_box",
    "revit_select_elements", "revit_zoom_to_elements", "revit_apply_view_filter",
    "revit_color_override_by_param", "revit_override_element_graphics",
  ],
};

const TOOL_PROFILE: Record<string, string> = {};
for (const [profile, names] of Object.entries(PROFILES))
  for (const name of names) TOOL_PROFILE[name] = profile;

function resolveEnabledProfiles(): Set<string> | null {
  const raw = process.env.REVIT_MCP_PROFILE?.trim();
  if (!raw) return null; // null = expose everything (default)
  const set = new Set(raw.split(",").map((s) => s.trim().toLowerCase()).filter(Boolean));
  set.add("core"); // core tools are always available
  for (const p of set)
    if (p !== "core" && !(p in PROFILES))
      console.error(`[revit-mcp-server] WARNING: unknown profile "${p}" in REVIT_MCP_PROFILE`);
  return set;
}

const ENABLED_PROFILES = resolveEnabledProfiles();
let toolsRegistered = 0;
let toolsSkipped = 0;

// Gate server.tool() by profile without touching the registration call sites.
const _registerTool = server.tool.bind(server);
(server as unknown as { tool: (...a: unknown[]) => unknown }).tool = (
  ...args: unknown[]
) => {
  const name = args[0] as string;
  const profile = TOOL_PROFILE[name] ?? "core";
  if (ENABLED_PROFILES === null || ENABLED_PROFILES.has(profile)) {
    toolsRegistered++;
    return (_registerTool as (...a: unknown[]) => unknown)(...args);
  }
  toolsSkipped++;
  return undefined;
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

server.tool("revit_list_elements",
  "List elements filtered by BuiltInCategory. Paginated: returns total, hasMore, nextOffset — " +
  "page through large sets with offset (no 5000 ceiling).", {
  category: z.string().optional().describe("BuiltInCategory, e.g. 'OST_Walls'."),
  onlyInstances: z.boolean().optional(),
  limit: z.number().int().min(1).max(5000).optional().describe("Page size. Default 200."),
  offset: z.number().int().min(0).optional().describe("Page start index. Default 0. Use nextOffset from the previous page."),
}, fwd("list_elements"));

server.tool("revit_get_element_info", "Full parameters + bbox of one element.", {
  id: z.number().int(),
}, fwd("get_element_info"));

server.tool("revit_find_elements",
  "Query elements by category + parameter filters. Returns matched elements with optional field " +
  "projections. Paginated: total reflects all matches after filters; page through with offset (no 5000 ceiling).", {
  category: z.string().describe("BuiltInCategory name (required)."),
  view_id: z.number().int().positive().optional()
    .describe("Scope the query to elements visible in this view (non-template View id). Omit for the whole document."),
  filters: z.array(z.object({
    parameterName: z.string(),
    operator: z.enum(["equals", "eq", "not_equals", "neq", "contains", "greater", "gt", "less", "lt", "greater_equal", "gte", "less_equal", "lte"]).optional(),
    value: z.union([z.string(), z.number(), z.boolean()]),
  })).optional().describe("Parameter filters."),
  fields: z.array(z.string()).optional().describe("Parameter names to project in results."),
  limit: z.number().int().min(1).max(5000).optional().describe("Page size. Default 200."),
  offset: z.number().int().min(0).optional().describe("Page start index. Default 0. Use nextOffset from the previous page."),
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
server.tool("revit_list_spaces",
  "All placed MEP Spaces (OST_MEPSpaces) with area, volume, number, level. " +
  "Use levelId (from revit_list_levels) to filter to a single level.",
  {
    levelId: z.number().int().optional().describe("ElementId of a Level — return only spaces on that level."),
    limit: z.number().int().min(1).max(2000).optional().describe("Max spaces to return. Default 500."),
  },
  fwd("list_spaces"));
server.tool("revit_list_materials", "All materials with class, category, color.", {}, fwd("list_materials"));
server.tool("revit_list_phases", "All phases.", {}, fwd("list_phases"));
server.tool("revit_list_view_templates", "All view templates.", {}, fwd("list_view_templates"));

server.tool("revit_get_views", "All non-template views with type, level, scale.", {}, fwd("get_views"));

server.tool("revit_get_model_health",
  "One-shot model health report (read-only). Aggregates the metrics a BIM manager checks to judge model quality: " +
  "warnings (count + top groups), file size, imported vs linked CAD, in-place families, model/detail groups, " +
  "views not on sheets, element counts by category, and complexity counts (levels, grids, design options, reference planes). " +
  "Returns a scorecard with a letter grade (A-F), a 0-100 score, and flagged issues. " +
  "Use this for a quick 'is this model healthy?' overview before deeper work.",
  {
    deep: z.boolean().optional().describe("Also run the purge scan (Document.GetUnusedElements). Slower on large models. Default false."),
    topN: z.number().int().min(1).max(50).optional().describe("How many top warning groups / categories to list. Default 10."),
  },
  fwd("get_model_health"));

server.tool("revit_get_worksets",
  "List user worksets with per-workset element counts (read-only). " +
  "Flags empty worksets (no instances) and un-renamed defaults ('Workset1'). " +
  "Returns isWorkshared, count, emptyCount, and per-workset details (name, elementCount, isEmpty, isOpen, owner). " +
  "For a non-workshared model returns isWorkshared=false.",
  {},
  fwd("get_worksets"));
server.tool("revit_get_active_view", "Current active view info.", {}, fwd("get_active_view"));
server.tool("revit_get_selected_elements", "Elements currently selected by the user in Revit UI.", {}, fwd("get_selected_elements"));
server.tool("revit_get_linked_files", "List Revit link instances (metadata only — id, name, load status).", {}, fwd("get_linked_files"));

server.tool("revit_get_linked_elements",
  "Read elements that live INSIDE a specific linked RVT file. Bounding boxes are transformed to host-model coordinates. Use this before check_clearance to inspect what's in a link.",
  {
    linkId: z.number().int().describe("ElementId of the RevitLinkInstance (from revit_get_linked_files)."),
    category: z.string().optional().describe("BuiltInCategory name, e.g. 'OST_DuctCurves'. Omit for all elements."),
    limit: z.number().int().min(1).max(2000).optional().describe("Max elements to return. Default 200."),
  },
  fwd("get_linked_elements"));

server.tool("revit_get_element_geometry", "Bounding box, centroid, volume, surface area, face/solid counts.", {
  id: z.number().int(),
}, fwd("get_element_geometry"));

// ═══════════════════════════════════════════════════════════════════════════
// CREATION — ARCHITECTURE
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_create_wall", "Create a single straight wall. Returns changeSummary for diff review.", {
  start: xyz, end: xyz.describe("End point."),
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
  start: xyz, end: xyz.describe("End point."),
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
  start: xyz, end: xyz.describe("End point."),
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

server.tool("revit_place_family_instance", "Place a FamilyInstance at a point. Pass hostId (wall element id) for doors/windows so Revit uses the hosted overload and auto-cuts the opening. Without hostId the instance is placed non-hosted (no opening cut). When selection is ambiguous returns a candidate list with placed:false.", {
  location: xyz,
  familyName: z.string().optional(),
  familyTypeName: z.string().optional(),
  category: z.string().optional().describe("BuiltInCategory to narrow the search."),
  levelName: z.string().optional(),
  hostId: z.number().int().optional().describe("Element id of the host wall/face. Required for doors and windows to cut the opening."),
  flipFacing: z.boolean().optional().describe("Flip the door/window facing side (which side the door opens toward)."),
  flipHand: z.boolean().optional().describe("Flip the door/window hand side (hinge side)."),
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
  origin: xyz, direction: xyz.describe("Direction vector."),
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
// ANNOTATION — TAGGING / DIMENSIONS
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_tag_all_in_view", "Tag all (untagged) elements of a category in a view — mirrors Revit's 'Tag All Not Tagged'. Returns tagged/skipped/failed counts.", {
  category: z.string().describe("Category display name, e.g. 'Doors', 'Windows', 'Rooms'."),
  viewId: z.number().int().optional().describe("Target view. Defaults to active view."),
  leader: z.boolean().optional().describe("Add leader line to each tag. Default false."),
  skipTagged: z.boolean().optional().describe("Skip elements that already have a tag. Default true."),
  dryRun: dryRunField,
}, fwdWrite("tag_all_in_view"));

server.tool("revit_create_aligned_dimension", "Create an aligned dimension chain between two or more element references in a view. Supports Grids, Walls (centreline or face), ReferencePlanes, columns, beams, and FamilyInstances. Wall side: 'auto'/'centre' = wall centreline (default), 'exterior'/'interior' = outer/inner face, 'core' = core centre. The dimension line must cross all references and they must be visible in the target view.", {
  references: z.array(z.object({
    elementId: z.number().int().describe("ElementId of the grid, wall, column, beam, or other element to dimension to."),
    side: z.enum(["auto", "exterior", "interior", "centre", "core"]).optional()
      .describe("For walls: which face/centreline to use. 'auto'/'centre' = overall centreline. 'exterior'/'interior' = outer/inner face. 'core' = core centre. Ignored for non-wall elements."),
  })).min(2).describe("At least 2 element references to dimension between."),
  line: z.object({
    start: xyz.describe("Start point of the dimension line. Must lie on the opposite side of the references from end."),
    end: xyz.describe("End point of the dimension line. The line must cross all references."),
  }).describe("Position and direction of the dimension witness line. Must be perpendicular to the elements being dimensioned and cross all references."),
  viewId: z.number().int().optional().describe("Target view. Defaults to active view. All references must be visible in this view."),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_aligned_dimension"));

// revit_create_spot_elevation — hidden pending a reliable face-reference approach.
// The ReferenceIntersector raycast against a temporary 3D view returns no face hit
// for floors even at the bbox centre (doc.Regenerate() did not help); the prior
// solid-face approach hit "Spot Dimension does not lie on its reference". The C#
// command stays registered (HTTP-callable) for future work but is off the MCP surface.

// Spatial-QC pack — registered in C# (HTTP-callable via /mcp) but deliberately NOT exposed as MCP
// tools. They are consumed programmatically by AutomatedSpatialQC's Python (inputs like loops/points
// come from other calls), so surfacing them to LLM tool routing would only dilute the tool list.
// revit_spatial_get_room_boundary — hidden (HTTP-only spatial-QC pack; C# spatial_get_room_boundary)
// revit_spatial_clearance_envelope — hidden (HTTP-only spatial-QC pack; C# spatial_clearance_envelope)
// revit_spatial_clearance_envelope_batch — hidden (HTTP-only spatial-QC pack; C# spatial_clearance_envelope_batch)
// revit_spatial_raycast_headroom — hidden (HTTP-only spatial-QC pack; C# spatial_raycast_headroom)

server.tool("revit_get_tags_in_view", "List all IndependentTag elements in a view. Optionally filter by tagged element category.", {
  viewId: z.number().int().optional().describe("Target view. Defaults to active view."),
  category: z.string().optional().describe("Filter by tagged element category name, e.g. 'Doors'."),
}, fwd("get_tags_in_view"));

server.tool("revit_get_schedule_data",
  "Read the rendered cell data of a ViewSchedule (text exactly as shown — calculated fields, units, " +
  "formatting applied). The first row is normally the column headers. Paginated by row: returns totalRows, " +
  "totalColumns, hasMore, nextOffset; page large schedules with offset.", {
  scheduleId: z.number().int().describe("ElementId of the ViewSchedule (from revit_get_views)."),
  limit: z.number().int().min(1).max(1000).optional().describe("Rows per page. Default 100."),
  offset: z.number().int().min(0).optional().describe("First body row to return. Default 0. Use nextOffset from the previous page."),
}, fwd("get_schedule_data"));

server.tool("revit_get_doors",
  "All placed doors with nominal width (m), plan location (world XY, m), level, and door swing geometry: " +
  "facingX/Y (FacingOrientation — the normal / pull-swing side), handX/Y (HandOrientation — along the wall), " +
  "and facingFlipped/handFlipped. Orientation is geometry, not a parameter, so find_elements cannot return it — " +
  "use this for ADA/egress maneuvering-clearance and door-swing checks. " +
  "For door PARAMETERS (FireRating, Mark, type name, custom Width param) use find_elements(category='Doors'); " +
  "for which rooms a door connects use get_element_rooms; for a bare id list use list_elements(category='OST_Doors').",
  {},
  fwd("get_doors"));

server.tool("revit_load_family",
  "Load a family (.rfa) from disk into the project. Returns the family id and its types.", {
  filePath: z.string().describe("Absolute path to a .rfa file."),
  overwrite: z.boolean().optional().describe("Overwrite the family (and its parameter values) if already loaded. Default true."),
}, fwdWrite("load_family"));

server.tool("revit_duplicate_family_type",
  "Duplicate a family type (FamilySymbol) under a new name. Set parameters on the new type afterwards with revit_set_parameter using the returned typeId.", {
  sourceTypeId: z.number().int().describe("ElementId of the FamilySymbol to copy."),
  newName: z.string().describe("Name for the new type (unique within the family)."),
}, fwdWrite("duplicate_family_type"));

server.tool("revit_create_detail_line",
  "Create a view-specific detail line in a 2D view (plan/section/elevation/drafting/detail). Endpoints are projected onto the view plane.", {
  start: xyz.describe("Start point."),
  end: xyz.describe("End point."),
  viewId: z.number().int().optional().describe("Target 2D view. Defaults to active view."),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_detail_line"));

server.tool("revit_create_filled_region",
  "Create a filled region (2D annotation) from a closed boundary in a 2D view. Points are projected onto the view plane and the loop is closed automatically.", {
  boundary: z.array(z.object({ x: z.number(), y: z.number(), z: z.number().optional() })).min(3)
    .describe("Boundary points in order (at least 3)."),
  filledRegionTypeId: z.number().int().optional().describe("FilledRegionType id. Defaults to the first available."),
  viewId: z.number().int().optional().describe("Target 2D view. Defaults to active view."),
  units: unitsField,
  dryRun: dryRunField,
}, fwdWrite("create_filled_region"));

// ═══════════════════════════════════════════════════════════════════════════
// EDIT — PARAMETERS
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_set_parameter", "Set a parameter on an element. Returns before/after diff. For numeric (Double) parameters with measurable units pass the matching unit string for automatic conversion; default 'internal' means raw Revit feet.", {
  id: z.number().int(),
  parameterName: z.string(),
  value: z.union([z.string(), z.number(), z.boolean(), z.object({ id: z.number().int() })]),
  units: z.enum(["meters", "feet", "square_meters", "square_feet", "cubic_meters", "cubic_feet", "internal"]).optional()
    .describe("Unit for numeric double parameters. Length: 'meters'/'feet'. Area: 'square_meters'/'square_feet'. Volume: 'cubic_meters'/'cubic_feet'. Default 'internal' (raw Revit feet)."),
  dryRun: dryRunField,
}, fwdWrite("set_parameter"));

server.tool("revit_set_parameter_batch", "Set the same parameter on multiple elements. Returns changeSummary + partialFailure flag. Set atomic:true for all-or-nothing (any failure rolls back the whole call); default is best-effort.", {
  ids: idsField,
  parameterName: z.string(),
  value: z.union([z.string(), z.number(), z.boolean(), z.object({ id: z.number().int() })]),
  units: z.enum(["meters", "feet", "square_meters", "square_feet", "cubic_meters", "cubic_feet", "internal"]).optional()
    .describe("Unit for numeric double parameters. Length: 'meters'/'feet'. Area: 'square_meters'/'square_feet'. Volume: 'cubic_meters'/'cubic_feet'. Default 'internal'."),
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

server.tool("revit_isolate_elements_in_view",
  "Temporarily isolate HOST elements in a view (cyan 'Isolate Element' mode). Pass reset=true to clear. " +
  "NOTE: host-document elements only — cannot keep individual linked-file elements (isolating host hides the whole link). " +
  "For a region spanning host + linked geometry, use revit_set_section_box instead.",
  {
    ids: idsField.optional(),
    viewId: z.number().int().optional(),
    reset: z.boolean().optional().describe("Clear the temporary hide/isolate state instead of isolating. Default false."),
  },
  fwd("isolate_elements_in_view"));

server.tool("revit_duplicate_view",
  "Duplicate a view (preserving its template, filters, and overrides). Returns the new view id. " +
  "Use to make a throwaway inspection copy of an already-filtered view before section-boxing it.",
  {
    viewId: z.number().int().describe("Source view to duplicate."),
    duplicateOption: z.enum(["Duplicate", "WithDetailing", "AsDependent"]).optional().describe("Duplicate mode. Default 'Duplicate'."),
    newName: z.string().optional().describe("Rename the new view."),
    dryRun: dryRunField,
  },
  fwdWrite("duplicate_view"));

server.tool("revit_set_section_box",
  "Set or clear the section box of a 3D view, cropping it to a bounding region. " +
  "Crops ALL geometry including linked files — the reliable way to isolate a clearance region spanning host + linked MEP. " +
  "Provide min/max corners (default units 'feet', matching get_linked_elements bboxes); enable=false deactivates the box.",
  {
    viewId: z.number().int().optional().describe("3D view id. Defaults to active view; must be a View3D."),
    min: z.object({ x: z.number(), y: z.number(), z: z.number() }).optional().describe("Lower corner {x,y,z}. Required unless enable=false."),
    max: z.object({ x: z.number(), y: z.number(), z: z.number() }).optional().describe("Upper corner {x,y,z}. Required unless enable=false."),
    units: z.enum(["feet", "mm", "meters"]).optional().describe("Units of min/max. Default 'feet'."),
    paddingMm: z.number().optional().describe("Expand the box outward on all sides, in mm. Default 0."),
    enable: z.boolean().optional().describe("false deactivates the section box. Default true."),
    dryRun: dryRunField,
  },
  fwdWrite("set_section_box"));

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
  colorRGB: rgbSchema.optional(),
  visible: z.boolean().optional().describe("False = hide matching elements."),
  reuseExisting: z.boolean().optional().describe("Reuse filter if a filter with this name already exists. Default false."),
  dryRun: dryRunField,
}, fwdWrite("apply_view_filter"));

server.tool("revit_color_override_by_param", "Color-code elements in a view by a parameter value.", {
  viewId: z.number().int().optional(),
  category: z.string(),
  parameterName: z.string(),
  colorMap: z.record(rgbSchema)
    .describe('Map of parameter value → RGB color. E.g. {"Fire Rated": {r:255,g:0,b:0}}'),
  dryRun: dryRunField,
}, fwdWrite("color_override_by_param"));

// ═══════════════════════════════════════════════════════════════════════════
// COORDINATION / CLASH DETECTION
// ═══════════════════════════════════════════════════════════════════════════

const elementSetSchema = z.object({
  source: z.enum(["host", "link"]).default("host").describe("'host' = current model, 'link' = a linked RVT file."),
  linkId: z.number().int().optional().describe("Required when source='link'. ElementId of the RevitLinkInstance."),
  categories: z.array(z.string()).optional().describe("BuiltInCategory names to include, e.g. ['OST_DuctCurves', 'OST_PipeCurves']. Empty = all element types."),
  limit: z.number().int().min(1).max(2000).optional().describe("Max elements to load from this set. Default 500."),
  scopeId: z.number().int().optional().describe("ElementId of any spatial container — MEP Space (revit_list_spaces), Room (revit_list_rooms), Floor, equipment, or any element with a bounding box. Only elements whose centroid falls inside that element's bbox are included. Omit to check all elements of the category in the model."),
});
server.tool("revit_override_element_graphics",
  "Apply per-element color and fill overrides to specific elements in a view. " +
  "Use to highlight violations (e.g. red for clashing furniture, gray for doors). " +
  "Pass reset=true to clear overrides. Works on any element type in any view.",
  {
    viewId: z.number().int().describe("ElementId of the view to override in."),
    elementIds: z.array(z.number().int()).min(1).describe("Element IDs to override."),
    color: z.object({ r: z.number().int().min(0).max(255), g: z.number().int().min(0).max(255), b: z.number().int().min(0).max(255) }).optional().describe("RGB color. Default red (255,0,0)."),
    transparency: z.number().int().min(0).max(100).optional().describe("Surface transparency 0–100%. Default 0."),
    reset: z.boolean().optional().describe("If true, clear all overrides for the given elements. Default false."),
  },
  fwdWrite("override_element_graphics"));

// Separate object reference for setB to prevent MCP SDK JSON Schema deduplication (setB would render as {} otherwise).
const elementSetSchemaB = elementSetSchema.extend({});

server.tool("revit_check_clearance",
  "Detect hard clashes or clearance violations between two element sets (host or linked files). " +
  "axis='bbox' (default): inflates setA bounding boxes by clearanceMm in all directions — use for omnidirectional proximity checks (e.g. 1m clear zone around a door). " +
  "axis='Z': fires a vertical ReferenceIntersector raycast from each setA element and automatically finds the nearest setB element directly below or above it; " +
  "reports the actual measured clearance — use direction='below' (duct→floor below) or 'above' (duct→ceiling above). " +
  "IMPORTANT for axis='Z': do NOT manually fetch or enumerate setB elements first — just pass a category filter in setB and let the raycast locate the nearest hit automatically. Fetching all setB elements and comparing manually is unnecessary, slow, and wrong. " +
  "clearanceMm=0 + axis='bbox' → hard clash only. " +
  "Example: setA = HVAC ducts (host), setB = floors in Arch link, axis='Z', direction='below', clearanceMm=2400.",
  {
    setA: elementSetSchema,
    setB: elementSetSchemaB,
    axis: z.enum(["bbox", "Z"]).optional().describe("Clearance axis. 'bbox'=inflate AABB (default). 'Z'=vertical raycast (accurate, requires a 3D view)."),
    direction: z.enum(["below", "above"]).optional().describe("Ray direction for axis=Z. 'below'=cast downward from setA bottom (MEP→floor). 'above'=cast upward from setA top (MEP→ceiling). Default 'below'."),
    viewId: z.number().int().optional().describe("ElementId of a 3D view for axis=Z raycast. Uses active view if omitted."),
    clearanceMm: z.number().min(0).optional().describe("Clearance threshold in mm. Violations reported when measured distance < clearanceMm. Default 0."),
    sampleCount: z.number().int().min(1).max(10).optional().describe("axis=Z only: number of points sampled along each element's centreline (LocationCurve). Default 3 (start/mid/end). Increase to 5 for long sloped elements spanning multiple floor slabs."),
    maxResults: z.number().int().min(1).max(2000).optional().describe("Clash pairs cap. Default 200."),
  },
  fwd("check_clearance"));

server.tool("revit_get_view_image",
  "Export a Revit view to PNG and return it as an image. Omit viewId to capture the active view. " +
  "Useful for visual coordination review — capture a section or 3D view showing potential clashes.",
  {
    viewId: z.number().int().optional().describe("ElementId of the view to export. Omit for active view."),
    dpi: z.number().int().min(36).max(300).optional().describe("Image resolution (snaps to 72/150/300). Default 72."),
  },
  async (params) => {
    const envelope = await callRevit("get_view_image", params);
    if (!envelope.ok) return envelopeToToolResult(envelope);

    const data = envelope.data as Record<string, unknown> | undefined;
    const base64 = data?.imageBase64 as string | undefined;
    if (base64) {
      const meta = { ...data };
      delete meta.imageBase64;
      return {
        content: [
          { type: "image" as const, data: base64, mimeType: "image/png" as const },
          { type: "text" as const, text: JSON.stringify({ ok: true, data: meta }, null, 2) },
        ],
      };
    }
    return envelopeToToolResult(envelope);
  });

// ═══════════════════════════════════════════════════════════════════════════
// EDIT — TYPE / TEMPLATE / PARAMETER COPY
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_change_element_type",
  "Swap the type of an element (e.g. change a wall from 'Generic - 200mm' to 'Generic - 300mm', " +
  "or change a door family symbol). Uses revit_list_family_types / revit_list_wall_types to find valid typeId values. " +
  "Returns old and new type info.",
  {
    id: z.number().int().describe("ElementId of the element to change."),
    typeId: z.number().int().describe("ElementId of the target type (WallType, FloorType, FamilySymbol, …)."),
    dryRun: dryRunField,
  },
  fwdWrite("change_element_type"));

server.tool("revit_apply_view_template",
  "Apply or remove a view template from a view. " +
  "Provide templateId (ElementId) or templateName; omit both (or pass templateId=-1) to remove the current template. " +
  "Use revit_list_view_templates to discover available templates.",
  {
    viewId: z.number().int().describe("ElementId of the view to modify."),
    templateId: z.number().int().optional().describe("ElementId of the view template. Pass -1 to remove."),
    templateName: z.string().optional().describe("Template name (case-insensitive). Alternative to templateId."),
    dryRun: dryRunField,
  },
  fwdWrite("apply_view_template"));

server.tool("revit_copy_parameters",
  "Copy parameter values from a source element to one or more target elements. " +
  "Only writable parameters with matching storage types on both elements are copied. " +
  "Useful for stamping Mark, Comments, or custom instance parameters across many elements at once.",
  {
    sourceId: z.number().int().describe("ElementId of the source element."),
    targetIds: idsField.describe("ElementIds of target elements."),
    parameterNames: z.array(z.string()).optional()
      .describe("Parameter names to copy. Omit to copy all writable parameters found on both elements."),
    dryRun: dryRunField,
  },
  fwdWrite("copy_parameters"));

// ═══════════════════════════════════════════════════════════════════════════
// ROOM CONTAINMENT
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_get_element_rooms",
  "Get room containment for one or more family instances (doors, windows, furniture, fixtures, etc.). " +
  "Returns room / fromRoom / toRoom — each { id, name, number } or null. " +
  "Uses the Revit API's phase-dependent FamilyInstance.get_Room/get_FromRoom/get_ToRoom, " +
  "NOT centroid-in-bbox — authoritative for wall-hosted elements (doors/windows) whose centroid " +
  "lies inside the wall between two rooms. " +
  "fromRoom + toRoom apply to boundary connectors (OST_Doors, OST_Windows). " +
  "room applies to point-located elements (OST_Furniture, OST_PlumbingFixtures, OST_LightingFixtures, etc.). " +
  "Non-family-instance elements return null for all fields. " +
  "Use revit_list_rooms to discover room ids/names in the model.",
  {
    ids: idsField.describe("ElementIds to query. 1–N elements per call."),
  },
  fwd("get_element_rooms"));

// ═══════════════════════════════════════════════════════════════════════════
// EDIT — SCHEDULE / LEVEL / PDF EXPORT
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_configure_schedule",
  "Add filters, sort fields, and grouping to an existing ViewSchedule, and optionally export it to CSV. " +
  "Use revit_create_schedule to create the schedule first, then call this to configure it. " +
  "Use revit_list_sheets or revit_get_views to find schedule ids.",
  {
    scheduleId: z.number().int().describe("ElementId of the ViewSchedule."),
    clearFilters: z.boolean().optional().describe("Remove all existing filters before adding new ones. Default false."),
    clearSortFields: z.boolean().optional().describe("Remove all existing sort/group fields before adding new ones. Default false."),
    filters: z.array(z.object({
      field: z.string().describe("Schedule field name (e.g. 'Area', 'Level', 'Mark')."),
      operator: z.enum([
        "equals", "not_equals", "greater", "greater_equal", "less", "less_equal",
        "contains", "not_contains", "begins_with", "ends_with", "has_value", "has_no_value",
      ]).optional().describe("Filter operator. Default 'equals'."),
      value: z.string().optional().describe("Filter value (not used for has_value / has_no_value)."),
    })).optional().describe("Filters to add to the schedule."),
    sortFields: z.array(z.object({
      field: z.string().describe("Schedule field name to sort/group by."),
      ascending: z.boolean().optional().describe("Sort ascending. Default true."),
      groupBy: z.boolean().optional().describe("Add a group header row for each distinct value. Default false."),
    })).optional().describe("Sort and group fields to add."),
    exportCsv: z.boolean().optional().describe("Export the schedule to CSV and include the content in the response."),
    dryRun: dryRunField,
  },
  fwdWrite("configure_schedule"));

server.tool("revit_set_level_elevation",
  "Change the elevation of a Level element. All hosted elements (floors, ceilings, MEP) move with it. " +
  "Use revit_list_levels to find level ids.",
  {
    id: z.number().int().describe("ElementId of the Level."),
    elevation: z.number().describe("New elevation value."),
    units: z.enum(["meters", "feet", "mm", "internal"]).optional()
      .describe("Units for the elevation value. Default 'meters'. 'internal' = Revit internal feet."),
    dryRun: dryRunField,
  },
  fwdWrite("set_level_elevation"));

server.tool("revit_export_view_pdf",
  "Export a view or sheet to a PDF file on disk. " +
  "Returns the output file path and size. Use revit_get_views or revit_list_sheets to find view ids.",
  {
    viewId: z.number().int().optional().describe("ElementId of the view or sheet. Defaults to the active view."),
    outputFolder: z.string().optional().describe("Folder path for the PDF. Defaults to Documents folder."),
    fileName: z.string().optional().describe("File name without extension. Defaults to view name + timestamp."),
    rasterQuality: z.enum(["Low", "Medium", "High", "Presentation"]).optional()
      .describe("Raster image quality. Default 'Medium'."),
    colorMode: z.enum(["Color", "Grayscale", "BlackLine"]).optional()
      .describe("Color mode. Default 'Color'."),
  },
  fwd("export_view_pdf"));

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
// WORKFLOW RECIPES (P4) — orchestration over verified kernel commands
// ═══════════════════════════════════════════════════════════════════════════

server.tool("revit_recipe_model_health_triage",
  "WORKFLOW RECIPE (read-only): run a model-health scan and return a prioritized, " +
  "actionable triage list — each issue with its severity and a recommended fix. " +
  "Composes get_model_health; use this instead of get_model_health when you want guidance, not raw metrics.",
  { deep: z.boolean().optional().describe("Include the purge scan (slower). Default false.") },
  async (params) => envelopeToToolResult(await modelHealthTriage(callRevit, params.deep === true)));

const clashPairSchema = z.object({
  label: z.string().optional().describe("Human label for this pair, e.g. 'Ducts × Struct link'."),
  setA: elementSetSchema,
  setB: elementSetSchemaB,
  axis: z.enum(["bbox", "Z"]).optional(),
  direction: z.enum(["below", "above"]).optional(),
  clearanceMm: z.number().min(0).optional(),
  viewId: z.number().int().optional(),
  sampleCount: z.number().int().min(1).max(10).optional(),
  maxResults: z.number().int().min(1).max(2000).optional(),
});

server.tool("revit_recipe_clash_review",
  "WORKFLOW RECIPE (read-only): run a coordination clash sweep across multiple element-set pairs " +
  "(host vs host, or host vs LINKED RVT) and return a consolidated, prioritized clash report — " +
  "hard clashes first, then clearance violations by smallest gap, counted per pair. " +
  "Each pair is a check_clearance input; for links set setB.source='link' + linkId (from revit_get_linked_files). " +
  "Composes check_clearance.",
  { pairs: z.array(clashPairSchema).min(1).describe("Coordination matrix: each entry is a labelled check_clearance pair.") },
  async (params) => envelopeToToolResult(await clashReview(callRevit, params.pairs as never)));

// ═══════════════════════════════════════════════════════════════════════════
// BOOT
// ═══════════════════════════════════════════════════════════════════════════

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error(`[revit-mcp-server] v0.8.20 connected to Revit addin at ${REVIT_BASE_URL}`);
  if (ENABLED_PROFILES !== null)
    console.error(
      `[revit-mcp-server] profiles: ${[...ENABLED_PROFILES].sort().join(", ")} ` +
      `— ${toolsRegistered} tools exposed, ${toolsSkipped} hidden`,
    );

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
