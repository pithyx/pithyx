import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useMessages } from '../../shell/messages';
import { alertMessage } from '../../shell/storageAlerts';
import { Badge, Button } from '@pithyx/ui';
import {
  internalCall,
  type Pool,
  type ScrubAction,
  type StorageAlert,
  type StorageScheduleList,
  type StorageScheduleUpdate,
} from '../sdk/internal';
import styles from './Storage.module.css';
import { describeProblem, formatDate, healthKey, storageKey, useHealth, usePools } from './shared';
import { useFormatLocale } from '../../i18n';

const schedulesKey = [...storageKey, 'schedules'] as const;

const FREQUENCIES = ['off', 'weekly', 'monthly'] as const;

/** Alerts, scrubs and SMART self-test schedules. Everything live through the storage topics. */
export function MaintenanceSection({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('storage');
  const health = useHealth(app);
  const pools = usePools(app);
  const schedules = useQuery({
    queryKey: schedulesKey,
    queryFn: () => internalCall(app, 'internal.admin.storage.schedules', {}),
  });

  return (
    <div className={styles.storage}>
      <header className={styles.pageHeader}>
        <div>
          <p className={styles.muted}>{t('health.intro')}</p>
        </div>
      </header>
      <section aria-labelledby="storage-alerts" className={styles.section}>
        <h3 id="storage-alerts" className={styles.sectionTitle}>
          {t('health.alerts')}
        </h3>
        {health.isError && <ErrorNotice error={describeProblem(health.error)} />}
        {health.data !== undefined && <AlertList app={app} alerts={health.data.alerts} />}
      </section>
      {schedules.isError && <ErrorNotice error={describeProblem(schedules.error)} />}
      {schedules.data !== undefined && (
        <Schedules app={app} pools={pools.data?.pools ?? []} schedules={schedules.data} />
      )}
    </div>
  );
}

function AlertList({ app, alerts }: { app: PithyxApp; alerts: StorageAlert[] }) {
  const { t } = useTranslation('storage');
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
    return <p className={styles.muted}>{t('health.noAlerts')}</p>;
  }
  return (
    <>
      {mute.isError && <ErrorNotice error={describeProblem(mute.error)} />}
      <ul className={styles.alerts} aria-label={t('health.alerts')}>
        {alerts.map((alert) => {
          const message = alertMessage(alert);
          return (
            <li key={alert.key} className={styles.alert} data-severity={alert.severity}>
              <div className={styles.alertText}>
                <div className={styles.poolHead}>
                  <Badge tone={alert.severity === 'critical' ? 'danger' : 'highlight'}>
                    {t(`health.severity.${alert.severity === 'critical' ? 'critical' : 'warning'}`)}
                  </Badge>
                  <strong>{text(message, 'title')}</strong>
                  {alert.muted && <span className={styles.muted}>{t('health.muted')}</span>}
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
                {alert.muted ? t('health.unmute') : t('health.mute')}
              </Button>
            </li>
          );
        })}
      </ul>
    </>
  );
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
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
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
    value === null ? t('health.never') : t('health.next', { date: formatDate(value, locale) });
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
            {t(`health.frequency.${entry}`)}
          </option>
        ))}
      </select>
    </label>
  );

  return (
    <>
      {change.isError && <ErrorNotice error={describeProblem(change.error)} />}
      {scrub.isError && <ErrorNotice error={describeProblem(scrub.error)} />}
      <h3 className={styles.sectionTitle}>{t('health.scrub.title')}</h3>
      <p className={styles.muted}>{t('health.scrub.intro')}</p>
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
              t('health.scrub.schedule', { pool: pool.name }),
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
                  {paused ? t('health.scrub.resume') : t('health.scrub.start')}
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
                  {t('health.scrub.pause')}
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
                  {t('health.scrub.stop')}
                </Button>
              )}
            </div>
          </div>
        );
      })}
      <h3 className={styles.sectionTitle}>{t('health.selfTests.title')}</h3>
      <p className={styles.muted}>{t('health.selfTests.intro')}</p>
      {(['shortSelfTest', 'longSelfTest'] as const).map((kind) => {
        const schedule = schedules.schedules.find((entry) => entry.kind === kind);
        return (
          <div key={kind} className={styles.scheduleRow}>
            {frequencySelect(
              kind,
              '',
              schedule?.frequency ?? (kind === 'shortSelfTest' ? 'weekly' : 'monthly'),
              t(`health.selfTests.${kind}`),
            )}
            <span className={styles.muted}>{next(schedule?.nextRunAt ?? null)}</span>
          </div>
        );
      })}
    </>
  );
}
