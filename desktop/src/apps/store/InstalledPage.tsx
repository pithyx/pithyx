import type { PithyxApp } from '@pithyx/sdk';
import { Button, Toolbar } from '@pithyx/ui';
import type { UseQueryResult } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { IconUpload } from '../../ui/icons';
import type { AdminAppList, AppUpdate, CatalogInfo } from '../sdk/store';
import { describeProblem } from '../storage/shared';
import { CardGrid, InstalledAppCard } from './AppCards';
import { InstallDialog } from './InstallDialog';
import styles from './Store.module.css';

/**
 * The installed apps as cards with their state, and installing an app from its package file (.pithyxapp), which is
 * not verified. Everyone finds the apps they may use in their Pithyx menu.
 */
export function InstalledPage({
  app,
  installed,
  updates,
  catalogs,
  onOpen,
}: {
  app: PithyxApp;
  installed: UseQueryResult<AdminAppList>;
  updates: readonly AppUpdate[];
  catalogs: readonly CatalogInfo[];
  onOpen: (appId: string) => void;
}) {
  const { t } = useTranslation('store');
  const [installing, setInstalling] = useState(false);
  const apps = installed.data?.apps ?? [];
  return (
    <>
      <Toolbar aria-label={t('installed.toolbar')}>
        <Button
          variant="primary"
          onPress={() => {
            setInstalling(true);
          }}
        >
          <IconUpload size={18} stroke={1.75} aria-hidden />
          {t('installed.installFromFile')}
        </Button>
      </Toolbar>
      <p className={styles.muted}>
        {t('installed.intro')}
        {installed.data?.pool != null && ` ${t('installed.pool', { pool: installed.data.pool })}`}
      </p>
      {installed.isPending && (
        <p className={styles.muted} role="status">
          {t('installed.loading')}
        </p>
      )}
      {installed.isError && <ErrorNotice error={describeProblem(installed.error)} />}
      {installed.data !== undefined && apps.length === 0 && (
        <p className={styles.empty}>{t('installed.empty')}</p>
      )}
      {apps.length > 0 && (
        <CardGrid label={t('nav.installed')}>
          {apps.map((details) => (
            <InstalledAppCard
              key={details.id}
              app={app}
              details={details}
              update={updates.find((update) => update.id === details.id)}
              catalogs={catalogs}
              onOpen={() => {
                onOpen(details.id);
              }}
            />
          ))}
        </CardGrid>
      )}
      {installing && (
        <InstallDialog
          app={app}
          onClose={() => {
            setInstalling(false);
          }}
        />
      )}
    </>
  );
}
