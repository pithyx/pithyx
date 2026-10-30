import type { PithyxApp } from '@pithyx/sdk';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { Button } from '@pithyx/ui';
import type { Pool } from '../sdk/internal';
import { DatasetDialog, type DatasetAction } from './DatasetDialog';
import { datasetRole } from './datasets';
import { ImportDialog } from './ImportDialog';
import { PoolChangeDialog } from './PoolChangeDialog';
import { breakable, describeProblem, useDisks, usePools } from './shared';
import styles from './Storage.module.css';

/**
 * What most boxes never need, each with a sentence on what it does and when: volumes (block devices on a pool),
 * exporting a pool to move its disks, importing a pool from other disks, and the datasets with their limits.
 */
export function AdvancedSection({ app, onDatasets }: { app: PithyxApp; onDatasets: () => void }) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const pools = usePools(app);
  const disks = useDisks(app);
  const [action, setAction] = useState<DatasetAction | null>(null);
  const [exporting, setExporting] = useState<Pool | null>(null);
  const [importing, setImporting] = useState(false);
  const writable = pools.data?.pools.filter((pool) => !pool.readOnly) ?? [];
  const datasets = pools.data?.datasets ?? [];
  const volumes = datasets.filter(
    (dataset) => dataset.type === 'volume' && datasetRole(dataset.pool, dataset.name) === 'volume',
  );
  return (
    <div className={styles.storage}>
      <header className={styles.pageHeader}>
        <div>
          <p className={styles.muted}>{t('advanced.intro')}</p>
        </div>
      </header>
      {pools.isError && <ErrorNotice error={describeProblem(pools.error)} />}

      {/* Nothing in 1.0 uses a volume (no iSCSI, no virtual machines), so only existing ones show, to delete them. */}
      {volumes.length > 0 && (
        <section className={styles.section} aria-labelledby="advanced-volumes">
          <h3 id="advanced-volumes" className={styles.sectionTitle}>
            {t('advanced.volumes.title')}
          </h3>
          <p className={styles.muted}>{t('advanced.volumes.explain')}</p>
          <ul className={styles.plainList} aria-label={t('advanced.volumes.title')}>
            {volumes.map((volume) => (
              <li key={volume.name} className={styles.advancedItem}>
                <span>
                  <span className={styles.mono}>{breakable(volume.name)}</span>{' '}
                  <span className={styles.muted}>
                    {formatBytes(volume.volumeSizeBytes ?? 0, locale)}
                  </span>
                </span>
                <Button
                  size="sm"
                  variant="quiet"
                  aria-label={t('datasets.destroy.label', { dataset: volume.name })}
                  onPress={() => {
                    setAction({ kind: 'destroy', dataset: volume });
                  }}
                >
                  {t('datasets.destroy.button')}
                </Button>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className={styles.section} aria-labelledby="advanced-export">
        <h3 id="advanced-export" className={styles.sectionTitle}>
          {t('advanced.export.title')}
        </h3>
        <p className={styles.muted}>{t('advanced.export.explain')}</p>
        {writable.length === 0 ? (
          <p className={styles.muted}>{t('pools.empty')}</p>
        ) : (
          <div className={styles.buttons}>
            {writable.map((pool) => (
              <Button
                key={pool.name}
                size="sm"
                isDisabled={disks.data === undefined}
                onPress={() => {
                  setExporting(pool);
                }}
              >
                {t('advanced.export.button', { pool: pool.name })}
              </Button>
            ))}
          </div>
        )}
      </section>

      <section className={styles.section} aria-labelledby="advanced-import">
        <h3 id="advanced-import" className={styles.sectionTitle}>
          {t('advanced.import.title')}
        </h3>
        <p className={styles.muted}>{t('advanced.import.explain')}</p>
        <div className={styles.buttons}>
          <Button
            size="sm"
            isDisabled={disks.data === undefined}
            onPress={() => {
              setImporting(true);
            }}
          >
            {t('import.button')}
          </Button>
        </div>
      </section>

      <section className={styles.section} aria-labelledby="advanced-datasets">
        <h3 id="advanced-datasets" className={styles.sectionTitle}>
          {t('advanced.datasets.title')}
        </h3>
        <p className={styles.muted}>{t('advanced.datasets.explain')}</p>
        <div className={styles.buttons}>
          <Button size="sm" onPress={onDatasets}>
            {t('advanced.datasets.button')}
          </Button>
        </div>
      </section>

      {action !== null && (
        <DatasetDialog
          app={app}
          action={action}
          onClose={() => {
            setAction(null);
          }}
        />
      )}
      {exporting !== null && disks.data !== undefined && (
        <PoolChangeDialog
          app={app}
          pool={exporting}
          action="pool.export"
          disks={disks.data.disks}
          onClose={() => {
            setExporting(null);
          }}
        />
      )}
      {importing && disks.data !== undefined && (
        <ImportDialog
          app={app}
          disks={disks.data.disks}
          onClose={() => {
            setImporting(false);
          }}
        />
      )}
    </div>
  );
}
