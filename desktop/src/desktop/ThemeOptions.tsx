import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { setTheme, storedTheme, themeChoices, type ThemeChoice } from '../theme/preference';
import { Menu, MenuItem } from '@pithyx/ui';
import { useSizeClass } from '../wm/sizeClass';

/** System, light and dark, with the current choice checked; the account menu's appearance submenu. */
export function ThemeOptions() {
  const { t } = useTranslation();
  const [choice, setChoice] = useState<ThemeChoice>(storedTheme);
  return (
    <Menu
      aria-label={t('menu.theme')}
      selectionMode="single"
      selectedKeys={[choice]}
      onAction={(key) => {
        const next = themeChoices.find((entry) => entry === key);
        if (next !== undefined) {
          setChoice(next);
          setTheme(next);
        }
      }}
    >
      {themeChoices.map((entry) => (
        <MenuItem key={entry} id={entry}>
          {t(`menu.themeChoice.${entry}`)}
        </MenuItem>
      ))}
    </Menu>
  );
}

/**
 * Where the appearance submenu opens: beside its item, or below it on a phone, where the account menu leaves no room
 * beside it.
 */
export function useSubmenuPlacement() {
  return useSizeClass() === 'compact' ? 'bottom end' : 'end top';
}
