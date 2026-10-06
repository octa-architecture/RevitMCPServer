/**
 * OCTA practice standards bridge.
 *
 * Serves the OCTA skill folders (SKILL.md + references/*.md, kept on the OCTA
 * shared drive) through this MCP server, so every Claude client that connects
 * to Revit gets the practice standards automatically — no per-app skill install.
 *
 * Files are read fresh on every call: edit a skill on the drive and the next
 * call sees it. If the folder is missing, the server behaves exactly like
 * upstream (no instructions, no extra tool).
 */

import fs from "node:fs";
import path from "node:path";
import { z } from "zod";

export const DEFAULT_STANDARDS_DIR =
  "H:\\Shared drives\\OCTA\\3 Standards & Library\\3 Claude\\Skills";

export interface SkillInfo {
  name: string;
  description: string;
  references: string[];
}

export function standardsDir(): string {
  return process.env.OCTA_STANDARDS_DIR?.trim() || DEFAULT_STANDARDS_DIR;
}

function frontmatterField(text: string, field: string): string | undefined {
  const fm = /^---\r?\n([\s\S]*?)\r?\n---/.exec(text);
  if (!fm) return undefined;
  const line = new RegExp(`^${field}:\\s*(.+)$`, "m").exec(fm[1]);
  return line?.[1].trim();
}

export function listSkills(dir: string = standardsDir()): SkillInfo[] {
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return [];
  }
  const skills: SkillInfo[] = [];
  for (const entry of entries) {
    if (!entry.isDirectory()) continue;
    const skillFile = path.join(dir, entry.name, "SKILL.md");
    let text: string;
    try {
      text = fs.readFileSync(skillFile, "utf8");
    } catch {
      continue;
    }
    let references: string[] = [];
    try {
      references = fs
        .readdirSync(path.join(dir, entry.name, "references"))
        .filter((f) => f.toLowerCase().endsWith(".md"))
        .sort();
    } catch {
      // no references folder
    }
    skills.push({
      name: frontmatterField(text, "name") ?? entry.name,
      description: frontmatterField(text, "description") ?? "",
      references,
    });
  }
  return skills.sort((a, b) => a.name.localeCompare(b.name));
}

/** Server instructions sent to the client on connect; empty when no skills are found. */
export function buildInstructions(skills: SkillInfo[] = listSkills()): string {
  if (skills.length === 0) return "";
  const list = skills.map((s) => `- ${s.name}: ${s.description}`).join("\n");
  return [
    "This Revit connection is configured for OCTA (architecture practice).",
    "Before creating, renaming, tagging, placing, loading, deleting or issuing anything in a Revit model,",
    "call octa_get_standards (no arguments) once in the conversation and follow it; then read the",
    "specific reference file it points to for the task. Read-only questions don't need it.",
    "Available OCTA guides:",
    list,
  ].join("\n");
}

/**
 * Return a skill's SKILL.md, or one of its reference files. Names are matched
 * case-insensitively and only by bare file/folder name, so callers can't
 * escape the standards folder.
 */
export function readStandard(
  skill: string | undefined,
  file: string | undefined,
  dir: string = standardsDir(),
): { ok: true; text: string } | { ok: false; error: string } {
  const skills = listSkills(dir);
  if (skills.length === 0)
    return { ok: false, error: `No OCTA standards found at ${dir}. Is the OCTA shared drive available?` };

  const wanted = (skill ?? "octa-practice-standards").toLowerCase();
  const folder = fs
    .readdirSync(dir, { withFileTypes: true })
    .find((e) => e.isDirectory() && e.name.toLowerCase() === wanted);
  if (!folder)
    return { ok: false, error: `Unknown skill "${skill}". Available: ${skills.map((s) => s.name).join(", ")}` };

  const skillDir = path.join(dir, folder.name);
  if (!file) return { ok: true, text: fs.readFileSync(path.join(skillDir, "SKILL.md"), "utf8") };

  const base = path.basename(file).toLowerCase().replace(/\.md$/, "") + ".md";
  const refDir = path.join(skillDir, "references");
  let refs: string[] = [];
  try {
    refs = fs.readdirSync(refDir);
  } catch {
    // fall through to the error below
  }
  const match = refs.find((r) => r.toLowerCase() === base);
  if (!match)
    return { ok: false, error: `No reference "${file}" in ${folder.name}. Available: ${refs.join(", ") || "none"}` };
  return { ok: true, text: fs.readFileSync(path.join(refDir, match), "utf8") };
}

type ToolRegistrar = (
  name: string,
  description: string,
  shape: Record<string, z.ZodTypeAny>,
  handler: (params: { skill?: string; file?: string }) => Promise<unknown>,
) => unknown;

export function registerOctaTools(tool: ToolRegistrar): void {
  if (listSkills().length === 0) return;
  tool(
    "octa_get_standards",
    "OCTA practice standards for Revit (sheet numbering, view names and templates, annotation, " +
      "parameters, families, revisions, phasing). Call with no arguments before changing a model; " +
      "pass `file` (e.g. 'sheets-and-numbering') to read a specific reference.",
    {
      skill: z.string().optional().describe("Skill folder name. Default 'octa-practice-standards'."),
      file: z.string().optional().describe("Reference file name, e.g. 'views-and-templates'. Omit for the main guide."),
    },
    async ({ skill, file }) => {
      const result = readStandard(skill, file);
      return result.ok
        ? { content: [{ type: "text", text: result.text }] }
        : { content: [{ type: "text", text: result.error }], isError: true };
    },
  );
}
