import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button, Dialog, TextField } from '@pithyx/ui';
import { internalCall, type Job, type Pool, type StorageDisk, type Vdev } from '../sdk/internal';
import { isFinished, JobStatus, PlanPreview, RunButton, RunError, useRunPlan } from './PlanRun';
import styles from './Storage.module.css';
import { describeProblem, guardReason } from './shared';
import { useFormatLocale } from '../../i18n';

export type PoolAction =
  | 'pool.replace'
  | 'pool.spare.add'
  | 'pool.spare.remove'
  | 'pool.offline'
  | 'pool.online'
  | 'pool.destroy';

const actionKey = {
  'pool.replace': 'replace',
  'pool.spare.add': 'spareAdd',
  'pool.spare.remove': 'spareRemove',
  'pool.offline': 'offline',
  'pool.online': 'online',
  'pool.destroy': 'destroy',
} as const;

/**
 * One repair or maintenance action on a pool through a command plan: pick a disk where the action needs one (a new
 * disk is never used as a replacement without its serial typed), type the pool's name to destroy it, see the exact
 * commands, run them. A destroy runs in the background: the dialog closes as soon as its job started, and the
 * notifications say when it is done or why it failed. Other actions can be closed while they run, too.
 */
export function PoolActionDialog({
  app,
  pool,
  vdev,
  action,
  disks,
  onClose,
  onStarted,
}: {
  app: PithyxApp;
  pool: Pool;
  vdev?: Vdev;
  action: PoolAction;
  disks: StorageDisk[];
  onClose: () => void;
  /** Called once the plan's job started, before a destroy closes the dialog. */
  onStarted?: (job: Job) => void;
}) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const key = actionKey[action];
  const needsDisk = action === 'pool.replace' || action === 'pool.spare.add';
  const [diskId, setDiskId] = useState<string | null>(null);
  const [serial, setSerial] = useState('');
  const [confirmName, setConfirmName] = useState('');
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
      }),
  });
  const { run, job } = useRunPlan(app, (started) => {
    onStarted?.(started);
    if (action === 'pool.destroy') {
      onClose();
    }
  });
  const askSerial =
    chosen !== undefined &&
    (action === 'pool.replace' || chosen.guard.decision === 'needsSerialConfirmation');
  const ready =
    (!needsDisk || diskId !== null) && (action !== 'pool.destroy' || confirmName === pool.name);

  return (
    <Dialog
      isOpen
      isDismissable
      tone={action === 'pool.destroy' ? 'danger' : 'default'}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t(`actions.${key}.title`, { pool: pool.name, device: vdev?.name ?? '' })}
      actions={
        job !== null ? (
          <Button variant="primary" onPress={onClose}>
            {t('create.close')}
          </Button>
        ) : (
          <>
            <Button onPress={onClose}>{t('create.close')}</Button>
            {plan.data?.planId ? (
              <RunButton
                preview={plan.data}
                label={t(`actions.${key}.run`)}
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
                {t('create.preview')}
              </Button>
            )}
          </>
        )
      }
    >
      <div className={styles.form}>
        <p>{t(`actions.${key}.explain`, { pool: pool.name, device: vdev?.name ?? '' })}</p>
        {needsDisk && (
          <fieldset className={styles.diskChoice}>
            <legend className={styles.fieldLabel}>{t('actions.disk')}</legend>
            {usable.length === 0 && <p className={styles.muted}>{t('create.noDisks')}</p>}
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
                  {disk.serial ?? ''} · {formatBytes(disk.sizeBytes, locale)}
                </span>
              </label>
            ))}
          </fieldset>
        )}
        {askSerial && (
          <TextField
            label={t('create.confirm', {
              disk: chosen.disk.name,
              serial: chosen.disk.serial ?? '',
            })}
            description={
              action === 'pool.replace'
                ? t('actions.replace.serialHint')
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
        {action === 'pool.destroy' && (
          <TextField
            label={t('actions.destroy.confirm', { pool: pool.name })}
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
        {job !== null && !isFinished(job) && <p className={styles.muted}>{t('run.background')}</p>}
      </div>
    </Dialog>
  );
}
