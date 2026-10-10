import type { Events, EventName, FileRef } from '@pithyx/sdk';
import type { AppConnection } from '@pithyx/sdk/host';
import type { RefObject } from 'react';
import type { PendingFile } from './rules';

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

/** What turns a file a window is launched with into the app's handle for it; set by the window of a third-party app. */
const adopters = new Map<string, (file: PendingFile) => Promise<FileRef | undefined>>();

export function registerFileAdopter(
  windowId: string,
  adopt: (file: PendingFile) => Promise<FileRef | undefined>,
): () => void {
  adopters.set(windowId, adopt);
  return () => {
    if (adopters.get(windowId) === adopt) {
      adopters.delete(windowId);
    }
  };
}

/** Hands a file to an open window as `openFile`. False when the window cannot take it (no handle, or the app lacks the permission). */
export async function openFileIn(windowId: string, file: PendingFile): Promise<boolean> {
  const ref = await adopters.get(windowId)?.(file);
  if (ref === undefined) {
    return false;
  }
  emitTo(windowId, 'openFile', { file: ref });
  return true;
}
