/**
 * Unit tests for revitClient.ts.
 *
 * Because AUTH_TOKEN and BASE_URL are evaluated at module-import time,
 * every test that needs different env values must call vi.resetModules()
 * then dynamically import the module so those constants are re-evaluated.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Helper — build a minimal Response-like mock.
function mockFetch(body: unknown, status = 200) {
  return vi.fn().mockResolvedValueOnce({
    status,
    text: () => Promise.resolve(typeof body === 'string' ? body : JSON.stringify(body)),
  } as unknown as Response);
}

// ── envelopeToToolResult ──────────────────────────────────────────────────────

describe('envelopeToToolResult', () => {
  it('returns isError=false and text content for ok response', async () => {
    const { envelopeToToolResult } = await import('../revitClient.js');
    const result = envelopeToToolResult({ ok: true, data: { foo: 'bar' } });
    expect(result.isError).toBe(false);
    expect(result.content).toHaveLength(1);
    expect(result.content[0].type).toBe('text');
    const parsed = JSON.parse(result.content[0].text);
    expect(parsed.ok).toBe(true);
  });

  it('returns isError=true for error response', async () => {
    const { envelopeToToolResult } = await import('../revitClient.js');
    const result = envelopeToToolResult({
      ok: false,
      error: { code: 'not_found', message: 'missing' },
    });
    expect(result.isError).toBe(true);
  });
});

// ── callRevit body construction ───────────────────────────────────────────────

describe('callRevit', () => {
  beforeEach(() => {
    vi.stubEnv('REVIT_MCP_AUTH', 'false');
    vi.resetModules();
  });
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('POST to /mcp with command and params', async () => {
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('ping', { id: 1 });
    const [url, opts] = vi.mocked(global.fetch).mock.calls[0];
    expect(String(url)).toMatch(/\/mcp$/);
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.command).toBe('ping');
    expect(body.params).toEqual({ id: 1 });
  });

  it('includes dryRun=true in body when requested', async () => {
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('create_wall', {}, true);
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.dryRun).toBe(true);
  });

  it('omits dryRun from body when false', async () => {
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('ping', {}, false);
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.dryRun).toBeUndefined();
  });
});

// ── callRevitBatch body construction ──────────────────────────────────────────

describe('callRevitBatch', () => {
  beforeEach(() => {
    vi.stubEnv('REVIT_MCP_AUTH', 'false');
    vi.resetModules();
  });
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('POST to /mcp/batch with steps and stopOnError', async () => {
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevitBatch } = await import('../revitClient.js');
    await callRevitBatch([{ command: 'ping', params: {} }]);
    const [url, opts] = vi.mocked(global.fetch).mock.calls[0];
    expect(String(url)).toMatch(/\/mcp\/batch$/);
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.steps).toHaveLength(1);
    expect(body.steps[0].command).toBe('ping');
    expect(body.stopOnError).toBe(true);
  });

  it('passes stopOnError=false when specified', async () => {
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevitBatch } = await import('../revitClient.js');
    await callRevitBatch([{ command: 'ping' }], false);
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.stopOnError).toBe(false);
  });
});

// ── error handling ────────────────────────────────────────────────────────────

describe('postJson error handling', () => {
  beforeEach(() => {
    vi.stubEnv('REVIT_MCP_AUTH', 'false');
    vi.resetModules();
  });
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('returns bad_response for non-JSON body', async () => {
    vi.stubGlobal('fetch', mockFetch('not json at all', 200));
    const { callRevit } = await import('../revitClient.js');
    const result = await callRevit('ping', {});
    expect(result.ok).toBe(false);
    expect(result.error?.code).toBe('bad_response');
  });

  it('returns transport_error on network failure', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValueOnce(new Error('ECONNREFUSED')));
    const { callRevit } = await import('../revitClient.js');
    const result = await callRevit('ping', {});
    expect(result.ok).toBe(false);
    expect(result.error?.code).toBe('transport_error');
  });

  it('returns timeout code on AbortError', async () => {
    const err = Object.assign(new Error('The operation was aborted'), { name: 'AbortError' });
    vi.stubGlobal('fetch', vi.fn().mockRejectedValueOnce(err));
    const { callRevit } = await import('../revitClient.js');
    const result = await callRevit('ping', {});
    expect(result.ok).toBe(false);
    expect(result.error?.code).toBe('timeout');
  });
});

// ── auth header ───────────────────────────────────────────────────────────────

describe('auth header', () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('includes Bearer token when REVIT_MCP_AUTH_TOKEN is set', async () => {
    vi.stubEnv('REVIT_MCP_AUTH_TOKEN', 'secret-token-xyz');
    vi.resetModules();
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('ping', {});
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const headers = (opts as RequestInit).headers as Record<string, string>;
    expect(headers['authorization']).toBe('Bearer secret-token-xyz');
  });

  it('omits auth header when REVIT_MCP_AUTH=false', async () => {
    vi.stubEnv('REVIT_MCP_AUTH', 'false');
    vi.resetModules();
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('ping', {});
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const headers = (opts as RequestInit).headers as Record<string, string>;
    expect(headers['authorization']).toBeUndefined();
  });

  it('always includes content-type header', async () => {
    vi.stubEnv('REVIT_MCP_AUTH', 'false');
    vi.resetModules();
    vi.stubGlobal('fetch', mockFetch({ ok: true }));
    const { callRevit } = await import('../revitClient.js');
    await callRevit('ping', {});
    const [, opts] = vi.mocked(global.fetch).mock.calls[0];
    const headers = (opts as RequestInit).headers as Record<string, string>;
    expect(headers['content-type']).toBe('application/json');
  });
});
