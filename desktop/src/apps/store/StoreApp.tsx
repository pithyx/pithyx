import { Badge, Button, IconButton } from '@pithyx/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { AppIcon } from '../../shell/AppIcon';
import { IconSettings, IconUpload } from '../../ui/icons';
import { manifestText } from '../installed';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import {
  APPS_CHANGED,
  onStore,
  storeCall,
  type AdminAppList,
  type AppDetails,
  type StoreAppSummary,
} from '../sdk/store';
import { describeProblem } from '../storage/shared';
import { AppPage } from './AppPage';
import { CatalogAppPage } from './CatalogAppPage';
import { CatalogBrowser } from './CatalogBrowser';
import { StoreSettings } from './StoreSettings';
import { catalogsOf, useStoreState, type CatalogTab } from './storeState';
import { UpdatesTab } from './UpdatesTab';
import { InstallDialog } from './InstallDialog';
import { UninstallDialog } from './UninstallDialog';
import { iconUrl, manifestFacts } from './model';
import styles from './Store.module.css';

type Tab = 'installed' | 'updates' | CatalogTab;

type View =
  | { page: 'tabs' }
  | { page: 'installed'; appId: string }
  | { page: 'catalog'; entry: StoreAppSummary; tab: Tab }
  | { page: 'settings' };

export const appsKey = ['store', 'apps'] as const;

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
 * The Store (D308, D329): the installed apps, their updates, the apps of the catalogs in the tabs Official, Community
 * and Own, and the settings behind the gear. Installing and updating from a file or a catalog, and for each installed
 * app its permissions, folders, users, logging, update policy, rollback and removal. Administrators only (D320).
 */
export function StoreApp({ app }: SdkAppProps) {
  const { t } = useTranslation('store');
  const [tab, setTab] = useState<Tab>('installed');
  const [view, setView] = useState<View>({ page: 'tabs' });
  const [installing, setInstalling] = useState(false);
  const [uninstalling, setUninstalling] = useState<{ appId: string; name: string } | null>(null);
  const installed = useInstalled(app);
  const store = useStoreState(app);
  const catalogs = store.data?.catalogs ?? [];
  const updates = store.data?.updates ?? [];
  const visible: Tab[] = [
    'installed',
    ...(updates.length > 0 ? (['updates'] as const) : []),
    ...(catalogsOf(catalogs, 'official').length > 0 ? (['official'] as const) : []),
    ...(catalogsOf(catalogs, 'community').length > 0 ? (['community'] as const) : []),
    ...(catalogs.some((catalog) => catalog.kind === 'custom') ? (['own'] as const) : []),
  ];
  // A tab that went away (the last update installed, a catalog switched off) falls back to the installed apps.
  const shown: Tab = visible.includes(tab) ? tab : 'installed';
  const current =
    view.page === 'installed'
      ? installed.data?.apps.find((entry) => entry.id === view.appId)
      : undefined;
  // An app removed meanwhile (here or elsewhere) leaves its page.
  if (view.page === 'installed' && installed.data !== undefined && current === undefined) {
    setView({ page: 'tabs' });
  }

  const uninstall =
    uninstalling === null ? null : (
      <UninstallDialog
        app={app}
        appId={uninstalling.appId}
        name={uninstalling.name}
        onClose={() => {
          setUninstalling(null);
        }}
      />
    );
  const back = () => {
    setView({ page: 'tabs' });
  };

  if (view.page === 'settings' && store.data !== undefined) {
    return <StoreSettings app={app} state={store.data} onBack={back} />;
  }
  if (view.page === 'catalog') {
    return (
      <CatalogAppPage
        app={app}
        entry={view.entry}
        backLabel={t(`tabs.${view.tab}`)}
        onBack={back}
      />
    );
  }
  // Both views put the uninstall dialog second in a fragment, so it keeps its job when the removed app's page goes.
  if (current !== undefined) {
    return (
      <>
        <AppPage
          app={app}
          details={current}
          update={updates.find((update) => update.id === current.id)}
          catalogs={catalogs}
          onBack={back}
          onUninstall={(name) => {
            setUninstalling({ appId: current.id, name });
          }}
        />
        {uninstall}
      </>
    );
  }

  const openCatalogApp = (entry: StoreAppSummary) => {
    setView({ page: 'catalog', entry, tab: shown });
  };

  return (
    <>
      <div className={styles.app}>
        <div className={styles.tabBar}>
          <div className={styles.tabs} role="tablist" aria-label={t('tabs.label')}>
            {visible.map((entry) => (
              <button
                key={entry}
                type="button"
                role="tab"
                className={styles.tab}
                aria-selected={shown === entry}
                onClick={() => {
                  setTab(entry);
                }}
              >
                {entry === 'updates'
                  ? t('tabs.updatesCount', { count: updates.length })
                  : t(`tabs.${entry}`)}
              </button>
            ))}
          </div>
          <IconButton
            label={t('settings.open')}
            isDisabled={store.data === undefined}
            onPress={() => {
              setView({ page: 'settings' });
            }}
          >
            <IconSettings size={18} stroke={1.75} aria-hidden />
          </IconButton>
        </div>
        {store.isError && <ErrorNotice error={describeProblem(store.error)} />}
        {shown === 'updates' && (
          <UpdatesTab app={app} updates={updates} installed={installed.data} catalogs={catalogs} />
        )}
        {(shown === 'official' || shown === 'community' || shown === 'own') && (
          <CatalogBrowser
            key={shown}
            app={app}
            title={t(`catalog.${shown}.title`)}
            intro={t(`catalog.${shown}.intro`)}
            catalogs={catalogsOf(catalogs, shown)}
            onOpen={openCatalogApp}
          />
        )}
        {shown === 'installed' && (
          <section className={styles.page} aria-labelledby="store-installed">
            <header className={styles.pageHeader}>
              <div className={styles.headerText}>
                <h2 id="store-installed" className={styles.pageTitle}>
                  {t('installed.title')}
                </h2>
                <p className={styles.muted}>{t('installed.intro')}</p>
                {installed.data?.pool != null && (
                  <p className={styles.muted}>
                    {t('installed.pool', { pool: installed.data.pool })}
                  </p>
                )}
              </div>
              <Button
                variant="primary"
                onPress={() => {
                  setInstalling(true);
                }}
              >
                <IconUpload size={16} stroke={1.75} aria-hidden />
                {t('installed.installFromFile')}
              </Button>
            </header>
            {installed.isPending && (
              <p className={styles.loading} role="status">
                {t('installed.loading')}
              </p>
            )}
            {installed.isError && <ErrorNotice error={describeProblem(installed.error)} />}
            {installed.data?.apps.length === 0 && (
              <p className={styles.empty}>{t('installed.empty')}</p>
            )}
            {installed.data !== undefined && installed.data.apps.length > 0 && (
              <ul className={styles.list}>
                {installed.data.apps.map((entry) => (
                  <li key={entry.id}>
                    <InstalledTile
                      details={entry}
                      update={updates.some((update) => update.id === entry.id)}
                      onOpen={() => {
                        setView({ page: 'installed', appId: entry.id });
                      }}
                    />
                  </li>
                ))}
              </ul>
            )}
          </section>
        )}
        {installing && (
          <InstallDialog
            app={app}
            onClose={() => {
              setInstalling(false);
            }}
          />
        )}
      </div>
      {uninstall}
    </>
  );
}

function InstalledTile({
  details,
  update,
  onOpen,
}: {
  details: AppDetails;
  update: boolean;
  onOpen: () => void;
}) {
  const { t, i18n } = useTranslation('store');
  const facts = manifestFacts(details.manifest);
  const name = manifestText(facts.name, i18n.language);
  const description =
    facts.description === null ? null : manifestText(facts.description, i18n.language);
  return (
    <button type="button" className={styles.tile} onClick={onOpen}>
      <AppIcon app={{ icon: iconUrl(details), tone: 'graphite' }} size={44} />
      <span className={styles.tileText}>
        <span className={styles.tileTitle}>{name}</span>
        <span className={styles.tileMeta}>
          {t('installed.version', { version: details.version })}
          {facts.author !== null && ` · ${facts.author}`}
        </span>
        {description !== null && description !== '' && (
          <span className={styles.tileDescription}>{description}</span>
        )}
        <span className={styles.badges}>
          {details.source === 'sideload' && (
            <Badge tone="neutral">{t('installed.unverified')}</Badge>
          )}
          {update && <Badge tone="highlight">{t('catalog.updateAvailable')}</Badge>}
          {!details.enabled && <Badge tone="neutral">{t('installed.disabled')}</Badge>}
          {details.busy && <Badge tone="accent">{t('installed.busy')}</Badge>}
          {!details.available && <Badge tone="danger">{t('installed.unavailable')}</Badge>}
        </span>
      </span>
    </button>
  );
}
