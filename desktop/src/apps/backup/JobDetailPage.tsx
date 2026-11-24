import type { PithyxApp } from '@pithyx/sdk';
import {
  Button,
  DataTable,
  Tab,
  TabList,
  TabPanel,
  Tabs,
  Toolbar,
  ToolbarSeparator,
  type DataTableColumn,
} from '@pithyx/ui';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import {
  IconChevronRight,
  IconKey,
  IconLock,
  IconPencil,
  IconRestore,
  IconTrash,
} from '../../ui/icons';
import {
  backupsCall,
  versionsKey,
  type BackupJob,
  type BackupTarget,
  type BackupVersion,
} from '../sdk/backups';
import { describeProblem, formatDate } from '../storage/shared';
import { BytesChart } from './BytesChart';
import { useActiveRuns } from './data';
import { Route } from './Direction';
import { RETENTION_KEYS } from './JobDialogs';
import {
  JobDetails,
  PullFacts,
  RunButtons,
  RunCommandErrors,
  RunsTable,
  runningOf,
  useRunCommands,
  useCronText,
  useScheduleText,
  useSourceNames,
  useWhen,
} from './JobsPage';
import styles from './Backup.module.css';

/**
 * A backup's own page (#404, D416): the path back to the list, its route, and tabs for its state, its versions, its
 * schedule and retention, its folders, its settings and every run. The toolbar runs, checks, edits and deletes it and
 * shows its key again. A server backup (#396) has no key and no check, and its versions are snapshots on this box.
 */
export function JobDetailPage({
  app,
  job,
  target,
  onBack,
  onBrowse,
  onOpen,
}: {
  app: PithyxApp;
  job: BackupJob;
  target: BackupTarget | undefined;
  onBack: () => void;
  /** Opens the restore page on a version. */
  onBrowse: (version: BackupVersion) => void;
  onOpen: (kind: 'edit' | 'delete' | 'revealKey') => void;
}) {
  const { t } = useTranslation('backup');
  const board = useActiveRuns();
  const { route, sourcePath } = useSourceNames(app);
  const commands = useRunCommands(app);
  const running = runningOf(job);
  const pull = job.pull;
  const edit = (
    <Button
      size="sm"
      onPress={() => {
        onOpen('edit');
      }}
    >
      <IconPencil size={16} stroke={1.75} aria-hidden />
      {t('jobs.edit')}
    </Button>
  );

  return (
    <div className={styles.page}>
      <nav className={styles.breadcrumb} aria-label={t('detail.breadcrumb')}>
        <button type="button" className={styles.breadcrumbLink} onClick={onBack}>
          {t(pull === null ? 'jobs.title' : 'pulls.title')}
        </button>
        <IconChevronRight size={14} stroke={1.75} aria-hidden />
        <span aria-current="page">{job.settings.name}</span>
      </nav>
      <Route {...route(job, target)} />
      <Toolbar aria-label={t('detail.toolbar')}>
        <RunButtons job={job} commands={commands} />
        <ToolbarSeparator />
        <Button
          onPress={() => {
            onOpen('edit');
          }}
        >
          <IconPencil size={18} stroke={1.75} aria-hidden />
          {t('jobs.edit')}
        </Button>
        <Button
          isDisabled={running !== undefined}
          onPress={() => {
            onOpen('delete');
          }}
        >
          <IconTrash size={18} stroke={1.75} aria-hidden />
          {t('jobs.delete')}
        </Button>
        {pull === null && (
          <>
            <ToolbarSeparator />
            <Button
              onPress={() => {
                onOpen('revealKey');
              }}
            >
              <IconKey size={18} stroke={1.75} aria-hidden />
              {t('detail.key')}
            </Button>
          </>
        )}
      </Toolbar>
      <RunCommandErrors commands={commands} />
      <Tabs defaultSelectedKey="overview">
        <TabList aria-label={t('detail.tabs')}>
          <Tab id="overview">{t('detail.tab.overview')}</Tab>
          <Tab id="versions">{t('detail.tab.versions')}</Tab>
          <Tab id="schedule">{t('detail.tab.schedule')}</Tab>
          <Tab id="sources">{t('detail.tab.sources')}</Tab>
          <Tab id="settings">{t('detail.tab.settings')}</Tab>
          <Tab id="log">{t('detail.tab.log')}</Tab>
        </TabList>
        <TabPanel id="overview">
          <JobDetails
            app={app}
            job={job}
            target={target}
            board={board}
            route={route(job, target)}
            sourcePath={sourcePath}
            withHead={false}
          />
        </TabPanel>
        <TabPanel id="versions">
          <VersionsPanel app={app} job={job} {...(pull === null ? { onBrowse } : {})} />
        </TabPanel>
        <TabPanel id="schedule">
          <ScheduleFacts job={job} />
          {edit}
        </TabPanel>
        <TabPanel id="sources">
          <dl className={styles.facts}>
            {pull === null ? (
              <>
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
              <PullFacts job={job} pull={pull} target={target} sourcePath={sourcePath} />
            )}
            <dt>{t('jobForm.excludes')}</dt>
            <dd>
              {(job.settings.excludes ?? []).length === 0 ? (
                t('detail.noExcludes')
              ) : (
                <ul className={styles.plainList}>
                  {(job.settings.excludes ?? []).map((pattern) => (
                    <li key={pattern} className={styles.mono}>
                      {pattern}
                    </li>
                  ))}
                </ul>
              )}
            </dd>
          </dl>
          {edit}
        </TabPanel>
        <TabPanel id="settings">
          <SettingsFacts job={job} target={target} />
          {edit}
        </TabPanel>
        <TabPanel id="log">
          <RunsTable app={app} job={job} />
        </TabPanel>
      </Tabs>
    </div>
  );
}

/** When the backup runs, what it keeps, when it is checked and when it counts as overdue. */
function ScheduleFacts({ job }: { job: BackupJob }) {
  const { t } = useTranslation('backup');
  const when = useWhen();
  const scheduleText = useScheduleText();
  const cronText = useCronText();
  const { retention, checkSchedule, checkReadPercent, overdueHours } = job.settings;
  const kept = RETENTION_KEYS.filter((key) => retention[key] > 0).map((key) =>
    t('detail.keepCount', { count: retention[key], kind: t(`jobForm.keep.${key}`) }),
  );
  return (
    <dl className={styles.facts}>
      <dt>{t('jobs.schedule')}</dt>
      <dd>{scheduleText(job)}</dd>
      <dt>{t('jobs.nextRun')}</dt>
      <dd>{job.nextRunAt === null ? t('jobs.byHand') : when(job.nextRunAt)}</dd>
      <dt>{t('jobForm.retention')}</dt>
      <dd>{kept.length === 0 ? t('detail.keepAll') : kept.join(', ')}</dd>
      {job.kind !== 'pull' && (
        <>
          <dt>{t('jobs.check')}</dt>
          <dd>
            {checkSchedule == null
              ? t('jobs.noCheck')
              : t('detail.checkValue', {
                  schedule: cronText(checkSchedule),
                  percent: checkReadPercent,
                })}
          </dd>
        </>
      )}
      {job.nextCheckAt !== null && (
        <>
          <dt>{t('detail.nextCheck')}</dt>
          <dd>{when(job.nextCheckAt)}</dd>
        </>
      )}
      <dt>{t('detail.overdue')}</dt>
      <dd>
        {overdueHours == null
          ? t('detail.overdueDefault')
          : t('detail.overdueHours', { count: overdueHours })}
      </dd>
    </dl>
  );
}

/** Where the backup goes (a server backup: where it comes from) and how fast it may use the network. */
function SettingsFacts({ job, target }: { job: BackupJob; target: BackupTarget | undefined }) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const limit = (kib: number) =>
    kib === 0 ? t('detail.noLimit') : t('detail.limit', { rate: formatBytes(kib * 1024, locale) });
  return (
    <dl className={styles.facts}>
      <dt>{t(job.kind === 'pull' ? 'pulls.server' : 'jobForm.target')}</dt>
      <dd>
        {target === undefined
          ? ''
          : t('detail.targetValue', {
              name: target.name,
              address: t('targets.address', {
                user: target.user,
                host: target.host,
                port: target.port,
              }),
            })}
      </dd>
      {job.kind === 'pull' ? (
        <>
          <dt>{t('pulls.dataset')}</dt>
          <dd className={styles.mono}>{job.dataset ?? ''}</dd>
        </>
      ) : (
        <>
          <dt>{t('jobs.repository')}</dt>
          <dd className={styles.mono}>{job.repository}</dd>
        </>
      )}
      <dt>{t('jobForm.enabled')}</dt>
      <dd>{t(job.settings.enabled ? 'detail.yes' : 'detail.no')}</dd>
      {job.kind !== 'pull' && (
        <>
          <dt>{t('detail.upload')}</dt>
          <dd>{limit(job.settings.uploadLimitKib)}</dd>
        </>
      )}
      <dt>{t('detail.download')}</dt>
      <dd>{limit(job.settings.downloadLimitKib)}</dd>
    </dl>
  );
}

/** Why the retention kept a version, as tags; a protected version says so first. */
function KeepTags({ version }: { version: BackupVersion }) {
  const { t } = useTranslation('backup');
  const reasons = version.keep.filter((reason) => reason !== 'protected');
  return (
    <span className={styles.tags}>
      {version.protected && (
        <span className={styles.tag} data-tone="accent">
          <IconLock size={12} stroke={2} aria-hidden />
          {t('versions.protected')}
        </span>
      )}
      {reasons.map((reason) => (
        <span key={reason} className={styles.tag}>
          {t(`jobForm.keep.${reason}`)}
        </span>
      ))}
    </span>
  );
}

/**
 * The versions in the backup's repository as the box stored them after its last run: a chart of what each added and
 * its size, the versions with the reasons the retention kept them, and the selected one's changes and protection.
 */
function VersionsPanel({
  app,
  job,
  onBrowse,
}: {
  app: PithyxApp;
  job: BackupJob;
  /** Opens the restore page on a version; left out where the restore page cannot browse the backup. */
  onBrowse?: (version: BackupVersion) => void;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const versions = useQuery({
    // A finished run stores new versions and may drop old ones.
    queryKey: [...versionsKey(job.id), job.lastRun?.id, job.lastRun?.state],
    queryFn: () => backupsCall(app, 'internal.admin.backups.versions', { id: job.id }),
    placeholderData: keepPreviousData,
  });
  const list = versions.data?.versions ?? [];
  const selected = list.find((version) => version.id === selectedId) ?? list[0];
  const bytes = (value: number | null) => (value === null ? '' : formatBytes(value, locale));
  const count = (value: number | null) =>
    value === null ? '' : new Intl.NumberFormat(locale).format(value);
  const columns: DataTableColumn<BackupVersion>[] = [
    {
      id: 'time',
      label: t('versions.time'),
      cell: (version) => formatDate(version.time, locale),
      sortValue: (version) => version.time,
      isRowHeader: true,
    },
    {
      id: 'files',
      label: t('versions.files'),
      align: 'end',
      hideWhenNarrow: true,
      cell: (version) => count(version.files),
    },
    {
      id: 'size',
      label: t('jobs.size'),
      align: 'end',
      cell: (version) => bytes(version.bytes),
    },
    {
      id: 'added',
      label: t('runs.added'),
      align: 'end',
      muted: true,
      hideWhenNarrow: true,
      cell: (version) => bytes(version.addedBytes),
    },
    {
      id: 'keep',
      label: t('versions.keep'),
      cell: (version) => <KeepTags version={version} />,
    },
  ];
  const oldestFirst = [...list].reverse();
  const latest = list[0];

  return (
    <div className={styles.stackPage}>
      {versions.isError && <ErrorNotice error={describeProblem(versions.error)} />}
      {list.length > 0 && (
        <BytesChart
          title={t('versions.chart.title')}
          barLegend={t('versions.chart.added')}
          lineLegend={t('versions.chart.size')}
          label={t('versions.chart.label', {
            count: list.length,
            size: bytes(latest?.bytes ?? null),
          })}
          points={oldestFirst.map((version) => ({
            key: String(version.id),
            at: new Date(version.time),
            bar: version.addedBytes ?? 0,
            line: version.bytes,
          }))}
        />
      )}
      <DataTable
        aria-label={t('versions.list')}
        columns={columns}
        items={list}
        getKey={(version) => version.id}
        density="comfortable"
        selectionMode="single"
        selectedKeys={selected === undefined ? new Set() : new Set([selected.id])}
        onSelectionChange={(keys) => {
          const [first] = keys === 'all' ? [] : [...keys];
          setSelectedId(first === undefined ? null : Number(first));
        }}
        onRowAction={(key) => {
          const version = list.find((entry) => entry.id === Number(key));
          if (version !== undefined) {
            onBrowse?.(version);
          }
        }}
        defaultSort={{ column: 'time', direction: 'descending' }}
        emptyState={versions.isPending ? t('versions.loading') : t('versions.empty')}
        details={
          selected === undefined ? undefined : (
            <VersionDetails
              app={app}
              job={job}
              version={selected}
              {...(onBrowse === undefined ? {} : { onBrowse })}
            />
          )
        }
        detailsLabel={t('versions.details')}
      />
    </div>
  );
}

const CHANGE_TONES = { added: 'success', modified: 'accent', removed: 'danger' } as const;

/** One version: what it holds, what changed against the one before, and protecting it from the retention. */
function VersionDetails({
  app,
  job,
  version,
  onBrowse,
}: {
  app: PithyxApp;
  job: BackupJob;
  version: BackupVersion;
  onBrowse?: (version: BackupVersion) => void;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const count = (value: number | null) =>
    value === null ? t('versions.unknown') : new Intl.NumberFormat(locale).format(value);
  const protect = useMutation({
    mutationFn: (keep: boolean) =>
      backupsCall(app, 'internal.admin.backups.protect', {
        id: job.id,
        versionId: version.id,
        protected: keep,
      }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: versionsKey(job.id) }),
  });
  const changes = version.changes ?? [];
  return (
    <div className={styles.details}>
      <div className={styles.detailsHead}>
        <span className={styles.detailsName}>
          {t('versions.title', { time: formatDate(version.time, locale) })}
        </span>
        <span className={styles.muted}>{t('versions.id', { id: version.snapshot })}</span>
        <KeepTags version={version} />
      </div>
      <dl className={styles.facts}>
        <dt>{t('versions.files')}</dt>
        <dd>{count(version.files)}</dd>
        <dt>{t('jobs.size')}</dt>
        <dd>{version.bytes === null ? '' : formatBytes(version.bytes, locale)}</dd>
        <dt>{t('runs.added')}</dt>
        <dd>{version.addedBytes === null ? '' : formatBytes(version.addedBytes, locale)}</dd>
        <dt>{t('versions.new')}</dt>
        <dd>{count(version.filesAdded)}</dd>
        <dt>{t('versions.changed')}</dt>
        <dd>{count(version.filesChanged)}</dd>
        <dt>{t('versions.removed')}</dt>
        <dd>{count(version.filesRemoved)}</dd>
      </dl>
      {version.changes !== null && (
        <div className={styles.field}>
          <h3 className={styles.detailsTitle}>{t('versions.changes')}</h3>
          {changes.length === 0 ? (
            <p className={styles.muted}>{t('versions.noChanges')}</p>
          ) : (
            <ul className={styles.changes}>
              {changes.map((change) => (
                <li key={`${change.change}:${change.path}`}>
                  <span className={styles.tag} data-tone={CHANGE_TONES[change.change]}>
                    {t(`versions.change.${change.change}`)}
                  </span>
                  <span className={`${styles.mono} ${styles.breakable}`}>{change.path}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      {protect.isError && <ErrorNotice error={describeProblem(protect.error)} />}
      <div className={styles.row}>
        {onBrowse !== undefined && (
          <Button
            size="sm"
            onPress={() => {
              onBrowse(version);
            }}
          >
            <IconRestore size={16} stroke={1.75} aria-hidden />
            {t('versions.browse')}
          </Button>
        )}
        <Button
          size="sm"
          isDisabled={protect.isPending}
          onPress={() => {
            protect.mutate(!version.protected);
          }}
        >
          <IconLock size={16} stroke={1.75} aria-hidden />
          {t(version.protected ? 'versions.release' : 'versions.protect')}
        </Button>
      </div>
    </div>
  );
}
