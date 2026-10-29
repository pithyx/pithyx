import { useQueryClient } from '@tanstack/react-query';
import { AppFrame, NavList, type NavGroup } from '@pithyx/ui';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { IconHome } from '../../ui/icons';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { onInternal, SHARES_CHANGED, STORAGE_CHANGED } from '../sdk/internal';
import { OverviewPage } from './OverviewPage';
import { SETTINGS_PAGES, type SettingsPageId } from './pages';
import styles from './Settings.module.css';
import { sharesKey } from './shares';

type Place = SettingsPageId | 'overview';

/** The page named by launch arguments such as `{ page: 'users' }`, if any. */
function pageOf(args: unknown): SettingsPageId | null {
  const page = (args as { page?: unknown } | null)?.page;
  return SETTINGS_PAGES.find((entry) => entry.id === page)?.id ?? null;
}

/**
 * Settings (D394): one frame with the grouped navigation on the left (Overview; System; Access) and the page on the
 * right. The overview shows the box, what needs attention and every area as a tile. Personal settings and Storage are
 * apps of their own.
 */
export function SettingsApp({ app }: SdkAppProps) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [current, setCurrent] = useState<Place>(
    () => pageOf(app.context.launch.args) ?? 'overview',
  );
  const [view, setView] = useState<'navigation' | 'page'>(() =>
    pageOf(app.context.launch.args) === null ? 'navigation' : 'page',
  );
  const page = SETTINGS_PAGES.find((entry) => entry.id === current);

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
          setView('page');
        }
      }),
    [app],
  );

  const open = (next: Place) => {
    setCurrent(next);
    setView('page');
  };
  const item = (id: SettingsPageId) => {
    const entry = SETTINGS_PAGES.find((candidate) => candidate.id === id);
    const Icon = entry?.icon ?? IconHome;
    return {
      id,
      label: t(`pages.${id}.title`),
      icon: <Icon size={18} stroke={1.75} aria-hidden />,
      keywords: [t(`pages.${id}.description`), t(`pages.${id}.keywords`)],
    };
  };
  const groups: NavGroup[] = [
    {
      id: 'start',
      items: [
        {
          id: 'overview',
          label: t('overview.page'),
          icon: <IconHome size={18} stroke={1.75} aria-hidden />,
        },
      ],
    },
    ...(['system', 'access'] as const).map((group) => ({
      id: group,
      label: t(`groups.${group}`),
      items: SETTINGS_PAGES.filter((entry) => entry.group === group).map((entry) => item(entry.id)),
    })),
  ];

  return (
    <AppFrame
      className={styles.frame}
      view={view}
      onViewChange={setView}
      navigation={
        <NavList
          aria-label={t('overview.navigation')}
          searchPlaceholder={t('overview.search')}
          groups={groups}
          selectedId={current}
          onSelect={(id) => {
            open(id as Place);
          }}
        />
      }
      {...(page === undefined ? { title: t('overview.page') } : {})}
    >
      {page === undefined ? (
        <OverviewPage app={app} pages={SETTINGS_PAGES} onOpen={open} />
      ) : (
        <page.Component app={app} />
      )}
    </AppFrame>
  );
}
