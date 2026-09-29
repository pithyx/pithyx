import type { PithyxApp } from '@pithyx/sdk';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Badge } from '../../ui/Controls';
import { cx } from '../../ui/cx';
import type { SmartReport, StorageDisk } from '../sdk/internal';
import {
  describeProblem,
  formatDate,
  guardReason,
  useDisks,
  useHealth,
  type Translate,
} from './shared';
import styles from './Storage.module.css';

const SMART_STATES = ['passed', 'warning', 'failed', 'unsupported', 'unknown'] as const;
const SMART_REASONS = ['virtual', 'not_available', 'asleep', 'not_read', 'read_failed'] as const;
const SELF_TESTS = ['passed', 'failed', 'aborted', 'running'] as const;

function known<T extends string>(values: readonly T[], value: string | null, fallback: T): T {
  return values.find((entry) => entry === value) ?? fallback;
}

/** Every disk with the guard's verdict for a new pool and its SMART health. Live through the storage topics. */
export function DisksSection({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('storage');
  const disks = useDisks(app);
  const health = useHealth(app);
  return (
    <div className={styles.storage}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('nav.disks')}</h2>
          <p className={styles.muted}>{t('disks.intro')}</p>
        </div>
      </header>
      {disks.data?.environment === 'dev' && <p className={styles.notice}>{t('dev')}</p>}
      {disks.isError && <ErrorNotice error={describeProblem(disks.error)} />}
      {health.isError && <ErrorNotice error={describeProblem(health.error)} />}
      {disks.data !== undefined && (
        <DiskTable disks={disks.data.disks} reports={health.data?.smart ?? []} />
      )}
    </div>
  );
}

function DiskTable({ disks, reports }: { disks: StorageDisk[]; reports: SmartReport[] }) {
  const { t, i18n } = useTranslation('storage');
  const byId = new Map(reports.map((report) => [report.diskId, report]));
  return (
    <table className={cx(styles.table, styles.stack)}>
      <caption className="visually-hidden">{t('disks.caption')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('disks.name')}</th>
          <th scope="col" className={styles.optional}>
            {t('disks.model')}
          </th>
          <th scope="col">{t('disks.serial')}</th>
          <th scope="col" className={styles.number}>
            {t('disks.size')}
          </th>
          <th scope="col">{t('disks.status')}</th>
          <th scope="col">{t('health.smart.state')}</th>
        </tr>
      </thead>
      <tbody>
        {disks.map(({ disk, guard }) => {
          const report = byId.get(disk.id);
          const state = known(SMART_STATES, report?.state ?? null, 'unknown');
          return (
            <tr key={disk.id}>
              <th scope="row" className={styles.mono}>
                {disk.name}
              </th>
              <td className={styles.optional} data-label={t('disks.model')}>
                {disk.model ?? ''}
              </td>
              <td className={styles.mono} data-label={t('disks.serial')}>
                {disk.serial ?? ''}
              </td>
              <td className={styles.number} data-label={t('disks.size')}>
                {formatBytes(disk.sizeBytes, i18n.language)}
              </td>
              <td data-label={t('disks.status')}>
                <div className={styles.verdict}>
                  <Badge
                    tone={
                      guard.decision === 'allowed'
                        ? 'accent'
                        : guard.decision === 'refused'
                          ? 'neutral'
                          : 'highlight'
                    }
                  >
                    {t(`decision.${guard.decision}`)}
                  </Badge>
                  <span className={styles.reason}>{guardReason(t, guard)}</span>
                </div>
              </td>
              <td data-label={t('health.smart.state')}>
                <div className={styles.verdict}>
                  <Badge
                    tone={
                      state === 'passed'
                        ? 'accent'
                        : state === 'failed'
                          ? 'danger'
                          : state === 'warning'
                            ? 'highlight'
                            : 'neutral'
                    }
                  >
                    {t(`health.smart.states.${state}`)}
                  </Badge>
                  <span className={styles.reason}>{smartDetails(t, report, i18n.language)}</span>
                </div>
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

function smartDetails(t: Translate, report: SmartReport | undefined, locale: string): string {
  if (report === undefined) {
    return t('health.smart.reasons.not_read');
  }
  if (report.readAt === null) {
    return t(`health.smart.reasons.${known(SMART_REASONS, report.reason, 'not_read')}`);
  }
  const parts = [
    t('health.smart.counters', {
      reallocated: report.reallocatedSectors ?? 0,
      pending: report.pendingSectors ?? 0,
      mediaErrors: report.mediaErrors ?? 0,
    }),
  ];
  if (report.temperatureCelsius !== null) {
    parts.unshift(t('health.smart.temperature', { value: report.temperatureCelsius }));
  }
  if (report.lastSelfTest !== null) {
    parts.push(t(`health.smart.selfTest.${known(SELF_TESTS, report.lastSelfTest, 'aborted')}`));
  }
  if (report.asleep) {
    parts.push(t('health.smart.asleep', { date: formatDate(report.readAt, locale) }));
  }
  return parts.join(' · ');
}
