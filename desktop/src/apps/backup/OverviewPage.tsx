import type { PithyxApp } from '@pithyx/sdk';
import {
  AttentionList,
  Button,
  Meter,
  Select,
  StatusBadge,
  SummaryTile,
  SummaryTiles,
  Toolbar,
  cx,
  type AttentionItem,
} from '@pithyx/ui';
import { useMutation } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice, translateError } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import {
  IconAlertTriangle,
  IconArrowLeft,
  IconArrowRight,
  IconCalendar,
  IconCircleCheck,
  IconCircleX,
  IconPlayerPlay,
  IconPlus,
  IconShieldCheck,
  IconUpload,
} from '../../ui/icons';
import { backupsCall, type BackupDay, type BackupJob, type BackupTarget } from '../sdk/backups';
import { describeProblem } from '../storage/shared';
import { RunningPanel } from './ActivityPage';
import { BytesChart } from './BytesChart';
import { addDays, clock, isoDay, useDays, useHistory } from './data';
import { EmptyCard } from './EmptyCard';
import { RUN_TONES, useBackupJobs, useWhen } from './JobsPage';
import styles from './Backup.module.css';

const PERIODS = { week: 7, month: 30, quarter: 90 } as const;
type Period = keyof typeof PERIODS;

/** How a day went: any failed run makes it a failed day, then warnings; `none` when nothing ran. */
type DayState = 'failed' | 'warning' | 'ok' | 'none';

function dayState(day: BackupDay | undefined): DayState {
  if (day === undefined || day.jobs === 0) {
    return 'none';
  }
  return day.failed > 0 ? 'failed' : day.warnings > 0 ? 'warning' : 'ok';
}

const DAY_ICONS = {
  failed: IconCircleX,
  warning: IconAlertTriangle,
  ok: IconCircleCheck,
} as const;

/** The first day of the week in the user's locale, 0 for Sunday; browsers without week info start on Monday. */
function firstWeekday(locale: string): number {
  const info = new Intl.Locale(locale) as Intl.Locale & {
    getWeekInfo?: () => { firstDay: number };
    weekInfo?: { firstDay: number };
  };
  return (info.getWeekInfo?.().firstDay ?? info.weekInfo?.firstDay ?? 1) % 7;
}

function sum(values: readonly (number | null)[]): number {
  return values.reduce<number>((total, value) => total + (value ?? 0), 0);
}

/**
 * The Backup app's overview (#403, D416): four tiles (what is protected, how the runs of the period went, the next
 * run, what the backups take on their targets), a month calendar of the runs, what runs now, what needs attention and
 * the backed-up data per day.
 */
export function OverviewPage({
  app,
  targets,
  onAddJob,
  onShowPage,
  onCheckHostKey,
}: {
  app: PithyxApp;
  targets: readonly BackupTarget[] | undefined;
  onAddJob: () => void;
  onShowPage: (page: 'activity' | 'jobs' | 'targets') => void;
  onCheckHostKey: (target: BackupTarget) => void;
}) {
  const { t } = useTranslation('backup');
  const jobs = useBackupJobs(app);
  const [period, setPeriod] = useState<Period>('month');
  const today = new Date(clock.now());
  const to = isoDay(today);
  const from = isoDay(addDays(today, 1 - PERIODS[period]));
  const days = useDays(app, from, to);
  const start = useMutation({
    mutationFn: async (ids: readonly number[]) => {
      const results = await Promise.allSettled(
        ids.map((id) => backupsCall(app, 'internal.admin.backups.start', { id, kind: 'run' })),
      );
      const failed = results.find((result) => result.status === 'rejected');
      if (failed !== undefined) {
        throw failed.reason;
      }
    },
  });
  const check = useMutation({
    mutationFn: (id: number) =>
      backupsCall(app, 'internal.admin.backups.start', { id, kind: 'check' }),
  });
  const list = jobs.data?.jobs ?? [];
  const idle = list.filter(
    (job) =>
      job.settings.enabled &&
      job.lastRun?.state !== 'running' &&
      job.lastCheck?.state !== 'running',
  );

  if (jobs.data !== undefined && list.length === 0) {
    return (
      <div className={styles.page}>
        <EmptyCard
          icon={<IconShieldCheck size={24} stroke={1.75} />}
          title={t('overview.emptyTitle')}
          text={t('overview.empty')}
          action={t('overview.add')}
          onAction={onAddJob}
        />
      </div>
    );
  }

  return (
    <div className={cx(styles.page, styles.overview)}>
      <Toolbar
        aria-label={t('overview.toolbar')}
        end={
          <Select
            aria-label={t('overview.period')}
            options={(Object.keys(PERIODS) as Period[]).map((entry) => ({
              id: entry,
              label: t(`activity.periods.${entry}`),
            }))}
            value={period}
            onChange={(value) => {
              setPeriod(value as Period);
            }}
          />
        }
      >
        <Button variant="primary" onPress={onAddJob}>
          <IconPlus size={18} stroke={1.75} aria-hidden />
          {t('overview.add')}
        </Button>
        <Button
          isDisabled={idle.length === 0 || start.isPending}
          onPress={() => {
            start.mutate(idle.map((job) => job.id));
          }}
        >
          <IconPlayerPlay size={18} stroke={1.75} aria-hidden />
          {t('overview.runAll')}
        </Button>
      </Toolbar>
      {jobs.isError && <ErrorNotice error={describeProblem(jobs.error)} />}
      {start.isError && <ErrorNotice error={describeProblem(start.error)} />}
      {check.isError && <ErrorNotice error={describeProblem(check.error)} />}
      <Tiles jobs={list} targets={targets ?? []} days={days.data?.days} period={period} />
      <div className={styles.overviewGrid}>
        <Calendar
          app={app}
          jobs={list}
          onShowActivity={() => {
            onShowPage('activity');
          }}
        />
        <div className={styles.overviewSide}>
          <RunningPanel app={app} targets={targets} />
          <Attention
            jobs={list}
            targets={targets ?? []}
            onShowPage={onShowPage}
            onCheckHostKey={onCheckHostKey}
            onRun={(job) => {
              start.mutate([job.id]);
            }}
            onCheck={(job) => {
              check.mutate(job.id);
            }}
          />
        </div>
      </div>
      {days.isError && <ErrorNotice error={describeProblem(days.error)} />}
      {days.data !== undefined && <DataChart days={days.data.days} />}
    </div>
  );
}

function Tiles({
  jobs,
  targets,
  days,
  period,
}: {
  jobs: readonly BackupJob[];
  targets: readonly BackupTarget[];
  days: readonly BackupDay[] | undefined;
  period: Period;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const when = useWhen();
  const number = new Intl.NumberFormat(locale);
  const protectedBytes = sum(jobs.map((job) => job.protectedBytes));
  const repositoryBytes = sum(jobs.map((job) => job.repositoryBytes));
  const usedTargets = new Set(jobs.map((job) => job.targetId));
  const succeeded = sum(days?.map((day) => day.succeeded) ?? []);
  const warnings = sum(days?.map((day) => day.warnings) ?? []);
  const failed = sum(days?.map((day) => day.failed) ?? []);
  const total = succeeded + warnings + failed;
  const next = jobs
    .filter((job) => job.settings.enabled && job.nextRunAt !== null)
    .sort((a, b) => (a.nextRunAt ?? '').localeCompare(b.nextRunAt ?? ''))[0];
  // The target with the least room left, as a share of its size.
  const freeShare = (target: BackupTarget) =>
    target.space === null || target.space.totalBytes === 0
      ? 1
      : target.space.freeBytes / target.space.totalBytes;
  const fullest = targets
    .filter((target) => usedTargets.has(target.id) && target.space !== null)
    .sort((a, b) => freeShare(a) - freeShare(b))[0];
  const space = fullest?.space ?? null;
  const share = (value: number) => (total === 0 ? 0 : (value / total) * 100);

  return (
    <SummaryTiles>
      <SummaryTile
        label={t('overview.protected')}
        value={formatBytes(protectedBytes, locale)}
        detail={
          <span className={styles.tileLines}>
            <span>{t('overview.protectedIn', { count: jobs.length })}</span>
            <span className={styles.tileLine}>
              <IconUpload size={14} stroke={1.75} aria-hidden />
              {t('overview.protectedOut', { size: formatBytes(protectedBytes, locale) })}
            </span>
          </span>
        }
      />
      <SummaryTile
        label={t(`overview.runs.${period}`)}
        value={number.format(succeeded + warnings)}
        detail={
          <span className={styles.tileLines}>
            <span>{t('overview.runsOf', { count: total })}</span>
            {total > 0 && (
              <span
                className={styles.stack}
                role="img"
                aria-label={t('overview.runsLabel', { succeeded, warnings, failed })}
              >
                <span data-state="ok" style={{ width: `${String(share(succeeded))}%` }} />
                <span data-state="warning" style={{ width: `${String(share(warnings))}%` }} />
                <span data-state="failed" style={{ width: `${String(share(failed))}%` }} />
              </span>
            )}
          </span>
        }
      />
      <SummaryTile
        label={t('overview.next')}
        value={next?.nextRunAt == null ? t('overview.nextNone') : when(next.nextRunAt)}
        {...(next === undefined ? {} : { detail: next.settings.name })}
      />
      <SummaryTile
        label={t('overview.used')}
        value={formatBytes(repositoryBytes, locale)}
        detail={
          <span className={styles.tileLines}>
            <span>{t('overview.usedOn', { count: usedTargets.size })}</span>
            {fullest !== undefined && space !== null && (
              <Meter
                aria-label={t('overview.spaceLabel', { name: fullest.name })}
                value={space.totalBytes - space.freeBytes}
                maxValue={space.totalBytes}
                tone={space.freeBytes < space.totalBytes * 0.1 ? 'danger' : 'accent'}
                start={t('overview.space', {
                  name: fullest.name,
                  used: formatBytes(space.totalBytes - space.freeBytes, locale),
                  total: formatBytes(space.totalBytes, locale),
                })}
              />
            )}
          </span>
        }
      />
    </SummaryTiles>
  );
}

/** A month of days, each with how its runs went; a day picked shows what failed or warned on it. */
function Calendar({
  app,
  jobs,
  onShowActivity,
}: {
  app: PithyxApp;
  jobs: readonly BackupJob[];
  onShowActivity: () => void;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const titleId = useId();
  const today = isoDay(new Date(clock.now()));
  const [month, setMonth] = useState(() => {
    const now = new Date(clock.now());
    return new Date(now.getFullYear(), now.getMonth(), 1);
  });
  const [picked, setPicked] = useState<string | null>(null);
  const weekStart = firstWeekday(locale);
  const first = addDays(month, -((month.getDay() - weekStart + 7) % 7));
  const length = new Date(month.getFullYear(), month.getMonth() + 1, 0).getDate();
  const weeks = Math.ceil((((month.getDay() - weekStart + 7) % 7) + length) / 7);
  const cells = Array.from({ length: weeks * 7 }, (_, index) => addDays(first, index));
  const from = isoDay(cells[0] ?? month);
  const to = isoDay(cells[cells.length - 1] ?? month);
  const days = useDays(app, from, to);
  const byDate = new Map(days.data?.days.map((day) => [day.date, day]));
  // Without a pick the calendar shows the latest day that had a problem, else today.
  const latestProblem = [...byDate.values()]
    .filter((day) => day.date <= today && (day.failed > 0 || day.warnings > 0))
    .map((day) => day.date)
    .sort()
    .at(-1);
  const selected = picked ?? latestProblem ?? today;
  const selectedDay = byDate.get(selected);
  const weekday = new Intl.DateTimeFormat(locale, { weekday: 'short' });
  const longDate = new Intl.DateTimeFormat(locale, { dateStyle: 'full' });
  const shortDate = new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short' });
  const monthName = new Intl.DateTimeFormat(locale, { month: 'long', year: 'numeric' });
  const move = (by: number) => {
    setMonth(new Date(month.getFullYear(), month.getMonth() + by, 1));
    setPicked(null);
  };
  const describe = (day: BackupDay | undefined) => {
    if (day === undefined || dayState(day) === 'none') {
      return t('overview.calendar.none');
    }
    // Only the counts that are not zero, so a calm day reads in a few words.
    return [
      t('overview.calendar.ran', { count: day.jobs }),
      ...(day.failed > 0 ? [t('overview.calendar.failedRuns', { count: day.failed })] : []),
      ...(day.warnings > 0 ? [t('overview.calendar.warningRuns', { count: day.warnings })] : []),
      ...(day.failed === 0 && day.warnings === 0 ? [t('overview.calendar.allOk')] : []),
    ].join(', ');
  };

  return (
    <section className={cx(styles.panel, styles.calendarPanel)} aria-labelledby={titleId}>
      <div className={styles.panelHead}>
        <IconCalendar size={18} stroke={1.75} aria-hidden />
        <h2 id={titleId} className={styles.panelTitle}>
          {monthName.format(month)}
        </h2>
        <span className={styles.legend} aria-hidden>
          <span data-state="ok">{t('overview.calendar.ok')}</span>
          <span data-state="warning">{t('overview.calendar.warning')}</span>
          <span data-state="failed">{t('overview.calendar.failed')}</span>
        </span>
        <span className={styles.panelEnd}>
          <Button
            size="sm"
            aria-label={t('overview.calendar.previous')}
            onPress={() => {
              move(-1);
            }}
          >
            <IconArrowLeft size={16} stroke={1.75} aria-hidden />
          </Button>
          <Button
            size="sm"
            aria-label={t('overview.calendar.next')}
            onPress={() => {
              move(1);
            }}
          >
            <IconArrowRight size={16} stroke={1.75} aria-hidden />
          </Button>
        </span>
      </div>
      {days.isError && <ErrorNotice error={describeProblem(days.error)} />}
      <div className={styles.calendar}>
        {cells.slice(0, 7).map((date) => (
          <span key={`head-${isoDay(date)}`} className={styles.weekday} aria-hidden>
            {weekday.format(date)}
          </span>
        ))}
        {cells.map((date) => {
          const iso = isoDay(date);
          const day = byDate.get(iso);
          const state = iso > today ? 'future' : dayState(day);
          const Icon = state === 'future' || state === 'none' ? undefined : DAY_ICONS[state];
          return (
            <button
              key={iso}
              type="button"
              className={styles.calendarDay}
              data-state={state}
              data-outside={date.getMonth() !== month.getMonth() || undefined}
              data-today={iso === today || undefined}
              aria-pressed={iso === selected}
              aria-label={`${longDate.format(date)}: ${iso > today ? t('overview.calendar.future') : describe(day)}`}
              onClick={() => {
                setPicked(iso);
              }}
            >
              <span className={styles.calendarDate}>{date.getDate()}</span>
              {Icon !== undefined && day !== undefined && (
                <span className={styles.calendarCount}>
                  <Icon size={14} stroke={2} aria-hidden />
                  {t('overview.calendar.count', { ok: day.jobsOk, jobs: day.jobs })}
                </span>
              )}
            </button>
          );
        })}
      </div>
      <DayProblems
        app={app}
        jobs={jobs}
        day={selectedDay}
        label={shortDate.format(new Date(`${selected}T12:00:00`))}
        summary={describe(selectedDay)}
        onShowActivity={onShowActivity}
      />
    </section>
  );
}

/** The failed runs and those with warnings of the picked day, each with its error. */
function DayProblems({
  app,
  jobs,
  day,
  label,
  summary,
  onShowActivity,
}: {
  app: PithyxApp;
  jobs: readonly BackupJob[];
  day: BackupDay | undefined;
  label: string;
  summary: string;
  onShowActivity: () => void;
}) {
  const { t } = useTranslation('backup');
  const { t: tAll } = useTranslation();
  const problems = day !== undefined && (day.failed > 0 || day.warnings > 0);
  const history = useHistory(
    app,
    { since: day?.start ?? '', until: day?.end ?? '', limit: 100 },
    problems,
  );
  const runs = problems
    ? (history.data?.runs ?? []).filter(
        (run) => run.state === 'failed' || run.state === 'interrupted' || run.state === 'warning',
      )
    : [];
  const nameOf = (jobId: number) =>
    jobs.find((job) => job.id === jobId)?.settings.name ?? t('activity.deletedJob');
  return (
    <div className={styles.dayProblems}>
      <p className={styles.daySummary}>
        <span className={styles.name}>{label}</span>
        <span className={styles.muted}>{summary}</span>
      </p>
      {runs.length > 0 && (
        <ul className={styles.dayRuns}>
          {runs.map((run) => (
            <li key={run.id}>
              <span className={styles.name}>{nameOf(run.jobId)}</span>
              <StatusBadge tone={RUN_TONES[run.state]}>{t(`runs.states.${run.state}`)}</StatusBadge>
              <span className={styles.muted}>
                {run.errorCode === null
                  ? t('overview.unreadable', { count: run.warnings })
                  : translateError(tAll, {
                      code: run.errorCode as never,
                      params: (run.errorParams ?? {}) as Record<string, unknown>,
                      requestId: '',
                    })}
              </span>
            </li>
          ))}
        </ul>
      )}
      {problems && (
        <div>
          <Button size="sm" onPress={onShowActivity}>
            {t('overview.showActivity')}
          </Button>
        </div>
      )}
    </div>
  );
}

/** One row per problem with the action that leads to its fix (D394); a calm panel when there is none. */
function Attention({
  jobs,
  targets,
  onShowPage,
  onCheckHostKey,
  onRun,
  onCheck,
}: {
  jobs: readonly BackupJob[];
  targets: readonly BackupTarget[];
  onShowPage: (page: 'activity' | 'jobs' | 'targets') => void;
  onCheckHostKey: (target: BackupTarget) => void;
  onRun: (job: BackupJob) => void;
  onCheck: (job: BackupJob) => void;
}) {
  const { t } = useTranslation('backup');
  const { t: tAll } = useTranslation();
  const when = useWhen();
  const message = (code: string, params: unknown) =>
    translateError(tAll, {
      code: code as never,
      params: (params ?? {}) as Record<string, unknown>,
      requestId: '',
    });
  const items: AttentionItem[] = [];
  for (const target of targets) {
    const test = target.lastTest;
    if (test === null || test.succeeded || test.errorCode === null) {
      continue;
    }
    items.push({
      id: `target-${String(target.id)}`,
      tone: 'danger',
      title: target.name,
      description: message(test.errorCode, test.errorParams),
      action:
        test.errorCode === 'backup.target.host_key_changed'
          ? {
              label: t('targets.checkHostKey'),
              onPress: () => {
                onCheckHostKey(target);
              },
            }
          : {
              label: t('overview.showTargets'),
              onPress: () => {
                onShowPage('targets');
              },
            },
    });
  }
  for (const job of jobs) {
    const run = job.lastRun;
    const running = run?.state === 'running' || job.lastCheck?.state === 'running';
    if (running) {
      continue;
    }
    const runNow = {
      label: t('jobs.runNow'),
      onPress: () => {
        onRun(job);
      },
    };
    if (run !== null && (run.state === 'failed' || run.state === 'interrupted')) {
      items.push({
        id: `run-${String(job.id)}`,
        tone: 'danger',
        title: job.settings.name,
        description:
          run.errorCode === null
            ? t('overview.failed', { time: when(run.startedAt) })
            : t('overview.failedWith', {
                time: when(run.startedAt),
                error: message(run.errorCode, run.errorParams),
              }),
        action: runNow,
      });
    } else if (job.overdue) {
      items.push({
        id: `overdue-${String(job.id)}`,
        tone: 'warning',
        title: job.settings.name,
        description:
          job.lastSuccessAt === null
            ? t('overview.overdueNever')
            : t('overview.overdue', { time: when(job.lastSuccessAt) }),
        action: runNow,
      });
    } else if (run?.state === 'warning') {
      items.push({
        id: `warning-${String(job.id)}`,
        tone: 'warning',
        title: job.settings.name,
        description: t('overview.unreadable', { count: run.warnings }),
        action: {
          label: t('overview.show'),
          onPress: () => {
            onShowPage('activity');
          },
        },
      });
    }
    if (job.lastCheck?.state === 'failed' || job.lastCheck?.state === 'interrupted') {
      items.push({
        id: `check-${String(job.id)}`,
        tone: 'danger',
        title: job.settings.name,
        description: t('overview.checkFailed', { time: when(job.lastCheck.startedAt) }),
        action: {
          label: t('jobs.checkNow'),
          onPress: () => {
            onCheck(job);
          },
        },
      });
    }
  }
  if (items.length === 0) {
    return (
      <section className={styles.panel} aria-label={t('overview.attention')}>
        <p className={cx(styles.panelBody, styles.allFine)}>
          <IconCircleCheck size={18} stroke={1.75} aria-hidden />
          {t('overview.allFine')}
        </p>
      </section>
    );
  }
  return (
    <AttentionList className={styles.attention} title={t('overview.attention')} items={items} />
  );
}

/** The bytes the backups added per day as bars and what the repositories take as a line. */
function DataChart({ days }: { days: readonly BackupDay[] }) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const latest =
    days
      .map((day) => day.repositoryBytes)
      .filter((size) => size !== null)
      .at(-1) ?? null;
  return (
    <BytesChart
      title={t('overview.chart.title')}
      barLegend={t('overview.chart.added')}
      lineLegend={t('overview.chart.size')}
      label={t('overview.chart.label', {
        days: days.length,
        added: formatBytes(sum(days.map((day) => day.addedBytes)), locale),
        size: latest === null ? t('overview.chart.unknown') : formatBytes(latest, locale),
      })}
      points={days.map((day) => ({
        key: day.date,
        at: new Date(`${day.date}T12:00:00`),
        bar: day.addedBytes,
        line: day.repositoryBytes,
      }))}
    />
  );
}
