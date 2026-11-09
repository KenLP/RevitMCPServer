#!/usr/bin/env node
// Builds the Claude Code plugin in plugin/ from the MCP bridge in src/McpServer.
//
//   node scripts/build-plugin.mjs           write plugin/server, plugin/package.json, the version
//   node scripts/build-plugin.mjs --check   exit 1 when plugin/ differs from a fresh build
//
// The plugin carries the compiled bridge because a plugin install runs no build step. Claude Code
// installs the runtime dependencies from plugin/package-lock.json with scripts disabled, so
// package.json pins exact versions taken from src/McpServer/package-lock.json.
// Run it after `npm run build` in src/McpServer, at every release (check-version.mjs checks the
// version stamps), and refresh the lockfile with `npm install --package-lock-only --ignore-scripts`
// in plugin/ when the bridge's runtime dependencies change.

import { readFileSync, writeFileSync, readdirSync, mkdirSync, rmSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const bridge = join(root, "src", "McpServer");
const dist = join(bridge, "dist");
const plugin = join(root, "plugin");
const check = process.argv.includes("--check");

const pkg = JSON.parse(readFileSync(join(bridge, "package.json"), "utf8"));
const lock = JSON.parse(readFileSync(join(bridge, "package-lock.json"), "utf8"));

if (!existsSync(join(dist, "index.js"))) {
  console.error("src/McpServer/dist/index.js is missing: run `npm run build` in src/McpServer first.");
  process.exit(1);
}

/** Expected plugin files: path relative to plugin/ → content. */
const expected = new Map();

for (const file of readdirSync(dist).filter((f) => f.endsWith(".js")).sort()) {
  // Drop the source-map comment: the maps and the TypeScript sources are not in the plugin.
  const js = readFileSync(join(dist, file), "utf8").replace(/\n\/\/# sourceMappingURL=.*\s*$/, "\n");
  expected.set(`server/${file}`, js);
}

const dependencies = {};
for (const name of Object.keys(pkg.dependencies).sort()) {
  const locked = lock.packages[`node_modules/${name}`]?.version;
  if (!locked) throw new Error(`${name} is not in src/McpServer/package-lock.json`);
  dependencies[name] = locked;
}
expected.set("package.json", JSON.stringify({
  name: "revit-mcp-lephu-server",
  version: pkg.version,
  private: true,
  description: "Runtime dependencies of the RevitMCP bridge in server/.",
  type: "module",
  license: "MIT",
  dependencies,
}, null, 2) + "\n");

const manifestPath = join(plugin, ".claude-plugin", "plugin.json");
const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
manifest.version = pkg.version;
expected.set(".claude-plugin/plugin.json", JSON.stringify(manifest, null, 2) + "\n");

const normalize = (s) => s.replace(/\r\n/g, "\n");

if (check) {
  const problems = [];
  for (const [rel, content] of expected) {
    const path = join(plugin, rel);
    if (!existsSync(path)) problems.push(`missing: plugin/${rel}`);
    else if (normalize(readFileSync(path, "utf8")) !== normalize(content)) problems.push(`differs: plugin/${rel}`);
  }
  for (const file of readdirSync(join(plugin, "server")))
    if (!expected.has(`server/${file}`)) problems.push(`stale: plugin/server/${file}`);
  const pluginLock = JSON.parse(readFileSync(join(plugin, "package-lock.json"), "utf8"));
  for (const [name, version] of Object.entries(dependencies))
    if (pluginLock.packages?.[`node_modules/${name}`]?.version !== version)
      problems.push(`plugin/package-lock.json: ${name} is not ${version}`);
  if (problems.length) {
    console.error("plugin/ is out of date (run node scripts/build-plugin.mjs):\n  " + problems.join("\n  "));
    process.exit(1);
  }
  console.log(`plugin/ matches the bridge build (v${pkg.version}).`);
} else {
  rmSync(join(plugin, "server"), { recursive: true, force: true });
  mkdirSync(join(plugin, "server"), { recursive: true });
  for (const [rel, content] of expected) writeFileSync(join(plugin, rel), content);
  console.log(`plugin/ written for v${pkg.version}: ${[...expected.keys()].join(", ")}`);
}
