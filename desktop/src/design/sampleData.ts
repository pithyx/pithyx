import type { RecentFile } from '../apps/files/recent';
// Sample content for the design mockups. Interface text comes from i18n; what stays here is user data (names of
// files, folders, pools and notes) and raw values that the formatters turn into localized text.
import {
  IconBox,
  IconDatabase,
  IconFileText,
  IconFileTypePdf,
  IconFileZip,
  IconFolder,
  IconFolders,
  IconListDetails,
  IconPhoto,
  IconSettings,
  IconTerminal2,
  IconUsers,
} from '../ui/icons';
import type { components } from '@pithyx/api-client';
import { coreAppIds } from '../apps/ids';
import { useAppName } from '../apps/names';
import type { Language } from '../i18n';
import type { JobItem, NotificationItem } from '../shell/Notifications';
import type { StartEntry } from '../shell/StartMenu';
import type { AppInfo } from '../shell/types';

export const appDefinitions = {
  files: { id: coreAppIds.files, icon: IconFolders, tone: 'petrol' },
  storage: { id: coreAppIds.storage, icon: IconDatabase, tone: 'ocean' },
  logs: { id: coreAppIds.logs, icon: IconListDetails, tone: 'graphite' },
  settings: { id: coreAppIds.settings, icon: IconSettings, tone: 'indigo' },
  photos: { id: 'photos', icon: IconPhoto, tone: 'rose' },
  users: { id: 'users', icon: IconUsers, tone: 'plum' },
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
export const currentFolder = 'Photos 2026';

export const noteText =
  'Replace disk 3 in tank before Friday.\nOrder: 8 TB, CMR.\n\nCall Sam about the backup box.';

export const desktopItems = [
  { name: 'Documents', item: { kind: 'folder' } },
  { name: 'Photos 2026', item: { kind: 'folder' } },
  { name: 'Projects', item: { kind: 'folder' } },
  { name: 'Tax return 2025.pdf', item: { kind: 'file', icon: IconFileTypePdf } },
  { name: 'network-plan.txt', item: { kind: 'file', icon: IconFileText } },
] as const;

export const desktopShortcuts: AppId[] = ['files', 'storage'];

export const startApps: AppId[] = [
  'files',
  'storage',
  'settings',
  'users',
  'photos',
  'containers',
  'logs',
  'terminal',
];

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
    path: 'Documents/Tax return 2025.pdf',
    size: 482_133,
    modified: '2026-10-28T09:12:00Z',
    openedAt: '2026-10-29T08:41:00Z',
  },
  {
    share: 1,
    path: 'network-plan.txt',
    size: 2_311,
    modified: '2026-10-28T16:02:00Z',
    openedAt: '2026-10-28T16:05:00Z',
  },
  {
    share: 2,
    path: 'Photos 2026/Lake.jpg',
    size: 3_904_221,
    modified: '2026-10-12T11:30:00Z',
    openedAt: '2026-10-27T19:20:00Z',
  },
  {
    share: 3,
    path: 'Music/Live set.flac',
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
export const searchQuery: Record<Language, string> = { en: 'pho', de: 'fot' };

export const searchFiles: StartEntry[] = [
  { id: 's-folder', name: 'Photos 2026', detail: '/home/alex/Desktop', icon: IconFolder },
  { id: 's-file', name: 'phone-backup.zip', detail: '/shares/backup/devices', icon: IconFileZip },
];

export function notifications(apps: Apps): NotificationItem[] {
  return [
    {
      id: 'n1',
      app: apps.storage,
      message: { code: 'storage.scrub.finished', params: { pool: 'tank', errors: 0 } },
      minutesAgo: 2,
      unread: true,
      tone: 'success',
    },
    {
      id: 'n2',
      app: apps.storage,
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
