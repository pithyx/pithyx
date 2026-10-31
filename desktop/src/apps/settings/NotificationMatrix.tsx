import type { PithyxApp } from '@pithyx/sdk';
import { Checkbox } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import {
  matrixKey,
  notificationsCall,
  type NotificationMatrix as Matrix,
  type NotificationMatrixChange,
} from '../sdk/notifications';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';

/** The events the daemon's matrix lists (NotificationEvents.cs), for their names; an unknown one shows its code. */
export const EVENT_CODES = [
  'storage.alert.poolFaulted',
  'storage.alert.poolSuspended',
  'storage.alert.poolDegraded',
  'storage.alert.diskMissing',
  'storage.alert.dataErrors',
  'storage.alert.scrubErrors',
  'storage.alert.smartFailed',
  'storage.alert.smartWarning',
  'storage.alert.capacityCritical',
  'storage.alert.capacityWarning',
  'storage.alert.poolLocked',
  'storage.alert.poolLockedVault',
  'storage.alert.poolLockedUsb',
  'storage.alert.poolLockedUsbFailed',
  'storage.usb.unlocked',
  'storage.alert.resolved',
  'update.available',
  'update.debian.available',
  'update.installed',
  'update.rolled_back',
  'update.restored',
  'update.source.changed',
  'store.updates.available',
  'auth.signin.blocked',
  'apps.elevated.critical',
  'apps.device.missing',
  'store.catalog.failed',
  'system.mirror.degraded',
  'system.ups.on_battery',
  'system.ups.online',
  'system.ups.shutdown',
  'system.ups.unreachable',
  'proxy.acme.renewal_failed',
  'jobs.job.succeeded',
  'jobs.job.failed',
  'jobs.job.cancelled',
  'jobs.job.interrupted',
] as const;
type EventCode = (typeof EVENT_CODES)[number];
const isEventCode = (code: string): code is EventCode =>
  (EVENT_CODES as readonly string[]).includes(code);

type Category = Matrix['events'][number]['category'];
/** A column: the administrators' desktop (null) or a channel of the box. */
type Column = number | null;

const cellKey = (event: string, column: Column) => `${event}\n${String(column ?? 0)}`;

/**
 * Settings > Notifications > Events (D407): one row per event the box reports, grouped by category, one column for the
 * administrators' desktop and one per channel of the box. A checkbox applies at once (optimistically); a group's
 * checkbox sets every event of the group in that column.
 */
export function NotificationMatrix({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const { t: tDesktop } = useTranslation('desktop');
  const queryClient = useQueryClient();
  const matrix = useQuery({
    queryKey: matrixKey,
    queryFn: () => notificationsCall(app, 'internal.admin.notifications.matrix', {}),
  });
  const change = useMutation({
    mutationFn: (cells: NotificationMatrixChange[]) =>
      notificationsCall(app, 'internal.admin.notifications.setMatrix', { cells }),
    onMutate: (cells) => {
      const wanted = new Map(
        cells.map((cell) => [cellKey(cell.event, cell.channel ?? null), cell]),
      );
      queryClient.setQueryData<Matrix>(matrixKey, (current) =>
        current === undefined
          ? current
          : {
              ...current,
              cells: current.cells.map((cell) => {
                const next = wanted.get(cellKey(cell.event, cell.channel ?? null));
                return next === undefined
                  ? cell
                  : { ...cell, enabled: next.enabled, changed: true };
              }),
            },
      );
    },
    onSuccess: (next) => {
      queryClient.setQueryData(matrixKey, next);
    },
    onError: () => {
      void queryClient.invalidateQueries({ queryKey: matrixKey });
    },
  });

  if (matrix.isError) {
    return <ErrorNotice error={describeProblem(matrix.error)} />;
  }
  const data = matrix.data;
  if (data === undefined) {
    return null;
  }

  const enabled = new Map(
    data.cells.map((cell) => [cellKey(cell.event, cell.channel ?? null), cell.enabled]),
  );
  const columns: { id: Column; name: string }[] = [
    { id: null, name: t('notifications.matrix.desktop') },
    ...data.channels.map((channel) => ({
      id: channel.id,
      name: channel.enabled
        ? channel.name
        : t('notifications.matrix.channelOff', { name: channel.name }),
    })),
  ];
  const eventName = (code: string) =>
    isEventCode(code) ? t(`notifications.matrix.events.${code}`) : code;
  const groups: { category: Category; events: string[] }[] = [];
  for (const entry of data.events) {
    const group = groups.find((candidate) => candidate.category === entry.category);
    if (group === undefined) {
      groups.push({ category: entry.category, events: [entry.code] });
    } else {
      group.events.push(entry.code);
    }
  }

  return (
    <div className={styles.content}>
      <p className={styles.muted}>{t('notifications.matrix.explain')}</p>
      {data.channels.length === 0 && (
        <p className={styles.notice} role="note">
          {t('notifications.matrix.noChannels')}
        </p>
      )}
      {change.isError && <ErrorNotice error={describeProblem(change.error)} />}
      <div className={styles.matrixScroll}>
        <table className={styles.matrix}>
          <thead>
            <tr>
              <th scope="col">{t('notifications.matrix.event')}</th>
              {columns.map((column) => (
                <th key={String(column.id)} scope="col" className={styles.matrixColumn}>
                  {column.name}
                </th>
              ))}
            </tr>
          </thead>
          {groups.map((group) => {
            const groupName = tDesktop(`channels.categoryNames.${group.category}`);
            return (
              <tbody key={group.category}>
                <tr className={styles.matrixGroup}>
                  <th scope="rowgroup">{groupName}</th>
                  {columns.map((column) => {
                    const on = group.events.filter((code) => enabled.get(cellKey(code, column.id)));
                    return (
                      <td
                        key={String(column.id)}
                        className={styles.matrixCell}
                        data-column={column.name}
                      >
                        <Checkbox
                          isSelected={on.length === group.events.length}
                          isIndeterminate={on.length > 0 && on.length < group.events.length}
                          onChange={(selected) => {
                            change.mutate(
                              group.events.map((code) => ({
                                event: code,
                                channel: column.id,
                                enabled: selected,
                              })),
                            );
                          }}
                        >
                          <span className="visually-hidden">
                            {t('notifications.matrix.group', {
                              group: groupName,
                              column: column.name,
                            })}
                          </span>
                        </Checkbox>
                      </td>
                    );
                  })}
                </tr>
                {group.events.map((code) => (
                  <tr key={code}>
                    <th scope="row" className={styles.matrixEvent}>
                      {eventName(code)}
                    </th>
                    {columns.map((column) => (
                      <td
                        key={String(column.id)}
                        className={styles.matrixCell}
                        data-column={column.name}
                      >
                        <Checkbox
                          isSelected={enabled.get(cellKey(code, column.id)) === true}
                          onChange={(selected) => {
                            change.mutate([{ event: code, channel: column.id, enabled: selected }]);
                          }}
                        >
                          <span className="visually-hidden">
                            {t('notifications.matrix.cell', {
                              event: eventName(code),
                              column: column.name,
                            })}
                          </span>
                        </Checkbox>
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            );
          })}
        </table>
      </div>
    </div>
  );
}
