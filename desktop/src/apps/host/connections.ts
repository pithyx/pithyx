import type { Events, EventName } from '@pithyx/sdk';
import type { AppConnection } from '@pithyx/sdk/host';
import type { RefObject } from 'react';

// The connections of the open app windows, for events the desktop sends outside a window's own lifecycle (a new
// launch, a notification action). `ready` once the app finished the handshake.
const windows = new Map<string, { connection: RefObject<AppConnection | null>; ready: boolean }>();

export function registerWindow(
  windowId: string,
  connection: RefObject<AppConnection | null>,
): () => void {
  const entry = { connection, ready: false };
  windows.set(windowId, entry);
  return () => {
    if (windows.get(windowId) === entry) {
      windows.delete(windowId);
    }
  };
}

/** Called with false when a window connects its app, and with true once the app finished the handshake. */
export function setReady(windowId: string, ready: boolean): void {
  const entry = windows.get(windowId);
  if (entry !== undefined) {
    entry.ready = ready;
  }
}

/** Whether the app of a window completed the handshake, so it can answer questions. */
export function isReady(windowId: string): boolean {
  return windows.get(windowId)?.ready === true;
}

export function emitTo<E extends EventName>(windowId: string, event: E, data: Events[E]): void {
  windows.get(windowId)?.connection.current?.emit(event, data);
}
