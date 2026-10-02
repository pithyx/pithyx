import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { Badge } from '../../ui/Controls';
import { IconCamera } from '../../ui/icons';
import { internalCall, type Dataset, type SnapshotScheduleList } from '../sdk/internal';
import { DatasetDialog, type DatasetAction } from './DatasetDialog';
import styles from './Storage.module.css';
import { describeProblem, storageKey, usePools } from './shared';
import { useFormatLocale } from '../../i18n';

type Kind = 'hourly' | 'daily' | 'weekly' | 'monthly';

function snapshotsKey(dataset: string) {
  return [...storageKey, 'snapshots', dataset] as const;
}

/**
 * Snapshots of one dataset at a time: the list, taking one now, the schedules with their counts, holds, and the
 * command plans that delete a snapshot Pithyx took or roll back to one after the dataset name was typed, with every
 * newer snapshot listed.
 */
export function SnapshotsSection({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('storage');
  const pools = usePools(app);
  const filesystems = (pools.data?.datasets ?? []).filter(
    (dataset) => dataset.type === 'filesystem',
  );
  const [chosen, setChosen] = useState<string | null>(null);
  const dataset = filesystems.find((candidate) => candidate.name === chosen) ?? filesystems[0];
  return (
    <div className={styles.storage}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('nav.snapshots')}</h2>
          <p className={styles.muted}>{t('snapshots.intro')}</p>
        </div>
      </header>
      {pools.isError && <ErrorNotice error={describeProblem(pools.error)} />}
      {pools.data !== undefined && dataset === undefined && (
        <p className={styles.muted}>{t('snapshots.noDatasets')}</p>
      )}
      {dataset !== undefined && (
        <section aria-label={t('snapshots.title')} className={styles.section}>
          <label className={styles.selectField}>
            <span className={styles.fieldLabel}>{t('snapshots.dataset')}</span>
            <select
              className={styles.select}
              value={dataset.name}
              onChange={(event) => {
                setChosen(event.currentTarget.value);
              }}
            >
              {filesystems.map((candidate) => (
                <option key={candidate.name} value={candidate.name}>
                  {candidate.name}
                </option>
              ))}
            </select>
          </label>
          <DatasetSnapshots key={dataset.name} app={app} dataset={dataset} />
        </section>
      )}
    </div>
  );
}

function DatasetSnapshots({ app, dataset: target }: { app: PithyxApp; dataset: Dataset }) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const client = useQueryClient();
  const dataset = target.name;
  const [pending, setPending] = useState<DatasetAction | null>(null);
  const list = useQuery({
    queryKey: [...snapshotsKey(dataset), 'list'],
    queryFn: () => internalCall(app, 'internal.admin.storage.snapshots.list', { dataset }),
  });
  const reload = () => client.invalidateQueries({ queryKey: [...snapshotsKey(dataset), 'list'] });
  const action = useMutation({
    mutationFn: (run: () => Promise<unknown>) => run(),
    onSettled: reload,
  });
  const snapshots = [...(list.data?.snapshots ?? [])].reverse();

  return (
    <div className={styles.snapshots}>
      <div className={styles.snapshotBar}>
        <Button
          size="sm"
          isDisabled={action.isPending}
          onPress={() => {
            action.mutate(() =>
              internalCall(app, 'internal.admin.storage.snapshots.create', { dataset }),
            );
          }}
        >
          <IconCamera size={16} aria-hidden />
          {t('snapshots.take')}
        </Button>
      </div>
      {action.isError && <ErrorNotice error={describeProblem(action.error)} />}
      <Schedules app={app} dataset={dataset} />
      {list.isError && <ErrorNotice error={describeProblem(list.error)} />}
      {list.data?.snapshots.length === 0 && <p className={styles.muted}>{t('snapshots.empty')}</p>}
      {snapshots.length > 0 && (
        <table className={styles.table}>
          <thead>
            <tr>
              <th scope="col">{t('snapshots.name')}</th>
              <th scope="col">{t('snapshots.created')}</th>
              <th scope="col" className={`${styles.number} ${styles.optional}`}>
                {t('snapshots.used')}
              </th>
              <th scope="col">
                <span className={styles.visuallyHidden}>{t('snapshots.actions')}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {snapshots.map((snapshot) => (
              <tr key={snapshot.name}>
                <td>
                  <span className={styles.snapshotName}>
                    <span className={styles.mono}>{snapshot.name}</span>
                    {snapshot.pithyxKind === null ? (
                      <Badge tone="neutral">{t('snapshots.foreign')}</Badge>
                    ) : (
                      <Badge tone="accent">
                        {t(`snapshots.kinds.${snapshot.pithyxKind as Kind | 'manual'}`)}
                      </Badge>
                    )}
                    {snapshot.holds > 0 && <Badge tone="highlight">{t('snapshots.held')}</Badge>}
                  </span>
                </td>
                <td>{app.format.dateTime(snapshot.createdAt)}</td>
                <td className={`${styles.number} ${styles.optional}`}>
                  {formatBytes(snapshot.usedBytes, locale)}
                </td>
                <td>
                  <span className={styles.snapshotActions}>
                    <Button
                      size="sm"
                      variant="quiet"
                      isDisabled={action.isPending}
                      onPress={() => {
                        action.mutate(() =>
                          internalCall(
                            app,
                            snapshot.holds > 0
                              ? 'internal.admin.storage.snapshots.release'
                              : 'internal.admin.storage.snapshots.hold',
                            { dataset, name: snapshot.name },
                          ),
                        );
                      }}
                    >
                      {snapshot.holds > 0 ? t('snapshots.release') : t('snapshots.hold')}
                    </Button>
                    <Button
                      size="sm"
                      variant="quiet"
                      isDisabled={action.isPending}
                      onPress={() => {
                        setPending({
                          kind: 'rollback',
                          dataset: target,
                          snapshot,
                          newer: (list.data?.snapshots ?? []).filter(
                            (other) => other.createTxg > snapshot.createTxg,
                          ).length,
                        });
                      }}
                    >
                      {t('snapshots.rollback')}
                    </Button>
                    {snapshot.pithyxKind !== null && snapshot.holds === 0 && (
                      <Button
                        size="sm"
                        variant="quiet"
                        isDisabled={action.isPending}
                        onPress={() => {
                          setPending({ kind: 'snapshotDestroy', dataset: target, snapshot });
                        }}
                      >
                        {t('snapshots.delete')}
                      </Button>
                    )}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {pending !== null && (
        <DatasetDialog
          app={app}
          action={pending}
          onClose={() => {
            setPending(null);
            void reload();
          }}
        />
      )}
    </div>
  );
}

/** The four schedules of a dataset; saved together. */
function Schedules({ app, dataset }: { app: PithyxApp; dataset: string }) {
  const { t } = useTranslation('storage');
  const client = useQueryClient();
  const saved = useQuery({
    queryKey: [...snapshotsKey(dataset), 'schedules'],
    queryFn: () => internalCall(app, 'internal.admin.storage.snapshots.schedules', { dataset }),
  });
  const [draft, setDraft] = useState<SnapshotScheduleList | null>(null);
  const current = draft ?? saved.data;
  const save = useMutation({
    mutationFn: (schedules: SnapshotScheduleList) =>
      internalCall(app, 'internal.admin.storage.snapshots.setSchedules', schedules),
    onSuccess: (result) => {
      client.setQueryData([...snapshotsKey(dataset), 'schedules'], result);
      setDraft(null);
      void client.invalidateQueries({ queryKey: [...snapshotsKey(dataset), 'list'] });
    },
  });
  if (current === undefined) {
    return saved.isError ? <ErrorNotice error={describeProblem(saved.error)} /> : null;
  }

  function change(kind: string, update: { enabled?: boolean; keep?: number }) {
    if (current === undefined) {
      return;
    }
    setDraft({
      ...current,
      schedules: current.schedules.map((schedule) =>
        schedule.kind === kind ? { ...schedule, ...update } : schedule,
      ),
    });
  }

  return (
    <fieldset className={styles.schedules}>
      <legend className={styles.fieldLabel}>{t('snapshots.schedules')}</legend>
      {current.schedules.map((schedule) => (
        <div key={schedule.kind} className={styles.schedule}>
          <label className={styles.check}>
            <input
              type="checkbox"
              checked={schedule.enabled}
              onChange={(event) => {
                change(schedule.kind, { enabled: event.currentTarget.checked });
              }}
            />
            {t(`snapshots.kinds.${schedule.kind as Kind}`)}
          </label>
          <label className={styles.keep}>
            <span>{t('snapshots.keep')}</span>
            <input
              type="number"
              min={1}
              max={1000}
              className={styles.select}
              value={schedule.keep}
              onChange={(event) => {
                change(schedule.kind, { keep: event.currentTarget.valueAsNumber || 1 });
              }}
            />
          </label>
        </div>
      ))}
      <p className={styles.muted}>{t('snapshots.schedulesHint')}</p>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      <div>
        <Button
          size="sm"
          variant="primary"
          isDisabled={draft === null || save.isPending}
          onPress={() => {
            if (draft !== null) {
              save.mutate(draft);
            }
          }}
        >
          {t('snapshots.save')}
        </Button>
      </div>
    </fieldset>
  );
}
