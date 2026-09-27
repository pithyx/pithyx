import { describe, expect, it } from 'vitest';
import { isFault } from './describe';

describe('request ids in inline errors', () => {
  it('are for daemon faults, not for refusals such as a wrong password', () => {
    expect(
      isFault({ code: 'auth.credentials.invalid', params: {}, requestId: 'r', status: 401 }),
    ).toBe(false);
    expect(isFault({ code: 'fs.name.invalid', params: {}, requestId: 'r', status: 400 })).toBe(
      false,
    );
    expect(
      isFault({ code: 'system.internal.unexpected', params: {}, requestId: 'r', status: 500 }),
    ).toBe(true);
    expect(
      isFault({ code: 'storage.command.failed', params: {}, requestId: 'r', status: 502 }),
    ).toBe(true);
    expect(isFault({ code: 'system.internal.unexpected', params: {}, requestId: 'r' })).toBe(true);
    expect(isFault({ code: 'storage.pool.name_taken', params: {}, requestId: 'r' })).toBe(false);
  });
});
