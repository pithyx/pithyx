import { unwrap, type components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';
import { endTransfer, reportProgress, startTransfer } from '../../shell/transfers';
import { openApp } from '../../wm/store';
import { toPithyxError } from './daemonError';

export type AdminAppList = components['schemas']['AdminAppList'];
export type AppDetails = components['schemas']['AppDetails'];
export type AppPackagePreview = components['schemas']['AppPackagePreview'];
export type AppAccess = components['schemas']['AppAccess'];
export type AppGrants = components['schemas']['AppGrants'];
export type AppStorageAccess = components['schemas']['AppStorageAccess'];
export type AppLogLevel = components['schemas']['AppLogLevel'];
export type SubjectGrant = components['schemas']['SubjectGrant'];
export type Job = components['schemas']['Job'];
export type PoolList = components['schemas']['PoolList'];
export type ShareDetails = components['schemas']['ShareDetails'];
export type StoreState = components['schemas']['StoreState'];
export type StoreSettings = components['schemas']['StoreSettings'];
export type CatalogInfo = components['schemas']['CatalogInfo'];
export type CatalogKind = components['schemas']['CatalogKind'];
export type StoreAppSummary = components['schemas']['StoreApp'];
export type StoreAppDetails = components['schemas']['StoreAppDetails'];
export type AppUpdate = components['schemas']['AppUpdate'];
export type UpdatePolicy = NonNullable<components['schemas']['UpdatePolicy']>;
export type StoreChannel = components['schemas']['StoreChannel'];

/** The Store's calls, outside the public SDK; the daemon checks the administrator right on every request behind them. */
interface StoreMethods {
  'internal.admin.apps.list': { params: Record<string, never>; result: AdminAppList };
  /** Uploads a package file with its progress in the taskbar; the daemon checks it and answers what installing does. */
  'internal.admin.apps.upload': { params: { file: Blob; name: string }; result: AppPackagePreview };
  'internal.admin.apps.discard': { params: { packageId: string }; result: null };
  'internal.admin.apps.install': {
    params: { packageId: string; pool?: string; access?: AppAccess };
    result: Job;
  };
  'internal.admin.apps.uninstall': {
    params: { appId: string; data: 'keep' | 'delete' };
    result: Job;
  };
  'internal.admin.apps.setEnabled': {
    params: { appId: string; enabled: boolean };
    result: AppDetails;
  };
  'internal.admin.apps.setPermission': {
    params: { appId: string; permission: string; granted: boolean };
    result: AppDetails;
  };
  'internal.admin.apps.mapStorage': {
    params: {
      appId: string;
      storageId: string;
      shareId: number;
      path: string;
      access: AppStorageAccess;
    };
    result: AppDetails;
  };
  'internal.admin.apps.unmapStorage': {
    params: { appId: string; storageId: string };
    result: AppDetails;
  };
  'internal.admin.apps.setLogLevel': {
    params: { appId: string; level: AppLogLevel };
    result: AppDetails;
  };
  'internal.admin.apps.grants': { params: { appId: string }; result: AppGrants };
  'internal.admin.apps.setGrants': {
    params: { appId: string; grants: SubjectGrant[] };
    result: AppGrants;
  };
  'internal.admin.apps.pools': { params: Record<string, never>; result: PoolList };
  'internal.admin.apps.shares': { params: Record<string, never>; result: ShareDetails[] };
  /** Opens an installed app for the administrator, as the menu would. */
  'internal.admin.apps.open': { params: { appId: string }; result: boolean };
  'internal.admin.apps.rollback': { params: { appId: string }; result: Job };
  'internal.admin.apps.setUpdatePolicy': {
    params: { appId: string; policy: UpdatePolicy | null };
    result: AppDetails;
  };
  'internal.admin.store.state': { params: Record<string, never>; result: StoreState };
  'internal.admin.store.setSettings': {
    params: { policy: UpdatePolicy; time: string; channel: StoreChannel };
    result: StoreSettings;
  };
  'internal.admin.store.check': { params: Record<string, never>; result: Job };
  'internal.admin.store.addCatalog': { params: { url: string }; result: CatalogInfo };
  'internal.admin.store.updateCatalog': {
    params: { key: number; enabled?: boolean; registryToken?: string };
    result: CatalogInfo;
  };
  'internal.admin.store.removeCatalog': { params: { key: number }; result: null };
  'internal.admin.store.syncCatalog': { params: { key: number }; result: Job };
  'internal.admin.store.search': {
    params: { catalog?: string; query?: string };
    result: { apps: StoreAppSummary[] };
  };
  'internal.admin.store.details': {
    params: { catalog: string; appId: string };
    result: StoreAppDetails;
  };
  /** Takes a catalog's version for the install dialog; it answers like an upload. */
  'internal.admin.store.stage': {
    params: { catalog: string; appId: string; version: string };
    result: AppPackagePreview;
  };
}

/** Sent with the whole list whenever an app, its state or the app pool changes (the topic `apps/admin`). */
export const APPS_CHANGED = 'internal.admin.apps.changed';

/** Sent with every change of a job the Store started (install, update, removal). */
export const APPS_JOB = 'internal.admin.apps.job';

/** Sent with the Store's whole state whenever catalogs, updates or settings change (the topic `store/admin`). */
export const STORE_CHANGED = 'internal.admin.store.changed';

type StoreName = keyof StoreMethods;

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const hasText = (value: Record<string, unknown>, key: string) =>
  typeof value[key] === 'string' && value[key] !== '';

const hasApp = (value: unknown): value is Record<string, unknown> & { appId: string } =>
  isRecord(value) && hasText(value, 'appId');

async function daemon<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    throw toPithyxError(error);
  }
}

function problemOf(status: number, body: string): PithyxError {
  try {
    const problem = JSON.parse(body) as {
      code?: unknown;
      params?: Record<string, unknown>;
      requestId?: string;
    };
    if (typeof problem.code === 'string') {
      return new PithyxError({
        code: problem.code,
        params: problem.params ?? {},
        ...(problem.requestId === undefined ? {} : { requestId: problem.requestId }),
      });
    }
  } catch {
    // Not a problem document; fall through.
  }
  return new PithyxError({ code: 'system.internal.unexpected', params: { status } });
}

/** Streams a package to the daemon with its progress in the taskbar, which fetch cannot report. */
function upload(file: Blob, name: string): Promise<AppPackagePreview> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    const transfer = startTransfer('upload', name, file.size, () => {
      request.abort();
    });
    request.open('PUT', '/api/apps/packages');
    request.setRequestHeader('X-Pithyx-Request', '1');
    request.setRequestHeader('Content-Type', 'application/gzip');
    request.upload.onprogress = (event) => {
      reportProgress(transfer, event.loaded);
    };
    request.onload = () => {
      // The Store shows a refused package itself; the taskbar only shows the transfer while it runs.
      endTransfer(transfer);
      if (request.status >= 200 && request.status < 300) {
        resolve(JSON.parse(request.responseText) as AppPackagePreview);
      } else {
        reject(problemOf(request.status, request.responseText));
      }
    };
    request.onabort = () => {
      endTransfer(transfer);
      reject(new PithyxError({ code: 'sdk.transfer.cancelled', params: {} }));
    };
    request.onerror = () => {
      endTransfer(transfer);
      reject(new PithyxError({ code: 'system.internal.unexpected', params: {} }));
    };
    request.send(file);
  });
}

/**
 * The host side of the Store's window. `emit` reaches that window only; the returned `close` stops following the
 * topic `apps/admin` and the jobs it started.
 */
export function storeHost(emit: (event: string, data?: unknown) => void): {
  extensions: HostExtensions;
  close: () => void;
} {
  const stops = [
    realtime.listen('apps/admin', (data) => {
      emit(APPS_CHANGED, data);
    }),
    realtime.listen('store/admin', (data) => {
      emit(STORE_CHANGED, data);
    }),
  ];
  const follow = (job: Job) => {
    stops.push(
      realtime.listen(`jobs/${String(job.id)}`, (data) => {
        emit(APPS_JOB, data);
      }),
    );
    return job;
  };
  const appPath = (appId: string) => ({ params: { path: { appId } } });
  const extensions: Record<StoreName, HostExtensions[string]> = {
    'internal.admin.apps.list': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/apps'))),
    },
    'internal.admin.apps.upload': {
      validate: (value) => isRecord(value) && value.file instanceof Blob && hasText(value, 'name'),
      handle: (params) => {
        const { file, name } = params as StoreMethods['internal.admin.apps.upload']['params'];
        return upload(file, name);
      },
    },
    'internal.admin.apps.discard': {
      validate: (value) => isRecord(value) && hasText(value, 'packageId'),
      handle: (params) =>
        daemon(async () => {
          const { packageId } = params as { packageId: string };
          unwrap(
            await api.DELETE('/api/apps/packages/{packageId}', { params: { path: { packageId } } }),
          );
          return null;
        }),
    },
    'internal.admin.apps.install': {
      validate: (value) =>
        isRecord(value) &&
        hasText(value, 'packageId') &&
        (value.pool === undefined || typeof value.pool === 'string') &&
        (value.access === undefined || value.access === 'everybody' || value.access === 'admins'),
      handle: (params) =>
        daemon(async () => {
          const { packageId, pool, access } =
            params as StoreMethods['internal.admin.apps.install']['params'];
          return follow(
            unwrap(
              await api.POST('/api/apps/packages/{packageId}/install', {
                params: { path: { packageId } },
                body: {
                  ...(pool === undefined ? {} : { pool }),
                  ...(access === undefined ? {} : { access }),
                },
              }),
            ),
          );
        }),
    },
    'internal.admin.apps.uninstall': {
      validate: (value) => hasApp(value) && (value.data === 'keep' || value.data === 'delete'),
      handle: (params) =>
        daemon(async () => {
          const { appId, data } = params as StoreMethods['internal.admin.apps.uninstall']['params'];
          return follow(
            unwrap(
              await api.DELETE('/api/apps/{appId}', {
                params: { path: { appId }, query: { data } },
              }),
            ),
          );
        }),
    },
    'internal.admin.apps.setEnabled': {
      validate: (value) => hasApp(value) && typeof value.enabled === 'boolean',
      handle: (params) =>
        daemon(async () => {
          const { appId, enabled } =
            params as StoreMethods['internal.admin.apps.setEnabled']['params'];
          return unwrap(
            await api.PATCH('/api/apps/{appId}', { ...appPath(appId), body: { enabled } }),
          );
        }),
    },
    'internal.admin.apps.setPermission': {
      validate: (value) =>
        hasApp(value) && hasText(value, 'permission') && typeof value.granted === 'boolean',
      handle: (params) =>
        daemon(async () => {
          const { appId, permission, granted } =
            params as StoreMethods['internal.admin.apps.setPermission']['params'];
          return unwrap(
            await api.PUT('/api/apps/{appId}/permissions', {
              ...appPath(appId),
              body: { permissions: [{ permission, granted }] },
            }),
          );
        }),
    },
    'internal.admin.apps.mapStorage': {
      validate: (value) =>
        hasApp(value) &&
        hasText(value, 'storageId') &&
        Number.isSafeInteger(value.shareId) &&
        typeof value.path === 'string' &&
        (value.access === 'read' || value.access === 'readWrite'),
      handle: (params) =>
        daemon(async () => {
          const { appId, storageId, shareId, path, access } =
            params as StoreMethods['internal.admin.apps.mapStorage']['params'];
          return unwrap(
            await api.PUT('/api/apps/{appId}/storage/{storageId}', {
              params: { path: { appId, storageId } },
              body: { shareId, path, access },
            }),
          );
        }),
    },
    'internal.admin.apps.unmapStorage': {
      validate: (value) => hasApp(value) && hasText(value, 'storageId'),
      handle: (params) =>
        daemon(async () => {
          const { appId, storageId } =
            params as StoreMethods['internal.admin.apps.unmapStorage']['params'];
          return unwrap(
            await api.DELETE('/api/apps/{appId}/storage/{storageId}', {
              params: { path: { appId, storageId } },
            }),
          );
        }),
    },
    'internal.admin.apps.setLogLevel': {
      validate: (value) => hasApp(value) && (value.level === 'info' || value.level === 'debug'),
      handle: (params) =>
        daemon(async () => {
          const { appId, level } =
            params as StoreMethods['internal.admin.apps.setLogLevel']['params'];
          return unwrap(
            await api.PUT('/api/apps/{appId}/log-level', { ...appPath(appId), body: { level } }),
          );
        }),
    },
    'internal.admin.apps.grants': {
      validate: hasApp,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.GET('/api/apps/{appId}/grants', appPath((params as { appId: string }).appId)),
          ),
        ),
    },
    'internal.admin.apps.setGrants': {
      validate: (value) =>
        hasApp(value) &&
        Array.isArray(value.grants) &&
        value.grants.every(
          (grant: unknown) =>
            isRecord(grant) &&
            (grant.subjectKind === 'user' || grant.subjectKind === 'group') &&
            Number.isSafeInteger(grant.subjectId) &&
            grant.level === 'allow',
        ),
      handle: (params) =>
        daemon(async () => {
          const { appId, grants } =
            params as StoreMethods['internal.admin.apps.setGrants']['params'];
          return unwrap(
            await api.PUT('/api/apps/{appId}/grants', { ...appPath(appId), body: { grants } }),
          );
        }),
    },
    'internal.admin.apps.pools': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/storage/pools'))),
    },
    'internal.admin.apps.shares': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/shares/details'))),
    },
    'internal.admin.apps.open': {
      validate: hasApp,
      handle: (params) => Promise.resolve(openApp((params as { appId: string }).appId)),
    },
    'internal.admin.apps.rollback': {
      validate: hasApp,
      handle: (params) =>
        daemon(async () =>
          follow(
            unwrap(
              await api.POST(
                '/api/apps/{appId}/rollback',
                appPath((params as { appId: string }).appId),
              ),
            ),
          ),
        ),
    },
    'internal.admin.apps.setUpdatePolicy': {
      validate: (value) =>
        hasApp(value) &&
        (value.policy === null || ['automatic', 'notify', 'none'].includes(value.policy as string)),
      handle: (params) =>
        daemon(async () => {
          const { appId, policy } =
            params as StoreMethods['internal.admin.apps.setUpdatePolicy']['params'];
          return unwrap(
            await api.PUT('/api/apps/{appId}/update-policy', {
              ...appPath(appId),
              body: { policy },
            }),
          );
        }),
    },
    'internal.admin.store.state': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/store'))),
    },
    'internal.admin.store.setSettings': {
      validate: (value) =>
        isRecord(value) &&
        ['automatic', 'notify', 'none'].includes(value.policy as string) &&
        typeof value.time === 'string' &&
        (value.channel === 'stable' || value.channel === 'beta'),
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.PUT('/api/store/settings', {
              body: params as StoreMethods['internal.admin.store.setSettings']['params'],
            }),
          ),
        ),
    },
    'internal.admin.store.check': {
      validate: isRecord,
      handle: () => daemon(async () => follow(unwrap(await api.POST('/api/store/check')))),
    },
    'internal.admin.store.addCatalog': {
      validate: (value) => isRecord(value) && hasText(value, 'url'),
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/store/catalogs', {
              body: { url: (params as { url: string }).url },
            }),
          ),
        ),
    },
    'internal.admin.store.updateCatalog': {
      validate: (value) =>
        isRecord(value) &&
        Number.isSafeInteger(value.key) &&
        (value.enabled === undefined || typeof value.enabled === 'boolean') &&
        (value.registryToken === undefined || typeof value.registryToken === 'string'),
      handle: (params) =>
        daemon(async () => {
          const { key, enabled, registryToken } =
            params as StoreMethods['internal.admin.store.updateCatalog']['params'];
          return unwrap(
            await api.PATCH('/api/store/catalogs/{key}', {
              params: { path: { key } },
              body: {
                ...(enabled === undefined ? {} : { enabled }),
                ...(registryToken === undefined ? {} : { registryToken }),
              },
            }),
          );
        }),
    },
    'internal.admin.store.removeCatalog': {
      validate: (value) => isRecord(value) && Number.isSafeInteger(value.key),
      handle: (params) =>
        daemon(async () => {
          unwrap(
            await api.DELETE('/api/store/catalogs/{key}', {
              params: { path: { key: (params as { key: number }).key } },
            }),
          );
          return null;
        }),
    },
    'internal.admin.store.syncCatalog': {
      validate: (value) => isRecord(value) && Number.isSafeInteger(value.key),
      handle: (params) =>
        daemon(async () =>
          follow(
            unwrap(
              await api.POST('/api/store/catalogs/{key}/sync', {
                params: { path: { key: (params as { key: number }).key } },
              }),
            ),
          ),
        ),
    },
    'internal.admin.store.search': {
      validate: (value) =>
        isRecord(value) &&
        (value.catalog === undefined || typeof value.catalog === 'string') &&
        (value.query === undefined || typeof value.query === 'string'),
      handle: (params) =>
        daemon(async () => {
          const { catalog, query } =
            params as StoreMethods['internal.admin.store.search']['params'];
          return unwrap(
            await api.GET('/api/store/apps', {
              params: {
                query: {
                  ...(catalog === undefined ? {} : { catalog }),
                  ...(query === undefined || query === '' ? {} : { q: query }),
                },
              },
            }),
          );
        }),
    },
    'internal.admin.store.details': {
      validate: (value) => hasApp(value) && hasText(value, 'catalog'),
      handle: (params) =>
        daemon(async () => {
          const { catalog, appId } =
            params as StoreMethods['internal.admin.store.details']['params'];
          return unwrap(
            await api.GET('/api/store/catalogs/{catalog}/apps/{appId}', {
              params: { path: { catalog, appId } },
            }),
          );
        }),
    },
    'internal.admin.store.stage': {
      validate: (value) => hasApp(value) && hasText(value, 'catalog') && hasText(value, 'version'),
      handle: (params) =>
        daemon(async () => {
          const { catalog, appId, version } =
            params as StoreMethods['internal.admin.store.stage']['params'];
          return unwrap(
            await api.POST('/api/store/catalogs/{catalog}/apps/{appId}/{version}/package', {
              params: { path: { catalog, appId, version } },
            }),
          );
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

/** Calls one of the Store's methods through the SDK. */
export async function storeCall<M extends StoreName>(
  app: PithyxApp,
  method: M,
  params: StoreMethods[M]['params'],
): Promise<StoreMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as StoreMethods[M]['result'];
}

/** Listens for one of the Store's events; returns the function that stops listening. */
export function onStore(
  app: PithyxApp,
  event: typeof APPS_CHANGED | typeof APPS_JOB | typeof STORE_CHANGED,
  handler: (data: unknown) => void,
): () => void {
  return app.on(event as unknown as EventName, handler);
}
