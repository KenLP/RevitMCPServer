import { describe, it, expect } from "vitest";
import { readFileSync, readdirSync, existsSync } from "node:fs";
import { spawn } from "node:child_process";
import { join } from "node:path";
import {
  annotationsFor, toolTitle, READ_ONLY, NON_DESTRUCTIVE, IDEMPOTENT_WRITES,
  FILE_WRITERS_ON_READ_COMMANDS, UI_STATE_ONLY,
} from "../annotations.js";

const SRC = join(__dirname, "..");
const COMMANDS = join(__dirname, "..", "..", "..", "RevitMCP.Core", "Commands");

/** tool name → the C# command its handler forwards to (null for batch and recipes). */
function registeredTools(): Map<string, string | null> {
  const text = readFileSync(join(SRC, "index.ts"), "utf8");
  const tools = new Map<string, string | null>();
  for (const part of text.split(/(?=server\.tool\(\s*")/).slice(1)) {
    const name = part.match(/server\.tool\(\s*"(\w+)"/)![1];
    tools.set(name, part.match(/fwd\w*\("(\w+)"\)/)?.[1]
      ?? part.match(/callRevit\("(\w+)"/)?.[1] ?? null);
  }
  return tools;
}

/** C# command name → IsReadOnly, read from the command classes. */
function csharpReadOnly(): Map<string, boolean> {
  const map = new Map<string, boolean>();
  for (const file of readdirSync(COMMANDS).filter((f) => f.endsWith(".cs"))) {
    const text = readFileSync(join(COMMANDS, file), "utf8");
    for (const cls of text.split(/(?=\bclass\s+\w+\s*:\s*IRevitCommand)/).slice(1)) {
      const name = cls.match(/Name\s*=>\s*"(\w+)"/)?.[1];
      const ro = cls.match(/IsReadOnly\s*=>\s*(true|false)/)?.[1];
      if (name && ro) map.set(name, ro === "true");
    }
  }
  return map;
}

describe("tool annotations", () => {
  const tools = registeredTools();

  it("finds the registered tools and the C# commands", () => {
    expect(tools.size).toBeGreaterThanOrEqual(100);
    expect(csharpReadOnly().size).toBeGreaterThanOrEqual(90);
  });

  it("lists only tools that exist", () => {
    for (const set of [READ_ONLY, NON_DESTRUCTIVE, IDEMPOTENT_WRITES, FILE_WRITERS_ON_READ_COMMANDS, UI_STATE_ONLY])
      for (const name of set) expect(tools.has(name), name).toBe(true);
    for (const name of NON_DESTRUCTIVE) expect(READ_ONLY.has(name), name).toBe(false);
  });

  it("agrees with IsReadOnly of the C# command behind each tool", () => {
    const ro = csharpReadOnly();
    for (const [tool, command] of tools) {
      if (command === null) continue;
      expect(ro.has(command), `${tool} → ${command}`).toBe(true);
      const hint = annotationsFor(tool).readOnlyHint;
      if (ro.get(command)) {
        // A read command is a read-only tool, unless the tool writes a file the caller names.
        expect(hint, tool).toBe(!FILE_WRITERS_ON_READ_COMMANDS.has(tool));
      } else {
        // A write command is never a read-only tool, unless it changes UI state only.
        expect(hint, tool).toBe(UI_STATE_ONLY.has(tool));
      }
    }
  });

  it("marks batch and the recipes by what they do", () => {
    expect(annotationsFor("revit_batch")).toMatchObject({ readOnlyHint: false, destructiveHint: true });
    expect(annotationsFor("revit_recipe_clash_review").readOnlyHint).toBe(true);
    expect(annotationsFor("revit_recipe_model_health_triage").readOnlyHint).toBe(true);
  });

  it("treats a tool nobody classified as destructive", () => {
    expect(annotationsFor("revit_not_classified")).toMatchObject({ readOnlyHint: false, destructiveHint: true });
  });

  it("marks deletes, overwrites and saves destructive", () => {
    for (const name of ["revit_delete_elements", "revit_set_parameter", "revit_save_as",
      "revit_close_document", "revit_export_ifc", "revit_load_family", "revit_ungroup_elements"])
      expect(annotationsFor(name).destructiveHint, name).toBe(true);
    expect(annotationsFor("revit_create_wall").destructiveHint).toBe(false);
  });

  it("gives every tool a short, readable title and no hint is open-world", () => {
    for (const name of tools.keys()) {
      const a = annotationsFor(name);
      expect(a.title.length, name).toBeGreaterThan(2);
      expect(a.title.length, name).toBeLessThanOrEqual(64);
      expect(a.title, name).not.toMatch(/_/);
      expect(a.openWorldHint).toBe(false);
    }
    expect(toolTitle("revit_create_3d_view")).toBe("Create 3D view");
    expect(toolTitle("revit_export_ifc")).toBe("Export IFC");
    expect(toolTitle("revit_recipe_clash_review")).toBe("Recipe: clash review");
  });
});

/** Talk to the built server over stdio, as a client does, and read tools/list. */
function listTools(): Promise<Array<{ name: string; title?: string; description?: string; annotations?: Record<string, unknown> }>> {
  const entry = join(SRC, "..", "dist", "index.js");
  if (!existsSync(entry)) throw new Error(`${entry} is missing: run "npm run build" before the tests.`);
  return new Promise((resolve, reject) => {
    // Port 9 (discard): the start-up health probe fails fast and touches no running Revit.
    const child = spawn(process.execPath, [entry], { env: { ...process.env, REVIT_MCP_PORT: "9" } });
    let buffer = "";
    const timer = setTimeout(() => { child.kill(); reject(new Error("no tools/list answer")); }, 15000);
    child.stdout.on("data", (chunk) => {
      buffer += chunk;
      for (const line of buffer.split("\n")) {
        if (!line.includes('"id":2')) continue;
        clearTimeout(timer);
        child.kill();
        resolve(JSON.parse(line).result.tools);
        return;
      }
    });
    const send = (msg: object) => child.stdin.write(JSON.stringify(msg) + "\n");
    send({ jsonrpc: "2.0", id: 1, method: "initialize",
      params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "test", version: "0" } } });
    send({ jsonrpc: "2.0", method: "notifications/initialized" });
    send({ jsonrpc: "2.0", id: 2, method: "tools/list", params: {} });
  });
}

describe("tools/list of the built server", () => {
  it("carries the title and the hints for every tool", async () => {
    const listed = await listTools();
    expect(listed.length).toBe(registeredTools().size);
    for (const tool of listed) {
      const expected = annotationsFor(tool.name);
      expect(tool.title, tool.name).toBe(expected.title);
      expect(tool.annotations, tool.name).toEqual(expected);
    }
  }, 20000);

  // Directory review rejects descriptions that tell the model how to behave. A description says
  // what the tool does; advice on choosing between tools lives in the plugin's skill.
  it("describes each tool without instructing the model", async () => {
    const steering = /\buse this\b|\binstead\b|\bimportant\b|\bdo not\b|\bdon't\b|\byou\b|\buse revit_|\buses revit_|\bcall this\b|\bmake sure\b/i;
    for (const tool of await listTools())
      expect(tool.description ?? "", tool.name).not.toMatch(steering);
  }, 20000);
});
