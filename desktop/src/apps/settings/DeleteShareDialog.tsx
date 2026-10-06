import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button, Dialog, TextField } from '@pithyx/ui';
import { internalCall, type ShareDetails } from '../sdk/internal';
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

type DataChoice = 'keep' | 'destroy';

/**
 * Deletes a shared folder after its name was typed and the data choice was made: keep (the dataset or folder stays)
 * or destroy (the dataset with every snapshot, all listed before). Both run as a plan.
 */
export function DeleteShareDialog({
  app,
  share,
  onClose,
}: {
  app: PithyxApp;
  share: ShareDetails;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const [data, setData] = useState<DataChoice | null>(null);
  const [confirmName, setConfirmName] = useState('');
  const plan = useMutation({
    mutationFn: () =>
      internalCall(app, 'internal.admin.shares.plan', {
        action: 'share.delete',
        shareId: share.id,
        confirmName,
        ...(data === null ? {} : { data }),
      }),
  });
  const { run, job } = useRunPlan(app);
  const folderOnly = share.dataset === null;
  const ready = data !== null && confirmName.trim() === share.name;
  const location = share.dataset ?? share.path;
  const choose = (choice: DataChoice) => {
    setData(choice);
    plan.reset();
  };

  return (
    <Dialog
      isOpen
      isDismissable={job === null || isFinished(job)}
      tone={data === 'destroy' ? 'danger' : 'default'}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('shares.deleteDialog.title', { name: share.name })}
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
                label={t('shares.deleteDialog.run')}
                run={run}
                job={job}
                danger={data === 'destroy'}
              />
            ) : (
              <Button
                variant="primary"
                isDisabled={!ready || plan.isPending}
                onPress={() => {
                  plan.mutate();
                }}
              >
                {t('shares.deleteDialog.preview')}
              </Button>
            )}
          </>
        )
      }
    >
      <div className={styles.form}>
        <fieldset className={styles.choice} disabled={job !== null}>
          <legend className={styles.fieldLabel}>{t('shares.deleteDialog.choice')}</legend>
          <label className={styles.option}>
            <input
              type="radio"
              name="data"
              checked={data === 'keep'}
              onChange={() => {
                choose('keep');
              }}
            />
            <span>
              {t('shares.deleteDialog.keep')}
              <span className={styles.optionHint}>
                {folderOnly
                  ? t('shares.deleteDialog.keepFolder', { path: share.path })
                  : t('shares.deleteDialog.keepDataset', { dataset: location })}
              </span>
            </span>
          </label>
          <label className={styles.option}>
            <input
              type="radio"
              name="data"
              checked={data === 'destroy'}
              disabled={folderOnly}
              onChange={() => {
                choose('destroy');
              }}
            />
            <span>
              {t('shares.deleteDialog.destroy')}
              <span className={styles.optionHint}>
                {folderOnly
                  ? t('shares.deleteDialog.folderOnly')
                  : t('shares.deleteDialog.destroyHint')}
              </span>
            </span>
          </label>
        </fieldset>
        <TextField
          label={t('shares.deleteDialog.confirm', { name: share.name })}
          value={confirmName}
          onChange={(value) => {
            setConfirmName(value);
            plan.reset();
          }}
          isDisabled={job !== null}
          autoCapitalize="none"
          autoComplete="off"
        />
        {plan.isError && <ErrorNotice error={describeProblem(plan.error)} />}
        {plan.data !== undefined &&
          (plan.data.commands?.length === 0 ? (
            <p className={styles.preview} role="status">
              {t('shares.deleteDialog.nothingRuns', { location })}
            </p>
          ) : (
            <>
              {plan.data.datasets.length > 0 && (
                <div className={styles.gone}>
                  <p>{t('shares.deleteDialog.gone')}</p>
                  <ul className={styles.mono}>
                    {plan.data.datasets.map((entry) => (
                      <li key={entry}>{entry}</li>
                    ))}
                  </ul>
                </div>
              )}
              {/* The storage preview would describe its dataset list as what a pool destroy takes along. */}
              <PlanPreview preview={{ ...plan.data, datasets: [] }} disks={[]} />
            </>
          ))}
        <RunError run={run} />
        {job !== null && <JobStatus job={job} />}
      </div>
    </Dialog>
  );
}
