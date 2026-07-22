#!/usr/bin/env node
/**
 * Verifies version strings in package.json, index.ts, RevitMCPAddin.csproj, and
 * CHANGELOG.md all agree.  Exits non-zero on any mismatch.
 *
 * The C# runtime version is single-sourced from the csproj <Version>; /health
 * reports it via the compiled AssemblyInformationalVersion (see BuildInfo), so
 * there is no hand-typed version literal in McpHttpServer.cs to check anymore.
 */
import { readFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

const pkg = JSON.parse(readFileSync(join(root, 'src/McpServer/package.json'), 'utf8'));
const expected = pkg.version;

const indexTs = readFileSync(join(root, 'src/McpServer/src/index.ts'), 'utf8');
const tsMatch = indexTs.match(/new McpServer\(\s*\{[^}]*version:\s*"([^"]+)"/s);

const csproj = readFileSync(join(root, 'src/RevitAddin/RevitMCPAddin.csproj'), 'utf8');
const csMatch = csproj.match(/<Version>([^<]+)<\/Version>/);

const changelog = readFileSync(join(root, 'CHANGELOG.md'), 'utf8');

const errors = [];

if (!tsMatch) {
  errors.push('Could not find version in src/McpServer/src/index.ts');
} else if (tsMatch[1] !== expected) {
  errors.push(`index.ts version "${tsMatch[1]}" != package.json "${expected}"`);
}

const startupLogMatch = indexTs.match(/\[revit-mcp-server\] v([0-9]+\.[0-9]+\.[0-9]+) connected to Revit/);
if (!startupLogMatch) {
  errors.push('Could not find startup log version in src/McpServer/src/index.ts');
} else if (startupLogMatch[1] !== expected) {
  errors.push(`index.ts startup log "v${startupLogMatch[1]}" != package.json "${expected}"`);
}

if (!csMatch) {
  errors.push('Could not find <Version> in src/RevitAddin/RevitMCPAddin.csproj');
} else if (csMatch[1] !== expected) {
  errors.push(`RevitMCPAddin.csproj <Version> "${csMatch[1]}" != package.json "${expected}"`);
}

if (!changelog.includes(`## [${expected}]`)) {
  errors.push(`CHANGELOG.md missing entry for [${expected}]`);
}

const readme = readFileSync(join(root, 'README.md'), 'utf8');
const readmeBadge = readme.match(/\*\*v(\d+\.\d+\.\d+)\*\*/);
if (!readmeBadge) {
  errors.push('README.md: could not find **vX.Y.Z** version badge');
} else if (readmeBadge[1] !== expected) {
  errors.push(`README.md badge "v${readmeBadge[1]}" != package.json "${expected}"`);
}
const readmeHealth = readme.match(/version\s*:\s*(\d+\.\d+\.\d+)/);
if (readmeHealth && readmeHealth[1] !== expected) {
  errors.push(`README.md health example version "${readmeHealth[1]}" != package.json "${expected}"`);
}

// -- Tool / command inventory consistency ------------------------------------
// Single source of truth: the actual code. We count the real declarations and
// fail if the docs (or the TS↔C# surfaces) disagree, so the counts can never
// silently drift again.
const registry = readFileSync(
  join(root, 'src/RevitMCP.Core/Commands/CommandRegistry.cs'), 'utf8');

// MCP tools = active server.tool("name", ...) calls in index.ts (allow digits, e.g. create_3d_view)
const toolCount = [...indexTs.matchAll(/server\.tool\(\s*"([a-z0-9_]+)"/g)].length;

// C# commands = Register(new XxxCommand()) calls in the registry
const registerCount = (registry.match(/Register\(new\s+\w+\(\)\)/g) || []).length;

// Hidden = tools implemented in C# but commented out of the MCP surface in index.ts
const hiddenCount = (indexTs.match(/^\s*\/\/\s*revit_[a-z0-9_]+.*hidden/gim) || []).length;

// Recipes (revit_recipe_*) are Node-only orchestration tools with no C# command,
// so they are excluded from the C#-parity invariant.
const recipeCount = [...indexTs.matchAll(/server\.tool\(\s*"revit_recipe_[a-z0-9_]+"/g)].length;

// Structural invariant: exposed C# commands (registered − hidden) + 1 batch transport
// tool + Node-only recipes must equal the number of MCP tools exposed in index.ts.
const expectedTools = registerCount - hiddenCount + 1 + recipeCount;
if (toolCount !== expectedTools) {
  errors.push(
    `Tool/command drift: index.ts exposes ${toolCount} MCP tools, but ` +
    `${registerCount} registered − ${hiddenCount} hidden + 1 batch + ${recipeCount} recipes = ${expectedTools}`);
}

// -- Doc claims must match reality - EVERY occurrence, not just the first -----
// The earlier version used .match() (first hit only) and only looked at README,
// so "64 revit_* tools", "80 tools", "81 commands" rotted in place across README,
// ARCHITECTURE and TROUBLESHOOTING while the gate stayed green. Scan them all.
//
// ROADMAP.md is exempt from the bare "N commands" rule on purpose: it is a
// historical milestone table ("| 60 commands | v0.3.0 | Done |") where old
// numbers are the point.
const docFiles = [
  ['README.md', readme],
  ['docs/ARCHITECTURE.md', readFileSync(join(root, 'docs/ARCHITECTURE.md'), 'utf8')],
  ['docs/TROUBLESHOOTING.md', readFileSync(join(root, 'docs/TROUBLESHOOTING.md'), 'utf8')],
  ['docs/COMMANDS.md', readFileSync(join(root, 'docs/COMMANDS.md'), 'utf8')],
  ['docs/API_COVERAGE.md', readFileSync(join(root, 'docs/API_COVERAGE.md'), 'utf8')],
  ['src/McpServer/src/index.ts', indexTs],
];

// Claims that must equal the live MCP tool count.
const TOOL_CLAIMS = [
  /(\d+)\s+MCP tools/g,          // "89 MCP tools"
  /expose all (\d+) tools/g,     // TROUBLESHOOTING profile note
  /(\d+)-tool surface/g,         // ARCHITECTURE profile note
  /see (\d+) `revit_\*` tools/g, // README verify step
  /With (\d+) tools,/g,          // index.ts profile comment
  /all (\d+) tools to every/g,   // README profile note
];
// Claims that must equal the registered C# command count.
const CMD_CLAIMS = [
  /(\d+)\s+C# commands/g,
  /dispatcher \+ (\d+) commands/g, // README repo-layout tree
  /# (\d+) commands, one file each/g, // ARCHITECTURE tree
];

let sawToolClaim = false;
let sawCmdClaim = false;
for (const [name, text] of docFiles) {
  for (const re of TOOL_CLAIMS) {
    for (const m of text.matchAll(re)) {
      sawToolClaim = true;
      if (Number(m[1]) !== toolCount)
        errors.push(`${name}: claims ${m[1]} tools ("${m[0].trim()}") but index.ts exposes ${toolCount}`);
    }
  }
  for (const re of CMD_CLAIMS) {
    for (const m of text.matchAll(re)) {
      sawCmdClaim = true;
      if (Number(m[1]) !== registerCount)
        errors.push(`${name}: claims ${m[1]} commands ("${m[0].trim()}") but the registry has ${registerCount}`);
    }
  }
}
if (!sawToolClaim) errors.push('No "N MCP tools" claim found in any doc - the count gate is not actually guarding anything');
if (!sawCmdClaim) errors.push('No "N C# commands" claim found in any doc - the count gate is not actually guarding anything');

// package-lock carries its own copy of the version and ships inside the release
// ZIP; it sat at 0.4.2 for eleven releases because nothing checked it.
const lock = JSON.parse(readFileSync(join(root, 'src/McpServer/package-lock.json'), 'utf8'));
if (lock.version !== expected)
  errors.push(`package-lock.json version "${lock.version}" != package.json "${expected}" (run: npm install --package-lock-only)`);

// Unreplaced template placeholders must never reach a release artifact.
for (const [name, text] of [
  ['scripts/build-release.ps1', readFileSync(join(root, 'scripts/build-release.ps1'), 'utf8')],
  ['scripts/install.ps1', readFileSync(join(root, 'scripts/install.ps1'), 'utf8')],
  ['README.md', readme],
]) {
  if (text.includes('your-org'))
    errors.push(`${name}: contains the placeholder "your-org" - replace with the real repo owner`);
}

if (errors.length > 0) {
  console.error('Consistency check failed:');
  for (const e of errors) console.error(`  - ${e}`);
  process.exit(1);
}

console.log(
  `All consistent: v${expected}, ${toolCount} MCP tools, ` +
  `${registerCount} C# commands (${hiddenCount} hidden), ${recipeCount} recipe(s).`);
