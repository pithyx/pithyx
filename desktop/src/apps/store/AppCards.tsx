import type { PithyxApp } from '@pithyx/sdk';
import { Button, StatusBadge } from '@pithyx/ui';
import { useId, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { AppIcon } from '../../shell/AppIcon';
import { manifestText } from '../installed';
import {
  storeCall,
  type AdminAppList,
  type AppDetails,
  type AppUpdate,
  type CatalogInfo,
  type StoreAppSummary,
} from '../sdk/store';
import { ElevatedBadge } from './ElevatedRights';
import { IncompatibleBadge } from './Incompatible';
import { InstallDialog } from './InstallDialog';
import { iconUrl, manifestFacts } from './model';
import { MaturityBadge } from './StoreInfo';
import { useSourceLabel } from './storeState';
import styles from './Store.module.css';

/**
 * One app as a card (D394): its icon, name, where it comes from, a short description, and the one action its state
 * allows with the state in words. The name opens the app's page, and so does a click anywhere else on the card.
 */
function StoreCard({
  icon,
  name,
  source,
  description,
  action,
  badges,
  onOpen,
}: {
  icon: string | null;
  name: string;
  source: string;
  description: string | null;
  action: ReactNode;
  badges: ReactNode;
  onOpen: () => void;
}) {
  const id = useId();
  return (
    <article className={styles.card} aria-labelledby={id}>
      <AppIcon app={{ icon, tone: 'graphite' }} size={48} />
      <div className={styles.cardBody}>
        <h3 className={styles.cardName}>
          <button id={id} type="button" className={styles.cardLink} onClick={onOpen}>
            {name}
          </button>
        </h3>
        <p className={styles.cardSource}>{source}</p>
        {description !== null && description !== '' && (
          <p className={styles.cardDescription}>{description}</p>
        )}
        <div className={styles.cardActions}>
          {action}
          {badges}
        </div>
      </div>
    </article>
  );
}

/** The cards in a grid of up to three columns, fewer in a narrow window. */
export function CardGrid({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className={styles.cards} role="list" aria-label={label}>
      {children}
    </div>
  );
}

/** Opens an installed app for the administrator, as the menu would. */
function openInstalled(app: PithyxApp, appId: string) {
  void storeCall(app, 'internal.admin.apps.open', { appId });
}

/**
 * A catalog app: **Install** (with Root access when it asks for root on the box, D388), **Update** with the new
 * version, **Open** when installed, or **Not compatible** when this box can install no version (D393). Installing and
 * updating go through the same dialog as on the app's page.
 */
export function CatalogAppCard({
  app,
  entry,
  catalogs,
  installed,
  onOpen,
}: {
  app: PithyxApp;
  entry: StoreAppSummary;
  catalogs: readonly CatalogInfo[];
  installed: AdminAppList | undefined;
  onOpen: () => void;
}) {
  const { t, i18n } = useTranslation('store');
  const sourceLabel = useSourceLabel(catalogs);
  const [installing, setInstalling] = useState(false);
  const name = manifestText(entry.name, i18n.language);
  const details = installed?.apps.find((candidate) => candidate.id === entry.id);
  const isInstalled = entry.installedVersion !== null;
  const busy = details?.busy === true;

  const action =
    entry.incompatible !== null ? null : isInstalled && entry.update ? (
      <>
        <Button
          size="sm"
          variant="primary"
          isDisabled={busy}
          onPress={() => {
            setInstalling(true);
          }}
        >
          {t('card.update')}
        </Button>
        <span className={styles.cardVersion}>{entry.version}</span>
      </>
    ) : isInstalled ? (
      <Button
        size="sm"
        isDisabled={busy || details?.enabled === false || details?.available === false}
        onPress={() => {
          openInstalled(app, entry.id);
        }}
      >
        {t('detail.open')}
      </Button>
    ) : (
      <Button
        size="sm"
        onPress={() => {
          setInstalling(true);
        }}
      >
        {t('catalog.install')}
      </Button>
    );

  return (
    <div role="listitem" className={styles.cardItem}>
      <StoreCard
        icon={entry.icon}
        name={name}
        source={sourceLabel({ catalog: entry.catalog, kind: entry.kind })}
        description={
          entry.description === null ? null : manifestText(entry.description, i18n.language)
        }
        action={action}
        badges={
          <>
            {entry.incompatible !== null && <IncompatibleBadge />}
            {isInstalled && !entry.update && entry.incompatible === null && (
              <StatusBadge tone="success">{t('catalog.installed')}</StatusBadge>
            )}
            {busy && <StatusBadge tone="info">{t('installed.busy')}</StatusBadge>}
            {entry.critical && <StatusBadge tone="danger">{t('elevated.rootBadge')}</StatusBadge>}
            <MaturityBadge maturity={entry.maturity} />
          </>
        }
        onOpen={onOpen}
      />
      {installing && (
        <InstallDialog
          app={app}
          {...(isInstalled ? { updating: name } : {})}
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
    </div>
  );
}

/**
 * An installed app: **Update** with the new version when a catalog offers one, otherwise **Open**, and its state
 * (disabled, in progress, pool missing, special rights) in words.
 */
export function InstalledAppCard({
  app,
  details,
  update,
  catalogs,
  onOpen,
}: {
  app: PithyxApp;
  details: AppDetails;
  update: AppUpdate | undefined;
  catalogs: readonly CatalogInfo[];
  onOpen: () => void;
}) {
  const { t, i18n } = useTranslation('store');
  const sourceLabel = useSourceLabel(catalogs);
  const [updating, setUpdating] = useState(false);
  const facts = manifestFacts(details.manifest);
  const name = manifestText(facts.name, i18n.language);

  const action =
    update !== undefined ? (
      <>
        <Button
          size="sm"
          variant="primary"
          isDisabled={details.busy}
          onPress={() => {
            setUpdating(true);
          }}
        >
          {t('card.update')}
        </Button>
        <span className={styles.cardVersion}>{update.version}</span>
      </>
    ) : (
      <Button
        size="sm"
        isDisabled={details.busy || !details.enabled || !details.available}
        onPress={() => {
          openInstalled(app, details.id);
        }}
      >
        {t('detail.open')}
      </Button>
    );

  return (
    <div role="listitem" className={styles.cardItem}>
      <StoreCard
        icon={iconUrl(details)}
        name={name}
        source={sourceLabel({ catalog: details.source === 'catalog' ? details.catalog : null })}
        description={
          facts.description === null ? null : manifestText(facts.description, i18n.language)
        }
        action={action}
        badges={
          <>
            {update === undefined && (
              <span className={styles.cardVersion}>
                {t('installed.version', { version: details.version })}
              </span>
            )}
            {!details.enabled && <StatusBadge>{t('installed.disabled')}</StatusBadge>}
            {details.busy && <StatusBadge tone="info">{t('installed.busy')}</StatusBadge>}
            {!details.available && (
              <StatusBadge tone="danger">{t('installed.unavailable')}</StatusBadge>
            )}
            <ElevatedBadge details={details} />
          </>
        }
        onOpen={onOpen}
      />
      {updating && update !== undefined && (
        <InstallDialog
          app={app}
          updating={name}
          catalog={{
            catalog: update.catalog,
            appId: update.id,
            version: update.version,
            verified: catalogs.some((catalog) => catalog.id === update.catalog && catalog.verified),
          }}
          onClose={() => {
            setUpdating(false);
          }}
        />
      )}
    </div>
  );
}
