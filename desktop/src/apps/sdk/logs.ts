import { unwrap, type components, type ErrorCode } from '@pithyx/api-client';
import type { EventName, MethodName, PithyxApp } from '@pithyx/sdk';
import { type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { realtime } from '../../realtime/connection';
import type { DescribedError } from '../../errors/describe';
import { daemon } from './daemonError';

export type LogEntry = components['schemas']['LogEntry'];
export type LogPage = components['schemas']['LogPage'];
export type LogSettings = components['schemas']['LogSettings'];
export type LogStatus = components['schemas']['LogStatus'];
export type LogLevelName = LogEntry['level'];
export type LogCount = components['schemas']['LogCount'];
export type LogsAdded = components['schemas']['LogsAdded'];

/** The entries of every write while the Log Center follows `system/logs`. */
export const LOGS_ADDED = 'internal.admin.logs.added';

/** The filters of GET /api/logs; every one is optional. */
export interface LogFilter {
  from?: string;
  to?: string;
  level?: LogLevelName[];
  source?: string[];
  search?: string;
  cursor?: string;
  limit?: number;
}

/** The Log Center's calls, outside the public SDK; the daemon checks the administrator right on every request. */
interface LogMethods {
  'internal.admin.logs.query': { params: LogFilter; result: LogPage };
  'internal.admin.logs.sources': { params: Record<string, never>; result: string[] };
  'internal.admin.logs.counts': { params: { from: string }; result: LogCount[] };
  'internal.admin.logs.watch': { params: { watch: boolean }; result: null };
  'internal.admin.logs.settings': { params: Record<string, never>; result: LogStatus };
  'internal.admin.logs.setSettings': { params: LogSettings; result: LogStatus };
  'internal.admin.logs.export': { params: LogFilter & { format: 'jsonl' | 'text' }; result: null };
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const isStrings = (value: unknown) =>
  value === undefined || (Array.isArray(value) && value.every((item) => typeof item === 'string'));

function isFilter(value: unknown): value is LogFilter {
  return (
    isRecord(value) &&
    ['from', 'to', 'search', 'cursor'].every(
      (key) => value[key] === undefined || typeof value[key] === 'string',
    ) &&
    isStrings(value.level) &&
    isStrings(value.source) &&
    (value.limit === undefined || Number.isSafeInteger(value.limit))
  );
}

function isSettings(value: unknown): value is LogSettings {
  return (
    isRecord(value) &&
    Number.isSafeInteger(value.retentionDays) &&
    Number.isSafeInteger(value.maxBytes) &&
    typeof value.archiveEnabled === 'boolean' &&
    (value.archivePool === null || typeof value.archivePool === 'string') &&
    Number.isSafeInteger(value.archiveRetentionDays) &&
    Number.isSafeInteger(value.archiveMaxBytes)
  );
}

/** The query string of a filter; arrays repeat their key, as the daemon expects. */
export function logQuery(filter: LogFilter & { format?: string }): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(filter)) {
    for (const item of Array.isArray(value) ? value : [value]) {
      if (item !== undefined && item !== '') {
        query.append(key, String(item));
      }
    }
  }
  return query.toString();
}

/**
 * The host side of the Log Center's window. While the app watches, it follows `system/logs`, whose events carry the
 * new entries themselves: asking the daemon again would log that request and feed itself.
 */
export function logsHost(emit: (event: string, data?: unknown) => void): {
  extensions: HostExtensions;
  close: () => void;
} {
  let stop: (() => void) | null = null;
  const unwatch = () => {
    stop?.();
    stop = null;
  };
  return {
    extensions: {
      'internal.admin.logs.counts': {
        validate: (value) => isRecord(value) && typeof value.from === 'string',
        handle: (params) =>
          daemon(async () =>
            unwrap(
              await api.GET('/api/logs/counts', {
                params: { query: { from: (params as { from: string }).from } },
              }),
            ),
          ),
      },
      'internal.admin.logs.watch': {
        validate: (value) => isRecord(value) && typeof value.watch === 'boolean',
        handle: (params) => {
          if (!(params as { watch: boolean }).watch) {
            unwatch();
          } else {
            stop ??= realtime.listen('system/logs', (data) => {
              emit(LOGS_ADDED, data);
            });
          }
          return Promise.resolve(null);
        },
      },
      'internal.admin.logs.query': {
        validate: isFilter,
        handle: (params) =>
          daemon(async () =>
            unwrap(await api.GET('/api/logs', { params: { query: params as LogFilter } })),
          ),
      },
      'internal.admin.logs.sources': {
        validate: isRecord,
        handle: () => daemon(async () => unwrap(await api.GET('/api/logs/sources'))),
      },
      'internal.admin.logs.settings': {
        validate: isRecord,
        handle: () => daemon(async () => unwrap(await api.GET('/api/logs/settings'))),
      },
      'internal.admin.logs.setSettings': {
        validate: isSettings,
        handle: (params) =>
          daemon(async () =>
            unwrap(await api.PUT('/api/logs/settings', { body: params as LogSettings })),
          ),
      },
      'internal.admin.logs.export': {
        validate: (value) =>
          isFilter(value) && (value as { format?: unknown }).format !== undefined,
        handle: (params) => {
          // A plain link keeps the session cookie and lets the browser stream the file to disk.
          const link = document.createElement('a');
          link.href = `/api/logs/export?${logQuery(params as LogFilter)}`;
          link.download = '';
          link.rel = 'noopener';
          document.body.append(link);
          link.click();
          link.remove();
          return null;
        },
      },
    },
    close: unwatch,
  };
}

/** The entries of every write while the Log Center watches; the first event is the topic's empty snapshot. */
export function onLogs(app: PithyxApp, handler: (added: LogsAdded) => void): () => void {
  return app.on(LOGS_ADDED as unknown as EventName, (data) => {
    handler(data as unknown as LogsAdded);
  });
}

/** Calls one of the Log Center's methods through the SDK. */
export async function logsCall<M extends keyof LogMethods>(
  app: PithyxApp,
  method: M,
  params: LogMethods[M]['params'],
): Promise<LogMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as LogMethods[M]['result'];
}

/** A failed call as the error notice shows it: the problem the SDK passed on. */
export function describeProblem(error: unknown): DescribedError {
  const problem = error as { code?: string; params?: Record<string, unknown>; requestId?: string };
  return {
    code: (problem.code ?? 'system.internal.unexpected') as ErrorCode,
    params: problem.params ?? {},
    requestId: problem.requestId ?? '',
  };
}
