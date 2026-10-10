import {
  IconApps,
  IconCloudOff,
  IconCode,
  IconCpu,
  IconDatabase,
  IconFolders,
  IconLayoutDashboard,
  IconLicense,
  IconNetwork,
  IconRefresh,
  IconShieldLock,
  IconUsersGroup,
  type Icon,
} from '@tabler/icons-react';
import type { LandingIcon } from '../../lib/landingIcons.ts';

export const icons: Record<LandingIcon, Icon> = {
  storage: IconDatabase,
  files: IconFolders,
  apps: IconApps,
  users: IconUsersGroup,
  network: IconNetwork,
  updates: IconRefresh,
  desktop: IconLayoutDashboard,
  shield: IconShieldLock,
  local: IconCloudOff,
  code: IconCode,
  license: IconLicense,
  hardware: IconCpu,
};
