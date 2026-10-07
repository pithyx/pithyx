import { Badge, Button } from '@pithyx/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { AppIcon } from '../../shell/AppIcon';
import { IconUpload } from '../../ui/icons';
import { manifestText } from '../installed';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { APPS_CHANGED, onStore, storeCall, type AdminAppList, type AppDetails } from '../sdk/store';
import { describeProblem } from '../storage/shared';
import { AppPage } from './AppPage';
import { InstallDialog } from './InstallDialog';
import { UninstallDialog } from './UninstallDialog';
import { iconUrl, manifestFacts } from './model';
import styles from './Store.module.css';

type Tab = 'installed' | 'official' | 'community' | 'own';

/**
 * The tabs of the store plan (D308). Official, Community and Own come with the catalogs ("Store (after SDK 1.0)" in
 * TODO.md); they are named here and stay hidden until then. Own appears once a custom catalog or a sideload exists.
 */
const TABS: readonly { id: Tab; visible: boolean }[] = [
  { id: 'installed', visible: true },
  { id: 'official', visible: false },
  { id: 'community', visible: false },
  { id: 'own', visible: false },
];

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
 * The Store (D308): the apps installed on the box, installing and updating them from a package file, and for each app
 * its permissions, folders, who may use it, its logging and its removal. Administrators only (D320).
 */
export function StoreApp({ app }: SdkAppProps) {
  const { t } = useTranslation('store');
  const [tab, setTab] = useState<Tab>('installed');
  const [selected, setSelected] = useState<string | null>(null);
  const [installing, setInstalling] = useState(false);
  const [uninstalling, setUninstalling] = useState<{ appId: string; name: string } | null>(null);
  const installed = useInstalled(app);
  const visible = TABS.filter((entry) => entry.visible);
  const current = installed.data?.apps.find((entry) => entry.id === selected);
  // An app removed meanwhile (here or elsewhere) leaves its page.
  if (selected !== null && installed.data !== undefined && current === undefined) {
    setSelected(null);
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

  if (current !== undefined) {
    return (
      <>
        <AppPage
          app={app}
          details={current}
          onBack={() => {
            setSelected(null);
          }}
          onUninstall={(name) => {
            setUninstalling({ appId: current.id, name });
          }}
        />
        {uninstall}
      </>
    );
  }

  return (
    <div className={styles.app}>
      {visible.length > 1 && (
        <div className={styles.tabs} role="tablist" aria-label={t('tabs.label')}>
          {visible.map((entry) => (
            <button
              key={entry.id}
              type="button"
              role="tab"
              className={styles.tab}
              aria-selected={tab === entry.id}
              onClick={() => {
                setTab(entry.id);
              }}
            >
              {t(`tabs.${entry.id}`)}
            </button>
          ))}
        </div>
      )}
      <section className={styles.page} aria-labelledby="store-installed">
        <header className={styles.pageHeader}>
          <div className={styles.headerText}>
            <h2 id="store-installed" className={styles.pageTitle}>
              {t('installed.title')}
            </h2>
            <p className={styles.muted}>{t('installed.intro')}</p>
            {installed.data?.pool != null && (
              <p className={styles.muted}>{t('installed.pool', { pool: installed.data.pool })}</p>
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
                  onOpen={() => {
                    setSelected(entry.id);
                  }}
                />
              </li>
            ))}
          </ul>
        )}
      </section>
      {uninstall}
      {installing && (
        <InstallDialog
          app={app}
          onClose={() => {
            setInstalling(false);
          }}
        />
      )}
    </div>
  );
}

function InstalledTile({ details, onOpen }: { details: AppDetails; onOpen: () => void }) {
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
          {!details.enabled && <Badge tone="neutral">{t('installed.disabled')}</Badge>}
          {details.busy && <Badge tone="accent">{t('installed.busy')}</Badge>}
          {!details.available && <Badge tone="danger">{t('installed.unavailable')}</Badge>}
        </span>
      </span>
    </button>
  );
}
