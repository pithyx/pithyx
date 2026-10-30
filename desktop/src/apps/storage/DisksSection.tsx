import type { PithyxApp } from '@pithyx/sdk';
import { DataTable, StatusBadge, type DataTableColumn } from '@pithyx/ui';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
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
import { useFormatLocale } from '../../i18n';

const SMART_STATES = ['passed', 'warning', 'failed', 'unsupported', 'unknown'] as const;
const SMART_REASONS = ['virtual', 'not_available', 'asleep', 'not_read', 'read_failed'] as const;
const SELF_TESTS = ['passed', 'failed', 'aborted', 'running'] as const;

function known<T extends string>(values: readonly T[], value: string | null, fallback: T): T {
  return values.find((entry) => entry === value) ?? fallback;
}

type Row = StorageDisk & { report: SmartReport | undefined };

const decisionTones = {
  allowed: 'success',
  refused: 'neutral',
  needsSerialConfirmation: 'warning',
} as const;
const smartTones = {
  passed: 'success',
  warning: 'warning',
  failed: 'danger',
  unsupported: 'neutral',
  unknown: 'neutral',
} as const;

/**
 * Every disk with the guard's verdict for a new pool and its SMART health (D394: a table with the selected disk's
 * details on the right). Live through the storage topics.
 */
export function DisksSection({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const disks = useDisks(app);
  const health = useHealth(app);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const reports = new Map((health.data?.smart ?? []).map((report) => [report.diskId, report]));
  const rows: Row[] = (disks.data?.disks ?? []).map((entry) => ({
    ...entry,
    report: reports.get(entry.disk.id),
  }));
  const selected = rows.find((row) => row.disk.id === selectedId);
  const smartState = (row: Row) => known(SMART_STATES, row.report?.state ?? null, 'unknown');
  const columns: DataTableColumn<Row>[] = [
    {
      id: 'name',
      label: t('disks.name'),
      isRowHeader: true,
      sortValue: (row) => row.disk.name,
      cell: (row) => (
        <span className={styles.diskCell}>
          <span className={styles.mono}>{row.disk.name}</span>
          <span className={styles.muted}>
            {[row.disk.serial, formatBytes(row.disk.sizeBytes, locale)]
              .filter((part) => part !== null && part !== '')
              .join(' · ')}
          </span>
        </span>
      ),
    },
    {
      id: 'status',
      label: t('disks.status'),
      sortValue: (row) => row.guard.decision,
      cell: (row) => (
        <span className={styles.diskCell}>
          <StatusBadge tone={decisionTones[row.guard.decision]}>
            {t(`decision.${row.guard.decision}`)}
          </StatusBadge>
          <span className={styles.reason}>{guardReason(t, row.guard)}</span>
        </span>
      ),
    },
    {
      id: 'smart',
      label: t('health.smart.state'),
      sortValue: (row) => smartState(row),
      cell: (row) => (
        <span className={styles.diskCell}>
          <StatusBadge tone={smartTones[smartState(row)]}>
            {t(`health.smart.states.${smartState(row)}`)}
          </StatusBadge>
          <span className={styles.reason}>{smartDetails(t, row.report, locale)}</span>
        </span>
      ),
    },
  ];
  return (
    <div className={styles.storage}>
      <p className={styles.muted}>{t('disks.intro')}</p>
      {disks.data?.environment === 'dev' && <p className={styles.notice}>{t('dev')}</p>}
      {disks.isError && <ErrorNotice error={describeProblem(disks.error)} />}
      {health.isError && <ErrorNotice error={describeProblem(health.error)} />}
      {disks.data !== undefined && (
        <DataTable
          aria-label={t('disks.caption')}
          className={styles.disksTable}
          columns={columns}
          items={rows}
          getKey={(row) => row.disk.id}
          density="comfortable"
          selectionMode="single"
          selectedKeys={selected === undefined ? new Set() : new Set([selected.disk.id])}
          onSelectionChange={(keys) => {
            const [key] = keys === 'all' ? [] : [...keys];
            setSelectedId(key === undefined ? null : String(key));
          }}
          {...(selected === undefined
            ? {}
            : { details: <DiskDetails row={selected} />, detailsLabel: t('disks.details') })}
        />
      )}
    </div>
  );
}

/** The facts of the selected disk that the table leaves out. */
function DiskDetails({ row }: { row: Row }) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const { disk } = row;
  return (
    <div className={styles.diskDetails}>
      <h2 className={styles.mono}>{disk.name}</h2>
      <dl className={styles.facts}>
        <dt>{t('disks.model')}</dt>
        <dd>{disk.model ?? ''}</dd>
        <dt>{t('disks.serial')}</dt>
        <dd className={styles.mono}>{disk.serial ?? ''}</dd>
        <dt>{t('disks.size')}</dt>
        <dd>{formatBytes(disk.sizeBytes, locale)}</dd>
        <dt>{t('disks.path')}</dt>
        <dd className={styles.mono}>{disk.path}</dd>
        <dt>{t('disks.status')}</dt>
        <dd>{guardReason(t, row.guard)}</dd>
        <dt>{t('health.smart.state')}</dt>
        <dd>{smartDetails(t, row.report, locale)}</dd>
      </dl>
    </div>
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
