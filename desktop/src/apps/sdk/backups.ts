import { unwrap, type components } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import type { HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { daemon } from './daemonError';

export type BackupTarget = components['schemas']['BackupTarget'];
export type BackupTargetList = components['schemas']['BackupTargetList'];
export type BackupHostKey = components['schemas']['BackupHostKey'];
export type BackupTargetRequest = components['schemas']['BackupTargetRequest'];

/** The Backup app's calls, outside the public SDK; the daemon checks the administrator right on every request behind them. */
interface BackupMethods {
  'internal.admin.backups.targets': { params: Record<string, never>; result: BackupTargetList };
  /** The host keys the server offers, Ed25519 first; nothing is stored. */
  'internal.admin.backups.scan': {
    params: { host: string; port?: number };
    result: { keys: BackupHostKey[] };
  };
  /** Saves a target with the confirmed host key; the daemon makes its own SSH key. */
  'internal.admin.backups.create': { params: BackupTargetRequest; result: BackupTarget };
  'internal.admin.backups.update': {
    params: BackupTargetRequest & { id: number };
    result: BackupTarget;
  };
  /** Pins a new host key after the administrator compared its fingerprint. */
  'internal.admin.backups.setHostKey': {
    params: { id: number; hostKey: string };
    result: BackupTarget;
  };
  /** Signs in, creates the folder if missing, writes and removes a test file; the outcome is the target's `lastTest`. */
  'internal.admin.backups.test': { params: { id: number }; result: BackupTarget };
  'internal.admin.backups.delete': { params: { id: number }; result: null };
}

export const targetsKey = ['backups', 'targets'] as const;

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const hasId = (value: unknown): value is { id: number } =>
  isRecord(value) && Number.isSafeInteger(value.id);

const optionalString = (value: unknown) => value === undefined || typeof value === 'string';

const isPort = (value: unknown) => value === undefined || Number.isSafeInteger(value);

const isTarget = (value: unknown): value is BackupTargetRequest =>
  isRecord(value) &&
  typeof value.name === 'string' &&
  typeof value.host === 'string' &&
  typeof value.user === 'string' &&
  isPort(value.port) &&
  optionalString(value.path) &&
  optionalString(value.hostKey);

// The request without the id, and without fields left out, so the daemon sees what the administrator entered.
function body(params: BackupTargetRequest): BackupTargetRequest {
  return {
    name: params.name,
    host: params.host,
    user: params.user,
    ...(params.port === undefined ? {} : { port: params.port }),
    ...(params.path === undefined ? {} : { path: params.path }),
    ...(params.hostKey === undefined ? {} : { hostKey: params.hostKey }),
  };
}

/** The host side of the Backup app's window. */
export function backupsHost(): HostExtensions {
  return {
    'internal.admin.backups.targets': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/backups/targets'))),
    },
    'internal.admin.backups.scan': {
      validate: (value) => isRecord(value) && typeof value.host === 'string' && isPort(value.port),
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/backups/targets/host-keys', {
              body: params as BackupMethods['internal.admin.backups.scan']['params'],
            }),
          ),
        ),
    },
    'internal.admin.backups.create': {
      validate: isTarget,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/backups/targets', { body: body(params as BackupTargetRequest) }),
          ),
        ),
    },
    'internal.admin.backups.update': {
      validate: (value) => hasId(value) && isTarget(value),
      handle: (params) =>
        daemon(async () => {
          const target = params as BackupMethods['internal.admin.backups.update']['params'];
          return unwrap(
            await api.PUT('/api/backups/targets/{id}', {
              params: { path: { id: target.id } },
              body: body(target),
            }),
          );
        }),
    },
    'internal.admin.backups.setHostKey': {
      validate: (value) => isRecord(value) && typeof value.hostKey === 'string' && hasId(value),
      handle: (params) =>
        daemon(async () => {
          const { id, hostKey } =
            params as BackupMethods['internal.admin.backups.setHostKey']['params'];
          return unwrap(
            await api.PUT('/api/backups/targets/{id}/host-key', {
              params: { path: { id } },
              body: { hostKey },
            }),
          );
        }),
    },
    'internal.admin.backups.test': {
      validate: hasId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/backups/targets/{id}/test', {
              params: { path: { id: (params as { id: number }).id } },
            }),
          ),
        ),
    },
    'internal.admin.backups.delete': {
      validate: hasId,
      handle: (params) =>
        daemon(async () => {
          unwrap(
            await api.DELETE('/api/backups/targets/{id}', {
              params: { path: { id: (params as { id: number }).id } },
            }),
          );
          return null;
        }),
    },
  };
}

/** Calls one of the Backup app's methods through the SDK. */
export async function backupsCall<M extends keyof BackupMethods>(
  app: PithyxApp,
  method: M,
  params: BackupMethods[M]['params'],
): Promise<BackupMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as BackupMethods[M]['result'];
}
