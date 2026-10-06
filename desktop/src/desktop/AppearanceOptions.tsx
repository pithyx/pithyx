import { unwrap } from '@pithyx/api-client';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { showError } from '../errors/errorStore';
import {
  ACCENTS,
  applyAppearance,
  currentAppearance,
  useAppearance,
  WALLPAPERS,
  type Appearance,
} from '../theme/appearance';
import { Menu, MenuItem } from '@pithyx/ui';
import styles from './Desktop.module.css';

/** Applies a change at once and saves it for every device; a refused save puts the previous look back. */
async function saveAppearance(change: Partial<Appearance>) {
  const previous = currentAppearance();
  const next = { ...previous, ...change };
  applyAppearance(next);
  try {
    unwrap(await api.PUT('/api/desktop/appearance', { body: next }));
  } catch (error) {
    applyAppearance(previous);
    showError(error);
  }
}

/** The accent presets with a swatch each, the current one checked; the account menu's accent submenu. */
export function AccentOptions() {
  const { t } = useTranslation();
  const { accent } = useAppearance();
  return (
    <Menu
      aria-label={t('menu.accent')}
      selectionMode="single"
      selectedKeys={[accent]}
      onAction={(key) => {
        const next = ACCENTS.find((entry) => entry === key);
        if (next !== undefined) {
          void saveAppearance({ accent: next });
        }
      }}
    >
      {ACCENTS.map((entry) => (
        <MenuItem
          key={entry}
          id={entry}
          icon={<span className={styles.swatch} data-accent={entry} aria-hidden />}
        >
          {t(`menu.accents.${entry}`)}
        </MenuItem>
      ))}
    </Menu>
  );
}

/** The Pithyx boxes or a plain wallpaper; the account menu's wallpaper submenu. */
export function WallpaperOptions() {
  const { t } = useTranslation();
  const { wallpaper } = useAppearance();
  return (
    <Menu
      aria-label={t('menu.wallpaper')}
      selectionMode="single"
      selectedKeys={[wallpaper]}
      onAction={(key) => {
        const next = WALLPAPERS.find((entry) => entry === key);
        if (next !== undefined) {
          void saveAppearance({ wallpaper: next });
        }
      }}
    >
      {WALLPAPERS.map((entry) => (
        <MenuItem key={entry} id={entry}>
          {t(`menu.wallpapers.${entry}`)}
        </MenuItem>
      ))}
    </Menu>
  );
}
