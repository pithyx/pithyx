import type { components } from '@pithyx/api-client';
import type { BackupHistoryQuery, BackupJob, BackupRun, BackupTarget } from '../apps/sdk/backups';
import { useRealtime } from '../realtime/connection';
import { now } from './sampleData';
import type { ExtraAnswers } from './storageSample';

// Sample data of the Backup app (D414, D415, D416): two targets, three backups (one backing up right now, one fine, one
// failed), the runs of the selected one and two weeks of history. Names of servers and folders are user data; keys and fingerprints are invented.

const at = (hoursAgo: number) => new Date(now.getTime() - hoursAgo * 3_600_000).toISOString();

const hostKey = {
  type: 'ssh-ed25519',
  fingerprint: 'SHA256:q3Vd0m9cWwqK7f2lZ1y8u5mYwYt0b6g8bXUeJr1pS4c',
  key: 'AAAAC3NzaC1lZDI1NTE5AAAAIK8xNq2c7pXy4Qm0WkV3sGf9a1bR5tZ2yHcJ6eL0dPqU',
};

export const sampleTargets: BackupTarget[] = [
  {
    id: 1,
    kind: 'sftp',
    name: 'Offsite server',
    host: 'backup.example.net',
    port: 23,
    user: 'u412',
    path: 'pithyx',
    hostKey,
    publicKey:
      'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFh3kQ8vT2nYc5pW0bZ7xR4mL1sD9gK6aE3uJ2oN8iVf pithyx-backup@nas-living-room',
    lastTest: { succeeded: true, testedAt: at(30), errorCode: null, errorParams: null },
    createdAt: at(24 * 40),
  },
  {
    id: 2,
    kind: 'sftp',
    name: 'Second box',
    host: '192.168.1.20',
    port: 22,
    user: 'backup',
    path: '',
    hostKey: { ...hostKey, fingerprint: 'SHA256:7bXc1nQpR8vW2eY5tZ0aL4mK9sD3gF6hJ1uI8oP2qRs' },
    publicKey:
      'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGq9wE2rT5yU8iO1pA4sD7fG0hJ3kL6zX9cV2bN5mQ8w pithyx-backup@nas-living-room',
    lastTest: { succeeded: true, testedAt: at(50), errorCode: null, errorParams: null },
    createdAt: at(24 * 12),
  },
];

const sources = [
  { shareId: 2, name: 'Photos', home: false, available: true, supported: true },
  { shareId: 3, name: 'Documents', home: false, available: true, supported: true },
  { shareId: 4, name: 'Media', home: false, available: true, supported: true },
  { shareId: 11, name: 'homes/alex', home: true, available: true, supported: true },
  { shareId: 12, name: 'homes/sam', home: true, available: true, supported: true },
];

const retention = { last: 0, hourly: 0, daily: 7, weekly: 4, monthly: 12, yearly: 0 };

function run(
  id: number,
  hoursAgo: number,
  minutes: number,
  extra: Partial<BackupRun> = {},
): BackupRun {
  return {
    id,
    jobId: 2,
    kind: 'backup',
    trigger: 'schedule',
    state: 'succeeded',
    startedAt: at(hoursAgo),
    finishedAt: at(hoursAgo - minutes / 60),
    snapshot: 'a1b2c3d4',
    files: 48_212,
    bytes: 412_000_000_000,
    addedBytes: 1_240_000_000,
    warnings: 0,
    errorCode: null,
    errorParams: null,
    boardJobId: null,
    unreadable: [],
    ...extra,
  };
}

const documentsRuns: BackupRun[] = [
  run(110, 12.5, 6),
  run(109, 36.5, 4, {
    state: 'warning',
    warnings: 2,
    addedBytes: 860_000_000,
    unreadable: [
      { path: 'Documents/Taxes/2025/.~lock.return.ods#', reason: 'permission' },
      { path: 'Documents/Scans/tmp-0412.pdf', reason: 'missing' },
    ],
  }),
  run(108, 60.5, 5, { addedBytes: 310_000_000 }),
  run(107, 84.5, 3, {
    kind: 'check',
    files: null,
    bytes: null,
    addedBytes: null,
    snapshot: null,
  }),
  run(106, 108.5, 41, { trigger: 'manual', addedBytes: 96_000_000_000 }),
].map((entry) =>
  entry.kind === 'backup' ? { ...entry, files: 9_842, bytes: 23_400_000_000 } : entry,
);

const settings = (name: string, sourceList: { shareId: number; path: string }[]) => ({
  name,
  sources: sourceList,
  schedule: '0 2 * * *',
  enabled: true,
  retention,
  checkSchedule: '0 5 * * 0',
  checkReadPercent: 5,
  uploadLimitKib: 0,
  downloadLimitKib: 0,
  overdueHours: null,
  excludes: [],
});

export const sampleJobs: BackupJob[] = [
  {
    id: 1,
    kind: 'versioned',
    targetId: 1,
    repository: 'photos',
    settings: settings('Photos and homes', [
      { shareId: 2, path: '' },
      { shareId: 11, path: '' },
      { shareId: 12, path: '' },
    ]),
    lastRun: run(120, 0.4, 0, {
      jobId: 1,
      state: 'running',
      trigger: 'manual',
      finishedAt: null,
      files: null,
      bytes: null,
      addedBytes: null,
      snapshot: null,
      boardJobId: 501,
    }),
    lastCheck: run(118, 80, 12, {
      jobId: 1,
      kind: 'check',
      files: null,
      bytes: null,
      addedBytes: null,
    }),
    lastSuccessAt: at(12.4),
    nextRunAt: at(-11.5),
    nextCheckAt: at(-80),
    overdue: false,
    createdAt: at(24 * 40),
  },
  {
    id: 2,
    kind: 'versioned',
    targetId: 1,
    repository: 'documents',
    settings: settings('Documents', [{ shareId: 3, path: '' }]),
    lastRun: documentsRuns[0] ?? null,
    lastCheck: documentsRuns[3] ?? null,
    lastSuccessAt: at(12.4),
    nextRunAt: at(-11.5),
    nextCheckAt: at(-80),
    overdue: false,
    createdAt: at(24 * 30),
  },
  {
    id: 3,
    kind: 'versioned',
    targetId: 2,
    repository: 'media',
    settings: {
      ...settings('Media to the second box', [{ shareId: 4, path: 'Music' }]),
      schedule: '0 3 * * 6',
      checkSchedule: null,
    },
    lastRun: run(115, 101, 2, {
      jobId: 3,
      state: 'failed',
      files: null,
      bytes: null,
      addedBytes: null,
      snapshot: null,
      errorCode: 'backup.target.unreachable',
      errorParams: { reason: 'timeout' },
    }),
    lastCheck: null,
    lastSuccessAt: at(24 * 18),
    nextRunAt: at(-24 * 2.5),
    nextCheckAt: null,
    overdue: true,
    createdAt: at(24 * 20),
  },
];

// Two weeks of nightly runs of the first two backups and the weekly ones of the third, newest first.
const olderRuns: BackupRun[] = Array.from({ length: 13 }, (_, day) => [
  run(300 + day * 2, 24 * (day + 1) + 22, 14, {
    jobId: 1,
    files: 52_118,
    bytes: 431_000_000_000,
    addedBytes: 2_100_000_000,
    ...(day === 8 ? { state: 'warning' as const, warnings: 1 } : {}),
  }),
  ...(day >= 3
    ? [run(301 + day * 2, 24 * (day + 1) + 12.5, 5, { files: 9_840, bytes: 23_300_000_000 })]
    : []),
]).flat();
const mediaRuns: BackupRun[] = [
  ...(sampleJobs[2]?.lastRun === undefined || sampleJobs[2].lastRun === null
    ? []
    : [sampleJobs[2].lastRun]),
  run(114, 101 + 168, 48, {
    jobId: 3,
    files: 61_004,
    bytes: 402_000_000_000,
    addedBytes: 22_800_000_000,
  }),
];
const history: BackupRun[] = [
  ...(sampleJobs[0]?.lastRun ? [sampleJobs[0].lastRun] : []),
  ...documentsRuns,
  ...mediaRuns,
  ...olderRuns,
].sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt));

/** The answers of the Backup app's calls in the gallery. */
export const backupAnswers: ExtraAnswers = {
  'internal.admin.backups.targets': () => ({ targets: sampleTargets }),
  'internal.admin.backups.sources': () => ({ sources }),
  'internal.admin.backups.jobs': () => ({ jobs: sampleJobs }),
  'internal.admin.backups.runs': (params) => ({
    runs: (params as { id: number }).id === 2 ? documentsRuns : [],
  }),
  'internal.admin.backups.history': (params) => {
    const { jobId, kind, state, since, limit } = params as BackupHistoryQuery;
    return {
      runs: history
        .filter(
          (entry) =>
            (jobId === undefined || entry.jobId === jobId) &&
            (kind === undefined || entry.kind === kind) &&
            (state === undefined || entry.state === state) &&
            (since === undefined || entry.startedAt >= since),
        )
        .slice(0, limit ?? 200),
    };
  },
};

/** A box that has targets but no job yet. */
export const backupNoJobsAnswers: ExtraAnswers = {
  ...backupAnswers,
  'internal.admin.backups.jobs': () => ({ jobs: [] }),
};

/** A box that has neither a target nor a job: the Backup app's first start. */
export const backupEmptyAnswers: ExtraAnswers = {
  ...backupNoJobsAnswers,
  'internal.admin.backups.targets': () => ({ targets: [] }),
};

type BoardJob = components['schemas']['Job'];

/** The run of the first job on the job board, as an administrator gets it on the `jobs` topic. */
export function seedRunningBackup() {
  const job: BoardJob = {
    id: 501,
    kind: 'backup.run',
    state: 'running',
    progress: {
      itemsDone: 31_904,
      itemsTotal: 52_118,
      bytesDone: 188_000_000_000,
      bytesTotal: 431_000_000_000,
      currentItem: '/tank/photos/2026/Autumn/IMG_4412.CR3',
    },
    parameters: { jobId: 1, name: 'Photos and homes' },
    result: null,
    errorCode: null,
    errorParams: null,
    requestId: 'sample',
    createdAt: at(0.4),
    startedAt: at(0.4),
    finishedAt: null,
  };
  useRealtime.setState((state) => ({ data: { ...state.data, jobs: { jobs: [job] } } }));
}
