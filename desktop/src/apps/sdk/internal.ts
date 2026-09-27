import { unwrap, PithyxApiError, type components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';

export type StorageDiskList = components['schemas']['StorageDiskList'];
export type StorageDisk = components['schemas']['StorageDisk'];
export type PoolList = components['schemas']['PoolList'];
export type Pool = components['schemas']['Pool'];
export type Dataset = components['schemas']['Dataset'];
export type Vdev = components['schemas']['Vdev'];
export type StoragePlanRequest = components['schemas']['StoragePlanRequest'];
export type StoragePlanPreview = components['schemas']['StoragePlanPreview'];
export type Job = components['schemas']['Job'];

/**
 * Administrative calls of the desktop's own core apps, outside the public SDK: not versioned, never served to a
 * sandboxed app (only CoreSdkApp passes them to `serveApp`), and the daemon checks the administrator right on every
 * request behind them.
 */
export interface InternalMethods {
  'internal.admin.storage.disks': { params: Record<string, never>; result: StorageDiskList };
  'internal.admin.storage.pools': { params: Record<string, never>; result: PoolList };
  'internal.admin.storage.plan': { params: StoragePlanRequest; result: StoragePlanPreview };
  'internal.admin.storage.run': { params: { planId: string }; result: Job };
}

/** Sent to the app when pools or disks changed; the app reads them again. */
export const STORAGE_CHANGED = 'internal.admin.storage.changed';

/** Sent to the app with every change of a job it started with `internal.admin.storage.run`. */
export const STORAGE_JOB = 'internal.admin.storage.job';

type InternalName = keyof InternalMethods;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isPlanRequest(value: unknown): boolean {
  const optionalText = (field: unknown) =>
    field === undefined || field === null || typeof field === 'string';
  return (
    isRecord(value) &&
    typeof value.action === 'string' &&
    typeof value.pool === 'string' &&
    optionalText(value.layout) &&
    optionalText(value.vdev) &&
    optionalText(value.encryption) &&
    optionalText(value.confirmName) &&
    (value.diskIds === undefined ||
      value.diskIds === null ||
      (Array.isArray(value.diskIds) && value.diskIds.every((id) => typeof id === 'string'))) &&
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
export function adminStorageHost(emit: (event: string, data?: unknown) => void): {
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
  const follow = (job: Job) => {
    stops.push(
      realtime.listen(`jobs/${String(job.id)}`, (data) => {
        emit(STORAGE_JOB, data);
      }),
    );
  };
  const extensions: Record<InternalName, HostExtensions[string]> = {
    'internal.admin.storage.disks': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/storage/disks'))),
    },
    'internal.admin.storage.pools': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/storage/pools'))),
    },
    'internal.admin.storage.plan': {
      validate: isPlanRequest,
      handle: (params) =>
        daemon(async () =>
          unwrap(await api.POST('/api/storage/plans', { body: params as StoragePlanRequest })),
        ),
    },
    'internal.admin.storage.run': {
      validate: (params) => isRecord(params) && typeof params.planId === 'string',
      handle: (params) =>
        daemon(async () => {
          const job = unwrap(
            await api.POST('/api/storage/plans/{planId}/run', {
              params: { path: { planId: (params as { planId: string }).planId } },
            }),
          );
          follow(job);
          return job;
        }),
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
  event: typeof STORAGE_CHANGED | typeof STORAGE_JOB,
  handler: (data: unknown) => void,
): () => void {
  return app.on(event as unknown as EventName, handler);
}
