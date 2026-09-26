import { describe, expect, it } from 'vitest';
import { closeCodes } from './generated/close-codes';
import { closeAction } from './realtime';

describe('closeAction', () => {
  it.each([
    [closeCodes.DaemonRestart, 'reconnect'],
    [closeCodes.ClientTooSlow, 'reconnect'],
    [closeCodes.SessionEnded, 'login'],
    [closeCodes.Forbidden, 'stop'],
    [closeCodes.NormalClosure, 'none'],
    [1006, 'reconnect'],
  ])('maps %i to %s', (code, action) => {
    expect(closeAction(code)).toBe(action);
  });

  it('knows the codes from the daemon contract', () => {
    expect(closeCodes).toMatchObject({
      DaemonRestart: 1001,
      ClientTooSlow: 1008,
      SessionEnded: 4401,
      Forbidden: 4403,
    });
  });
});
