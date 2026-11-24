import { unwrap, type components } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import type { HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';
import { daemon } from './daemonError';

export type BackupTarget = components['schemas']['BackupTarget'];
export type BackupTargetList = components['schemas']['BackupTargetList'];
export type BackupHostKey = components['schemas']['BackupHostKey'];
export type BackupTargetRequest = components['schemas']['BackupTargetRequest'];
export type BackupJob = components['schemas']['BackupJob'];
export type BackupJobSettings = components['schemas']['BackupJobSettings'];
export type BackupJobCreated = components['schemas']['BackupJobCreated'];
export type BackupJobCreateRequest = components['schemas']['BackupJobCreateRequest'];
export type BackupRetention = components['schemas']['BackupRetention'];
export type BackupRun = components['schemas']['BackupRun'];
export type BackupSourceOption = components['schemas']['BackupSourceOption'];
export type BackupDay = components['schemas']['BackupDay'];
export type BackupDayList = components['schemas']['BackupDayList'];
export type BackupVersion = components['schemas']['BackupVersion'];
type BoardJob = components['schemas']['Job'];

export interface BackupHistoryQuery {
  jobId?: number;
  kind?: BackupRun['kind'];
  state?: BackupRun['state'];
  /** ISO 8601 */
  since?: string;
  /** ISO 8601, exclusive */
  until?: string;
  before?: number;
  limit?: number;
}

/** The Backup app's calls, outside the public SDK; the daemon checks the administrator right on every request behind them. */
interface BackupMethods {
  'internal.admin.backups.targets': { params: Record<string, never>; result: BackupTargetList };
  /** The host keys the server offers, Ed25519 first; nothing is stored. */
  'internal.admin.backups.scan': {
    params: { host: string; port?: number };
    result: { keys: BackupHostKey[] };
  };
  /** Saves a target with the confirmed host key; the daemon makes its own SSH key. */
  'internal.admin.backups.create': { params: BackupTargetRequest; result: BackupTarget };
  'internal.admin.backups.update': {
    params: BackupTargetRequest & { id: number };
    result: BackupTarget;
  };
  /** Pins a new host key after the administrator compared its fingerprint. */
  'internal.admin.backups.setHostKey': {
    params: { id: number; hostKey: string };
    result: BackupTarget;
  };
  /** Signs in, creates the folder if missing, writes and removes a test file; the outcome is the target's `lastTest`. */
  'internal.admin.backups.test': { params: { id: number }; result: BackupTarget };
  'internal.admin.backups.delete': { params: { id: number }; result: null };
  /** The shared folders and homes a job can back up. */
  'internal.admin.backups.sources': {
    params: Record<string, never>;
    result: { sources: BackupSourceOption[] };
  };
  'internal.admin.backups.jobs': { params: Record<string, never>; result: { jobs: BackupJob[] } };
  /** Makes the job's repository with a new key, returned only here, or connects to one with `existingKey`. */
  'internal.admin.backups.createJob': { params: BackupJobCreateRequest; result: BackupJobCreated };
  'internal.admin.backups.updateJob': {
    params: { id: number; settings: BackupJobSettings };
    result: BackupJob;
  };
  /** Deletes the job and its key; the repository stays on the target. */
  'internal.admin.backups.deleteJob': { params: { id: number }; result: null };
  'internal.admin.backups.runs': { params: { id: number }; result: { runs: BackupRun[] } };
  /** The runs and checks of every job, newest first; `before` is the id of the last run of the previous page. */
  'internal.admin.backups.history': {
    params: BackupHistoryQuery;
    result: { runs: BackupRun[] };
  };
  /** The runs per day in the box's time zone; `from` and `to` are yyyy-MM-dd, both included. */
  'internal.admin.backups.days': {
    params: { from?: string; to?: string };
    result: BackupDayList;
  };
  /** Starts a run or a check now; it goes on the job board. */
  'internal.admin.backups.start': {
    params: { id: number; kind: 'run' | 'check' };
    result: BoardJob;
  };
  /** Cancels a running run or check through its job on the board. */
  'internal.admin.backups.cancel': { params: { boardJobId: number }; result: null };
  /** The versions in a job's repository, newest first, as the box saw them after its last run. */
  'internal.admin.backups.versions': {
    params: { id: number };
    result: { versions: BackupVersion[] };
  };
  /** Protects a version from the retention or releases it again. */
  'internal.admin.backups.protect': {
    params: { id: number; versionId: number; protected: boolean };
    result: BackupVersion;
  };
  /** The job's repository key, after the password and, with two-factor sign-in on, a current code. */
  'internal.admin.backups.revealKey': {
    params: { id: number; password: string; totpCode?: string };
    result: { key: string };
  };
}

export const targetsKey = ['backups', 'targets'] as const;
export const jobsKey = ['backups', 'jobs'] as const;
export const sourcesKey = ['backups', 'sources'] as const;

/** A job's runs and checks. */
export const runsKey = (id: number) => ['backups', 'runs', id] as const;

/** The runs of every job; its queries invalidate together with every job's runs. */
export const historyKey = (query: BackupHistoryQuery) =>
  ['backups', 'runs', 'history', query] as const;

/** A job's versions. */
export const versionsKey = (id: number) => ['backups', 'versions', id] as const;

/** The runs per day; under the runs, so a finished run refreshes them. */
export const daysKey = (from: string, to: string) => ['backups', 'runs', 'days', from, to] as const;

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const hasId = (value: unknown): value is { id: number } =>
  isRecord(value) && Number.isSafeInteger(value.id);

const optionalString = (value: unknown) => value === undefined || typeof value === 'string';

const isPort = (value: unknown) => value === undefined || Number.isSafeInteger(value);

const isTarget = (value: unknown): value is BackupTargetRequest =>
  isRecord(value) &&
  typeof value.name === 'string' &&
  typeof value.host === 'string' &&
  typeof value.user === 'string' &&
  isPort(value.port) &&
  optionalString(value.path) &&
  optionalString(value.hostKey);

// The request without the id, and without fields left out, so the daemon sees what the administrator entered.
function body(params: BackupTargetRequest): BackupTargetRequest {
  return {
    name: params.name,
    host: params.host,
    user: params.user,
    ...(params.port === undefined ? {} : { port: params.port }),
    ...(params.path === undefined ? {} : { path: params.path }),
    ...(params.hostKey === undefined ? {} : { hostKey: params.hostKey }),
  };
}

const isStringArray = (value: unknown) =>
  value === undefined ||
  value === null ||
  (Array.isArray(value) && value.every((item) => typeof item === 'string'));

const isRetention = (value: unknown) =>
  isRecord(value) &&
  ['last', 'hourly', 'daily', 'weekly', 'monthly', 'yearly'].every((key) =>
    Number.isSafeInteger(value[key]),
  );

const isSettings = (value: unknown): value is BackupJobSettings =>
  isRecord(value) &&
  typeof value.name === 'string' &&
  typeof value.schedule === 'string' &&
  typeof value.enabled === 'boolean' &&
  Array.isArray(value.sources) &&
  value.sources.every(
    (source) =>
      isRecord(source) && Number.isSafeInteger(source.shareId) && typeof source.path === 'string',
  ) &&
  isRetention(value.retention) &&
  (value.checkSchedule === undefined ||
    value.checkSchedule === null ||
    typeof value.checkSchedule === 'string') &&
  Number.isSafeInteger(value.checkReadPercent) &&
  Number.isSafeInteger(value.uploadLimitKib) &&
  Number.isSafeInteger(value.downloadLimitKib) &&
  (value.overdueHours === undefined ||
    value.overdueHours === null ||
    Number.isSafeInteger(value.overdueHours)) &&
  isStringArray(value.excludes);

/** The settings as the daemon takes them, without anything else the caller passed along. */
function settingsBody(settings: BackupJobSettings): BackupJobSettings {
  const { last, hourly, daily, weekly, monthly, yearly } = settings.retention;
  return {
    name: settings.name,
    sources: settings.sources.map(({ shareId, path }) => ({ shareId, path })),
    schedule: settings.schedule,
    enabled: settings.enabled,
    retention: { last, hourly, daily, weekly, monthly, yearly },
    checkSchedule: settings.checkSchedule ?? null,
    checkReadPercent: settings.checkReadPercent,
    uploadLimitKib: settings.uploadLimitKib,
    downloadLimitKib: settings.downloadLimitKib,
    overdueHours: settings.overdueHours ?? null,
    excludes: settings.excludes ?? [],
  };
}

/** Only the filters that are set, so the daemon gets no empty parameters. */
function historyQuery({ jobId, kind, state, since, until, before, limit }: BackupHistoryQuery) {
  return {
    ...(jobId === undefined ? {} : { jobId }),
    ...(kind === undefined ? {} : { kind }),
    ...(state === undefined ? {} : { state }),
    ...(since === undefined ? {} : { since }),
    ...(until === undefined ? {} : { until }),
    ...(before === undefined ? {} : { before }),
    ...(limit === undefined ? {} : { limit }),
  };
}

/** The host side of the Backup app's window. */
export function backupsHost(): HostExtensions {
  return {
    'internal.admin.backups.targets': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/backups/targets'))),
    },
    'internal.admin.backups.scan': {
      validate: (value) => isRecord(value) && typeof value.host === 'string' && isPort(value.port),
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/backups/targets/host-keys', {
              body: params as BackupMethods['internal.admin.backups.scan']['params'],
            }),
          ),
        ),
    },
    'internal.admin.backups.create': {
      validate: isTarget,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/backups/targets', { body: body(params as BackupTargetRequest) }),
          ),
        ),
    },
    'internal.admin.backups.update': {
      validate: (value) => hasId(value) && isTarget(value),
      handle: (params) =>
        daemon(async () => {
          const target = params as BackupMethods['internal.admin.backups.update']['params'];
          return unwrap(
            await api.PUT('/api/backups/targets/{id}', {
              params: { path: { id: target.id } },
              body: body(target),
            }),
          );
        }),
    },
    'internal.admin.backups.setHostKey': {
      validate: (value) => isRecord(value) && typeof value.hostKey === 'string' && hasId(value),
      handle: (params) =>
        daemon(async () => {
          const { id, hostKey } =
            params as BackupMethods['internal.admin.backups.setHostKey']['params'];
          return unwrap(
            await api.PUT('/api/backups/targets/{id}/host-key', {
              params: { path: { id } },
              body: { hostKey },
            }),
          );
        }),
    },
    'internal.admin.backups.test': {
      validate: hasId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/backups/targets/{id}/test', {
              params: { path: { id: (params as { id: number }).id } },
            }),
          ),
        ),
    },
    'internal.admin.backups.delete': {
      validate: hasId,
      handle: (params) =>
        daemon(async () => {
          unwrap(
            await api.DELETE('/api/backups/targets/{id}', {
              params: { path: { id: (params as { id: number }).id } },
            }),
          );
          return null;
        }),
    },
    'internal.admin.backups.sources': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/backups/sources'))),
    },
    'internal.admin.backups.jobs': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/backups/jobs'))),
    },
    'internal.admin.backups.createJob': {
      validate: (value) =>
        isRecord(value) &&
        Number.isSafeInteger(value.targetId) &&
        typeof value.repository === 'string' &&
        (value.existingKey === undefined ||
          value.existingKey === null ||
          typeof value.existingKey === 'string') &&
        isSettings(value.settings),
      handle: (params) =>
        daemon(async () => {
          const request = params as BackupJobCreateRequest;
          return unwrap(
            await api.POST('/api/backups/jobs', {
              body: {
                targetId: request.targetId,
                repository: request.repository,
                existingKey: request.existingKey ?? null,
                settings: settingsBody(request.settings),
              },
            }),
          );
        }),
    },
    'internal.admin.backups.updateJob': {
      validate: (value) => hasId(value) && isSettings((value as { settings?: unknown }).settings),
      handle: (params) =>
        daemon(async () => {
          const { id, settings } =
            params as BackupMethods['internal.admin.backups.updateJob']['params'];
          return unwrap(
            await api.PUT('/api/backups/jobs/{id}', {
              params: { path: { id } },
              body: settingsBody(settings),
            }),
          );
        }),
    },
    'internal.admin.backups.deleteJob': {
      validate: hasId,
      handle: (params) =>
        daemon(async () => {
          unwrap(
            await api.DELETE('/api/backups/jobs/{id}', {
              params: { path: { id: (params as { id: number }).id } },
            }),
          );
          return null;
        }),
    },
    'internal.admin.backups.runs': {
      validate: hasId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.GET('/api/backups/jobs/{id}/runs', {
              params: { path: { id: (params as { id: number }).id }, query: { limit: 20 } },
            }),
          ),
        ),
    },
    'internal.admin.backups.history': {
      validate: (value) =>
        isRecord(value) &&
        ['jobId', 'before', 'limit'].every(
          (key) => value[key] === undefined || Number.isSafeInteger(value[key]),
        ) &&
        optionalString(value.kind) &&
        optionalString(value.state) &&
        optionalString(value.since) &&
        optionalString(value.until),
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.GET('/api/backups/runs', {
              params: { query: historyQuery(params as BackupHistoryQuery) },
            }),
          ),
        ),
    },
    'internal.admin.backups.days': {
      validate: (value) =>
        isRecord(value) && optionalString(value.from) && optionalString(value.to),
      handle: (params) =>
        daemon(async () => {
          const { from, to } = params as BackupMethods['internal.admin.backups.days']['params'];
          return unwrap(
            await api.GET('/api/backups/days', {
              params: {
                query: {
                  ...(from === undefined ? {} : { from }),
                  ...(to === undefined ? {} : { to }),
                },
              },
            }),
          );
        }),
    },
    'internal.admin.backups.start': {
      validate: (value) =>
        isRecord(value) && (value.kind === 'run' || value.kind === 'check') && hasId(value),
      handle: (params) =>
        daemon(async () => {
          const { id, kind } = params as BackupMethods['internal.admin.backups.start']['params'];
          const options = { params: { path: { id } } };
          return unwrap(
            kind === 'run'
              ? await api.POST('/api/backups/jobs/{id}/run', options)
              : await api.POST('/api/backups/jobs/{id}/check', options),
          );
        }),
    },
    'internal.admin.backups.cancel': {
      validate: (value) => isRecord(value) && Number.isSafeInteger(value.boardJobId),
      handle: (params) =>
        daemon(async () => {
          unwrap(
            await api.POST('/api/jobs/{id}/cancel', {
              params: { path: { id: (params as { boardJobId: number }).boardJobId } },
            }),
          );
          return null;
        }),
    },
    'internal.admin.backups.versions': {
      validate: hasId,
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.GET('/api/backups/jobs/{id}/versions', {
              params: { path: { id: (params as { id: number }).id } },
            }),
          ),
        ),
    },
    'internal.admin.backups.protect': {
      validate: (value) =>
        hasId(value) &&
        Number.isSafeInteger((value as { versionId?: unknown }).versionId) &&
        typeof (value as { protected?: unknown }).protected === 'boolean',
      handle: (params) =>
        daemon(async () => {
          const {
            id,
            versionId,
            protected: keep,
          } = params as BackupMethods['internal.admin.backups.protect']['params'];
          return unwrap(
            await api.PUT('/api/backups/jobs/{id}/versions/{versionId}/protection', {
              params: { path: { id, versionId } },
              body: { protected: keep },
            }),
          );
        }),
    },
    'internal.admin.backups.revealKey': {
      validate: (value) =>
        hasId(value) &&
        typeof (value as { password?: unknown }).password === 'string' &&
        optionalString((value as { totpCode?: unknown }).totpCode),
      handle: (params) =>
        daemon(async () => {
          const { id, password, totpCode } =
            params as BackupMethods['internal.admin.backups.revealKey']['params'];
          return unwrap(
            await api.POST('/api/backups/jobs/{id}/key', {
              params: { path: { id } },
              body: { password, ...(totpCode === undefined ? {} : { totpCode }) },
            }),
          );
        }),
    },
  };
}

/** Calls one of the Backup app's methods through the SDK. */
export async function backupsCall<M extends keyof BackupMethods>(
  app: PithyxApp,
  method: M,
  params: BackupMethods[M]['params'],
): Promise<BackupMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as BackupMethods[M]['result'];
}
