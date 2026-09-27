import { useTranslation } from 'react-i18next';
import { Button as AriaButton } from 'react-aria-components';
import { AppIcon } from '../shell/AppIcon';
import type { AppInfo } from '../shell/types';
import { Menu, MenuItem, MenuPopover, MenuSeparator, MenuTrigger } from '../ui/Menu';
import { runWindowCommand, type WindowCommand } from './commands';
import type { Placement } from './types';
import { useWindowUi } from './ui';
import styles from './WindowLayer.module.css';

/**
 * The app icon in the title bar opens the window menu: every window action without dragging, with its shortcut.
 * Move and Size start the keyboard mode.
 */
export function WindowMenu({
  id,
  app,
  title,
  placement,
  free,
}: {
  id: string;
  app: Pick<AppInfo, 'icon' | 'tone'>;
  title: string;
  placement: Placement;
  /** Free windows (expanded size class) can be moved and resized. */
  free: boolean;
}) {
  const { t } = useTranslation();
  const open = useWindowUi((state) => state.menu === id);
  return (
    <MenuTrigger
      isOpen={open}
      onOpenChange={(next) => {
        useWindowUi.setState({ menu: next ? id : null });
      }}
    >
      <AriaButton className={styles.menuButton} aria-label={t('window.menu', { title })}>
        <AppIcon app={app} size={20} />
      </AriaButton>
      <MenuPopover placement="bottom start">
        <Menu
          aria-label={t('window.menu', { title })}
          onAction={(key) => {
            runWindowCommand(id, String(key) as WindowCommand);
          }}
        >
          {placement.kind !== 'free' && (
            <MenuItem id="restore" shortcut={t('window.keys.down')}>
              {t('window.restore')}
            </MenuItem>
          )}
          {free && <MenuItem id="move">{t('window.move')}</MenuItem>}
          {free && <MenuItem id="size">{t('window.size')}</MenuItem>}
          <MenuItem id="minimize">{t('window.minimize')}</MenuItem>
          {placement.kind !== 'maximized' && (
            <MenuItem id="maximize" shortcut={t('window.keys.up')}>
              {t('window.maximize')}
            </MenuItem>
          )}
          <MenuItem id="snap-left" shortcut={t('window.keys.left')}>
            {t('window.snapLeft')}
          </MenuItem>
          <MenuItem id="snap-right" shortcut={t('window.keys.right')}>
            {t('window.snapRight')}
          </MenuItem>
          <MenuSeparator />
          <MenuItem id="close">{t('window.close')}</MenuItem>
        </Menu>
      </MenuPopover>
    </MenuTrigger>
  );
}
