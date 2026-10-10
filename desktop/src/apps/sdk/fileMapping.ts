import { PithyxApiError, type components } from '@pithyx/api-client';
import type {
  Events,
  FileEntry,
  FileLocation,
  FileOperation,
  FileVersion,
  JobInfo,
  Problem,
  TrashPage,
} from '@pithyx/sdk';
import { PithyxError } from '@pithyx/sdk/host';
import { showError } from '../../errors/errorStore';
import { realtime } from '../../realtime/connection';
import { endTransfer, reportProgress, startTransfer } from '../../shell/transfers';

export type ApiEntry = components['schemas']['FileEntry'];
export type ApiJob = components['schemas']['Job'];
type ApiTrashPage = components['schemas']['TrashPage'];
type ApiFileVersionList = components['schemas']['FileVersionList'];

/** Sends an event to the app that owns a broker. */
export type Emit = <E extends keyof Events>(event: E, data: Events[E]) => void;

const FINISHED: readonly JobInfo['state'][] = ['succeeded', 'failed', 'cancelled', 'interrupted'];

export function toEntry(entry: ApiEntry | components['schemas']['AppFileEntry']): FileEntry {
  return { name: entry.name, kind: entry.kind, size: entry.size, modified: entry.modified };
}

export function toJob(job: ApiJob): JobInfo {
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

export function toTrashPage(page: ApiTrashPage): TrashPage {
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

export function toVersions(list: ApiFileVersionList): FileVersion[] {
  return list.versions.map((version) => ({
    snapshot: version.snapshot,
    createdAt: version.createdAt,
    kind: version.kind,
    size: version.size,
    modified: version.modified,
  }));
}

/** The same object without the keys whose value is undefined, as optional query parameters want it. */
export function defined<T extends object>(value: T): { [K in keyof T]?: Exclude<T[K], undefined> } {
  return Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined)) as {
    [K in keyof T]?: Exclude<T[K], undefined>;
  };
}

export function query(values: Record<string, string | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined) {
      search.set(key, value);
    }
  }
  return search.toString();
}

/** The selection of a `files.run` call as the daemon's operation request wants it, for shares. */
export function operationBody(params: FileOperation) {
  const { kind, source, target: destination, onConflict, permanent } = params;
  const shareId = shareIdOf(source.location);
  const join = (name: string) => (source.folder === '' ? name : `${source.folder}/${name}`);
  const selection =
    'names' in source.items
      ? { items: source.items.names.map((name) => ({ shareId, path: join(name) })) }
      : { allIn: { folder: { shareId, path: source.folder }, except: source.items.except } };
  return {
    kind,
    ...selection,
    ...(destination === undefined
      ? {}
      : { target: { shareId: shareIdOf(destination.location), path: destination.path } }),
    ...(onConflict === undefined ? {} : { onConflict }),
    permanent: permanent === true,
  };
}

/** The daemon's share id of a location. Named storage has no share id of its own and is refused. */
export function shareIdOf(location: FileLocation): number {
  const id = 'share' in location ? Number(location.share) : Number.NaN;
  if (!Number.isSafeInteger(id)) {
    throw new PithyxError({ code: 'fs.share.not_found', params: {} });
  }
  return id;
}

export function problemOf(status: number, body: string): PithyxError {
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

/**
 * Streams a file to the daemon with progress, shown in the taskbar with a cancel button; fetch cannot report upload
 * progress. `headers` carry the credentials of the caller (the session's CSRF header, or an app's host token). A
 * cancelled upload rejects with `sdk.transfer.cancelled`.
 */
export function putFile<T = ApiEntry>(
  url: string,
  name: string,
  data: Blob,
  onProgress: (loaded: number) => void,
  headers: Record<string, string> = { 'X-Pithyx-Request': '1' },
): Promise<T> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    const transfer = startTransfer('upload', name, data.size, () => {
      request.abort();
    });
    request.open('PUT', url);
    for (const [key, value] of Object.entries(headers)) {
      request.setRequestHeader(key, value);
    }
    request.setRequestHeader('Content-Type', 'application/octet-stream');
    request.upload.onprogress = (event) => {
      reportProgress(transfer, event.loaded);
      onProgress(event.loaded);
    };
    request.onload = () => {
      const ok = request.status >= 200 && request.status < 300;
      endTransfer(transfer, !ok);
      if (ok) {
        resolve(JSON.parse(request.responseText) as T);
      } else {
        reject(problemOf(request.status, request.responseText));
      }
    };
    request.onabort = () => {
      endTransfer(transfer);
      reject(new PithyxError({ code: 'sdk.transfer.cancelled', params: {} }));
    };
    request.onerror = () => {
      endTransfer(transfer, true);
      reject(new PithyxError({ code: 'fs.upload.incomplete', params: {} }));
    };
    request.send(data);
  });
}

/** Forwards the progress of the jobs an app started to it, as `jobChanged`, until they finish. */
export function jobFollower(emit: Emit) {
  const jobs = new Map<number, () => void>();
  return {
    follow(job: JobInfo): JobInfo {
      if (!FINISHED.includes(job.state) && !jobs.has(job.id)) {
        const stop = realtime.listen(`jobs/${String(job.id)}`, (data) => {
          const info = toJob(data as ApiJob);
          emit('jobChanged', info);
          if (FINISHED.includes(info.state)) {
            jobs.get(job.id)?.();
            jobs.delete(job.id);
          }
        });
        jobs.set(job.id, stop);
      }
      return job;
    },
    close() {
      for (const stop of jobs.values()) {
        stop();
      }
      jobs.clear();
    },
  };
}

/**
 * The live changes of the folders an app watches, as `folderChanged`. `topic` is the folder's topic from its listing;
 * `allowed` is asked for every change, so a revoked permission ends the flow of names at once.
 */
export function folderWatcher(emit: Emit) {
  const watches = new Map<string, () => void>();
  let next = 1;
  return {
    start(topic: string, allowed: () => boolean = () => true): string {
      const watch = `w${String(next++)}`;
      watches.set(
        watch,
        realtime.listen(topic, (data) => {
          const change = data as Partial<components['schemas']['FolderChanges']>;
          // The snapshot only says whether the folder is watched; changes come as events.
          if (change.changed === undefined || !allowed()) {
            return;
          }
          emit('folderChanged', {
            watch,
            changed: change.changed.map(toEntry),
            removed: change.removed ?? [],
            resync: change.resync === true,
            gone: change.gone === true,
          });
        }),
      );
      return watch;
    },
    stop(watch: string) {
      watches.get(watch)?.();
      watches.delete(watch);
    },
    close() {
      for (const stop of watches.values()) {
        stop();
      }
      watches.clear();
    },
  };
}

/**
 * `sharesChanged` for an app: the topic says only that the user's share list may have changed, and its first message is
 * the current revision. `allowed` is asked at every change. Returns the function that stops listening.
 */
export function sharesWatcher(emit: Emit, allowed: () => boolean): () => void {
  let revision: number | undefined;
  return realtime.listen('shares/list', (data) => {
    const next = (data as components['schemas']['ShareListRevision']).revision;
    if (revision !== undefined && next !== revision && allowed()) {
      emit('sharesChanged', {});
    }
    revision = next;
  });
}

/** The desktop's error dialog for a problem an app reports with `ui.showError`. */
export function showProblem(problem: Problem): void {
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
}
