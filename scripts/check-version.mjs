#!/usr/bin/env node
/**
 * Verifies version strings in package.json, index.ts, McpHttpServer.cs, and
 * CHANGELOG.md all agree.  Exits non-zero on any mismatch.
 */
import { readFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

const pkg = JSON.parse(readFileSync(join(root, 'src/McpServer/package.json'), 'utf8'));
const expected = pkg.version;

const indexTs = readFileSync(join(root, 'src/McpServer/src/index.ts'), 'utf8');
const tsMatch = indexTs.match(/new McpServer\(\s*\{[^}]*version:\s*"([^"]+)"/s);

const httpServer = readFileSync(join(root, 'src/RevitAddin/Server/McpHttpServer.cs'), 'utf8');
const csMatch = httpServer.match(/\["version"\]\s*=\s*"([^"]+)"/);

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
  errors.push('Could not find version in src/RevitAddin/Server/McpHttpServer.cs');
} else if (csMatch[1] !== expected) {
  errors.push(`McpHttpServer.cs version "${csMatch[1]}" != package.json "${expected}"`);
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

// ── Tool / command inventory consistency ────────────────────────────────────
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

// README headline claims must match reality.
const readmeTools = readme.match(/(\d+)\s+MCP tools/);
if (!readmeTools) {
  errors.push('README.md: could not find an "N MCP tools" claim');
} else if (Number(readmeTools[1]) !== toolCount) {
  errors.push(`README.md claims ${readmeTools[1]} MCP tools but index.ts exposes ${toolCount}`);
}

const readmeCmds = readme.match(/(\d+)\s+C# commands/);
if (!readmeCmds) {
  errors.push('README.md: could not find an "N C# commands" claim');
} else if (Number(readmeCmds[1]) !== registerCount) {
  errors.push(`README.md claims ${readmeCmds[1]} C# commands but the registry has ${registerCount}`);
}

if (errors.length > 0) {
  console.error('Consistency check failed:');
  for (const e of errors) console.error(`  - ${e}`);
  process.exit(1);
}

console.log(
  `All consistent: v${expected}, ${toolCount} MCP tools, ` +
  `${registerCount} C# commands (${hiddenCount} hidden), ${recipeCount} recipe(s).`);
