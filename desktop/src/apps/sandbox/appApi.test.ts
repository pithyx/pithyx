import { afterEach, describe, expect, it, vi } from 'vitest';

/** The test runs in Node, where a Request needs an absolute address; the page's relative ones get the box's. */
async function loadApi() {
  const Native = Request;
  vi.stubGlobal(
    'Request',
    class extends Native {
      constructor(input: RequestInfo | URL, init?: RequestInit) {
        super(
          typeof input === 'string' && input.startsWith('/') ? `http://box${input}` : input,
          init,
        );
      }
    },
  );
  vi.resetModules();
  return (await import('./appApi')).appApiFor;
}

function problem(code: string, status: number): Response {
  return new Response(
    JSON.stringify({
      type: 'about:blank',
      title: code,
      status,
      detail: code,
      code,
      params: {},
      requestId: 'r1',
    }),
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

/** A daemon that forgot its tokens: only the token it issued last is valid. */
function daemonAfterRestart() {
  let issued = 0;
  const seen: string[] = [];
  const handler = vi.fn((input: Request | URL | string, init?: RequestInit) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url, 'http://box');
    if (url.pathname === '/api/apps/com.example.demo/tokens') {
      issued += 1;
      return Promise.resolve(
        new Response(
          JSON.stringify({
            token: `t${String(issued)}`,
            expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(),
          }),
          { headers: { 'Content-Type': 'application/json' } },
        ),
      );
    }
    seen.push(request.headers.get('Authorization') ?? '');
    return Promise.resolve(
      request.headers.get('Authorization') === 'Bearer t2'
        ? new Response(JSON.stringify({ keys: ['a'] }), {
            headers: { 'Content-Type': 'application/json' },
          })
        : problem('app.token.invalid', 401),
    );
  });
  return { handler, seen, issuedCount: () => issued };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the tokens of an app window', () => {
  it('asks again once when the daemon refuses the cached host token, for typed calls and raw ones', async () => {
    const daemon = daemonAfterRestart();
    vi.stubGlobal('fetch', daemon.handler);
    const api = (await loadApi())('com.example.demo', 'w1');
    // The first token was issued before the box restarted and is refused.
    await api.call((headers) =>
      fetch('/api/app/storage', { headers }).then(async (response) => ({
        data: response.ok ? ((await response.json()) as { keys: string[] }) : undefined,
        response,
        error: response.ok ? undefined : ((await response.json()) as unknown),
      })),
    );
    expect(daemon.seen).toEqual(['Bearer t1', 'Bearer t2']);
    expect(daemon.issuedCount()).toBe(2);

    const body = await api.raw('/api/app/storage');
    expect(await body.json()).toEqual({ keys: ['a'] });
    // The renewed token is kept; no third token is needed.
    expect(daemon.issuedCount()).toBe(2);
  });

  it('hands out a new backend token when asked to renew, and keeps the cached one otherwise', async () => {
    const daemon = daemonAfterRestart();
    vi.stubGlobal('fetch', daemon.handler);
    const api = (await loadApi())('com.example.demo', 'w1');
    expect((await api.backendToken()).token).toBe('t1');
    expect((await api.backendToken()).token).toBe('t1');
    expect((await api.backendToken(true)).token).toBe('t2');
    expect((await api.backendToken()).token).toBe('t2');
  });
});
