// Sample data for the gallery screens of Files, the Log Center, the Resource Monitor and the Store: a family box with
// photos, a week of log entries, an hour of measurements and a few catalog apps. Like storageSample.ts, everything
// here is user data or raw values, never interface text, and none of it belongs to a real box.
import type { FileEntry, FolderPage, ShareInfo, TrashPage } from '@pithyx/sdk';
import type { LogCount, LogEntry, LogStatus } from '../apps/sdk/logs';
import type { ProcessList, ResourceSample, ResourceSnapshot } from '../apps/sdk/monitor';
import type { AdminAppList, AppDetails, StoreAppSummary, StoreState } from '../apps/sdk/store';
import { local } from './sampleLanguage';
import type { ExtraAnswers } from './storageSample';

/** The Files side of the sample app: shares, folders by `<share>:<path>` and recycle bins by share. */
export interface FilesSample {
  shares: ShareInfo[];
  folders: Record<string, FileEntry[]>;
  trash: Record<string, TrashPage>;
}

const file = (name: string, size: number, modified: string): FileEntry => ({
  name,
  kind: 'file',
  size,
  modified,
});
const folder = (name: string, modified: string): FileEntry => ({
  name,
  kind: 'folder',
  size: 0,
  modified,
});

/** The open folder of the Files screens, the picture selected in it and the first entry of its share's bin. */
export const sampleFolder = local({ en: '2026-summer', de: '2026-sommer' });
export const samplePicture = local({ en: 'Lighthouse.jpg', de: 'Leuchtturm.jpg' });
export const sampleTrashed = local({
  en: 'Blurry sunset.jpg',
  de: 'Unscharfer Sonnenuntergang.jpg',
});
const duplicate = local({ en: 'Duplicate harbour.jpg', de: 'Hafen doppelt.jpg' });

export const filesSample: FilesSample = {
  shares: [
    { id: 'home', name: 'home', access: 'readWrite', kind: 'home' },
    { id: '2', name: 'Photos', access: 'readWrite' },
    { id: '3', name: 'Family', access: 'readWrite' },
    { id: '4', name: 'Media', access: 'read' },
  ],
  folders: {
    '2:': [
      folder('2025-winter', '2026-02-11T18:20:00Z'),
      folder(local({ en: '2026-spring', de: '2026-frühling' }), '2026-05-30T09:41:00Z'),
      folder(sampleFolder, '2026-09-02T16:05:00Z'),
      folder('Scans', '2026-03-14T11:12:00Z'),
    ],
    [`2:${sampleFolder}`]: [
      folder(local({ en: 'Drone', de: 'Drohne' }), '2026-08-21T10:02:00Z'),
      file(
        local({ en: 'Beach at dawn.jpg', de: 'Strand am Morgen.jpg' }),
        5_412_330,
        '2026-08-14T05:48:00Z',
      ),
      file(local({ en: 'Boat trip.jpg', de: 'Bootsfahrt.jpg' }), 4_981_204, '2026-08-15T13:22:00Z'),
      file(local({ en: 'Campfire.jpg', de: 'Lagerfeuer.jpg' }), 3_720_118, '2026-08-16T21:07:00Z'),
      file(local({ en: 'Harbour.heic', de: 'Hafen.heic' }), 2_840_551, '2026-08-17T10:31:00Z'),
      file(samplePicture, 6_102_877, '2026-08-17T19:55:00Z'),
      file(local({ en: 'Market.jpg', de: 'Markt.jpg' }), 4_215_003, '2026-08-18T09:14:00Z'),
      file(local({ en: 'Old town.jpg', de: 'Altstadt.jpg' }), 5_007_640, '2026-08-18T15:40:00Z'),
      file(local({ en: 'Picnic.jpg', de: 'Picknick.jpg' }), 3_391_772, '2026-08-19T12:26:00Z'),
      file(
        local({ en: 'Sunset over the bay.jpg', de: 'Sonnenuntergang am Meer.jpg' }),
        7_034_219,
        '2026-08-19T20:12:00Z',
      ),
      file(
        local({ en: 'Surf lesson.mp4', de: 'Surfkurs.mp4' }),
        412_998_310,
        '2026-08-20T11:03:00Z',
      ),
      file(local({ en: 'Travel plan.pdf', de: 'Reiseplan.pdf' }), 284_310, '2026-07-30T08:17:00Z'),
    ],
  },
  trash: {
    '2': {
      items: [
        {
          id: 41,
          name: sampleTrashed,
          originalPath: `${sampleFolder}/${sampleTrashed}`,
          kind: 'file',
          size: 6_812_004,
          deletedAt: '2026-09-24T19:02:00Z',
          deletedBy: 'anna',
          expiresAt: '2026-10-24T19:02:00Z',
        },
        {
          id: 40,
          name: duplicate,
          originalPath: `${sampleFolder}/${duplicate}`,
          kind: 'file',
          size: 2_840_551,
          deletedAt: '2026-09-21T08:44:00Z',
          deletedBy: 'tom',
          expiresAt: '2026-10-21T08:44:00Z',
        },
      ],
      nextCursor: null,
      retentionDays: 30,
    },
  },
};

/** One folder page of the sample, in the order Files asked for. */
export function folderPage(share: string, path: string, sort: string, order: string): FolderPage {
  const entries = [...(filesSample.folders[`${share}:${path}`] ?? [])];
  const value = (entry: FileEntry) =>
    sort === 'size' ? entry.size : sort === 'modified' ? entry.modified : entry.name.toLowerCase();
  entries.sort((a, b) => {
    if (a.kind !== b.kind) {
      return a.kind === 'folder' ? -1 : 1;
    }
    const compared = value(a) < value(b) ? -1 : value(a) > value(b) ? 1 : 0;
    return order === 'desc' ? -compared : compared;
  });
  return { path, folderId: `${share}:${path}`, total: entries.length, entries, nextCursor: null };
}

// The log entries end shortly before the gallery's clock (sampleData.now, 2026-09-26 14:32 local).
const entry = (
  id: number,
  time: string,
  level: LogEntry['level'],
  source: string,
  message: string,
  extra: Partial<LogEntry> = {},
): LogEntry => ({
  id,
  time,
  level,
  source,
  message,
  user: null,
  data: null,
  requestId: null,
  archived: false,
  ...extra,
});

export const sampleLogEntries: LogEntry[] = [
  entry(212, '2026-09-26T12:31:58Z', 'warn', 'storage', 'Disk 3 of tank is warm: 52 °C', {
    data: { pool: 'tank', disk: 'ata-NAS_HDD_ZA1B2C5F', celsius: '52' },
  }),
  entry(211, '2026-09-26T12:31:02Z', 'info', 'shares', 'anna opened Photos over SMB', {
    user: 'anna',
    data: { share: 'Photos', protocol: 'smb', client: '192.0.2.24' },
  }),
  entry(210, '2026-09-26T12:30:41Z', 'info', 'auth', 'tom signed in', {
    user: 'tom',
    data: { method: 'password', twoFactor: 'totp' },
  }),
  entry(
    209,
    '2026-09-26T12:28:13Z',
    'error',
    'app/org.example.photos',
    'Thumbnail worker stopped: out of memory',
    {
      data: { exitCode: '137', container: 'worker' },
    },
  ),
  entry(208, '2026-09-26T12:12:11Z', 'info', 'app/org.example.notes', 'Backend started'),
  entry(
    207,
    '2026-09-26T12:12:09Z',
    'info',
    'store',
    'Notes 2.4.1 installed from the official catalog',
    {
      user: 'anna',
    },
  ),
  entry(
    206,
    '2026-09-26T11:58:40Z',
    'warn',
    'network',
    'The name server 192.0.2.53 did not answer',
  ),
  entry(
    205,
    '2026-09-26T09:00:02Z',
    'info',
    'storage',
    'Snapshot tank/pithyx/shares/family@daily-2026-09-26 taken',
  ),
  entry(204, '2026-09-26T03:00:11Z', 'info', 'store', 'Checked 2 catalogs: 1 update available'),
  entry(
    203,
    '2026-09-25T22:14:37Z',
    'error',
    'storage',
    'backup is degraded: a disk is unavailable',
    {
      data: { pool: 'backup', disk: 'ata-NAS_HDD_WX40B3MM' },
    },
  ),
  entry(202, '2026-09-25T21:40:19Z', 'info', 'auth', 'anna signed in', {
    user: 'anna',
    data: { method: 'passkey' },
  }),
  entry(201, '2026-09-25T18:03:55Z', 'warn', 'auth', 'Sign-in failed for the user guest', {
    data: { reason: 'password', client: '192.0.2.77' },
  }),
  entry(200, '2026-09-25T16:20:00Z', 'info', 'shares', 'Shared folder Media created', {
    user: 'anna',
  }),
  entry(
    199,
    '2026-09-25T09:00:03Z',
    'info',
    'storage',
    'Snapshot tank/pithyx/shares/family@daily-2026-09-25 taken',
  ),
  entry(198, '2026-09-25T03:00:12Z', 'info', 'store', 'Checked 2 catalogs: no updates'),
];

export const sampleLogSources = [
  'daemon',
  'auth',
  'network',
  'shares',
  'storage',
  'store',
  'app/org.example.notes',
  'app/org.example.photos',
];

export const sampleLogCounts: LogCount[] = [
  { source: 'storage', warnings: 1, errors: 1 },
  { source: 'auth', warnings: 1, errors: 0 },
  { source: 'network', warnings: 1, errors: 0 },
  { source: 'app/org.example.photos', warnings: 0, errors: 1 },
];

export const sampleLogStatus: LogStatus = {
  settings: {
    retentionDays: 30,
    maxBytes: 512_000_000,
    archiveEnabled: false,
    archivePool: null,
    archiveRetentionDays: 365,
    archiveMaxBytes: 5_000_000_000,
  },
  currentBytes: 18_422_112,
  currentOldest: '2026-08-27T00:00:04Z',
  archiveState: 'off',
  archiveBytes: null,
  archivedAt: null,
  archiveOldest: null,
  archivePool: null,
  pools: ['tank', 'backup'],
};

export const logsAnswers: ExtraAnswers = {
  'internal.admin.logs.query': () => ({ entries: sampleLogEntries, nextCursor: null }),
  'internal.admin.logs.sources': () => sampleLogSources,
  'internal.admin.logs.counts': () => sampleLogCounts,
  'internal.admin.logs.settings': () => sampleLogStatus,
};

// An hour of measurements in buckets of 5 seconds, as the daemon keeps them, with a scrub of tank in the last quarter
// of the hour and a large upload in its second half. The curves are made up of a few waves.
const GB = 1_000_000_000;
const STEPS = 720;
const STEP_SECONDS = 5;
const wave = (index: number, base: number, swing: number, period: number) =>
  Math.max(
    0,
    base + swing * Math.sin((index / period) * Math.PI * 2) + swing * 0.25 * Math.sin(index * 0.9),
  );
const end = Date.parse('2026-09-26T12:32:00Z');
const times = Array.from({ length: STEPS }, (_, index) =>
  new Date(end - (STEPS - index) * STEP_SECONDS * 1000).toISOString(),
);
const scrub = (index: number) => index >= 540;

const latestSample: ResourceSample = {
  at: '2026-09-26T12:32:00Z',
  cpu: { percent: 23, cores: [31, 18, 26, 17] },
  memory: {
    totalBytes: 16 * GB,
    usedBytes: 4.6 * GB,
    availableBytes: 11.1 * GB,
    freeBytes: 1.2 * GB,
    cacheBytes: 0.9 * GB,
    arcBytes: 7.4 * GB,
    arcMaxBytes: 8 * GB,
    swapTotalBytes: 2 * GB,
    swapUsedBytes: 0,
  },
  disks: [
    { name: 'sda', busyPercent: 64, readBytesPerSecond: 182_000_000, writeBytesPerSecond: 0 },
    { name: 'sdb', busyPercent: 61, readBytesPerSecond: 179_000_000, writeBytesPerSecond: 0 },
    { name: 'sdc', busyPercent: 66, readBytesPerSecond: 185_000_000, writeBytesPerSecond: 0 },
    { name: 'nvme0n1', busyPercent: 2, readBytesPerSecond: 120_000, writeBytesPerSecond: 410_000 },
  ],
  network: [{ name: 'enp1s0', receiveBytesPerSecond: 3_400_000, sendBytesPerSecond: 21_800_000 }],
  temperatures: [
    { chip: 'coretemp', label: 'Package', celsius: 48, highCelsius: null, criticalCelsius: 100 },
    { chip: 'drivetemp', label: 'sda', celsius: 38, highCelsius: 50, criticalCelsius: 60 },
    { chip: 'drivetemp', label: 'sdb', celsius: 39, highCelsius: 50, criticalCelsius: 60 },
    { chip: 'drivetemp', label: 'sdc', celsius: 52, highCelsius: 50, criticalCelsius: 60 },
    { chip: 'nvme', label: 'Composite', celsius: 41, highCelsius: null, criticalCelsius: 84 },
  ],
  loadAverage: [0.82, 0.71, 0.64],
  uptimeSeconds: 12 * 86_400 + 4 * 3_600 + 17 * 60,
};

export const sampleResources: ResourceSnapshot = {
  cpuCount: 4,
  memoryTotalBytes: 16 * GB,
  latest: latestSample,
  history: {
    bucketSeconds: STEP_SECONDS,
    times,
    cpuPercent: times.map((_, index) => wave(index, scrub(index) ? 24 : 9, 5, 204)),
    memoryUsedBytes: times.map((_, index) => wave(index, 4.4 * GB, 0.2 * GB, 276)),
    arcBytes: times.map((_, index) => Math.min(7.4 * GB, 5.8 * GB + index * 0.0025 * GB)),
    network: [
      {
        name: 'enp1s0',
        in: times.map((_, index) => wave(index, 2_600_000, 1_400_000, 156)),
        out: times.map((_, index) =>
          wave(index, index > 360 ? 19_000_000 : 1_200_000, 900_000, 108),
        ),
      },
    ],
    disks: ['sda', 'sdb', 'sdc', 'nvme0n1'].map((name) => ({
      name,
      in: times.map((_, index) =>
        name === 'nvme0n1'
          ? wave(index, 150_000, 80_000, 132)
          : scrub(index)
            ? wave(index, 180_000_000, 8_000_000, 60)
            : wave(index, 900_000, 600_000, 84),
      ),
      out: times.map((_, index) =>
        wave(index, name === 'nvme0n1' ? 420_000 : 160_000, 120_000, 228),
      ),
    })),
  },
};

const process = (
  pid: number,
  name: string,
  user: string,
  cpuPercent: number,
  memoryBytes: number,
) => ({
  pid,
  name,
  user,
  cpuPercent,
  memoryBytes,
});

export const sampleProcesses: ProcessList = {
  at: '2026-09-26T12:32:00Z',
  processes: [
    process(812, 'z_rd_int', 'root', 14.2, 0),
    process(1204, 'pithyx-daemon', 'pithyx', 2.1, 148_000_000),
    process(2210, 'smbd', 'anna', 1.6, 42_000_000),
    process(3318, 'thumbnailer', 'app-40012', 1.2, 212_000_000),
    process(3302, 'node', 'app-40007', 0.6, 96_000_000),
    process(931, 'sshd', 'root', 0.1, 9_400_000),
    process(1, 'systemd', 'root', 0, 13_100_000),
    process(1530, 'containerd', 'root', 0.2, 58_000_000),
  ],
};

export const monitorEvents: Record<string, unknown> = {
  'internal.admin.monitor.resources': sampleResources,
  'internal.admin.monitor.processes': sampleProcesses,
};

// App icons as tinted tiles with a simple glyph, drawn here so the gallery needs no package.
const icon = (tile: string, glyph: string, path: string) =>
  `data:image/svg+xml,${encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48"><rect width="48" height="48" rx="10" fill="${tile}"/><path d="${path}" fill="none" stroke="${glyph}" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
  )}`;

const storeApp = (
  id: string,
  name: Record<string, string>,
  description: Record<string, string>,
  author: string,
  categories: string[],
  version: string,
  publishedAt: string,
  appIcon: string,
  extra: Partial<StoreAppSummary> = {},
): StoreAppSummary => ({
  id,
  name,
  description,
  author,
  catalog: 'org.pithyx.official',
  kind: 'official',
  categories,
  keywords: [],
  license: 'MIT',
  maturity: 'stable',
  version,
  publishedAt,
  icon: appIcon,
  installedVersion: null,
  update: false,
  critical: false,
  incompatible: null,
  ...extra,
});

export const sampleStoreApps: StoreAppSummary[] = [
  storeApp(
    'org.example.photos',
    { en: 'Photo Library', de: 'Fotobibliothek' },
    {
      en: 'Browse, tag and share the photos on your shared folders, with albums and faces found on the box.',
      de: 'Fotos in deinen freigegebenen Ordnern ansehen, verschlagworten und teilen, mit Alben und Gesichtern, die die Box findet.',
    },
    'Example Photo Team',
    ['photos'],
    '3.2.0',
    '2026-09-24T10:00:00Z',
    icon('#dbe8f7', '#1f4f86', 'M10 34l9-10 7 7 5-5 7 8M10 12h28v24H10z'),
  ),
  storeApp(
    'org.example.notes',
    { en: 'Notes', de: 'Notizen' },
    {
      en: 'Plain text notes with folders and search, kept in your home folder.',
      de: 'Notizen als reiner Text mit Ordnern und Suche, in deinem Home-Ordner.',
    },
    'Example Labs',
    ['productivity'],
    '2.4.1',
    '2026-09-20T09:30:00Z',
    icon('#f6ead0', '#7a4d0b', 'M14 10h20v28H14zM19 18h10M19 24h10M19 30h6'),
    { installedVersion: '2.4.1' },
  ),
  storeApp(
    'org.example.media',
    { en: 'Media Server', de: 'Medienserver' },
    {
      en: 'Streams films and music from your shared folders to the devices of your home.',
      de: 'Spielt Filme und Musik aus deinen freigegebenen Ordnern auf den Geräten zu Hause ab.',
    },
    'Example Media Group',
    ['media'],
    '1.8.0',
    '2026-09-18T14:00:00Z',
    icon('#e5dff4', '#4b3592', 'M12 14h24v20H12zM21 19l8 5-8 5z'),
    { installedVersion: '1.7.2', update: true },
  ),
  storeApp(
    'org.example.backup',
    { en: 'Offsite Backup', de: 'Externe Sicherung' },
    {
      en: 'Copies chosen shared folders, encrypted, to a second box or a disk of your own.',
      de: 'Kopiert ausgewählte freigegebene Ordner verschlüsselt auf eine zweite Box oder eine eigene Platte.',
    },
    'Example Backup Project',
    ['backup'],
    '0.9.4',
    '2026-09-12T08:00:00Z',
    icon('#d8efe6', '#1d6b4f', 'M24 10v20M16 22l8 8 8-8M12 36h24'),
  ),
  storeApp(
    'org.example.recipes',
    { en: 'Recipe Box', de: 'Rezeptkiste' },
    {
      en: 'Collect recipes, plan the week and print a shopping list.',
      de: 'Rezepte sammeln, die Woche planen und eine Einkaufsliste drucken.',
    },
    'Example Kitchen',
    ['productivity'],
    '1.2.0',
    '2026-09-03T12:00:00Z',
    icon('#f9e1dc', '#8c2f1c', 'M14 20h20v16H14zM18 20v-6h12v6'),
  ),
  storeApp(
    'org.example.monitor',
    { en: 'Uptime Watch', de: 'Online-Wächter' },
    {
      en: 'Checks your websites and devices and tells you when one stops answering.',
      de: 'Prüft deine Websites und Geräte und meldet, wenn eines nicht mehr antwortet.',
    },
    'Example Ops',
    ['tools'],
    '2.0.3',
    '2026-08-27T16:00:00Z',
    icon('#dcecef', '#1e5f6b', 'M10 26h7l4-9 6 16 4-7h7'),
  ),
];

const installed = (
  id: string,
  version: string,
  name: Record<string, string>,
  previousVersion: string | null = null,
): AppDetails =>
  ({
    id,
    version,
    previousVersion,
    available: true,
    busy: false,
    enabled: true,
    catalog: 'org.pithyx.official',
    source: 'catalog',
    sha256: '0'.repeat(64),
    uid: 40007,
    installedAt: '2026-09-01T10:00:00Z',
    updatedAt: '2026-09-20T10:00:00Z',
    logLevel: 'info',
    updatePolicy: null,
    devices: [],
    elevated: [],
    permissions: [],
    storage: [],
    manifest: { id, version, name, manifestVersion: 2 },
  }) as unknown as AppDetails;

export const sampleInstalledApps: AdminAppList = {
  pool: 'tank',
  apps: [
    installed('org.example.notes', '2.4.1', { en: 'Notes', de: 'Notizen' }, '2.3.0'),
    installed('org.example.media', '1.7.2', { en: 'Media Server', de: 'Medienserver' }),
  ],
};

export const sampleStoreState: StoreState = {
  settings: {
    policy: 'notify',
    time: '03:00',
    channel: 'stable',
    lastCheckAt: '2026-09-26T01:00:11Z',
    nextCheckAt: '2026-09-27T01:00:00Z',
  },
  updates: [
    {
      id: 'org.example.media',
      catalog: 'org.pithyx.official',
      installedVersion: '1.7.2',
      version: '1.8.0',
      policy: 'notify',
      automatic: false,
      critical: false,
      elevated: false,
      addedPermissions: [],
      releaseNotes: {
        en: 'Faster library scans and subtitles in more languages.',
        de: 'Schnelleres Durchsuchen der Bibliothek und Untertitel in mehr Sprachen.',
      },
    },
  ],
  catalogs: [
    {
      key: 1,
      id: 'org.pithyx.official',
      kind: 'official',
      url: 'https://github.com/pithyx/apps-official.git',
      name: { en: 'Pithyx apps', de: 'Pithyx-Apps' },
      description: null,
      homepage: null,
      enabled: true,
      busy: false,
      status: 'ok',
      verified: true,
      verifiedAt: '2026-09-26T01:00:11Z',
      checkedAt: '2026-09-26T01:00:11Z',
      commit: null,
      sequence: 12,
      appCount: 6,
      errorCode: null,
      errorParams: null,
      hasRegistryToken: false,
      hostKey: null,
      sshPublicKey: null,
    },
  ],
};

export const storeAnswers: ExtraAnswers = {
  'internal.admin.apps.list': () => sampleInstalledApps,
  'internal.admin.store.state': () => sampleStoreState,
  'internal.admin.store.search': () => ({ apps: sampleStoreApps }),
};

const home = (
  shareId: number,
  userId: number,
  username: string,
  displayName: string | null,
  usedBytes: number,
) => ({
  shareId,
  userId,
  username,
  displayName,
  folder: username,
  pool: 'tank',
  usedBytes,
  quotaBytes: 500 * GB,
  ownQuotaBytes: null,
  unavailable: null,
});

export const homesAnswers: ExtraAnswers = {
  'internal.admin.homes.overview': () => ({
    pool: 'tank',
    defaultQuotaBytes: 500 * GB,
    moving: null,
    pools: [
      { name: 'tank', availableBytes: 13_900 * GB, unavailable: null },
      { name: 'backup', availableBytes: 1_300 * GB, unavailable: null },
    ],
    homes: [
      home(11, 1, 'ada', 'Ada Admin', 84 * GB),
      home(12, 2, 'ben', 'Ben Berger', 212 * GB),
      home(13, 3, 'carla.mueller-schmidt', 'Carla Müller-Schmidt', 37 * GB),
      home(14, 4, 'guest', 'Guest', 0.2 * GB),
    ],
  }),
};
