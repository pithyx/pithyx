import type { PithyxApp } from '@pithyx/sdk';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button, cx } from '@pithyx/ui';
import type { Dataset } from '../sdk/internal';
import { DatasetDialog, type DatasetAction } from './DatasetDialog';
import { canRenameOrDelete, datasetRole, isHiddenDataset } from './datasets';
import { breakable, describeProblem, usePools } from './shared';
import styles from './Storage.module.css';
import { useFormatLocale } from '../../i18n';

/** Datasets and volumes of every pool with their space, limits and compression, and the actions on them. */
export function DatasetsSection({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('storage');
  const pools = usePools(app);
  const [action, setAction] = useState<DatasetAction | null>(null);
  const datasets = pools.data?.datasets ?? [];
  return (
    <div className={styles.storage}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('nav.datasets')}</h2>
          <p className={styles.muted}>{t('datasets.intro')}</p>
        </div>
      </header>
      {pools.isError && <ErrorNotice error={describeProblem(pools.error)} />}
      {pools.data?.pools.length === 0 && <p className={styles.muted}>{t('pools.empty')}</p>}
      {pools.data?.pools.map((pool) => {
        const own = datasets.filter(
          (dataset) => dataset.pool === pool.name && !isHiddenDataset(pool.name, dataset.name),
        );
        return (
          <section
            key={pool.name}
            aria-labelledby={`datasets-${pool.name}`}
            className={styles.section}
          >
            <div className={styles.sectionHead}>
              <h3 id={`datasets-${pool.name}`} className={styles.sectionTitle}>
                {pool.name}
              </h3>
            </div>
            <DatasetTable pool={pool.name} datasets={own} onAction={setAction} />
          </section>
        );
      })}
      {action !== null && (
        <DatasetDialog
          app={app}
          action={action}
          onClose={() => {
            setAction(null);
          }}
        />
      )}
    </div>
  );
}

function DatasetTable({
  pool,
  datasets,
  onAction,
}: {
  pool: string;
  datasets: Dataset[];
  onAction: (action: DatasetAction) => void;
}) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const bytes = (value: number) => formatBytes(value, locale);
  return (
    <table className={styles.table}>
      <caption className="visually-hidden">{t('datasets.caption', { pool })}</caption>
      <thead>
        <tr>
          <th scope="col">{t('datasets.name')}</th>
          <th scope="col" className={styles.number}>
            {t('datasets.used')}
          </th>
          <th scope="col" className={cx(styles.number, styles.optional)}>
            {t('datasets.available')}
          </th>
          <th scope="col" className={styles.wide}>
            {t('datasets.quota')}
          </th>
          <th scope="col" className={styles.wide}>
            {t('datasets.compression')}
          </th>
          <th scope="col" className={styles.optional}>
            {t('datasets.location')}
          </th>
          <th scope="col">
            <span className="visually-hidden">{t('datasets.actions')}</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {datasets.map((dataset) => {
          const movable = canRenameOrDelete(datasetRole(pool, dataset.name));
          return (
            <tr key={dataset.name} data-dataset={dataset.name}>
              <th scope="row" className={styles.datasetName}>
                {breakable(dataset.name)}
                {dataset.type === 'volume' && (
                  <span className={styles.muted}> · {t('datasets.volume.label')}</span>
                )}
              </th>
              <td className={styles.number}>{bytes(dataset.usedBytes)}</td>
              <td className={cx(styles.number, styles.optional)}>
                {bytes(dataset.availableBytes)}
              </td>
              <td className={styles.wide}>
                {dataset.quotaBytes === null ? t('datasets.noQuota') : bytes(dataset.quotaBytes)}
              </td>
              <td className={styles.wide}>{dataset.compression ?? ''}</td>
              <td className={cx(styles.datasetName, styles.optional)}>
                {dataset.type === 'volume'
                  ? t('datasets.volumeSize', { size: bytes(dataset.volumeSizeBytes ?? 0) })
                  : dataset.mountPoint === null
                    ? t('datasets.notMounted')
                    : breakable(dataset.mountPoint)}
              </td>
              <td>
                <span className={styles.snapshotActions}>
                  <Button
                    size="sm"
                    variant="quiet"
                    aria-label={t('datasets.limits.label', { dataset: dataset.name })}
                    onPress={() => {
                      onAction({ kind: 'limits', dataset });
                    }}
                  >
                    {t('datasets.limits.button')}
                  </Button>
                  {movable && (
                    <>
                      <Button
                        size="sm"
                        variant="quiet"
                        aria-label={t('datasets.rename.label', { dataset: dataset.name })}
                        onPress={() => {
                          onAction({ kind: 'rename', dataset });
                        }}
                      >
                        {t('datasets.rename.button')}
                      </Button>
                      <Button
                        size="sm"
                        variant="quiet"
                        aria-label={t('datasets.destroy.label', { dataset: dataset.name })}
                        onPress={() => {
                          onAction({ kind: 'destroy', dataset });
                        }}
                      >
                        {t('datasets.destroy.button')}
                      </Button>
                    </>
                  )}
                </span>
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
