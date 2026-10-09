import type { PithyxApp } from '@pithyx/sdk';
import { Badge, Button } from '@pithyx/ui';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AppIcon } from '../../shell/AppIcon';
import { manifestText } from '../installed';
import type { AdminAppList, AppUpdate, CatalogInfo } from '../sdk/store';
import { InstallDialog } from './InstallDialog';
import { iconUrl, manifestFacts } from './model';
import styles from './Store.module.css';

/**
 * Newer versions of installed apps that the catalogs offer: what each asks for anew and what the box will do with it
 * (installs it on its own, or waits for an administrator), and updating one through the install dialog.
 */
export function UpdatesTab({
  app,
  updates,
  installed,
  catalogs,
}: {
  app: PithyxApp;
  updates: readonly AppUpdate[];
  installed: AdminAppList | undefined;
  catalogs: readonly CatalogInfo[];
}) {
  const { t, i18n } = useTranslation('store');
  const [updating, setUpdating] = useState<{ update: AppUpdate; name: string } | null>(null);
  return (
    <section className={styles.page} aria-labelledby="store-updates">
      <header className={styles.pageHeader}>
        <div className={styles.headerText}>
          <h2 id="store-updates" className={styles.pageTitle}>
            {t('updates.title')}
          </h2>
          <p className={styles.muted}>{t('updates.intro')}</p>
        </div>
      </header>
      <ul className={styles.updates}>
        {updates.map((update) => {
          const details = installed?.apps.find((entry) => entry.id === update.id);
          const facts = details === undefined ? null : manifestFacts(details.manifest);
          const name = facts === null ? update.id : manifestText(facts.name, i18n.language);
          return (
            <li key={update.id} className={styles.update}>
              <AppIcon
                app={{ icon: details === undefined ? null : iconUrl(details), tone: 'graphite' }}
                size={44}
              />
              <span className={styles.tileText}>
                <span className={styles.tileTitle}>{name}</span>
                <span className={styles.tileMeta}>
                  {t('updates.versions', { from: update.installedVersion, to: update.version })}
                </span>
                <span className={styles.badges}>
                  {update.addedPermissions.length > 0 && (
                    <Badge tone="highlight">
                      {t('updates.newPermissions', { count: update.addedPermissions.length })}
                    </Badge>
                  )}
                  {update.elevated && <Badge tone="danger">{t('updates.elevated')}</Badge>}
                  <Badge tone="neutral">
                    {update.automatic
                      ? t('updates.automatic')
                      : t(`updates.policy.${update.policy ?? 'notify'}`)}
                  </Badge>
                </span>
              </span>
              <Button
                variant="primary"
                isDisabled={details?.busy === true}
                onPress={() => {
                  setUpdating({ update, name });
                }}
              >
                {t('updates.update')}
              </Button>
            </li>
          );
        })}
      </ul>
      {updating !== null && (
        <InstallDialog
          app={app}
          updating={updating.name}
          catalog={{
            catalog: updating.update.catalog,
            appId: updating.update.id,
            version: updating.update.version,
            verified: catalogs.some(
              (catalog) => catalog.id === updating.update.catalog && catalog.verified,
            ),
          }}
          onClose={() => {
            setUpdating(null);
          }}
        />
      )}
    </section>
  );
}
