// Sample content for the design mockups. It is not user-visible product text and stays English only.
import {
  IconBox,
  IconDatabase,
  IconFileText,
  IconFileTypePdf,
  IconFileZip,
  IconFolder,
  IconFolders,
  IconListDetails,
  IconPalette,
  IconPhoto,
  IconSettings,
  IconShieldLock,
  IconTerminal2,
  IconUsers,
} from '@tabler/icons-react';
import type { StartEntry, StartResults } from '../shell/StartMenu';
import type { AppInfo } from '../shell/types';

export const apps = {
  files: { id: 'files', name: 'Files', icon: IconFolders, tone: 'petrol' },
  storage: { id: 'storage', name: 'Storage', icon: IconDatabase, tone: 'ocean' },
  logs: { id: 'logs', name: 'Logs', icon: IconListDetails, tone: 'graphite' },
  settings: { id: 'settings', name: 'Settings', icon: IconSettings, tone: 'indigo' },
  photos: { id: 'photos', name: 'Photos', icon: IconPhoto, tone: 'rose' },
  users: { id: 'users', name: 'Users', icon: IconUsers, tone: 'plum' },
  containers: { id: 'containers', name: 'Containers', icon: IconBox, tone: 'moss' },
  terminal: { id: 'terminal', name: 'Terminal', icon: IconTerminal2, tone: 'amber' },
} satisfies Record<string, AppInfo>;

export const user = {
  name: 'Alex Morgan',
  initials: 'AM',
  role: 'Administrator',
  username: 'alex',
};
export const hostname = 'nas-living-room';
export const now = new Date('2026-09-26T14:32:00');
export const requestId = '4bf92f3577b34da6a3ce929d0e0e4736';
export const recoveryKey = 'VYX4-7KQ2-MZ9P-3HT8-WB6N-R2DC-5FJX-L8QE';

export const noteText =
  'Replace disk 3 in tank before Friday.\nOrder: 8 TB, CMR.\n\nCall Sam about the backup box.';

export const desktopItems = [
  { name: 'Documents', item: { kind: 'folder' } },
  { name: 'Photos 2026', item: { kind: 'folder' } },
  { name: 'Projects', item: { kind: 'folder' } },
  { name: 'Tax return 2025.pdf', item: { kind: 'file', icon: IconFileTypePdf } },
  { name: 'network-plan.txt', item: { kind: 'file', icon: IconFileText } },
  { name: 'Files', item: { kind: 'shortcut', app: apps.files } },
  { name: 'Storage', item: { kind: 'shortcut', app: apps.storage } },
] as const;

export const pinned: AppInfo[] = [
  apps.files,
  apps.storage,
  apps.settings,
  apps.users,
  apps.photos,
  apps.containers,
  apps.logs,
  apps.terminal,
];

export const recent: StartEntry[] = [
  { id: 'r1', name: 'Tax return 2025.pdf', detail: '/home/alex/Documents', icon: IconFileTypePdf },
  { id: 'r2', name: 'network-plan.txt', detail: '/home/alex/Desktop', icon: IconFileText },
  { id: 'r3', name: 'backup-2026-09.zip', detail: '/shares/backup', icon: IconFileZip },
];

export const searchQuery = 'pho';

export const searchResults: StartResults = {
  apps: [{ id: 's-photos', name: 'Photos', detail: 'App', app: apps.photos }],
  settings: [
    {
      id: 's-wallpaper',
      name: 'Wallpaper and photos',
      detail: 'Settings › Personalization',
      icon: IconPalette,
    },
    {
      id: 's-perm',
      name: 'Photo library permissions',
      detail: 'Settings › Users',
      icon: IconShieldLock,
    },
  ],
  files: [
    { id: 's-folder', name: 'Photos 2026', detail: '/home/alex/Desktop', icon: IconFolder },
    { id: 's-file', name: 'phone-backup.zip', detail: '/shares/backup/devices', icon: IconFileZip },
  ],
};

export const notifications = [
  {
    id: 'n1',
    app: apps.storage,
    title: 'Scrub finished',
    body: 'Pool tank was checked. No errors found.',
    minutesAgo: 2,
    unread: true,
    tone: 'success',
  },
  {
    id: 'n2',
    app: apps.storage,
    title: 'Disk getting warm',
    body: 'Disk 3 in tank reports 52 °C. Check the airflow.',
    minutesAgo: 14,
    unread: true,
    tone: 'warning',
    action: 'Show disk',
  },
  {
    id: 'n3',
    app: apps.containers,
    title: 'Update available',
    body: 'Immich 2.4.1 is ready to install.',
    minutesAgo: 95,
  },
] as const;

export const jobs = [
  {
    id: 'j1',
    title: 'Copying 1,204 files to Photos 2026',
    progress: 64,
    detail: '3.1 GB of 4.8 GB, about 2 minutes left',
  },
  { id: 'j2', title: 'Snapshot of tank/home', progress: 20, detail: 'Preparing' },
];
