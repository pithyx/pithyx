import type { PithyxApp } from '@pithyx/sdk';
import { ProgressBar } from '@pithyx/ui';
import { useEffect, useState } from 'react';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { describeProblem } from '../storage/shared';
import { APPS_JOB, onStore, type Job } from '../sdk/store';
import styles from './Store.module.css';

const FINISHED = new Set(['succeeded', 'failed', 'cancelled', 'interrupted']);

export function isFinished(job: Job | null): boolean {
  return job !== null && FINISHED.has(job.state);
}

/** Follows the install, update or removal job this dialog started, until it ends. */
export function useAppJob(app: PithyxApp) {
  const [started, setStarted] = useState<Job | null>(null);
  // Updates by job id: a short job can report its end before the call that started it returns.
  const [updates, setUpdates] = useState<Readonly<Record<number, Job>>>({});
  // The desktop forwards the job's topic to this window: syncing with the SDK's events.
  useEffect(
    () =>
      onStore(app, APPS_JOB, (data) => {
        const update = data as Job;
        setUpdates((current) => ({ ...current, [update.id]: update }));
      }),
    [app],
  );
  return {
    job: started === null ? null : (updates[started.id] ?? started),
    start: setStarted,
  };
}

/** A running job as a progress bar with its label; a failed one as its error; a finished one as its message. */
export function JobProgress({
  job,
  running,
  succeeded,
  stopped,
}: {
  job: Job;
  running: string;
  succeeded: string;
  stopped: string;
}) {
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
  if (job.state === 'succeeded') {
    return (
      <p className={styles.success} role="status">
        {succeeded}
      </p>
    );
  }
  if (FINISHED.has(job.state)) {
    return (
      <p className={styles.muted} role="status">
        {stopped}
      </p>
    );
  }
  const { itemsDone, itemsTotal } = job.progress;
  return (
    <ProgressBar
      label={running}
      {...(itemsTotal === 0
        ? { isIndeterminate: true, showValue: false }
        : { value: (itemsDone / itemsTotal) * 100 })}
    />
  );
}
