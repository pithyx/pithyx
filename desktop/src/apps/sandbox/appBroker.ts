import type { components } from '@pithyx/api-client';
import type { FileEntry, FileLocation, FileRef, FolderPage, ShareInfo } from '@pithyx/sdk';
import { sdkError, type MethodHandlers } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { randomId } from '../../platform/browser';
import { rememberNotification } from '../host/store';
import { toPithyxError } from '../sdk/daemonError';
import { locationQuery, type AppApi } from './appApi';

type AppFileEntry = components['schemas']['AppFileEntry'];
type AppShareInfo = components['schemas']['AppShareInfo'];
type AppFolderPage = components['schemas']['AppFolderPage'];

/** Turns a daemon failure into the problem the app sees, with the same code. */
async function daemon<T>(work: () => Promise<T>): Promise<T> {
  try {
    return await work();
  } catch (error) {
    throw toPithyxError(error);
  }
}

function entryOf(entry: AppFileEntry | components['schemas']['FileEntry']): FileEntry {
  return { name: entry.name, kind: entry.kind, size: entry.size, modified: entry.modified };
}

function shareOf(share: AppShareInfo): ShareInfo {
  return {
    id: share.id,
    name: share.name,
    access: share.access === 'readWrite' ? 'readWrite' : 'read',
    ...(share.unavailable == null
      ? {}
      : { unavailable: share.unavailable as NonNullable<ShareInfo['unavailable']> }),
  };
}

function pageOf(page: AppFolderPage): FolderPage {
  return { ...page, entries: page.entries.map(entryOf) };
}

function join(folder: string, name: string): string {
  return folder === '' ? name : `${folder}/${name}`;
}

/**
 * The SDK methods of a third-party app window that reach the daemon, through its API for apps with the window's host
 * token (A11): the daemon checks the app's permissions and the user's rights on every call. File handles (`FileRef`)
 * are this window's own: a `ref` names a place this window opened, and nothing outside this object can resolve it.
 * `debug` log entries reach the daemon only while the app's log level says so (D305).
 */
export function appBrokerFor(target: {
  app: string;
  windowId: string;
  daemon: AppApi;
  logLevel: () => 'debug' | 'info';
}): MethodHandlers {
  const { daemon: client } = target;
  const refs = new Map<string, { location: FileLocation; path: string }>();

  const fileRef = (location: FileLocation, path: string, entry: AppFileEntry): FileRef => {
    const ref = randomId();
    refs.set(ref, { location, path });
    const name = path.split('/').at(-1) ?? path;
    return { ref, name, size: entry.size, modified: entry.modified, writable: entry.writable };
  };
  const place = (file: FileRef) => {
    const found = refs.get(file.ref);
    if (found === undefined) {
      throw sdkError('sdk.request.invalid', { method: 'files' });
    }
    return found;
  };
  const query = (location: FileLocation, path: string) =>
    new URLSearchParams({ ...locationQuery(location), path }).toString();
  const write = async (location: FileLocation, path: string, data: Blob, overwrite: boolean) => {
    const response = await client.raw(
      `/api/app/files/content?${query(location, path)}&overwrite=${String(overwrite)}`,
      { method: 'PUT', body: data },
    );
    return (await response.json()) as AppFileEntry;
  };

  return {
    'log.write': async ({ entries }) => {
      const wanted =
        target.logLevel() === 'debug'
          ? entries
          : entries.filter((entry) => entry.level !== 'debug');
      if (wanted.length > 0) {
        // Logging must never disturb the app; a lost batch is not worth an error.
        await client
          .call((headers) => api.POST('/api/app/logs', { headers, body: { entries: wanted } }))
          .catch(() => undefined);
      }
    },
    notify: async ({ title, body, level, actions, data, tag }) =>
      daemon(async () => {
        const created = await client.call((headers) =>
          api.POST('/api/app/notifications', {
            headers,
            body: {
              title,
              body: body ?? null,
              level: level ?? null,
              actions: actions ?? null,
              data: data === undefined ? null : (data as never),
              tag: tag ?? null,
            },
          }),
        );
        const id = String(created.id);
        // The daemon keeps the buttons and the data; this tab remembers which window sent it.
        rememberNotification(id, {
          app: target.app,
          windowId: target.windowId,
          actions: actions ?? [],
          data: data ?? null,
        });
        return { id };
      }),
    'storage.get': async ({ key }) =>
      daemon(
        async () =>
          (
            await client.call((headers) =>
              api.GET('/api/app/storage/value', { headers, params: { query: { key } } }),
            )
          ).value,
      ),
    'storage.set': async ({ key, value }) =>
      daemon(async () => {
        await client.call((headers) =>
          api.PUT('/api/app/storage/value', {
            headers,
            params: { query: { key } },
            body: { value: (value === undefined ? null : value) as never },
          }),
        );
        return undefined;
      }),
    'storage.remove': async ({ key }) =>
      daemon(async () => {
        await client.call((headers) =>
          api.DELETE('/api/app/storage/value', { headers, params: { query: { key } } }),
        );
        return undefined;
      }),
    'storage.keys': async () =>
      daemon(
        async () => (await client.call((headers) => api.GET('/api/app/storage', { headers }))).keys,
      ),
    'directory.users': async () =>
      daemon(async () =>
        client.call((headers) => api.GET('/api/app/directory/users', { headers })),
      ),
    'directory.groups': async () =>
      daemon(async () =>
        client.call((headers) => api.GET('/api/app/directory/groups', { headers })),
      ),
    'backend.token': async () => daemon(async () => client.backendToken()),
    'backend.ticket': async ({ endpoint }) =>
      daemon(async () => {
        const issued = await client.call((headers) =>
          api.POST('/api/app/tickets', { headers, body: { endpoint } }),
        );
        return { ticket: issued.ticket, expiresAt: issued.expiresAt };
      }),
    'files.shares': async () =>
      daemon(async () =>
        (await client.call((headers) => api.GET('/api/app/files/shares', { headers }))).map(
          shareOf,
        ),
      ),
    'files.list': async ({ location, path, cursor, limit, sort, order }) =>
      daemon(async () =>
        pageOf(
          await client.call((headers) =>
            api.GET('/api/app/files/list', {
              headers,
              params: {
                query: {
                  ...locationQuery(location),
                  path,
                  ...(cursor === undefined ? {} : { cursor }),
                  ...(limit === undefined ? {} : { limit }),
                  ...(sort === undefined ? {} : { sort }),
                  ...(order === undefined ? {} : { order }),
                },
              },
            }),
          ),
        ),
      ),
    'files.open': async ({ location, path }) =>
      daemon(async () => {
        const entry = await client.call((headers) =>
          api.GET('/api/app/files/entry', {
            headers,
            params: { query: { ...locationQuery(location), path } },
          }),
        );
        if (entry.kind !== 'file') {
          throw sdkError('sdk.request.invalid', { method: 'files.open' });
        }
        return fileRef(location, path, entry);
      }),
    'files.read': async ({ file }) =>
      daemon(async () => {
        const { location, path } = place(file);
        return (await client.raw(`/api/app/files/content?${query(location, path)}`)).blob();
      }),
    'files.write': async ({ file, data }) =>
      daemon(async () => {
        const { location, path } = place(file);
        return fileRef(location, path, await write(location, path, data, true));
      }),
    'files.upload': async ({ location, folder, name, data, overwrite }) =>
      daemon(async () =>
        entryOf(await write(location, join(folder, name), data, overwrite === true)),
      ),
    'files.createFolder': async ({ location, path }) =>
      daemon(async () =>
        entryOf(
          await client.call((headers) =>
            api.POST('/api/app/files/folders', {
              headers,
              params: { query: { ...locationQuery(location), path } },
            }),
          ),
        ),
      ),
  };
}
