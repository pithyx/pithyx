import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { onInternal, STORAGE_CHANGED } from '../sdk/internal';
import { AdvancedSection } from './AdvancedSection';
import { DatasetsSection } from './DatasetsSection';
import { DisksSection } from './DisksSection';
import { MaintenanceSection } from './MaintenanceSection';
import { PoolsSection } from './PoolsSection';
import { storageKey } from './shared';
import styles from './Storage.module.css';

// The sections in the navigation. Datasets open from Advanced; snapshots have no section any more (restore points of
// shared folders live in Settings, D283), but SnapshotsSection stays for a later expert view.
const sections = ['pools', 'disks', 'maintenance', 'advanced'] as const;
export type StorageSection = (typeof sections)[number] | 'datasets';

/** The section named by launch arguments such as `{ section: 'disks' }`, if any. */
function sectionOf(args: unknown): StorageSection | undefined {
  const section = (args as { section?: unknown } | null)?.section;
  return section === 'datasets' ? section : sections.find((entry) => entry === section);
}

/** Storage: pools, disks, health and maintenance, and the advanced tasks. Administrators only. */
export function StorageApp({ app }: SdkAppProps) {
  const { t } = useTranslation('storage');
  const queryClient = useQueryClient();
  const [section, setSection] = useState<StorageSection>(
    () => sectionOf(app.context.launch.args) ?? 'pools',
  );

  // The desktop forwards changes of the storage topics; this syncs every cached answer with them.
  useEffect(
    () =>
      onInternal(app, STORAGE_CHANGED, () => {
        void queryClient.invalidateQueries({ queryKey: storageKey });
      }),
    [app, queryClient],
  );
  // Opening Storage again, for example from an alert, may name the section to show.
  useEffect(
    () =>
      app.on('launch', (launch) => {
        const next = sectionOf(launch.args);
        if (next !== undefined) {
          setSection(next);
        }
      }),
    [app],
  );

  return (
    <div className={styles.app}>
      <nav className={styles.nav} aria-label={t('sections')}>
        {sections.map((entry) => (
          <button
            key={entry}
            type="button"
            className={styles.navItem}
            aria-current={
              entry === section || (entry === 'advanced' && section === 'datasets')
                ? 'page'
                : undefined
            }
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
        {section === 'maintenance' && <MaintenanceSection app={app} />}
        {section === 'advanced' && (
          <AdvancedSection
            app={app}
            onDatasets={() => {
              setSection('datasets');
            }}
          />
        )}
      </main>
    </div>
  );
}
