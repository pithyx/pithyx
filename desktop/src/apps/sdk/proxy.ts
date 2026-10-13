import { PithyxApiError, unwrap, type components } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';

/** The query of Settings > Reverse proxy. */
export const proxyKey = ['settings', 'system', 'proxy'] as const;

export type ProxySettings = components['schemas']['ProxySettings'];
export type ProxyHost = components['schemas']['ProxyHost'];
export type ProxyHostRequest = components['schemas']['ProxyHostRequest'];
export type ProxyRedirect = components['schemas']['ProxyRedirect'];
export type ProxyRedirectRequest = components['schemas']['ProxyRedirectRequest'];
export type ProxyStream = components['schemas']['ProxyStream'];
export type ProxyStreamRequest = components['schemas']['ProxyStreamRequest'];
export type ProxyOptionsRequest = components['schemas']['ProxyOptionsRequest'];
export type ProxyTargetStatus = components['schemas']['ProxyTargetStatus'];
export type ProxyTargetTls = components['schemas']['ProxyTargetTls'];
export type ProxyHeader = components['schemas']['ProxyHeader'];

/** The reverse proxy for administrators, outside the public SDK; the daemon checks the right on every request. */
interface ProxyMethods {
  'internal.admin.proxy.settings': { params: Record<string, never>; result: ProxySettings };
  'internal.admin.proxy.setOptions': { params: ProxyOptionsRequest; result: ProxySettings };
  'internal.admin.proxy.saveHost': {
    params: { id?: number; host: ProxyHostRequest };
    result: ProxySettings;
  };
  'internal.admin.proxy.deleteHost': { params: { id: number }; result: ProxySettings };
  'internal.admin.proxy.checkHost': { params: { id: number }; result: ProxyTargetStatus };
  'internal.admin.proxy.saveRedirect': {
    params: { id?: number; redirect: ProxyRedirectRequest };
    result: ProxySettings;
  };
  'internal.admin.proxy.deleteRedirect': { params: { id: number }; result: ProxySettings };
  'internal.admin.proxy.saveStream': {
    params: { id?: number; stream: ProxyStreamRequest };
    result: ProxySettings;
  };
  'internal.admin.proxy.deleteStream': { params: { id: number }; result: ProxySettings };
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const hasId = (value: unknown): value is { id: number } =>
  isRecord(value) && typeof value.id === 'number';

const optionalId = (value: Record<string, unknown>) =>
  value.id === undefined || typeof value.id === 'number';

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

/** The host side of the Reverse proxy page of the Settings app. */
export function proxyHost(): HostExtensions {
  return {
    'internal.admin.proxy.settings': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/system/proxy'))),
    },
    'internal.admin.proxy.setOptions': {
      validate: (value) => isRecord(value) && typeof value.unknownHosts === 'string',
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.PUT('/api/system/proxy/options', { body: params as ProxyOptionsRequest }),
          ),
        ),
    },
    'internal.admin.proxy.saveHost': {
      validate: (value) => isRecord(value) && optionalId(value) && isRecord(value.host),
      handle: (params) =>
        daemon(async () => {
          const { id, host } = params as { id?: number; host: ProxyHostRequest };
          return unwrap(
            id === undefined
              ? await api.POST('/api/system/proxy/hosts', { body: host })
              : await api.PUT('/api/system/proxy/hosts/{id}', {
                  params: { path: { id } },
                  body: host,
                }),
          );
        }),
    },
    'internal.admin.proxy.deleteHost': {
      validate: hasId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.DELETE('/api/system/proxy/hosts/{id}', {
              params: { path: { id: (params as { id: number }).id } },
            }),
          ),
        ),
    },
    'internal.admin.proxy.checkHost': {
      validate: hasId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/system/proxy/hosts/{id}/check', {
              params: { path: { id: (params as { id: number }).id } },
            }),
          ),
        ),
    },
    'internal.admin.proxy.saveRedirect': {
      validate: (value) => isRecord(value) && optionalId(value) && isRecord(value.redirect),
      handle: (params) =>
        daemon(async () => {
          const { id, redirect } = params as { id?: number; redirect: ProxyRedirectRequest };
          return unwrap(
            id === undefined
              ? await api.POST('/api/system/proxy/redirects', { body: redirect })
              : await api.PUT('/api/system/proxy/redirects/{id}', {
                  params: { path: { id } },
                  body: redirect,
                }),
          );
        }),
    },
    'internal.admin.proxy.deleteRedirect': {
      validate: hasId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.DELETE('/api/system/proxy/redirects/{id}', {
              params: { path: { id: (params as { id: number }).id } },
            }),
          ),
        ),
    },
    'internal.admin.proxy.saveStream': {
      validate: (value) => isRecord(value) && optionalId(value) && isRecord(value.stream),
      handle: (params) =>
        daemon(async () => {
          const { id, stream } = params as { id?: number; stream: ProxyStreamRequest };
          return unwrap(
            id === undefined
              ? await api.POST('/api/system/proxy/streams', { body: stream })
              : await api.PUT('/api/system/proxy/streams/{id}', {
                  params: { path: { id } },
                  body: stream,
                }),
          );
        }),
    },
    'internal.admin.proxy.deleteStream': {
      validate: hasId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.DELETE('/api/system/proxy/streams/{id}', {
              params: { path: { id: (params as { id: number }).id } },
            }),
          ),
        ),
    },
  };
}

export async function proxyCall<M extends keyof ProxyMethods>(
  app: PithyxApp,
  method: M,
  params: ProxyMethods[M]['params'],
): Promise<ProxyMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as ProxyMethods[M]['result'];
}
