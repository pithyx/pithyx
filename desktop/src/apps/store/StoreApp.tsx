import { AppFrame, NavList, SearchField, type NavGroup } from '@pithyx/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useDeferredValue, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { IconLayoutGrid, IconPackage, IconRefresh, IconSettings, IconStar } from '../../ui/icons';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import {
  APPS_CHANGED,
  onStore,
  storeCall,
  type AdminAppList,
  type StoreAppSummary,
} from '../sdk/store';
import { AppPage } from './AppPage';
import { CatalogAppPage } from './CatalogAppPage';
import { CatalogBrowser, type CatalogPlace } from './CatalogBrowser';
import { CatalogsPage } from './CatalogsPage';
import { categoryIcon } from './categories';
import { InstalledPage } from './InstalledPage';
import { CATEGORIES, isCategory } from './model';
import { useCatalogApps, useStoreState } from './storeState';
import { UninstallDialog } from './UninstallDialog';
import { UpdatesPage } from './UpdatesPage';
import styles from './Store.module.css';

/** A place of the navigation; a category is `category:<name>` (D389). */
type Place = 'discover' | 'all' | 'installed' | 'updates' | 'catalogs' | `category:${string}`;

/** An app page opened from a place: an installed app's own page, or a catalog app's. */
type Opened = { kind: 'installed'; appId: string } | { kind: 'catalog'; entry: StoreAppSummary };

export const appsKey = ['store', 'apps'] as const;

const PAGES = ['discover', 'all', 'installed', 'updates', 'catalogs'] as const;

/** The place named by launch arguments such as `{ page: 'updates' }`, if any. */
function placeOf(args: unknown): Place | null {
  const page = (args as { page?: unknown } | null)?.page;
  return PAGES.find((entry) => entry === page) ?? null;
}

/** Every installed app with its state and the app pool; the topic `apps/admin` keeps it current. */
export function useInstalled(app: SdkAppProps['app']) {
  const queryClient = useQueryClient();
  // The desktop forwards the topic to this window: syncing the query cache with the SDK's events.
  useEffect(
    () =>
      onStore(app, APPS_CHANGED, (data) => {
        queryClient.setQueryData<AdminAppList>(appsKey, data as AdminAppList);
      }),
    [app, queryClient],
  );
  return useQuery({
    queryKey: appsKey,
    queryFn: () => storeCall(app, 'internal.admin.apps.list', {}),
  });
}

/**
 * The Store (D308, D329, D394): one frame with Discover, All apps and the categories of the enabled catalogs, the
 * administrator's apps (Installed, Updates) and the catalogs. Apps appear as cards with their state and the one action
 * that fits it; a card opens the app's page with its permissions, folders, users, logging, update policy, rollback and
 * removal. Administrators only (D320).
 */
export function StoreApp({ app }: SdkAppProps) {
  const { t } = useTranslation('store');
  const [place, setPlace] = useState<Place>(() => placeOf(app.context.launch.args) ?? 'discover');
  const [view, setView] = useState<'navigation' | 'page'>(() =>
    placeOf(app.context.launch.args) === null ? 'navigation' : 'page',
  );
  const [opened, setOpened] = useState<Opened | null>(null);
  const [query, setQuery] = useState('');
  const deferred = useDeferredValue(query.trim());
  const [uninstalling, setUninstalling] = useState<{ appId: string; name: string } | null>(null);
  const installed = useInstalled(app);
  const store = useStoreState(app);
  // Every app of the enabled catalogs, for the categories in the navigation and Discover.
  const everything = useCatalogApps(app, '');
  const catalogs = store.data?.catalogs ?? [];
  const updates = store.data?.updates ?? [];

  // Opening the Store again may name the page to show.
  useEffect(
    () =>
      app.on('launch', (launch) => {
        const next = placeOf(launch.args);
        if (next !== null) {
          setPlace(next);
          setOpened(null);
          setView('page');
        }
      }),
    [app],
  );

  const current =
    opened?.kind === 'installed'
      ? installed.data?.apps.find((entry) => entry.id === opened.appId)
      : undefined;
  // An app removed meanwhile (here or elsewhere) leaves its page.
  if (opened?.kind === 'installed' && installed.data !== undefined && current === undefined) {
    setOpened(null);
  }

  // Only the categories some app of the enabled catalogs names are offered (D389).
  const categories = CATEGORIES.filter((name) =>
    (everything.data?.apps ?? []).some((entry) => entry.categories.includes(name)),
  );
  const icon = (Icon: typeof IconStar) => <Icon size={18} stroke={1.75} aria-hidden />;
  const groups: NavGroup[] = [
    {
      id: 'browse',
      items: [
        { id: 'discover', label: t('nav.discover'), icon: icon(IconStar) },
        { id: 'all', label: t('nav.all'), icon: icon(IconLayoutGrid) },
      ],
    },
    ...(categories.length === 0
      ? []
      : [
          {
            id: 'categories',
            label: t('nav.categories'),
            items: categories.map((name) => ({
              id: `category:${name}`,
              label: t(`category.${name}`),
              icon: icon(categoryIcon(name)),
            })),
          },
        ]),
    {
      id: 'yours',
      label: t('nav.yours'),
      items: [
        { id: 'installed', label: t('nav.installed'), icon: icon(IconPackage) },
        {
          id: 'updates',
          label: t('nav.updates'),
          icon: icon(IconRefresh),
          count: updates.length,
          countLabel: t('nav.updatesCount', { count: updates.length }),
        },
      ],
    },
    {
      id: 'manage',
      items: [{ id: 'catalogs', label: t('nav.catalogs'), icon: icon(IconSettings) }],
    },
  ];

  // An installed app has one page, whichever place or search it was opened from.
  const openCatalogApp = (entry: StoreAppSummary) => {
    setOpened(
      installed.data?.apps.some((candidate) => candidate.id === entry.id) === true
        ? { kind: 'installed', appId: entry.id }
        : { kind: 'catalog', entry },
    );
  };
  const back = () => {
    setOpened(null);
  };
  const shownCategory = place.startsWith('category:') ? place.slice('category:'.length) : null;
  const backLabel =
    shownCategory === null
      ? t(`nav.${place as (typeof PAGES)[number]}`)
      : isCategory(shownCategory)
        ? t(`category.${shownCategory}`)
        : shownCategory;
  const catalogPlace: CatalogPlace | null =
    place === 'discover' || place === 'all'
      ? place
      : shownCategory !== null
        ? { category: shownCategory }
        : null;

  const page =
    current !== undefined ? (
      <AppPage
        app={app}
        details={current}
        update={updates.find((update) => update.id === current.id)}
        catalogs={catalogs}
        backLabel={backLabel}
        onBack={back}
        onUninstall={(name) => {
          setUninstalling({ appId: current.id, name });
        }}
      />
    ) : opened?.kind === 'catalog' ? (
      <CatalogAppPage app={app} entry={opened.entry} backLabel={backLabel} onBack={back} />
    ) : catalogPlace !== null ? (
      <CatalogBrowser
        app={app}
        place={catalogPlace}
        query={deferred}
        catalogs={catalogs}
        installed={installed.data}
        onOpen={openCatalogApp}
        onShowCatalogs={() => {
          setPlace('catalogs');
        }}
      />
    ) : place === 'installed' ? (
      <InstalledPage
        app={app}
        installed={installed}
        updates={updates}
        catalogs={catalogs}
        onOpen={(appId) => {
          setOpened({ kind: 'installed', appId });
        }}
      />
    ) : place === 'updates' ? (
      <UpdatesPage app={app} state={store} installed={installed.data} />
    ) : (
      <CatalogsPage app={app} state={store} />
    );

  return (
    <>
      <AppFrame
        view={view}
        onViewChange={setView}
        navigation={
          <NavList
            aria-label={t('nav.label')}
            searchPlaceholder={t('nav.search')}
            groups={groups}
            selectedId={place}
            onSelect={(id) => {
              setPlace(id as Place);
              setOpened(null);
            }}
          />
        }
        // An app page carries its own heading with the app's icon, name and actions.
        {...(opened === null ? { title: backLabel } : {})}
        pageKey={
          opened === null
            ? place
            : `${place}/${opened.kind}/${opened.kind === 'installed' ? opened.appId : opened.entry.id}`
        }
        {...(opened === null && catalogPlace !== null
          ? {
              titleEnd: (
                <SearchField
                  className={styles.search}
                  label={t('search.label')}
                  placeholder={t('search.placeholder')}
                  value={query}
                  onChange={setQuery}
                />
              ),
            }
          : {})}
      >
        {page}
      </AppFrame>
      {/* Outside the page, so the removal keeps its job when the removed app's page goes. */}
      {uninstalling !== null && (
        <UninstallDialog
          app={app}
          appId={uninstalling.appId}
          name={uninstalling.name}
          onClose={() => {
            setUninstalling(null);
          }}
        />
      )}
    </>
  );
}
