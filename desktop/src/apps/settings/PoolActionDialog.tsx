import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { TextField } from '../../ui/TextField';
import { internalCall, type Pool, type StorageDisk, type Vdev } from '../sdk/internal';
import { isFinished, JobStatus, PlanPreview, RunButton, RunError, useRunPlan } from './PlanRun';
import styles from './Settings.module.css';
import { describeProblem, guardReason } from './StoragePage';

export type PoolAction =
  | 'pool.replace'
  | 'pool.spare.add'
  | 'pool.spare.remove'
  | 'pool.offline'
  | 'pool.online'
  | 'pool.destroy'
  | 'dataset.create';

const actionKey = {
  'pool.replace': 'replace',
  'pool.spare.add': 'spareAdd',
  'pool.spare.remove': 'spareRemove',
  'pool.offline': 'offline',
  'pool.online': 'online',
  'pool.destroy': 'destroy',
  'dataset.create': 'datasetCreate',
} as const;

/**
 * One repair or maintenance action on a pool through a command plan: pick a disk where the action needs one (a new
 * disk is never used as a replacement without its serial typed), type the pool's name to destroy it, see the exact
 * commands, run them.
 */
export function PoolActionDialog({
  app,
  pool,
  vdev,
  action,
  disks,
  onClose,
}: {
  app: PithyxApp;
  pool: Pool;
  vdev?: Vdev;
  action: PoolAction;
  disks: StorageDisk[];
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation('settings');
  const key = actionKey[action];
  const needsDisk = action === 'pool.replace' || action === 'pool.spare.add';
  const [diskId, setDiskId] = useState<string | null>(null);
  const [serial, setSerial] = useState('');
  const [confirmName, setConfirmName] = useState('');
  const [datasetName, setDatasetName] = useState('');
  const usable = disks.filter(({ guard }) => guard.decision !== 'refused');
  const chosen = usable.find(({ disk }) => disk.id === diskId);
  const plan = useMutation({
    mutationFn: () =>
      internalCall(app, 'internal.admin.storage.plan', {
        action,
        pool: pool.name,
        ...(vdev === undefined ? {} : { vdev: vdev.vdevGuid }),
        ...(diskId === null ? {} : { diskIds: [diskId], confirmations: { [diskId]: serial } }),
        ...(action === 'pool.destroy' ? { confirmName } : {}),
        ...(action === 'dataset.create' ? { name: datasetName.trim() } : {}),
      }),
  });
  const { run, job } = useRunPlan(app);
  const askSerial =
    chosen !== undefined &&
    (action === 'pool.replace' || chosen.guard.decision === 'needsSerialConfirmation');
  const ready =
    (!needsDisk || diskId !== null) && (action !== 'pool.destroy' || confirmName === pool.name);

  return (
    <Dialog
      isOpen
      isDismissable={job === null || isFinished(job)}
      tone={action === 'pool.destroy' ? 'danger' : 'default'}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t(`storage.actions.${key}.title`, { pool: pool.name, device: vdev?.name ?? '' })}
      actions={
        job !== null ? (
          <Button variant="primary" isDisabled={!isFinished(job)} onPress={onClose}>
            {t('storage.create.close')}
          </Button>
        ) : (
          <>
            <Button onPress={onClose}>{t('storage.create.close')}</Button>
            {plan.data?.planId ? (
              <RunButton
                preview={plan.data}
                label={t(`storage.actions.${key}.run`)}
                run={run}
                job={job}
                danger={action === 'pool.destroy'}
              />
            ) : (
              <Button
                variant="primary"
                isDisabled={!ready || plan.isPending}
                onPress={() => {
                  plan.mutate();
                }}
              >
                {t('storage.create.preview')}
              </Button>
            )}
          </>
        )
      }
    >
      <div className={styles.form}>
        <p>{t(`storage.actions.${key}.explain`, { pool: pool.name, device: vdev?.name ?? '' })}</p>
        {needsDisk && (
          <fieldset className={styles.diskChoice}>
            <legend className={styles.fieldLabel}>{t('storage.actions.disk')}</legend>
            {usable.length === 0 && <p className={styles.muted}>{t('storage.create.noDisks')}</p>}
            {usable.map(({ disk }) => (
              <label key={disk.id} className={styles.check}>
                <input
                  type="radio"
                  name="disk"
                  checked={diskId === disk.id}
                  onChange={() => {
                    setDiskId(disk.id);
                    setSerial('');
                    plan.reset();
                  }}
                />
                <span className={styles.mono}>{disk.name}</span>
                <span className={styles.muted}>
                  {disk.serial ?? ''} · {formatBytes(disk.sizeBytes, i18n.language)}
                </span>
              </label>
            ))}
          </fieldset>
        )}
        {askSerial && (
          <TextField
            label={t('storage.create.confirm', {
              disk: chosen.disk.name,
              serial: chosen.disk.serial ?? '',
            })}
            description={
              action === 'pool.replace'
                ? t('storage.actions.replace.serialHint')
                : guardReason(t, chosen.guard)
            }
            value={serial}
            onChange={(value) => {
              setSerial(value);
              plan.reset();
            }}
            autoCapitalize="none"
            autoComplete="off"
          />
        )}
        {action === 'dataset.create' && (
          <TextField
            label={t('storage.actions.datasetCreate.name')}
            description={t('storage.actions.datasetCreate.nameHint', { pool: pool.name })}
            value={datasetName}
            onChange={(value) => {
              setDatasetName(value);
              plan.reset();
            }}
            autoCapitalize="none"
            autoComplete="off"
          />
        )}
        {action === 'pool.destroy' && (
          <TextField
            label={t('storage.actions.destroy.confirm', { pool: pool.name })}
            value={confirmName}
            onChange={(value) => {
              setConfirmName(value);
              plan.reset();
            }}
            autoCapitalize="none"
            autoComplete="off"
          />
        )}
        {plan.isError && <ErrorNotice error={describeProblem(plan.error)} />}
        {plan.data !== undefined && <PlanPreview preview={plan.data} disks={disks} />}
        <RunError run={run} />
        {job !== null && <JobStatus job={job} />}
      </div>
    </Dialog>
  );
}
