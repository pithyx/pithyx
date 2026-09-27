import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { Badge } from '../../ui/Controls';
import { Dialog } from '../../ui/Dialog';
import { IconCamera } from '../../ui/icons';
import { TextField } from '../../ui/TextField';
import {
  internalCall,
  type Dataset,
  type Snapshot,
  type SnapshotScheduleList,
} from '../sdk/internal';
import styles from './Settings.module.css';
import { describeProblem } from './StoragePage';

type Kind = 'hourly' | 'daily' | 'weekly' | 'monthly';
type Pending =
  | { kind: 'delete'; snapshot: Snapshot }
  | { kind: 'rollback'; snapshot: Snapshot; newer: Snapshot[] };

function snapshotsKey(dataset: string) {
  return ['settings', 'storage', 'snapshots', dataset] as const;
}

/**
 * Snapshots of one dataset at a time: the list, taking one now, the schedules with their counts, holds, deleting a
 * snapshot Pithyx took after its name was typed, and rolling back after the dataset name was typed with every newer
 * snapshot listed.
 */
export function SnapshotsSection({ app, datasets }: { app: PithyxApp; datasets: Dataset[] }) {
  const { t } = useTranslation('settings');
  const filesystems = datasets.filter((dataset) => dataset.type === 'filesystem');
  const [chosen, setChosen] = useState<string | null>(null);
  const dataset = filesystems.find((candidate) => candidate.name === chosen) ?? filesystems[0];
  return (
    <section aria-labelledby="storage-snapshots" className={styles.section}>
      <h3 id="storage-snapshots" className={styles.sectionTitle}>
        {t('storage.snapshots.title')}
      </h3>
      <p className={styles.muted}>{t('storage.snapshots.intro')}</p>
      {dataset === undefined ? (
        <p className={styles.muted}>{t('storage.snapshots.noDatasets')}</p>
      ) : (
        <>
          <label className={styles.selectField}>
            <span className={styles.fieldLabel}>{t('storage.snapshots.dataset')}</span>
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
          <DatasetSnapshots key={dataset.name} app={app} dataset={dataset.name} />
        </>
      )}
    </section>
  );
}

function DatasetSnapshots({ app, dataset }: { app: PithyxApp; dataset: string }) {
  const { t, i18n } = useTranslation('settings');
  const client = useQueryClient();
  const [pending, setPending] = useState<Pending | null>(null);
  const [typed, setTyped] = useState('');
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

  function close() {
    setPending(null);
    setTyped('');
  }

  function confirmPending() {
    const current = pending;
    close();
    if (current?.kind === 'delete') {
      action.mutate(() =>
        internalCall(app, 'internal.admin.storage.snapshots.delete', {
          dataset,
          name: current.snapshot.name,
        }),
      );
    } else if (current?.kind === 'rollback') {
      action.mutate(() =>
        internalCall(app, 'internal.admin.storage.snapshots.rollback', {
          dataset,
          name: current.snapshot.name,
          confirmDataset: typed,
          discardNewer: current.newer.map((snapshot) => snapshot.name),
        }),
      );
    }
  }

  const expected = pending?.kind === 'delete' ? pending.snapshot.name : dataset;
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
          {t('storage.snapshots.take')}
        </Button>
      </div>
      {action.isError && <ErrorNotice error={describeProblem(action.error)} />}
      <Schedules app={app} dataset={dataset} />
      {list.isError && <ErrorNotice error={describeProblem(list.error)} />}
      {list.data?.snapshots.length === 0 && (
        <p className={styles.muted}>{t('storage.snapshots.empty')}</p>
      )}
      {snapshots.length > 0 && (
        <table className={styles.table}>
          <thead>
            <tr>
              <th scope="col">{t('storage.snapshots.name')}</th>
              <th scope="col">{t('storage.snapshots.created')}</th>
              <th scope="col" className={`${styles.number} ${styles.optional}`}>
                {t('storage.snapshots.used')}
              </th>
              <th scope="col">
                <span className={styles.visuallyHidden}>{t('storage.snapshots.actions')}</span>
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
                      <Badge tone="neutral">{t('storage.snapshots.foreign')}</Badge>
                    ) : (
                      <Badge tone="accent">
                        {t(`storage.snapshots.kinds.${snapshot.pithyxKind as Kind | 'manual'}`)}
                      </Badge>
                    )}
                    {snapshot.holds > 0 && (
                      <Badge tone="highlight">{t('storage.snapshots.held')}</Badge>
                    )}
                  </span>
                </td>
                <td>{app.format.dateTime(snapshot.createdAt)}</td>
                <td className={`${styles.number} ${styles.optional}`}>
                  {formatBytes(snapshot.usedBytes, i18n.language)}
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
                      {snapshot.holds > 0
                        ? t('storage.snapshots.release')
                        : t('storage.snapshots.hold')}
                    </Button>
                    <Button
                      size="sm"
                      variant="quiet"
                      isDisabled={action.isPending}
                      onPress={() => {
                        setPending({
                          kind: 'rollback',
                          snapshot,
                          newer: (list.data?.snapshots ?? []).filter(
                            (other) => other.createTxg > snapshot.createTxg,
                          ),
                        });
                      }}
                    >
                      {t('storage.snapshots.rollback')}
                    </Button>
                    {snapshot.pithyxKind !== null && snapshot.holds === 0 && (
                      <Button
                        size="sm"
                        variant="quiet"
                        isDisabled={action.isPending}
                        onPress={() => {
                          setPending({ kind: 'delete', snapshot });
                        }}
                      >
                        {t('storage.snapshots.delete')}
                      </Button>
                    )}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <Dialog
        isOpen={pending !== null}
        onOpenChange={(open) => {
          if (!open) {
            close();
          }
        }}
        role="alertdialog"
        tone="danger"
        title={
          pending?.kind === 'rollback'
            ? t('storage.snapshots.rollbackTitle', { dataset })
            : t('storage.snapshots.deleteTitle')
        }
        actions={
          <>
            <Button onPress={close}>{t('storage.snapshots.cancel')}</Button>
            <Button variant="danger" isDisabled={typed !== expected} onPress={confirmPending}>
              {pending?.kind === 'rollback'
                ? t('storage.snapshots.rollbackConfirm')
                : t('storage.snapshots.deleteConfirm')}
            </Button>
          </>
        }
      >
        <div className={styles.form}>
          {pending?.kind === 'rollback' ? (
            <>
              <p>
                {t('storage.snapshots.rollbackBody', {
                  name: pending.snapshot.name,
                  date: app.format.dateTime(pending.snapshot.createdAt),
                })}
              </p>
              {pending.newer.length > 0 && (
                <>
                  <p>{t('storage.snapshots.rollbackNewer', { count: pending.newer.length })}</p>
                  <ul className={styles.commands}>
                    {pending.newer.map((snapshot) => (
                      <li key={snapshot.name} className={styles.mono}>
                        {snapshot.name}
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </>
          ) : (
            <p>{t('storage.snapshots.deleteBody', { name: pending?.snapshot.name ?? '' })}</p>
          )}
          <TextField
            label={
              pending?.kind === 'rollback'
                ? t('storage.snapshots.typeDataset', { dataset })
                : t('storage.snapshots.typeName')
            }
            value={typed}
            onChange={setTyped}
            autoCapitalize="none"
            inputClassName={styles.mono}
          />
        </div>
      </Dialog>
    </div>
  );
}

/** The four schedules of a dataset; saved together. */
function Schedules({ app, dataset }: { app: PithyxApp; dataset: string }) {
  const { t } = useTranslation('settings');
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
      <legend className={styles.fieldLabel}>{t('storage.snapshots.schedules')}</legend>
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
            {t(`storage.snapshots.kinds.${schedule.kind as Kind}`)}
          </label>
          <label className={styles.keep}>
            <span>{t('storage.snapshots.keep')}</span>
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
      <p className={styles.muted}>{t('storage.snapshots.schedulesHint')}</p>
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
          {t('storage.snapshots.save')}
        </Button>
      </div>
    </fieldset>
  );
}
