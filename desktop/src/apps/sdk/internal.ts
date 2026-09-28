import { unwrap, PithyxApiError, type components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';
import { openApp } from '../../wm/store';

export type StorageDiskList = components['schemas']['StorageDiskList'];
export type StorageDisk = components['schemas']['StorageDisk'];
export type PoolList = components['schemas']['PoolList'];
export type Pool = components['schemas']['Pool'];
export type Dataset = components['schemas']['Dataset'];
export type Vdev = components['schemas']['Vdev'];
export type StoragePlanRequest = components['schemas']['StoragePlanRequest'];
export type StoragePlanPreview = components['schemas']['StoragePlanPreview'];
export type Job = components['schemas']['Job'];
export type ImportScan = components['schemas']['ImportScan'];
export type ImportablePool = components['schemas']['ImportablePool'];
export type ImportVdev = components['schemas']['ImportVdev'];
export type Snapshot = components['schemas']['Snapshot'];
export type SnapshotList = components['schemas']['SnapshotList'];
export type SnapshotScheduleList = components['schemas']['SnapshotScheduleList'];
export type SnapshotRollbackRequest = components['schemas']['SnapshotRollbackRequest'];

interface SnapshotRef {
  dataset: string;
  name: string;
}
export type StorageHealth = components['schemas']['StorageHealth'];
export type StorageAlert = components['schemas']['StorageAlert'];
export type SmartReport = components['schemas']['SmartReport'];
export type StorageScheduleList = components['schemas']['StorageScheduleList'];
export type StorageSchedule = components['schemas']['StorageSchedule'];
export type StorageScheduleUpdate = components['schemas']['StorageScheduleUpdate'];
export type ScrubAction = 'start' | 'pause' | 'stop';
export type ShareDetails = components['schemas']['ShareDetails'];
export type ShareUserAccess = components['schemas']['ShareUserAccess'];
export type ShareAccess = components['schemas']['ShareAccess'];
export type SharePlanRequest = components['schemas']['SharePlanRequest'];

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
  'internal.admin.storage.importScan': { params: Record<string, never>; result: ImportScan };
  'internal.admin.storage.snapshots.list': { params: { dataset: string }; result: SnapshotList };
  'internal.admin.storage.snapshots.create': { params: { dataset: string }; result: Snapshot };
  'internal.admin.storage.snapshots.delete': { params: SnapshotRef; result: undefined };
  'internal.admin.storage.snapshots.hold': { params: SnapshotRef; result: undefined };
  'internal.admin.storage.snapshots.release': { params: SnapshotRef; result: undefined };
  'internal.admin.storage.snapshots.rollback': {
    params: SnapshotRollbackRequest;
    result: undefined;
  };
  'internal.admin.storage.snapshots.schedules': {
    params: { dataset: string };
    result: SnapshotScheduleList;
  };
  'internal.admin.storage.snapshots.setSchedules': {
    params: SnapshotScheduleList;
    result: SnapshotScheduleList;
  };
  'internal.admin.storage.health': { params: Record<string, never>; result: StorageHealth };
  'internal.admin.storage.mute': { params: { key: string; muted: boolean }; result: StorageHealth };
  'internal.admin.storage.scrub': { params: { pool: string; action: ScrubAction }; result: null };
  'internal.admin.storage.schedules': {
    params: Record<string, never>;
    result: StorageScheduleList;
  };
  'internal.admin.storage.schedule': { params: StorageScheduleUpdate; result: StorageScheduleList };
  'internal.admin.openApp': { params: { app: AdminApp }; result: boolean };
  'internal.admin.shares.list': { params: Record<string, never>; result: ShareDetails[] };
  'internal.admin.shares.access': { params: { shareId: number }; result: ShareUserAccess[] };
  'internal.admin.shares.setAccess': {
    params: { shareId: number; userId: number; access: ShareAccess };
    result: null;
  };
  'internal.admin.shares.update': {
    params: { shareId: number; name?: string; description?: string };
    result: ShareDetails;
  };
  /** Plans creating or deleting a shared folder; the plan runs with `internal.admin.storage.run`. */
  'internal.admin.shares.plan': { params: SharePlanRequest; result: StoragePlanPreview };
}

/** Administrator apps another core app may open, for example Settings opening Storage. */
const adminApps = ['storage', 'settings'] as const;
export type AdminApp = (typeof adminApps)[number];

/** Sent to the app when pools or disks changed; the app reads them again. */
export const STORAGE_CHANGED = 'internal.admin.storage.changed';

/** Sent to the app with every change of a job it started with `internal.admin.storage.run`. */
export const STORAGE_JOB = 'internal.admin.storage.job';

/** Sent to the app when the shares may have changed (the `shares/list` topic); the app reads them again. */
export const SHARES_CHANGED = 'internal.admin.shares.changed';

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
    optionalText(value.poolGuid) &&
    optionalText(value.newName) &&
    (value.force === undefined || value.force === null || typeof value.force === 'boolean') &&
    (value.diskIds === undefined ||
      value.diskIds === null ||
      (Array.isArray(value.diskIds) && value.diskIds.every((id) => typeof id === 'string'))) &&
    (value.confirmations === undefined ||
      value.confirmations === null ||
      (isRecord(value.confirmations) &&
        Object.values(value.confirmations).every((serial) => typeof serial === 'string')))
  );
}

function isDataset(value: unknown): value is Record<string, unknown> & { dataset: string } {
  return isRecord(value) && typeof value.dataset === 'string' && value.dataset !== '';
}

function isSnapshotRef(value: unknown): value is Record<string, unknown> & SnapshotRef {
  return isDataset(value) && typeof value.name === 'string' && value.name !== '';
}

function isRollback(value: unknown): boolean {
  return (
    isSnapshotRef(value) &&
    typeof value.confirmDataset === 'string' &&
    (value.discardNewer === undefined ||
      value.discardNewer === null ||
      (Array.isArray(value.discardNewer) &&
        value.discardNewer.every((name) => typeof name === 'string')))
  );
}

function isSchedules(value: unknown): boolean {
  return (
    isDataset(value) &&
    Array.isArray(value.schedules) &&
    value.schedules.every(
      (schedule) =>
        isRecord(schedule) &&
        typeof schedule.kind === 'string' &&
        typeof schedule.enabled === 'boolean' &&
        Number.isSafeInteger(schedule.keep),
    )
  );
}

/** A snapshot call that answers 204. */
function snapshotAction(
  path:
    | '/api/storage/snapshots/delete'
    | '/api/storage/snapshots/hold'
    | '/api/storage/snapshots/release',
): HostExtensions[string] {
  return {
    validate: isSnapshotRef,
    handle: (params) =>
      daemon(async () => {
        const { dataset, name } = params as SnapshotRef;
        unwrap(await api.POST(path, { body: { dataset, name } }));
      }),
  };
}

function isShareId(value: unknown): value is Record<string, unknown> & { shareId: number } {
  return isRecord(value) && Number.isSafeInteger(value.shareId);
}

const SHARE_ACCESS: readonly string[] = ['none', 'read', 'readWrite'];

function isShareAccess(value: unknown): boolean {
  return (
    isShareId(value) &&
    Number.isSafeInteger(value.userId) &&
    typeof value.access === 'string' &&
    SHARE_ACCESS.includes(value.access)
  );
}

function isShareUpdate(value: unknown): boolean {
  return (
    isShareId(value) &&
    (value.name === undefined || typeof value.name === 'string') &&
    (value.description === undefined || typeof value.description === 'string')
  );
}

function isSharePlan(value: unknown): boolean {
  const optionalText = (field: unknown) =>
    field === undefined || field === null || typeof field === 'string';
  return (
    isRecord(value) &&
    (value.action === 'share.create' || value.action === 'share.delete') &&
    optionalText(value.name) &&
    optionalText(value.pool) &&
    optionalText(value.description) &&
    optionalText(value.confirmName) &&
    (value.shareId === undefined ||
      value.shareId === null ||
      Number.isSafeInteger(value.shareId)) &&
    (value.data === undefined ||
      value.data === null ||
      value.data === 'keep' ||
      value.data === 'destroy')
  );
}

function isMuteRequest(value: unknown): boolean {
  return isRecord(value) && typeof value.key === 'string' && typeof value.muted === 'boolean';
}

function isScrubRequest(value: unknown): boolean {
  return (
    isRecord(value) &&
    typeof value.pool === 'string' &&
    (value.action === 'start' || value.action === 'pause' || value.action === 'stop')
  );
}

function isScheduleUpdate(value: unknown): boolean {
  return (
    isRecord(value) &&
    typeof value.kind === 'string' &&
    typeof value.target === 'string' &&
    typeof value.frequency === 'string'
  );
}

async function scrub(pool: string, action: ScrubAction): Promise<null> {
  const options = { params: { path: { pool } } };
  unwrap(
    action === 'start'
      ? await api.POST('/api/storage/pools/{pool}/scrub', options)
      : action === 'pause'
        ? await api.POST('/api/storage/pools/{pool}/scrub/pause', options)
        : await api.DELETE('/api/storage/pools/{pool}/scrub', options),
  );
  return null;
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
  // The first message is the snapshot the app has already read; later revisions are changes.
  let sharesRevision: number | undefined;
  const stops = [
    realtime.listen('storage/pools', changed),
    realtime.listen('storage/disks', changed),
    realtime.listen('storage/alerts', changed),
    realtime.listen('shares/list', (data) => {
      const { revision } = data as components['schemas']['ShareListRevision'];
      if (sharesRevision !== undefined && revision !== sharesRevision) {
        emit(SHARES_CHANGED);
      }
      sharesRevision = revision;
    }),
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
    'internal.admin.storage.importScan': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.POST('/api/storage/import/scan'))),
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
    'internal.admin.storage.snapshots.list': {
      validate: isDataset,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.GET('/api/storage/snapshots', {
              params: { query: { dataset: (params as { dataset: string }).dataset } },
            }),
          ),
        ),
    },
    'internal.admin.storage.snapshots.create': {
      validate: isDataset,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/storage/snapshots', {
              body: { dataset: (params as { dataset: string }).dataset },
            }),
          ),
        ),
    },
    'internal.admin.storage.snapshots.delete': snapshotAction('/api/storage/snapshots/delete'),
    'internal.admin.storage.snapshots.hold': snapshotAction('/api/storage/snapshots/hold'),
    'internal.admin.storage.snapshots.release': snapshotAction('/api/storage/snapshots/release'),
    'internal.admin.storage.snapshots.rollback': {
      validate: isRollback,
      handle: (params) =>
        daemon(async () => {
          unwrap(
            await api.POST('/api/storage/snapshots/rollback', {
              body: params as SnapshotRollbackRequest,
            }),
          );
        }),
    },
    'internal.admin.storage.snapshots.schedules': {
      validate: isDataset,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.GET('/api/storage/snapshots/schedules', {
              params: { query: { dataset: (params as { dataset: string }).dataset } },
            }),
          ),
        ),
    },
    'internal.admin.storage.snapshots.setSchedules': {
      validate: isSchedules,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.PUT('/api/storage/snapshots/schedules', {
              body: params as SnapshotScheduleList,
            }),
          ),
        ),
    },
    'internal.admin.storage.health': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/storage/alerts'))),
    },
    'internal.admin.storage.mute': {
      validate: isMuteRequest,
      handle: (params) =>
        daemon(async () => {
          const { key, muted } = params as InternalMethods['internal.admin.storage.mute']['params'];
          const options = { params: { path: { key } } };
          return unwrap(
            muted
              ? await api.POST('/api/storage/alerts/{key}/mute', options)
              : await api.DELETE('/api/storage/alerts/{key}/mute', options),
          );
        }),
    },
    'internal.admin.storage.scrub': {
      validate: isScrubRequest,
      handle: (params) =>
        daemon(() => {
          const { pool, action } =
            params as InternalMethods['internal.admin.storage.scrub']['params'];
          return scrub(pool, action);
        }),
    },
    'internal.admin.storage.schedules': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/storage/schedules'))),
    },
    'internal.admin.openApp': {
      validate: (params) => isRecord(params) && adminApps.some((known) => known === params.app),
      handle: (params) => Promise.resolve(openApp((params as { app: AdminApp }).app)),
    },
    'internal.admin.shares.list': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/shares/details'))),
    },
    'internal.admin.shares.access': {
      validate: isShareId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.GET('/api/shares/{shareId}/access', {
              params: { path: { shareId: (params as { shareId: number }).shareId } },
            }),
          ),
        ),
    },
    'internal.admin.shares.setAccess': {
      validate: isShareAccess,
      handle: (params) =>
        daemon(async () => {
          const { shareId, userId, access } =
            params as InternalMethods['internal.admin.shares.setAccess']['params'];
          unwrap(
            await api.PUT('/api/shares/{shareId}/access/{userId}', {
              params: { path: { shareId, userId } },
              body: { access },
            }),
          );
          return null;
        }),
    },
    'internal.admin.shares.update': {
      validate: isShareUpdate,
      handle: (params) =>
        daemon(async () => {
          const { shareId, name, description } =
            params as InternalMethods['internal.admin.shares.update']['params'];
          return unwrap(
            await api.PATCH('/api/shares/{shareId}', {
              params: { path: { shareId } },
              body: {
                ...(name === undefined ? {} : { name }),
                ...(description === undefined ? {} : { description }),
              },
            }),
          );
        }),
    },
    'internal.admin.shares.plan': {
      validate: isSharePlan,
      handle: (params) =>
        daemon(async () =>
          unwrap(await api.POST('/api/shares/plans', { body: params as SharePlanRequest })),
        ),
    },
    'internal.admin.storage.schedule': {
      validate: isScheduleUpdate,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.PUT('/api/storage/schedules', { body: params as StorageScheduleUpdate }),
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
  event: typeof STORAGE_CHANGED | typeof STORAGE_JOB | typeof SHARES_CHANGED,
  handler: (data: unknown) => void,
): () => void {
  return app.on(event as unknown as EventName, handler);
}
