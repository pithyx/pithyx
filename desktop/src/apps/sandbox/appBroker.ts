import type { components } from '@pithyx/api-client';
import type { FileLocation, FileRef, FolderPage, ShareInfo } from '@pithyx/sdk';
import { PithyxError, sdkError, type MethodHandlers } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { randomId } from '../../platform/browser';
import { download } from '../../shell/transfers';
import { useDesktop } from '../../wm/store';
import type { PendingFile } from '../host/rules';
import { offerFileDialog, rememberNotification, type ChosenFile } from '../host/store';
import { toPithyxError } from '../sdk/daemonError';
import {
  folderWatcher,
  jobFollower,
  shareIdOf,
  sharesWatcher,
  showProblem,
  toEntry,
  toJob,
  toTrashPage,
  toVersions,
  type Emit,
} from '../sdk/fileMapping';
import { appOperationBody, locationQuery, type AppApi } from './appApi';

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

function shareOf(share: AppShareInfo): ShareInfo {
  return {
    id: share.id,
    name: share.name,
    access: share.access === 'readWrite' ? 'readWrite' : 'read',
    ...(share.unavailable == null
      ? {}
      : { unavailable: share.unavailable as NonNullable<ShareInfo['unavailable']> }),
    ...(share.kind === 'home' ? { kind: 'home' as const } : {}),
  };
}

/** The page as the app sees it: the folder's topic stays with the desktop. */
function pageOf(page: AppFolderPage): FolderPage {
  return {
    path: page.path,
    folderId: page.folderId,
    total: page.total,
    entries: page.entries.map(toEntry),
    nextCursor: page.nextCursor ?? null,
  };
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
  /** The permissions the app holds now (the daemon's context follows revocations). */
  permissions: () => readonly string[];
  /** Sends an event to the app's window. */
  emit: Emit;
}): {
  methods: MethodHandlers;
  close: () => void;
  adopt: (file: PendingFile) => Promise<FileRef | undefined>;
} {
  const { daemon: client } = target;
  const refs = new Map<string, { location: FileLocation; path: string }>();
  const watches = folderWatcher(target.emit);
  const jobs = jobFollower(target.emit);
  const stopShares = sharesWatcher(target.emit, () =>
    target.permissions().includes('files.shares'),
  );
  const require = (permission: string) => {
    if (!target.permissions().includes(permission)) {
      throw new PithyxError({ code: 'app.permission.denied', params: { permission } });
    }
  };
  /** Dialogs open for a window the user works in only, like menus. */
  const focused = () => useDesktop.getState().focused === target.windowId;

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

  /** Registers a file the user chose in a dialog with the daemon and gives the app a handle for it. */
  async function handOut(file: ChosenFile, write: boolean, entry?: AppFileEntry): Promise<FileRef> {
    const location = { share: String(file.share) };
    await client.call((headers) =>
      api.POST('/api/app/files/handles', {
        headers,
        body: { share: location.share, path: file.path, write },
      }),
    );
    return fileRef(
      location,
      file.path,
      entry ?? {
        name: file.name,
        kind: 'file',
        size: file.size,
        modified: file.modified,
        writable: write,
      },
    );
  }
  const urlFor = async (location: FileLocation, path: string) => {
    const created = await client.call((headers) =>
      api.POST('/api/app/files/url', { headers, body: { path, ...locationQuery(location) } }),
    );
    return { url: new URL(created.url, window.location.origin).href, expiresAt: created.expiresAt };
  };
  const shareBody = (location: FileLocation, method: string) => String(shareIdOf(location, method));
  const started = async (call: () => Promise<components['schemas']['Job']>) =>
    jobs.follow(toJob(await daemon(call)));

  const methods: MethodHandlers = {
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
    notify: async ({ title, body, level, actions, data, tag, category }) =>
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
              category: category ?? null,
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
    'backend.token': async ({ renew }) => daemon(async () => client.backendToken(renew === true)),
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
    'files.upload': async ({ location, folder, name, data, overwrite, upload }) =>
      daemon(async () => {
        const path = join(folder, name);
        const entry = await client.upload(
          `/api/app/files/content?${query(location, path)}&overwrite=${String(overwrite === true)}`,
          name,
          data,
          (loaded) => {
            if (upload !== undefined) {
              target.emit('uploadProgress', { upload, loaded, total: data.size });
            }
          },
        );
        return toEntry(entry);
      }),
    'files.createFolder': async ({ location, path }) =>
      daemon(async () =>
        toEntry(
          await client.call((headers) =>
            api.POST('/api/app/files/folders', {
              headers,
              params: { query: { ...locationQuery(location), path } },
            }),
          ),
        ),
      ),
    'files.pick': async ({ multiple, extensions }) => {
      require('files.read');
      if (!focused()) {
        return [];
      }
      const chosen = await offerFileDialog({
        kind: 'pick',
        windowId: target.windowId,
        multiple: multiple === true,
        extensions: (extensions ?? []).map((extension) => extension.replace(/^\./, '')),
        suggestedName: '',
      });
      const refs: FileRef[] = [];
      for (const file of chosen ?? []) {
        const write = target.permissions().includes('files.write') && file.access === 'readWrite';
        refs.push(await daemon(async () => handOut(file, write)));
      }
      return refs;
    },
    'files.saveAs': async ({ suggestedName, data }) => {
      require('files.write');
      if (!focused()) {
        return null;
      }
      const chosen = (
        await offerFileDialog({
          kind: 'save',
          windowId: target.windowId,
          multiple: false,
          extensions: [],
          suggestedName,
        })
      )?.[0];
      if (chosen === undefined) {
        return null;
      }
      return daemon(async () => {
        await handOut(chosen, true);
        const location = { share: String(chosen.share) };
        const entry = await client.upload(
          `/api/app/files/content?${query(location, chosen.path)}&overwrite=${String(chosen.overwrite)}`,
          chosen.name,
          data,
          () => undefined,
        );
        return fileRef(location, chosen.path, entry);
      });
    },
    'files.url': async ({ file }) =>
      daemon(async () => {
        const { location, path } = place(file);
        return urlFor(location, path);
      }),
    'files.watch': async ({ location, path }) =>
      daemon(async () => {
        // The topic of a folder comes with its listing; a one-entry page is the cheapest way to get it.
        const page = await client.call((headers) =>
          api.GET('/api/app/files/list', {
            headers,
            params: { query: { ...locationQuery(location), path, limit: 1 } },
          }),
        );
        const needsShares = 'share' in location;
        return {
          watch: watches.start(
            page.topic,
            () => !needsShares || target.permissions().includes('files.shares'),
          ),
        };
      }),
    'files.unwatch': ({ watch }) => {
      watches.stop(watch);
    },
    'files.download': async ({ location, path }) =>
      daemon(async () => {
        const { url } = await urlFor(location, path);
        // Returns once the download started; its progress and a cancel button are in the taskbar.
        download(`${url}&download=true`, path.slice(path.lastIndexOf('/') + 1));
        return undefined;
      }),
    'files.rename': async ({ location, path, name }) =>
      daemon(async () =>
        toEntry(
          await client.call((headers) =>
            api.POST('/api/app/files/rename', {
              headers,
              body: { path, name, ...locationQuery(location) },
            }),
          ),
        ),
      ),
    'files.run': async (params) =>
      started(() =>
        client.call((headers) =>
          api.POST('/api/app/files/operations', { headers, body: appOperationBody(params) }),
        ),
      ),
    'files.versions': async ({ location, path }) =>
      daemon(async () =>
        toVersions(
          await client.call((headers) =>
            api.GET('/api/app/files/versions', {
              headers,
              params: { query: { share: shareBody(location, 'files.versions'), path } },
            }),
          ),
        ),
      ),
    'files.restoreVersion': async ({ location, path, snapshot, overwrite }) =>
      started(() =>
        client.call((headers) =>
          api.POST('/api/app/files/versions/restore', {
            headers,
            body: {
              share: shareBody(location, 'files.restoreVersion'),
              path,
              snapshot,
              overwrite: overwrite === true,
            },
          }),
        ),
      ),
    'jobs.cancel': async ({ job }) =>
      daemon(async () => {
        await client.call((headers) =>
          api.POST('/api/app/jobs/{id}/cancel', { headers, params: { path: { id: job } } }),
        );
        return undefined;
      }),
    'trash.list': async ({ location, cursor, limit }) =>
      daemon(async () =>
        toTrashPage(
          await client.call((headers) =>
            api.GET('/api/app/files/trash', {
              headers,
              params: {
                query: {
                  share: shareBody(location, 'trash.list'),
                  ...(cursor === undefined ? {} : { cursor }),
                  ...(limit === undefined ? {} : { limit }),
                },
              },
            }),
          ),
        ),
      ),
    'trash.restore': async ({ location, ids, onConflict }) =>
      started(() =>
        client.call((headers) =>
          api.POST('/api/app/files/trash/restore', {
            headers,
            body: {
              share: shareBody(location, 'trash.restore'),
              ids,
              ...(onConflict === undefined ? {} : { onConflict }),
            },
          }),
        ),
      ),
    'trash.delete': async ({ location, ids }) =>
      started(() =>
        client.call((headers) =>
          api.POST('/api/app/files/trash/delete', {
            headers,
            body: { share: shareBody(location, 'trash.delete'), ids },
          }),
        ),
      ),
    'trash.empty': async ({ location }) =>
      started(() =>
        client.call((headers) =>
          api.POST('/api/app/files/trash/empty', {
            headers,
            body: { share: shareBody(location, 'trash.empty') },
          }),
        ),
      ),
    'ui.showError': ({ problem }) => {
      showProblem(problem);
    },
  };

  return {
    methods,
    /**
     * The handle for a file the user opened with this app from the Files app (`launch.file`, `openFile`): the app
     * reads it with `files.read`, or writes it when it holds `files.write` and the user may. Undefined when the app
     * holds no `files.read`, because it could not read the file anyway.
     */
    adopt: async (file) => {
      if (!target.permissions().includes('files.read')) {
        return undefined;
      }
      const write = file.writable && target.permissions().includes('files.write');
      try {
        return await handOut(
          {
            share: Number(file.share),
            path: file.path,
            name: file.path.split('/').at(-1) ?? file.path,
            size: file.size,
            modified: file.modified,
            access: file.writable ? 'readWrite' : 'read',
            overwrite: false,
          },
          write,
        );
      } catch {
        return undefined;
      }
    },
    close: () => {
      stopShares();
      watches.close();
      jobs.close();
    },
  };
}
