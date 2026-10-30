// The pages of Settings without their components, so the desktop's menu search can name them without loading the app.

export type SettingsPageId =
  | 'info'
  | 'pools'
  | 'disks'
  | 'maintenance'
  | 'storageAdvanced'
  | 'network'
  | 'reverseProxy'
  | 'certificates'
  | 'regional'
  | 'updates'
  | 'ups'
  | 'notifications'
  | 'backup'
  | 'security'
  | 'shares'
  | 'homes'
  | 'fileServices'
  | 'users';

/** The groups of the navigation in their order: everything that sets up or changes the box (D402). */
export const SETTINGS_GROUPS = ['system', 'storage', 'network', 'services', 'access'] as const;
export type SettingsGroup = (typeof SETTINGS_GROUPS)[number];

/** Every page with its group, in the order of the navigation; `pages.ts` adds the icons and components. */
export const SETTINGS_PAGE_IDS: readonly { id: SettingsPageId; group: SettingsGroup }[] = [
  { id: 'info', group: 'system' },
  { id: 'regional', group: 'system' },
  { id: 'security', group: 'system' },
  { id: 'updates', group: 'system' },
  { id: 'notifications', group: 'system' },
  { id: 'ups', group: 'system' },
  { id: 'backup', group: 'system' },
  { id: 'pools', group: 'storage' },
  { id: 'disks', group: 'storage' },
  { id: 'maintenance', group: 'storage' },
  { id: 'storageAdvanced', group: 'storage' },
  { id: 'network', group: 'network' },
  { id: 'certificates', group: 'network' },
  { id: 'reverseProxy', group: 'services' },
  { id: 'fileServices', group: 'services' },
  { id: 'users', group: 'access' },
  { id: 'shares', group: 'access' },
  { id: 'homes', group: 'access' },
];
