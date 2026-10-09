import { PithyxApiError, unwrap, type components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';

export type NetworkState = components['schemas']['NetworkState'];
export type NetworkConfig = components['schemas']['NetworkConfig'];
export type NetworkCheck = components['schemas']['NetworkCheck'];
export type PendingNetworkChange = components['schemas']['PendingNetworkChange'];

/** The query key of the network state in Settings. */
export const networkKey = ['settings', 'network'] as const;

/** Every change of `system/network`, the first one being its snapshot. */
export const NETWORK_STATE = 'internal.admin.network.state';

/** The network calls of Settings, outside the public SDK; the daemon checks the administrator right on each. */
interface NetworkMethods {
  'internal.admin.network.get': { params: Record<string, never>; result: NetworkState };
  'internal.admin.network.check': { params: NetworkConfig; result: NetworkCheck };
  'internal.admin.network.apply': { params: NetworkConfig; result: PendingNetworkChange };
  'internal.admin.network.confirm': { params: { id: string }; result: null };
  'internal.admin.network.revert': { params: { id: string }; result: null };
  'internal.admin.network.watch': { params: { watch: boolean }; result: null };
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const isConfig = (value: unknown): value is NetworkConfig =>
  isRecord(value) && Array.isArray(value.interfaces) && isRecord(value.global);

const isChange = (value: unknown): value is { id: string } =>
  isRecord(value) && typeof value.id === 'string';

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
 * The host side of Settings > Network. The live topic is followed only while the page watches it; closing the window
 * releases it.
 */
export function networkHost(emit: (event: string, data?: unknown) => void): {
  extensions: HostExtensions;
  close: () => void;
} {
  let release: (() => void) | null = null;
  const watch = (wanted: boolean) => {
    if (wanted && release === null) {
      release = realtime.listen('system/network', (data) => {
        emit(NETWORK_STATE, data);
      });
    } else if (!wanted && release !== null) {
      release();
      release = null;
    }
  };
  return {
    extensions: {
      'internal.admin.network.get': {
        validate: isRecord,
        handle: () => daemon(async () => unwrap(await api.GET('/api/system/network'))),
      },
      'internal.admin.network.check': {
        validate: isConfig,
        handle: (params) =>
          daemon(async () =>
            unwrap(await api.POST('/api/system/network/check', { body: params as NetworkConfig })),
          ),
      },
      'internal.admin.network.apply': {
        validate: isConfig,
        handle: (params) =>
          daemon(async () =>
            unwrap(await api.PUT('/api/system/network', { body: params as NetworkConfig })),
          ),
      },
      'internal.admin.network.confirm': {
        validate: isChange,
        handle: (params) =>
          daemon(async () => {
            unwrap(
              await api.POST('/api/system/network/changes/{changeId}/confirm', {
                params: { path: { changeId: (params as { id: string }).id } },
              }),
            );
            return null;
          }),
      },
      'internal.admin.network.revert': {
        validate: isChange,
        handle: (params) =>
          daemon(async () => {
            unwrap(
              await api.POST('/api/system/network/changes/{changeId}/revert', {
                params: { path: { changeId: (params as { id: string }).id } },
              }),
            );
            return null;
          }),
      },
      'internal.admin.network.watch': {
        validate: (value) => isRecord(value) && typeof value.watch === 'boolean',
        handle: (params) => {
          watch((params as { watch: boolean }).watch);
          return null;
        },
      },
    },
    close: () => {
      watch(false);
    },
  };
}

export async function networkCall<M extends keyof NetworkMethods>(
  app: PithyxApp,
  method: M,
  params: NetworkMethods[M]['params'],
): Promise<NetworkMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as NetworkMethods[M]['result'];
}

export function onNetwork(app: PithyxApp, handler: (state: NetworkState) => void): () => void {
  return app.on(NETWORK_STATE as unknown as EventName, handler as (data: unknown) => void);
}
