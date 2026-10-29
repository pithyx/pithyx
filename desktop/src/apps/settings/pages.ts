import type { PithyxApp } from '@pithyx/sdk';
import type { ComponentType } from 'react';
import {
  IconBatteryCharging,
  IconBell,
  IconCertificate,
  IconCloudDownload,
  IconFolders,
  IconHome,
  IconInfoCircle,
  IconNetwork,
  IconRestore,
  IconServer2,
  IconShieldLock,
  IconUsers,
  IconWorld,
  type TablerIcon,
} from '../../ui/icons';
import { UsersAndGroupsPage } from '../users/UsersAndGroupsPage';
import { BackupPage } from './BackupPage';
import { CertificatesPage } from './CertificatesPage';
import { FileServicesPage } from './FileServicesPage';
import { HomesPage } from './HomesPage';
import { InfoCenterPage } from './InfoCenterPage';
import { NetworkPage } from './NetworkPage';
import { NotificationsPage } from './NotificationsPage';
import { RegionalPage } from './RegionalPage';
import { SecurityPage } from './SecurityPage';
import { SharesPage } from './SharesPage';
import { UpdatesPage } from './UpdatesPage';
import { UpsPage } from './UpsPage';

export type SettingsPageId =
  | 'info'
  | 'network'
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

export interface SettingsPageInfo {
  id: SettingsPageId;
  /** The group of the navigation (D394): the box itself, or who reaches what. */
  group: 'system' | 'access';
  icon: TablerIcon;
  Component: ComponentType<{ app: PithyxApp }>;
}

/** Every page in the order of the navigation and the overview's tiles; personal settings are an app of their own. */
export const SETTINGS_PAGES: readonly SettingsPageInfo[] = [
  { id: 'info', group: 'system', icon: IconInfoCircle, Component: InfoCenterPage },
  { id: 'network', group: 'system', icon: IconNetwork, Component: NetworkPage },
  { id: 'security', group: 'system', icon: IconShieldLock, Component: SecurityPage },
  { id: 'certificates', group: 'system', icon: IconCertificate, Component: CertificatesPage },
  { id: 'updates', group: 'system', icon: IconCloudDownload, Component: UpdatesPage },
  { id: 'notifications', group: 'system', icon: IconBell, Component: NotificationsPage },
  { id: 'regional', group: 'system', icon: IconWorld, Component: RegionalPage },
  { id: 'ups', group: 'system', icon: IconBatteryCharging, Component: UpsPage },
  { id: 'backup', group: 'system', icon: IconRestore, Component: BackupPage },
  { id: 'users', group: 'access', icon: IconUsers, Component: UsersAndGroupsPage },
  { id: 'shares', group: 'access', icon: IconFolders, Component: SharesPage },
  { id: 'homes', group: 'access', icon: IconHome, Component: HomesPage },
  { id: 'fileServices', group: 'access', icon: IconServer2, Component: FileServicesPage },
];
