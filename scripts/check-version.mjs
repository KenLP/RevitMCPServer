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

if (errors.length > 0) {
  console.error('Version inconsistencies found:');
  for (const e of errors) console.error(`  - ${e}`);
  process.exit(1);
}

console.log(`All version references consistent: ${expected}`);
