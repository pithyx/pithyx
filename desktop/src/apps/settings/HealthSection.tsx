import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useMessages } from '../../shell/messages';
import { alertMessage } from '../../shell/storageAlerts';
import { Button } from '../../ui/Button';
import { Badge } from '../../ui/Controls';
import {
  internalCall,
  type Pool,
  type ScrubAction,
  type SmartReport,
  type StorageAlert,
  type StorageDisk,
  type StorageScheduleList,
  type StorageScheduleUpdate,
} from '../sdk/internal';
import styles from './Settings.module.css';
import { describeProblem } from './StoragePage';

const healthKey = ['settings', 'storage', 'health'] as const;
const schedulesKey = ['settings', 'storage', 'schedules'] as const;

const FREQUENCIES = ['off', 'weekly', 'monthly'] as const;
const SMART_STATES = ['passed', 'warning', 'failed', 'unsupported', 'unknown'] as const;
const SMART_REASONS = ['virtual', 'not_available', 'asleep', 'not_read', 'read_failed'] as const;
const SELF_TESTS = ['passed', 'failed', 'aborted', 'running'] as const;

function known<T extends string>(values: readonly T[], value: string | null, fallback: T): T {
  return values.find((entry) => entry === value) ?? fallback;
}

type Translate = ReturnType<typeof useTranslation<'settings'>>['t'];

function formatDate(value: string, locale: string): string {
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(
    new Date(value),
  );
}

/** Alerts, SMART per disk, scrubs and self-test schedules. Everything live through the storage topics. */
export function HealthSection({
  app,
  pools,
  disks,
}: {
  app: PithyxApp;
  pools: Pool[];
  disks: StorageDisk[];
}) {
  const { t } = useTranslation('settings');
  const health = useQuery({
    queryKey: healthKey,
    queryFn: () => internalCall(app, 'internal.admin.storage.health', {}),
  });
  const schedules = useQuery({
    queryKey: schedulesKey,
    queryFn: () => internalCall(app, 'internal.admin.storage.schedules', {}),
  });

  return (
    <section aria-labelledby="storage-health" className={styles.section}>
      <h3 id="storage-health" className={styles.sectionTitle}>
        {t('storage.health.title')}
      </h3>
      {health.isError && <ErrorNotice error={describeProblem(health.error)} />}
      {health.data !== undefined && <AlertList app={app} alerts={health.data.alerts} />}
      {health.data !== undefined && <SmartTable reports={health.data.smart} disks={disks} />}
      {schedules.isError && <ErrorNotice error={describeProblem(schedules.error)} />}
      {schedules.data !== undefined && (
        <Schedules app={app} pools={pools} schedules={schedules.data} />
      )}
    </section>
  );
}

function AlertList({ app, alerts }: { app: PithyxApp; alerts: StorageAlert[] }) {
  const { t } = useTranslation('settings');
  const text = useMessages('notifications');
  const queryClient = useQueryClient();
  const mute = useMutation({
    mutationFn: (params: { key: string; muted: boolean }) =>
      internalCall(app, 'internal.admin.storage.mute', params),
    onSuccess: (next) => {
      queryClient.setQueryData(healthKey, next);
    },
  });
  if (alerts.length === 0) {
    return <p className={styles.muted}>{t('storage.health.noAlerts')}</p>;
  }
  return (
    <>
      {mute.isError && <ErrorNotice error={describeProblem(mute.error)} />}
      <ul className={styles.alerts} aria-label={t('storage.health.alerts')}>
        {alerts.map((alert) => {
          const message = alertMessage(alert);
          return (
            <li key={alert.key} className={styles.alert} data-severity={alert.severity}>
              <div className={styles.alertText}>
                <div className={styles.poolHead}>
                  <Badge tone={alert.severity === 'critical' ? 'danger' : 'highlight'}>
                    {t(
                      `storage.health.severity.${alert.severity === 'critical' ? 'critical' : 'warning'}`,
                    )}
                  </Badge>
                  <strong>{text(message, 'title')}</strong>
                  {alert.muted && <span className={styles.muted}>{t('storage.health.muted')}</span>}
                </div>
                <p className={styles.muted}>{text(message, 'body')}</p>
              </div>
              <Button
                size="sm"
                variant="quiet"
                isDisabled={mute.isPending}
                onPress={() => {
                  mute.mutate({ key: alert.key, muted: !alert.muted });
                }}
              >
                {alert.muted ? t('storage.health.unmute') : t('storage.health.mute')}
              </Button>
            </li>
          );
        })}
      </ul>
    </>
  );
}

function SmartTable({ reports, disks }: { reports: SmartReport[]; disks: StorageDisk[] }) {
  const { t, i18n } = useTranslation('settings');
  const byId = new Map(reports.map((report) => [report.diskId, report]));
  return (
    <>
      <h4 className={styles.subTitle}>{t('storage.health.smart.title')}</h4>
      <table className={styles.table}>
        <caption className="visually-hidden">{t('storage.health.smart.caption')}</caption>
        <thead>
          <tr>
            <th scope="col">{t('storage.disks.name')}</th>
            <th scope="col">{t('storage.health.smart.state')}</th>
            <th scope="col">{t('storage.health.smart.details')}</th>
          </tr>
        </thead>
        <tbody>
          {disks.map(({ disk }) => {
            const report = byId.get(disk.id);
            const state = known(SMART_STATES, report?.state ?? null, 'unknown');
            return (
              <tr key={disk.id}>
                <th scope="row" className={styles.mono}>
                  {disk.name}
                </th>
                <td>
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
                    {t(`storage.health.smart.states.${state}`)}
                  </Badge>
                </td>
                <td className={styles.reason}>{smartDetails(t, report, i18n.language)}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </>
  );
}

function smartDetails(t: Translate, report: SmartReport | undefined, locale: string): string {
  if (report === undefined) {
    return t('storage.health.smart.reasons.not_read');
  }
  if (report.readAt === null) {
    return t(`storage.health.smart.reasons.${known(SMART_REASONS, report.reason, 'not_read')}`);
  }
  const parts = [
    t('storage.health.smart.counters', {
      reallocated: report.reallocatedSectors ?? 0,
      pending: report.pendingSectors ?? 0,
      mediaErrors: report.mediaErrors ?? 0,
    }),
  ];
  if (report.temperatureCelsius !== null) {
    parts.unshift(t('storage.health.smart.temperature', { value: report.temperatureCelsius }));
  }
  if (report.lastSelfTest !== null) {
    parts.push(
      t(`storage.health.smart.selfTest.${known(SELF_TESTS, report.lastSelfTest, 'aborted')}`),
    );
  }
  if (report.asleep) {
    parts.push(t('storage.health.smart.asleep', { date: formatDate(report.readAt, locale) }));
  }
  return parts.join(' · ');
}

function Schedules({
  app,
  pools,
  schedules,
}: {
  app: PithyxApp;
  pools: Pool[];
  schedules: StorageScheduleList;
}) {
  const { t, i18n } = useTranslation('settings');
  const queryClient = useQueryClient();
  const change = useMutation({
    mutationFn: (update: StorageScheduleUpdate) =>
      internalCall(app, 'internal.admin.storage.schedule', update),
    onMutate: (update) => {
      // Optimistic: the select shows the new value at once and goes back if the daemon refuses.
      const before = queryClient.getQueryData<StorageScheduleList>(schedulesKey);
      if (before !== undefined) {
        queryClient.setQueryData(schedulesKey, {
          schedules: before.schedules.map((entry) =>
            entry.kind === update.kind && entry.target === update.target
              ? { ...entry, frequency: update.frequency }
              : entry,
          ),
        });
      }
      return before;
    },
    onError: (_, __, before) => {
      queryClient.setQueryData(schedulesKey, before);
    },
    onSuccess: (next) => {
      queryClient.setQueryData(schedulesKey, next);
    },
  });
  const scrub = useMutation({
    mutationFn: (params: { pool: string; action: ScrubAction }) =>
      internalCall(app, 'internal.admin.storage.scrub', params),
  });
  const next = (value: string | null) =>
    value === null
      ? t('storage.health.never')
      : t('storage.health.next', { date: formatDate(value, i18n.language) });
  const frequencySelect = (kind: string, target: string, frequency: string, label: string) => (
    <label className={styles.selectField}>
      <span className={styles.fieldLabel}>{label}</span>
      <select
        className={styles.select}
        value={frequency}
        onChange={(event) => {
          change.mutate({ kind, target, frequency: event.target.value });
        }}
      >
        {FREQUENCIES.map((entry) => (
          <option key={entry} value={entry}>
            {t(`storage.health.frequency.${entry}`)}
          </option>
        ))}
      </select>
    </label>
  );

  return (
    <>
      {change.isError && <ErrorNotice error={describeProblem(change.error)} />}
      {scrub.isError && <ErrorNotice error={describeProblem(scrub.error)} />}
      <h4 className={styles.subTitle}>{t('storage.health.scrub.title')}</h4>
      <p className={styles.muted}>{t('storage.health.scrub.intro')}</p>
      {pools.map((pool) => {
        const schedule = schedules.schedules.find(
          (entry) => entry.kind === 'scrub' && entry.target === pool.name,
        );
        const scrubbing = pool.scan?.kind === 'scrub' && pool.scan.state === 'scanning';
        const paused = scrubbing && pool.scan?.paused === true;
        return (
          <div key={pool.name} className={styles.scheduleRow}>
            {frequencySelect(
              'scrub',
              pool.name,
              schedule?.frequency ?? 'monthly',
              t('storage.health.scrub.schedule', { pool: pool.name }),
            )}
            <span className={styles.muted}>{next(schedule?.nextRunAt ?? null)}</span>
            <div className={styles.buttons}>
              {(!scrubbing || paused) && (
                <Button
                  size="sm"
                  isDisabled={scrub.isPending}
                  onPress={() => {
                    scrub.mutate({ pool: pool.name, action: 'start' });
                  }}
                >
                  {paused ? t('storage.health.scrub.resume') : t('storage.health.scrub.start')}
                </Button>
              )}
              {scrubbing && !paused && (
                <Button
                  size="sm"
                  isDisabled={scrub.isPending}
                  onPress={() => {
                    scrub.mutate({ pool: pool.name, action: 'pause' });
                  }}
                >
                  {t('storage.health.scrub.pause')}
                </Button>
              )}
              {scrubbing && (
                <Button
                  size="sm"
                  variant="quiet"
                  isDisabled={scrub.isPending}
                  onPress={() => {
                    scrub.mutate({ pool: pool.name, action: 'stop' });
                  }}
                >
                  {t('storage.health.scrub.stop')}
                </Button>
              )}
            </div>
          </div>
        );
      })}
      <h4 className={styles.subTitle}>{t('storage.health.selfTests.title')}</h4>
      <p className={styles.muted}>{t('storage.health.selfTests.intro')}</p>
      {(['shortSelfTest', 'longSelfTest'] as const).map((kind) => {
        const schedule = schedules.schedules.find((entry) => entry.kind === kind);
        return (
          <div key={kind} className={styles.scheduleRow}>
            {frequencySelect(
              kind,
              '',
              schedule?.frequency ?? (kind === 'shortSelfTest' ? 'weekly' : 'monthly'),
              t(`storage.health.selfTests.${kind}`),
            )}
            <span className={styles.muted}>{next(schedule?.nextRunAt ?? null)}</span>
          </div>
        );
      })}
    </>
  );
}
