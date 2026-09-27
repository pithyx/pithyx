import { IconCornerDownLeft, IconLogout, IconPower, IconRotateClockwise } from '../ui/icons';
import type { TablerIcon } from '../ui/icons';
import type { ReactNode, Ref } from 'react';
import {
  GridList,
  GridListItem,
  Header,
  ListBox,
  ListBoxItem,
  ListBoxSection,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { IconButton } from '../ui/Button';
import { Menu, MenuItem, MenuPopover, MenuSeparator, MenuTrigger } from '../ui/Menu';
import { SearchField } from '../ui/TextField';
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
  pinned: AppInfo[];
  recent: StartEntry[];
  results: StartResults;
  user: { name: string; initials: string; role: string };
  menuRef?: Ref<HTMLDivElement>;
  /** Opens a pinned app or a search result by its id. */
  onOpen?: (id: string) => void;
  onSignOut?: () => void;
  /** Restart and shut down appear once the daemon offers them. */
  power?: boolean;
}

/** Start menu with search. Results are grouped into apps, settings and files; Enter opens the first one. */
export function StartMenu({
  position,
  query,
  onQueryChange,
  pinned,
  recent,
  results,
  user,
  menuRef,
  onOpen,
  onSignOut,
  power = true,
}: StartMenuProps) {
  const { t } = useTranslation();
  const groups = [
    { key: 'apps', title: t('start.results.apps'), entries: results.apps },
    { key: 'settings', title: t('start.results.settings'), entries: results.settings },
    { key: 'files', title: t('start.results.files'), entries: results.files },
  ].filter((group) => group.entries.length > 0);
  const firstId = groups[0]?.entries[0]?.id;

  return (
    <div
      ref={menuRef}
      className={styles.menu}
      role="dialog"
      aria-label={t('start.label')}
      data-position={position}
    >
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
          <section className={styles.section} aria-labelledby="start-pinned">
            <h3 id="start-pinned" className={styles.sectionTitle}>
              {t('start.pinned')}
            </h3>
            <GridList
              aria-labelledby="start-pinned"
              layout="grid"
              className={styles.pinned}
              items={pinned}
              onAction={(key) => onOpen?.(String(key))}
            >
              {(app) => (
                <GridListItem className={styles.tile} textValue={app.name}>
                  <AppIcon app={app} size={40} />
                  <span className={styles.tileName}>{app.name}</span>
                </GridListItem>
              )}
            </GridList>
          </section>
          {recent.length > 0 && (
            <section className={styles.section} aria-labelledby="start-recent">
              <h3 id="start-recent" className={styles.sectionTitle}>
                {t('start.recent')}
              </h3>
              <ListBox
                aria-labelledby="start-recent"
                className={styles.list}
                items={recent}
                onAction={(key) => onOpen?.(String(key))}
              >
                {(entry) => (
                  <ListBoxItem className={styles.entry} textValue={entry.name}>
                    <EntryIcon entry={entry} />
                    <span className={styles.entryText}>
                      <span className={styles.entryName}>{entry.name}</span>
                      {entry.detail !== undefined && (
                        <span className={styles.entryDetail}>{entry.detail}</span>
                      )}
                    </span>
                  </ListBoxItem>
                )}
              </ListBox>
            </section>
          )}
        </div>
      ) : (
        <div className={styles.body}>
          {groups.length === 0 ? (
            <p className={styles.empty}>{t('start.noResults', { query })}</p>
          ) : (
            <ListBox
              aria-label={t('start.resultsLabel')}
              className={styles.list}
              selectionMode="single"
              defaultSelectedKeys={firstId === undefined ? [] : [firstId]}
              onAction={(key) => onOpen?.(String(key))}
            >
              {groups.map((group) => (
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

      <footer className={styles.footer}>
        <span className={styles.user}>
          <span className={styles.avatar} aria-hidden>
            {user.initials}
          </span>
          <span className={styles.entryText}>
            <span className={styles.entryName}>{user.name}</span>
            <span className={styles.entryDetail}>{user.role}</span>
          </span>
        </span>
        <MenuTrigger>
          <IconButton label={t('start.power')}>
            <IconPower size={20} stroke={1.75} />
          </IconButton>
          <MenuPopover placement={position === 'top' ? 'bottom end' : 'top end'}>
            <Menu
              aria-label={t('start.power')}
              onAction={(key) => {
                if (key === 'sign-out') {
                  onSignOut?.();
                }
              }}
            >
              <MenuItem id="sign-out" icon={<IconLogout size={18} stroke={1.75} />}>
                {t('menu.signOut')}
              </MenuItem>
              {power && (
                <>
                  <MenuSeparator />
                  <MenuItem icon={<IconRotateClockwise size={18} stroke={1.75} />}>
                    {t('start.restart')}
                  </MenuItem>
                  <MenuItem icon={<IconPower size={18} stroke={1.75} />} tone="danger">
                    {t('start.shutDown')}
                  </MenuItem>
                </>
              )}
            </Menu>
          </MenuPopover>
        </MenuTrigger>
      </footer>
    </div>
  );
}
