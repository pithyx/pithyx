import { unwrap, type components } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';
import { endTransfer, reportProgress, startTransfer } from '../../shell/transfers';
import { toPithyxError } from './daemonError';

export type UpdateStatus = components['schemas']['UpdateStatus'];
export type UpdateHistoryEntry = components['schemas']['UpdateHistoryEntry'];
export type SnapshotInfo = components['schemas']['SnapshotInfo'];
export type Job = components['schemas']['Job'];

/** The query key of Settings > Updates. */
export const updatesKey = ['settings', 'updates'] as const;

/** Every change of `system/updates`, the first one being its snapshot. */
export const UPDATES_STATE = 'internal.admin.updates.state';

/** Every change of the job of an update or rollback this window started. */
export const UPDATES_JOB = 'internal.admin.updates.job';

/** The update calls of Settings, outside the public SDK; the daemon checks the administrator right on each. */
interface UpdateMethods {
  'internal.admin.updates.get': { params: Record<string, never>; result: UpdateStatus };
  'internal.admin.updates.check': { params: Record<string, never>; result: UpdateStatus };
  'internal.admin.updates.settings': {
    params: { checkEnabled: boolean; channel: string };
    result: UpdateStatus;
  };
  /** Streams an update file with its progress in the taskbar; the daemon checks it completely before answering. */
  'internal.admin.updates.upload': { params: { file: Blob; name: string }; result: UpdateStatus };
  'internal.admin.updates.discard': { params: Record<string, never>; result: UpdateStatus };
  'internal.admin.updates.install': {
    params: { source: 'online' | 'file'; includeDebian: boolean };
    result: Job;
  };
  'internal.admin.updates.rollback': { params: { snapshot: string; version: string }; result: Job };
  /** Keeps a checked pithyx-source.json for the recovery menu, which alone switches the source (D339). */
  'internal.admin.updates.source.save': { params: { text: string }; result: UpdateStatus };
  'internal.admin.updates.source.discard': { params: Record<string, never>; result: UpdateStatus };
}

type UpdateName = keyof UpdateMethods;

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const hasText = (value: Record<string, unknown>, key: string) =>
  typeof value[key] === 'string' && value[key] !== '';

async function daemon<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    throw toPithyxError(error);
  }
}

function problemOf(status: number, body: string): PithyxError {
  try {
    const problem = JSON.parse(body) as { code?: unknown; params?: Record<string, unknown> };
    if (typeof problem.code === 'string') {
      return new PithyxError({ code: problem.code, params: problem.params ?? {} });
    }
  } catch {
    // Not a problem document; fall through.
  }
  return new PithyxError({ code: 'system.internal.unexpected', params: { status } });
}

/** Streams an update file to the daemon with its progress in the taskbar, which fetch cannot report. */
function upload(file: Blob, name: string): Promise<UpdateStatus> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    const transfer = startTransfer('upload', name, file.size, () => {
      request.abort();
    });
    request.open('PUT', '/api/system/updates/file');
    request.setRequestHeader('X-Pithyx-Request', '1');
    request.setRequestHeader('Content-Type', 'application/octet-stream');
    request.upload.onprogress = (event) => {
      reportProgress(transfer, event.loaded);
    };
    request.onload = () => {
      endTransfer(transfer);
      if (request.status >= 200 && request.status < 300) {
        resolve(JSON.parse(request.responseText) as UpdateStatus);
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
      reject(new PithyxError({ code: 'sdk.box.unreachable', params: {} }));
    };
    request.send(file);
  });
}

/** Sends the exact bytes of a source file: the daemon checks them as the recovery menu does, duplicate keys included. */
async function saveSource(text: string): Promise<UpdateStatus> {
  const response = await fetch('/api/system/updates/source', {
    method: 'PUT',
    headers: { 'X-Pithyx-Request': '1', 'Content-Type': 'application/json' },
    body: text,
  });
  const body = await response.text();
  if (!response.ok) {
    throw problemOf(response.status, body);
  }
  return JSON.parse(body) as UpdateStatus;
}

/**
 * The host side of Settings > Updates: the topic `system/updates` while the window is open and the jobs it starts. The
 * returned `close` stops following both.
 */
export function updatesHost(emit: (event: string, data?: unknown) => void): {
  extensions: HostExtensions;
  close: () => void;
} {
  const stops = [
    realtime.listen('system/updates', (data) => {
      emit(UPDATES_STATE, data);
    }),
  ];
  const follow = (job: Job) => {
    stops.push(
      realtime.listen(`jobs/${String(job.id)}`, (data) => {
        emit(UPDATES_JOB, data);
      }),
    );
    return job;
  };
  const extensions: Record<UpdateName, HostExtensions[string]> = {
    'internal.admin.updates.get': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/system/updates'))),
    },
    'internal.admin.updates.check': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.POST('/api/system/updates/check'))),
    },
    'internal.admin.updates.settings': {
      validate: (value) =>
        isRecord(value) && typeof value.checkEnabled === 'boolean' && hasText(value, 'channel'),
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.PUT('/api/system/updates/settings', {
              body: params as UpdateMethods['internal.admin.updates.settings']['params'],
            }),
          ),
        ),
    },
    'internal.admin.updates.upload': {
      validate: (value) => isRecord(value) && value.file instanceof Blob && hasText(value, 'name'),
      handle: (params) => {
        const { file, name } = params as UpdateMethods['internal.admin.updates.upload']['params'];
        return upload(file, name);
      },
    },
    'internal.admin.updates.discard': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.DELETE('/api/system/updates/file'))),
    },
    'internal.admin.updates.install': {
      validate: (value) =>
        isRecord(value) &&
        (value.source === 'online' || value.source === 'file') &&
        typeof value.includeDebian === 'boolean',
      handle: (params) =>
        daemon(async () =>
          follow(
            unwrap(
              await api.POST('/api/system/updates/install', {
                body: params as UpdateMethods['internal.admin.updates.install']['params'],
              }),
            ),
          ),
        ),
    },
    'internal.admin.updates.rollback': {
      validate: (value) =>
        isRecord(value) && hasText(value, 'snapshot') && hasText(value, 'version'),
      handle: (params) =>
        daemon(async () =>
          follow(
            unwrap(
              await api.POST('/api/system/updates/rollback', {
                body: params as UpdateMethods['internal.admin.updates.rollback']['params'],
              }),
            ),
          ),
        ),
    },
    'internal.admin.updates.source.save': {
      validate: (value) => isRecord(value) && hasText(value, 'text'),
      handle: (params) =>
        saveSource((params as UpdateMethods['internal.admin.updates.source.save']['params']).text),
    },
    'internal.admin.updates.source.discard': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.DELETE('/api/system/updates/source'))),
    },
  };
  return {
    extensions,
    close: () => {
      for (const stop of stops) {
        stop();
      }
    },
  };
}

export async function updatesCall<M extends UpdateName>(
  app: PithyxApp,
  method: M,
  params: UpdateMethods[M]['params'],
): Promise<UpdateMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as UpdateMethods[M]['result'];
}

export function onUpdates(
  app: PithyxApp,
  event: typeof UPDATES_STATE | typeof UPDATES_JOB,
  handler: (data: unknown) => void,
): () => void {
  return app.on(event as unknown as EventName, handler);
}
