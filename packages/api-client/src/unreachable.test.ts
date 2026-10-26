import { afterEach, describe, expect, it, vi } from 'vitest';
import { BoxUnreachableError, createApiClient } from './index';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('a request that does not reach the box', () => {
  it('rejects with sdk.box.unreachable when fetch fails', async () => {
    vi.stubGlobal('fetch', () => Promise.reject(new TypeError('Failed to fetch')));
    const api = createApiClient('http://box');
    const failure: unknown = await api.GET('/api/storage/pools').catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(BoxUnreachableError);
    expect(failure).toMatchObject({ code: 'sdk.box.unreachable', params: {} });
  });

  it('keeps an aborted request an AbortError', async () => {
    vi.stubGlobal('fetch', () => Promise.reject(new DOMException('aborted', 'AbortError')));
    const api = createApiClient('http://box');
    const failure: unknown = await api.GET('/api/storage/pools').catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(DOMException);
    expect(failure).toMatchObject({ name: 'AbortError' });
  });
});
