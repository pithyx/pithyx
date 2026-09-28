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
import { addLayouts, poolLayout, type AddLayout } from './layouts';
import {
  ConfirmNameField,
  isFinished,
  JobStatus,
  PlanPreview,
  RunButton,
  RunError,
  useRunPlan,
} from './PlanRun';
import styles from './Storage.module.css';
import { describeProblem, guardReason } from './shared';

export type PoolChange = 'pool.import' | 'pool.export' | 'pool.vdev.add' | 'pool.raidz.expand';

/**
 * Importing a previewed pool for use, exporting a pool, adding disks as a new vdev and expanding a raidz vdev by one
 * disk. Each shows the exact commands first; where the daemon says so (an irreversible or mismatched vdev, datasets
 * that get `canmount=noauto`), the pool's name is typed before the commands can run.
 */
export function PoolChangeDialog({
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
  action: PoolChange;
  disks: StorageDisk[];
  onClose: () => void;
}) {
  const { t } = useTranslation('storage');
  const key = changeKey(action, pool);
  const [selected, setSelected] = useState<readonly string[]>([]);
  const [confirmations, setConfirmations] = useState<Record<string, string>>({});
  const [layout, setLayout] = useState<AddLayout | null>(null);
  const [confirmName, setConfirmName] = useState('');
  // Once a preview asked for the name, the field stays while the user types it.
  const [nameAsked, setNameAsked] = useState(action === 'pool.raidz.expand');
  const layouts = addLayouts(selected.length);
  const matching = poolLayout(pool);
  const effectiveLayout =
    layout !== null && layouts.includes(layout)
      ? layout
      : matching !== null && layouts.includes(matching)
        ? matching
        : (layouts.at(-1) ?? null);
  const plan = useMutation({
    mutationFn: () =>
      internalCall(app, 'internal.admin.storage.plan', {
        action,
        pool: pool.name,
        ...(vdev === undefined ? {} : { vdev: vdev.vdevGuid }),
        ...(action === 'pool.vdev.add' && effectiveLayout !== null
          ? { layout: effectiveLayout }
          : {}),
        ...(selected.length === 0
          ? {}
          : {
              diskIds: [...selected],
              confirmations: Object.fromEntries(
                Object.entries(confirmations).filter(([id]) => selected.includes(id)),
              ),
            }),
        ...(confirmName === '' ? {} : { confirmName }),
      }),
    onSuccess: (preview) => {
      if (preview.requiresConfirmName) {
        setNameAsked(true);
      }
    },
  });
  const { run, job } = useRunPlan(app);
  const needsDisks = action === 'pool.vdev.add' || action === 'pool.raidz.expand';
  const ready =
    !needsDisks ||
    (action === 'pool.raidz.expand' ? selected.length === 1 : effectiveLayout !== null);

  function choose(ids: readonly string[]) {
    setSelected(ids);
    plan.reset();
  }

  return (
    <Dialog
      isOpen
      isDismissable={job === null || isFinished(job)}
      tone={action === 'pool.import' ? 'default' : 'danger'}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t(`change.${key}.title`, { pool: pool.name, vdev: vdev?.name ?? '' })}
      actions={
        job !== null ? (
          <Button variant="primary" isDisabled={!isFinished(job)} onPress={onClose}>
            {t('create.close')}
          </Button>
        ) : (
          <>
            <Button onPress={onClose}>{t('create.close')}</Button>
            {plan.data?.planId ? (
              <RunButton
                preview={plan.data}
                label={t(`change.${key}.run`)}
                run={run}
                job={job}
                danger={action !== 'pool.import'}
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
        <p>{t(`change.${key}.explain`, { pool: pool.name, vdev: vdev?.name ?? '' })}</p>
        {needsDisks && (
          <DiskChoice
            disks={disks}
            multiple={action === 'pool.vdev.add'}
            selected={selected}
            confirmations={confirmations}
            onSelect={choose}
            onConfirm={(id, serial) => {
              setConfirmations({ ...confirmations, [id]: serial });
              plan.reset();
            }}
          />
        )}
        {action === 'pool.vdev.add' && selected.length > 0 && (
          <fieldset className={styles.diskChoice}>
            <legend className={styles.fieldLabel}>{t('create.layout')}</legend>
            {layouts.map((option) => (
              <label key={option} className={styles.check}>
                <input
                  type="radio"
                  name="layout"
                  checked={effectiveLayout === option}
                  onChange={() => {
                    setLayout(option);
                    plan.reset();
                  }}
                />
                <span>{t(`change.layouts.${option}`, { count: selected.length })}</span>
              </label>
            ))}
          </fieldset>
        )}
        {nameAsked && (
          <ConfirmNameField
            pool={pool.name}
            value={confirmName}
            isReadOnly={job !== null}
            onChange={(value) => {
              setConfirmName(value);
              if (plan.data?.planId) {
                plan.reset();
              }
            }}
          />
        )}
        {plan.isError && <ErrorNotice error={describeProblem(plan.error)} />}
        {plan.data !== undefined && (
          <PlanPreview
            preview={plan.data}
            disks={disks}
            datasetsLabel={
              action === 'pool.import'
                ? t('change.import.mounted')
                : action === 'pool.export'
                  ? t('change.export.datasets')
                  : undefined
            }
          />
        )}
        <RunError run={run} />
        {job !== null && <JobStatus job={job} />}
      </div>
    </Dialog>
  );
}

function changeKey(action: PoolChange, pool: Pool) {
  switch (action) {
    case 'pool.import':
      return 'import';
    case 'pool.export':
      return pool.readOnly ? 'discard' : 'export';
    case 'pool.vdev.add':
      return 'add';
    case 'pool.raidz.expand':
      return 'expand';
  }
}

/** Disks that may be used, with the serial field for one that holds data, as in the pool wizard. */
function DiskChoice({
  disks,
  multiple,
  selected,
  confirmations,
  onSelect,
  onConfirm,
}: {
  disks: StorageDisk[];
  multiple: boolean;
  selected: readonly string[];
  confirmations: Record<string, string>;
  onSelect: (ids: readonly string[]) => void;
  onConfirm: (id: string, serial: string) => void;
}) {
  const { t, i18n } = useTranslation('storage');
  const usable = disks.filter(({ guard }) => guard.decision !== 'refused');
  return (
    <fieldset className={styles.diskChoice}>
      <legend className={styles.fieldLabel}>{t(multiple ? 'create.disks' : 'actions.disk')}</legend>
      {usable.length === 0 && <p className={styles.muted}>{t('create.noDisks')}</p>}
      {usable.map(({ disk, guard }) => (
        <div key={disk.id} className={styles.diskOption}>
          <label className={styles.check}>
            <input
              type={multiple ? 'checkbox' : 'radio'}
              name="disk"
              checked={selected.includes(disk.id)}
              onChange={(event) => {
                onSelect(
                  !multiple
                    ? [disk.id]
                    : event.target.checked
                      ? [...selected, disk.id]
                      : selected.filter((id) => id !== disk.id),
                );
              }}
            />
            <span className={styles.mono}>{disk.name}</span>
            <span className={styles.muted}>
              {disk.serial ?? ''} · {formatBytes(disk.sizeBytes, i18n.language)}
            </span>
          </label>
          {guard.decision === 'needsSerialConfirmation' && selected.includes(disk.id) && (
            <TextField
              label={t('create.confirm', { disk: disk.name, serial: disk.serial ?? '' })}
              description={guardReason(t, guard)}
              value={confirmations[disk.id] ?? ''}
              onChange={(value) => {
                onConfirm(disk.id, value);
              }}
              autoCapitalize="none"
              autoComplete="off"
            />
          )}
        </div>
      ))}
    </fieldset>
  );
}
