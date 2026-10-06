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
  orthogonal: z.boolean().optional().describe("Default true: horizontal shoulder then vertical drop (90°)."),
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

  tool("revit_tidy_text_leaders",
    "Apply the OCTA leader rule in a view: leaders never cross and are 90° (horizontal from the text, vertical drop " +
    "only if needed). Re-orders notes on each side to match their targets, moves notes level with targets where there " +
    "is room, never moves arrow tips. Reports crossings before/after. Run after annotating any detail.",
    { viewId: z.number().int().optional(), noteIds: z.array(z.number().int()).optional(),
      gapMm: z.number().optional().describe("Paper gap between notes, default 2."),
      moveNotes: z.boolean().optional().describe("false = only square up elbows."),
      alignColumns: z.boolean().optional().describe("Default true: margin notes share a left text edge per column; notes inside the drawing stay put."),
      dryRun },
    fwdWrite("tidy_text_leaders"));

  tool("revit_convert_line_leaders",
    "Fix notes whose 'leaders' were drawn as detail lines (a line + two arrowhead strokes): replace each with a real " +
    "text leader to the same tip and delete the drawn lines. Dry-run first to check the matches.",
    { viewId: z.number().int().optional(), maxGapMm: z.number().optional(), arrowMaxMm: z.number().optional(), dryRun },
    fwdWrite("convert_line_leaders"));

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
    "Save a view's current graphics as a new view template (and assign it by default). By default the template does " +
    "NOT control View Scale, so applying it never changes a detail's scale.",
    { viewId: z.number().int(), name: z.string(), assign: z.boolean().optional(),
      uncontrolled: z.array(z.string()).optional().describe("Properties the template must not control. Default ['View Scale']."),
      dryRun },
    fwdWrite("create_view_template_from_view"));

  tool("revit_set_template_controls",
    "Choose which properties a view template controls: release (stop controlling, e.g. ['View Scale']) or control.",
    { templateId: z.number().int().optional(), templateName: z.string().optional(),
      release: z.array(z.string()).optional(), control: z.array(z.string()).optional(), dryRun },
    fwdWrite("set_template_controls"));

  tool("revit_set_view_scale", "Set a view's scale 1:N (fails if its template controls scale).",
    { viewId: z.number().int().optional(), scale: z.number().int().min(1), dryRun }, fwdWrite("set_view_scale"));

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

  // ── Family builder ───────────────────────────────────────────────────────
  tool("revit_create_detail_family",
    "Build a PARAMETRIC 2D detail item family (.rfa) from a spec: reference planes, labelled dimensions, lines whose " +
    "ends sit on plane crossings (each line and end is locked), yes/no visibility, and types. Every type is flexed and " +
    "every line end checked; nothing is saved unless all pass. Saves to the OCTA Family Development folder and loads it. " +
    "Built-in planes: 'CV' (vertical, X=0) and 'CH' (horizontal, Y=0). Lengths in mm. Check the library first.",
    {
      name: z.string().describe("e.g. 'OCTA-Detail-Angle'"),
      lineBased: z.boolean().optional(),
      folder: z.string().optional(),
      load: z.boolean().optional(),
      parameters: z.array(z.object({ name: z.string(), kind: z.enum(["length", "yesno"]).optional(),
        instance: z.boolean().optional(), default: z.union([z.number(), z.boolean()]).optional() })).optional(),
      planes: z.array(z.object({ name: z.string(), axis: z.enum(["vertical", "horizontal"]), offset: z.number() })),
      dimensions: z.array(z.object({ planes: z.array(z.string()).min(2), parameter: z.string().optional(),
        equal: z.boolean().optional() })).optional(),
      lines: z.array(z.object({ from: z.tuple([z.string(), z.string()]), to: z.tuple([z.string(), z.string()]),
        visibleIf: z.string().optional(), style: z.string().optional() })).min(1),
      types: z.array(z.object({ name: z.string(), values: z.record(z.union([z.number(), z.boolean()])).optional() })).optional(),
      dryRun,
    }, fwdWrite("create_detail_family"));

  tool("revit_create_model_family",
    "Build a PARAMETRIC 3D family (.rfa) from a spec: reference planes on x/y/z axes, labelled dimensions (or equal " +
    "constraints), box solids/voids whose every face is locked to its planes, yes/no visibility, material parameters, " +
    "category, and types. Every type is flexed and each box checked against its planes; nothing is saved unless all pass. " +
    "Built-in planes: 'CX' (X=0), 'CY' (Y=0), 'LEVEL' (Z=0). Lengths in mm. Check the library first.",
    {
      name: z.string().describe("e.g. 'OCTA-Joinery-Base Cabinet'"),
      category: z.string().optional().describe("e.g. 'Casework', 'Furniture', 'Specialty Equipment'. Default Generic Models."),
      folder: z.string().optional(),
      load: z.boolean().optional(),
      parameters: z.array(z.object({ name: z.string(), kind: z.enum(["length", "yesno", "material"]).optional(),
        instance: z.boolean().optional(), default: z.union([z.number(), z.boolean()]).optional(),
        formula: z.string().optional().describe("Revit formula, e.g. 'and(Top Rail On Flat, not(Solid Top))' or 'Kick Height + Height'.") })).optional(),
      planes: z.array(z.object({ name: z.string(), axis: z.enum(["x", "y", "z"]), offset: z.number(),
        reference: z.string().optional().describe("left|right|front|back|top|bottom|strong|weak|not — named references give drag handles and snapping.") })),
      references: z.record(z.string()).optional().describe("Reference types for built-in planes, e.g. { CY: 'back' }."),
      dimensions: z.array(z.object({ planes: z.array(z.string()).min(2), parameter: z.string().optional(),
        equal: z.boolean().optional() })).optional(),
      boxes: z.array(z.object({ x: z.tuple([z.string(), z.string()]), y: z.tuple([z.string(), z.string()]),
        z: z.tuple([z.string(), z.string()]), void: z.boolean().optional(), visibleIf: z.string().optional(),
        material: z.string().optional(),
        detail: z.enum(["all", "fine", "coarse-medium"]).optional().describe("'fine' = construction parts; 'coarse-medium' = simple envelope.") })).min(1),
      types: z.array(z.object({ name: z.string(), values: z.record(z.union([z.number(), z.boolean()])).optional() })).optional(),
      scenarios: z.array(z.object({ name: z.string(), values: z.record(z.union([z.number(), z.boolean()])) })).optional()
        .describe("Instance-parameter combinations (tick boxes, sizes) flex-tested on the first type, not saved as types."),
      dryRun,
    }, fwdWrite("create_model_family"));

  tool("revit_fit_3d_crop_to_section_box",
    "Crop a 3D view to its section box so revit_get_view_image (and sheets) frame just the boxed area.",
    { viewId: z.number().int().optional(), paddingMm: z.number().optional(), dryRun },
    fwdWrite("fit_3d_crop_to_section_box"));

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
    "Create a revision numbered in the OCTA stage sequence (stageCode DD → DD-01, DD-02…; reuses the project's 'DD' sequence). " +
    "Practice OS owns the register: confirm with the user before creating revisions.",
    { stageCode: z.enum(["SD", "TP", "DD", "BP", "TD", "FC"]), description: z.string(),
      date: z.string().describe("As shown on sheets: dd/mm/yyyy, e.g. 06/10/2026."),
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
