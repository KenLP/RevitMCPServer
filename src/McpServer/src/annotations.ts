/**
 * MCP tool annotations: a title and the hints a client uses to decide whether a call needs
 * confirmation. They describe the effect on the user's model and files, not on Revit's UI.
 *
 *   readOnlyHint    true  — reads only; the model and files are left as they were
 *   destructiveHint true  — can change or remove what is already there (edit, delete, overwrite)
 *                   false — only adds (a new wall, view, sheet ...) or changes nothing persistent
 *   idempotentHint  true  — repeating the same call has no further effect
 *   openWorldHint   false — every tool talks to the local Revit add-in only
 *
 * A tool not listed in READ_ONLY or NON_DESTRUCTIVE is treated as destructive, so a new tool that
 * nobody classified errs on the side of asking. __tests__/annotations.test.ts checks every name
 * here against the tools index.ts registers and against the C# commands' IsReadOnly.
 */

export interface ToolAnnotations {
  [key: string]: unknown;
  title: string;
  readOnlyHint: boolean;
  destructiveHint?: boolean;
  idempotentHint?: boolean;
  openWorldHint: boolean;
}

/** Tools that change neither the model nor any file. */
export const READ_ONLY = new Set<string>([
  "revit_ping", "revit_get_version", "revit_get_document_info",
  "revit_list_elements", "revit_get_element_info", "revit_find_element_by_unique_id",
  "revit_find_elements", "revit_query_where", "revit_get_parameter",
  "revit_list_levels", "revit_list_wall_types", "revit_list_floor_types", "revit_list_categories",
  "revit_list_families", "revit_list_family_types", "revit_list_sheets", "revit_list_rooms",
  "revit_list_spaces", "revit_list_materials", "revit_list_phases", "revit_list_view_templates",
  "revit_get_views", "revit_get_model_health", "revit_get_worksets", "revit_get_active_view",
  "revit_get_selected_elements", "revit_get_linked_files", "revit_get_linked_elements",
  "revit_get_element_geometry", "revit_get_tags_in_view", "revit_get_schedule_data",
  "revit_get_doors", "revit_check_clearance", "revit_get_view_image", "revit_get_element_rooms",
  "revit_recipe_model_health_triage", "revit_recipe_clash_review",
  // UI state only: what is selected, zoomed to or shown. Nothing in the model or on disk changes.
  "revit_select_elements", "revit_zoom_to_elements", "revit_open_view",
]);

/** Writes that only add to the model or open something, never change or remove what exists. */
export const NON_DESTRUCTIVE = new Set<string>([
  "revit_open_document",
  "revit_create_wall", "revit_create_floor", "revit_create_level", "revit_create_grid",
  "revit_create_room", "revit_create_column", "revit_create_beam", "revit_create_ceiling",
  "revit_create_opening_in_wall", "revit_place_family_instance",
  "revit_create_sheet", "revit_place_view_on_sheet", "revit_create_floor_plan_view",
  "revit_create_section_view", "revit_create_3d_view", "revit_create_perspective_view",
  "revit_create_schedule", "revit_tag_element", "revit_tag_all_in_view", "revit_create_text_note",
  "revit_create_aligned_dimension", "revit_create_detail_line", "revit_create_filled_region",
  "revit_duplicate_family_type", "revit_duplicate_view", "revit_copy_element", "revit_array_linear",
  // A temporary isolate in the view, cleared by Revit's "Reset Temporary Hide/Isolate".
  "revit_isolate_elements_in_view",
]);

/** Writes documented as idempotent: the same call again finds the work already done. */
export const IDEMPOTENT_WRITES = new Set<string>([
  "revit_open_document", "revit_close_document", "revit_isolate_elements_in_view",
]);

/** C# commands marked IsReadOnly whose tool still writes a file the caller names. */
export const FILE_WRITERS_ON_READ_COMMANDS = new Set<string>(["revit_export_view_pdf"]);

/** C# commands that are not read-only but change only UI state (see READ_ONLY). */
export const UI_STATE_ONLY = new Set<string>([
  "revit_select_elements", "revit_zoom_to_elements", "revit_open_view",
]);

const WORDS: Record<string, string> = {
  "3d": "3D", ifc: "IFC", pdf: "PDF", id: "ID",
};

/** "revit_create_3d_view" → "Create 3D view"; recipes read "Recipe: …". */
export function toolTitle(name: string): string {
  const parts = name.replace(/^revit_/, "").split("_");
  const recipe = parts[0] === "recipe";
  const words = (recipe ? parts.slice(1) : parts).map((w) => WORDS[w] ?? w);
  const text = words.join(" ");
  const sentence = text.charAt(0).toUpperCase() + text.slice(1);
  return recipe ? `Recipe: ${sentence.charAt(0).toLowerCase()}${sentence.slice(1)}` : sentence;
}

export function annotationsFor(name: string): ToolAnnotations {
  const title = toolTitle(name);
  if (READ_ONLY.has(name))
    return { title, readOnlyHint: true, openWorldHint: false };
  return {
    title,
    readOnlyHint: false,
    destructiveHint: !NON_DESTRUCTIVE.has(name),
    idempotentHint: IDEMPOTENT_WRITES.has(name),
    openWorldHint: false,
  };
}
