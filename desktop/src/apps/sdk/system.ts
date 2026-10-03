import { PithyxApiError, unwrap, type components } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queries';
import { applySystemRegion } from '../../i18n';
import type { QueryClient } from '@tanstack/react-query';

export type SystemOverview = components['schemas']['SystemOverview'];
export type NetworkInterfaceInfo = components['schemas']['NetworkInterfaceInfo'];
export type TimeSettings = components['schemas']['TimeSettings'];
export type TimeSettingsRequest = components['schemas']['TimeSettingsRequest'];
export type SystemRegion = components['schemas']['SystemRegion'];
export type FileServiceSettings = components['schemas']['FileServiceSettings'];
export type FileServiceSettingsRequest = components['schemas']['FileServiceSettingsRequest'];

/** System settings for administrators, outside the public SDK; the daemon checks the right on every request. */
interface SystemMethods {
  'internal.admin.system.overview': { params: Record<string, never>; result: SystemOverview };
  'internal.admin.system.network': {
    params: Record<string, never>;
    result: NetworkInterfaceInfo[];
  };
  'internal.admin.system.time': { params: Record<string, never>; result: TimeSettings };
  'internal.admin.system.setTime': { params: TimeSettingsRequest; result: TimeSettings };
  'internal.admin.system.region': { params: Record<string, never>; result: SystemRegion };
  'internal.admin.system.setRegion': { params: SystemRegion; result: SystemRegion };
  'internal.admin.system.fileServices': {
    params: Record<string, never>;
    result: FileServiceSettings;
  };
  'internal.admin.system.setFileServices': {
    params: FileServiceSettingsRequest;
    result: FileServiceSettings;
  };
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

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
 * The host side of the Info Center, Network and Regional settings pages of the Settings app. A saved region applies
 * to this desktop at once; other desktops pick it up on their next load.
 */
export function systemHost(queryClient: QueryClient): HostExtensions {
  return {
    'internal.admin.system.overview': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/system/overview'))),
    },
    'internal.admin.system.network': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/system/network'))),
    },
    'internal.admin.system.time': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/system/time'))),
    },
    'internal.admin.system.setTime': {
      validate: (value) =>
        isRecord(value) &&
        typeof value.timeZone === 'string' &&
        Array.isArray(value.ntpServers) &&
        value.ntpServers.every((server) => typeof server === 'string'),
      handle: (params) =>
        daemon(async () =>
          unwrap(await api.PUT('/api/system/time', { body: params as TimeSettingsRequest })),
        ),
    },
    'internal.admin.system.region': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/system/region'))),
    },
    'internal.admin.system.setRegion': {
      validate: (value) =>
        isRecord(value) &&
        typeof value.language === 'string' &&
        (value.region === null || typeof value.region === 'string'),
      handle: (params) =>
        daemon(async () => {
          const saved = unwrap(
            await api.PUT('/api/system/region', { body: params as SystemRegion }),
          );
          queryClient.setQueryData(queryKeys.setup, (setup: object | undefined) =>
            setup === undefined ? setup : { ...setup, ...saved },
          );
          await applySystemRegion(saved);
          return saved;
        }),
    },
    'internal.admin.system.fileServices': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/file-services'))),
    },
    'internal.admin.system.setFileServices': {
      validate: (value) =>
        isRecord(value) &&
        [
          value.smbEnabled,
          value.sftpEnabled,
          value.sshEnabled,
          value.webDavEnabled,
          value.ftpEnabled,
          value.ftpPlainAllowed,
        ].every((flag) => flag === undefined || flag === null || typeof flag === 'boolean') &&
        (value.workgroup === undefined ||
          value.workgroup === null ||
          typeof value.workgroup === 'string'),
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.PUT('/api/file-services', { body: params as FileServiceSettingsRequest }),
          ),
        ),
    },
  };
}

export async function systemCall<M extends keyof SystemMethods>(
  app: PithyxApp,
  method: M,
  params: SystemMethods[M]['params'],
): Promise<SystemMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as SystemMethods[M]['result'];
}
