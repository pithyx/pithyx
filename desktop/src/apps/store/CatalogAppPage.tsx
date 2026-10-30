import type { PithyxApp } from '@pithyx/sdk';
import { Button, StatusBadge } from '@pithyx/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { AppIcon } from '../../shell/AppIcon';
import { IconArrowLeft, IconDownload, IconExternalLink } from '../../ui/icons';
import { manifestText } from '../installed';
import { storeCall, type StoreAppDetails, type StoreAppSummary } from '../sdk/store';
import { describeProblem, formatDate } from '../storage/shared';
import {
  IncompatibleBadge,
  IncompatibleNotice,
  readsManifest,
  useVersionReason,
} from './Incompatible';
import { InstallDialog } from './InstallDialog';
import { manifestFacts } from './model';
import { ElevatedRightText } from './ElevatedRights';
import { PermissionText } from './PermissionText';
import { MaturityBadge, StoreInfoRows } from './StoreInfo';
import styles from './Store.module.css';

/**
 * A catalog app's page: screenshots, author, license, permissions, the links to its homepage and to its source in the
 * catalog, every version, and installing or updating it through the same dialog as a package file.
 */
export function CatalogAppPage({
  app,
  entry,
  backLabel,
  onBack,
}: {
  app: PithyxApp;
  entry: StoreAppSummary;
  backLabel: string;
  onBack: () => void;
}) {
  const { t, i18n } = useTranslation('store');
  // Whether the dialog updates is fixed when it opens: the page learns of the install before the dialog closes.
  const [installing, setInstalling] = useState<{ update: boolean } | null>(null);
  const details = useCatalogDetails(app, entry.catalog, entry.id);
  const summary = details.data?.app ?? entry;
  const incompatible = summary.incompatible;
  // A manifest of another version than this box reads has no rights or Store information to show.
  const facts =
    details.data === undefined || !readsManifest(incompatible)
      ? null
      : manifestFacts(details.data.manifest);
  const name = manifestText(summary.name, i18n.language);
  const description =
    summary.description === null ? null : manifestText(summary.description, i18n.language);
  const installed = summary.installedVersion !== null;

  return (
    <>
      <Button className={styles.back} variant="quiet" size="sm" onPress={onBack}>
        <IconArrowLeft size={16} stroke={1.75} aria-hidden />
        {backLabel}
      </Button>
      <article className={styles.page} aria-labelledby="store-catalog-app-name">
        <header className={styles.appHeader}>
          <AppIcon app={{ icon: summary.icon, tone: 'graphite' }} size={64} />
          <div className={styles.headerText}>
            <h1 id="store-catalog-app-name" className={styles.appTitle}>
              {name}
            </h1>
            <p className={styles.muted}>
              {t('installed.version', { version: summary.version })}
              {summary.author !== null && ` · ${summary.author}`}
            </p>
            <span className={styles.badges}>
              <StatusBadge tone={summary.kind === 'custom' ? 'warning' : 'info'}>
                {t(`catalog.badge.${summary.kind}`)}
              </StatusBadge>
              <MaturityBadge maturity={summary.maturity} />
              {incompatible !== null && <IncompatibleBadge />}
              {installed && (
                <StatusBadge tone="success">
                  {t('catalog.installedVersion', { version: summary.installedVersion })}
                </StatusBadge>
              )}
            </span>
          </div>
          <div className={styles.buttons}>
            {incompatible === null && (!installed || summary.update) && (
              <Button
                variant="primary"
                onPress={() => {
                  setInstalling({ update: installed });
                }}
              >
                <IconDownload size={16} stroke={1.75} aria-hidden />
                {installed
                  ? t('catalog.update', { version: summary.version })
                  : t('catalog.install')}
              </Button>
            )}
            {installed && (
              <Button
                onPress={() => {
                  void storeCall(app, 'internal.admin.apps.open', { appId: summary.id });
                }}
              >
                <IconExternalLink size={16} stroke={1.75} aria-hidden />
                {t('detail.open')}
              </Button>
            )}
          </div>
        </header>
        {description !== null && description !== '' && <p>{description}</p>}
        {incompatible !== null && <IncompatibleNotice incompatible={incompatible} />}
        {summary.kind === 'custom' && <p className={styles.notice}>{t('catalog.unverified')}</p>}
        {details.isPending && (
          <p className={styles.loading} role="status">
            {t('catalog.loadingApp')}
          </p>
        )}
        {details.isError && <ErrorNotice error={describeProblem(details.error)} />}
        {details.data !== undefined && (
          <>
            <CatalogScreenshots details={details.data} name={name} />
            {facts?.root === true && <p className={styles.warning}>{t('install.root')}</p>}
            {facts !== null && facts.elevated.length > 0 && (
              <section className={styles.group} aria-labelledby="store-catalog-elevated">
                <h2 id="store-catalog-elevated" className={styles.sectionTitle}>
                  {t('elevated.section')}
                </h2>
                <p className={styles.warning}>{t('elevated.warning')}</p>
                <ul className={styles.permissions}>
                  {facts.elevated.map((right) => (
                    <li key={right} className={styles.permission}>
                      <ElevatedRightText right={right} reason={facts.reasons[right]} />
                    </li>
                  ))}
                </ul>
              </section>
            )}
            {facts !== null && (
              <section className={styles.group} aria-labelledby="store-catalog-permissions">
                <h2 id="store-catalog-permissions" className={styles.sectionTitle}>
                  {t('detail.permissions.title')}
                </h2>
                <p className={styles.muted}>
                  {facts.backend ? t('install.backend.yes') : t('install.backend.no')}
                </p>
                {facts.permissions.length === 0 ? (
                  <p className={styles.muted}>{t('install.noPermissions')}</p>
                ) : (
                  <ul className={styles.permissions}>
                    {facts.permissions.map((permission) => (
                      <li key={permission} className={styles.permission}>
                        <PermissionText
                          permission={permission}
                          reason={facts.reasons[permission]}
                          hosts={facts.hosts}
                        />
                      </li>
                    ))}
                  </ul>
                )}
              </section>
            )}
            <section className={styles.group} aria-labelledby="store-catalog-facts">
              <h2 id="store-catalog-facts" className={styles.sectionTitle}>
                {t('detail.facts.title')}
              </h2>
              <dl className={styles.facts}>
                <dt>{t('detail.facts.author')}</dt>
                <dd>{summary.author ?? '-'}</dd>
                <dt>{t('detail.facts.license')}</dt>
                <dd>{summary.license}</dd>
                {facts !== null && <StoreInfoRows facts={facts} />}
                <dt>{t('catalog.catalog')}</dt>
                <dd className={styles.breakable}>{summary.catalog}</dd>
                {details.data.homepage !== null && (
                  <>
                    <dt>{t('detail.facts.homepage')}</dt>
                    <dd className={styles.breakable}>
                      <a href={details.data.homepage} target="_blank" rel="noreferrer">
                        {details.data.homepage}
                      </a>
                    </dd>
                  </>
                )}
                <dt>{t('catalog.source')}</dt>
                <dd className={styles.breakable}>
                  <a href={details.data.source} target="_blank" rel="noreferrer">
                    {t('catalog.sourceLink')}
                  </a>
                </dd>
              </dl>
            </section>
            <CatalogVersions details={details.data} />
          </>
        )}
      </article>
      {installing !== null && (
        <InstallDialog
          app={app}
          {...(installing.update ? { updating: name } : {})}
          catalog={{
            catalog: summary.catalog,
            appId: summary.id,
            version: summary.version,
            verified: summary.kind !== 'custom',
          }}
          onClose={() => {
            setInstalling(null);
          }}
        />
      )}
    </>
  );
}

/** The details query of a catalog app, shared by its catalog page and the page of the installed app. */
export function useCatalogDetails(app: PithyxApp, catalog: string | null, appId: string) {
  return useQuery({
    queryKey: ['store', 'details', catalog, appId],
    queryFn: () =>
      storeCall(app, 'internal.admin.store.details', { catalog: catalog ?? '', appId }),
    enabled: catalog !== null,
  });
}

/** The screenshots a catalog shows for an app. */
export function CatalogScreenshots({ details, name }: { details: StoreAppDetails; name: string }) {
  const { t } = useTranslation('store');
  if (details.screenshots.length === 0) {
    return null;
  }
  return (
    <section className={styles.group} aria-labelledby="store-screenshots">
      <h2 id="store-screenshots" className={styles.sectionTitle}>
        {t('catalog.screenshots')}
      </h2>
      <ul className={styles.screenshots}>
        {details.screenshots.map((url, index) => (
          <li key={url}>
            <img
              className={styles.screenshot}
              src={url}
              alt={t('catalog.screenshot', { name, number: index + 1 })}
              loading="lazy"
            />
          </li>
        ))}
      </ul>
    </section>
  );
}

/** Every version a catalog published of an app, with the ones this box cannot run marked. */
export function CatalogVersions({ details }: { details: StoreAppDetails }) {
  const { t } = useTranslation('store');
  const locale = useFormatLocale();
  const reason = useVersionReason();
  return (
    <section className={styles.group} aria-labelledby="store-catalog-versions">
      <h2 id="store-catalog-versions" className={styles.sectionTitle}>
        {t('catalog.versions')}
      </h2>
      <ul className={styles.permissions}>
        {details.versions.map((version) => (
          <li key={version.version} className={styles.permission}>
            <span className={styles.permissionText}>
              <span className={styles.permissionTitle}>{version.version}</span>
              <span className={styles.muted}>
                {formatDate(version.publishedAt, locale)}
                {version.incompatible !== null && ` · ${reason(version.incompatible)}`}
              </span>
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}
