import { unwrap, PithyxApiError, type components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';

export type StorageDiskList = components['schemas']['StorageDiskList'];
export type StorageDisk = components['schemas']['StorageDisk'];
export type PoolList = components['schemas']['PoolList'];
export type Pool = components['schemas']['Pool'];
export type Vdev = components['schemas']['Vdev'];
export type PoolCreatePreviewRequest = components['schemas']['PoolCreatePreviewRequest'];
export type PoolCreatePreview = components['schemas']['PoolCreatePreview'];

/**
 * Administrative calls of the desktop's own core apps, outside the public SDK: not versioned, never served to a
 * sandboxed app (only CoreSdkApp passes them to `serveApp`), and the daemon checks the administrator right on every
 * request behind them.
 */
export interface InternalMethods {
  'internal.admin.storage.disks': { params: Record<string, never>; result: StorageDiskList };
  'internal.admin.storage.pools': { params: Record<string, never>; result: PoolList };
  'internal.admin.storage.preview': { params: PoolCreatePreviewRequest; result: PoolCreatePreview };
}

/** Sent to the app when pools or disks changed; the app reads them again. */
export const STORAGE_CHANGED = 'internal.admin.storage.changed';

type InternalName = keyof InternalMethods;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isPreviewRequest(value: unknown): boolean {
  return (
    isRecord(value) &&
    typeof value.name === 'string' &&
    typeof value.layout === 'string' &&
    Array.isArray(value.diskIds) &&
    value.diskIds.every((id) => typeof id === 'string') &&
    (value.confirmations === undefined ||
      value.confirmations === null ||
      (isRecord(value.confirmations) &&
        Object.values(value.confirmations).every((serial) => typeof serial === 'string')))
  );
}

async function daemon<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    throw error instanceof PithyxApiError
      ? new PithyxError({
          code: error.code,
          params: error.params,
          detail: error.message,
          requestId: error.requestId,
        })
      : error;
  }
}

/**
 * The host side of the storage calls, for the Settings app's window. `emit` reaches that window only; the returned
 * `close` stops following the storage topics.
 */
export function adminStorageHost(emit: (event: string) => void): {
  extensions: HostExtensions;
  close: () => void;
} {
  const changed = () => {
    emit(STORAGE_CHANGED);
  };
  const stops = [
    realtime.listen('storage/pools', changed),
    realtime.listen('storage/disks', changed),
  ];
  const extensions: Record<InternalName, HostExtensions[string]> = {
    'internal.admin.storage.disks': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/storage/disks'))),
    },
    'internal.admin.storage.pools': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/storage/pools'))),
    },
    'internal.admin.storage.preview': {
      validate: isPreviewRequest,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/storage/pools/preview', {
              body: params as PoolCreatePreviewRequest,
            }),
          ),
        ),
    },
  };
  return {
    extensions,
    close: () => {
      stops.forEach((stop) => {
        stop();
      });
    },
  };
}

/** Calls an internal method from a core app. */
export async function internalCall<M extends InternalName>(
  app: PithyxApp,
  method: M,
  params: InternalMethods[M]['params'],
): Promise<InternalMethods[M]['result']> {
  // The SDK's call is typed to the public methods; internal names only exist in this module.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as InternalMethods[M]['result'];
}

/** Listens for an internal event; returns the function that stops listening. */
export function onInternal(
  app: PithyxApp,
  event: typeof STORAGE_CHANGED,
  handler: () => void,
): () => void {
  return app.on(event as unknown as EventName, handler);
}
