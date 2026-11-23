import type { components } from '@pithyx/api-client';
import type { PithyxApp } from '@pithyx/sdk';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useRealtime } from '../../realtime/connection';
import {
  backupsCall,
  historyKey,
  jobsKey,
  type BackupHistoryQuery,
  type BackupJob,
} from '../sdk/backups';

export type BoardJob = components['schemas']['Job'];

/** The backup runs and checks on the job board, by their board id; administrators get the box's jobs on the topic. */
export function useActiveRuns(): ReadonlyMap<number, BoardJob> {
  const jobs = useRealtime((state) => state.data.jobs?.jobs);
  return new Map(
    (jobs ?? [])
      .filter(
        (job) =>
          job.kind.startsWith('backup.') && (job.state === 'running' || job.state === 'queued'),
      )
      .map((job) => [job.id, job]),
  );
}

/** Every job; a run that starts or ends on the board reads the list again. */
export function useBackupJobs(app: PithyxApp) {
  const active = useActiveRuns();
  return useQuery({
    queryKey: [...jobsKey, [...active.keys()].join(',')],
    queryFn: () => backupsCall(app, 'internal.admin.backups.jobs', {}),
    placeholderData: keepPreviousData,
  });
}

/** The runs of every job; a run that starts or ends on the board reads them again. */
export function useHistory(app: PithyxApp, query: BackupHistoryQuery) {
  const active = useActiveRuns();
  return useQuery({
    queryKey: [...historyKey(query), [...active.keys()].join(',')],
    queryFn: () => backupsCall(app, 'internal.admin.backups.history', query),
    placeholderData: keepPreviousData,
  });
}

/** The job a run on the board belongs to: the one whose last run or check it is, else the one it was started for. */
export function jobOfBoard(board: BoardJob, jobs: readonly BackupJob[]): BackupJob | undefined {
  const byRun = jobs.find(
    (job) => job.lastRun?.boardJobId === board.id || job.lastCheck?.boardJobId === board.id,
  );
  if (byRun !== undefined) {
    return byRun;
  }
  const parameters = board.parameters as { jobId?: unknown } | null;
  return jobs.find((job) => job.id === parameters?.jobId);
}

const DAY = 86_400_000;

/** Midnight of the day `days` before today, in the browser's time zone. */
export function daysAgo(days: number, now = new Date()): Date {
  const start = new Date(now);
  start.setHours(0, 0, 0, 0);
  start.setDate(start.getDate() - days);
  return start;
}

/** The index of the local day of `time` among the last `days` days, 0 the oldest, or -1 outside them. */
export function dayIndex(time: string, days: number, now = new Date()): number {
  const first = daysAgo(days - 1, now);
  const start = new Date(time);
  if (start < first) {
    return -1;
  }
  // Rounding absorbs the hour a change to or from daylight saving time adds or removes.
  const index = Math.round((daysAgo(0, start).getTime() - first.getTime()) / DAY);
  return index < days ? index : -1;
}
