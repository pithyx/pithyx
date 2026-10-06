import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button, Dialog, TextField } from '@pithyx/ui';
import { coreAppIds } from '../ids';
import { internalCall } from '../sdk/internal';
import {
  isFinished,
  JobStatus,
  PlanPreview,
  RunButton,
  RunError,
  useRunPlan,
} from '../storage/PlanRun';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';
import { sharePools, usePools } from './shares';

/**
 * Creates a shared folder as its own dataset: name, pool and description, then the exact command and the dataset
 * name the daemon derived from the name, then the run with its progress.
 */
export function CreateShareDialog({ app, onClose }: { app: PithyxApp; onClose: () => void }) {
  const { t } = useTranslation('settings');
  const pools = usePools(app);
  const choices = pools.data === undefined ? [] : sharePools(pools.data);
  const [name, setName] = useState('');
  const [chosenPool, setPool] = useState<string | null>(null);
  const [description, setDescription] = useState('');
  const pool = chosenPool ?? choices[0] ?? null;
  const plan = useMutation({
    mutationFn: () =>
      internalCall(app, 'internal.admin.shares.plan', {
        action: 'share.create',
        name,
        pool: pool ?? '',
        description,
      }),
  });
  const { run, job } = useRunPlan(app);
  const edit = (change: () => void) => {
    change();
    plan.reset();
  };
  const ready = name.trim() !== '' && pool !== null;

  return (
    <Dialog
      isOpen
      isDismissable={job === null || isFinished(job)}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('shares.createDialog.title')}
      actions={
        job !== null ? (
          <Button variant="primary" isDisabled={!isFinished(job)} onPress={onClose}>
            {t('shares.close')}
          </Button>
        ) : (
          <>
            <Button onPress={onClose}>{t('shares.close')}</Button>
            {plan.data?.planId ? (
              <RunButton
                preview={plan.data}
                label={t('shares.createDialog.run')}
                run={run}
                job={job}
              />
            ) : (
              <Button
                variant="primary"
                isDisabled={!ready || plan.isPending}
                onPress={() => {
                  plan.mutate();
                }}
              >
                {t('shares.createDialog.preview')}
              </Button>
            )}
          </>
        )
      }
    >
      <div className={styles.form}>
        <TextField
          label={t('shares.createDialog.name')}
          description={t('shares.createDialog.nameHint')}
          value={name}
          onChange={(value) => {
            edit(() => {
              setName(value);
            });
          }}
          isDisabled={job !== null}
          autoComplete="off"
          maxLength={64}
        />
        {pools.isError && <ErrorNotice error={describeProblem(pools.error)} />}
        {pools.data !== undefined && choices.length === 0 ? (
          <div className={styles.notice}>
            <p>{t('shares.createDialog.noPools')}</p>
            <Button
              size="sm"
              onPress={() => {
                void internalCall(app, 'internal.admin.openApp', { app: coreAppIds.storage });
              }}
            >
              {t('shares.createDialog.openStorage')}
            </Button>
          </div>
        ) : (
          <label className={styles.selectField}>
            <span className={styles.fieldLabel}>{t('shares.createDialog.pool')}</span>
            <select
              className={styles.select}
              value={pool ?? ''}
              disabled={job !== null || choices.length === 0}
              onChange={(event) => {
                edit(() => {
                  setPool(event.target.value);
                });
              }}
            >
              {choices.map((choice) => (
                <option key={choice} value={choice}>
                  {choice}
                </option>
              ))}
            </select>
          </label>
        )}
        <TextField
          label={t('shares.createDialog.description')}
          value={description}
          onChange={(value) => {
            edit(() => {
              setDescription(value);
            });
          }}
          isDisabled={job !== null}
          autoComplete="off"
          maxLength={200}
        />
        {plan.isError && <ErrorNotice error={describeProblem(plan.error)} />}
        {plan.data !== undefined && (
          <>
            {plan.data.datasets[0] !== undefined && (
              <p>{t('shares.createDialog.dataset', { dataset: plan.data.datasets[0] })}</p>
            )}
            {/* The storage preview would describe its dataset list as what a pool destroy takes along. */}
            <PlanPreview preview={{ ...plan.data, datasets: [] }} disks={[]} />
          </>
        )}
        <RunError run={run} />
        {job !== null && <JobStatus job={job} />}
      </div>
    </Dialog>
  );
}
