import {
  IconCornerDownLeft,
  IconExternalLink,
  IconHome,
  IconLogout,
  IconPlus,
  IconPower,
  IconRotateClockwise,
  IconUserCog,
} from '../ui/icons';
import type { TablerIcon } from '../ui/icons';
import { useState, type ReactNode, type Ref } from 'react';
import {
  Button as AriaButton,
  GridList,
  GridListHeader,
  GridListItem,
  GridListSection,
  Header,
  ListBox,
  ListBoxItem,
  ListBoxSection,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import {
  ContextMenuPopover,
  IconButton,
  Menu,
  MenuItem,
  MenuPopover,
  MenuTrigger,
  SearchField,
} from '@pithyx/ui';
import { AppIcon } from './AppIcon';
import styles from './StartMenu.module.css';
import type { AppInfo, TaskbarPosition } from './types';

export interface StartEntry {
  id: string;
  name: string;
  detail?: string;
  app?: Pick<AppInfo, 'icon' | 'tone'>;
  icon?: TablerIcon;
}

/** A group of the app list: System (administrators only), Apps or You. */
export interface StartGroup {
  id: string;
  title: string;
  apps: AppInfo[];
}

export interface StartResults {
  apps: StartEntry[];
  settings: StartEntry[];
  files: StartEntry[];
}

/** Marks the part of a name that matches the query. */
function Highlight({ text, query }: { text: string; query: string }): ReactNode {
  const index = text.toLocaleLowerCase().indexOf(query.toLocaleLowerCase());
  if (query === '' || index < 0) {
    return text;
  }
  return (
    <>
      {text.slice(0, index)}
      <mark className={styles.mark}>{text.slice(index, index + query.length)}</mark>
      {text.slice(index + query.length)}
    </>
  );
}

function EntryIcon({ entry }: { entry: StartEntry }) {
  if (entry.app !== undefined) {
    return <AppIcon app={entry.app} size={28} />;
  }
  const Glyph = entry.icon;
  return (
    <span className={styles.glyph} aria-hidden>
      {Glyph !== undefined && <Glyph size={20} stroke={1.75} />}
    </span>
  );
}

export interface StartMenuProps {
  position: TaskbarPosition;
  query: string;
  onQueryChange?: (query: string) => void;
  groups: StartGroup[];
  results: StartResults;
  /** The tile area beside the app list: status for administrators, then files and folders. */
  tiles?: ReactNode;
  user: { name: string; initials: string; role: string };
  /** Wraps the profile button in the account menu (two-factor, language, desktop, taskbar). */
  userMenu?: (trigger: ReactNode) => ReactNode;
  menuRef?: Ref<HTMLDivElement>;
  /** Opens an app or a search result by its id. */
  onOpen?: (id: string) => void;
  /** Opens Files in the user's home folder. */
  onHome?: () => void;
  /** Opens Personal settings. */
  onPreferences?: () => void;
  /** Puts a shortcut to an app on the desktop; the app tiles' context menu offers it when set. */
  onAddToDesktop?: (id: string) => void;
  onSignOut?: () => void;
  /** Restart and shut down, for users with that right; sign-out has its own button. */
  power?: boolean;
  /** The power menu's choice; the caller asks for confirmation. */
  onPower?: (action: 'restart' | 'shutdown') => void;
}

/**
 * The Pithyx menu attached to the taskbar (D394): a rail with the account, home, personal settings, sign out and power,
 * the searchable app list grouped by area, and the tiles. Search results are grouped into apps, settings and files;
 * Enter opens the first one.
 */
export function StartMenu({
  position,
  query,
  onQueryChange,
  groups,
  results,
  tiles,
  user,
  userMenu,
  menuRef,
  onOpen,
  onHome,
  onPreferences,
  onAddToDesktop,
  onSignOut,
  power = true,
  onPower,
}: StartMenuProps) {
  const { t } = useTranslation();
  const resultGroups = [
    { key: 'apps', title: t('start.results.apps'), entries: results.apps },
    { key: 'settings', title: t('start.results.settings'), entries: results.settings },
    { key: 'files', title: t('start.results.files'), entries: results.files },
  ].filter((group) => group.entries.length > 0);
  const firstId = resultGroups[0]?.entries[0]?.id;
  const apps = groups.flatMap((group) => group.apps);
  const [menu, setMenu] = useState<{ app: AppInfo; x: number; y: number } | null>(null);
  const profile = (
    <AriaButton
      className={styles.railButton}
      aria-label={t('start.userMenu', { name: user.name })}
      data-role={user.role}
    >
      <span className={styles.avatar} aria-hidden>
        {user.initials}
      </span>
    </AriaButton>
  );

  return (
    <div
      ref={menuRef}
      className={styles.menu}
      role="dialog"
      aria-label={t('start.label')}
      data-position={position}
    >
      <nav className={styles.rail} aria-label={t('start.rail')}>
        {userMenu === undefined ? profile : userMenu(profile)}
        {onHome !== undefined && (
          <IconButton label={t('start.home')} className={styles.railButton} onPress={onHome}>
            <IconHome size={20} stroke={1.75} />
          </IconButton>
        )}
        <span className={styles.railSpacer} />
        {onPreferences !== undefined && (
          <IconButton
            label={t('menu.preferences')}
            className={styles.railButton}
            onPress={onPreferences}
          >
            <IconUserCog size={20} stroke={1.75} />
          </IconButton>
        )}
        <IconButton
          label={t('menu.signOut')}
          className={styles.railButton}
          onPress={() => {
            onSignOut?.();
          }}
        >
          <IconLogout size={20} stroke={1.75} />
        </IconButton>
        {power && (
          <MenuTrigger>
            <IconButton label={t('start.power')} className={styles.railButton}>
              <IconPower size={20} stroke={1.75} />
            </IconButton>
            <MenuPopover placement={position === 'top' ? 'bottom start' : 'top start'}>
              <Menu
                aria-label={t('start.power')}
                onAction={(key) => {
                  onPower?.(key === 'shutdown' ? 'shutdown' : 'restart');
                }}
              >
                <MenuItem id="restart" icon={<IconRotateClockwise size={18} stroke={1.75} />}>
                  {t('start.restart')}
                </MenuItem>
                <MenuItem id="shutdown" icon={<IconPower size={18} stroke={1.75} />} tone="danger">
                  {t('start.shutDown')}
                </MenuItem>
              </Menu>
            </MenuPopover>
          </MenuTrigger>
        )}
      </nav>
      <div className={styles.appColumn}>
        <div className={styles.search}>
          <SearchField
            label={t('start.searchLabel')}
            placeholder={t('start.search')}
            value={query}
            onChange={(value) => onQueryChange?.(value)}
            onSubmit={() => {
              if (firstId !== undefined) {
                onOpen?.(firstId);
              }
            }}
            autoFocus
          />
        </div>

        {query === '' ? (
          <div className={styles.body}>
            <section
              className={styles.section}
              aria-label={t('start.apps')}
              onKeyDown={(event) => {
                if (event.key !== 'ContextMenu' && !(event.key === 'F10' && event.shiftKey)) {
                  return;
                }
                const tile =
                  event.target instanceof Element ? event.target.closest('[data-app]') : null;
                const app = apps.find((entry) => entry.id === tile?.getAttribute('data-app'));
                if (tile !== null && app !== undefined) {
                  event.preventDefault();
                  const rect = tile.getBoundingClientRect();
                  setMenu({ app, x: rect.left, y: rect.bottom });
                }
              }}
            >
              <GridList
                aria-label={t('start.apps')}
                className={styles.apps}
                onAction={(key) => onOpen?.(String(key))}
              >
                {groups
                  .filter((group) => group.apps.length > 0)
                  .map((group) => (
                    <GridListSection key={group.id} className={styles.group}>
                      <GridListHeader className={styles.sectionTitle}>{group.title}</GridListHeader>
                      {group.apps.map((app) => (
                        <GridListItem
                          key={app.id}
                          id={app.id}
                          className={styles.tile}
                          textValue={app.name}
                          onContextMenu={(event) => {
                            event.preventDefault();
                            setMenu({ app, x: event.clientX, y: event.clientY });
                          }}
                          data-app={app.id}
                        >
                          <AppIcon app={app} size={28} />
                          <span className={styles.tileName}>{app.name}</span>
                        </GridListItem>
                      ))}
                    </GridListSection>
                  ))}
              </GridList>
            </section>
          </div>
        ) : (
          <div className={styles.body}>
            {resultGroups.length === 0 ? (
              <p className={styles.empty}>{t('start.noResults', { query })}</p>
            ) : (
              <ListBox
                aria-label={t('start.resultsLabel')}
                className={styles.list}
                selectionMode="single"
                defaultSelectedKeys={firstId === undefined ? [] : [firstId]}
                onAction={(key) => onOpen?.(String(key))}
              >
                {resultGroups.map((group) => (
                  <ListBoxSection key={group.key} className={styles.group}>
                    <Header className={styles.sectionTitle}>{group.title}</Header>
                    {group.entries.map((entry) => (
                      <ListBoxItem
                        key={entry.id}
                        id={entry.id}
                        className={styles.entry}
                        textValue={entry.name}
                      >
                        <EntryIcon entry={entry} />
                        <span className={styles.entryText}>
                          <span className={styles.entryName}>
                            <Highlight text={entry.name} query={query} />
                          </span>
                          {entry.detail !== undefined && (
                            <span className={styles.entryDetail}>{entry.detail}</span>
                          )}
                        </span>
                        {entry.id === firstId && (
                          <span className={styles.enterHint}>
                            <IconCornerDownLeft size={14} stroke={2} aria-hidden />
                            {t('start.openHint')}
                          </span>
                        )}
                      </ListBoxItem>
                    ))}
                  </ListBoxSection>
                ))}
              </ListBox>
            )}
          </div>
        )}
      </div>

      {tiles !== undefined && query === '' && <div className={styles.tiles}>{tiles}</div>}

      {menu !== null && (
        <ContextMenuPopover
          x={menu.x}
          y={menu.y}
          onClose={() => {
            setMenu(null);
          }}
        >
          <Menu
            aria-label={t('start.appMenu', { name: menu.app.name })}
            autoFocus="first"
            onAction={(key) => {
              setMenu(null);
              if (key === 'add') {
                onAddToDesktop?.(menu.app.id);
              } else {
                onOpen?.(menu.app.id);
              }
            }}
          >
            <MenuItem id="open" icon={<IconExternalLink size={18} stroke={1.75} />}>
              {t('start.open')}
            </MenuItem>
            {onAddToDesktop !== undefined && (
              <MenuItem id="add" icon={<IconPlus size={18} stroke={1.75} />}>
                {t('taskbar.addToDesktop')}
              </MenuItem>
            )}
          </Menu>
        </ContextMenuPopover>
      )}
    </div>
  );
}
