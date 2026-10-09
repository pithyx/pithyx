import type { PithyxApp } from '@pithyx/sdk';
import { Badge, SearchField } from '@pithyx/ui';
import { useQuery } from '@tanstack/react-query';
import { useDeferredValue, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { AppIcon } from '../../shell/AppIcon';
import { manifestText } from '../installed';
import { storeCall, type CatalogInfo, type StoreAppSummary } from '../sdk/store';
import { describeProblem } from '../storage/shared';
import { catalogName } from './storeState';
import styles from './Store.module.css';

/**
 * The apps of one tab's catalogs as a grid with a search over id, name, description and author. A catalog that was
 * never fetched or whose last fetch failed says so above the grid; its last verified copy is still listed.
 */
export function CatalogBrowser({
  app,
  title,
  intro,
  catalogs,
  onOpen,
}: {
  app: PithyxApp;
  title: string;
  intro: string;
  catalogs: readonly CatalogInfo[];
  onOpen: (entry: StoreAppSummary) => void;
}) {
  const { t, i18n } = useTranslation('store');
  const [query, setQuery] = useState('');
  const deferred = useDeferredValue(query.trim());
  const ids = catalogs.map((catalog) => catalog.id).filter((id): id is string => id !== null);
  const search = useQuery({
    queryKey: ['store', 'search', deferred],
    queryFn: () =>
      storeCall(app, 'internal.admin.store.search', deferred === '' ? {} : { query: deferred }),
  });
  const apps = (search.data?.apps ?? []).filter((entry) => ids.includes(entry.catalog));
  return (
    <section className={styles.page} aria-labelledby="store-catalog-title">
      <header className={styles.pageHeader}>
        <div className={styles.headerText}>
          <h2 id="store-catalog-title" className={styles.pageTitle}>
            {title}
          </h2>
          <p className={styles.muted}>{intro}</p>
        </div>
      </header>
      {catalogs
        .filter((catalog) => catalog.status !== 'ok')
        .map((catalog) => (
          <p key={catalog.key} className={styles.notice}>
            {catalog.status === 'failed'
              ? t('catalog.failedNotice', { name: catalogName(catalog, i18n.language) })
              : t('catalog.newNotice', { name: catalogName(catalog, i18n.language) })}
          </p>
        ))}
      <SearchField
        label={t('catalog.search')}
        placeholder={t('catalog.searchPlaceholder')}
        value={query}
        onChange={setQuery}
      />
      {search.isPending && (
        <p className={styles.loading} role="status">
          {t('catalog.loading')}
        </p>
      )}
      {search.isError && <ErrorNotice error={describeProblem(search.error)} />}
      {search.data !== undefined && apps.length === 0 && (
        <p className={styles.empty}>
          {deferred === '' ? t('catalog.empty') : t('catalog.noMatch', { query: deferred })}
        </p>
      )}
      {apps.length > 0 && (
        <ul className={styles.list} aria-label={t('catalog.apps')}>
          {apps.map((entry) => (
            <li key={`${entry.catalog}/${entry.id}`}>
              <button
                type="button"
                className={styles.tile}
                onClick={() => {
                  onOpen(entry);
                }}
              >
                <AppIcon app={{ icon: entry.icon, tone: 'graphite' }} size={44} />
                <span className={styles.tileText}>
                  <span className={styles.tileTitle}>
                    {manifestText(entry.name, i18n.language)}
                  </span>
                  <span className={styles.tileMeta}>
                    {t('installed.version', { version: entry.version })}
                    {entry.author !== null && ` · ${entry.author}`}
                  </span>
                  {entry.description !== null && (
                    <span className={styles.tileDescription}>
                      {manifestText(entry.description, i18n.language)}
                    </span>
                  )}
                  <span className={styles.badges}>
                    <SourceBadge kind={entry.kind} />
                    {entry.installedVersion !== null && (
                      <Badge tone={entry.update ? 'highlight' : 'neutral'}>
                        {entry.update ? t('catalog.updateAvailable') : t('catalog.installed')}
                      </Badge>
                    )}
                  </span>
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** "Pithyx" for the official catalog, "Community" or "Unverified" for the others. */
export function SourceBadge({ kind }: { kind: StoreAppSummary['kind'] }) {
  const { t } = useTranslation('store');
  return (
    <Badge tone={kind === 'official' ? 'accent' : 'neutral'}>{t(`catalog.badge.${kind}`)}</Badge>
  );
}
