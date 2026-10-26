import { BoxUnreachableError, PithyxApiError } from '@pithyx/api-client';
import { PithyxError } from '@pithyx/sdk/host';
import { describe, expect, it } from 'vitest';
import { daemon } from './daemonError';

const fail = (error: unknown) => daemon(() => Promise.reject(error));

describe('daemon', () => {
  it('reports a box that cannot be reached with its own code, not as a server error', async () => {
    await expect(fail(new BoxUnreachableError())).rejects.toMatchObject({
      code: 'sdk.box.unreachable',
      params: {},
    });
  });

  it('hands a daemon problem on with its code and request id', async () => {
    const problem = new PithyxApiError(404, {
      type: 'about:blank',
      title: 'Not Found',
      status: 404,
      detail: 'No such pool',
      code: 'storage.pool.not_found',
      params: { pool: 'tank' },
      requestId: 'abc',
    });
    const failure: unknown = await fail(problem).catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(PithyxError);
    expect(failure).toMatchObject({ code: 'storage.pool.not_found', requestId: 'abc' });
  });

  it('leaves an error of the handler itself unchanged', async () => {
    const own = new PithyxError({ code: 'sdk.request.invalid', params: {} });
    await expect(fail(own)).rejects.toBe(own);
  });
});
