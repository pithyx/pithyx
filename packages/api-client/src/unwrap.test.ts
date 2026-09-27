import { describe, expect, it } from 'vitest';
import { unwrap, PithyxApiError } from './index';

function response(status: number, headers: Record<string, string> = {}): Response {
  return new Response(status === 204 ? null : '{}', { status, headers });
}

describe('unwrap', () => {
  it('returns the data of a successful response', () => {
    expect(unwrap({ data: { ok: true }, response: response(200) })).toEqual({ ok: true });
  });

  it('accepts a successful response without a body', () => {
    expect(unwrap({ response: response(204) })).toBeUndefined();
  });

  it('turns problem details into a PithyxApiError', () => {
    const error = {
      type: 'about:blank',
      title: 'Unauthorized',
      status: 401,
      detail: 'The session has expired.',
      code: 'auth.session.expired',
      params: {},
      requestId: 'abc',
    };

    expect(() => unwrap({ error, response: response(401) })).toThrow(PithyxApiError);
    try {
      unwrap({ error, response: response(401) });
    } catch (thrown) {
      expect(thrown).toMatchObject({ code: 'auth.session.expired', requestId: 'abc', status: 401 });
    }
  });

  it('reports a failure without problem details with the request id header', () => {
    expect(() =>
      unwrap({ error: 'Bad Gateway', response: response(502, { 'X-Request-Id': 'r1' }) }),
    ).toThrow(
      expect.objectContaining({ code: 'system.internal.unexpected', requestId: 'r1' }) as Error,
    );
  });
});
