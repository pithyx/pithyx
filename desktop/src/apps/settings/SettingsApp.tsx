import type { PithyxApp } from '@pithyx/sdk';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState, type ComponentType } from 'react';
import { useTranslation } from 'react-i18next';
import {
  IconArrowLeft,
  IconCertificate,
  IconCloudDownload,
  IconFolders,
  IconInfoCircle,
  IconNetwork,
  IconRestore,
  IconRoute,
  IconServer2,
  IconUsers,
  IconUsersGroup,
  IconWorld,
  type TablerIcon,
} from '../../ui/icons';
import { SearchField } from '@pithyx/ui';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { onInternal, SHARES_CHANGED, STORAGE_CHANGED } from '../sdk/internal';
import { GroupsPage } from '../users/GroupsPage';
import { UsersPage } from '../users/UsersPage';
import { BackupPage } from './BackupPage';
import { CertificatesPage } from './CertificatesPage';
import { FileServicesPage } from './FileServicesPage';
import { InfoCenterPage } from './InfoCenterPage';
import { NetworkPage } from './NetworkPage';
import { RegionalPage } from './RegionalPage';
import { ReverseProxyPage } from './ReverseProxyPage';
import styles from './Settings.module.css';
import { sharesKey } from './shares';
import { SharesPage } from './SharesPage';
import { UpdatesPage } from './UpdatesPage';

type PageId =
  | 'info'
  | 'network'
  | 'certificates'
  | 'reverseProxy'
  | 'regional'
  | 'updates'
  | 'backup'
  | 'shares'
  | 'fileServices'
  | 'users'
  | 'groups';

interface SettingsPage {
  id: PageId;
  category: 'system' | 'sharing' | 'accounts';
  icon: TablerIcon;
  Component: ComponentType<{ app: PithyxApp }>;
}

// In the order of the overview. Only finished or clearly marked pages; personal settings are in the user menu.
const PAGES: readonly SettingsPage[] = [
  { id: 'info', category: 'system', icon: IconInfoCircle, Component: InfoCenterPage },
  { id: 'network', category: 'system', icon: IconNetwork, Component: NetworkPage },
  { id: 'certificates', category: 'system', icon: IconCertificate, Component: CertificatesPage },
  { id: 'reverseProxy', category: 'system', icon: IconRoute, Component: ReverseProxyPage },
  { id: 'regional', category: 'system', icon: IconWorld, Component: RegionalPage },
  { id: 'updates', category: 'system', icon: IconCloudDownload, Component: UpdatesPage },
  { id: 'backup', category: 'system', icon: IconRestore, Component: BackupPage },
  { id: 'shares', category: 'sharing', icon: IconFolders, Component: SharesPage },
  { id: 'fileServices', category: 'sharing', icon: IconServer2, Component: FileServicesPage },
  { id: 'users', category: 'accounts', icon: IconUsers, Component: UsersPage },
  { id: 'groups', category: 'accounts', icon: IconUsersGroup, Component: GroupsPage },
];
const CATEGORIES = ['system', 'sharing', 'accounts'] as const;

/** The page named by launch arguments such as `{ page: 'users' }`, if any. */
function pageOf(args: unknown): PageId | null {
  const page = (args as { page?: unknown } | null)?.page;
  return PAGES.find((entry) => entry.id === page)?.id ?? null;
}

/**
 * Settings: system settings only, as an overview of categories with tiles and a search. A tile opens its page; "All
 * settings" goes back. Personal settings (theme, accent color, language, taskbar, wallpaper) live in the user menu,
 * and storage has its own app.
 */
export function SettingsApp({ app }: SdkAppProps) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [current, setCurrent] = useState<PageId | null>(() => pageOf(app.context.launch.args));
  const [query, setQuery] = useState('');
  const page = PAGES.find((entry) => entry.id === current);

  // The desktop forwards changes of the shares and the pools (used space); this syncs every cached answer with them.
  useEffect(() => {
    const refresh = () => {
      void queryClient.invalidateQueries({ queryKey: sharesKey });
    };
    const stops = [
      onInternal(app, SHARES_CHANGED, refresh),
      onInternal(app, STORAGE_CHANGED, refresh),
    ];
    return () => {
      stops.forEach((stop) => {
        stop();
      });
    };
  }, [app, queryClient]);
  // Opening Settings again may name the page to show.
  useEffect(
    () =>
      app.on('launch', (launch) => {
        const next = pageOf(launch.args);
        if (next !== null) {
          setCurrent(next);
        }
      }),
    [app],
  );

  if (page !== undefined) {
    const { Component } = page;
    return (
      <div className={styles.app}>
        <nav className={styles.crumbs} aria-label={t('overview.navigation')}>
          <button
            type="button"
            className={styles.back}
            onClick={() => {
              setCurrent(null);
            }}
          >
            <IconArrowLeft size={16} stroke={1.75} aria-hidden />
            {t('overview.all')}
          </button>
        </nav>
        {/* Focusable, so keyboard users can scroll a page without controls. */}
        <main className={styles.page} tabIndex={0}>
          <Component app={app} />
        </main>
      </div>
    );
  }

  const needle = query.trim().toLocaleLowerCase();
  const matches = PAGES.filter(
    (entry) =>
      needle === '' ||
      [
        t(`pages.${entry.id}.title`),
        t(`pages.${entry.id}.description`),
        t(`pages.${entry.id}.keywords`),
      ]
        .join(' ')
        .toLocaleLowerCase()
        .includes(needle),
  );

  return (
    <div className={styles.app}>
      <main className={styles.overview}>
        <header className={styles.overviewHeader}>
          <h2 className={styles.pageTitle}>{t('overview.title')}</h2>
          <SearchField
            label={t('overview.search')}
            placeholder={t('overview.search')}
            className={styles.search}
            value={query}
            onChange={setQuery}
            onSubmit={() => {
              const first = matches[0];
              if (first !== undefined) {
                setCurrent(first.id);
              }
            }}
          />
        </header>
        {matches.length === 0 && <p className={styles.muted}>{t('overview.noMatch')}</p>}
        {CATEGORIES.map((category) => {
          const tiles = matches.filter((entry) => entry.category === category);
          if (tiles.length === 0) {
            return null;
          }
          return (
            <section
              key={category}
              className={styles.category}
              aria-labelledby={`settings-${category}`}
            >
              <h3 id={`settings-${category}`} className={styles.categoryTitle}>
                {t(`categories.${category}`)}
              </h3>
              <ul className={styles.tiles}>
                {tiles.map((entry) => {
                  const Icon = entry.icon;
                  return (
                    <li key={entry.id}>
                      <button
                        type="button"
                        className={styles.tile}
                        onClick={() => {
                          setCurrent(entry.id);
                        }}
                      >
                        <span className={styles.tileIcon} aria-hidden>
                          <Icon size={24} stroke={1.75} />
                        </span>
                        <span className={styles.tileText}>
                          <span className={styles.tileTitle}>{t(`pages.${entry.id}.title`)}</span>
                          <span className={styles.tileDescription}>
                            {t(`pages.${entry.id}.description`)}
                          </span>
                        </span>
                      </button>
                    </li>
                  );
                })}
              </ul>
            </section>
          );
        })}
      </main>
    </div>
  );
}
