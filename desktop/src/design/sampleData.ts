import type { RecentFile } from '../apps/files/recent';
// Sample content for the design mockups. Interface text comes from i18n; what stays here is user data (names of
// files, folders, pools and notes) and raw values that the formatters turn into localized text.
import {
  IconActivity,
  IconBox,
  IconBuildingStore,
  IconFileText,
  IconFileTypePdf,
  IconFileZip,
  IconFolder,
  IconFolders,
  IconListDetails,
  IconPhoto,
  IconSettings,
  IconTerminal2,
  IconUserCog,
} from '../ui/icons';
import type { components } from '@pithyx/api-client';
import { coreAppIds } from '../apps/ids';
import { useAppName } from '../apps/names';
import type { Language } from '../i18n';
import type { JobItem, NotificationItem } from '../shell/Notifications';
import type { StartEntry } from '../shell/StartMenu';
import type { AppInfo } from '../shell/types';
import { sampleFolder } from './appSamples';
import { local } from './sampleLanguage';

export const appDefinitions = {
  files: { id: coreAppIds.files, icon: IconFolders, tone: 'petrol' },
  monitor: { id: coreAppIds.monitor, icon: IconActivity, tone: 'moss' },
  logs: { id: coreAppIds.logs, icon: IconListDetails, tone: 'graphite' },
  settings: { id: coreAppIds.settings, icon: IconSettings, tone: 'graphite' },
  store: { id: coreAppIds.store, icon: IconBuildingStore, tone: 'indigo' },
  preferences: { id: coreAppIds.preferences, icon: IconUserCog, tone: 'ocean' },
  photos: { id: 'photos', icon: IconPhoto, tone: 'rose' },
  containers: { id: 'containers', icon: IconBox, tone: 'moss' },
  terminal: { id: 'terminal', icon: IconTerminal2, tone: 'amber' },
} satisfies Record<string, Omit<AppInfo, 'name'>>;

export type AppId = keyof typeof appDefinitions;
export type Apps = Record<AppId, AppInfo>;

/** The core apps with their translated names. */
export function useApps(): Apps {
  const appName = useAppName();
  const apps = {} as Apps;
  for (const [id, definition] of Object.entries(appDefinitions) as [
    AppId,
    Omit<AppInfo, 'name'>,
  ][]) {
    apps[id] = { ...definition, name: appName(definition.id) };
  }
  return apps;
}

export const user = { name: 'Alex Morgan', firstName: 'Alex', initials: 'AM', username: 'alex' };
export const hostname = 'nas-living-room';
export const now = new Date('2026-09-26T14:32:00');
export const recoveryKey = 'PTHX-7KQ2-MZ9P-3HT8-WB6N-R2DC-5FJX-Q8';
export const currentFolder = sampleFolder;

export const noteText = local({
  en: 'Replace disk 3 in tank before Friday.\nOrder: 8 TB, CMR.\n\nCall Sam about the backup box.',
  de: 'Platte 3 in tank bis Freitag tauschen.\nBestellen: 8 TB, CMR.\n\nSam wegen der Backup-Box anrufen.',
});

const photos2026 = local({ en: 'Photos 2026', de: 'Fotos 2026' });
const taxReturn = local({ en: 'Tax return 2025.pdf', de: 'Steuererklärung 2025.pdf' });
const networkPlan = local({ en: 'network-plan.txt', de: 'netzwerkplan.txt' });

export const desktopItems = [
  { name: local({ en: 'Documents', de: 'Dokumente' }), item: { kind: 'folder' } },
  { name: photos2026, item: { kind: 'folder' } },
  { name: local({ en: 'Projects', de: 'Projekte' }), item: { kind: 'folder' } },
  { name: taxReturn, item: { kind: 'file', icon: IconFileTypePdf } },
  { name: networkPlan, item: { kind: 'file', icon: IconFileText } },
] as const;

export const desktopShortcuts: AppId[] = ['files', 'settings'];

/** The menu's app list by area, as the desktop groups it: System for administrators, Apps, You. */
export const startApps = {
  system: ['files', 'settings', 'monitor', 'logs', 'store'],
  apps: ['photos', 'containers', 'terminal'],
  you: ['preferences'],
} as const satisfies Record<string, readonly AppId[]>;

/** Sample state for the menu's tiles; only the fields the tiles read are filled in. */
export const startPools = [
  { name: 'tank', state: 'ONLINE', capacityPercent: 36, freeBytes: 2_300_000_000_000 },
  { name: 'fast', state: 'ONLINE', capacityPercent: 85, freeBytes: 70_000_000_000 },
] as unknown as components['schemas']['Pool'][];

export const startSample = {
  cpu: { percent: 4, cores: [] },
  memory: { totalBytes: 8_000_000_000, availableBytes: 5_100_000_000 },
  network: [],
} as unknown as components['schemas']['ResourceSample'];

export const startUpdates = {
  installedVersion: '1.2.0',
  available: null,
} as unknown as components['schemas']['UpdateStatus'];

export const startShares = [
  { id: 1, name: 'alex', kind: 'home', access: 'readWrite', unavailable: null },
  { id: 2, name: 'family', kind: 'share', access: 'readWrite', unavailable: null },
  { id: 3, name: 'media', kind: 'share', access: 'read', unavailable: null },
] as unknown as components['schemas']['ShareInfo'][];

/** Recent files of the menu and of Files' Recent place (#333), newest first. */
export const startRecent: RecentFile[] = [
  {
    share: 2,
    path: `${local({ en: 'Documents', de: 'Dokumente' })}/${taxReturn}`,
    size: 482_133,
    modified: '2026-10-28T09:12:00Z',
    openedAt: '2026-10-29T08:41:00Z',
  },
  {
    share: 1,
    path: networkPlan,
    size: 2_311,
    modified: '2026-10-28T16:02:00Z',
    openedAt: '2026-10-28T16:05:00Z',
  },
  {
    share: 2,
    path: `${photos2026}/${local({ en: 'Lake.jpg', de: 'See.jpg' })}`,
    size: 3_904_221,
    modified: '2026-10-12T11:30:00Z',
    openedAt: '2026-10-27T19:20:00Z',
  },
  {
    share: 3,
    path: `${local({ en: 'Music', de: 'Musik' })}/Live set.flac`,
    size: 61_220_480,
    modified: '2026-09-02T20:00:00Z',
    openedAt: '2026-10-25T21:47:00Z',
  },
  {
    share: 1,
    path: 'Backups/backup-2026-09.zip',
    size: 912_004_112,
    modified: '2026-09-30T03:00:00Z',
    openedAt: '2026-10-21T10:03:00Z',
  },
];

/** A query that matches the Photos app in each language. */
export const searchQuery: Record<Language, string> = { en: 'sha', de: 'frei' };

export const searchFiles: StartEntry[] = [
  {
    id: 's-folder',
    name: photos2026,
    detail: local({ en: '/home/alex/Desktop', de: '/home/alex/Schreibtisch' }),
    icon: IconFolder,
  },
  {
    id: 's-file',
    name: local({ en: 'phone-backup.zip', de: 'handy-backup.zip' }),
    detail: local({ en: '/shares/backup/devices', de: '/shares/backup/geraete' }),
    icon: IconFileZip,
  },
];

export function notifications(apps: Apps): NotificationItem[] {
  return [
    {
      id: 'n1',
      app: apps.settings,
      message: { code: 'storage.scrub.finished', params: { pool: 'tank', errors: 0 } },
      minutesAgo: 2,
      unread: true,
      tone: 'success',
    },
    {
      id: 'n2',
      app: apps.settings,
      message: { code: 'storage.disk.warm', params: { disk: 3, pool: 'tank', temperature: 52 } },
      minutesAgo: 14,
      unread: true,
      tone: 'warning',
    },
    {
      id: 'n3',
      app: apps.containers,
      message: { code: 'apps.update.available', params: { app: 'Immich', version: '2.4.1' } },
      minutesAgo: 95,
    },
  ];
}

export const jobs: JobItem[] = [
  {
    id: 'j1',
    message: {
      code: 'files.copy',
      params: {
        count: 1204,
        target: 'Photos 2026',
        done: { bytes: 3_100_000_000 },
        total: { bytes: 4_800_000_000 },
        minutes: 2,
      },
    },
    progress: 64,
  },
  {
    id: 'j2',
    message: { code: 'snapshot.create', params: { dataset: 'tank/home' } },
    progress: 20,
  },
];
