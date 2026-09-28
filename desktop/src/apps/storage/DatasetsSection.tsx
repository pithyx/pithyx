import type { PithyxApp } from '@pithyx/sdk';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { cx } from '../../ui/cx';
import type { Dataset } from '../sdk/internal';
import { describeProblem, usePools } from './shared';
import styles from './Storage.module.css';

/** Datasets and volumes of every pool with their space, limits and compression. */
export function DatasetsSection({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('storage');
  const pools = usePools(app);
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
      {pools.data?.pools.map((pool) => (
        <section
          key={pool.name}
          aria-labelledby={`datasets-${pool.name}`}
          className={styles.section}
        >
          <h3 id={`datasets-${pool.name}`} className={styles.sectionTitle}>
            {pool.name}
          </h3>
          <DatasetTable
            pool={pool.name}
            datasets={datasets.filter((dataset) => dataset.pool === pool.name)}
          />
        </section>
      ))}
    </div>
  );
}

function DatasetTable({ pool, datasets }: { pool: string; datasets: Dataset[] }) {
  const { t, i18n } = useTranslation('storage');
  const bytes = (value: number) => formatBytes(value, i18n.language);
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
          <th scope="col" className={styles.optional}>
            {t('datasets.quota')}
          </th>
          <th scope="col" className={styles.optional}>
            {t('datasets.compression')}
          </th>
          <th scope="col">{t('datasets.location')}</th>
        </tr>
      </thead>
      <tbody>
        {datasets.map((dataset) => (
          <tr key={dataset.name} data-dataset={dataset.name}>
            <th scope="row" className={styles.mono}>
              {dataset.name}
              {dataset.type === 'volume' && (
                <span className={styles.muted}> · {t('datasets.volume')}</span>
              )}
            </th>
            <td className={styles.number}>{bytes(dataset.usedBytes)}</td>
            <td className={cx(styles.number, styles.optional)}>{bytes(dataset.availableBytes)}</td>
            <td className={styles.optional}>
              {dataset.quotaBytes === null ? t('datasets.noQuota') : bytes(dataset.quotaBytes)}
            </td>
            <td className={styles.optional}>{dataset.compression ?? ''}</td>
            <td className={styles.mono}>
              {dataset.type === 'volume'
                ? t('datasets.volumeSize', { size: bytes(dataset.volumeSizeBytes ?? 0) })
                : (dataset.mountPoint ?? t('datasets.notMounted'))}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
