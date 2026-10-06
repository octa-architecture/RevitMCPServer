import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { buildInstructions, listSkills, readStandard } from "../octa.js";

let dir: string;

beforeAll(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "octa-skills-"));
  const skill = path.join(dir, "octa-practice-standards");
  fs.mkdirSync(path.join(skill, "references"), { recursive: true });
  fs.writeFileSync(
    path.join(skill, "SKILL.md"),
    "---\nname: octa-practice-standards\ndescription: OCTA Revit standards.\n---\n\n# Main guide\n",
  );
  fs.writeFileSync(path.join(skill, "references", "sheets-and-numbering.md"), "# Sheets\nAR-SS-NN");
  fs.mkdirSync(path.join(dir, "not-a-skill"));
});

afterAll(() => fs.rmSync(dir, { recursive: true, force: true }));

describe("octa standards", () => {
  it("lists skills with frontmatter and references", () => {
    expect(listSkills(dir)).toEqual([
      { name: "octa-practice-standards", description: "OCTA Revit standards.", references: ["sheets-and-numbering.md"] },
    ]);
  });

  it("returns no skills and no instructions when the folder is missing", () => {
    expect(listSkills(path.join(dir, "missing"))).toEqual([]);
    expect(buildInstructions([])).toBe("");
  });

  it("names each skill in the instructions", () => {
    expect(buildInstructions(listSkills(dir))).toContain("- octa-practice-standards: OCTA Revit standards.");
  });

  it("reads the main guide by default", () => {
    const r = readStandard(undefined, undefined, dir);
    expect(r.ok && r.text).toContain("# Main guide");
  });

  it("reads a reference with or without .md, any case", () => {
    for (const f of ["sheets-and-numbering", "Sheets-And-Numbering.md"]) {
      const r = readStandard(undefined, f, dir);
      expect(r.ok && r.text).toContain("AR-SS-NN");
    }
  });

  it("refuses paths outside the standards folder", () => {
    const r = readStandard(undefined, "..\\..\\SKILL", dir);
    expect(r.ok).toBe(false);
  });

  it("reports unknown skills and files", () => {
    expect(readStandard("nope", undefined, dir).ok).toBe(false);
    expect(readStandard(undefined, "nope", dir).ok).toBe(false);
  });
});
