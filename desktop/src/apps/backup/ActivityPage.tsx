import type { PithyxApp } from '@pithyx/sdk';
import { Button, DataTable, Select, StatusBadge, Toolbar, type DataTableColumn } from '@pithyx/ui';
import { useMutation } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { IconActivity, IconDownload, IconX } from '../../ui/icons';
import {
  backupsCall,
  type BackupHistoryQuery,
  type BackupJob,
  type BackupRun,
  type BackupTarget,
} from '../sdk/backups';
import { describeProblem, formatDate } from '../storage/shared';
import { daysAgo, jobOfBoard, useActiveRuns, useHistory, type BoardJob } from './data';
import { DirectionTag, Route, directionOf } from './Direction';
import {
  RUN_TONES,
  RunError,
  RunProgress,
  runSeconds,
  useBackupJobs,
  useDuration,
  useSourceNames,
} from './JobsPage';
import styles from './Backup.module.css';

const PAGE = 200;
const PERIODS = { day: 1, week: 7, month: 30, quarter: 90, all: undefined } as const;
type Period = keyof typeof PERIODS;
const STATES: readonly BackupRun['state'][] = [
  'succeeded',
  'warning',
  'failed',
  'cancelled',
  'interrupted',
  'running',
];

/** A run on the board with what it has done so far: files, data, speed and the time left. */
function RunningRun({
  app,
  board,
  job,
  target,
  route,
}: {
  app: PithyxApp;
  board: BoardJob;
  job: BackupJob | undefined;
  target: BackupTarget | undefined;
  route: string;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const duration = useDuration();
  const cancel = useMutation({
    mutationFn: () => backupsCall(app, 'internal.admin.backups.cancel', { boardJobId: board.id }),
  });
  const run =
    [job?.lastRun, job?.lastCheck].find((entry) => entry?.boardJobId === board.id) ?? undefined;
  const isCheck = board.kind === 'backup.check';
  const name = job?.settings.name ?? (board.parameters as { name?: string } | null)?.name ?? '';
  const progress = board.progress;
  const number = new Intl.NumberFormat(locale);
  const percent = new Intl.NumberFormat(locale, { style: 'percent' });
  const [now, setNow] = useState(() => Date.now());
  // Speed and time left follow the clock, which lives outside React.
  useEffect(() => {
    const timer = setInterval(() => {
      setNow(Date.now());
    }, 5000);
    return () => {
      clearInterval(timer);
    };
  }, []);
  const elapsed = board.startedAt === null ? 0 : (now - Date.parse(board.startedAt)) / 1000;
  const speed = elapsed <= 0 ? 0 : progress.bytesDone / elapsed;
  const left =
    speed <= 0 || progress.bytesTotal <= 0
      ? undefined
      : Math.max(0, (progress.bytesTotal - progress.bytesDone) / speed);
  return (
    <li className={styles.running}>
      <div className={styles.runningHead}>
        <span className={styles.runningName}>
          <span className={styles.name}>{name}</span>
          <StatusBadge tone="info">
            {board.state === 'queued'
              ? t('activity.waiting')
              : t(`activity.kinds.${isCheck ? 'check' : 'backup'}`)}
          </StatusBadge>
        </span>
        <Button
          size="sm"
          variant="danger"
          isDisabled={cancel.isPending}
          onPress={() => {
            cancel.mutate();
          }}
        >
          <IconX size={16} stroke={1.75} aria-hidden />
          {t('jobs.cancel')}
        </Button>
      </div>
      <span className={styles.badgeRow}>
        <DirectionTag direction={directionOf(job?.kind ?? 'versioned')} />
        <Route from={route} to={target?.name ?? ''} />
      </span>
      {run !== undefined && board.state === 'running' && <RunProgress run={run} board={board} />}
      {board.state === 'running' && (
        <dl className={styles.runningFacts}>
          {progress.bytesTotal > 0 && (
            <div>
              <dt>{t('activity.progress')}</dt>
              <dd>{percent.format(Math.min(1, progress.bytesDone / progress.bytesTotal))}</dd>
            </div>
          )}
          {progress.itemsTotal > 0 && (
            <div>
              <dt>{t('activity.files')}</dt>
              <dd>
                {t('activity.ofTotal', {
                  done: number.format(progress.itemsDone),
                  total: number.format(progress.itemsTotal),
                })}
              </dd>
            </div>
          )}
          {!isCheck && speed > 0 && (
            <div>
              <dt>{t('activity.speed')}</dt>
              <dd>{t('activity.perSecond', { size: formatBytes(speed, locale) })}</dd>
            </div>
          )}
          {left !== undefined && (
            <div>
              <dt>{t('activity.left')}</dt>
              <dd>{t('activity.about', { duration: duration(left) })}</dd>
            </div>
          )}
        </dl>
      )}
      {progress.currentItem != null && progress.currentItem !== '' && (
        <span className={`${styles.mono} ${styles.muted} ${styles.breakable}`}>
          {progress.currentItem}
        </span>
      )}
      {cancel.isError && <ErrorNotice error={describeProblem(cancel.error)} />}
    </li>
  );
}

/** The selected run: when, how it ended, what it wrote and the files it could not read. */
function RunDetails({
  run,
  job,
  target,
  route,
}: {
  run: BackupRun;
  job: BackupJob | undefined;
  target: BackupTarget | undefined;
  route: string;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const duration = useDuration();
  const number = new Intl.NumberFormat(locale);
  const seconds = runSeconds(run);
  const more = run.warnings - run.unreadable.length;
  return (
    <div className={styles.details}>
      <div className={styles.detailsHead}>
        <span className={styles.detailsName}>{job?.settings.name ?? t('activity.deletedJob')}</span>
        <Route from={route} to={target?.name ?? ''} />
        <span className={styles.muted}>
          {run.finishedAt === null
            ? formatDate(run.startedAt, locale)
            : t('activity.period', {
                start: formatDate(run.startedAt, locale),
                end: formatDate(run.finishedAt, locale),
              })}
        </span>
      </div>
      <RunError run={run} />
      <dl className={styles.facts}>
        <dt>{t('runs.state')}</dt>
        <dd>
          <StatusBadge tone={RUN_TONES[run.state]}>{t(`runs.states.${run.state}`)}</StatusBadge>
        </dd>
        <dt>{t('runs.kind')}</dt>
        <dd>{t(`activity.kinds.${run.kind}`)}</dd>
        <dt>{t('activity.trigger')}</dt>
        <dd>{t(`activity.triggers.${run.trigger}`)}</dd>
        {run.snapshot !== null && (
          <>
            <dt>{t('activity.version')}</dt>
            <dd className={styles.mono}>{run.snapshot.slice(0, 8)}</dd>
          </>
        )}
        {run.files !== null && (
          <>
            <dt>{t('runs.files')}</dt>
            <dd>{number.format(run.files)}</dd>
          </>
        )}
        {run.bytes !== null && (
          <>
            <dt>{t('runs.size')}</dt>
            <dd>{formatBytes(run.bytes, locale)}</dd>
          </>
        )}
        {run.addedBytes !== null && (
          <>
            <dt>{t('runs.added')}</dt>
            <dd>{formatBytes(run.addedBytes, locale)}</dd>
          </>
        )}
        {seconds !== undefined && (
          <>
            <dt>{t('runs.duration')}</dt>
            <dd>{duration(seconds)}</dd>
          </>
        )}
        {seconds !== undefined && seconds > 0 && run.bytes !== null && (
          <>
            <dt>{t('activity.throughput')}</dt>
            <dd>{t('activity.perSecond', { size: formatBytes(run.bytes / seconds, locale) })}</dd>
          </>
        )}
      </dl>
      {run.warnings > 0 && (
        <>
          <h3 className={styles.detailsTitle}>
            {t('activity.unreadableTitle', { count: run.warnings })}
          </h3>
          <ul className={styles.unreadable}>
            {run.unreadable.map((item) => (
              <li key={item.path}>
                <span className={`${styles.mono} ${styles.breakable}`}>{item.path}</span>
                <span className={styles.muted}>
                  {t(`activity.reasons.${reasonKey(item.reason)}`)}
                </span>
              </li>
            ))}
          </ul>
          {more > 0 && (
            <p className={styles.muted}>{t('activity.unreadableMore', { count: more })}</p>
          )}
          <p className={styles.hint}>{t('activity.unreadableHint')}</p>
        </>
      )}
    </div>
  );
}

function reasonKey(reason: string): 'permission' | 'missing' | 'other' {
  return reason === 'permission' || reason === 'missing' ? reason : 'other';
}

function csvCell(value: string | number | null): string {
  const text = value === null ? '' : String(value);
  return /[",\n\r]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

function saveCsv(text: string, fileName: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noopener';
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 60_000);
}

/**
 * Activity (D416): what runs now with its progress and **Cancel** (restic cannot pause), then every run of every
 * backup with filters, a CSV export and the selected run's details, including the files it could not read.
 */
export function ActivityPage({
  app,
  targets,
}: {
  app: PithyxApp;
  targets: readonly BackupTarget[] | undefined;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const jobs = useBackupJobs(app);
  const board = useActiveRuns();
  const { route } = useSourceNames(app);
  const [jobId, setJobId] = useState<number | undefined>();
  const [state, setState] = useState<BackupRun['state'] | undefined>();
  const [period, setPeriod] = useState<Period>('week');
  const [limit, setLimit] = useState(PAGE);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const days = PERIODS[period];
  // The start of the period is fixed per day, so the query key stays the same while the page is open.
  const query: BackupHistoryQuery = {
    ...(jobId === undefined ? {} : { jobId }),
    ...(state === undefined ? {} : { state }),
    ...(days === undefined ? {} : { since: daysAgo(days - 1).toISOString() }),
    limit,
  };
  const history = useHistory(app, query);
  const list = jobs.data?.jobs ?? [];
  const jobById = new Map(list.map((job) => [job.id, job]));
  const targetOf = (job: BackupJob | undefined) =>
    job === undefined ? undefined : targets?.find((target) => target.id === job.targetId);
  const runs = history.data?.runs ?? [];
  const selected = runs.find((run) => run.id === selectedId);
  const selectedJob = selected === undefined ? undefined : jobById.get(selected.jobId);
  const active = [...board.values()].sort((a, b) =>
    a.state === b.state ? a.id - b.id : a.state === 'running' ? -1 : 1,
  );
  const number = new Intl.NumberFormat(locale);
  const nameOf = (run: BackupRun) =>
    jobById.get(run.jobId)?.settings.name ?? t('activity.deletedJob');

  const columns: DataTableColumn<BackupRun>[] = [
    {
      id: 'started',
      label: t('runs.started'),
      cell: (run) => formatDate(run.startedAt, locale),
      isRowHeader: true,
    },
    {
      id: 'direction',
      label: t('activity.direction'),
      hideWhenNarrow: true,
      cell: (run) => (
        <DirectionTag direction={directionOf(jobById.get(run.jobId)?.kind ?? 'versioned')} />
      ),
    },
    { id: 'backup', label: t('activity.backup'), cell: nameOf },
    {
      id: 'kind',
      label: t('runs.kind'),
      muted: true,
      hideWhenNarrow: true,
      cell: (run) => t(`activity.kinds.${run.kind}`),
    },
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
      id: 'added',
      label: t('runs.added'),
      align: 'end',
      hideWhenNarrow: true,
      cell: (run) => (run.addedBytes === null ? '' : formatBytes(run.addedBytes, locale)),
    },
  ];

  const exportCsv = () => {
    const header = [
      t('runs.started'),
      t('activity.finished'),
      t('activity.backup'),
      t('activity.direction'),
      t('runs.kind'),
      t('runs.state'),
      t('runs.files'),
      t('activity.bytes'),
      t('activity.addedBytes'),
      t('activity.warnings'),
      t('activity.error'),
    ];
    const rows = runs.map((run) => [
      run.startedAt,
      run.finishedAt,
      nameOf(run),
      t(`direction.${directionOf(jobById.get(run.jobId)?.kind ?? 'versioned')}`),
      t(`activity.kinds.${run.kind}`),
      t(`runs.states.${run.state}`),
      run.files,
      run.bytes,
      run.addedBytes,
      run.warnings,
      run.errorCode,
    ]);
    const today = new Date().toISOString().slice(0, 10);
    saveCsv(
      [header, ...rows].map((row) => row.map(csvCell).join(',')).join('\r\n') + '\r\n',
      `backup-activity-${today}.csv`,
    );
  };

  return (
    <div className={styles.page}>
      <p className={styles.intro}>{t('activity.intro')}</p>
      <section className={styles.panel} aria-labelledby="backup-running">
        <div className={styles.panelHead}>
          <IconActivity size={18} stroke={1.75} aria-hidden />
          <h2 id="backup-running" className={styles.panelTitle}>
            {t('activity.runningTitle')}
          </h2>
          <span className={styles.muted}>
            {t('activity.runningCount', {
              running: active.filter((job) => job.state === 'running').length,
              waiting: active.filter((job) => job.state === 'queued').length,
            })}
          </span>
        </div>
        {active.length === 0 ? (
          <p className={`${styles.muted} ${styles.panelBody}`}>{t('activity.nothingRunning')}</p>
        ) : (
          <ul className={styles.runningList}>
            {active.map((entry) => {
              const job = jobOfBoard(entry, list);
              return (
                <RunningRun
                  key={entry.id}
                  app={app}
                  board={entry}
                  job={job}
                  target={targetOf(job)}
                  route={job === undefined ? '' : route(job)}
                />
              );
            })}
          </ul>
        )}
      </section>
      <h2 className={styles.sectionTitle}>{t('activity.historyTitle')}</h2>
      <Toolbar
        aria-label={t('activity.filters')}
        end={
          <Button isDisabled={runs.length === 0} onPress={exportCsv}>
            <IconDownload size={18} stroke={1.75} aria-hidden />
            {t('activity.exportCsv')}
          </Button>
        }
      >
        <Select
          label={t('activity.filterBackup')}
          options={[
            { id: 'all', label: t('activity.allBackups') },
            ...list.map((job) => ({ id: String(job.id), label: job.settings.name })),
          ]}
          value={jobId === undefined ? 'all' : String(jobId)}
          onChange={(value) => {
            setJobId(value === 'all' ? undefined : Number(value));
            setLimit(PAGE);
          }}
        />
        <Select
          label={t('activity.filterState')}
          options={[
            { id: 'all', label: t('activity.allStates') },
            ...STATES.map((entry) => ({ id: entry, label: t(`runs.states.${entry}`) })),
          ]}
          value={state ?? 'all'}
          onChange={(value) => {
            setState(value === 'all' ? undefined : (value as BackupRun['state']));
            setLimit(PAGE);
          }}
        />
        <Select
          label={t('activity.filterPeriod')}
          options={(Object.keys(PERIODS) as Period[]).map((entry) => ({
            id: entry,
            label: t(`activity.periods.${entry}`),
          }))}
          value={period}
          onChange={(value) => {
            setPeriod(value as Period);
            setLimit(PAGE);
          }}
        />
      </Toolbar>
      {history.isError && <ErrorNotice error={describeProblem(history.error)} />}
      <DataTable
        aria-label={t('activity.historyTitle')}
        columns={columns}
        items={runs}
        getKey={(run) => run.id}
        selectionMode="single"
        selectedKeys={selected === undefined ? new Set() : new Set([selected.id])}
        onSelectionChange={(keys) => {
          const [first] = keys === 'all' ? [] : [...keys];
          setSelectedId(first === undefined ? null : Number(first));
        }}
        emptyState={history.isPending ? t('activity.loading') : t('activity.empty')}
        details={
          selected === undefined ? undefined : (
            <RunDetails
              run={selected}
              job={selectedJob}
              target={targetOf(selectedJob)}
              route={selectedJob === undefined ? '' : route(selectedJob)}
            />
          )
        }
        detailsLabel={t('activity.details')}
      />
      {runs.length === limit && limit < 2000 && (
        <div>
          <Button
            onPress={() => {
              setLimit(limit + PAGE);
            }}
          >
            {t('activity.more')}
          </Button>
        </div>
      )}
    </div>
  );
}
