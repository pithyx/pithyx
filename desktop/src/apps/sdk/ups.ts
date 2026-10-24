import { unwrap, type components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import type { HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';
import { toPithyxError } from './daemonError';

export type UpsStatus = components['schemas']['UpsStatus'];
export type UpsSettingsRequest = components['schemas']['UpsSettingsRequest'];
export type UpsMode = components['schemas']['UpsMode'];

/** The query key of Settings > UPS. */
export const upsKey = ['settings', 'ups'] as const;

/** Every change of `system/ups`, the first one being its snapshot. */
export const UPS_STATE = 'internal.admin.ups.state';

/** The UPS calls of Settings, outside the public SDK; the daemon checks the administrator right on each. */
interface UpsMethods {
  'internal.admin.ups.get': { params: Record<string, never>; result: UpsStatus };
  'internal.admin.ups.set': { params: UpsSettingsRequest; result: UpsStatus };
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const optional = (value: unknown, type: 'string' | 'number') =>
  value === undefined || value === null || typeof value === type;

async function daemon<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    throw toPithyxError(error);
  }
}

/** The host side of Settings > UPS: the topic `system/ups` while the window is open. `close` stops following it. */
export function upsHost(emit: (event: string, data?: unknown) => void): {
  extensions: HostExtensions;
  close: () => void;
} {
  const stop = realtime.listen('system/ups', (data) => {
    emit(UPS_STATE, data);
  });
  const extensions: Record<keyof UpsMethods, HostExtensions[string]> = {
    'internal.admin.ups.get': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/system/ups'))),
    },
    'internal.admin.ups.set': {
      validate: (value) =>
        isRecord(value) &&
        (value.mode === 'off' || value.mode === 'usb' || value.mode === 'network') &&
        ['host', 'name', 'username', 'password'].every((key) => optional(value[key], 'string')) &&
        ['port', 'batteryLow', 'runtimeLow'].every((key) => optional(value[key], 'number')),
      handle: (params) =>
        daemon(async () =>
          unwrap(await api.PUT('/api/system/ups', { body: params as UpsSettingsRequest })),
        ),
    },
  };
  return { extensions, close: stop };
}

export async function upsCall<M extends keyof UpsMethods>(
  app: PithyxApp,
  method: M,
  params: UpsMethods[M]['params'],
): Promise<UpsMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as UpsMethods[M]['result'];
}

/** Follows `system/ups` in a page of Settings. */
export function onUps(app: PithyxApp, handler: (status: UpsStatus) => void): () => void {
  return app.on(UPS_STATE as unknown as EventName, (data) => {
    handler(data as unknown as UpsStatus);
  });
}
