import {
  IconActivity,
  IconChecklist,
  IconCoin,
  IconDeviceGamepad2,
  IconFileText,
  IconFolder,
  IconHistory,
  IconMessageCircle,
  IconMovie,
  IconNetwork,
  IconPhoto,
  IconSchool,
  IconShield,
  IconSmartHome,
  IconTerminal2,
  IconTool,
  type TablerIcon,
} from '../../ui/icons';
import type { Category } from './model';

const ICONS: Record<Category, TablerIcon> = {
  productivity: IconChecklist,
  office: IconFileText,
  communication: IconMessageCircle,
  files: IconFolder,
  backup: IconHistory,
  photos: IconPhoto,
  media: IconMovie,
  'home-automation': IconSmartHome,
  network: IconNetwork,
  security: IconShield,
  monitoring: IconActivity,
  development: IconTerminal2,
  education: IconSchool,
  finance: IconCoin,
  games: IconDeviceGamepad2,
  utilities: IconTool,
};

/** The icon of a category in the Store's navigation. */
export function categoryIcon(category: Category): TablerIcon {
  return ICONS[category];
}
