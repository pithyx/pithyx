import type { components } from '@pithyx/api-client';
import type { PithyxApp } from '@pithyx/sdk';
import {
  Button,
  Card,
  DataTable,
  ProgressBar,
  StatusBadge,
  type DataTableColumn,
  type StatusTone,
} from '@pithyx/ui';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { useRealtime } from '../../realtime/connection';
import {
  IconArchive,
  IconPencil,
  IconPlayerPlay,
  IconPlus,
  IconShieldCheck,
  IconTrash,
  IconX,
} from '../../ui/icons';
import {
  backupsCall,
  jobsKey,
  runsKey,
  sourcesKey,
  type BackupJob,
  type BackupRun,
  type BackupTarget,
} from '../sdk/backups';
import { describeProblem, formatDate } from '../storage/shared';
import styles from './Backup.module.css';

type BoardJob = components['schemas']['Job'];

/** The backup runs and checks on the job board, by their board id; administrators get the box's jobs on the topic. */
function useActiveRuns(): ReadonlyMap<number, BoardJob> {
  const jobs = useRealtime((state) => state.data.jobs?.jobs);
  return new Map(
    (jobs ?? []).filter((job) => job.kind.startsWith('backup.')).map((job) => [job.id, job]),
  );
}

/** Every job; a run that starts or ends on the board reads the list again. */
export function useBackupJobs(app: PithyxApp, active: ReadonlyMap<number, BoardJob>) {
  return useQuery({
    queryKey: [...jobsKey, [...active.keys()].join(',')],
    queryFn: () => backupsCall(app, 'internal.admin.backups.jobs', {}),
    placeholderData: keepPreviousData,
  });
}

type JobState =
  | 'running'
  | 'checking'
  | 'overdue'
  | 'never'
  | 'succeeded'
  | 'warning'
  | 'failed'
  | 'cancelled'
  | 'interrupted';

const TONES = {
  running: 'info',
  checking: 'info',
  overdue: 'warning',
  never: 'neutral',
  succeeded: 'success',
  warning: 'warning',
  failed: 'danger',
  cancelled: 'neutral',
  interrupted: 'warning',
} as const satisfies Record<JobState, StatusTone>;

const RUN_TONES = {
  running: 'info',
  succeeded: 'success',
  warning: 'warning',
  failed: 'danger',
  cancelled: 'neutral',
  interrupted: 'warning',
} as const satisfies Record<BackupRun['state'], StatusTone>;

/** What a job's card says first: a run or check that runs, then overdue, then how its last run ended. */
export function jobState(job: BackupJob): JobState {
  if (job.lastRun?.state === 'running') {
    return 'running';
  }
  if (job.lastCheck?.state === 'running') {
    return 'checking';
  }
  if (job.overdue) {
    return 'overdue';
  }
  return job.lastRun?.state ?? 'never';
}

/** The run or check that runs now, if any. */
function runningOf(job: BackupJob): BackupRun | undefined {
  return [job.lastRun, job.lastCheck].find((run) => run?.state === 'running') ?? undefined;
}

function useDuration() {
  const locale = useFormatLocale();
  const unit = (value: number, name: 'second' | 'minute' | 'hour') =>
    new Intl.NumberFormat(locale, { style: 'unit', unit: name, unitDisplay: 'narrow' }).format(
      value,
    );
  return (run: BackupRun): string => {
    if (run.finishedAt === null) {
      return '';
    }
    const seconds = Math.max(
      0,
      Math.round((Date.parse(run.finishedAt) - Date.parse(run.startedAt)) / 1000),
    );
    if (seconds < 60) {
      return unit(seconds, 'second');
    }
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) {
      return unit(minutes, 'minute');
    }
    return `${unit(Math.floor(minutes / 60), 'hour')} ${unit(minutes % 60, 'minute')}`;
  };
}

function RunError({ run }: { run: BackupRun }) {
  if (run.errorCode === null) {
    return null;
  }
  return (
    <ErrorNotice
      error={describeProblem({
        code: run.errorCode,
        params: (run.errorParams ?? {}) as Record<string, unknown>,
        requestId: '',
      })}
    />
  );
}

/** The job's last runs and checks, newest first. */
function RunsTable({ app, job }: { app: PithyxApp; job: BackupJob }) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const duration = useDuration();
  const runs = useQuery({
    // A new or finished run changes the job's last run, which reads the list again.
    queryKey: [
      ...runsKey(job.id),
      job.lastRun?.id,
      job.lastRun?.state,
      job.lastCheck?.id,
      job.lastCheck?.state,
    ],
    queryFn: () => backupsCall(app, 'internal.admin.backups.runs', { id: job.id }),
    placeholderData: keepPreviousData,
  });
  const number = new Intl.NumberFormat(locale);
  const columns: DataTableColumn<BackupRun>[] = [
    {
      id: 'started',
      label: t('runs.started'),
      cell: (run) => formatDate(run.startedAt, locale),
      isRowHeader: true,
      width: '28%',
    },
    { id: 'kind', label: t('runs.kind'), cell: (run) => t(`runs.kinds.${run.kind}`) },
    {
      id: 'state',
      label: t('runs.state'),
      cell: (run) => (
        <StatusBadge tone={RUN_TONES[run.state]}>{t(`runs.states.${run.state}`)}</StatusBadge>
      ),
    },
    {
      id: 'files',
      label: t('runs.files'),
      align: 'end',
      hideWhenNarrow: true,
      cell: (run) => (run.files === null ? '' : number.format(run.files)),
    },
    {
      id: 'size',
      label: t('runs.size'),
      align: 'end',
      hideWhenNarrow: true,
      cell: (run) => (run.bytes === null ? '' : formatBytes(run.bytes, locale)),
    },
    {
      id: 'added',
      label: t('runs.added'),
      align: 'end',
      muted: true,
      hideWhenNarrow: true,
      cell: (run) => (run.addedBytes === null ? '' : formatBytes(run.addedBytes, locale)),
    },
    {
      id: 'duration',
      label: t('runs.duration'),
      align: 'end',
      muted: true,
      hideWhenNarrow: true,
      cell: duration,
    },
  ];
  return (
    <div className={styles.field}>
      <span className={styles.fieldLabel}>{t('runs.title')}</span>
      {runs.isError && <ErrorNotice error={describeProblem(runs.error)} />}
      <DataTable
        aria-label={t('runs.title')}
        columns={columns}
        items={runs.data?.runs ?? []}
        getKey={(run) => run.id}
        emptyState={runs.isPending ? t('runs.loading') : t('runs.empty')}
      />
    </div>
  );
}

function Progress({ run, board }: { run: BackupRun; board: BoardJob | undefined }) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const progress = board?.progress;
  const label = t(run.kind === 'check' ? 'jobs.progressCheck' : 'jobs.progressBackup');
  if (progress === undefined || progress.bytesTotal <= 0) {
    return <ProgressBar label={label} isIndeterminate showValue={false} />;
  }
  return (
    <ProgressBar
      label={label}
      value={Math.min(progress.bytesDone, progress.bytesTotal)}
      maxValue={progress.bytesTotal}
      valueLabel={t('jobs.progressValue', {
        done: formatBytes(progress.bytesDone, locale),
        total: formatBytes(progress.bytesTotal, locale),
      })}
    />
  );
}

function JobCard({
  app,
  job,
  target,
  board,
  shareName,
  onOpen,
}: {
  app: PithyxApp;
  job: BackupJob;
  target: BackupTarget | undefined;
  board: ReadonlyMap<number, BoardJob>;
  shareName: (shareId: number) => string;
  onOpen: (kind: 'edit' | 'delete') => void;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const state = jobState(job);
  const running = runningOf(job);
  const [expanded, setExpanded] = useState(
    state === 'failed' || state === 'overdue' || state === 'never',
  );
  const start = useMutation({
    mutationFn: (kind: 'run' | 'check') =>
      backupsCall(app, 'internal.admin.backups.start', { id: job.id, kind }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: jobsKey }),
  });
  const cancel = useMutation({
    mutationFn: (boardJobId: number) =>
      backupsCall(app, 'internal.admin.backups.cancel', { boardJobId }),
  });
  const facts = [
    t('jobs.to', { target: target?.name ?? '' }),
    job.nextRunAt === null
      ? t('jobs.byHand')
      : t('jobs.next', { time: formatDate(job.nextRunAt, locale) }),
    job.lastSuccessAt === null
      ? t('jobs.noBackup')
      : t('jobs.lastSuccess', { time: formatDate(job.lastSuccessAt, locale) }),
  ].join(' · ');
  const sources = job.settings.sources;
  const lastCheck = job.lastCheck;
  const checkLine = () => {
    if (lastCheck?.finishedAt != null) {
      return t('jobs.lastCheck', {
        time: formatDate(lastCheck.finishedAt, locale),
        state: t(`runs.states.${lastCheck.state}`),
      });
    }
    return job.nextCheckAt === null
      ? t('jobs.noCheck')
      : t('jobs.nextCheck', { time: formatDate(job.nextCheckAt, locale) });
  };

  return (
    <Card
      icon={<IconArchive size={24} stroke={1.75} aria-hidden />}
      title={job.settings.name}
      badge={<StatusBadge tone={TONES[state]}>{t(`jobs.status.${state}`)}</StatusBadge>}
      facts={facts}
      meter={
        running === undefined ? undefined : (
          <Progress
            run={running}
            board={running.boardJobId === null ? undefined : board.get(running.boardJobId)}
          />
        )
      }
      isExpanded={expanded}
      onExpandedChange={setExpanded}
      actions={
        <>
          {running?.boardJobId != null ? (
            <Button
              size="sm"
              isDisabled={cancel.isPending}
              onPress={() => {
                if (running.boardJobId !== null) {
                  cancel.mutate(running.boardJobId);
                }
              }}
            >
              <IconX size={16} stroke={1.75} aria-hidden />
              {t('jobs.cancel')}
            </Button>
          ) : (
            <>
              <Button
                size="sm"
                isDisabled={start.isPending}
                onPress={() => {
                  start.mutate('run');
                }}
              >
                <IconPlayerPlay size={16} stroke={1.75} aria-hidden />
                {t('jobs.runNow')}
              </Button>
              <Button
                size="sm"
                isDisabled={start.isPending}
                onPress={() => {
                  start.mutate('check');
                }}
              >
                <IconShieldCheck size={16} stroke={1.75} aria-hidden />
                {t('jobs.checkNow')}
              </Button>
            </>
          )}
          <Button
            size="sm"
            onPress={() => {
              onOpen('edit');
            }}
          >
            <IconPencil size={16} stroke={1.75} aria-hidden />
            {t('jobs.edit')}
          </Button>
          <Button
            size="sm"
            variant="danger"
            isDisabled={running !== undefined}
            onPress={() => {
              onOpen('delete');
            }}
          >
            <IconTrash size={16} stroke={1.75} aria-hidden />
            {t('jobs.delete')}
          </Button>
        </>
      }
    >
      <div className={styles.cardBody}>
        {job.lastRun?.state === 'failed' && <RunError run={job.lastRun} />}
        {lastCheck !== null && lastCheck.state === 'failed' && <RunError run={lastCheck} />}
        {start.isError && <ErrorNotice error={describeProblem(start.error)} />}
        {cancel.isError && <ErrorNotice error={describeProblem(cancel.error)} />}
        <div className={styles.field}>
          <span className={styles.fieldLabel}>{t('jobs.sources')}</span>
          <ul className={styles.plainList}>
            {sources.map((source) => (
              <li key={`${String(source.shareId)}/${source.path}`} className={styles.mono}>
                {source.path === ''
                  ? shareName(source.shareId)
                  : t('jobs.sourcePath', { share: shareName(source.shareId), path: source.path })}
              </li>
            ))}
          </ul>
        </div>
        <div className={styles.field}>
          <span className={styles.fieldLabel}>{t('jobs.repository')}</span>
          <span className={`${styles.mono} ${styles.breakable}`}>
            {t('jobs.repositoryValue', { repository: job.repository, target: target?.name ?? '' })}
          </span>
        </div>
        <p className={styles.muted}>{checkLine()}</p>
        {expanded && <RunsTable app={app} job={job} />}
      </div>
    </Card>
  );
}

/** The jobs: one card each, with its state, its last runs and what can be done with it. */
export function JobsPage({
  app,
  targets,
  onAdd,
  onOpen,
}: {
  app: PithyxApp;
  targets: readonly BackupTarget[] | undefined;
  onAdd: () => void;
  onOpen: (kind: 'edit' | 'delete', job: BackupJob) => void;
}) {
  const { t } = useTranslation('backup');
  const board = useActiveRuns();
  const jobs = useBackupJobs(app, board);
  const sources = useQuery({
    queryKey: sourcesKey,
    queryFn: () => backupsCall(app, 'internal.admin.backups.sources', {}),
  });
  const names = new Map(sources.data?.sources.map((source) => [source.shareId, source.name]));
  // A folder deleted since the job was saved has no name any more; the run then fails with the source's error.
  const shareName = (shareId: number) => names.get(shareId) ?? t('jobs.sourceGone');
  const noTargets = targets?.length === 0;

  return (
    <div className={styles.page}>
      <p className={styles.muted}>{t('jobs.intro')}</p>
      {jobs.isError && <ErrorNotice error={describeProblem(jobs.error)} />}
      {jobs.isPending && <p className={styles.muted}>{t('jobs.loading')}</p>}
      {jobs.data?.jobs.length === 0 && (
        <div className={styles.empty}>
          <p className={styles.muted}>{noTargets ? t('jobs.noTargets') : t('jobs.empty')}</p>
          {!noTargets && (
            <Button onPress={onAdd}>
              <IconPlus size={16} stroke={1.75} aria-hidden />
              {t('jobs.add')}
            </Button>
          )}
        </div>
      )}
      {jobs.data !== undefined && jobs.data.jobs.length > 0 && (
        <ul className={styles.targets} aria-label={t('jobs.list')}>
          {jobs.data.jobs.map((job) => (
            <li key={job.id}>
              <JobCard
                app={app}
                job={job}
                target={targets?.find((target) => target.id === job.targetId)}
                board={board}
                shareName={shareName}
                onOpen={(kind) => {
                  onOpen(kind, job);
                }}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
