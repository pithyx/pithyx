import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { TextField } from '../../ui/TextField';
import {
  internalCall,
  onInternal,
  STORAGE_JOB,
  type Job,
  type StorageDisk,
  type StoragePlanPreview,
} from '../sdk/internal';
import styles from './Storage.module.css';
import { describeProblem, guardReason, type Translate } from './shared';

/**
 * The commands of a plan, or why the plan cannot be made, with the plan's warnings first. A plan that waits for the
 * pool's typed name has no commands yet; the dialog shows its {@link ConfirmNameField}.
 */
export function PlanPreview({
  preview,
  disks,
  datasetsLabel,
}: {
  preview: StoragePlanPreview;
  disks: StorageDisk[];
  /** What the listed datasets mean for this action; by default that they are lost. */
  datasetsLabel?: string | undefined;
}) {
  const { t } = useTranslation('storage');
  return (
    <>
      <PlanWarnings t={t} warnings={preview.warnings} />
      <PlanBody preview={preview} disks={disks} datasetsLabel={datasetsLabel} />
    </>
  );
}

const WARNINGS = new Set([
  'lowers_redundancy',
  'replication_mismatch',
  'irreversible',
  'no_redundancy',
  'other_system',
  'degraded',
  'unmounted_datasets',
  'datasets_unavailable',
  'changes_lost',
  'newer_snapshots',
]);

function PlanWarnings({ t, warnings }: { t: Translate; warnings: string[] }) {
  const known = warnings.filter((warning) => WARNINGS.has(warning));
  if (known.length === 0) {
    return null;
  }
  const text = t as unknown as (key: string) => string;
  return (
    <div className={styles.warnings} role="note" aria-label={t('warnings.label')}>
      {known.map((warning) => (
        <p key={warning}>{text(`warnings.${warning}`)}</p>
      ))}
    </div>
  );
}

const UNMOUNTED = new Set([
  'system_path',
  'shadows_mount',
  'duplicate_mountpoint',
  'key_unavailable',
]);

function UnmountedList({ preview }: { preview: StoragePlanPreview }) {
  const { t } = useTranslation('storage');
  const text = t as unknown as (key: string) => string;
  if (preview.unmounted.length === 0) {
    return null;
  }
  return (
    <>
      <p>{t('unmounted.title')}</p>
      <ul>
        {preview.unmounted.map((dataset) => (
          <li key={dataset.name}>
            <span className={styles.mono}>{dataset.name}</span> (
            <span className={styles.mono}>{dataset.mountPoint}</span>):{' '}
            {UNMOUNTED.has(dataset.reason) ? text(`unmounted.${dataset.reason}`) : dataset.reason}
          </li>
        ))}
      </ul>
      {preview.unmounted.some((dataset) => dataset.reason !== 'key_unavailable') && (
        <p>{t('unmounted.noauto')}</p>
      )}
    </>
  );
}

function PlanBody({
  preview,
  disks,
  datasetsLabel,
}: {
  preview: StoragePlanPreview;
  disks: StorageDisk[];
  datasetsLabel: string | undefined;
}) {
  const { t } = useTranslation('storage');
  if (
    preview.commands === null &&
    preview.requiresConfirmName &&
    preview.disks.every((verdict) => verdict.decision === 'allowed')
  ) {
    return (
      <div className={styles.preview} role="status">
        <p>{t('confirmName.needed')}</p>
        <UnmountedList preview={preview} />
      </div>
    );
  }
  if (preview.commands === null) {
    return (
      <div className={styles.preview} role="status">
        <p>{t('create.blocked')}</p>
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
          <p>{datasetsLabel ?? t('actions.datasets')}</p>
          <ul className={styles.mono}>
            {preview.datasets.map((dataset) => (
              <li key={dataset}>{dataset}</li>
            ))}
          </ul>
        </>
      )}
      <UnmountedList preview={preview} />
      <p>{t('create.commands')}</p>
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

/** The pool's name typed to confirm a plan whose preview says `requiresConfirmName`. */
export function ConfirmNameField({
  pool,
  value,
  onChange,
  isReadOnly = false,
}: {
  pool: string;
  value: string;
  onChange: (value: string) => void;
  isReadOnly?: boolean;
}) {
  const { t } = useTranslation('storage');
  return (
    <TextField
      label={t('confirmName.label', { pool })}
      value={value}
      onChange={onChange}
      autoCapitalize="none"
      autoComplete="off"
      isReadOnly={isReadOnly}
    />
  );
}

/** Runs a shown plan as a job and follows it until it ends; `onStarted` gets the job once it was started. */
export function useRunPlan(app: PithyxApp, onStarted?: (job: Job) => void) {
  const [started, setStarted] = useState<Job | null>(null);
  // Updates by job id: a short job can report its end before the run call returns its id.
  const [updates, setUpdates] = useState<Readonly<Record<number, Job>>>({});
  const run = useMutation({
    mutationFn: (planId: string) => internalCall(app, 'internal.admin.storage.run', { planId }),
    onSuccess: (job) => {
      setStarted(job);
      onStarted?.(job);
    },
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

/** Where a running plan is; the error of a failed one. */
export function JobStatus({ job }: { job: Job }) {
  const { t } = useTranslation('storage');
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
        ? t('run.succeeded')
        : FINISHED.has(job.state)
          ? t('run.stopped')
          : t('run.running', { step, count: job.progress.itemsTotal })}
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
