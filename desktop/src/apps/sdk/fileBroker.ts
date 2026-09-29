import { unwrap, PithyxApiError, type components } from '@pithyx/api-client';
import type {
  Events,
  FileEntry,
  FileLocation,
  FileVersion,
  JobInfo,
  Problem,
  TrashPage,
} from '@pithyx/sdk';
import { PithyxError, type MethodHandlers } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { toPithyxError } from './daemonError';
import { showError } from '../../errors/errorStore';
import { realtime } from '../../realtime/connection';

type ApiEntry = components['schemas']['FileEntry'];
type ApiJob = components['schemas']['Job'];
type ApiTrashPage = components['schemas']['TrashPage'];
type ApiFileVersionList = components['schemas']['FileVersionList'];

/** Sends an event to the app that owns this broker. */
export type Emit = <E extends keyof Events>(event: E, data: Events[E]) => void;

export interface FileBrokerTarget {
  permissions: readonly string[];
  emit: Emit;
}

const FINISHED: readonly JobInfo['state'][] = ['succeeded', 'failed', 'cancelled', 'interrupted'];

/** Turns a failed daemon call into the problem the app sees, unchanged. */
async function daemon<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    throw toPithyxError(error);
  }
}

function toEntry(entry: ApiEntry): FileEntry {
  return { name: entry.name, kind: entry.kind, size: entry.size, modified: entry.modified };
}

function toJob(job: ApiJob): JobInfo {
  const result = job.result as { done?: number; skipped?: string[] } | null | undefined;
  return {
    id: job.id,
    kind: job.kind,
    state: job.state,
    progress: {
      itemsDone: job.progress.itemsDone,
      itemsTotal: job.progress.itemsTotal,
      bytesDone: job.progress.bytesDone,
      bytesTotal: job.progress.bytesTotal,
      current: job.progress.currentItem ?? null,
    },
    problem:
      job.errorCode == null
        ? null
        : { code: job.errorCode, params: job.errorParams ?? {}, requestId: job.requestId },
    result: result == null ? null : { done: result.done ?? 0, skipped: result.skipped ?? [] },
  };
}

function toTrashPage(page: ApiTrashPage): TrashPage {
  return {
    items: page.items.map((item) => ({
      id: item.id,
      name: item.name,
      originalPath: item.originalPath,
      kind: item.kind,
      size: item.size,
      deletedAt: item.deletedAt,
      deletedBy: item.deletedBy ?? null,
      expiresAt: item.expiresAt ?? null,
    })),
    nextCursor: page.nextCursor ?? null,
    retentionDays: page.retentionDays,
  };
}

function toVersions(list: ApiFileVersionList): FileVersion[] {
  return list.versions.map((version) => ({
    snapshot: version.snapshot,
    createdAt: version.createdAt,
    kind: version.kind,
    size: version.size,
    modified: version.modified,
  }));
}

/** The daemon's share id of a location. Named storage arrives with app installation (M4) and has no mapping yet. */
function shareOf(location: FileLocation): number {
  const id = 'share' in location ? Number(location.share) : Number.NaN;
  if (!Number.isSafeInteger(id)) {
    throw new PithyxError({ code: 'fs.share.not_found', params: {} });
  }
  return id;
}

function problemOf(status: number, body: string): PithyxError {
  try {
    const problem = JSON.parse(body) as Partial<Problem>;
    if (typeof problem.code === 'string') {
      return new PithyxError({
        code: problem.code,
        params: problem.params ?? {},
        ...(problem.detail === undefined ? {} : { detail: problem.detail }),
        ...(problem.requestId === undefined ? {} : { requestId: problem.requestId }),
      });
    }
  } catch {
    // Not a problem document; fall through.
  }
  return new PithyxError({ code: 'system.internal.unexpected', params: { status } });
}

/** Streams a file to the daemon with progress; fetch cannot report upload progress. */
function put(url: string, data: Blob, onProgress: (loaded: number) => void): Promise<ApiEntry> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open('PUT', url);
    request.setRequestHeader('X-Pithyx-Request', '1');
    request.setRequestHeader('Content-Type', 'application/octet-stream');
    request.upload.onprogress = (event) => {
      onProgress(event.loaded);
    };
    request.onload = () => {
      if (request.status >= 200 && request.status < 300) {
        resolve(JSON.parse(request.responseText) as ApiEntry);
      } else {
        reject(problemOf(request.status, request.responseText));
      }
    };
    request.onerror = () => {
      reject(new PithyxError({ code: 'fs.upload.incomplete', params: {} }));
    };
    request.send(data);
  });
}

/** The same object without the keys whose value is undefined, as optional query parameters want it. */
function defined<T extends object>(value: T): { [K in keyof T]?: Exclude<T[K], undefined> } {
  return Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined)) as {
    [K in keyof T]?: Exclude<T[K], undefined>;
  };
}

function query(values: Record<string, string | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined) {
      search.set(key, value);
    }
  }
  return search.toString();
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
  const watches = new Map<string, () => void>();
  const jobs = new Map<number, () => void>();
  let nextWatch = 1;
  // The topic says only that the user's share list may have changed; its first message is the current revision.
  let sharesRevision: number | undefined;
  const stopShares = target.permissions.includes('files.shares')
    ? realtime.listen('shares/list', (data) => {
        const { revision } = data as components['schemas']['ShareListRevision'];
        if (sharesRevision !== undefined && revision !== sharesRevision) {
          target.emit('sharesChanged', {});
        }
        sharesRevision = revision;
      })
    : undefined;

  const require = (permission: string) => {
    if (!target.permissions.includes(permission)) {
      throw new PithyxError({ code: 'app.permission.denied', params: { permission } });
    }
  };

  /** Forwards a job's progress to the app until it finishes. */
  function follow(job: JobInfo): JobInfo {
    if (!FINISHED.includes(job.state) && !jobs.has(job.id)) {
      const stop = realtime.listen(`jobs/${String(job.id)}`, (data) => {
        const info = toJob(data as ApiJob);
        target.emit('jobChanged', info);
        if (FINISHED.includes(info.state)) {
          jobs.get(job.id)?.();
          jobs.delete(job.id);
        }
      });
      jobs.set(job.id, stop);
    }
    return job;
  }

  async function started(
    call: () => Promise<{ data?: ApiJob; error?: unknown; response: Response }>,
  ) {
    return follow(toJob(await daemon(async () => unwrap(await call()))));
  }

  const methods: MethodHandlers = {
    'files.shares': async () => {
      require('files.shares');
      const shares = await daemon(async () => unwrap(await api.GET('/api/shares')));
      return shares.map((share) => ({
        id: String(share.id),
        name: share.name,
        access: share.access === 'readWrite' ? 'readWrite' : 'read',
      }));
    },
    'files.list': async ({ location, path, cursor, limit, sort, order }) => {
      require('files.shares');
      const shareId = shareOf(location);
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
      const shareId = shareOf(location);
      // The topic of a folder comes with its listing; a one-entry page is the cheapest way to get it.
      const page = await daemon(async () =>
        unwrap(
          await api.GET('/api/files/{shareId}/list', {
            params: { path: { shareId }, query: { path, limit: 1 } },
          }),
        ),
      );
      const watch = `w${String(nextWatch++)}`;
      watches.set(
        watch,
        realtime.listen(page.topic, (data) => {
          const change = data as Partial<components['schemas']['FolderChanges']>;
          // The snapshot only says whether the folder is watched; changes come as events.
          if (change.changed === undefined) {
            return;
          }
          target.emit('folderChanged', {
            watch,
            changed: change.changed.map(toEntry),
            removed: change.removed ?? [],
            resync: change.resync === true,
            gone: change.gone === true,
          });
        }),
      );
      return { watch };
    },
    'files.unwatch': ({ watch }) => {
      watches.get(watch)?.();
      watches.delete(watch);
    },
    'files.upload': async ({ location, folder, name, data, overwrite, upload }) => {
      require('files.shares');
      const shareId = shareOf(location);
      const path = folder === '' ? name : `${folder}/${name}`;
      const url = `/api/files/${String(shareId)}/content?${query({ path, overwrite: overwrite === true ? 'true' : undefined })}`;
      const entry = await put(url, data, (loaded) => {
        if (upload !== undefined) {
          target.emit('uploadProgress', { upload, loaded, total: data.size });
        }
      });
      return toEntry(entry);
    },
    'files.download': ({ location, path }) => {
      require('files.shares');
      const link = document.createElement('a');
      link.href = `/api/files/${String(shareOf(location))}/content?${query({ path })}`;
      link.download = '';
      link.rel = 'noopener';
      document.body.append(link);
      link.click();
      link.remove();
    },
    'files.createFolder': async ({ location, path }) => {
      require('files.shares');
      const shareId = shareOf(location);
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
      const shareId = shareOf(location);
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
    'files.run': async ({ kind, source, target: destination, onConflict, permanent }) => {
      require('files.shares');
      const shareId = shareOf(source.location);
      const join = (name: string) => (source.folder === '' ? name : `${source.folder}/${name}`);
      const selection =
        'names' in source.items
          ? { items: source.items.names.map((name) => ({ shareId, path: join(name) })) }
          : { allIn: { folder: { shareId, path: source.folder }, except: source.items.except } };
      return started(() =>
        api.POST('/api/files/operations', {
          body: {
            kind,
            ...selection,
            ...(destination === undefined
              ? {}
              : { target: { shareId: shareOf(destination.location), path: destination.path } }),
            ...(onConflict === undefined ? {} : { onConflict }),
            permanent: permanent === true,
          },
        }),
      );
    },
    'files.versions': async ({ location, path }) => {
      require('files.shares');
      const shareId = shareOf(location);
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
      const shareId = shareOf(location);
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
      const shareId = shareOf(location);
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
      const shareId = shareOf(location);
      return started(() =>
        api.POST('/api/files/{shareId}/trash/restore', {
          params: { path: { shareId } },
          body: { ids, ...(onConflict === undefined ? {} : { onConflict }) },
        }),
      );
    },
    'trash.delete': async ({ location, ids }) => {
      require('files.shares');
      const shareId = shareOf(location);
      return started(() =>
        api.POST('/api/files/{shareId}/trash/delete', {
          params: { path: { shareId } },
          body: { ids },
        }),
      );
    },
    'trash.empty': async ({ location }) => {
      require('files.shares');
      const shareId = shareOf(location);
      return started(() =>
        api.POST('/api/files/{shareId}/trash/empty', { params: { path: { shareId } } }),
      );
    },
    'ui.showError': ({ problem }) => {
      showError(
        new PithyxApiError(0, {
          type: 'about:blank',
          title: problem.code,
          status: 0,
          detail: problem.detail ?? problem.code,
          code: problem.code as never,
          params: problem.params,
          requestId: problem.requestId ?? '',
        }),
      );
    },
  };

  return {
    methods,
    close: () => {
      stopShares?.();
      for (const stop of [...watches.values(), ...jobs.values()]) {
        stop();
      }
      watches.clear();
      jobs.clear();
    },
  };
}
