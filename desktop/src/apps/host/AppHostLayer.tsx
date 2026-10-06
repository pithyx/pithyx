import { ContextMenuPopover, Menu, MenuItem, MenuSeparator } from '@pithyx/ui';
import { lazy, Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { useDesktop } from '../../wm/store';
import { useAppName } from '../names';
import { closeMenu, useHost, type AppMenu } from './store';

// The dialogs for an app that does not answer and for a copy the browser refused are rare; they load when needed.
const HostDialogs = lazy(() =>
  import('./HostDialogs').then(({ HostDialogs }) => ({ default: HostDialogs })),
);

/** A menu an app asked for, drawn like the desktop's own: same look, keyboard and touch handling. */
function AppMenuPopover({ menu }: { menu: AppMenu }) {
  const { t } = useTranslation();
  const appName = useAppName();
  const window = useDesktop((state) => state.windows[menu.windowId]);
  const name = window?.title ?? (window === undefined ? '' : appName(window.app));
  const entries = menu.items.flatMap((item) => ('separator' in item ? [] : [item]));
  const checkable = entries.some((item) => item.checked !== undefined);
  return (
    <ContextMenuPopover
      x={menu.x}
      y={menu.y}
      onClose={() => {
        closeMenu(null);
      }}
    >
      <Menu
        aria-label={t('appHost.menu', { name })}
        autoFocus="first"
        selectionMode={checkable ? 'multiple' : 'none'}
        selectedKeys={entries.filter((item) => item.checked === true).map((item) => item.id)}
        disabledKeys={entries.filter((item) => item.disabled === true).map((item) => item.id)}
        onAction={(key) => {
          closeMenu(String(key));
        }}
        onClose={() => {
          closeMenu(null);
        }}
      >
        {menu.items.map((item, index) =>
          'separator' in item ? (
            <MenuSeparator key={`separator-${String(index)}`} />
          ) : (
            <MenuItem
              key={item.id}
              id={item.id}
              tone={item.danger === true ? 'danger' : 'default'}
              {...(item.shortcut === undefined ? {} : { shortcut: item.shortcut })}
            >
              {item.label}
            </MenuItem>
          ),
        )}
      </Menu>
    </ContextMenuPopover>
  );
}

/** What the desktop draws for app windows above everything else: their context menus and questions. */
export function AppHostLayer() {
  const menu = useHost((state) => state.menu);
  const dialogs = useHost((state) => state.unresponsive.length > 0 || state.copy !== null);
  return (
    <>
      {menu !== null && (
        <AppMenuPopover key={`${menu.windowId}:${String(menu.x)}:${String(menu.y)}`} menu={menu} />
      )}
      {dialogs && (
        <Suspense>
          <HostDialogs />
        </Suspense>
      )}
    </>
  );
}
