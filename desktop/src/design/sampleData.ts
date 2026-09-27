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
import { useTranslation } from 'react-i18next';
import type { Language } from '../i18n';
import type { JobItem, NotificationItem } from '../shell/Notifications';
import type { StartEntry } from '../shell/StartMenu';
import type { AppInfo } from '../shell/types';

export const appDefinitions = {
  files: { id: 'files', icon: IconFolders, tone: 'petrol' },
  storage: { id: 'storage', icon: IconDatabase, tone: 'ocean' },
  logs: { id: 'logs', icon: IconListDetails, tone: 'graphite' },
  settings: { id: 'settings', icon: IconSettings, tone: 'indigo' },
  photos: { id: 'photos', icon: IconPhoto, tone: 'rose' },
  users: { id: 'users', icon: IconUsers, tone: 'plum' },
  containers: { id: 'containers', icon: IconBox, tone: 'moss' },
  terminal: { id: 'terminal', icon: IconTerminal2, tone: 'amber' },
} satisfies Record<string, Omit<AppInfo, 'name'>>;

export type AppId = keyof typeof appDefinitions;
export type Apps = Record<AppId, AppInfo>;

/** The core apps with their translated names. */
export function useApps(): Apps {
  const { t } = useTranslation();
  const apps = {} as Apps;
  for (const [id, definition] of Object.entries(appDefinitions) as [
    AppId,
    Omit<AppInfo, 'name'>,
  ][]) {
    apps[id] = { ...definition, name: t(`apps.${id}`) };
  }
  return apps;
}

export const user = { name: 'Alex Morgan', firstName: 'Alex', initials: 'AM', username: 'alex' };
export const hostname = 'nas-living-room';
export const now = new Date('2026-09-26T14:32:00');
export const requestId = '4bf92f3577b34da6a3ce929d0e0e4736';
export const recoveryKey = 'VYX4-7KQ2-MZ9P-3HT8-WB6N-R2DC-5FJX-L8QE';
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

export const pinned: AppId[] = [
  'files',
  'storage',
  'settings',
  'users',
  'photos',
  'containers',
  'logs',
  'terminal',
];

export const recent: StartEntry[] = [
  { id: 'r1', name: 'Tax return 2025.pdf', detail: '/home/alex/Documents', icon: IconFileTypePdf },
  { id: 'r2', name: 'network-plan.txt', detail: '/home/alex/Desktop', icon: IconFileText },
  { id: 'r3', name: 'backup-2026-09.zip', detail: '/shares/backup', icon: IconFileZip },
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
