import type { PithyxApp } from '@pithyx/sdk';
import {
  Button,
  DataTable,
  ProgressBar,
  StatusBadge,
  Toolbar,
  ToolbarSeparator,
  type DataTableColumn,
  type StatusTone,
} from '@pithyx/ui';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import {
  IconArchive,
  IconListDetails,
  IconPencil,
  IconPlayerPlay,
  IconPlus,
  IconServer,
  IconServer2,
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
  type BackupPullSettings,
  type BackupRun,
  type BackupTarget,
} from '../sdk/backups';
import { describeProblem } from '../storage/shared';
import { dayIndex, daysAgo, useActiveRuns, useBackupJobs, useHistory, type BoardJob } from './data';
import { DirectionTag, Route, directionOf, type RouteParts } from './Direction';
import { EmptyCard } from './EmptyCard';
import { scheduleOf } from './schedule';
import styles from './Backup.module.css';

export { useBackupJobs } from './data';

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

export const JOB_TONES = {
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

export const RUN_TONES = {
  running: 'info',
  succeeded: 'success',
  warning: 'warning',
  failed: 'danger',
  cancelled: 'neutral',
  interrupted: 'warning',
} as const satisfies Record<BackupRun['state'], StatusTone>;

/** What a job's row says first: a run or check that runs, then overdue, then how its last run ended. */
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
export function runningOf(job: BackupJob): BackupRun | undefined {
  return [job.lastRun, job.lastCheck].find((run) => run?.state === 'running') ?? undefined;
}

/** The names of a job's folders, for its route. */
export function useSourceNames(app: PithyxApp) {
  const { t } = useTranslation('backup');
  const sources = useQuery({
    queryKey: sourcesKey,
    queryFn: () => backupsCall(app, 'internal.admin.backups.sources', {}),
  });
  const names = new Map(sources.data?.sources.map((source) => [source.shareId, source.name]));
  // A folder deleted since the job was saved has no name any more; the run then fails with the source's error.
  const shareName = (shareId: number) => names.get(shareId) ?? t('jobs.sourceGone');
  const sourcePath = (source: { shareId: number; path: string }) =>
    source.path === ''
      ? shareName(source.shareId)
      : t('jobs.sourcePath', { share: shareName(source.shareId), path: source.path });
  return {
    shareName,
    sourcePath,
    /** Where a backup reads and where that goes; a server backup reads the server's folders into one on the box. */
    route: (job: BackupJob, target: BackupTarget | undefined): RouteParts =>
      job.pull === null
        ? {
            direction: 'out',
            from: job.settings.sources.map(sourcePath).join(', '),
            to: target?.name ?? '',
          }
        : {
            direction: 'in',
            from: t('route.server', {
              server: target?.name ?? '',
              paths: job.pull.paths.join(', '),
            }),
            to: sourcePath({ shareId: job.pull.shareId, path: job.pull.folder }),
          },
  };
}

/** A time close to now in few words: "Today, 02:06", "Tomorrow, 02:00", else the date without the year, "22.09., 22:48". */
export function useWhen() {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const time = new Intl.DateTimeFormat(locale, { timeStyle: 'short' });
  const date = new Intl.DateTimeFormat(locale, { day: '2-digit', month: '2-digit' });
  return (value: string): string => {
    const at = new Date(value);
    const day = Math.round((daysAgo(0, at).getTime() - daysAgo(0).getTime()) / 86_400_000);
    if (day === 0 || day === 1 || day === -1) {
      return t(`when.${day === 0 ? 'today' : day === 1 ? 'tomorrow' : 'yesterday'}`, {
        time: time.format(at),
      });
    }
    return t('when.date', { date: date.format(at), time: time.format(at) });
  };
}

export function useDuration() {
  const locale = useFormatLocale();
  const unit = (value: number, name: 'second' | 'minute' | 'hour') =>
    new Intl.NumberFormat(locale, { style: 'unit', unit: name, unitDisplay: 'narrow' }).format(
      value,
    );
  return (seconds: number): string => {
    if (seconds < 60) {
      return unit(Math.round(seconds), 'second');
    }
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) {
      return unit(minutes, 'minute');
    }
    return `${unit(Math.floor(minutes / 60), 'hour')} ${unit(minutes % 60, 'minute')}`;
  };
}

/** The seconds between a run's start and end, or undefined while it runs. */
export function runSeconds(run: BackupRun): number | undefined {
  return run.finishedAt === null
    ? undefined
    : Math.max(0, (Date.parse(run.finishedAt) - Date.parse(run.startedAt)) / 1000);
}

/** A cron schedule in words: "Daily 2:00 AM", "Every Monday, 2:00 AM", or the cron expression. */
export function useCronText() {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const clockTime = new Intl.DateTimeFormat(locale, { timeStyle: 'short' });
  // The schedule keeps "HH:MM" in the box's time; it is shown in the user's way of writing times.
  const at = (time: string) => {
    const [hours = 0, minutes = 0] = time.split(':').map(Number);
    return clockTime.format(new Date(2023, 0, 1, hours, minutes));
  };
  return (cron: string): string => {
    const schedule = scheduleOf(cron);
    switch (schedule.kind) {
      case 'hourly':
        return t('schedule.hourly', { minute: String(schedule.minute).padStart(2, '0') });
      case 'daily':
        return t('schedule.daily', { time: at(schedule.time) });
      case 'weekly':
        // 1 January 2023 was a Sunday, cron's day 0.
        return t('schedule.weekly', {
          day: new Intl.DateTimeFormat(locale, { weekday: 'long' }).format(
            new Date(2023, 0, 1 + schedule.day),
          ),
          time: at(schedule.time),
        });
      case 'monthly':
        return t('schedule.monthly', { day: schedule.day, time: at(schedule.time) });
      case 'custom':
        return schedule.cron;
    }
  };
}

/** A job's schedule in words, or that it runs only by hand. */
export function useScheduleText() {
  const { t } = useTranslation('backup');
  const cronText = useCronText();
  return (job: BackupJob): string =>
    job.settings.enabled ? cronText(job.settings.schedule) : t('schedule.off');
}

export function RunError({ run }: { run: BackupRun }) {
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

const DAYS = 14;

// Worst first: the day shows the worst way one of its runs ended.
const DAY_ORDER: readonly BackupRun['state'][] = [
  'failed',
  'interrupted',
  'warning',
  'cancelled',
  'succeeded',
];

/** The last 14 days of a job, one bar per day in the worst state of its backups that day. */
function DaysBar({ runs }: { runs: readonly BackupRun[] }) {
  const { t } = useTranslation('backup');
  const days: (BackupRun['state'] | undefined)[] = Array.from({ length: DAYS }, () => undefined);
  for (const run of runs) {
    const index = dayIndex(run.startedAt, DAYS);
    if (index < 0 || run.state === 'running') {
      continue;
    }
    const current = days[index];
    if (current === undefined || DAY_ORDER.indexOf(run.state) < DAY_ORDER.indexOf(current)) {
      days[index] = run.state;
    }
  }
  const count = (state: BackupRun['state']) => days.filter((day) => day === state).length;
  const label = t('jobs.daysLabel', {
    succeeded: count('succeeded'),
    warning: count('warning'),
    failed: count('failed') + count('interrupted'),
    none: days.filter((day) => day === undefined).length,
  });
  return (
    <span className={styles.days} role="img" aria-label={label}>
      {days.map((day, index) => (
        // The days have no identity beyond their place.
        <span key={index} className={styles.day} data-state={day ?? 'none'} />
      ))}
    </span>
  );
}

/** The job's last runs and checks, newest first. */
export function RunsTable({ app, job }: { app: PithyxApp; job: BackupJob }) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const when = useWhen();
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
  const columns: DataTableColumn<BackupRun>[] = [
    {
      id: 'started',
      label: t('runs.started'),
      cell: (run) => when(run.startedAt),
      isRowHeader: true,
    },
    {
      id: 'state',
      label: t('runs.state'),
      cell: (run) => {
        const seconds = runSeconds(run);
        return (
          <span className={styles.twoLines}>
            <StatusBadge tone={RUN_TONES[run.state]}>{t(`runs.states.${run.state}`)}</StatusBadge>
            <span className={styles.muted}>
              {seconds === undefined
                ? t(`runs.kinds.${run.kind}`)
                : t('runs.kindDuration', {
                    kind: t(`runs.kinds.${run.kind}`),
                    duration: duration(seconds),
                  })}
            </span>
          </span>
        );
      },
    },
    {
      id: 'added',
      label: t('runs.added'),
      align: 'end',
      muted: true,
      hideWhenNarrow: true,
      cell: (run) => (run.addedBytes === null ? '' : formatBytes(run.addedBytes, locale)),
    },
  ];
  return (
    <div className={styles.field}>
      <h3 className={styles.detailsTitle}>{t('runs.title')}</h3>
      {runs.isError && <ErrorNotice error={describeProblem(runs.error)} />}
      <DataTable
        aria-label={t('runs.title')}
        columns={columns}
        items={runs.data?.runs ?? []}
        getKey={(run) => run.id}
        density="comfortable"
        emptyState={runs.isPending ? t('runs.loading') : t('runs.empty')}
      />
    </div>
  );
}

/** A run's progress from its job on the board, unknown until restic counted the files. */
export function RunProgress({ run, board }: { run: BackupRun; board: BoardJob | undefined }) {
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

/**
 * A job's state, route, progress, folders, repository, checks and its last runs: beside the list, and without the head
 * on the job's own page, which shows name, route and state above.
 */
export function JobDetails({
  app,
  job,
  target,
  board,
  route,
  sourcePath,
  withHead = true,
}: {
  app: PithyxApp;
  job: BackupJob;
  target: BackupTarget | undefined;
  board: ReadonlyMap<number, BoardJob>;
  route: RouteParts;
  sourcePath: (source: { shareId: number; path: string }) => string;
  withHead?: boolean;
}) {
  const { t } = useTranslation('backup');
  const when = useWhen();
  const scheduleText = useScheduleText();
  const state = jobState(job);
  const running = runningOf(job);
  const lastCheck = job.lastCheck;
  return (
    <div className={styles.details}>
      {withHead && (
        <div className={styles.detailsHead}>
          <span className={styles.detailsName}>{job.settings.name}</span>
          <Route {...route} />
          <span className={styles.badgeRow}>
            <DirectionTag direction={directionOf(job.kind)} />
            <StatusBadge tone={JOB_TONES[state]}>{t(`jobs.status.${state}`)}</StatusBadge>
          </span>
        </div>
      )}
      {running !== undefined && (
        <RunProgress
          run={running}
          board={running.boardJobId === null ? undefined : board.get(running.boardJobId)}
        />
      )}
      {job.lastRun?.state === 'failed' && <RunError run={job.lastRun} />}
      {lastCheck?.state === 'failed' && <RunError run={lastCheck} />}
      <dl className={styles.facts}>
        <dt>{t('jobs.schedule')}</dt>
        <dd>{scheduleText(job)}</dd>
        <dt>{t('jobs.nextRun')}</dt>
        <dd>{job.nextRunAt === null ? t('jobs.byHand') : when(job.nextRunAt)}</dd>
        <dt>{t('jobs.lastBackup')}</dt>
        <dd>{job.lastSuccessAt === null ? t('jobs.noBackup') : when(job.lastSuccessAt)}</dd>
        {job.pull === null ? (
          <>
            <dt>{t('jobs.check')}</dt>
            <dd>
              {lastCheck?.finishedAt != null
                ? t('jobs.lastCheck', {
                    time: when(lastCheck.finishedAt),
                    state: t(`runs.states.${lastCheck.state}`),
                  })
                : job.nextCheckAt === null
                  ? t('jobs.noCheck')
                  : t('jobs.nextCheck', { time: when(job.nextCheckAt) })}
            </dd>
            <dt>{t('jobs.repository')}</dt>
            <dd className={styles.mono}>
              {t('jobs.repositoryValue', {
                repository: job.repository,
                target: target?.name ?? '',
              })}
            </dd>
            <dt>{t('jobs.sources')}</dt>
            <dd>
              <ul className={styles.plainList}>
                {job.settings.sources.map((source) => (
                  <li key={`${String(source.shareId)}/${source.path}`} className={styles.mono}>
                    {sourcePath(source)}
                  </li>
                ))}
              </ul>
            </dd>
          </>
        ) : (
          <PullFacts job={job} pull={job.pull} target={target} sourcePath={sourcePath} />
        )}
      </dl>
      <RunsTable app={app} job={job} />
    </div>
  );
}

/** What a server backup reads on its server and where it puts it on the box. */
export function PullFacts({
  job,
  pull,
  target,
  sourcePath,
}: {
  job: BackupJob;
  pull: BackupPullSettings;
  target: BackupTarget | undefined;
  sourcePath: (source: { shareId: number; path: string }) => string;
}) {
  const { t } = useTranslation('backup');
  return (
    <>
      <dt>{t('pulls.server')}</dt>
      <dd>
        {target === undefined
          ? ''
          : t('targets.address', { user: target.user, host: target.host, port: target.port })}
      </dd>
      <dt>{t('pulls.paths')}</dt>
      <dd>
        <ul className={styles.plainList}>
          {pull.paths.map((path) => (
            <li key={path} className={styles.mono}>
              {path}
            </li>
          ))}
        </ul>
      </dd>
      <dt>{t('pulls.destination')}</dt>
      <dd className={styles.mono}>{sourcePath({ shareId: pull.shareId, path: pull.folder })}</dd>
      <dt>{t('pulls.dataset')}</dt>
      <dd className={styles.mono}>{job.dataset ?? ''}</dd>
      <dt>{t('pulls.options')}</dt>
      <dd>
        {[
          t(pull.delete ? 'pulls.deleteOn' : 'pulls.deleteOff'),
          t(`pulls.owners.${pull.owners}`),
        ].join(' · ')}
      </dd>
    </>
  );
}

/** Starting a run or check and cancelling one, shared by the list and a backup's own page. */
export function useRunCommands(app: PithyxApp) {
  const queryClient = useQueryClient();
  const start = useMutation({
    mutationFn: ({ job, kind }: { job: BackupJob; kind: 'run' | 'check' }) =>
      backupsCall(app, 'internal.admin.backups.start', { id: job.id, kind }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: jobsKey }),
  });
  const cancel = useMutation({
    mutationFn: (boardJobId: number) =>
      backupsCall(app, 'internal.admin.backups.cancel', { boardJobId }),
  });
  return { start, cancel };
}

/** Back up now, or Cancel while a run or check goes on, and Check now, for the job given; a server backup has no check. */
export function RunButtons({
  job,
  commands: { start, cancel },
}: {
  job: BackupJob | undefined;
  commands: ReturnType<typeof useRunCommands>;
}) {
  const { t } = useTranslation('backup');
  const running = job === undefined ? undefined : runningOf(job);
  return (
    <>
      {running?.boardJobId != null ? (
        <Button
          isDisabled={cancel.isPending}
          onPress={() => {
            if (running.boardJobId !== null) {
              cancel.mutate(running.boardJobId);
            }
          }}
        >
          <IconX size={18} stroke={1.75} aria-hidden />
          {t('jobs.cancel')}
        </Button>
      ) : (
        <Button
          isDisabled={job === undefined || start.isPending}
          onPress={() => {
            if (job !== undefined) {
              start.mutate({ job, kind: 'run' });
            }
          }}
        >
          <IconPlayerPlay size={18} stroke={1.75} aria-hidden />
          {t(job?.kind === 'pull' ? 'pulls.runNow' : 'jobs.runNow')}
        </Button>
      )}
      {job?.kind !== 'pull' && (
        <Button
          isDisabled={job === undefined || running !== undefined || start.isPending}
          onPress={() => {
            if (job !== undefined) {
              start.mutate({ job, kind: 'check' });
            }
          }}
        >
          <IconShieldCheck size={18} stroke={1.75} aria-hidden />
          {t('jobs.checkNow')}
        </Button>
      )}
    </>
  );
}

/** The errors of {@link useRunCommands}. */
export function RunCommandErrors({ commands }: { commands: ReturnType<typeof useRunCommands> }) {
  return (
    <>
      {commands.start.isError && <ErrorNotice error={describeProblem(commands.start.error)} />}
      {commands.cancel.isError && <ErrorNotice error={describeProblem(commands.cancel.error)} />}
    </>
  );
}

/**
 * Box backups (D416): every backup from this box to a target as a table with its route, schedule, last and next run,
 * size and the last 14 days; the toolbar acts on the selected one and its details are on the right. With `kind` pull,
 * the server backups onto this box (#396, D420), from the servers in `targets`.
 */
export function JobsPage({
  app,
  kind,
  jobs,
  targets,
  onAdd,
  onAddTarget,
  onOpen,
  onShow,
}: {
  app: PithyxApp;
  kind: BackupJob['kind'];
  jobs: ReturnType<typeof useBackupJobs>;
  /** The targets, or the servers of server backups. */
  targets: readonly BackupTarget[] | undefined;
  onAdd: () => void;
  onAddTarget: () => void;
  onOpen: (kind: 'edit' | 'delete', job: BackupJob) => void;
  /** Opens the backup's own page with its versions. */
  onShow: (job: BackupJob) => void;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const board = useActiveRuns();
  const { route, sourcePath } = useSourceNames(app);
  const scheduleText = useScheduleText();
  const when = useWhen();
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const history = useHistory(app, {
    kind: 'backup',
    since: daysAgo(DAYS - 1).toISOString(),
    limit: 2000,
  });
  const runsOf = new Map<number, BackupRun[]>();
  for (const run of history.data?.runs ?? []) {
    runsOf.set(run.jobId, [...(runsOf.get(run.jobId) ?? []), run]);
  }
  // The texts of box backups are under `jobs`, those of server backups under `pulls`.
  const texts = kind === 'pull' ? 'pulls' : 'jobs';
  const list = (jobs.data?.jobs ?? []).filter((job) => job.kind === kind);
  const selected = list.find((job) => job.id === selectedId);
  const targetOf = (job: BackupJob) => targets?.find((target) => target.id === job.targetId);
  const commands = useRunCommands(app);
  const running = selected === undefined ? undefined : runningOf(selected);
  // The size of the last version, from the newest backup that wrote one.
  const sizeOf = (job: BackupJob) =>
    (runsOf.get(job.id) ?? []).find((run) => run.bytes !== null)?.bytes ??
    job.lastRun?.bytes ??
    null;

  const columns: DataTableColumn<BackupJob>[] = [
    {
      id: 'name',
      label: t('jobs.name'),
      // The kind sits below the name, so the table keeps its room for the route.
      cell: (job) => (
        <span className={styles.twoLines}>
          <span className={styles.name}>{job.settings.name}</span>
          <span className={styles.muted}>{t(`kinds.${job.kind}`)}</span>
        </span>
      ),
      sortValue: (job) => job.settings.name,
      isRowHeader: true,
      width: '18%',
    },
    {
      id: 'route',
      label: t('jobs.route'),
      cell: (job) => <Route {...route(job, targetOf(job))} />,
      width: '26%',
    },
    {
      id: 'last',
      label: t('jobs.lastRun'),
      width: 128,
      cell: (job) => {
        const state = jobState(job);
        return (
          <span className={styles.twoLines}>
            <StatusBadge tone={JOB_TONES[state]}>{t(`jobs.status.${state}`)}</StatusBadge>
            {job.lastRun !== null && (
              <span className={styles.muted}>{when(job.lastRun.startedAt)}</span>
            )}
          </span>
        );
      },
      sortValue: (job) => job.lastRun?.startedAt ?? '',
    },
    {
      id: 'next',
      label: t('jobs.nextRun'),
      hideWhenNarrow: true,
      width: 150,
      // The schedule sits below the next run, so the table keeps its room for the route.
      cell: (job) => (
        <span className={styles.twoLines}>
          <span>{job.nextRunAt === null ? t('jobs.byHand') : when(job.nextRunAt)}</span>
          <span className={styles.muted}>{scheduleText(job)}</span>
        </span>
      ),
      sortValue: (job) => job.nextRunAt ?? '',
    },
    {
      id: 'size',
      label: t('jobs.size'),
      align: 'end',
      hideWhenNarrow: true,
      width: 88,
      cell: (job) => {
        const bytes = sizeOf(job);
        return bytes === null ? '' : formatBytes(bytes, locale);
      },
      sortValue: (job) => sizeOf(job) ?? -1,
    },
    {
      id: 'days',
      label: t('jobs.days'),
      hideWhenNarrow: true,
      width: 144,
      cell: (job) => <DaysBar runs={runsOf.get(job.id) ?? []} />,
    },
  ];

  if (jobs.data !== undefined && list.length === 0 && targets !== undefined) {
    return (
      <div className={styles.page}>
        <p className={styles.intro}>{t(`${texts}.intro`)}</p>
        {targets.length === 0 ? (
          <EmptyCard
            icon={
              kind === 'pull' ? (
                <IconServer2 size={24} stroke={1.75} />
              ) : (
                <IconServer size={24} stroke={1.75} />
              )
            }
            title={t(`${texts}.noTargetsTitle`)}
            text={t(`${texts}.noTargets`)}
            action={t(kind === 'pull' ? 'servers.add' : 'targets.add')}
            onAction={onAddTarget}
          />
        ) : (
          <EmptyCard
            icon={<IconArchive size={24} stroke={1.75} />}
            title={t(`${texts}.emptyTitle`)}
            text={t(`${texts}.empty`)}
            action={t(`${texts}.add`)}
            onAction={onAdd}
          />
        )}
      </div>
    );
  }

  return (
    <div className={styles.page}>
      <p className={styles.intro}>{t(`${texts}.intro`)}</p>
      <Toolbar aria-label={t(`${texts}.toolbar`)}>
        <Button variant="primary" onPress={onAdd}>
          <IconPlus size={18} stroke={1.75} aria-hidden />
          {t(`${texts}.add`)}
        </Button>
        <ToolbarSeparator />
        <Button
          isDisabled={selected === undefined}
          onPress={() => {
            if (selected !== undefined) {
              onShow(selected);
            }
          }}
        >
          <IconListDetails size={18} stroke={1.75} aria-hidden />
          {t('jobs.open')}
        </Button>
        <RunButtons job={selected} commands={commands} />
        <ToolbarSeparator />
        <Button
          isDisabled={selected === undefined}
          onPress={() => {
            if (selected !== undefined) {
              onOpen('edit', selected);
            }
          }}
        >
          <IconPencil size={18} stroke={1.75} aria-hidden />
          {t('jobs.edit')}
        </Button>
        <Button
          isDisabled={selected === undefined || running !== undefined}
          onPress={() => {
            if (selected !== undefined) {
              onOpen('delete', selected);
            }
          }}
        >
          <IconTrash size={18} stroke={1.75} aria-hidden />
          {t('jobs.delete')}
        </Button>
      </Toolbar>
      {jobs.isError && <ErrorNotice error={describeProblem(jobs.error)} />}
      <RunCommandErrors commands={commands} />
      {history.isError && <ErrorNotice error={describeProblem(history.error)} />}
      <DataTable
        aria-label={t(`${texts}.list`)}
        columns={columns}
        items={list}
        getKey={(job) => job.id}
        density="comfortable"
        selectionMode="single"
        selectedKeys={selected === undefined ? new Set() : new Set([selected.id])}
        onSelectionChange={(keys) => {
          const [first] = keys === 'all' ? [] : [...keys];
          setSelectedId(first === undefined ? null : Number(first));
        }}
        onRowAction={(key) => {
          const job = list.find((entry) => entry.id === Number(key));
          if (job !== undefined) {
            onShow(job);
          }
        }}
        defaultSort={{ column: 'name', direction: 'ascending' }}
        emptyState={jobs.isPending ? t('jobs.loading') : t(`${texts}.empty`)}
        details={
          selected === undefined ? undefined : (
            <JobDetails
              app={app}
              job={selected}
              target={targetOf(selected)}
              board={board}
              route={route(selected, targetOf(selected))}
              sourcePath={sourcePath}
            />
          )
        }
        detailsLabel={t('jobs.details')}
      />
    </div>
  );
}
