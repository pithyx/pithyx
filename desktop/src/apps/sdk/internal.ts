import { unwrap, type components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import { type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';
import { openApp } from '../../wm/store';
import { coreAppIds } from '../ids';
import { daemon } from './daemonError';

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
export type ShareGrants = components['schemas']['ShareGrants'];
export type SubjectGrant = components['schemas']['SubjectGrant'];
export type ShareAccess = components['schemas']['ShareAccess'];
export type SharePlanRequest = components['schemas']['SharePlanRequest'];
export type NewPoolKey = components['schemas']['NewPoolKey'];
export type UsbStick = components['schemas']['UsbStick'];

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
  'internal.admin.storage.snapshots.hold': { params: SnapshotRef; result: undefined };
  'internal.admin.storage.snapshots.release': { params: SnapshotRef; result: undefined };
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
  /** A new pool key (D384); the box keeps no copy. */
  'internal.admin.storage.newKey': { params: Record<string, never>; result: NewPoolKey };
  /** The key from the vault again, after the password and the second factor (D384). */
  'internal.admin.storage.revealKey': {
    params: { pool: string; password: string; totpCode?: string };
    result: NewPoolKey;
  };
  'internal.admin.storage.unlock': {
    params: { pool: string; key?: string; keepInVault?: boolean };
    result: null;
  };
  'internal.admin.storage.changeKey': {
    params: { pool: string; key: string; keepInVault: boolean };
    result: null;
  };
  'internal.admin.storage.forgetKey': { params: { pool: string }; result: null };
  /** The USB sticks a pool key can be written to (D385). */
  'internal.admin.storage.usbSticks': { params: Record<string, never>; result: UsbStick[] };
  /** Writes the typed key, or the vault's after the password and the second factor, to a USB stick (D385). */
  'internal.admin.storage.writeKeyToUsb': {
    params: {
      pool: string;
      device: string;
      key?: string;
      password?: string;
      totpCode?: string;
      removeFromVault: boolean;
    };
    result: null;
  };
  'internal.admin.storage.schedules': {
    params: Record<string, never>;
    result: StorageScheduleList;
  };
  'internal.admin.storage.schedule': { params: StorageScheduleUpdate; result: StorageScheduleList };
  'internal.admin.openApp': { params: { app: AdminApp }; result: boolean };
  'internal.admin.shares.list': { params: Record<string, never>; result: ShareDetails[] };
  'internal.admin.shares.grants': { params: { shareId: number }; result: ShareGrants };
  'internal.admin.shares.setGrants': {
    params: { shareId: number; grants: SubjectGrant[] };
    result: ShareGrants;
  };
  'internal.admin.shares.update': {
    params: {
      shareId: number;
      name?: string;
      description?: string;
      smb?: boolean;
      sftp?: boolean;
      smbEncrypt?: boolean;
    };
    result: ShareDetails;
  };
  /** Plans creating or deleting a shared folder; the plan runs with `internal.admin.storage.run`. */
  'internal.admin.shares.plan': { params: SharePlanRequest; result: StoragePlanPreview };
  /** Switches a shared folder's daily restore points (kept 7 days) on or off. */
  'internal.admin.shares.restorePoints': {
    params: { shareId: number; enabled: boolean };
    result: null;
  };
}

/** Administrator apps another core app may open, for example Settings opening Storage. */
const adminApps = [coreAppIds.storage, coreAppIds.settings] as const;
export type AdminApp = (typeof adminApps)[number];

/** Sent to the app when pools or disks changed; the app reads them again. */
export const STORAGE_CHANGED = 'internal.admin.storage.changed';

/** Sent to the app with the datasets whose snapshots changed (the `storage/snapshots` topic). */
export const SNAPSHOTS_CHANGED = 'internal.admin.storage.snapshots.changed';

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
    optionalText(value.encryptionKey) &&
    (value.keepKeyInVault === undefined ||
      value.keepKeyInVault === null ||
      typeof value.keepKeyInVault === 'boolean') &&
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
  path: '/api/storage/snapshots/hold' | '/api/storage/snapshots/release',
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

function isShareGrants(value: unknown): boolean {
  return (
    isShareId(value) &&
    Array.isArray(value.grants) &&
    value.grants.every(
      (grant: unknown) =>
        isRecord(grant) &&
        (grant.subjectKind === 'user' || grant.subjectKind === 'group') &&
        Number.isSafeInteger(grant.subjectId) &&
        (grant.level === 'read' || grant.level === 'readWrite'),
    )
  );
}

function isShareUpdate(value: unknown): boolean {
  return (
    isShareId(value) &&
    (value.name === undefined || typeof value.name === 'string') &&
    (value.description === undefined || typeof value.description === 'string') &&
    [value.smb, value.sftp, value.smbEncrypt, value.webDav, value.ftp].every(
      (flag) => flag === undefined || typeof flag === 'boolean',
    )
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
  let snapshotsRevision: number | undefined;
  const stops = [
    realtime.listen('storage/pools', changed),
    realtime.listen('storage/disks', changed),
    realtime.listen('storage/alerts', changed),
    realtime.listen('storage/snapshots', (data) => {
      const { revision, datasets } = data as components['schemas']['SnapshotChanges'];
      if (snapshotsRevision !== undefined && revision !== snapshotsRevision) {
        emit(SNAPSHOTS_CHANGED, datasets);
      }
      snapshotsRevision = revision;
    }),
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
    'internal.admin.storage.snapshots.hold': snapshotAction('/api/storage/snapshots/hold'),
    'internal.admin.storage.snapshots.release': snapshotAction('/api/storage/snapshots/release'),
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
    'internal.admin.storage.newKey': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.POST('/api/storage/encryption/keys'))),
    },
    'internal.admin.storage.revealKey': {
      validate: (params) =>
        isRecord(params) &&
        typeof params.pool === 'string' &&
        typeof params.password === 'string' &&
        (params.totpCode === undefined || typeof params.totpCode === 'string'),
      handle: (params) =>
        daemon(async () => {
          const { pool, password, totpCode } =
            params as InternalMethods['internal.admin.storage.revealKey']['params'];
          return unwrap(
            await api.POST('/api/storage/pools/{pool}/key/reveal', {
              params: { path: { pool } },
              body: { password, totpCode: totpCode ?? null },
            }),
          );
        }),
    },
    'internal.admin.storage.unlock': {
      validate: (params) =>
        isRecord(params) &&
        typeof params.pool === 'string' &&
        (params.key === undefined || typeof params.key === 'string') &&
        (params.keepInVault === undefined || typeof params.keepInVault === 'boolean'),
      handle: (params) =>
        daemon(async () => {
          const { pool, key, keepInVault } =
            params as InternalMethods['internal.admin.storage.unlock']['params'];
          unwrap(
            await api.POST('/api/storage/pools/{pool}/unlock', {
              params: { path: { pool } },
              body: { key: key ?? null, keepInVault: keepInVault ?? false },
            }),
          );
          return null;
        }),
    },
    'internal.admin.storage.changeKey': {
      validate: (params) =>
        isRecord(params) &&
        typeof params.pool === 'string' &&
        typeof params.key === 'string' &&
        typeof params.keepInVault === 'boolean',
      handle: (params) =>
        daemon(async () => {
          const { pool, key, keepInVault } =
            params as InternalMethods['internal.admin.storage.changeKey']['params'];
          unwrap(
            await api.POST('/api/storage/pools/{pool}/key', {
              params: { path: { pool } },
              body: { key, keepInVault },
            }),
          );
          return null;
        }),
    },
    'internal.admin.storage.forgetKey': {
      validate: (params) => isRecord(params) && typeof params.pool === 'string',
      handle: (params) =>
        daemon(async () => {
          const { pool } = params as InternalMethods['internal.admin.storage.forgetKey']['params'];
          unwrap(
            await api.DELETE('/api/storage/pools/{pool}/key/vault', { params: { path: { pool } } }),
          );
          return null;
        }),
    },
    'internal.admin.storage.usbSticks': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/storage/usb-sticks')).sticks),
    },
    'internal.admin.storage.writeKeyToUsb': {
      validate: (params) =>
        isRecord(params) &&
        typeof params.pool === 'string' &&
        typeof params.device === 'string' &&
        (params.key === undefined || typeof params.key === 'string') &&
        (params.password === undefined || typeof params.password === 'string') &&
        (params.totpCode === undefined || typeof params.totpCode === 'string') &&
        typeof params.removeFromVault === 'boolean',
      handle: (params) =>
        daemon(async () => {
          const { pool, device, key, password, totpCode, removeFromVault } =
            params as InternalMethods['internal.admin.storage.writeKeyToUsb']['params'];
          unwrap(
            await api.POST('/api/storage/pools/{pool}/key/usb', {
              params: { path: { pool } },
              body: {
                device,
                key: key ?? null,
                password: password ?? null,
                totpCode: totpCode ?? null,
                removeFromVault,
              },
            }),
          );
          return null;
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
    'internal.admin.shares.grants': {
      validate: isShareId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.GET('/api/shares/{shareId}/grants', {
              params: { path: { shareId: (params as { shareId: number }).shareId } },
            }),
          ),
        ),
    },
    'internal.admin.shares.setGrants': {
      validate: isShareGrants,
      handle: (params) =>
        daemon(async () => {
          const { shareId, grants } =
            params as InternalMethods['internal.admin.shares.setGrants']['params'];
          return unwrap(
            await api.PUT('/api/shares/{shareId}/grants', {
              params: { path: { shareId } },
              body: { grants },
            }),
          );
        }),
    },
    'internal.admin.shares.restorePoints': {
      validate: (value) =>
        isRecord(value) &&
        Number.isSafeInteger(value.shareId) &&
        typeof value.enabled === 'boolean',
      handle: (params) =>
        daemon(async () => {
          const { shareId, enabled } =
            params as InternalMethods['internal.admin.shares.restorePoints']['params'];
          unwrap(
            await api.PUT('/api/shares/{shareId}/restore-points', {
              params: { path: { shareId } },
              body: { enabled },
            }),
          );
          return null;
        }),
    },
    'internal.admin.shares.update': {
      validate: isShareUpdate,
      handle: (params) =>
        daemon(async () => {
          const { shareId, name, description, smb, sftp, smbEncrypt } =
            params as InternalMethods['internal.admin.shares.update']['params'];
          return unwrap(
            await api.PATCH('/api/shares/{shareId}', {
              params: { path: { shareId } },
              body: {
                ...(name === undefined ? {} : { name }),
                ...(description === undefined ? {} : { description }),
                ...(smb === undefined ? {} : { smb }),
                ...(sftp === undefined ? {} : { sftp }),
                ...(smbEncrypt === undefined ? {} : { smbEncrypt }),
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
  event:
    typeof STORAGE_CHANGED | typeof STORAGE_JOB | typeof SHARES_CHANGED | typeof SNAPSHOTS_CHANGED,
  handler: (data: unknown) => void,
): () => void {
  return app.on(event as unknown as EventName, handler);
}
