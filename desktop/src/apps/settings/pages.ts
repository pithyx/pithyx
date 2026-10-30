import type { PithyxApp } from '@pithyx/sdk';
import type { ComponentType } from 'react';
import {
  IconAdjustmentsHorizontal,
  IconBatteryCharging,
  IconBell,
  IconCertificate,
  IconCloudDownload,
  IconDatabase,
  IconFolders,
  IconHome,
  IconInfoCircle,
  IconNetwork,
  IconRestore,
  IconRoute,
  IconServer,
  IconServer2,
  IconShieldLock,
  IconTool,
  IconUsers,
  IconWorld,
  type TablerIcon,
} from '../../ui/icons';
import { DisksSection } from '../storage/DisksSection';
import { MaintenanceSection } from '../storage/MaintenanceSection';
import { PoolsSection } from '../storage/PoolsSection';
import { UsersAndGroupsPage } from '../users/UsersAndGroupsPage';
import { BackupPage } from './BackupPage';
import { CertificatesPage } from './CertificatesPage';
import { FileServicesPage } from './FileServicesPage';
import { HomesPage } from './HomesPage';
import { InfoCenterPage } from './InfoCenterPage';
import { NetworkPage } from './NetworkPage';
import { NotificationsPage } from './NotificationsPage';
import { RegionalPage } from './RegionalPage';
import { ReverseProxyPage } from './ReverseProxyPage';
import { SecurityPage } from './SecurityPage';
import { SharesPage } from './SharesPage';
import { StorageAdvancedPage } from './StoragePages';
import { UpdatesPage } from './UpdatesPage';
import { UpsPage } from './UpsPage';

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

export interface SettingsPageInfo {
  id: SettingsPageId;
  /** The group of the navigation (D394, D402). */
  group: SettingsGroup;
  icon: TablerIcon;
  Component: ComponentType<{ app: PithyxApp }>;
}

/** Every page in the order of the navigation and the overview's tiles; personal settings are an app of their own. */
export const SETTINGS_PAGES: readonly SettingsPageInfo[] = [
  { id: 'info', group: 'system', icon: IconInfoCircle, Component: InfoCenterPage },
  { id: 'regional', group: 'system', icon: IconWorld, Component: RegionalPage },
  { id: 'security', group: 'system', icon: IconShieldLock, Component: SecurityPage },
  { id: 'updates', group: 'system', icon: IconCloudDownload, Component: UpdatesPage },
  { id: 'notifications', group: 'system', icon: IconBell, Component: NotificationsPage },
  { id: 'ups', group: 'system', icon: IconBatteryCharging, Component: UpsPage },
  { id: 'backup', group: 'system', icon: IconRestore, Component: BackupPage },
  { id: 'pools', group: 'storage', icon: IconDatabase, Component: PoolsSection },
  { id: 'disks', group: 'storage', icon: IconServer, Component: DisksSection },
  { id: 'maintenance', group: 'storage', icon: IconTool, Component: MaintenanceSection },
  {
    id: 'storageAdvanced',
    group: 'storage',
    icon: IconAdjustmentsHorizontal,
    Component: StorageAdvancedPage,
  },
  { id: 'network', group: 'network', icon: IconNetwork, Component: NetworkPage },
  { id: 'certificates', group: 'network', icon: IconCertificate, Component: CertificatesPage },
  { id: 'reverseProxy', group: 'services', icon: IconRoute, Component: ReverseProxyPage },
  { id: 'fileServices', group: 'services', icon: IconServer2, Component: FileServicesPage },
  { id: 'users', group: 'access', icon: IconUsers, Component: UsersAndGroupsPage },
  { id: 'shares', group: 'access', icon: IconFolders, Component: SharesPage },
  { id: 'homes', group: 'access', icon: IconHome, Component: HomesPage },
];
