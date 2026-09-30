import { PithyxApiError, unwrap, type components, type ErrorCode } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import type { DescribedError } from '../../errors/describe';

export type LogEntry = components['schemas']['LogEntry'];
export type LogPage = components['schemas']['LogPage'];
export type LogSettings = components['schemas']['LogSettings'];
export type LogStatus = components['schemas']['LogStatus'];
export type LogLevelName = LogEntry['level'];

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

/** The host side of the Log Center's window. */
export function logsHost(): { extensions: HostExtensions; close: () => void } {
  return {
    extensions: {
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
    close: () => undefined,
  };
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
