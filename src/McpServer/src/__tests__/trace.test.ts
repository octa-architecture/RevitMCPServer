/**
 * Unit tests for the evaluation-harness hooks in trace.ts and the headers revitClient sends.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { mkdtempSync, readFileSync, existsSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

const ENV_KEYS = ["REVIT_MCP_CLIENT", "REVIT_MCP_TRACE_ID", "REVIT_MCP_TRACE_FILE"];
let saved: Record<string, string | undefined>;

beforeEach(() => {
  saved = Object.fromEntries(ENV_KEYS.map((k) => [k, process.env[k]]));
  for (const k of ENV_KEYS) delete process.env[k];
});
afterEach(() => {
  for (const k of ENV_KEYS) {
    if (saved[k] === undefined) delete process.env[k];
    else process.env[k] = saved[k];
  }
  vi.restoreAllMocks();
});

describe("cleanHeaderValue / traceHeaders", () => {
  it("trims, strips control characters, and cuts to the limit", async () => {
    const { cleanHeaderValue } = await import("../trace.js");
    expect(cleanHeaderValue(undefined, 64)).toBeUndefined();
    expect(cleanHeaderValue("   ", 64)).toBeUndefined();
    expect(cleanHeaderValue("  a\u0001b\r\nc  ", 64)).toBe("abc");
    expect(cleanHeaderValue("x".repeat(300), 128)).toHaveLength(128);
  });

  it("sends nothing when the env vars are unset", async () => {
    const { traceHeaders } = await import("../trace.js");
    expect(traceHeaders()).toEqual({});
  });

  it("maps REVIT_MCP_CLIENT / REVIT_MCP_TRACE_ID to X-MCP-Client / X-MCP-Trace", async () => {
    process.env.REVIT_MCP_CLIENT = "harness";
    process.env.REVIT_MCP_TRACE_ID = "run-2026-10-05/q017";
    const { traceHeaders } = await import("../trace.js");
    expect(traceHeaders()).toEqual({ "x-mcp-client": "harness", "x-mcp-trace": "run-2026-10-05/q017" });
  });
});

describe("traceLine", () => {
  it("records size, outcome and the add-in error code", async () => {
    process.env.REVIT_MCP_TRACE_ID = "run/q1";
    const { traceLine } = await import("../trace.js");
    const fail = {
      isError: true,
      content: [{ type: "text", text: JSON.stringify({ ok: false, error: { code: "not_found", message: "x" } }) }],
    };
    const line = traceLine("revit_get_element_info", { id: 5 }, fail, 118, new Date("2026-10-05T00:00:00Z"));
    expect(line).toMatchObject({
      ts: "2026-10-05T00:00:00.000Z",
      tool: "revit_get_element_info",
      durationMs: 118,
      ok: false,
      errorCode: "not_found",
      reqBytes: Buffer.byteLength(JSON.stringify({ id: 5 })),
      trace: "run/q1",
    });
    expect(line.respBytes).toBe(Buffer.byteLength(JSON.stringify(fail.content)));
  });

  it("has a null error code for a successful call", async () => {
    const { traceLine } = await import("../trace.js");
    const line = traceLine("revit_ping", {}, { isError: false, content: [{ type: "text", text: "{}" }] }, 3);
    expect(line.ok).toBe(true);
    expect(line.errorCode).toBeNull();
    expect(line.trace).toBeNull();
  });
});

describe("withTrace", () => {
  it("appends one line per call when REVIT_MCP_TRACE_FILE is set, and returns the result untouched", async () => {
    const dir = mkdtempSync(join(tmpdir(), "rmcp-trace-"));
    const file = join(dir, "trace.jsonl");
    process.env.REVIT_MCP_TRACE_FILE = file;
    process.env.REVIT_MCP_TRACE_ID = "run/q2";
    try {
      const { withTrace } = await import("../trace.js");
      const result = { isError: false, content: [{ type: "text", text: '{"ok":true}' }] };
      const wrapped = withTrace("revit_ping", async (_args: unknown) => result);
      expect(await wrapped({})).toBe(result);
      await wrapped({});
      const lines = readFileSync(file, "utf8").trim().split("\n").map((l) => JSON.parse(l));
      expect(lines).toHaveLength(2);
      expect(lines[0]).toMatchObject({ tool: "revit_ping", ok: true, trace: "run/q2" });
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it("writes nothing when REVIT_MCP_TRACE_FILE is unset", async () => {
    const dir = mkdtempSync(join(tmpdir(), "rmcp-trace-off-"));
    const file = join(dir, "trace.jsonl");
    try {
      const { withTrace } = await import("../trace.js");
      await withTrace("revit_ping", async (_a: unknown) => ({ isError: false, content: [] }))({});
      expect(existsSync(file)).toBe(false);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it("never fails a tool call when the trace file cannot be written", async () => {
    const dir = mkdtempSync(join(tmpdir(), "rmcp-trace-bad-"));
    process.env.REVIT_MCP_TRACE_FILE = dir; // a directory, not a file → append fails
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      const { withTrace } = await import("../trace.js");
      const result = { isError: false, content: [] };
      expect(await withTrace("revit_ping", async (_a: unknown) => result)({})).toBe(result);
    } finally {
      err.mockRestore();
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe("revitClient sends the trace headers", () => {
  it("adds X-MCP-Client and X-MCP-Trace to every call", async () => {
    process.env.REVIT_MCP_CLIENT = "harness";
    process.env.REVIT_MCP_TRACE_ID = "run/q3";
    vi.resetModules();
    const fetchMock = vi.fn().mockResolvedValueOnce({
      status: 200,
      text: () => Promise.resolve(JSON.stringify({ ok: true, data: {} })),
    } as unknown as Response);
    vi.stubGlobal("fetch", fetchMock);
    const { callRevit } = await import("../revitClient.js");
    await callRevit("ping", {});
    const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>;
    expect(headers["x-mcp-client"]).toBe("harness");
    expect(headers["x-mcp-trace"]).toBe("run/q3");
    vi.unstubAllGlobals();
  });
});
