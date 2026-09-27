import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import {
  internalCall,
  onInternal,
  STORAGE_JOB,
  type Job,
  type StorageDisk,
  type StoragePlanPreview,
} from '../sdk/internal';
import styles from './Settings.module.css';
import { describeProblem, guardReason } from './StoragePage';

/** The commands of a plan, or why the plan cannot be made. */
export function PlanPreview({
  preview,
  disks,
}: {
  preview: StoragePlanPreview;
  disks: StorageDisk[];
}) {
  const { t } = useTranslation('settings');
  if (preview.commands === null) {
    return (
      <div className={styles.preview} role="status">
        <p>{t('storage.create.blocked')}</p>
        <ul>
          {preview.disks
            .filter((verdict) => verdict.decision !== 'allowed')
            .map((verdict) => (
              <li key={verdict.diskId}>
                <span className={styles.mono}>
                  {disks.find(({ disk }) => disk.id === verdict.diskId)?.disk.name ??
                    verdict.diskId}
                </span>
                : {guardReason(t, verdict)}
              </li>
            ))}
        </ul>
      </div>
    );
  }
  return (
    <div className={styles.preview} role="status">
      {preview.datasets.length > 0 && (
        <>
          <p>{t('storage.actions.datasets')}</p>
          <ul className={styles.mono}>
            {preview.datasets.map((dataset) => (
              <li key={dataset}>{dataset}</li>
            ))}
          </ul>
        </>
      )}
      <p>{t('storage.create.commands')}</p>
      <ol className={styles.commands}>
        {preview.commands.map((command, index) => (
          <li key={index}>
            <code>{[command.program, ...command.arguments].join(' ')}</code>
          </li>
        ))}
      </ol>
    </div>
  );
}

/** Runs a shown plan as a job and follows it until it ends. */
export function useRunPlan(app: PithyxApp) {
  const [started, setStarted] = useState<Job | null>(null);
  // Updates by job id: a short job can report its end before the run call returns its id.
  const [updates, setUpdates] = useState<Readonly<Record<number, Job>>>({});
  const run = useMutation({
    mutationFn: (planId: string) => internalCall(app, 'internal.admin.storage.run', { planId }),
    onSuccess: setStarted,
  });
  // The desktop forwards the job's topic; this follows the jobs this dialog started.
  useEffect(
    () =>
      onInternal(app, STORAGE_JOB, (data) => {
        const update = data as Job;
        setUpdates((current) => ({ ...current, [update.id]: update }));
      }),
    [app],
  );
  const job = started === null ? null : (updates[started.id] ?? started);
  return { run, job };
}

const FINISHED = new Set(['succeeded', 'failed', 'cancelled', 'interrupted']);

/** Where a running plan is; the error of a failed one with its request id. */
export function JobStatus({ job }: { job: Job }) {
  const { t } = useTranslation('settings');
  if (job.state === 'failed' && job.errorCode !== null) {
    return (
      <ErrorNotice
        error={describeProblem({
          code: job.errorCode,
          params: job.errorParams ?? {},
          requestId: job.requestId,
        })}
      />
    );
  }
  const step =
    job.progress.itemsTotal === 0
      ? 0
      : Math.min(job.progress.itemsDone + 1, job.progress.itemsTotal);
  return (
    <p className={styles.preview} role="status" aria-busy={!FINISHED.has(job.state)}>
      {job.state === 'succeeded'
        ? t('storage.run.succeeded')
        : FINISHED.has(job.state)
          ? t('storage.run.stopped')
          : t('storage.run.running', { step, count: job.progress.itemsTotal })}
      {!FINISHED.has(job.state) && job.progress.currentItem !== null && (
        <code className={styles.current}>{job.progress.currentItem}</code>
      )}
    </p>
  );
}

export function isFinished(job: Job | null): boolean {
  return job !== null && FINISHED.has(job.state);
}

/** The run button of a plan dialog: runs once, then the dialog only closes. */
export function RunButton({
  preview,
  label,
  run,
  job,
  danger = false,
}: {
  preview: StoragePlanPreview | undefined;
  label: string;
  run: ReturnType<typeof useRunPlan>['run'];
  job: Job | null;
  danger?: boolean;
}) {
  const planId = preview?.planId ?? null;
  return (
    <Button
      variant={danger ? 'danger' : 'primary'}
      isDisabled={planId === null || run.isPending || job !== null}
      onPress={() => {
        if (planId !== null) {
          run.mutate(planId);
        }
      }}
    >
      {label}
    </Button>
  );
}

export function RunError({ run }: { run: ReturnType<typeof useRunPlan>['run'] }) {
  return run.isError ? <ErrorNotice error={describeProblem(run.error)} /> : null;
}
