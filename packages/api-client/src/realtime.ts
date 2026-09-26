import { closeCodes, type CloseCodeName } from './generated/close-codes';

/**
 * What the desktop does when the WebSocket closes:
 * - `reconnect`: connect again with exponential backoff and restore every subscription
 * - `login`: the session ended; show the login screen and do not reconnect
 * - `stop`: do not reconnect; the connection can never succeed as is
 * - `none`: the client closed the connection itself
 */
export type CloseAction = 'reconnect' | 'login' | 'stop' | 'none';

/** One entry per daemon close code. A new code in the daemon fails the build here until it has an action. */
export const closeActions: Record<CloseCodeName, CloseAction> = {
  NormalClosure: 'none',
  DaemonRestart: 'reconnect',
  ClientTooSlow: 'reconnect',
  MessageTooBig: 'reconnect',
  SessionEnded: 'login',
  Forbidden: 'stop',
};

const actionsByCode = new Map<number, CloseAction>(
  (Object.keys(closeCodes) as CloseCodeName[]).map((name) => [
    closeCodes[name],
    closeActions[name],
  ]),
);

/** Codes the daemon does not send, such as 1006 for a lost network connection, mean: try again. */
export function closeAction(code: number): CloseAction {
  return actionsByCode.get(code) ?? 'reconnect';
}
