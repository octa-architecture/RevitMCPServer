/**
 * OCTA detailing tools: fills, detail components, text leaders, graphics
 * (instead of hiding), review flags and internal notes. Each forwards to the
 * matching command in RevitMCP.Core/Commands/Octa.
 */

import { z } from "zod";

type Handler = (params: Record<string, unknown>) => Promise<unknown>;
type ToolFn = (name: string, description: string, shape: Record<string, z.ZodTypeAny>, handler: Handler) => unknown;

const pt = z.object({ x: z.number(), y: z.number(), z: z.number().optional() });
const rgb = z.object({ r: z.number().int().min(0).max(255), g: z.number().int().min(0).max(255), b: z.number().int().min(0).max(255) });
const units = z.enum(["meters", "millimeters", "feet"]).optional()
  .describe("Coordinate units. Default 'meters'. Use 'millimeters' for detail work.");
const ids = z.array(z.number().int()).min(1);
const leader = z.object({
  end: pt.describe("Arrow tip — on the element being described."),
  elbow: pt.optional().describe("Optional elbow point."),
  side: z.enum(["left", "right"]).optional().describe("Which side of the text the leader leaves from. Default: side facing the end point."),
  arc: z.boolean().optional(),
});
const graphics = {
  halftone: z.boolean().optional().describe("Fade (halftone). Prefer this to hiding elements."),
  transparency: z.number().int().min(0).max(100).optional(),
  projectionLineColor: rgb.optional(),
  projectionLineWeight: z.number().int().min(1).max(16).optional(),
  projectionLinePattern: z.string().optional().describe("Line pattern name, or 'solid'."),
  cutLineColor: rgb.optional(),
  cutLineWeight: z.number().int().min(1).max(16).optional(),
  cutLinePattern: z.string().optional(),
  surfacePattern: z.string().optional().describe("Fill pattern name for surfaces in projection, or 'solid'."),
  surfaceColor: rgb.optional(),
  cutPattern: z.string().optional().describe("Fill pattern name for cut faces, or 'solid'."),
  cutColor: rgb.optional(),
};

export function registerOctaRevitTools(
  tool: ToolFn,
  fwd: (cmd: string) => Handler,
  fwdWrite: (cmd: string) => Handler,
  dryRun: z.ZodTypeAny,
): void {
  // ── Fills ────────────────────────────────────────────────────────────────
  tool("revit_list_fill_patterns", "List fill patterns (drafting/model) — names to use for filled region types and overrides.",
    { target: z.enum(["drafting", "model"]).optional(), nameContains: z.string().optional() }, fwd("list_fill_patterns"));

  tool("revit_list_filled_region_types",
    "List filled region types with patterns, colours, masking, line weight and how often each is used. " +
    "Check this (and existing details) before drawing fills — reuse the practice's types.",
    {}, fwd("list_filled_region_types"));

  tool("revit_create_filled_region_type",
    "Create (or update) a filled region type, e.g. a material hatch for details. Duplicates a base type.",
    {
      name: z.string(),
      baseTypeName: z.string().optional(),
      foregroundPattern: z.string().optional().describe("Fill pattern name or 'solid'."),
      foregroundColor: rgb.optional(),
      backgroundPattern: z.string().optional().describe("Fill pattern name, 'solid' or 'none'."),
      backgroundColor: rgb.optional(),
      isMasking: z.boolean().optional(),
      lineWeight: z.number().int().min(1).max(16).optional(),
      updateIfExists: z.boolean().optional(),
      dryRun,
    }, fwdWrite("create_filled_region_type"));

  // ── Detail components ────────────────────────────────────────────────────
  tool("revit_list_detail_components",
    "List loaded detail component families and types with their size parameters (mm), e.g. OCTA-Detail-Timber Member.",
    { familyContains: z.string().optional() }, fwd("list_detail_components"));

  tool("revit_place_detail_component",
    "Place a detail component (timber member, furring channel, packer…) in a detail/drafting view. " +
    "Point-based families take 'location' (+ rotationDegrees); line-based take 'start'/'end'. " +
    "To use a size that doesn't exist yet pass newType {name, params} (e.g. {name:'90x45', params:{Width:45, Depth:90}}, mm). " +
    "Use this instead of drawing members with detail lines.",
    {
      familyName: z.string().optional(),
      typeName: z.string().optional(),
      symbolId: z.number().int().optional(),
      viewId: z.number().int().optional(),
      location: pt.optional(),
      start: pt.optional(),
      end: pt.optional(),
      rotationDegrees: z.number().optional(),
      newType: z.object({ name: z.string(), params: z.record(z.union([z.number(), z.string(), z.boolean()])).optional() }).optional(),
      instanceParams: z.record(z.union([z.number(), z.string(), z.boolean()])).optional().describe("Length values in mm."),
      units,
      dryRun,
    }, fwdWrite("place_detail_component"));

  // ── Text with leaders ────────────────────────────────────────────────────
  tool("revit_list_text_note_types", "List text note types (font, size mm, colour, arrowhead).", {}, fwd("list_text_note_types"));

  tool("revit_create_text_with_leaders",
    "Create a note with real arrow leaders (never draw leader lines with detail lines). " +
    "Choose the practice text type with textTypeName; width is paper mm.",
    {
      text: z.string(),
      location: pt,
      viewId: z.number().int().optional(),
      textTypeName: z.string().optional(),
      width: z.number().optional().describe("Wrap width in paper mm. Default 60."),
      align: z.enum(["left", "center", "right"]).optional(),
      leaders: z.array(leader).optional(),
      units,
      dryRun,
    }, fwdWrite("create_text_with_leaders"));

  tool("revit_set_text_leaders", "Add leaders to an existing text note, or replace its leaders (replace=true).",
    { textNoteId: z.number().int(), leaders: z.array(leader).optional(), replace: z.boolean().optional(), units, dryRun },
    fwdWrite("set_text_leaders"));

  // ── Graphics (instead of hiding) ─────────────────────────────────────────
  tool("revit_get_view_graphics",
    "Read a view's graphics: template, detail level, category overrides, and every element HIDDEN in the view. " +
    "Use to audit details for hidden-element shortcuts.",
    { viewId: z.number().int().optional(), maxHidden: z.number().int().optional() }, fwd("get_view_graphics"));

  tool("revit_set_category_overrides",
    "Set category graphics in a view or a VIEW TEMPLATE (pass the template id): cut/projection fills and lines, halftone, visibility, detail level. " +
    "OCTA detail convention: cut = solid dark grey, projection = solid light grey; fade context with halftone. Don't hide elements.",
    {
      viewId: z.number().int(),
      overrides: z.array(z.object({ category: z.string().describe("'Walls' or 'OST_Walls'."), visible: z.boolean().optional(), ...graphics })).min(1),
      detailLevel: z.enum(["coarse", "medium", "fine"]).optional(),
      dryRun,
    }, fwdWrite("set_category_overrides"));

  tool("revit_set_element_graphics",
    "Per-element graphics in a view: halftone (fade), line colour/weight/pattern, surface/cut fills. reset=true clears. " +
    "Use instead of hiding elements.",
    { viewId: z.number().int().optional(), elementIds: ids, reset: z.boolean().optional(), ...graphics, dryRun },
    fwdWrite("set_element_graphics"));

  tool("revit_create_view_template_from_view",
    "Save a view's current graphics as a new view template (and assign it by default).",
    { viewId: z.number().int(), name: z.string(), assign: z.boolean().optional(), dryRun },
    fwdWrite("create_view_template_from_view"));

  // ── OCTA review workflow ─────────────────────────────────────────────────
  tool("octa_flag_elements",
    "Flag elements for review/clash in a view: magenta, heavier projection and cut lines (OCTA convention: magenta = internal, not for issue). " +
    "Pair with octa_add_internal_note to say why. reset=true clears the flag.",
    { viewId: z.number().int().optional(), elementIds: ids, lineWeight: z.number().int().min(1).max(16).optional(), reset: z.boolean().optional(), dryRun },
    fwdWrite("octa_flag_elements"));

  tool("octa_add_internal_note",
    "Add an internal office note (magenta 'OCTA - INTERNAL NOTE' text, created if missing) — questions and review comments " +
    "that must not go out on drawings, e.g. 'Check beam sits under trimmers here'. Supports leaders.",
    {
      text: z.string(),
      location: pt,
      viewId: z.number().int().optional(),
      leaders: z.array(leader).optional(),
      width: z.number().optional().describe("Paper mm. Default 70."),
      units,
      dryRun,
    }, fwdWrite("octa_add_internal_note"));

  tool("octa_list_internal_notes", "List all internal notes (text, view, hidden state). Run before issuing drawings.",
    { viewId: z.number().int().optional() }, fwd("octa_list_internal_notes"));

  // ── Phasing ──────────────────────────────────────────────────────────────
  tool("revit_list_phase_filters", "List phase filters and how each shows New / Existing / Demolished / Temporary.",
    {}, fwd("list_phase_filters"));

  const presentation = z.enum(["by_category", "overridden", "not_displayed"]).optional();
  tool("revit_create_phase_filter",
    "Create or update a phase filter. OCTA set: 'Show Existing', 'Show Demo + New', 'Show Previous + New', 'Show New'.",
    { name: z.string(), new: presentation, existing: presentation, demolished: presentation, temporary: presentation,
      updateIfExists: z.boolean().optional(), dryRun }, fwdWrite("create_phase_filter"));

  tool("revit_set_element_phase",
    "Set Phase Created and/or Phase Demolished (name or id) on elements. To demolish an existing element set " +
    "phaseDemolished to 'New Construction'; 'none' clears it. Per-element failures are reported, not fatal.",
    { elementIds: ids, phaseCreated: z.string().optional(), phaseDemolished: z.string().optional(), dryRun },
    fwdWrite("set_element_phase"));

  tool("revit_set_view_phase", "Set the Phase and/or Phase Filter of views (fails per view if a template controls it).",
    { viewIds: ids, phase: z.string().optional(), phaseFilter: z.string().optional(), dryRun }, fwdWrite("set_view_phase"));

  tool("revit_rename_phase", "Rename a phase. Ask the user first — views, schedules and elements all reference phases.",
    { phase: z.string(), newName: z.string(), dryRun }, fwdWrite("rename_phase"));

  // ── Revisions ────────────────────────────────────────────────────────────
  tool("revit_list_revisions", "List revisions (number, date, description, issued, numbering) and the sheets each is on.",
    {}, fwd("list_revisions"));

  tool("revit_create_revision",
    "Create a revision numbered in the OCTA stage sequence (stageCode DD → DD-01, DD-02…; sequence created if missing). " +
    "Practice OS owns the register: confirm with the user before creating revisions.",
    { stageCode: z.enum(["SD", "TP", "DD", "BP", "TD", "FC"]), description: z.string(),
      date: z.string().describe("As shown on sheets, e.g. 2026-10-06."),
      issuedBy: z.string().optional(), issuedTo: z.string().optional(), issued: z.boolean().optional(), dryRun },
    fwdWrite("create_revision"));

  tool("revit_update_revision", "Update a revision's description/date/issued by/to, or set issued (locks it).",
    { revisionId: z.number().int().optional(), revisionNumber: z.string().optional(), description: z.string().optional(),
      date: z.string().optional(), issuedBy: z.string().optional(), issuedTo: z.string().optional(),
      issued: z.boolean().optional(), dryRun }, fwdWrite("update_revision"));

  tool("revit_set_sheet_revisions", "Add (or remove=true) a revision on sheets by sheet number, e.g. ['AR-20-01'].",
    { revisionId: z.number().int().optional(), revisionNumber: z.string().optional(),
      sheetNumbers: z.array(z.string()).optional(), sheetIds: z.array(z.number().int()).optional(),
      remove: z.boolean().optional(), dryRun }, fwdWrite("set_sheet_revisions"));

  tool("revit_create_revision_cloud", "Draw a revision cloud around a changed area in a view or sheet.",
    { revisionId: z.number().int().optional(), revisionNumber: z.string().optional(), viewId: z.number().int().optional(),
      boundary: z.array(pt).min(3), units, dryRun }, fwdWrite("create_revision_cloud"));

  tool("octa_set_internal_notes_visibility",
    "Hide (visible=false) internal notes before printing/issuing, show them again after. Optional viewIds to limit.",
    { visible: z.boolean(), viewIds: z.array(z.number().int()).optional(), dryRun },
    fwdWrite("octa_set_internal_notes_visibility"));
}
