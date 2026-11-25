import type { components } from '@pithyx/api-client';
import type {
  BackupFileEntry,
  BackupFileHistory,
  BackupHistoryQuery,
  BackupJob,
  BackupRun,
  BackupTarget,
  BackupVersion,
} from '../apps/sdk/backups';
import { clock } from '../apps/backup/data';
import { useRealtime } from '../realtime/connection';
import { now } from './sampleData';
import type { ExtraAnswers } from './storageSample';

// Sample data of the Backup app (D414, D415, D416, D420): two targets and a server, three box backups (one backing up
// right now, one fine, one failed) and a server backup, the runs of the selected one and two weeks of history. Names of servers and folders are user data; keys and fingerprints are invented.

// The Backup app counts its periods and day bars from the gallery's sample time, like the taskbar's clock.
clock.now = () => now.getTime();

const at = (hoursAgo: number) => new Date(now.getTime() - hoursAgo * 3_600_000).toISOString();

const hostKey = {
  type: 'ssh-ed25519',
  fingerprint: 'SHA256:q3Vd0m9cWwqK7f2lZ1y8u5mYwYt0b6g8bXUeJr1pS4c',
  key: 'AAAAC3NzaC1lZDI1NTE5AAAAIK8xNq2c7pXy4Qm0WkV3sGf9a1bR5tZ2yHcJ6eL0dPqU',
};

const webKey =
  'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIB4vN7mQ2xK9pL5wR8tY1zC3aF6hJ0dS4gE7uI2oW9qT pithyx-backup@nas-living-room';

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
    space: { totalBytes: 1_000_000_000_000, freeBytes: 390_000_000_000, measuredAt: at(12) },
    purpose: 'target',
    authorizedKeys: null,
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
    space: { totalBytes: 4_000_000_000_000, freeBytes: 3_640_000_000_000, measuredAt: at(24 * 18) },
    purpose: 'target',
    authorizedKeys: null,
    createdAt: at(24 * 12),
  },
  {
    id: 3,
    kind: 'sftp',
    name: 'web01',
    host: 'web01.example.net',
    port: 22,
    user: 'root',
    path: '/',
    hostKey: { ...hostKey, fingerprint: 'SHA256:Lm4nB8vC2xZ6aS0dF3gH7jK1qW5eR9tY2uI4oP8sA6d' },
    publicKey: webKey,
    lastTest: { succeeded: true, testedAt: at(26), errorCode: null, errorParams: null },
    space: null,
    purpose: 'server',
    authorizedKeys: `restrict,command="rrsync -ro /" ${webKey}`,
    createdAt: at(24 * 9),
  },
];

const sources = [
  { shareId: 2, name: 'Photos', home: false, available: true, supported: true },
  { shareId: 3, name: 'Documents', home: false, available: true, supported: true },
  { shareId: 4, name: 'Media', home: false, available: true, supported: true },
  { shareId: 5, name: 'Servers', home: false, available: true, supported: true },
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
    repositoryBytes: null,
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
    dataset: null,
    pull: null,
    mirror: null,
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
    protectedBytes: 431_000_000_000,
    repositoryBytes: 395_000_000_000,
    createdAt: at(24 * 40),
  },
  {
    id: 2,
    kind: 'versioned',
    targetId: 1,
    repository: 'documents',
    dataset: null,
    pull: null,
    mirror: null,
    settings: settings('Documents', [{ shareId: 3, path: '' }]),
    lastRun: documentsRuns[0] ?? null,
    lastCheck: documentsRuns[3] ?? null,
    lastSuccessAt: at(12.4),
    nextRunAt: at(-11.5),
    nextCheckAt: at(-80),
    overdue: false,
    protectedBytes: 23_400_000_000,
    repositoryBytes: 19_800_000_000,
    createdAt: at(24 * 30),
  },
  {
    id: 3,
    kind: 'mirror',
    targetId: 2,
    repository: 'media',
    dataset: null,
    pull: null,
    mirror: { delete: false },
    settings: {
      ...settings('Media to the second box', [{ shareId: 4, path: 'Music' }]),
      schedule: '0 3 * * 6',
      retention: { last: 0, hourly: 0, daily: 0, weekly: 0, monthly: 0, yearly: 0 },
      checkSchedule: null,
      checkReadPercent: 0,
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
    protectedBytes: 402_000_000_000,
    repositoryBytes: 360_000_000_000,
    createdAt: at(24 * 20),
  },
  {
    id: 4,
    kind: 'pull',
    targetId: 3,
    repository: '',
    dataset: 'tank/pithyx/shares/servers/web01',
    pull: {
      shareId: 5,
      folder: 'web01',
      paths: ['/etc', '/srv/www', '/var/backups'],
      delete: true,
      owners: 'server',
    },
    mirror: null,
    settings: {
      ...settings('web01', []),
      schedule: '0 1 * * *',
      checkSchedule: null,
      checkReadPercent: 0,
    },
    lastRun: run(116, 13, 3, {
      jobId: 4,
      files: 18_420,
      bytes: 6_800_000_000,
      addedBytes: 42_000_000,
      snapshot: 'pithyx-pull-20261123T000000Z',
    }),
    lastCheck: null,
    lastSuccessAt: at(12.95),
    nextRunAt: at(-11),
    nextCheckAt: null,
    overdue: false,
    protectedBytes: 6_800_000_000,
    repositoryBytes: 7_300_000_000,
    createdAt: at(24 * 9),
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
  ...(sampleJobs[3]?.lastRun ? [sampleJobs[3].lastRun] : []),
  ...documentsRuns,
  ...mediaRuns,
  ...olderRuns,
].sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt));

// The versions of Documents, newest first: a week of nightly ones, then weekly and monthly ones the retention kept,
// the oldest protected by hand. Only the newest was compared with the one before.
const versionDays: [days: number, keep: BackupVersion['keep'], added: number][] = [
  [0, ['daily'], 1_240_000_000],
  [1, ['daily'], 860_000_000],
  [2, ['daily'], 310_000_000],
  [3, ['daily', 'weekly'], 120_000_000],
  [4, ['daily'], 96_000_000],
  [5, ['daily'], 2_400_000_000],
  [6, ['daily'], 640_000_000],
  [10, ['weekly'], 1_900_000_000],
  [17, ['weekly'], 410_000_000],
  [24, ['weekly', 'monthly'], 3_100_000_000],
  [55, ['monthly'], 820_000_000],
];
const sampleVersions: BackupVersion[] = versionDays.map(([days, keep, added], index) => ({
  id: 40 - index,
  snapshot: (0xa91f03c2 - index * 0x1013).toString(16),
  time: at(24 * days + 12.5),
  files: 9_842 - days * 7,
  bytes: 23_400_000_000 - days * 60_000_000,
  addedBytes: added,
  filesAdded: index === 0 ? 14 : null,
  filesChanged: index === 0 ? 3 : null,
  filesRemoved: index === 0 ? 1 : null,
  protected: days === 55,
  keep: days === 55 ? [...keep, 'protected'] : keep,
  changes:
    index === 0
      ? [
          { change: 'added', path: 'Documents/Taxes/2025/Receipts/invoice-0412.pdf' },
          { change: 'added', path: 'Documents/Taxes/2025/Receipts/invoice-0413.pdf' },
          { change: 'modified', path: 'Documents/Household/budget-2026.ods' },
          { change: 'removed', path: 'Documents/Drafts/old.docx' },
        ]
      : null,
}));

const folder = (name: string, hoursAgo: number, change: BackupFileEntry['change'] = null) =>
  ({ name, kind: 'folder', size: null, modified: at(hoursAgo), change }) as const;
const file = (
  name: string,
  size: number,
  hoursAgo: number,
  change: BackupFileEntry['change'] = null,
): BackupFileEntry => ({ name, kind: 'file', size, modified: at(hoursAgo), change });

// The folders of the newest version of Documents, as the restore page lists them; any other folder is empty.
const sampleFolders: Record<string, BackupFileEntry[]> = {
  '': [folder('Documents', 14, 'modified')],
  Documents: [
    folder('Drafts', 24 * 9),
    folder('Household', 20, 'modified'),
    folder('Scans', 24 * 40),
    folder('Taxes', 14, 'modified'),
    file('README.txt', 2_100, 24 * 200),
  ],
  'Documents/Taxes': [folder('2024', 24 * 210), folder('2025', 14, 'modified')],
  'Documents/Taxes/2025': [
    folder('Receipts', 14, 'modified'),
    file('tax-return-2025.pdf', 2_140_000, 38, 'modified'),
    file('expenses-2025.ods', 88_000, 24 * 11),
    file('notice-2024.pdf', 640_000, 24 * 174),
    file('notes.md', 4_100, 15, 'added'),
    file('tax-export.zip', 9_800_000, 24 * 21),
  ],
};

// A file in every version: on the box a little bigger, in the oldest two not there yet.
function sampleHistory(path: string): BackupFileHistory {
  const size = sampleFolders[path.slice(0, path.lastIndexOf('/'))]?.find(
    (entry) => entry.name === path.slice(path.lastIndexOf('/') + 1),
  )?.size;
  return {
    path,
    current: { kind: 'file', size: size == null ? null : Math.round(size * 1.08), modified: at(3) },
    versions: sampleVersions.map((version, index) => {
      const there = index < sampleVersions.length - 2;
      return {
        versionId: version.id,
        time: version.time,
        kind: there ? 'file' : null,
        size: there && size != null ? Math.round(size * (1 - index * 0.06)) : null,
        modified: there ? version.time : null,
        owner: there ? 'alex' : null,
        mode: there ? '0640' : null,
      };
    }),
  };
}

/** The answers of the Backup app's calls in the gallery. */
export const backupAnswers: ExtraAnswers = {
  'internal.admin.backups.targets': () => ({ targets: sampleTargets }),
  'internal.admin.backups.sources': () => ({ sources }),
  'internal.admin.backups.jobs': () => ({ jobs: sampleJobs }),
  'internal.admin.backups.runs': (params) => ({
    runs:
      (params as { id: number }).id === 2
        ? documentsRuns
        : history.filter((entry) => entry.jobId === (params as { id: number }).id),
  }),
  'internal.admin.backups.versions': (params) => ({
    versions: (params as { id: number }).id === 2 ? sampleVersions : [],
  }),
  'internal.admin.backups.days': (params) => {
    const { from, to } = params as { from: string; to: string };
    return sampleDays(from, to);
  },
  'internal.admin.backups.browse': () => null,
  'internal.admin.backups.files': (params) => {
    const { path } = params as { path: string };
    return { path, compared: true, entries: sampleFolders[path] ?? [] };
  },
  'internal.admin.backups.fileHistory': (params) =>
    sampleHistory((params as { path: string }).path),
  'internal.admin.backups.history': (params) => {
    const { jobId, kind, state, since, until, limit } = params as BackupHistoryQuery;
    return {
      runs: history
        .filter(
          (entry) =>
            (jobId === undefined || entry.jobId === jobId) &&
            (kind === undefined || entry.kind === kind) &&
            (state === undefined || entry.state === state) &&
            (since === undefined || entry.startedAt >= since) &&
            (until === undefined || entry.startedAt < until),
        )
        .slice(0, limit ?? 200),
    };
  },
};

const DAY = 86_400_000;

/** The sample's runs per day, as the daemon counts them, with the repositories growing towards today's sizes. */
function sampleDays(from: string, to: string) {
  const first = new Date(`${from}T00:00:00`);
  const last = new Date(`${to}T00:00:00`);
  const today = new Date(now);
  today.setHours(0, 0, 0, 0);
  const days = [];
  for (let date = first; date <= last; date = new Date(date.getTime() + DAY)) {
    const start = new Date(date);
    start.setHours(0, 0, 0, 0);
    const end = new Date(start.getTime() + DAY);
    const back = Math.round((today.getTime() - start.getTime()) / DAY);
    const runs = history.filter(
      (entry) =>
        entry.state !== 'running' &&
        Date.parse(entry.startedAt) >= start.getTime() &&
        Date.parse(entry.startedAt) < end.getTime(),
    );
    // Beyond the two weeks of history each night backs up the two daily backups, and one in nine days warns.
    const older = back > 14 && start < today;
    const count = (state: BackupRun['state']) =>
      runs.filter((entry) => entry.state === state).length;
    const succeeded = older ? (back % 9 === 0 ? 1 : 2) : count('succeeded');
    const warnings = older ? (back % 9 === 0 ? 1 : 0) : count('warning');
    const failed = older ? 0 : count('failed') + count('interrupted');
    const jobs = older ? 2 : new Set(runs.map((entry) => entry.jobId)).size;
    const failedJobs = new Set(
      runs
        .filter((entry) => entry.state === 'failed' || entry.state === 'interrupted')
        .map((entry) => entry.jobId),
    ).size;
    const growth = 0.85 + 0.15 * Math.min(1, Math.max(0, 1 - back / 120));
    days.push({
      date: `${String(start.getFullYear())}-${String(start.getMonth() + 1).padStart(2, '0')}-${String(start.getDate()).padStart(2, '0')}`,
      start: start.toISOString(),
      end: end.toISOString(),
      succeeded,
      warnings,
      failed,
      jobs,
      jobsOk: jobs - (older ? 0 : failedJobs),
      addedBytes: older
        ? 1_400_000_000 + ((back * 7919) % 5) * 600_000_000
        : runs.reduce(
            (total, entry) => total + (entry.kind === 'backup' ? (entry.addedBytes ?? 0) : 0),
            0,
          ),
      repositoryBytes: back < 0 ? null : Math.round(774_800_000_000 * growth),
    });
  }
  return { timeZone: 'Europe/Berlin', days };
}

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
