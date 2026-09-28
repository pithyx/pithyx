import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { onInternal, STORAGE_CHANGED } from '../sdk/internal';
import { DatasetsSection } from './DatasetsSection';
import { DisksSection } from './DisksSection';
import { MaintenanceSection } from './MaintenanceSection';
import { PoolsSection } from './PoolsSection';
import { storageKey } from './shared';
import { SnapshotsSection } from './SnapshotsSection';
import styles from './Storage.module.css';

const sections = ['pools', 'disks', 'datasets', 'snapshots', 'maintenance'] as const;
type Section = (typeof sections)[number];

/** Storage: pools, disks, datasets and volumes, snapshots, health and maintenance. Administrators only. */
export function StorageApp({ app }: SdkAppProps) {
  const { t } = useTranslation('storage');
  const queryClient = useQueryClient();
  const [section, setSection] = useState<Section>('pools');

  // The desktop forwards changes of the storage topics; this syncs every cached answer with them.
  useEffect(
    () =>
      onInternal(app, STORAGE_CHANGED, () => {
        void queryClient.invalidateQueries({ queryKey: storageKey });
      }),
    [app, queryClient],
  );

  return (
    <div className={styles.app}>
      <nav className={styles.nav} aria-label={t('sections')}>
        {sections.map((entry) => (
          <button
            key={entry}
            type="button"
            className={styles.navItem}
            aria-current={entry === section ? 'page' : undefined}
            onClick={() => {
              setSection(entry);
            }}
          >
            {t(`nav.${entry}`)}
          </button>
        ))}
      </nav>
      {/* Focusable, so keyboard users can scroll a section without controls, such as the disk table. */}
      <main className={styles.page} tabIndex={0}>
        {section === 'pools' && <PoolsSection app={app} />}
        {section === 'disks' && <DisksSection app={app} />}
        {section === 'datasets' && <DatasetsSection app={app} />}
        {section === 'snapshots' && <SnapshotsSection app={app} />}
        {section === 'maintenance' && <MaintenanceSection app={app} />}
      </main>
    </div>
  );
}
