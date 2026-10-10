import { unwrap } from '@pithyx/api-client';
import { PithyxError, type MethodHandlers } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { download } from '../../shell/transfers';
import { toPithyxError } from './daemonError';
import {
  defined,
  folderWatcher,
  jobFollower,
  operationBody,
  putFile,
  query,
  shareIdOf,
  sharesWatcher,
  showProblem,
  toEntry,
  toJob,
  toTrashPage,
  toVersions,
  type ApiJob,
  type Emit,
} from './fileMapping';

export type { Emit };

export interface FileBrokerTarget {
  permissions: readonly string[];
  emit: Emit;
}

const UNAVAILABLE: Record<string, 'poolMissing' | 'poolUnavailable' | 'notMounted'> = {
  pool_missing: 'poolMissing',
  pool_unavailable: 'poolUnavailable',
  not_mounted: 'notMounted',
};

/** Turns a failed daemon call into the problem the app sees, unchanged. */
async function daemon<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    throw toPithyxError(error);
  }
}

/**
 * The file manager API of the SDK, bound to one app window: shares, pages, live folder changes, uploads and
 * downloads, operations as jobs and the recycle bin. Every call goes to the daemon, which checks the user's rights
 * again; the permission check here only answers early.
 */
export function fileBrokerFor(target: FileBrokerTarget): {
  methods: MethodHandlers;
  close: () => void;
} {
  const watches = folderWatcher(target.emit);
  const jobs = jobFollower(target.emit);
  const stopShares = target.permissions.includes('files.shares')
    ? sharesWatcher(target.emit, () => true)
    : undefined;

  const require = (permission: string) => {
    if (!target.permissions.includes(permission)) {
      throw new PithyxError({ code: 'app.permission.denied', params: { permission } });
    }
  };

  async function started(
    call: () => Promise<{ data?: ApiJob; error?: unknown; response: Response }>,
  ) {
    return jobs.follow(toJob(await daemon(async () => unwrap(await call()))));
  }

  const methods: MethodHandlers = {
    'files.shares': async () => {
      require('files.shares');
      const shares = await daemon(async () => unwrap(await api.GET('/api/shares')));
      return shares.map((share) => ({
        id: String(share.id),
        name: share.name,
        access: share.access === 'readWrite' ? 'readWrite' : 'read',
        ...(share.unavailable == null
          ? {}
          : { unavailable: UNAVAILABLE[share.unavailable] ?? 'notMounted' }),
      }));
    },
    'files.list': async ({ location, path, cursor, limit, sort, order }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      const page = await daemon(async () =>
        unwrap(
          await api.GET('/api/files/{shareId}/list', {
            params: { path: { shareId }, query: defined({ path, cursor, limit, sort, order }) },
          }),
        ),
      );
      return {
        path: page.path,
        folderId: page.folderId,
        total: page.total,
        entries: page.entries.map(toEntry),
        nextCursor: page.nextCursor ?? null,
      };
    },
    'files.watch': async ({ location, path }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      // The topic of a folder comes with its listing; a one-entry page is the cheapest way to get it.
      const page = await daemon(async () =>
        unwrap(
          await api.GET('/api/files/{shareId}/list', {
            params: { path: { shareId }, query: { path, limit: 1 } },
          }),
        ),
      );
      const watch = watches.start(page.topic);
      return { watch };
    },
    'files.unwatch': ({ watch }) => {
      watches.stop(watch);
    },
    'files.upload': async ({ location, folder, name, data, overwrite, upload }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      const path = folder === '' ? name : `${folder}/${name}`;
      const url = `/api/files/${String(shareId)}/content?${query({ path, overwrite: overwrite === true ? 'true' : undefined })}`;
      const entry = await putFile(url, name, data, (loaded) => {
        if (upload !== undefined) {
          target.emit('uploadProgress', { upload, loaded, total: data.size });
        }
      });
      return toEntry(entry);
    },
    'files.download': ({ location, path }) => {
      require('files.shares');
      // Returns once the download started; its progress and a cancel button are in the taskbar.
      download(
        `/api/files/${String(shareIdOf(location))}/content?${query({ path })}`,
        path.slice(path.lastIndexOf('/') + 1),
      );
    },
    'files.createFolder': async ({ location, path }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      return toEntry(
        await daemon(async () =>
          unwrap(
            await api.POST('/api/files/{shareId}/folders', {
              params: { path: { shareId } },
              body: { path },
            }),
          ),
        ),
      );
    },
    'files.rename': async ({ location, path, name }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      return toEntry(
        await daemon(async () =>
          unwrap(
            await api.POST('/api/files/{shareId}/rename', {
              params: { path: { shareId } },
              body: { path, name },
            }),
          ),
        ),
      );
    },
    'files.run': async (params) => {
      require('files.shares');
      return started(() => api.POST('/api/files/operations', { body: operationBody(params) }));
    },
    'files.versions': async ({ location, path }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      return toVersions(
        await daemon(async () =>
          unwrap(
            await api.GET('/api/files/{shareId}/versions', {
              params: { path: { shareId }, query: { path } },
            }),
          ),
        ),
      );
    },
    'files.restoreVersion': async ({ location, path, snapshot, overwrite }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      return started(() =>
        api.POST('/api/files/{shareId}/versions/restore', {
          params: { path: { shareId } },
          body: { path, snapshot, overwrite: overwrite === true },
        }),
      );
    },
    'jobs.cancel': async ({ job }) => {
      await daemon(async () => {
        unwrap(await api.POST('/api/jobs/{id}/cancel', { params: { path: { id: job } } }));
      });
    },
    'trash.list': async ({ location, cursor, limit }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      return toTrashPage(
        await daemon(async () =>
          unwrap(
            await api.GET('/api/files/{shareId}/trash', {
              params: { path: { shareId }, query: defined({ cursor, limit }) },
            }),
          ),
        ),
      );
    },
    'trash.restore': async ({ location, ids, onConflict }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      return started(() =>
        api.POST('/api/files/{shareId}/trash/restore', {
          params: { path: { shareId } },
          body: { ids, ...(onConflict === undefined ? {} : { onConflict }) },
        }),
      );
    },
    'trash.delete': async ({ location, ids }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      return started(() =>
        api.POST('/api/files/{shareId}/trash/delete', {
          params: { path: { shareId } },
          body: { ids },
        }),
      );
    },
    'trash.empty': async ({ location }) => {
      require('files.shares');
      const shareId = shareIdOf(location);
      return started(() =>
        api.POST('/api/files/{shareId}/trash/empty', { params: { path: { shareId } } }),
      );
    },
    'ui.showError': ({ problem }) => {
      showProblem(problem);
    },
  };

  return {
    methods,
    close: () => {
      stopShares?.();
      watches.close();
      jobs.close();
    },
  };
}
