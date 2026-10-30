import type { PithyxApp } from '@pithyx/sdk';
import { Button, StatusBadge } from '@pithyx/ui';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { AppIcon } from '../../shell/AppIcon';
import { IconInfoCircle } from '../../ui/icons';
import { manifestText } from '../installed';
import type { AdminAppList, CatalogInfo, StoreAppSummary } from '../sdk/store';
import { describeProblem } from '../storage/shared';
import { CardGrid, CatalogAppCard } from './AppCards';
import { InstallDialog } from './InstallDialog';
import { discover, inCategory } from './model';
import { catalogName, useCatalogApps } from './storeState';
import styles from './Store.module.css';

/** Discover, All apps, or one category of the catalogs (D389). */
export type CatalogPlace = 'discover' | 'all' | { category: string };

/**
 * The apps of the enabled catalogs. Discover features the newest app the box does not have and lists the newest apps
 * below it (D404); All apps and a category list every match. A search over every catalog replaces Discover's sections
 * with its results and narrows the others. A catalog that was never fetched or whose last fetch failed says so once at
 * the top; its last verified copy is still listed.
 */
export function CatalogBrowser({
  app,
  place,
  query,
  catalogs,
  installed,
  onOpen,
  onShowCatalogs,
}: {
  app: PithyxApp;
  place: CatalogPlace;
  /** The search, trimmed; empty for none. */
  query: string;
  catalogs: readonly CatalogInfo[];
  installed: AdminAppList | undefined;
  onOpen: (entry: StoreAppSummary) => void;
  onShowCatalogs: () => void;
}) {
  const { t, i18n } = useTranslation('store');
  const search = useCatalogApps(app, query);
  const found = search.data?.apps ?? [];
  const category = typeof place === 'object' ? place.category : null;
  const apps = inCategory(found, category);
  const enabled = catalogs.filter((catalog) => catalog.enabled);
  const nameOf = (id: string) => {
    const catalog = catalogs.find((candidate) => candidate.id === id);
    return catalog === undefined ? id : catalogName(catalog, i18n.language);
  };

  const grid = (label: string, entries: readonly StoreAppSummary[]) => (
    <CardGrid label={label}>
      {entries.map((entry) => (
        <CatalogAppCard
          key={`${entry.catalog}/${entry.id}`}
          app={app}
          entry={entry}
          catalogs={catalogs}
          installed={installed}
          onOpen={() => {
            onOpen(entry);
          }}
        />
      ))}
    </CardGrid>
  );

  let body;
  if (search.isPending) {
    body = (
      <p className={styles.muted} role="status">
        {t('catalog.loading')}
      </p>
    );
  } else if (search.isError) {
    body = <ErrorNotice error={describeProblem(search.error)} />;
  } else if (enabled.length === 0) {
    body = (
      <div className={styles.empty}>
        <p>{t('discover.noCatalogs')}</p>
        <Button onPress={onShowCatalogs}>{t('discover.showCatalogs')}</Button>
      </div>
    );
  } else if (apps.length === 0) {
    body = (
      <p className={styles.empty}>
        {query !== ''
          ? t('catalog.noMatch', { query })
          : category !== null
            ? t('catalog.noCategoryMatch')
            : t('catalog.empty')}
      </p>
    );
  } else if (place === 'discover' && query === '') {
    const { featured, recent } = discover(apps);
    body = (
      <>
        {featured !== null && (
          <Featured
            app={app}
            entry={featured}
            label={
              featured.kind === 'custom'
                ? t('discover.featured.custom', { name: nameOf(featured.catalog) })
                : t(`discover.featured.${featured.kind}`)
            }
            onOpen={() => {
              onOpen(featured);
            }}
          />
        )}
        {recent.length > 0 && (
          <section className={styles.section} aria-labelledby="store-recent">
            <h2 id="store-recent" className={styles.sectionHeading}>
              {t('discover.recent')}
            </h2>
            {grid(t('discover.recent'), recent)}
          </section>
        )}
      </>
    );
  } else {
    const label = query === '' ? t('catalog.apps') : t('search.results', { query });
    body = (
      <>
        {query !== '' && <h2 className={styles.sectionHeading}>{label}</h2>}
        {grid(label, apps)}
      </>
    );
  }

  return (
    <>
      {enabled
        .filter((catalog) => catalog.status !== 'ok')
        .map((catalog) => (
          <p key={catalog.key} className={styles.infoBar} role="note">
            <IconInfoCircle size={18} stroke={1.75} aria-hidden />
            {catalog.status === 'failed'
              ? t('catalog.failedNotice', { name: catalogName(catalog, i18n.language) })
              : t('catalog.newNotice', { name: catalogName(catalog, i18n.language) })}
          </p>
        ))}
      {body}
    </>
  );
}

/** The app Discover features: a wide card with a large icon, its description and its one action. */
function Featured({
  app,
  entry,
  label,
  onOpen,
}: {
  app: PithyxApp;
  entry: StoreAppSummary;
  label: string;
  onOpen: () => void;
}) {
  const { t, i18n } = useTranslation('store');
  const [installing, setInstalling] = useState(false);
  const name = manifestText(entry.name, i18n.language);
  return (
    <article className={styles.featured} aria-labelledby="store-featured">
      <AppIcon app={{ icon: entry.icon, tone: 'graphite' }} size={72} />
      <div className={styles.featuredBody}>
        <span className={styles.featuredLabel}>{label}</span>
        <h2 className={styles.featuredName}>
          <button id="store-featured" type="button" className={styles.cardLink} onClick={onOpen}>
            {name}
          </button>
        </h2>
        {entry.description !== null && (
          <p className={styles.featuredDescription}>
            {manifestText(entry.description, i18n.language)}
          </p>
        )}
        {entry.critical && (
          <span className={styles.cardActions}>
            <StatusBadge tone="danger">{t('elevated.rootBadge')}</StatusBadge>
          </span>
        )}
      </div>
      <Button
        className={styles.featuredAction}
        variant="primary"
        onPress={() => {
          setInstalling(true);
        }}
      >
        {t('catalog.install')}
      </Button>
      {installing && (
        <InstallDialog
          app={app}
          catalog={{
            catalog: entry.catalog,
            appId: entry.id,
            version: entry.version,
            verified: entry.kind !== 'custom',
          }}
          onClose={() => {
            setInstalling(false);
          }}
        />
      )}
    </article>
  );
}
