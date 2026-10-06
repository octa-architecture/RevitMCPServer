/**
 * OCTA remote session tools: launch Revit (optionally with a model), exit it cleanly, list and
 * open documents. Launch runs in this Node process because the add-in isn't running yet.
 */

import { spawn } from "node:child_process";
import fs from "node:fs";
import { z } from "zod";
import { callRevit, checkRevitHealth, envelopeToToolResult } from "./revitClient.js";

type Handler = (params: Record<string, unknown>) => Promise<unknown>;
type ToolFn = (name: string, description: string, shape: Record<string, z.ZodTypeAny>, handler: Handler) => unknown;

const VERSION = process.env.REVIT_MCP_VERSION ?? "2026";
export const revitExe = () =>
  process.env.REVIT_EXE ?? `C:\\Program Files\\Autodesk\\Revit ${VERSION}\\Revit.exe`;

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const text = (obj: unknown, isError = false) => ({
  content: [{ type: "text" as const, text: JSON.stringify(obj, null, 2) }],
  isError,
});

/** Poll until `check` returns a value or the timeout passes. */
export async function waitFor<T>(check: () => Promise<T | undefined>, timeoutMs: number, everyMs = 3000): Promise<T | undefined> {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    const v = await check();
    if (v !== undefined) return v;
    await sleep(everyMs);
  }
  return undefined;
}

async function activeDocTitle(): Promise<string | undefined> {
  try {
    const r = await callRevit("ping", {});
    const d = r.data as { hasActiveDocument?: boolean; activeDocumentTitle?: string } | undefined;
    return r.ok && d?.hasActiveDocument ? d.activeDocumentTitle : undefined;
  } catch {
    return undefined;
  }
}

export function registerOctaSessionTools(tool: ToolFn): void {
  tool("revit_launch",
    "Start Revit on this PC (optionally opening a model) and wait until the MCP add-in answers. " +
    "Use when Revit isn't running (other tools fail with 'Failed to reach Revit addin'). " +
    "Harmless open-time dialogs such as unresolved CAD links are answered automatically; others wait for the user.",
    {
      modelPath: z.string().optional().describe("Full path to a .rvt/.rfa to open."),
      timeoutSeconds: z.number().int().min(30).max(900).optional().describe("Default 300."),
    },
    async (p) => {
      const modelPath = p.modelPath as string | undefined;
      if ((await checkRevitHealth()).reachable)
        return text({ ok: false, error: "Revit is already running with the add-in. Use revit_open_document to open a model." }, true);
      if (!fs.existsSync(revitExe())) return text({ ok: false, error: `Revit not found at ${revitExe()}` }, true);
      if (modelPath && !fs.existsSync(modelPath)) return text({ ok: false, error: `Model not found: ${modelPath}` }, true);

      const child = spawn(revitExe(), modelPath ? [modelPath] : [], { detached: true, stdio: "ignore" });
      child.unref();
      const timeout = ((p.timeoutSeconds as number | undefined) ?? 300) * 1000;
      const started = Date.now();

      const up = await waitFor(async () => ((await checkRevitHealth()).reachable ? true : undefined), timeout);
      if (!up)
        return text({ ok: false, error: "Revit started but the add-in didn't answer in time. A dialog (e.g. the add-in " +
          "security prompt) may be waiting for someone at the PC." }, true);
      const title = modelPath
        ? await waitFor(activeDocTitle, Math.max(timeout - (Date.now() - started), 30000))
        : undefined;
      return text({
        ok: true,
        seconds: Math.round((Date.now() - started) / 1000),
        activeDocument: title ?? null,
        note: modelPath && !title ? "Revit is up but the model isn't active yet — a dialog may be waiting." : undefined,
      });
    });

  tool("revit_exit",
    "Exit Revit cleanly. save=true saves modified documents first; save=false refuses if anything is unsaved " +
    "(never discards work). Waits until Revit has closed. Confirm with the user before saving their model.",
    {
      save: z.boolean(),
      timeoutSeconds: z.number().int().min(10).max(600).optional().describe("Default 120."),
    },
    async (p) => {
      const env = await callRevit("exit_revit", { save: p.save === true }).catch((e) => ({ ok: true, data: { note: String(e) } }));
      if (!env.ok) return envelopeToToolResult(env as never);
      const timeout = ((p.timeoutSeconds as number | undefined) ?? 120) * 1000;
      const gone = await waitFor(async () => ((await checkRevitHealth()).reachable ? undefined : true), timeout, 2000);
      return text({ ok: gone === true, exited: gone === true, result: (env as { data?: unknown }).data,
        error: gone ? undefined : "Revit is still running — a dialog may be waiting at the PC." }, !gone);
    });

  tool("revit_set_dialog_mode",
    "Switch Revit dialog handling. 'attended' (the user is at the PC): every dialog is shown to them, nothing is auto-answered — also don't restart Revit without asking. 'unattended' (away from keyboard / remote): the whitelisted harmless dialogs (unresolved references, document-warning OK) are answered automatically. Takes effect immediately.",
    { mode: z.enum(["attended", "unattended"]) },
    async (p) => {
      const fs = await import("node:fs");
      const path = await import("node:path");
      const file = path.join(process.env.APPDATA ?? "", "Autodesk", "Revit", "Addins", "2027", "revit-mcp-dialogs.json");
      let cfg: Record<string, unknown> = {};
      try { cfg = JSON.parse(fs.readFileSync(file, "utf8")); } catch { /* start fresh */ }
      cfg.enabled = p.mode === "unattended";
      cfg.note = p.mode === "unattended"
        ? "UNATTENDED mode: whitelisted dialogs are answered automatically."
        : "ATTENDED mode: dialogs are shown to the user, not auto-answered.";
      if (!Array.isArray(cfg.rules)) cfg.rules = [
        { name: "Unresolved references -> Ignore and continue", messageContains: "could not find or read", result: 1002 },
        { name: "Document warnings -> OK", dialogId: "Dialog_Revit_DocWarnDialog", result: 1 },
      ];
      fs.writeFileSync(file, JSON.stringify(cfg, null, 2));
      return { content: [{ type: "text" as const, text: JSON.stringify({ ok: true, mode: p.mode, file }) }] };
    });

  tool("revit_list_open_documents", "List documents open in Revit (title, path, modified, active).", {},
    async () => envelopeToToolResult(await callRevit("list_open_documents", {})));

  tool("revit_open_document", "Open a .rvt/.rfa in the running Revit and make it active.",
    { path: z.string(), audit: z.boolean().optional() },
    async (p) => envelopeToToolResult(await callRevit("open_document", p)));

  tool("revit_save_document",
    "Save the active document (or save it as a new .rvt). Ask the user first for a live project model; test models are fine.",
    { saveAsPath: z.string().optional(), overwrite: z.boolean().optional() },
    async (p) => envelopeToToolResult(await callRevit("save_document", p)));
}
